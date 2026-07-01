# pks-agent-marketplace-template

Deploy the **Agentics Skills Marketplace** with [.NET Aspire](https://aka.ms/aspire)
in one command. This template pulls the published container image from
`registry.agentics.dk`, wires up every supported configuration variable, and
gives you clean extension methods to pick an authentication provider.

> The marketplace itself is a container — you don't build any of it here. This
> repo is just the Aspire AppHost that runs it with the right configuration.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (`dotnet --version` ≥ 10)
- [Aspire CLI](https://learn.microsoft.com/dotnet/aspire/cli/install) (`aspire --version`)
- Docker (Desktop or Engine) running locally
- *(optional)* Registry pull credentials — mint a **pull-only owner** for yourself
  at [agentics.dk](https://agentics.dk/products/pks-skills-marketplace). Public
  images pull without any credentials.

## Quick start

```bash
# 1. (optional) your registry pull credentials — skip for a public image
dotnet user-secrets --project src/MarketplaceHost \
  set Parameters:registry-username <owner>
dotnet user-secrets --project src/MarketplaceHost \
  set Parameters:registry-password <password>

# 2. run it
aspire run
```

The Aspire dashboard opens; click the **marketplace** endpoint to open the app.
Out of the box it runs in **unsecured** mode (auto-login as an admin) so you can
click around immediately. Switch to a real auth provider in
[`src/MarketplaceHost/AppHost.cs`](src/MarketplaceHost/AppHost.cs).

## Choosing an authentication mode

`AppHost.cs` picks one provider by calling a single extension method on the
marketplace resource:

| Method | Provider | What to set |
|---|---|---|
| `.AddUnsecured()` | Auto-login dev admin (no auth) | nothing — **local/dev only** |
| `.AddAzureEntraID()` | Microsoft Entra ID (Azure AD) | `Marketplace:Entra:*` + `Parameters:entra-client-secret` |
| `.AddOIDC()` | Any OIDC provider (Keycloak, Auth0, …) | `Marketplace:Oidc:*` + `Parameters:oidc-client-secret` |
| `.AddMagicLink()` | Passwordless email links (SMTP) | `Marketplace:Smtp:*` + `Parameters:smtp-pass` |

Example — lock the marketplace to your Entra tenant, admins by app-role:

```jsonc
// src/MarketplaceHost/appsettings.json
"Marketplace": {
  "PublicUrl": "https://marketplace.yourco.com",
  "Entra": { "TenantId": "<tenant-guid>", "ClientId": "<app-client-id>" },
  "Auth":  { "AllowedTenants": "<tenant-guid>", "AdminRoles": "Marketplace.Admin" }
}
```

```csharp
// src/MarketplaceHost/AppHost.cs
var marketplace = builder.AddMarketplace("marketplace").AddAzureEntraID();
```

```bash
dotnet user-secrets --project src/MarketplaceHost \
  set Parameters:entra-client-secret <secret>
dotnet user-secrets --project src/MarketplaceHost \
  set Parameters:nextauth-secret $(openssl rand -base64 32)
aspire run
```

## Configuration reference

Everything lives under `Marketplace:*` in
[`appsettings.json`](src/MarketplaceHost/appsettings.json). Blank values are
skipped so the container's own defaults stand. Set any value three ways:

- edit `appsettings.json`,
- an environment variable: `Marketplace__LogLevel=debug aspire run`,
- a command-line switch: `aspire run -- --Marketplace:Tag=1.4.0`.

**Secrets** (`registry-password`, `entra-client-secret`, `oidc-client-secret`,
`smtp-pass`, `nextauth-secret`) are Aspire **parameters** — store them with
`dotnet user-secrets set Parameters:<name> <value>` so they never land in a file.

| `Marketplace:` key | Container env var | Notes |
|---|---|---|
| `Image` / `Tag` | (image ref) | Defaults to `registry.agentics.dk/agentics/pks-agent-marketplace:latest`. Always re-pulled on start. |
| `RegistryUsername` / `RegistryPassword` | — | Used for a `docker login` before the run. Prefer the `registry-username` / `registry-password` parameters. |
| `PublicUrl` | `NEXTAUTH_URL` | Public base URL — must match your OAuth redirect URIs. |
| `AuthTrustHost` | `AUTH_TRUST_HOST` | Default `true`. |
| `LogLevel` | `LOG_LEVEL` | `trace`\|`debug`\|`info`\|`warn`\|`error`. |
| `MaskPatterns` | `MASK_PATTERNS` | Extra terminal/log masking patterns. |
| `BrandName` / `BrandColor` / `EmailFrom` | `EMAIL_BRAND_NAME` / `EMAIL_BRAND_COLOR` / `EMAIL_FROM` | Email branding. |
| `EnableBundledPlugins` | `ENABLE_PKS_AGENT_MARKETPLACE_PLUGINS` | `false` = start with an empty marketplace (no bundled Agentics plugins). |
| `EnableOrganizationManagement` | `ENABLE_ORGANIZATION_MANAGEMENT` | Show the Organization sidebar group. Default off. |
| `EnableCustomRoles` | `ENABLE_CUSTOM_ROLES` | Show People → Custom Roles. Default off. |
| `EnableLibrariesConnectors` | `ENABLE_LIBRARIES_CONNECTIONS` | Show Libraries → Connectors. Default off. |
| `EnablePersonalSettings` | `ENABLE_PERSONAL_SETTINGS` | Show the Personal group. Default off. |
| `Auth:AllowedTenants` | `AUTH_ALLOWED_TENANTS` | Entra `tid` allowlist — the "only our company" gate. |
| `Auth:AllowedEmailDomains` | `AUTH_ALLOWED_EMAIL_DOMAINS` | Email-domain allowlist. |
| `Auth:AccessMode` | `AUTH_ACCESS_MODE` | `open` (default) \| `invite` (deny-by-default). |
| `Auth:AllowedEmails` | `AUTH_ALLOWED_EMAILS` | Explicit allowlist for `invite` mode. |
| `Auth:AdminGroups` / `AdminRoles` / `AdminEmails` | `AUTH_ADMIN_GROUPS` / `AUTH_ADMIN_ROLES` / `AUTH_ADMIN_EMAILS` | Map Entra group-oid / app-role / email → marketplace `global-admin`. |

A random `nextauth-secret` is **required** for every provider except `unsecured`.

## Data persistence

The container's `/app/user-data` (catalog, orgs, sessions, settings) is mounted
to a named Docker volume `marketplace-data`, so it survives restarts. Remove it
with `docker volume rm marketplace-data` for a clean slate.

## Publishing to the cloud

`aspire run` is for local/self-hosted deployment. To generate deployment
artifacts (e.g. a Docker Compose file or Azure resources) use `aspire publish` —
the same configuration applies. See the
[Aspire deployment docs](https://learn.microsoft.com/dotnet/aspire/deployment/).
