using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FootLook.Core.Options;
using FootLook.Core.Security;
using Microsoft.IdentityModel.Tokens;

namespace FootLook.Tests;

public class CentralJwksKeyProviderTests : IDisposable
{
    private readonly CentralTestKeys _current = new("key-1");
    private readonly CentralTestKeys _next = new("key-2");
    private readonly FakeJwksHandler _handler = new();
    private readonly ManualTimeProvider _clock = new();

    public CentralJwksKeyProviderTests()
    {
        _handler.Body = _current.JwksJson();
    }

    public void Dispose()
    {
        _current.Dispose();
        _next.Dispose();
    }

    private CentralJwksKeyProvider Create(FootLookCentralOptions? options = null) =>
        new(options ?? CentralTestOptions.Central(), new HttpClient(_handler), timeProvider: _clock);

    [Fact]
    public async Task Downloads_the_key_set_once_and_serves_it_from_the_cache()
    {
        var provider = Create();

        Assert.NotNull((await provider.GetKeyAsync("key-1")).Key);
        Assert.NotNull((await provider.GetKeyAsync("key-1")).Key);
        _clock.Advance(TimeSpan.FromMinutes(59));
        Assert.NotNull((await provider.GetKeyAsync("key-1")).Key);

        Assert.Equal(1, _handler.Requests);
        Assert.Equal("https://central.test/.well-known/jwks.json", _handler.LastUri!.ToString());
    }

    [Fact]
    public async Task Refreshes_after_the_cache_lifetime()
    {
        var provider = Create();
        await provider.GetKeyAsync("key-1");

        _clock.Advance(TimeSpan.FromMinutes(61));
        await provider.GetKeyAsync("key-1");

        Assert.Equal(2, _handler.Requests);
    }

    [Fact]
    public async Task An_unknown_key_id_refreshes_once_after_the_cooldown_and_finds_a_rotated_key()
    {
        var provider = Create();
        await provider.GetKeyAsync("key-1");
        _handler.Body = _next.JwksJson(_current); // central rotated: key-2 is new, key-1 is still published
        _clock.Advance(TimeSpan.FromMinutes(10));

        var lookup = await provider.GetKeyAsync("key-2");

        Assert.NotNull(lookup.Key);
        Assert.Equal("key-2", lookup.Key!.KeyId);
        Assert.Equal(2, _handler.Requests);
        // The old key is still served without another download.
        Assert.NotNull((await provider.GetKeyAsync("key-1")).Key);
        Assert.Equal(2, _handler.Requests);
    }

    [Fact]
    public async Task A_forged_key_id_cannot_make_the_host_hammer_central()
    {
        var provider = Create();
        await provider.GetKeyAsync("key-1");
        Assert.Equal(1, _handler.Requests);

        // A burst of junk passes with made-up key ids, all inside the cooldown of the first download.
        for (var i = 0; i < 50; i++)
        {
            var lookup = await provider.GetKeyAsync("forged-" + i);
            Assert.Null(lookup.Key);
            Assert.False(lookup.Unavailable);
        }

        Assert.Equal(1, _handler.Requests);

        // After the cooldown one (and only one) more refresh is allowed, however many forged ids follow.
        _clock.Advance(TimeSpan.FromSeconds(31));
        for (var i = 0; i < 50; i++)
        {
            await provider.GetKeyAsync("forged-again-" + i);
        }

        Assert.Equal(2, _handler.Requests);
    }

    [Fact]
    public async Task Keeps_serving_the_last_good_keys_when_central_goes_away()
    {
        var provider = Create();
        await provider.GetKeyAsync("key-1");

        _handler.Unreachable = true;
        _clock.Advance(TimeSpan.FromHours(3)); // well past the nominal lifetime

        var lookup = await provider.GetKeyAsync("key-1");
        Assert.NotNull(lookup.Key);
        Assert.False(lookup.Unavailable);
        Assert.Equal(2, _handler.Requests); // it tried, failed, and kept the old keys

        // No hammering while it is down: within the cooldown there is no new attempt.
        await provider.GetKeyAsync("key-1");
        Assert.Equal(2, _handler.Requests);

        // And it recovers by itself once central is back and the cooldown has passed.
        _handler.Unreachable = false;
        _handler.Body = _next.JwksJson();
        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.NotNull((await provider.GetKeyAsync("key-2")).Key);
    }

    [Fact]
    public async Task With_nothing_cached_an_unreachable_central_is_reported_unavailable_and_retried_shortly()
    {
        _handler.Unreachable = true;
        var provider = Create();

        var first = await provider.GetKeyAsync("key-1");
        Assert.True(first.Unavailable);
        Assert.Null(first.Key);
        Assert.Equal(1, _handler.Requests);

        // Immediately again: still unavailable, but without another download.
        Assert.True((await provider.GetKeyAsync("key-1")).Unavailable);
        Assert.Equal(1, _handler.Requests);

        _handler.Unreachable = false;
        _clock.Advance(TimeSpan.FromSeconds(6));
        Assert.NotNull((await provider.GetKeyAsync("key-1")).Key);
        Assert.Equal(2, _handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task A_non_success_answer_is_unavailable(HttpStatusCode status)
    {
        _handler.Status = status;

        Assert.True((await Create().GetKeyAsync("key-1")).Unavailable);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"keys\":[]}")]
    [InlineData("{\"keys\":[{\"kty\":\"oct\",\"kid\":\"key-1\",\"k\":\"AAAA\"}]}")]
    public async Task A_document_without_a_usable_rsa_key_is_unavailable(string body)
    {
        _handler.Body = body;

        Assert.True((await Create().GetKeyAsync("key-1")).Unavailable);
    }

    [Fact]
    public async Task Ignores_keys_that_are_too_weak_or_not_for_rs256_signatures()
    {
        using var weak = RSA.Create(1024);
        var weakParams = weak.ExportParameters(false);
        var doc = JsonSerializer.Serialize(new
        {
            keys = new object[]
            {
                new { kty = "RSA", kid = "weak", use = "sig", alg = "RS256", n = Base64UrlEncoder.Encode(weakParams.Modulus!), e = Base64UrlEncoder.Encode(weakParams.Exponent!) },
                _next.JwkEntry(alg: "RS512"),
                new { kty = "RSA", kid = "enc-key", use = "enc", alg = "RS256", n = "AQAB", e = "AQAB" },
                _current.JwkEntry(),
            }
        });
        _handler.Body = doc;
        var provider = Create();

        Assert.NotNull((await provider.GetKeyAsync("key-1")).Key);
        Assert.Null((await provider.GetKeyAsync("weak")).Key);
        Assert.Null((await provider.GetKeyAsync("key-2")).Key);
        Assert.Null((await provider.GetKeyAsync("enc-key")).Key);
    }

    [Fact]
    public async Task Refuses_a_plain_http_address_that_is_not_localhost_without_calling_it()
    {
        var provider = Create(CentralTestOptions.Central("http://central.example.invalid/.well-known/jwks.json"));

        Assert.True((await provider.GetKeyAsync("key-1")).Unavailable);
        Assert.Equal(0, _handler.Requests);
    }

    [Fact]
    public async Task Accepts_plain_http_on_localhost_for_development()
    {
        var provider = Create(CentralTestOptions.Central("http://localhost:5200/.well-known/jwks.json"));

        Assert.NotNull((await provider.GetKeyAsync("key-1")).Key);
        Assert.Equal(1, _handler.Requests);
    }

    [Fact]
    public async Task Refuses_an_oversized_document()
    {
        _handler.Body = "{\"keys\":[],\"padding\":\"" + new string('x', 300 * 1024) + "\"}";

        Assert.True((await Create().GetKeyAsync("key-1")).Unavailable);
    }

    [Fact]
    public async Task The_default_jwks_address_follows_the_issuer()
    {
        var options = new FootLookCentralOptions { ProjectId = "prj_x", Issuer = "https://central.test/" };
        var provider = new CentralJwksKeyProvider(options, new HttpClient(_handler), timeProvider: _clock);

        await provider.GetKeyAsync("key-1");

        Assert.Equal("https://central.test/.well-known/jwks.json", _handler.LastUri!.ToString());
    }
}

public class PassReplayCacheTests
{
    private readonly ManualTimeProvider _clock = new();

    [Fact]
    public void A_pass_id_works_once_until_it_is_pruned()
    {
        var cache = new PassReplayCache(_clock);
        var until = _clock.GetUtcNow().AddMinutes(6);

        Assert.True(cache.TryRegister("jti-1", until));
        Assert.False(cache.TryRegister("jti-1", until));
        Assert.True(cache.TryRegister("jti-2", until));

        // Remembered until the pass could no longer be accepted; after that the entry is pruned.
        _clock.Advance(TimeSpan.FromMinutes(7));
        Assert.True(cache.TryRegister("jti-3", _clock.GetUtcNow().AddMinutes(6)));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Stays_bounded_and_refuses_new_ids_while_full_of_live_ones()
    {
        var cache = new PassReplayCache(_clock, maxEntries: 3);
        var until = _clock.GetUtcNow().AddMinutes(6);

        Assert.True(cache.TryRegister("a", until));
        Assert.True(cache.TryRegister("b", until));
        Assert.True(cache.TryRegister("c", until));
        Assert.False(cache.TryRegister("d", until));
        Assert.Equal(3, cache.Count);

        // Room is made by expiry, not by forgetting a live id.
        _clock.Advance(TimeSpan.FromMinutes(7));
        Assert.True(cache.TryRegister("d", _clock.GetUtcNow().AddMinutes(6)));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task Concurrent_use_of_one_id_succeeds_exactly_once()
    {
        var cache = new PassReplayCache(_clock);
        var until = _clock.GetUtcNow().AddMinutes(6);

        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => cache.TryRegister("same", until))));

        Assert.Equal(1, results.Count(r => r));
    }
}
