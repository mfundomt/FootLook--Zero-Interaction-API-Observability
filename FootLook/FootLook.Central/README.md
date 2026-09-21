# FootLook.Central

The FootLook central service: accounts, projects, members, invite codes and signed passes.

## What it does

FootLook captures never leave the developer's own API host. This service only knows *who* may open the dashboard of *which* project.

- **Accounts.** A developer signs in with Microsoft (work or personal). `POST /auth/microsoft` validates the Microsoft ID token (FootLook.Core's `MicrosoftIdTokenValidator`), finds or creates the account in the Azure SQL `Users` table (FootLook.Data's `SqlFootLookAccountStore`; identity is tenant + subject, never the email) and returns a **central session token** (RS256 JWT, 8 hours, `aud = footlook-central`).
- **Projects.** A developer registers their API as a project (`prj_` + 16 base32 characters, public and unguessable), lists the return URLs the dashboard may be sent back to, and invites testers.
- **Invite codes.** `FL-` + 10 characters from an unambiguous alphabet (about 50 bits, CSPRNG). The code is returned once, when it is created; only its SHA-256 hash is stored. Redeeming makes the caller a member (limits: 25 members per project, 20 live invites per project, 1-25 uses, 1-720 hours). Repeated failed redemptions are throttled per user.
- **Passes.** `POST /projects/{id}/connect` gives the owner or a member a short-lived **pass** (RS256 JWT, 5 minutes, `aud = <ProjectId>`, `role = owner|member`, single-use `jti`) and the return URL to send the browser to. A non-member gets 404 `project_not_found` (never 403). The return URL must exactly match one of the project's allowed URLs, or be `http://localhost:<port>` / `http://127.0.0.1:<port>`.
- **Public key.** `GET /.well-known/jwks.json` publishes the verification keys. A host with only its ProjectId downloads them, verifies the pass, and starts its normal local observation session. No secret is ever given to a developer.

A pass is never valid as a central session token and vice versa: the audiences differ.

## Endpoints

| Route | Notes |
|---|---|
| `POST /auth/microsoft` | anonymous; `{ idToken, mode: "login"\|"register", acceptedTerms }` |
| `GET /me` | `{ id, email, displayName, isAdmin }` |
| `POST /projects`, `GET /projects` | create (max 10 owned) / list |
| `GET /projects/{id}`, `PATCH /projects/{id}`, `DELETE /projects/{id}` | PATCH and DELETE: owner only |
| `GET /projects/{id}/members`, `DELETE /projects/{id}/members/{userId}` | owner removes anyone but the owner; a member may remove themself |
| `POST /projects/{id}/invites`, `GET /projects/{id}/invites`, `DELETE /projects/{id}/invites/{inviteId}` | owner only |
| `POST /invites/redeem` | `{ code }` |
| `POST /projects/{id}/connect` | `{ returnUrl }` -> `{ pass, expiresAtUtc, returnUrl }` |
| `GET /.well-known/jwks.json` | anonymous, `Cache-Control: public, max-age=3600` |
| `GET /health` | anonymous liveness probe |

Errors are always `{ "error": "<code>" }`. There are no cookies anywhere; CORS allows no credentials.

## Configuration

Keys are ASP.NET configuration keys (`:` in files, `__` in environment variables and App Service settings).

| Key | Meaning | Default |
|---|---|---|
| `Central:Issuer` | `iss` of tokens and base of the JWKS address. Hosts must use the same value. | `https://footlook-auth.azurewebsites.net` (`http://localhost:5200` in Development) |
| `Central:SigningKeyPem` | **Secret.** RSA private key (PKCS8 or PKCS1 PEM, at least 2048 bits). Line breaks may be written as a literal `\n`, or the whole PEM may be base64-encoded. | none |
| `Central:PreviousSigningKeyPems` (array) | Earlier keys, public or private PEM, still published and accepted (rotation). | empty |
| `Central:KeyVaultKeyUri` | Azure Key Vault RSA key, `https://<vault>.vault.azure.net/keys/<name>[/<version>]`. Takes priority over the PEM. | none |
| `Central:KeyVaultPreviousKeyUris` (array) | Earlier vault keys still published (rotation). | empty |
| `Central:AllowRegistration` | `false` refuses new registrations (403 `registration_closed`). | `true` |
| `Central:AllowedOrigins` (array) | CORS origins. Empty means `https://www.footlook.co.za`, `https://footlook.co.za`, `http://localhost:4200`, `http://localhost:4210`. | empty |
| `Central:TrustForwardedHeaders` | Use `X-Forwarded-For` / `-Proto` from the App Service front end (last hop only) so per-IP limits see the client. | `true` |
| `Central:RateLimits:SignInPerMinute` / `RedeemPerMinute` / `ConnectPerMinute` | Fixed one-minute windows: sign-in per IP; redeem and connect per user. | 20 / 20 / 30 |
| `Central:RedeemMaxFailures`, `Central:RedeemFailureWindowMinutes` | Failed redemptions allowed per user in the window before 429 `too_many_attempts`. | 10 / 15 |
| `FootLook:Microsoft:ClientId` | The Entra app (client id `264de9b0-15e7-4887-b686-5b6c18a2f376`, public). Other keys of that section (`MetadataAddress`, `ClockSkewSeconds`, `MaxIdTokenAgeMinutes`) are FootLook.Core's. | as shown |
| `ConnectionStrings:FootLookAccounts` | **Secret.** Azure SQL connection string. Without it the service starts but sign-in answers 503 `accounts_unavailable` and project routes 503 `service_unavailable`. | none |

### The signing key

- **Local PEM.** `Central:SigningKeyPem`, supplied as an application setting or environment variable, never committed. In the Development environment only, when neither a PEM nor a Key Vault key is set, an **ephemeral key is generated at startup** with a loud warning: everything it signed stops working after a restart. In any other environment a missing key **stops startup** with a clear message. A key that is set but unusable (garbage, public-only, under 2048 bits) also stops startup, even in Development.
- **Azure Key Vault.** `Central:KeyVaultKeyUri` with `DefaultAzureCredential` (the App Service managed identity). Signing happens inside the vault (`CryptographyClient`, RS256), so the private key never reaches this process; the JWKS is built from the public part of the same key version. At startup one probe signature is made and verified against the public key, so a missing `sign` permission or the wrong key fails startup, not the first sign-in. The identity needs the key permissions `get` and `sign`. **This path has not been run against a real vault.**
- `kid` is the RFC 7638 thumbprint of the public key, so it is stable for a given key.
- **Rotation.** Put the new key in `SigningKeyPem` / `KeyVaultKeyUri` and the old one in `PreviousSigningKeyPems` / `KeyVaultPreviousKeyUris`. Both are published (current first) and central still accepts session tokens signed with the old one. Remove the old entry after 8 hours (the longest token lifetime) plus the hosts' JWKS cache time (1 hour).

## Database

Apply `FootLook.Data/Sql/002_projects.sql` (idempotent, additive; it creates `Projects`, `ProjectReturnUrls`, `ProjectMembers`, `ProjectInvites` and never touches `dbo.Users` or `dbo.LoginEvents`) after `001_create_users.sql`:

```
sqlcmd -S <server>.database.windows.net -d <database> -U <admin> -N -b -i FootLook.Data/Sql/002_projects.sql
```

(the password through the `SQLCMDPASSWORD` environment variable of that process only). Transient SQL errors are retried, like the account store.

## Run locally

```
cd FootLook/FootLook.Central
dotnet run --launch-profile http        # http://localhost:5200, Development, ephemeral signing key
```

Add the connection string for the projects/connect routes, through user secrets or the environment (never a committed file):

```
dotnet user-secrets set "ConnectionStrings:FootLookAccounts" "<connection string>"
```

A real Microsoft sign-in needs a browser (the site at `http://localhost:4210`). To exercise the API without one, the tests mint a central session token with the test key.

Tests: `dotnet test FootLook.Central.Tests`. SQL integration tests run only when the environment variable `FOOTLOOK_TEST_SQL` holds a connection string to a database that has both SQL scripts applied; they use random tenants and `@example.invalid` emails and delete everything they create.

## Azure App Service (`footlook-auth`) settings required

Names only; set the values in the portal or through Key Vault references.

- `ConnectionStrings__FootLookAccounts` (secret)
- One of `Central__SigningKeyPem` (secret) or `Central__KeyVaultKeyUri` (plus a system-assigned managed identity with key permissions `get` and `sign` on that vault)
- `ASPNETCORE_ENVIRONMENT` = `Production` (so a missing key stops startup instead of using a throwaway key)
- `Central__Issuer` = `https://footlook-auth.azurewebsites.net` (the default; set it if a custom domain is used)

Optional: `Central__AllowRegistration`, `Central__AllowedOrigins__0` ..., `Central__PreviousSigningKeyPems__0` ..., `Central__KeyVaultPreviousKeyUris__0` ..., `Central__RateLimits__*`, `FootLook__Microsoft__ClientId`. Turn on **HTTPS Only** in the App Service. Run a single instance: the failed-redemption throttle is kept in memory per instance.

## Logging

Tokens, passes, invite codes and the connection string are never logged, and error responses carry only `{ "error": "<code>" }`. Unexpected failures are logged by exception type only (plus the SQL error number), because an exception's text can carry connection details.
