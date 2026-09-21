namespace FootLook.Central.Security;

/// <summary>
/// The rules for where a browser may be sent back to with a pass. This is the open-redirect defence, so it is
/// strict on purpose: anything odd is refused rather than repaired.
/// <para>
/// A URL is accepted only when it starts with http:// or https:// (no schemes, relative forms or
/// protocol-relative "//host"), is plain printable ASCII (no whitespace, control characters or backslashes),
/// has no userinfo, no trailing-dot host, no IPv6 literal, and no encoded slash / dot / backslash in its
/// path. It is then rebuilt from the parsed parts (lower-case host, dot segments resolved), and the rebuilt
/// value is what is compared and what is echoed: what is authorised is exactly what the browser is sent to.
/// </para>
/// </summary>
public static class ReturnUrlPolicy
{
    public const int MaxAllowedUrlLength = 300;
    public const int MaxRequestedUrlLength = 2048;
    public const int MaxAllowedUrls = 5;

    private sealed record Parsed(string Scheme, string Host, int Port, bool DefaultPort, string Path, bool HasQuery, bool HasFragment)
    {
        public string Canonical => $"{Scheme}://{Host}{(DefaultPort ? string.Empty : ":" + Port)}{Path}";
    }

    /// <summary>
    /// Validates a URL a project owner wants to allow and returns its normalised form. https (or http for
    /// localhost / 127.0.0.1), absolute, no userinfo, no fragment, no query, no wildcards, at most 300 chars.
    /// </summary>
    public static bool TryNormalizeAllowed(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null || value.Length > MaxAllowedUrlLength || value.Contains('*'))
        {
            return false;
        }

        if (!TryParse(value, out var url) || url.HasFragment || url.HasQuery)
        {
            return false;
        }

        var loopback = IsLoopbackHost(url.Host);
        if (url.Scheme == Uri.UriSchemeHttp && !loopback)
        {
            return false;
        }

        normalized = url.Canonical;
        return normalized.Length <= MaxAllowedUrlLength;
    }

    /// <summary>
    /// Decides whether <paramref name="requested"/> is a permitted return address. On success
    /// <paramref name="returnUrl"/> is the value to send the browser to: the project's own stored entry when one
    /// matches (scheme, host, port and path, ignoring a trailing slash, and ignoring any query or fragment the
    /// caller added), or - for http://localhost:* and http://127.0.0.1:*, any path - the requested URL rebuilt
    /// without its query and fragment. Never anything else.
    /// </summary>
    public static bool TryMatch(string? requested, IEnumerable<string> allowedUrls, out string returnUrl)
    {
        returnUrl = string.Empty;
        if (requested is null || requested.Length > MaxRequestedUrlLength || !TryParse(requested, out var candidate))
        {
            return false;
        }

        if (candidate.Scheme == Uri.UriSchemeHttp && IsLoopbackHost(candidate.Host))
        {
            returnUrl = candidate.Canonical;
            return true;
        }

        foreach (var stored in allowedUrls)
        {
            // Stored values were normalised on the way in, but re-check: a bad row must never match.
            if (!TryParse(stored, out var allowed))
            {
                continue;
            }

            if (allowed.Scheme == candidate.Scheme &&
                allowed.Host == candidate.Host &&
                allowed.Port == candidate.Port &&
                string.Equals(TrimTrailingSlash(allowed.Path), TrimTrailingSlash(candidate.Path), StringComparison.Ordinal))
            {
                returnUrl = stored;
                return true;
            }
        }

        return false;
    }

    private static bool IsLoopbackHost(string host) => host is "localhost" or "127.0.0.1";

    private static string TrimTrailingSlash(string path) => path.TrimEnd('/');

    private static bool TryParse(string raw, out Parsed parsed)
    {
        parsed = null!;
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        // Printable ASCII only: no whitespace, control characters, backslashes or non-ASCII look-alikes.
        foreach (var c in raw)
        {
            if (c < 0x21 || c > 0x7E || c == '\\')
            {
                return false;
            }
        }

        var hasScheme = raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (!hasScheme)
        {
            return false;
        }

        var fragmentAt = raw.IndexOf('#');
        var hasFragment = fragmentAt >= 0;
        var beforeFragment = hasFragment ? raw[..fragmentAt] : raw;
        var queryAt = beforeFragment.IndexOf('?');
        var hasQuery = queryAt >= 0;
        var withoutQuery = hasQuery ? beforeFragment[..queryAt] : beforeFragment;

        // The authority is what sits between "://" and the first "/"; "@" there is userinfo (or a trick).
        var authorityStart = withoutQuery.IndexOf("://", StringComparison.Ordinal) + 3;
        var pathAt = withoutQuery.IndexOf('/', authorityStart);
        var authority = pathAt < 0 ? withoutQuery[authorityStart..] : withoutQuery[authorityStart..pathAt];
        if (authority.Length == 0 || authority.Contains('@') || authority.Contains('[') || authority.Contains(']'))
        {
            return false;
        }

        if (!Uri.TryCreate(withoutQuery, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) ||
            uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4) ||
            string.IsNullOrEmpty(uri.Host) ||
            uri.Host.EndsWith('.'))
        {
            return false;
        }

        // Checked on what was written, not on what Uri made of it: Uri resolves an encoded dot segment away.
        var path = uri.AbsolutePath;
        if (ContainsEncodedSeparator(withoutQuery) || ContainsEncodedSeparator(path))
        {
            return false;
        }

        parsed = new Parsed(uri.Scheme, uri.IdnHost.ToLowerInvariant(), uri.Port, uri.IsDefaultPort, path, hasQuery, hasFragment);
        return true;
    }

    private static bool ContainsEncodedSeparator(string path)
    {
        for (var i = 0; i + 2 < path.Length; i++)
        {
            if (path[i] != '%')
            {
                continue;
            }

            var code = path.Substring(i + 1, 2);
            if (code.Equals("2e", StringComparison.OrdinalIgnoreCase) ||
                code.Equals("2f", StringComparison.OrdinalIgnoreCase) ||
                code.Equals("5c", StringComparison.OrdinalIgnoreCase) ||
                code == "00")
            {
                return true;
            }
        }

        return false;
    }
}
