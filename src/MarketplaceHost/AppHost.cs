// AppHost.cs — the whole deployment, one file.
//
// This Aspire AppHost pulls the published Agentics Skills Marketplace image from
// registry.agentics.dk and runs it locally (or publishes it) with all of its
// configuration wired up for you. To deploy:
//
//   1. Set your registry pull credentials (minted at agentics.dk):
//        dotnet user-secrets set Parameters:registry-username <owner>
//        dotnet user-secrets set Parameters:registry-password <password>
//      (or leave blank to pull a public image.)
//
//   2. Pick an auth mode below (see the extension methods in
//      MarketplaceHostingExtensions.cs) and set its parameters.
//
//   3. aspire run
//
// Every configuration variable lives in appsettings.json under "Marketplace:*"
// so it's easy to find and set. See README.md for the full reference.

using MarketplaceHost;

var builder = DistributedApplication.CreateBuilder(args);

var marketplace = builder
    .AddMarketplace("marketplace")

    // ── Choose ONE authentication mode ─────────────────────────────────────
    // Zero-friction local test — auto-signs you in as an admin. Never expose
    // this publicly (see ALLOW_PUBLIC_UNSECURED in the README).
    .AddUnsecured();

    // Microsoft Entra ID (Azure AD). Set Marketplace:Entra:* in appsettings.json
    // or the client secret via `dotnet user-secrets set Parameters:entra-client-secret <secret>`.
    // .AddAzureEntraID();

    // Any OIDC provider (Keycloak, Auth0, Okta, …). Set Marketplace:Oidc:*.
    // .AddOIDC();

    // Passwordless email magic links via your SMTP server. Set Marketplace:Smtp:*.
    // .AddMagicLink();

builder.Build().Run();
