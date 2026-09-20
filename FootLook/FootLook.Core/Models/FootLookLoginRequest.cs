namespace FootLook.Core.Models
{
    /// <summary>Request body for POST {EndpointBasePath}/auth/login.</summary>
    public sealed record FootLookLoginRequest(string? Email, string? Password);

    /// <summary>Request body for POST {EndpointBasePath}/auth/register.</summary>
    public sealed record FootLookRegisterRequest(string? Email, string? Password, string? DisplayName);
}
