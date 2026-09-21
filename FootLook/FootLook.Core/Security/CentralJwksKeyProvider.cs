using System.Security.Cryptography;
using System.Text.Json;
using FootLook.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace FootLook.Core.Security
{
    /// <summary>
    /// Result of looking up one of the central service's public signing keys.
    /// </summary>
    /// <param name="Key">The public key with the requested id, or null when there is none.</param>
    /// <param name="Unavailable">
    /// True only when no key set could be loaded at all (the service is unreachable and nothing is
    /// cached), as opposed to the key id simply being unknown.
    /// </param>
    public readonly record struct CentralKeyLookup(SecurityKey? Key, bool Unavailable = false);

    /// <summary>
    /// Supplies the central service's PUBLIC signing keys to <see cref="IPassValidator"/>. Replace it
    /// (register your own before AddFootLook) to supply keys another way, e.g. from a test.
    /// </summary>
    public interface ICentralKeyProvider
    {
        Task<CentralKeyLookup> GetKeyAsync(string keyId, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Downloads the central service's public keys (a JWKS document) and caches them.
    /// <list type="bullet">
    /// <item>The key set is cached for about an hour (<see cref="DefaultCacheLifetime"/>).</item>
    /// <item>A key id that is not in the cache triggers ONE refresh (central may have rotated its key),
    /// but never more often than <see cref="DefaultRefreshCooldown"/> - so a forged key id in a stream
    /// of junk passes cannot make this host hammer the service. The cooldown counts from the last
    /// attempt, successful or not.</item>
    /// <item>If a refresh fails, the last good keys keep being served (past their nominal lifetime)
    /// until a later refresh succeeds. Only when nothing has ever been loaded is the result
    /// <see cref="CentralKeyLookup.Unavailable"/>; a failed first load is retried after a short pause
    /// (<see cref="DefaultFirstLoadRetry"/>).</item>
    /// </list>
    /// Only RSA keys of at least 2048 bits are accepted from the document, and the document itself
    /// must be small, over https (http only for localhost), and is fetched without following redirects.
    /// The keys are public, so nothing secret is sent or stored.
    /// </summary>
    public sealed class CentralJwksKeyProvider : ICentralKeyProvider
    {
        public static readonly TimeSpan DefaultCacheLifetime = TimeSpan.FromHours(1);
        public static readonly TimeSpan DefaultRefreshCooldown = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan DefaultFirstLoadRetry = TimeSpan.FromSeconds(5);

        private const int MaxDocumentBytes = 256 * 1024;
        private const int MaxKeys = 20;
        private const int MinModulusBits = 2048;
        private const int MaxKeyIdLength = 128;
        private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);

        private sealed record Snapshot(IReadOnlyDictionary<string, SecurityKey> Keys, DateTimeOffset FetchedAtUtc);

        private readonly FootLookCentralOptions _options;
        private readonly HttpClient _http;
        private readonly ILogger? _logger;
        private readonly TimeProvider _time;
        private readonly TimeSpan _cacheLifetime;
        private readonly TimeSpan _refreshCooldown;
        private readonly TimeSpan _firstLoadRetry;
        private readonly SemaphoreSlim _gate = new(1, 1);

        private volatile Snapshot? _snapshot;
        private DateTimeOffset? _lastAttemptUtc;

        public CentralJwksKeyProvider(
            FootLookCentralOptions options,
            HttpClient http,
            ILogger<CentralJwksKeyProvider>? logger = null,
            TimeProvider? timeProvider = null,
            TimeSpan? cacheLifetime = null,
            TimeSpan? refreshCooldown = null,
            TimeSpan? firstLoadRetry = null)
        {
            _options = options;
            _http = http;
            _logger = logger;
            _time = timeProvider ?? TimeProvider.System;
            _cacheLifetime = cacheLifetime ?? DefaultCacheLifetime;
            _refreshCooldown = refreshCooldown ?? DefaultRefreshCooldown;
            _firstLoadRetry = firstLoadRetry ?? DefaultFirstLoadRetry;
        }

        public async Task<CentralKeyLookup> GetKeyAsync(string keyId, CancellationToken cancellationToken = default)
        {
            var snapshot = _snapshot;
            if (snapshot is not null && IsFresh(snapshot, _time.GetUtcNow()) && snapshot.Keys.TryGetValue(keyId, out var cached))
            {
                return new CentralKeyLookup(cached);
            }

            await _gate.WaitAsync(cancellationToken);
            try
            {
                var now = _time.GetUtcNow();
                snapshot = _snapshot;
                if (snapshot is not null && IsFresh(snapshot, now) && snapshot.Keys.TryGetValue(keyId, out var refreshedMeanwhile))
                {
                    return new CentralKeyLookup(refreshedMeanwhile);
                }

                // Reached when the cache is empty, past its lifetime, or does not know this key id.
                var pause = snapshot is null ? _firstLoadRetry : _refreshCooldown;
                if (_lastAttemptUtc is null || now - _lastAttemptUtc.Value >= pause)
                {
                    _lastAttemptUtc = now;
                    var loaded = await TryLoadAsync(cancellationToken);
                    if (loaded is not null)
                    {
                        _snapshot = loaded;
                    }

                    snapshot = _snapshot;
                }
            }
            finally
            {
                _gate.Release();
            }

            if (snapshot is null)
            {
                return new CentralKeyLookup(null, Unavailable: true);
            }

            return snapshot.Keys.TryGetValue(keyId, out var key) ? new CentralKeyLookup(key) : new CentralKeyLookup(null);
        }

        private bool IsFresh(Snapshot snapshot, DateTimeOffset now) => now - snapshot.FetchedAtUtc < _cacheLifetime;

        private async Task<Snapshot?> TryLoadAsync(CancellationToken cancellationToken)
        {
            var url = _options.JwksUrl;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            {
                _logger?.LogWarning("FootLook central sign-in: the JWKS address must be an absolute https URL (http only for localhost): {Url}", url);
                return null;
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(FetchTimeout);

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Accept.ParseAdd("application/json");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    _logger?.LogWarning("FootLook central sign-in: the key set at {Url} answered {Status}.", url, (int)response.StatusCode);
                    return null;
                }

                var body = await ReadBoundedAsync(response, timeout.Token);
                if (body is null)
                {
                    _logger?.LogWarning("FootLook central sign-in: the key set at {Url} is larger than {Max} bytes.", url, MaxDocumentBytes);
                    return null;
                }

                var keys = ParseKeys(body);
                if (keys.Count == 0)
                {
                    _logger?.LogWarning("FootLook central sign-in: the key set at {Url} holds no usable RSA signing key.", url);
                    return null;
                }

                return new Snapshot(keys, _time.GetUtcNow());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or JsonException or FormatException)
            {
                _logger?.LogWarning(ex, "FootLook central sign-in: could not load the key set from {Url}.", url);
                return null;
            }
        }

        private static async Task<byte[]?> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            if (response.Content.Headers.ContentLength is > MaxDocumentBytes)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaxDocumentBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }

        private static Dictionary<string, SecurityKey> ParseKeys(byte[] json)
        {
            var keys = new Dictionary<string, SecurityKey>(StringComparer.Ordinal);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("keys", out var list) ||
                list.ValueKind != JsonValueKind.Array)
            {
                return keys;
            }

            foreach (var entry in list.EnumerateArray())
            {
                if (keys.Count >= MaxKeys)
                {
                    break;
                }

                if (entry.ValueKind != JsonValueKind.Object ||
                    !string.Equals(Text(entry, "kty"), "RSA", StringComparison.Ordinal))
                {
                    continue;
                }

                // A key marked for another use or algorithm is not ours to trust for RS256 passes.
                var use = Text(entry, "use");
                var alg = Text(entry, "alg");
                if ((use is not null && use != "sig") || (alg is not null && alg != SecurityAlgorithms.RsaSha256))
                {
                    continue;
                }

                var kid = Text(entry, "kid");
                var n = Text(entry, "n");
                var e = Text(entry, "e");
                if (string.IsNullOrEmpty(kid) || kid.Length > MaxKeyIdLength || string.IsNullOrEmpty(n) || string.IsNullOrEmpty(e))
                {
                    continue;
                }

                try
                {
                    var parameters = new RSAParameters
                    {
                        Modulus = Base64UrlEncoder.DecodeBytes(n),
                        Exponent = Base64UrlEncoder.DecodeBytes(e),
                    };

                    // Strip leading zero bytes before measuring: some encoders pad the modulus.
                    var significant = parameters.Modulus.SkipWhile(b => b == 0).Count();
                    if (significant * 8 < MinModulusBits)
                    {
                        continue;
                    }

                    keys[kid] = new RsaSecurityKey(parameters) { KeyId = kid };
                }
                catch (FormatException)
                {
                    // A malformed entry is skipped; the others may still be fine.
                }
            }

            return keys;
        }

        private static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
