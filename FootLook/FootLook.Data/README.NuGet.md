# FootLook.Data

FootLook.Data provides data access helpers and repository integrations used by FootLook.

## Install

```bash
dotnet add package FootLook.Data
```

## Notes

- Targets .NET 8.
- Depends on `FootLook.Core`.

## Microsoft sign-in accounts (Azure SQL)

`AddFootLookSqlAccounts` stores accounts that sign in with Microsoft (Entra ID) in SQL, for
`POST {EndpointBasePath}/auth/microsoft`. Create the schema once with `Sql/001_create_users.sql`.

```csharp
builder.Services.AddFootLookSqlAccounts(builder.Configuration); // reads ConnectionStrings:FootLookAccounts
builder.Services.AddFootLook(options => { /* ... */ });
```

- `ConnectionStrings:FootLookAccounts` is a **secret and is never committed**. Locally use
  `dotnet user-secrets set "ConnectionStrings:FootLookAccounts" "<connection string>"`; on Azure App
  Service set the application setting `ConnectionStrings__FootLookAccounts` (or a Key Vault reference).
  Use `Connection Timeout=60`: a serverless database that auto-paused takes up to ~60 s to resume.
- Without that setting the host still starts, and the endpoint answers `503 { "error": "accounts_unavailable" }`.
- `FootLook:Microsoft:ClientId` (public, in appsettings.json) is the audience ID tokens must have.
- Accounts are keyed by (Microsoft tenant id, subject) - never by email. The first account ever created
  becomes the admin, so close sign-up (`FootLook:AllowRegistration=false`) once the accounts you want exist.
