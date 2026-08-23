using FootLook.Core.Options;

namespace FootLook.Core.Services
{
    public sealed class FootLookDeveloperExperienceService
    {
        public DeveloperDiagnosticsReport RunDiagnostics(FootLookOptions options)
        {
            var checks = new List<DeveloperDiagnosticCheck>();

            checks.Add(Check(
                "endpoint-base-path",
                options.EndpointBasePath.StartsWith('/'),
                $"EndpointBasePath='{options.EndpointBasePath}'",
                "EndpointBasePath should start with '/'."));

            checks.Add(Check(
                "queue-capacity",
                options.QueCapacity >= 1000,
                $"QueCapacity={options.QueCapacity}",
                "Queue capacity should be at least 1000 for reliable burst handling."));

            checks.Add(Check(
                "sampling-rate",
                options.SamplingRate > 0 && options.SamplingRate <= 1.0,
                $"SamplingRate={options.SamplingRate}",
                "SamplingRate must be within (0, 1]."));

            checks.Add(Check(
                "ignored-paths-recursion-guard",
                options.IgnoredPaths.Any(x => string.Equals(x, options.EndpointBasePath, StringComparison.OrdinalIgnoreCase)) ||
                options.IgnoredPaths.Any(x => string.Equals(x, options.EndpointBasePath + "/", StringComparison.OrdinalIgnoreCase)),
                $"IgnoredPaths count={options.IgnoredPaths.Count}",
                "IgnoredPaths should include EndpointBasePath to avoid FootLook observing its own endpoints."));

            checks.Add(Check(
                "retention-days",
                options.RetentionDays > 0,
                $"RetentionDays={options.RetentionDays}",
                "RetentionDays should be > 0."));

            checks.Add(Check(
                "privacy-sensitive-headers",
                options.SensitiveHeaders.Any(h => string.Equals(h, "Authorization", StringComparison.OrdinalIgnoreCase)) &&
                options.SensitiveHeaders.Any(h => string.Equals(h, "Cookie", StringComparison.OrdinalIgnoreCase)),
                $"SensitiveHeaders count={options.SensitiveHeaders.Count}",
                "SensitiveHeaders should include Authorization and Cookie."));

            checks.Add(Check(
                "slo-thresholds",
                options.SloMaxAverageIngestLatencyMs > 0 &&
                options.SloMaxP95IngestLatencyMs > 0 &&
                options.SloMaxEventLossRatePercent >= 0 &&
                options.SloMaxDashboardFreshnessSeconds > 0,
                $"SLOs: avg={options.SloMaxAverageIngestLatencyMs}, p95={options.SloMaxP95IngestLatencyMs}, loss={options.SloMaxEventLossRatePercent}, fresh={options.SloMaxDashboardFreshnessSeconds}",
                "SLO thresholds must be positive and meaningful."));

            var passed = checks.Count(x => x.Passed);
            var status = passed == checks.Count ? "healthy" : passed >= checks.Count - 2 ? "warning" : "critical";

            return new DeveloperDiagnosticsReport(status, checks);
        }

        public DeveloperSelfHealResult ApplySelfHealing(FootLookOptions options)
        {
            var fixes = new List<string>();

            if (!options.EndpointBasePath.StartsWith('/'))
            {
                options.EndpointBasePath = "/" + options.EndpointBasePath.TrimStart('/');
                fixes.Add("Normalized EndpointBasePath to start with '/'.");
            }

            if (options.QueCapacity < 1000)
            {
                options.QueCapacity = 10000;
                fixes.Add("Increased QueCapacity to 10000.");
            }

            if (options.SamplingRate <= 0 || options.SamplingRate > 1)
            {
                options.SamplingRate = 1.0;
                fixes.Add("Reset SamplingRate to 1.0.");
            }

            if (!options.IgnoredPaths.Any(x => string.Equals(x, options.EndpointBasePath, StringComparison.OrdinalIgnoreCase)) &&
                !options.IgnoredPaths.Any(x => string.Equals(x, options.EndpointBasePath + "/", StringComparison.OrdinalIgnoreCase)))
            {
                options.IgnoredPaths.Add(options.EndpointBasePath);
                fixes.Add($"Added '{options.EndpointBasePath}' to IgnoredPaths.");
            }

            if (options.RetentionDays <= 0)
            {
                options.RetentionDays = 30;
                fixes.Add("Reset RetentionDays to 30.");
            }

            EnsureValue(options.SensitiveHeaders, "Authorization", fixes, "Added 'Authorization' to SensitiveHeaders.");
            EnsureValue(options.SensitiveHeaders, "Cookie", fixes, "Added 'Cookie' to SensitiveHeaders.");

            if (options.SloMaxAverageIngestLatencyMs <= 0)
            {
                options.SloMaxAverageIngestLatencyMs = 1000;
                fixes.Add("Reset SloMaxAverageIngestLatencyMs to 1000.");
            }

            if (options.SloMaxP95IngestLatencyMs <= 0)
            {
                options.SloMaxP95IngestLatencyMs = 2500;
                fixes.Add("Reset SloMaxP95IngestLatencyMs to 2500.");
            }

            if (options.SloMaxEventLossRatePercent < 0)
            {
                options.SloMaxEventLossRatePercent = 1.0;
                fixes.Add("Reset SloMaxEventLossRatePercent to 1.0.");
            }

            if (options.SloMaxDashboardFreshnessSeconds <= 0)
            {
                options.SloMaxDashboardFreshnessSeconds = 30;
                fixes.Add("Reset SloMaxDashboardFreshnessSeconds to 30.");
            }

            var diagnostics = RunDiagnostics(options);
            return new DeveloperSelfHealResult(fixes, diagnostics);
        }

        public DeveloperSelfHealResult ApplySetupProfile(FootLookOptions options, string profile)
        {
            var normalized = (profile ?? string.Empty).Trim().ToLowerInvariant();
            var fixes = new List<string>();

            switch (normalized)
            {
                case "staging":
                    options.SamplingRate = 0.5;
                    options.MaxBodyLength = 512 * 1024;
                    options.EnablePiiMasking = true;
                    options.EnablePrivacyAudit = true;
                    options.SloMaxAverageIngestLatencyMs = 800;
                    options.SloMaxP95IngestLatencyMs = 2000;
                    fixes.Add("Applied staging profile.");
                    break;
                case "production":
                case "prod":
                    options.SamplingRate = 0.2;
                    options.MaxBodyLength = 256 * 1024;
                    options.EnablePiiMasking = true;
                    options.EnablePrivacyAudit = true;
                    options.SloMaxAverageIngestLatencyMs = 500;
                    options.SloMaxP95IngestLatencyMs = 1500;
                    fixes.Add("Applied production profile.");
                    break;
                default:
                    options.SamplingRate = 1.0;
                    options.MaxBodyLength = 1024 * 1024;
                    options.EnablePiiMasking = true;
                    options.EnablePrivacyAudit = true;
                    options.SloMaxAverageIngestLatencyMs = 1000;
                    options.SloMaxP95IngestLatencyMs = 2500;
                    fixes.Add("Applied development profile.");
                    break;
            }

            var healed = ApplySelfHealing(options);
            fixes.AddRange(healed.AppliedFixes);
            return new DeveloperSelfHealResult(fixes, healed.Diagnostics);
        }

        private static DeveloperDiagnosticCheck Check(string key, bool passed, string observed, string recommendation)
        {
            return new DeveloperDiagnosticCheck(key, passed, observed, recommendation);
        }

        private static void EnsureValue(List<string> list, string value, List<string> fixes, string fixMessage)
        {
            if (list.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            list.Add(value);
            fixes.Add(fixMessage);
        }
    }

    public sealed record DeveloperDiagnosticCheck(string Key, bool Passed, string Observed, string Recommendation);

    public sealed record DeveloperDiagnosticsReport(string Status, IReadOnlyList<DeveloperDiagnosticCheck> Checks);

    public sealed record DeveloperSelfHealResult(IReadOnlyList<string> AppliedFixes, DeveloperDiagnosticsReport Diagnostics);
}
