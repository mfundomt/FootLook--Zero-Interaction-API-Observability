using FootLook.Core.Security;
using FootLook.Data.Accounts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace FootLook.Data.Extensions;

public static class FootLookSqlAccountsServiceCollectionExtensions
{
    /// <summary>
    /// Stores Microsoft (Entra ID) accounts in Azure SQL. Does nothing - and never throws - when
    /// the connection string is empty, so a host without it still starts; POST
    /// {EndpointBasePath}/auth/microsoft then answers 503 { "error": "accounts_unavailable" }.
    /// Nothing connects at startup: the first connection is made by the first sign-in.
    /// Call it before or after AddFootLook (the account store is registered with TryAdd).
    /// </summary>
    public static IServiceCollection AddFootLookSqlAccounts(this IServiceCollection services, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return services;
        }

        services.TryAddSingleton<IFootLookMicrosoftAccountStore>(provider =>
            new SqlFootLookAccountStore(connectionString, provider.GetService<ILogger<SqlFootLookAccountStore>>()));

        return services;
    }

    /// <summary>
    /// Reads <c>ConnectionStrings:{connectionName}</c> (default FootLookAccounts). Keep that value
    /// out of source control: user-secrets locally, an App Service application setting named
    /// <c>ConnectionStrings__FootLookAccounts</c> (or a Key Vault reference) when deployed.
    /// </summary>
    public static IServiceCollection AddFootLookSqlAccounts(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionName = "FootLookAccounts") =>
        services.AddFootLookSqlAccounts(configuration.GetConnectionString(connectionName));
}
