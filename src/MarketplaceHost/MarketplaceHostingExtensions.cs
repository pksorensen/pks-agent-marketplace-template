// MarketplaceHostingExtensions.cs — the reusable wiring behind AppHost.cs.
//
// AddMarketplace() adds the published marketplace container and forwards every
// supported environment variable from the "Marketplace:*" configuration section
// (appsettings.json, environment variables, or `--Marketplace:Key=value`).
//
// The Add*Auth extension methods layer a single authentication provider on top.
// All of them are additive and read their secrets from Aspire parameters, so
// nothing sensitive is ever committed to appsettings.json.

using System.Diagnostics;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHost;

public static class MarketplaceHostingExtensions
{
    /// <summary>The published marketplace image (override with Marketplace:Image).</summary>
    public const string DefaultImage = "registry.agentics.dk/agentics/pks-agent-marketplace";

    /// <summary>The registry we `docker login` to when pull credentials are supplied.</summary>
    public const string RegistryHost = "registry.agentics.dk";

    /// <summary>The container's internal HTTP port.</summary>
    public const int ContainerPort = 3000;

    /// <summary>
    /// Add the Agentics Skills Marketplace container to the app model, wiring every
    /// supported configuration variable from the "Marketplace:*" section. Registry
    /// pull credentials are read from the <c>registry-username</c> / <c>registry-password</c>
    /// parameters; when both are set the AppHost runs a `docker login` before starting
    /// (leave them blank to pull a public image).
    /// </summary>
    public static IResourceBuilder<ContainerResource> AddMarketplace(
        this IDistributedApplicationBuilder builder, string name = "marketplace")
    {
        var cfg = builder.Configuration;

        // ── Private registry login (optional) ──────────────────────────────
        // Secret + non-secret parameters both default from config/env so `aspire run`
        // never blocks prompting. Set them via appsettings, env, or user-secrets.
        var registryUser = builder.AddParameter("registry-username",
            () => cfg["Marketplace:RegistryUsername"] ?? Env("REGISTRY_USERNAME") ?? "");
        var registryPass = builder.AddParameter("registry-password",
            () => cfg["Marketplace:RegistryPassword"] ?? Env("REGISTRY_PASSWORD") ?? "", secret: true);

        builder.Eventing.Subscribe<BeforeStartEvent>(async (_, ct) =>
        {
            var user = (await registryUser.Resource.GetValueAsync(ct))?.Trim();
            var pass = await registryPass.Resource.GetValueAsync(ct);
            if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pass))
                await DockerLoginAsync(RegistryHost, user!, pass!, ct);
        });

        // ── The container ───────────────────────────────────────────────────
        var image = cfg["Marketplace:Image"] ?? DefaultImage;
        var tag = cfg["Marketplace:Tag"] ?? "latest";

        var mkt = builder.AddContainer(name, image, tag)
            // :latest never runs stale — always re-pull the manifest on start.
            .WithImagePullPolicy(ImagePullPolicy.Always)
            .WithHttpEndpoint(port: ContainerPort, targetPort: ContainerPort, name: "http")
            .WithVolume("marketplace-data", "/app/user-data")
            .WithEnvironment("PORT", ContainerPort.ToString())
            .WithEnvironment("AUTH_TRUST_HOST", cfg["Marketplace:AuthTrustHost"] ?? "true");

        // Runtime + branding — forwarded only when set (empty values are skipped so
        // the container's own defaults stand).
        Forward(mkt, cfg, "LOG_LEVEL", "Marketplace:LogLevel");
        Forward(mkt, cfg, "MASK_PATTERNS", "Marketplace:MaskPatterns");
        Forward(mkt, cfg, "NEXTAUTH_URL", "Marketplace:PublicUrl");
        Forward(mkt, cfg, "EMAIL_BRAND_NAME", "Marketplace:BrandName");
        Forward(mkt, cfg, "EMAIL_BRAND_COLOR", "Marketplace:BrandColor");
        Forward(mkt, cfg, "EMAIL_FROM", "Marketplace:EmailFrom");

        // Marketplace content + navigation feature flags.
        Forward(mkt, cfg, "ENABLE_PKS_AGENT_MARKETPLACE_PLUGINS", "Marketplace:EnableBundledPlugins");
        Forward(mkt, cfg, "ENABLE_ORGANIZATION_MANAGEMENT", "Marketplace:EnableOrganizationManagement");
        Forward(mkt, cfg, "ENABLE_CUSTOM_ROLES", "Marketplace:EnableCustomRoles");
        Forward(mkt, cfg, "ENABLE_LIBRARIES_CONNECTIONS", "Marketplace:EnableLibrariesConnectors");
        Forward(mkt, cfg, "ENABLE_PERSONAL_SETTINGS", "Marketplace:EnablePersonalSettings");

        // Authorization gates (all inert when unset — the "only our company" locks).
        Forward(mkt, cfg, "AUTH_ALLOWED_TENANTS", "Marketplace:Auth:AllowedTenants");
        Forward(mkt, cfg, "AUTH_ALLOWED_EMAIL_DOMAINS", "Marketplace:Auth:AllowedEmailDomains");
        Forward(mkt, cfg, "AUTH_ACCESS_MODE", "Marketplace:Auth:AccessMode");
        Forward(mkt, cfg, "AUTH_ALLOWED_EMAILS", "Marketplace:Auth:AllowedEmails");
        Forward(mkt, cfg, "AUTH_ADMIN_GROUPS", "Marketplace:Auth:AdminGroups");
        Forward(mkt, cfg, "AUTH_ADMIN_ROLES", "Marketplace:Auth:AdminRoles");
        Forward(mkt, cfg, "AUTH_ADMIN_EMAILS", "Marketplace:Auth:AdminEmails");

        // NextAuth signing secret — required for every non-unsecured deployment.
        // Injected only when set: an empty value must NOT be forwarded, or it would
        // shadow the container's unsecured-mode fallback (JS `??` treats "" as present).
        var nextAuthSecret = builder.AddParameter("nextauth-secret",
            () => cfg["Marketplace:NextAuthSecret"] ?? Env("NEXTAUTH_SECRET") ?? "", secret: true);
        mkt.WithEnvironment(async ctx =>
        {
            var s = await nextAuthSecret.Resource.GetValueAsync(ctx.CancellationToken);
            if (!string.IsNullOrEmpty(s))
                ctx.EnvironmentVariables["NEXTAUTH_SECRET"] = s;
        });

        return mkt;
    }

    // ── Authentication providers ────────────────────────────────────────────

    /// <summary>
    /// Zero-friction local mode: the container auto-signs-in a single admin dev user,
    /// no email round-trip. Dev only — pair with a public tunnel at your own risk.
    /// </summary>
    public static IResourceBuilder<ContainerResource> AddUnsecured(
        this IResourceBuilder<ContainerResource> mkt)
    {
        var cfg = mkt.ApplicationBuilder.Configuration;
        mkt.WithEnvironment("AUTH_PROVIDER", "unsecured")
           .WithEnvironment("ALLOW_UNSECURED_AUTH", "true");
        Forward(mkt, cfg, "UNSECURED_AUTH_EMAIL", "Marketplace:Unsecured:Email");
        Forward(mkt, cfg, "UNSECURED_AUTH_NAME", "Marketplace:Unsecured:Name");
        return mkt;
    }

    /// <summary>
    /// Microsoft Entra ID (Azure AD). Reads Marketplace:Entra:TenantId/ClientId from
    /// config and the client secret from the <c>entra-client-secret</c> parameter.
    /// Combine with Marketplace:Auth:AllowedTenants / AdminGroups / AdminRoles to lock
    /// the deployment to your directory.
    /// </summary>
    public static IResourceBuilder<ContainerResource> AddAzureEntraID(
        this IResourceBuilder<ContainerResource> mkt)
    {
        var b = mkt.ApplicationBuilder;
        var cfg = b.Configuration;
        var clientId = b.AddParameter("entra-client-id",
            () => cfg["Marketplace:Entra:ClientId"] ?? Env("AZURE_AD_CLIENT_ID") ?? "");
        var clientSecret = b.AddParameter("entra-client-secret",
            () => cfg["Marketplace:Entra:ClientSecret"] ?? Env("AZURE_AD_CLIENT_SECRET") ?? "", secret: true);

        mkt.WithEnvironment("AUTH_PROVIDER", "microsoft-entra")
           .WithEnvironment("AZURE_AD_CLIENT_ID", clientId)
           .WithEnvironment("AZURE_AD_CLIENT_SECRET", clientSecret);
        Forward(mkt, cfg, "AZURE_AD_TENANT_ID", "Marketplace:Entra:TenantId");
        Forward(mkt, cfg, "AZURE_AD_ISSUER", "Marketplace:Entra:Issuer");
        return mkt;
    }

    /// <summary>
    /// A generic OIDC provider (Keycloak, Auth0, Okta, …). Reads Marketplace:Oidc:Issuer/
    /// ClientId from config and the client secret from the <c>oidc-client-secret</c> parameter.
    /// </summary>
    public static IResourceBuilder<ContainerResource> AddOIDC(
        this IResourceBuilder<ContainerResource> mkt)
    {
        var b = mkt.ApplicationBuilder;
        var cfg = b.Configuration;
        var clientId = b.AddParameter("oidc-client-id",
            () => cfg["Marketplace:Oidc:ClientId"] ?? Env("OIDC_CLIENT_ID") ?? "");
        var clientSecret = b.AddParameter("oidc-client-secret",
            () => cfg["Marketplace:Oidc:ClientSecret"] ?? Env("OIDC_CLIENT_SECRET") ?? "", secret: true);

        mkt.WithEnvironment("AUTH_PROVIDER", "oidc")
           .WithEnvironment("OIDC_CLIENT_ID", clientId)
           .WithEnvironment("OIDC_CLIENT_SECRET", clientSecret);
        Forward(mkt, cfg, "OIDC_ISSUER", "Marketplace:Oidc:Issuer");
        return mkt;
    }

    /// <summary>
    /// Passwordless email magic links via your own SMTP server. Reads Marketplace:Smtp:*
    /// from config and the SMTP password from the <c>smtp-pass</c> parameter.
    /// </summary>
    public static IResourceBuilder<ContainerResource> AddMagicLink(
        this IResourceBuilder<ContainerResource> mkt)
    {
        var b = mkt.ApplicationBuilder;
        var cfg = b.Configuration;
        var smtpPass = b.AddParameter("smtp-pass",
            () => cfg["Marketplace:Smtp:Pass"] ?? Env("SMTP_PASS") ?? "", secret: true);

        mkt.WithEnvironment("AUTH_PROVIDER", "magic-link")
           .WithEnvironment("EMAIL_PROVIDER", "smtp")
           .WithEnvironment("SMTP_PASS", smtpPass);
        Forward(mkt, cfg, "SMTP_HOST", "Marketplace:Smtp:Host");
        Forward(mkt, cfg, "SMTP_PORT", "Marketplace:Smtp:Port");
        Forward(mkt, cfg, "SMTP_USER", "Marketplace:Smtp:User");
        Forward(mkt, cfg, "SMTP_FROM", "Marketplace:Smtp:From");
        return mkt;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string? Env(string key) => Environment.GetEnvironmentVariable(key);

    /// <summary>Forward a config value as a container env var, skipping empty values.</summary>
    private static void Forward(
        IResourceBuilder<ContainerResource> r, IConfiguration cfg, string envKey, string cfgKey)
    {
        var value = cfg[cfgKey];
        if (!string.IsNullOrWhiteSpace(value))
            r.WithEnvironment(envKey, value);
    }

    private static async Task DockerLoginAsync(string host, string user, string password, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("login");
        psi.ArgumentList.Add(host);
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(user);
        psi.ArgumentList.Add("--password-stdin");

        try
        {
            using var proc = Process.Start(psi)!;
            await proc.StandardInput.WriteAsync(password);
            proc.StandardInput.Close();
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode == 0)
                Console.WriteLine($"[marketplace] docker login to {host} succeeded.");
            else
                Console.Error.WriteLine($"[marketplace] docker login to {host} failed: {await proc.StandardError.ReadToEndAsync(ct)}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[marketplace] docker login to {host} threw: {ex.Message}");
        }
    }
}
