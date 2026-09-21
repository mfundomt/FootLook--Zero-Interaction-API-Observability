using FootLook.Core.Security;
using Microsoft.IdentityModel.Tokens;

namespace FootLook.Tests;

/// <summary>
/// The validator directly, to pin down WHICH check rejects each bad pass (the endpoint only ever says
/// invalid_pass), on a clock the test controls.
/// </summary>
public class CentralPassValidatorTests : IDisposable
{
    private readonly CentralTestKeys _keys = new();
    private readonly ManualTimeProvider _clock = new();
    private readonly StaticKeyProvider _provider;
    private readonly CentralPassValidator _validator;

    public CentralPassValidatorTests()
    {
        _provider = _keys.Provider();
        _validator = new CentralPassValidator(CentralTestOptions.Central(), _provider, timeProvider: _clock);
    }

    public void Dispose() => _keys.Dispose();

    private Task<PassValidationResult> Validate(Action<PassSpec>? spec = null)
    {
        // Mint relative to the manual clock so the tests can move time.
        var now = _clock.GetUtcNow().UtcDateTime;
        return _validator.ValidateAsync(_keys.Create(s =>
        {
            s.IssuedAt = now;
            s.NotBefore = now.AddSeconds(-5);
            s.Expires = now.AddMinutes(5);
            spec?.Invoke(s);
        }));
    }

    [Fact]
    public async Task A_valid_pass_yields_the_identity_in_it()
    {
        var result = await Validate(s => { s.Role = "owner"; s.Jti = "abc"; });

        Assert.True(result.IsValid);
        var pass = result.Pass!;
        Assert.Equal("0f8e6a52-3c1d-4e0b-9a67-0c5d2f1b7a11", pass.Subject);
        Assert.Equal("dev@example.invalid", pass.Email);
        Assert.Equal("Dev Person", pass.DisplayName);
        Assert.True(pass.IsOwner);
        Assert.Equal("abc", pass.JwtId);
        Assert.Equal(_clock.GetUtcNow().AddMinutes(5).ToUnixTimeSeconds(), pass.ExpiresAtUtc.ToUnixTimeSeconds());
    }

    [Theory]
    [InlineData("iss", "wrong_issuer")]
    [InlineData("aud", "wrong_audience")]
    public async Task Missing_issuer_or_audience_is_named(string claim, string reason)
    {
        var result = await Validate(s => s.Omit.Add(claim));

        Assert.Equal(reason, result.Failure);
    }

    [Fact]
    public async Task Each_check_reports_its_own_reason()
    {
        Assert.Equal("wrong_issuer", (await Validate(s => s.Issuer = "https://evil.test")).Failure);
        Assert.Equal("wrong_audience", (await Validate(s => s.Audience = "prj_other")).Failure);
        Assert.Equal("unknown_signing_key", (await Validate(s => s.KeyId = "nope")).Failure);
        Assert.Equal("subject_missing", (await Validate(s => s.Omit.Add("sub"))).Failure);
        Assert.Equal("email_missing", (await Validate(s => s.Omit.Add("email"))).Failure);
        Assert.Equal("role_invalid", (await Validate(s => s.Role = "admin")).Failure);
        Assert.Equal("jti_missing", (await Validate(s => s.Omit.Add("jti"))).Failure);
        Assert.Equal("time_claims_missing", (await Validate(s => s.Omit.Add("nbf"))).Failure);
        Assert.Equal("lifetime_invalid", (await Validate(s => s.Expires = _clock.GetUtcNow().UtcDateTime.AddHours(1))).Failure);
    }

    [Fact]
    public async Task A_bad_signature_is_named_and_a_swapped_key_does_not_help()
    {
        using var attacker = new CentralTestKeys();

        var result = await _validator.ValidateAsync(attacker.Create());

        Assert.Equal("bad_signature", result.Failure);
    }

    [Fact]
    public async Task Algorithms_other_than_rs256_are_refused_before_any_key_is_used()
    {
        var lookupsBefore = _provider.Lookups;
        var rs384 = _keys.Create(credentials: new SigningCredentials(_keys.SigningKey, SecurityAlgorithms.RsaSha384));

        Assert.Equal("bad_algorithm", (await _validator.ValidateAsync(_keys.CreateUnsigned())).Failure);
        Assert.Equal("bad_algorithm", (await _validator.ValidateAsync(_keys.CreateHs256WithPublicKeyAsSecret())).Failure);
        Assert.Equal("bad_algorithm", (await _validator.ValidateAsync(rs384)).Failure);
        Assert.Equal(lookupsBefore, _provider.Lookups);
    }

    [Fact]
    public async Task Time_checks_use_the_clock_and_the_skew()
    {
        var minted = _keys.Create(s =>
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            s.IssuedAt = now;
            s.NotBefore = now;
            s.Expires = now.AddMinutes(5);
        });

        Assert.True((await _validator.ValidateAsync(minted)).IsValid);

        _clock.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(59)));
        Assert.True((await _validator.ValidateAsync(minted)).IsValid); // inside the 60 s skew

        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("expired", (await _validator.ValidateAsync(minted)).Failure);
    }

    [Fact]
    public async Task Not_yet_valid_and_issued_in_the_future_are_named()
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        Assert.Equal("not_yet_valid", (await Validate(s => s.NotBefore = now.AddMinutes(2))).Failure);
        Assert.Equal("iat_in_future", (await Validate(s => s.IssuedAt = now.AddMinutes(2))).Failure);
    }

    [Fact]
    public async Task Keys_unavailable_is_not_the_same_as_a_bad_pass()
    {
        _provider.Unavailable = true;

        var result = await Validate();

        Assert.True(result.Unavailable);
        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("a.b")]
    [InlineData("a.b.c.d")]
    public async Task Junk_is_malformed_and_never_reaches_the_key_provider(string? junk)
    {
        var result = await _validator.ValidateAsync(junk);

        Assert.Equal("malformed", result.Failure);
        Assert.Equal(0, _provider.Lookups);
    }

    [Fact]
    public async Task Is_off_when_no_project_id_is_configured()
    {
        var off = new CentralPassValidator(new FootLook.Core.Options.FootLookCentralOptions(), _provider, timeProvider: _clock);

        Assert.Equal("central_mode_off", (await off.ValidateAsync(_keys.Create())).Failure);
    }
}
