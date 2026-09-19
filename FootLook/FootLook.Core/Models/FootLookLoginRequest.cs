namespace FootLook.Core.Models
{
    /// <summary>Request body for POST {EndpointBasePath}/auth/token.</summary>
    public sealed record FootLookLoginRequest(string? ApiKey);
}
