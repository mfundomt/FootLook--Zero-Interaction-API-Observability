using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FootLook.Core.Security;
using Microsoft.Extensions.DependencyInjection;

namespace FootLook.Tests;

public class MicrosoftSignInEndpointTests : IAsyncLifetime
{
    private const string Path = "/footlook/auth/microsoft";

    private readonly MicrosoftTestTokens _tokens = new();
    private readonly FakeMicrosoftAccountStore _store = new();
    private FootLookTestHost _host = null!;

    public async Task InitializeAsync() => _host = await StartAsync();

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        _tokens.Dispose();
    }

    private Task<FootLookTestHost> StartAsync(Action<FootLook.Core.Options.FootLookOptions>? configure = null, bool withStore = true) =>
        FootLookTestHost.StartAsync(configure, services =>
        {
            services.AddSingleton(_tokens.CreateValidator());
            if (withStore)
            {
                services.AddSingleton<IFootLookMicrosoftAccountStore>(_store);
            }
        });

    private Task<HttpResponseMessage> SignInAsync(string mode, Action<TokenSpec>? spec = null, bool? acceptedTerms = true, FootLookTestHost? host = null) =>
        (host ?? _host).PostAsync(Path, body: new { idToken = _tokens.Create(spec), mode, acceptedTerms });

    private static async Task<JsonDocument> JsonOf(HttpResponseMessage response) => await FootLookTestHost.ReadJsonAsync(response);

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string error)
    {
        Assert.Equal(status, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        // Exactly { "error": "<code>" } - no reasons, no exception text.
        Assert.Equal(new[] { "error" }, doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(error, doc.RootElement.GetProperty("error").GetString());
    }

    // ---- success ------------------------------------------------------------------

    [Fact]
    public async Task Register_new_account_returns_200_with_the_contract_shape()
    {
        var response = await SignInAsync("register");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("passwordHash", raw, StringComparison.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        Assert.False(string.IsNullOrEmpty(root.GetProperty("token").GetString()));
        var expires = root.GetProperty("expiresAtUtc").GetDateTime();
        Assert.True(expires > DateTime.UtcNow.AddHours(7));
        Assert.EndsWith("Z", root.GetProperty("expiresAtUtc").GetString());
        Assert.True(root.GetProperty("isAdmin").GetBoolean());

        var user = root.GetProperty("user");
        Assert.Equal("dev@example.invalid", user.GetProperty("email").GetString());
        Assert.Equal("Dev Person", user.GetProperty("displayName").GetString());
        Assert.False(string.IsNullOrEmpty(user.GetProperty("id").GetString()));
        Assert.Equal(3, user.EnumerateObject().Count());

        var stored = Assert.Single(_store.Users);
        Assert.Equal(stored.Id, user.GetProperty("id").GetString());
        Assert.Equal("register", Assert.Single(_store.Events).Outcome);
    }

    [Fact]
    public async Task Issued_token_opens_the_session_and_works_on_protected_routes()
    {
        using var doc = await JsonOf(await SignInAsync("register"));
        var token = doc.RootElement.GetProperty("token").GetString()!;

        var me = await _host.GetAsync("/footlook/auth/me", token);

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var meDoc = await JsonOf(me);
        Assert.Equal("dev@example.invalid", meDoc.RootElement.GetProperty("user").GetProperty("email").GetString());
        Assert.True(meDoc.RootElement.GetProperty("observationActive").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await _host.GetAsync("/footlook/captures", token)).StatusCode);
    }

    [Fact]
    public async Task Signing_out_and_back_in_with_Microsoft_starts_a_fresh_empty_observation_session()
    {
        using var first = await JsonOf(await SignInAsync("register"));
        var token1 = first.RootElement.GetProperty("token").GetString()!;
        await _host.GetAsync("/hello");
        await _host.WaitForCaptureAsync(token1, "/hello");

        Assert.Equal(HttpStatusCode.OK, (await _host.PostAsync("/footlook/auth/logout", token1)).StatusCode);

        using var second = await JsonOf(await SignInAsync("login", acceptedTerms: false));
        var token2 = second.RootElement.GetProperty("token").GetString()!;

        // Same Microsoft account, but a new session: nothing from the old one.
        Assert.Empty(await _host.GetCapturePathsAsync(token2));
        Assert.Empty(_host.Services.GetRequiredService<FootLook.Core.Interfaces.IShadowCaptureStore>().GetAll());
    }

    [Fact]
    public async Task First_account_is_admin_and_the_next_is_not_and_the_admin_token_can_use_admin_routes()
    {
        using var first = await JsonOf(await SignInAsync("register", s => s.Oid = "first"));
        using var second = await JsonOf(await SignInAsync("register", s => s.Oid = "second"));

        Assert.True(first.RootElement.GetProperty("isAdmin").GetBoolean());
        Assert.False(second.RootElement.GetProperty("isAdmin").GetBoolean());

        var adminToken = first.RootElement.GetProperty("token").GetString();
        var userToken = second.RootElement.GetProperty("token").GetString();
        Assert.NotEqual(HttpStatusCode.Forbidden, (await _host.PostAsync("/footlook/captures/pause", adminToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.PostAsync("/footlook/captures/pause", userToken)).StatusCode);
    }

    [Fact]
    public async Task Login_for_an_existing_account_returns_200_and_records_a_login()
    {
        await SignInAsync("register");

        var response = await SignInAsync("login", acceptedTerms: false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(_store.Users);
        Assert.Equal(new[] { "register", "login" }, _store.Events.Select(e => e.Outcome).ToArray());
    }

    [Fact]
    public async Task Register_for_an_identity_that_already_exists_just_signs_in_and_records_login()
    {
        using var first = await JsonOf(await SignInAsync("register"));

        var again = await SignInAsync("register");

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        using var second = await JsonOf(again);
        Assert.Equal(
            first.RootElement.GetProperty("user").GetProperty("id").GetString(),
            second.RootElement.GetProperty("user").GetProperty("id").GetString());
        Assert.Single(_store.Users);
        Assert.Equal(new[] { "register", "login" }, _store.Events.Select(e => e.Outcome).ToArray());
    }

    [Fact]
    public async Task Same_email_with_a_different_tenant_or_subject_is_a_different_account()
    {
        using var a = await JsonOf(await SignInAsync("register", s => s.Oid = "subject-a"));
        using var b = await JsonOf(await SignInAsync("register", s => s.Oid = "subject-b"));
        using var c = await JsonOf(await SignInAsync("register", s => { s.Tid = MicrosoftIdTokenValidator.PersonalTenantId; s.Oid = "subject-a"; }));

        var ids = new[] { a, b, c }.Select(d => d.RootElement.GetProperty("user").GetProperty("id").GetString()).ToArray();
        Assert.Equal(3, ids.Distinct().Count());
        Assert.Equal(3, _store.Users.Count);
    }

    [Fact]
    public async Task User_agent_is_passed_to_the_store()
    {
        var register = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = JsonContent.Create(new { idToken = _tokens.Create(), mode = "register", acceptedTerms = true })
        };
        register.Headers.UserAgent.ParseAdd("FootLookTests/1.0");
        await _host.Client.SendAsync(register);

        Assert.Equal("FootLookTests/1.0", Assert.Single(_store.Events).UserAgent);
    }

    [Fact]
    public async Task Microsoft_accounts_cannot_log_in_with_a_password()
    {
        using var doc = await JsonOf(await SignInAsync("register"));

        foreach (var password in new[] { "", "Passw0rd!123", "anything-at-all" })
        {
            var response = await _host.LoginAsync("dev@example.invalid", password);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    // ---- error contract -----------------------------------------------------------

    [Fact]
    public async Task Login_when_no_account_exists_is_404_account_not_found()
    {
        await AssertErrorAsync(await SignInAsync("login", acceptedTerms: false), HttpStatusCode.NotFound, "account_not_found");
        Assert.Empty(_store.Users);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Register_without_accepted_terms_is_400_terms_not_accepted(bool? accepted)
    {
        var response = await SignInAsync("register", acceptedTerms: accepted);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "terms_not_accepted");
        Assert.Equal(0, _store.SignInCalls);
    }

    [Fact]
    public async Task Terms_are_not_required_for_login()
    {
        await SignInAsync("register");

        Assert.Equal(HttpStatusCode.OK, (await SignInAsync("login", acceptedTerms: null)).StatusCode);
    }

    [Fact]
    public async Task Invalid_microsoft_token_is_401_invalid_token_without_detail()
    {
        foreach (var idToken in new[] { "garbage", "a.b.c", _tokens.Create(s => { s.NotBefore = DateTime.UtcNow.AddHours(-3); s.Expires = DateTime.UtcNow.AddHours(-1); }), _tokens.Create(s => s.Audience = "someone-else") })
        {
            var response = await _host.PostAsync(Path, body: new { idToken, mode = "login", acceptedTerms = false });
            await AssertErrorAsync(response, HttpStatusCode.Unauthorized, "invalid_token");
        }

        Assert.Equal(0, _store.SignInCalls);
    }

    [Fact]
    public async Task Register_of_a_new_account_when_registration_is_closed_is_403_registration_closed()
    {
        await using var closed = await StartAsync(o => o.AllowRegistration = false);

        await AssertErrorAsync(await SignInAsync("register", host: closed), HttpStatusCode.Forbidden, "registration_closed");
        Assert.Empty(_store.Users);
    }

    [Fact]
    public async Task Existing_accounts_can_still_sign_in_when_registration_is_closed()
    {
        await SignInAsync("register");
        await using var closed = await StartAsync(o => o.AllowRegistration = false);

        Assert.Equal(HttpStatusCode.OK, (await SignInAsync("register", host: closed)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SignInAsync("login", acceptedTerms: false, host: closed)).StatusCode);
    }

    [Fact]
    public async Task Login_when_registration_is_closed_and_no_account_exists_is_still_account_not_found()
    {
        await using var closed = await StartAsync(o => o.AllowRegistration = false);

        await AssertErrorAsync(await SignInAsync("login", acceptedTerms: false, host: closed), HttpStatusCode.NotFound, "account_not_found");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{not json")]
    [InlineData("{}")]
    [InlineData("{\"idToken\":\"x\"}")]
    [InlineData("{\"mode\":\"login\"}")]
    [InlineData("{\"idToken\":\"\",\"mode\":\"login\"}")]
    [InlineData("{\"idToken\":\"x\",\"mode\":\"delete\"}")]
    [InlineData("{\"idToken\":\"x\",\"mode\":\"login\",\"acceptedTerms\":\"yes\"}")]
    [InlineData("{\"idToken\":12,\"mode\":\"login\"}")]
    public async Task Malformed_requests_are_400_invalid_request(string body)
    {
        var response = await _host.Client.PostAsync(Path, new StringContent(body, Encoding.UTF8, "application/json"));

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task Missing_body_is_400_invalid_request()
    {
        var response = await _host.Client.PostAsync(Path, content: null);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task Oversized_body_is_400_invalid_request()
    {
        var body = "{\"idToken\":\"" + new string('a', 40_000) + "\",\"mode\":\"login\"}";
        var response = await _host.Client.PostAsync(Path, new StringContent(body, Encoding.UTF8, "application/json"));

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task Without_an_account_store_the_endpoint_answers_503_accounts_unavailable()
    {
        await using var noStore = await StartAsync(withStore: false);

        await AssertErrorAsync(await SignInAsync("register", host: noStore), HttpStatusCode.ServiceUnavailable, "accounts_unavailable");
        // A bad token is still just a bad token, whatever the store situation.
        var garbage = await noStore.PostAsync(Path, body: new { idToken = "garbage", mode = "login" });
        await AssertErrorAsync(garbage, HttpStatusCode.Unauthorized, "invalid_token");
    }

    [Fact]
    public async Task A_failing_store_is_503_accounts_unavailable_and_leaks_nothing()
    {
        _store.ThrowOnSignIn = new InvalidOperationException("Server=secret.example;Password=hunter2");

        var response = await SignInAsync("register");

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, "accounts_unavailable");
        Assert.DoesNotContain("hunter2", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Existing_password_endpoints_still_work_alongside_the_microsoft_one()
    {
        var token = await _host.RegisterAndLoginAsync("password.user@example.com");

        Assert.Equal(HttpStatusCode.OK, (await _host.GetAsync("/footlook/auth/me", token)).StatusCode);
    }

    [Fact]
    public async Task Route_only_accepts_post()
    {
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _host.GetAsync(Path)).StatusCode);
    }
}
