namespace FootLook.Core.Options
{
    /// <summary>
    /// One caller-facing API key configured for a FootLook host.
    /// </summary>
    /// <param name="Key">The secret value the caller must present.</param>
    /// <param name="IsAdmin">
    /// Whether this key may perform host-wide actions (pause/resume capture, clear the
    /// privacy audit log, run self-heal/setup) rather than only read/clear its own scope.
    /// </param>
    /// <param name="Label">Optional human-readable name for logging/auditing which key was used.</param>
    public sealed record FootLookApiKey(string Key, bool IsAdmin = false, string? Label = null);
}
