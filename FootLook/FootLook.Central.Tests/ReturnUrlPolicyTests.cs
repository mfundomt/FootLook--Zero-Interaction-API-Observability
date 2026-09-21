using FootLook.Central.Security;

namespace FootLook.Central.Tests;

/// <summary>The open-redirect defence, tried against the URL tricks people actually use.</summary>
public class ReturnUrlPolicyTests
{
    private static readonly string[] Allowed = { "https://good.example/app/footlook.html" };

    // ---- what an owner may put in the allow-list -------------------------------------------------

    [Theory]
    [InlineData("https://app.example.com/footlook.html", "https://app.example.com/footlook.html")]
    [InlineData("HTTPS://App.Example.COM/Footlook.html", "https://app.example.com/Footlook.html")] // host lower-cased, path is case-sensitive
    [InlineData("https://app.example.com", "https://app.example.com/")]
    [InlineData("https://app.example.com:8443/x", "https://app.example.com:8443/x")]
    [InlineData("https://app.example.com:443/x", "https://app.example.com/x")]
    [InlineData("http://localhost:5103/footlook.html", "http://localhost:5103/footlook.html")]
    [InlineData("http://127.0.0.1:5103/x", "http://127.0.0.1:5103/x")]
    [InlineData("https://localhost:5001/x", "https://localhost:5001/x")]
    public void A_valid_allowed_url_is_normalised(string input, string expected)
    {
        Assert.True(ReturnUrlPolicy.TryNormalizeAllowed(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/footlook.html")]                              // relative
    [InlineData("//evil.example/x")]                            // protocol-relative
    [InlineData("footlook.html")]
    [InlineData("http://app.example.com/x")]                    // http only for localhost / 127.0.0.1
    [InlineData("ftp://app.example.com/x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://user@app.example.com/x")]              // userinfo
    [InlineData("https://user:pw@app.example.com/x")]
    [InlineData("https://app.example.com/x#frag")]              // fragment
    [InlineData("https://app.example.com/x#")]
    [InlineData("https://app.example.com/x?a=1")]               // query has no place in an allow-list entry
    [InlineData("https://*.example.com/x")]                     // wildcards
    [InlineData("https://app.example.com/*")]
    [InlineData("https://app.example.com./x")]                  // trailing-dot host
    [InlineData("https://app.example.com/x y")]
    [InlineData("https://app.example.com/x\ty")]
    [InlineData("https://app.example.com\\x")]
    [InlineData("https://[::1]/x")]
    [InlineData("http://evil.example/x")]
    [InlineData("https://exämple.com/x")]                       // non-ASCII: must be punycode
    [InlineData("https://app.example.com/%2e%2e/x")]            // encoded dots
    [InlineData("https://app.example.com/a%2fb")]               // encoded slash
    [InlineData("https://")]
    [InlineData("https:///x")]
    [InlineData("https:app.example.com/x")]
    public void An_invalid_allowed_url_is_refused(string? input)
    {
        Assert.False(ReturnUrlPolicy.TryNormalizeAllowed(input, out _));
    }

    [Fact]
    public void An_allowed_url_over_300_characters_is_refused_and_300_is_fine()
    {
        var prefix = "https://app.example.com/";
        Assert.True(ReturnUrlPolicy.TryNormalizeAllowed(prefix + new string('a', 300 - prefix.Length), out _));
        Assert.False(ReturnUrlPolicy.TryNormalizeAllowed(prefix + new string('a', 301 - prefix.Length), out _));
    }

    // ---- what a caller may ask to be sent back to ------------------------------------------------

    [Theory]
    [InlineData("https://good.example/app/footlook.html")]
    [InlineData("https://GOOD.example/app/footlook.html")]                  // upper-case host
    [InlineData("HTTPS://good.example/app/footlook.html")]                  // upper-case scheme
    [InlineData("https://good.example/app/footlook.html/")]                 // trailing slash difference
    [InlineData("https://good.example/app/footlook.html?next=1")]           // caller-added query is ignored
    [InlineData("https://good.example/app/footlook.html?a=b&c=d#frag")]
    [InlineData("https://good.example/app/footlook.html#pass=abc")]         // caller-added fragment is ignored
    [InlineData("https://good.example:443/app/footlook.html")]              // explicit default port is the same origin
    public void A_matching_url_is_accepted_and_the_stored_entry_is_what_is_returned(string requested)
    {
        Assert.True(ReturnUrlPolicy.TryMatch(requested, Allowed, out var returnUrl));
        Assert.Equal("https://good.example/app/footlook.html", returnUrl);
    }

    [Fact]
    public void Trailing_slash_difference_works_both_ways()
    {
        Assert.True(ReturnUrlPolicy.TryMatch("https://good.example/app", new[] { "https://good.example/app/" }, out var a));
        Assert.Equal("https://good.example/app/", a);
        Assert.True(ReturnUrlPolicy.TryMatch("https://good.example/app/", new[] { "https://good.example/app" }, out var b));
        Assert.Equal("https://good.example/app", b);
        Assert.True(ReturnUrlPolicy.TryMatch("https://good.example", new[] { "https://good.example/" }, out _));
        Assert.True(ReturnUrlPolicy.TryMatch("https://good.example/", new[] { "https://good.example/" }, out _));
    }

    [Theory]
    // userinfo tricks
    [InlineData("https://good.example@evil.example/app/footlook.html")]
    [InlineData("https://evil.example@good.example/app/footlook.html")]
    [InlineData("https://good.example:pw@evil.example/app/footlook.html")]
    [InlineData("https://good.example%40evil.example/app/footlook.html")]
    [InlineData("https://good.example#@evil.example/app/footlook.html")]
    [InlineData("https://good.example?@evil.example/app/footlook.html")]
    // a different origin
    [InlineData("https://evil.example/app/footlook.html")]
    [InlineData("https://good.example.evil.example/app/footlook.html")]
    [InlineData("https://evilgood.example/app/footlook.html")]
    [InlineData("https://sub.good.example/app/footlook.html")]
    [InlineData("https://evil.example/https://good.example/app/footlook.html")]
    [InlineData("https://evil.example/?u=https://good.example/app/footlook.html")]
    // ports
    [InlineData("https://good.example:8443/app/footlook.html")]
    [InlineData("https://good.example:444/app/footlook.html")]
    [InlineData("https://good.example:80/app/footlook.html")]
    // scheme
    [InlineData("http://good.example/app/footlook.html")]
    [InlineData("ftp://good.example/app/footlook.html")]
    // trailing dot
    [InlineData("https://good.example./app/footlook.html")]
    // paths
    [InlineData("https://good.example/app/../admin")]
    [InlineData("https://good.example/app/footlook.html/../../evil")]
    [InlineData("https://good.example/other")]
    [InlineData("https://good.example/app/footlook.html/extra")]
    [InlineData("https://good.example/app/footlook.htm")]
    [InlineData("https://good.example/APP/footlook.html")]                  // paths are case-sensitive
    [InlineData("https://good.example/app/%2e%2e/footlook.html")]
    [InlineData("https://good.example/app/%2E%2E/footlook.html")]
    [InlineData("https://good.example/app%2ffootlook.html")]
    [InlineData("https://good.example/app/footlook.html%00")]
    // relative and protocol-relative
    [InlineData("//evil.example")]
    [InlineData("//good.example/app/footlook.html")]
    [InlineData("/app/footlook.html")]
    [InlineData("app/footlook.html")]
    [InlineData("\\\\evil.example\\x")]
    [InlineData("///evil.example")]
    [InlineData("https:evil.example")]
    [InlineData("https:/evil.example")]
    [InlineData("https:///evil.example")]
    // schemes that run code or embed content
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)//https://good.example/app/footlook.html")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("file:///c:/windows/win.ini")]
    // whitespace, control characters, backslashes, look-alikes
    [InlineData(" https://good.example/app/footlook.html")]
    [InlineData("https://good.example/app/footlook.html ")]
    [InlineData("https://good.example/app/footlook.html\n")]
    [InlineData("https://good.example/app/footlook.html\r\nLocation: https://evil.example")]
    [InlineData("https://good.example/app/foot\tlook.html")]
    [InlineData("https://good.example\\@evil.example/app/footlook.html")]
    [InlineData("https://good.example/app\\footlook.html")]
    [InlineData("https://gооd.example/app/footlook.html")]                  // Cyrillic o
    [InlineData("https://good.example/app/footlook.html\u200b")]
    // emptiness
    [InlineData("")]
    [InlineData("https://")]
    [InlineData("https://:443/app")]
    public void A_url_that_is_not_the_allowed_one_is_refused(string requested)
    {
        Assert.False(ReturnUrlPolicy.TryMatch(requested, Allowed, out var returnUrl));
        Assert.Equal(string.Empty, returnUrl);
    }

    [Fact]
    public void Null_and_very_long_urls_are_refused()
    {
        Assert.False(ReturnUrlPolicy.TryMatch(null, Allowed, out _));
        Assert.False(ReturnUrlPolicy.TryMatch("https://good.example/app/footlook.html?" + new string('a', 5000), Allowed, out _));
        Assert.False(ReturnUrlPolicy.TryMatch("https://good.example/" + new string('a', 3000), Allowed, out _));
        // Just under the cap is fine when it matches.
        Assert.True(ReturnUrlPolicy.TryMatch("https://good.example/app/footlook.html?" + new string('a', 1900), Allowed, out _));
    }

    [Fact]
    public void An_empty_allow_list_refuses_every_non_localhost_url()
    {
        Assert.False(ReturnUrlPolicy.TryMatch("https://good.example/app/footlook.html", Array.Empty<string>(), out _));
    }

    [Fact]
    public void A_project_with_several_entries_matches_any_one_of_them_exactly()
    {
        var list = new[] { "https://a.example/x", "https://b.example:8443/y" };
        Assert.True(ReturnUrlPolicy.TryMatch("https://b.example:8443/y", list, out var url));
        Assert.Equal("https://b.example:8443/y", url);
        Assert.False(ReturnUrlPolicy.TryMatch("https://a.example/y", list, out _));
        Assert.False(ReturnUrlPolicy.TryMatch("https://b.example/y", list, out _));
    }

    // ---- localhost: any port, any path, for every project ----------------------------------------

    [Theory]
    [InlineData("http://localhost:5103/footlook.html", "http://localhost:5103/footlook.html")]
    [InlineData("http://localhost:4200/", "http://localhost:4200/")]
    [InlineData("http://localhost:65535/a/b/c", "http://localhost:65535/a/b/c")]
    [InlineData("http://127.0.0.1:8080/x", "http://127.0.0.1:8080/x")]
    [InlineData("HTTP://LOCALHOST:5103/x", "http://localhost:5103/x")]
    [InlineData("http://localhost:5103/x?a=1#pass=zzz", "http://localhost:5103/x")]   // query and fragment are dropped from what is echoed
    [InlineData("http://localhost:5103/a/../b", "http://localhost:5103/b")]           // the browser is sent to the resolved path
    public void Loopback_urls_are_allowed_for_any_project_and_echoed_without_query_or_fragment(string requested, string expected)
    {
        Assert.True(ReturnUrlPolicy.TryMatch(requested, Array.Empty<string>(), out var returnUrl));
        Assert.Equal(expected, returnUrl);
    }

    [Theory]
    [InlineData("http://localhost.evil.example:4200/x")]
    [InlineData("http://localhost.:4200/x")]
    [InlineData("http://localhost:4200@evil.example/x")]
    [InlineData("http://localhost@evil.example:4200/x")]
    [InlineData("http://evil.example#@localhost:4200/x")]
    [InlineData("http://evil.example\\@localhost:4200/x")]
    [InlineData("http://evil.example/http://localhost:4200/x")]
    [InlineData("https://localhost:4200/x")]                                  // https localhost is not automatic (list it if needed)
    [InlineData("http://[::1]:4200/x")]
    [InlineData("http://0.0.0.0:4200/x")]
    [InlineData("http://192.168.1.10:4200/x")]
    [InlineData("http://localhostx:4200/x")]
    [InlineData("http://xlocalhost:4200/x")]
    [InlineData("http://127.0.0.1.evil.example:4200/x")]
    [InlineData("http://localhost:4200%2f@evil.example/x")]
    [InlineData("http://localhost:99999/x")]
    [InlineData("http://localhost:abc/x")]
    public void Look_alike_loopback_urls_are_refused(string requested)
    {
        Assert.False(ReturnUrlPolicy.TryMatch(requested, Array.Empty<string>(), out _));
    }

    [Fact]
    public void A_stored_row_that_is_not_a_valid_url_can_never_match()
    {
        Assert.False(ReturnUrlPolicy.TryMatch("https://good.example/app", new[] { "javascript:alert(1)", "", "//good.example/app", "https://good.example@evil.example/app" }, out _));
    }
}
