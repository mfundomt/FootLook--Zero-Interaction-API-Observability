using System.Security.Cryptography;
using System.Text;
using FootLook.Core.Security;
using Microsoft.IdentityModel.Tokens;

namespace FootLook.Tests;

public class MicrosoftIdTokenValidatorTests : IDisposable
{
    private readonly MicrosoftTestTokens _tokens = new();
    private readonly MicrosoftIdTokenValidator _validator;

    public MicrosoftIdTokenValidatorTests()
    {
        _validator = _tokens.CreateValidator();
    }

    public void Dispose() => _tokens.Dispose();

    private Task<MicrosoftTokenValidationResult> ValidateAsync(Action<TokenSpec>? configure = null) =>
        _validator.ValidateAsync(_tokens.Create(configure));

    private static void AssertRejected(MicrosoftTokenValidationResult result, string reason)
    {
        Assert.False(result.IsValid);
        Assert.Null(result.Identity);
        Assert.Equal(reason, result.Failure);
        Assert.False(result.Unavailable);
    }

    [Fact]
    public async Task Valid_work_token_yields_the_identity()
    {
        var result = await ValidateAsync();

        Assert.True(result.IsValid);
        var identity = result.Identity!;
        Assert.Equal(MicrosoftTestTokens.WorkTenant, identity.TenantId);
        Assert.Equal("11111111-2222-3333-4444-555555555555", identity.Subject);
        Assert.Equal("dev@example.invalid", identity.Email);
        Assert.Equal("Dev Person", identity.DisplayName);
        Assert.Equal("work", identity.AccountType);
    }

    [Fact]
    public async Task Personal_tenant_is_classified_personal_and_any_other_is_work()
    {
        var personal = await ValidateAsync(s => s.Tid = MicrosoftIdTokenValidator.PersonalTenantId);
        var work = await ValidateAsync(s => s.Tid = Guid.NewGuid().ToString());

        Assert.Equal("personal", personal.Identity!.AccountType);
        Assert.Equal(MicrosoftIdTokenValidator.PersonalTenantId, personal.Identity.TenantId);
        Assert.Equal("work", work.Identity!.AccountType);
    }

    [Fact]
    public async Task Subject_prefers_oid_and_falls_back_to_sub()
    {
        var withOid = await ValidateAsync(s => { s.Oid = "oid-value"; s.Sub = "sub-value"; });
        var withoutOid = await ValidateAsync(s => { s.Oid = null; s.Sub = "sub-value"; });
        var neither = await ValidateAsync(s => { s.Oid = null; s.Sub = null; });

        Assert.Equal("oid-value", withOid.Identity!.Subject);
        Assert.Equal("sub-value", withoutOid.Identity!.Subject);
        AssertRejected(neither, "subject_missing");
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        var result = await ValidateAsync(s =>
        {
            s.IssuedAt = DateTime.UtcNow.AddHours(-2);
            s.NotBefore = DateTime.UtcNow.AddHours(-2);
            s.Expires = DateTime.UtcNow.AddMinutes(-30);
        });

        AssertRejected(result, "expired");
    }

    [Fact]
    public async Task Token_expired_within_the_small_clock_skew_is_still_accepted()
    {
        var result = await ValidateAsync(s => { s.NotBefore = DateTime.UtcNow.AddMinutes(-60); s.Expires = DateTime.UtcNow.AddSeconds(-20); });

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Token_not_valid_yet_is_rejected()
    {
        var result = await ValidateAsync(s => s.NotBefore = DateTime.UtcNow.AddMinutes(10));

        AssertRejected(result, "not_yet_valid");
    }

    [Fact]
    public async Task Wrong_audience_is_rejected()
    {
        var result = await ValidateAsync(s => s.Audience = "00000000-0000-0000-0000-000000000001");

        AssertRejected(result, "wrong_audience");
    }

    [Fact]
    public async Task Configured_client_id_is_the_audience_that_is_required()
    {
        var other = _tokens.CreateValidator(o => o.ClientId = "00000000-0000-0000-0000-000000000001");

        var result = await other.ValidateAsync(_tokens.Create());

        AssertRejected(result, "wrong_audience");
    }

    [Fact]
    public async Task Issuer_of_a_different_tenant_than_the_tid_claim_is_rejected()
    {
        var result = await ValidateAsync(s => s.Issuer = MicrosoftIdTokenValidator.ExpectedIssuer(Guid.NewGuid().ToString()));

        AssertRejected(result, "wrong_issuer");
    }

    [Theory]
    [InlineData("https://evil.example/72f988bf-86f1-41af-91ab-2d7cd011db47/v2.0")]
    [InlineData("https://login.microsoftonline.com/72f988bf-86f1-41af-91ab-2d7cd011db47/v2.0/")]
    [InlineData("https://login.microsoftonline.com/common/v2.0")]
    [InlineData("https://sts.windows.net/72f988bf-86f1-41af-91ab-2d7cd011db47/")]
    public async Task Issuer_must_be_exactly_the_v2_issuer_for_the_tid(string issuer)
    {
        var result = await ValidateAsync(s => s.Issuer = issuer);

        AssertRejected(result, "wrong_issuer");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("common")]
    [InlineData("not-a-guid")]
    public async Task Missing_or_non_guid_tid_is_rejected(string? tid)
    {
        var result = await ValidateAsync(s =>
        {
            s.Tid = tid;
            s.Issuer = MicrosoftIdTokenValidator.ExpectedIssuer(tid ?? string.Empty);
        });

        AssertRejected(result, "tid_missing_or_not_guid");
    }

    [Fact]
    public async Task V1_token_is_rejected()
    {
        var result = await ValidateAsync(s =>
        {
            s.Ver = "1.0";
            s.Issuer = $"https://sts.windows.net/{s.Tid}/";
        });

        AssertRejected(result, "not_v2");
    }

    [Fact]
    public async Task Token_without_a_ver_claim_is_rejected()
    {
        AssertRejected(await ValidateAsync(s => s.Ver = null), "not_v2");
    }

    [Fact]
    public async Task Token_signed_with_a_different_key_is_rejected()
    {
        using var attacker = RSA.Create(2048);
        var forged = _tokens.Create(credentials: new SigningCredentials(
            new RsaSecurityKey(attacker) { KeyId = MicrosoftTestTokens.KeyId }, SecurityAlgorithms.RsaSha256));

        AssertRejected(await _validator.ValidateAsync(forged), "bad_signature");
    }

    [Fact]
    public async Task Token_with_an_unknown_key_id_is_rejected()
    {
        using var attacker = RSA.Create(2048);
        var forged = _tokens.Create(credentials: new SigningCredentials(
            new RsaSecurityKey(attacker) { KeyId = "somebody-elses-key" }, SecurityAlgorithms.RsaSha256));

        var result = await _validator.ValidateAsync(forged);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Symmetric_hs256_token_is_rejected()
    {
        // Algorithm-confusion attempt: a token "signed" with HMAC using any secret.
        var hmac = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(new string('k', 64))) { KeyId = MicrosoftTestTokens.KeyId }, SecurityAlgorithms.HmacSha256);
        var forged = _tokens.Create(credentials: hmac);

        Assert.False((await _validator.ValidateAsync(forged)).IsValid);
    }

    [Fact]
    public async Task Tampered_payload_is_rejected()
    {
        var token = _tokens.Create();
        var parts = token.Split('.');
        var payload = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(parts[1])).Replace("dev@example.invalid", "admin@example.invalid");
        var tampered = $"{parts[0]}.{Base64UrlEncoder.Encode(payload)}.{parts[2]}";

        Assert.False((await _validator.ValidateAsync(tampered)).IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("a.b.c")]
    public async Task Malformed_input_is_rejected(string? token)
    {
        AssertRejected(await _validator.ValidateAsync(token), "malformed");
    }

    [Fact]
    public async Task Oversized_token_is_rejected_without_being_parsed()
    {
        AssertRejected(await _validator.ValidateAsync(new string('a', 20_000)), "malformed");
    }

    [Fact]
    public async Task Missing_email_and_non_email_username_is_rejected()
    {
        AssertRejected(await ValidateAsync(s => { s.Email = null; s.PreferredUsername = null; }), "email_missing");
        AssertRejected(await ValidateAsync(s => { s.Email = null; s.PreferredUsername = "+27821234567"; }), "email_missing");
        AssertRejected(await ValidateAsync(s => { s.Email = null; s.PreferredUsername = "just-a-name"; }), "email_missing");
    }

    [Fact]
    public async Task Email_falls_back_to_preferred_username_only_when_it_looks_like_an_address()
    {
        var result = await ValidateAsync(s => { s.Email = null; s.PreferredUsername = "Dev.Person@Contoso.example"; });

        Assert.Equal("dev.person@contoso.example", result.Identity!.Email);
    }

    [Fact]
    public async Task Email_claim_wins_over_preferred_username()
    {
        var result = await ValidateAsync(s => { s.Email = "real@example.invalid"; s.PreferredUsername = "other@example.invalid"; });

        Assert.Equal("real@example.invalid", result.Identity!.Email);
    }

    [Fact]
    public async Task Display_name_falls_back_to_the_email_local_part()
    {
        var result = await ValidateAsync(s => { s.Name = null; s.Email = "jane.doe@example.invalid"; });

        Assert.Equal("jane.doe", result.Identity!.DisplayName);
    }

    [Fact]
    public async Task Identity_key_ignores_the_email()
    {
        var a = await ValidateAsync(s => s.Email = "one@example.invalid");
        var b = await ValidateAsync(s => s.Email = "two@example.invalid");

        Assert.Equal((a.Identity!.TenantId, a.Identity.Subject), (b.Identity!.TenantId, b.Identity.Subject));
    }

    [Fact]
    public async Task Token_older_than_the_max_age_is_rejected_unless_the_check_is_disabled()
    {
        var old = _tokens.Create(s =>
        {
            s.IssuedAt = DateTime.UtcNow.AddMinutes(-30);
            s.NotBefore = DateTime.UtcNow.AddMinutes(-30);
            s.Expires = DateTime.UtcNow.AddMinutes(30);
        });

        AssertRejected(await _validator.ValidateAsync(old), "token_too_old");
        Assert.True((await _tokens.CreateValidator(o => o.MaxIdTokenAgeMinutes = 0).ValidateAsync(old)).IsValid);
    }

    [Fact]
    public async Task Unreachable_signing_keys_are_reported_as_unavailable_not_as_a_bad_token()
    {
        var validator = new MicrosoftIdTokenValidator(
            new FootLook.Core.Options.FootLookMicrosoftOptions { ClientId = MicrosoftTestTokens.ClientId },
            null,
            new ThrowingConfigurationManager());

        var result = await validator.ValidateAsync(_tokens.Create());

        Assert.False(result.IsValid);
        Assert.True(result.Unavailable);
    }

    private sealed class ThrowingConfigurationManager : Microsoft.IdentityModel.Protocols.IConfigurationManager<Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration>
    {
        public Task<Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) =>
            throw new InvalidOperationException("metadata endpoint unreachable");

        public void RequestRefresh()
        {
        }
    }
}
