using FootLook.Core.Models;

namespace FootLook.Core.Services
{
    public sealed class CaptureIdentityResolver
    {
        public IdentityResolutionResult Resolve(CapturedRequest capture, string sessionId, string tabId, string? expectedSiteHost = null)
        {
            var normalizedSessionId = Normalize(sessionId);
            var normalizedTabId = Normalize(tabId);

            var score = 0.0;
            var signals = new List<string>();

            var sessionHeader = Normalize(GetHeaderValue(capture.Headers, "x-footlook-session-id"));
            var tabHeader = Normalize(GetHeaderValue(capture.Headers, "x-footlook-tab-id"));

            score += ScoreEqualitySignal(sessionHeader, normalizedSessionId, 0.55, -0.40, "Header sessionId", signals);
            score += ScoreEqualitySignal(tabHeader, normalizedTabId, 0.35, -0.25, "Header tabId", signals);

            var referer = GetHeaderValue(capture.Headers, "referer");
            if (string.IsNullOrWhiteSpace(referer))
            {
                referer = GetHeaderValue(capture.Headers, "referrer");
            }

            if (!string.IsNullOrWhiteSpace(referer))
            {
                try
                {
                    var refererUrl = new Uri(referer, UriKind.Absolute);
                    var refererSessionId = Normalize(GetQueryValue(refererUrl.Query, "footlookSessionId"));
                    var refererTabId = Normalize(GetQueryValue(refererUrl.Query, "footlookTabId"));

                    score += ScoreEqualitySignal(refererSessionId, normalizedSessionId, 0.25, -0.18, "Referer sessionId", signals);
                    score += ScoreEqualitySignal(refererTabId, normalizedTabId, 0.20, -0.12, "Referer tabId", signals);

                    if (!string.IsNullOrWhiteSpace(expectedSiteHost))
                    {
                        var normalizedHost = Normalize(expectedSiteHost);
                        var refererHost = Normalize(refererUrl.Host);
                        if (refererHost == normalizedHost)
                        {
                            score += 0.05;
                            signals.Add("Referer host matched expected site");
                        }
                        else
                        {
                            score -= 0.05;
                            signals.Add("Referer host mismatched expected site");
                        }
                    }
                }
                catch
                {
                    signals.Add("Referer present but unparsable");
                }
            }

            if (!string.IsNullOrWhiteSpace(capture.TraceId))
            {
                score += 0.03;
                signals.Add("Trace context present");
            }

            var confidence = Math.Clamp(score, 0.0, 1.0);
            return new IdentityResolutionResult(confidence, signals);
        }

        private static double ScoreEqualitySignal(
            string left,
            string right,
            double onMatch,
            double onMismatch,
            string signalName,
            List<string> signals)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return 0.0;
            }

            if (left == right)
            {
                signals.Add(signalName + " matched");
                return onMatch;
            }

            signals.Add(signalName + " mismatched");
            return onMismatch;
        }

        private static string Normalize(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToLowerInvariant();
        }

        private static string GetHeaderValue(Dictionary<string, string> headers, string headerName)
        {
            if (headers.Count == 0)
            {
                return string.Empty;
            }

            var match = headers.FirstOrDefault(h =>
                string.Equals(h.Key, headerName, StringComparison.OrdinalIgnoreCase));

            return match.Equals(default(KeyValuePair<string, string>))
                ? string.Empty
                : match.Value;
        }

        private static string GetQueryValue(string query, string key)
        {
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            var trimmed = query.StartsWith('?') ? query[1..] : query;
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return string.Empty;
            }

            var pairs = trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var pair in pairs)
            {
                var separatorIndex = pair.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                var currentKey = Uri.UnescapeDataString(pair[..separatorIndex]);
                if (!string.Equals(currentKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var currentValue = pair[(separatorIndex + 1)..];
                return Uri.UnescapeDataString(currentValue);
            }

            return string.Empty;
        }
    }

    public sealed record IdentityResolutionResult(double Confidence, IReadOnlyList<string> Signals);
}
