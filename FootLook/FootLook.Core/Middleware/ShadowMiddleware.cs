using FootLook.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using System.Text;
using FootLook.Core.Models;
using FootLook.Core.Options;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Components.Web;
using FootLook.Core.Services;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Collections.Concurrent;

#region FootLook Middleware Flow
//The flow of the middleware can be visualized as follows:
//HTTP request arrives
//↓
//FootLook reads the request body
//↓
//FootLook resets request.Body.Position = 0
//↓
//Controller/endpoint can still read the request body
//↓
//Endpoint/service executes
//↓
//Endpoint writes response into FootLook's temporary MemoryStream
//↓
//FootLook reads that response body
//↓
//FootLook resets the MemoryStream position
//↓
//FootLook copies the response back to the original response stream
//↓
//Client receives the response
#endregion

namespace FootLook.Core.Middleware
{
    public class ShadowMiddleware
    {
        private readonly RequestDelegate _next;
        //private readonly IShadowSink _sink;
        private readonly IShadowQueue _queue;
        private readonly FootLookOptions _options;
        private readonly ILogger<ShadowMiddleware> _logger;
        private readonly CaptureRuntimeState _captureRuntimeState;
        private readonly PrivacyAuditStore _privacyAuditStore;

        // ShadowMiddleware is constructed once for the app's lifetime (standard
        // UseMiddleware<T> convention), so these compiled regexes are built once per
        // distinct field name the app ever sees and reused for every subsequent request,
        // instead of being rebuilt from scratch on every single capture.
        private readonly ConcurrentDictionary<string, (Regex Json, Regex Query)> _sensitiveFieldRegexCache = new(StringComparer.Ordinal);

        public ShadowMiddleware(RequestDelegate next, IShadowQueue queue, FootLookOptions options, ILogger<ShadowMiddleware> logger, CaptureRuntimeState captureRuntimeState, PrivacyAuditStore privacyAuditStore)
        {
            _next = next;
            _queue = queue;
            _options = options;
            _logger = logger;
            _captureRuntimeState = captureRuntimeState;
            _privacyAuditStore = privacyAuditStore;
        }

        //public async Task InvokeAsync(HttpContext context)
        //{
        //    //ignore certain paths if specified in the options to avoid logging sensitive information or to reduce noise in the logs.
        //    if(ShouldIgnorePath(context))
        //    {
        //        await _next(context);
        //        return;
        //    }

        //    //Add duration tracking to measure how long the request takes to process. This can be useful for performance monitoring and identifying slow endpoints.
        //    var stopwatch = Stopwatch.StartNew();

        //    #region Request Body Capture 
        //    ////this allows for reading the request body without breaking the ASP.NET pipeline
        //    //context.Request.EnableBuffering();

        //    //using var reader = new StreamReader(
        //    //    context.Request.Body,
        //    //    encoding: Encoding.UTF8,
        //    //    leaveOpen: true);

        //    ////the ReadtoEndAsync method reads the entire request body as a string and returns it.
        //    ////This allows you to capture the content of the request for logging, analysis, or other purposes.
        //    //var requestBody = await reader.ReadToEndAsync();

        //    //Console.WriteLine(requestBody);

        //    ////reset the position of the request body stream to the beginning so that it can be read again
        //    ////by the next middleware or endpoint in the pipeline.
        //    //context.Request.Body.Position = 0;
        //    #endregion

        //    string requestBody = string.Empty;

        //    if (_options.CaptureRequestBody)
        //    {
        //        context.Request.EnableBuffering();

        //        using var reader = new StreamReader(
        //            context.Request.Body,
        //            encoding: Encoding.UTF8,
        //            leaveOpen: true);
        //        requestBody = await reader.ReadToEndAsync();

        //        context.Request.Body.Position = 0;
        //    }

        //    // Store the original response body stream to restore it later.
        //    var originalResponseBody = context.Response.Body;

        //    // Create a new memory stream to capture the response body.
        //    using var responseBodyCopy = new MemoryStream();

        //    try
        //    {

        //        // Temporarily replace the response body stream with our copy to capture the response.
        //         context.Response.Body = responseBodyCopy;
        //        string? exceptionMessage = null;

        //        try
        //        {
        //            await _next(context);
        //        }
        //        catch (Exception ex)
        //        {
        //            exceptionMessage = ex.Message;

        //            context.Response.StatusCode = 500;

        //            throw;
        //        }

        //        stopwatch.Stop();

        //        string responseBody = string.Empty;

        //        if (_options.CaptureResponseBody)
        //        {
        //            responseBodyCopy.Position = 0;
        //            responseBody = await new StreamReader(responseBodyCopy).ReadToEndAsync();
        //            responseBodyCopy.Position = 0;
        //        }

        //        Console.WriteLine("REQUEST BODY:");
        //        Console.WriteLine(requestBody);

        //        Console.WriteLine("RESPONSE BODY:");
        //        Console.WriteLine(responseBody);

        //        // Copy the captured response body back to the original response stream.
        //        await responseBodyCopy.CopyToAsync(originalResponseBody);

        //        // Restore the original response body stream.
        //        context.Response.Body = originalResponseBody;

        //        var headers = context.Request.Headers
        //            .ToDictionary(h => h.Key,
        //                          h => string.Join(", ", h.Value.ToArray())); // Join multiple header values with a comma if they exist.

        //        var capturedRequest = new CapturedRequest
        //        {
        //            Headers = headers,
        //            Method = context.Request.Method,
        //            Path = context.Request.Path,
        //            RequestBody = TrimBody(requestBody),
        //            ResponseBody = TrimBody(responseBody),
        //            StatusCode = context.Response.StatusCode,
        //            DurationMs = stopwatch.ElapsedMilliseconds,
        //            Exception = exceptionMessage
        //        };

        //        await _queue.EnqueueAsync(capturedRequest);

        //    }
        //    finally
        //    {
        //        // Ensure that the original response body stream is restored even if an exception occurs.
        //        context.Response.Body = originalResponseBody;


        //    }
        //}

        public async Task InvokeAsync(HttpContext context)
        {
            var captureScopeId = GetOrCreateCaptureScopeId(context);
            if (!_captureRuntimeState.IsCaptureEnabled)
            {
                await _next(context);
                return;
            }

            //Check if the request path should be ignored based on the options. This allows you to exclude certain endpoints or paths from being captured, which can be useful for sensitive information or to reduce noise in the logs.
            if (ShouldIgnorePath(context))
            {
                await _next(context);
                return;
            }
            //Check if the middleware is enabled in the options. If not, simply call the next middleware in the pipeline and return without doing any processing.
            if (!_options.Enabled)
            {
                await _next(context);
                return;
            }

            //Check if the HTTP method of the incoming request is one that should be captured based on the options. If not, call the next middleware and return.
            if (!ShouldCaptureMethod(context))
            {
                await _next(context);
                return;
            }
            //Check if the request should be sampled based on the sampling rate specified in the options. This allows you to capture only a subset of requests, which can be useful for high-traffic applications to reduce overhead and storage requirements.
            if (!ShouldSample())
            {
                await _next(context);
                return;
            }



            const string CorrelationHeader = "X-Correlation-ID";
            const string TraceParentHeader = "traceparent";
            const string TraceStateHeader = "tracestate";
            const string BaggageHeader = "baggage";

            var correlationId =
                context.Request.Headers[CorrelationHeader].FirstOrDefault()
                ?? Guid.NewGuid().ToString();

            context.Response.Headers[CorrelationHeader] = correlationId;

            var activity = Activity.Current;
            var traceParent = context.Request.Headers[TraceParentHeader].FirstOrDefault();
            var traceState = context.Request.Headers[TraceStateHeader].FirstOrDefault();
            var baggage = context.Request.Headers[BaggageHeader].FirstOrDefault();
            var traceId = activity?.TraceId.ToString() ?? string.Empty;
            var spanId = activity?.SpanId.ToString() ?? string.Empty;
            var parentSpanId = activity?.ParentSpanId.ToString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(traceParent))
            {
                traceParent = activity?.Id;
            }

            if (string.IsNullOrWhiteSpace(traceParent) &&
                !string.IsNullOrWhiteSpace(traceId) &&
                !string.IsNullOrWhiteSpace(spanId))
            {
                traceParent = $"00-{traceId}-{spanId}-01";
            }

            if (string.IsNullOrWhiteSpace(traceState))
            {
                traceState = activity?.TraceStateString ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(baggage) && activity is not null)
            {
                baggage = string.Join(",", activity.Baggage.Select(kvp => $"{kvp.Key}={kvp.Value}"));
            }

            if (!string.IsNullOrWhiteSpace(traceParent))
            {
                context.Response.Headers[TraceParentHeader] = traceParent;
            }

            if (!string.IsNullOrWhiteSpace(traceState))
            {
                context.Response.Headers[TraceStateHeader] = traceState;
            }

            if (!string.IsNullOrWhiteSpace(baggage))
            {
                context.Response.Headers[BaggageHeader] = baggage;
            }

            var stopwatch = Stopwatch.StartNew();

            string requestBody = string.Empty;

            bool requestBodyCaptured = false;
            bool requestTruncated = false;
            string? requestBodySkippedReason = null;

            if (!_options.CaptureRequestBody)
            {
                requestBodySkippedReason = "Request body capture disabled";
            }
            else if (!ShouldCaptureContentType(context.Request.ContentType))
            {
                requestBodySkippedReason = $"Unsupported request content type: {context.Request.ContentType}";
            }
            else
            {
                context.Request.EnableBuffering();

                using var reader = new StreamReader(
                    context.Request.Body,
                    Encoding.UTF8,
                    leaveOpen: true);

                // Read only up to MaxBodyLength characters instead of the whole body then
                // truncating the resulting string afterward - a large upload (or an
                // attacker sending an oversized body on purpose) previously meant an
                // unbounded in-memory string regardless of how small MaxBodyLength was set.
                var maxChars = Math.Max(0, _options.MaxBodyLength);
                var readBuffer = new char[Math.Clamp(maxChars, 1, 65536)];
                var bodyBuilder = new StringBuilder();
                int charsRead;
                while (bodyBuilder.Length < maxChars &&
                       (charsRead = await reader.ReadAsync(readBuffer, 0, Math.Min(readBuffer.Length, maxChars - bodyBuilder.Length))) > 0)
                {
                    bodyBuilder.Append(readBuffer, 0, charsRead);
                }

                if (bodyBuilder.Length >= maxChars)
                {
                    // StreamReader.EndOfStream reads synchronously under the hood, which
                    // Kestrel disallows on the request stream by default and throws
                    // InvalidOperationException ("Synchronous operations are disallowed")
                    // for any body that actually hits this cap. A one-char async probe read
                    // detects "is there more data" without that trap; the probed character
                    // itself is discarded, which is fine - it exists only to answer the
                    // truncated/not-truncated question, not to be captured.
                    var probeBuffer = new char[1];
                    var probeRead = await reader.ReadAsync(probeBuffer, 0, 1);
                    requestTruncated = probeRead > 0;
                }

                requestBody = bodyBuilder.ToString();
                context.Request.Body.Position = 0;

                requestBodyCaptured = true;
            }

            var originalResponseBody = context.Response.Body;

            // Wraps originalResponseBody rather than buffering into a separate MemoryStream:
            // every write reaches the client immediately (true streaming for SSE/chunked/
            // large responses instead of the client waiting for the whole thing to finish),
            // while a bounded side-buffer captures at most MaxBodyLength bytes for FootLook
            // itself - see CappedTeeStream for why buffer-then-copy had to go.
            var teeStream = new CappedTeeStream(originalResponseBody, _options.MaxBodyLength);

            Exception? capturedException = null;
            string responseBody = string.Empty;

            try
            {
                context.Response.Body = teeStream;

                try
                {
                    await _next(context);
                }
                catch (Exception ex)
                {
                    capturedException = ex;
                }

                stopwatch.Stop();

                if (capturedException is not null)
                {
                    // The downstream pipeline threw. FootLook must not touch the response at
                    // all here: forcing StatusCode=500 would pre-empt the host's own
                    // exception handling (UseExceptionHandler, a dev exception page, a
                    // custom filter) further up the pipeline, which may still run and needs
                    // to own the response. Note that with CappedTeeStream, any bytes the
                    // endpoint already wrote before throwing were streamed to the client in
                    // real time - that's unavoidable and correct: it's exactly what would
                    // have happened if FootLook were not installed, since a partial write to
                    // a real response stream can't be un-sent once it leaves the process.
                    // We only record what we observed and rethrow immediately so the real
                    // handler runs exactly as it would if FootLook were not installed.
                    _logger.LogError(capturedException, "FootLook captured failure for {Method} {Path}", context.Request.Method, context.Request.Path);

                    var failureHeaders = CaptureHeaders(context, out var failureMaskedHeaderCount);
                    var failureMaskedPath = MaskPathAndQuery(context, out var failureMaskedQueryParameterCount);
                    var failureMaskedRequestBodyFieldCount = 0;
                    var failureMaskedRequestBody = requestBodyCaptured
                        ? TrimBody(MaskSensitiveBodyFields(requestBody, out failureMaskedRequestBodyFieldCount), requestTruncated)
                        : null;
                    var failureClientIp = MaskClientIp(context.Connection.RemoteIpAddress?.ToString(), out var failureClientIpAnonymized);
                    var failureUserAgent = MaskUserAgent(context.Request.Headers.UserAgent.ToString(), out var failureUserAgentAnonymized);

                    // The endpoint may have written some response bytes (headers, a partial
                    // body) before throwing - CappedTeeStream already mirrored up to
                    // MaxBodyLength of whatever went out, so surface it the same as the
                    // success path instead of always reporting an empty response.
                    var failurePartialResponseBytes = teeStream.GetCapturedBytes();
                    var failureResponseBodyCaptured = _options.CaptureResponseBody && failurePartialResponseBytes.Length > 0;
                    var failureMaskedResponseBodyFieldCount = 0;
                    var failureMaskedResponseBody = failureResponseBodyCaptured
                        ? TrimBody(
                            MaskSensitiveBodyFields(Encoding.UTF8.GetString(failurePartialResponseBytes), out failureMaskedResponseBodyFieldCount),
                            teeStream.CaptureTruncated)
                        : null;

                    var failureCapturedRequest = new CapturedRequest
                    {
                        Headers = failureHeaders,
                        Method = context.Request.Method,
                        Path = failureMaskedPath,
                        RequestBody = failureMaskedRequestBody,
                        ResponseBody = failureMaskedResponseBody,
                        // Recorded for observability only - the client's actual status is
                        // whatever the host's real exception handler decides, since we never
                        // write to context.Response here.
                        StatusCode = 500,
                        RequestContentType = context.Request.ContentType,
                        RequestSizeBytes = context.Request.ContentLength ?? (string.IsNullOrEmpty(requestBody) ? 0 : Encoding.UTF8.GetByteCount(requestBody)),
                        ResponseContentType = context.Response.ContentType,
                        ResponseSizeBytes = failurePartialResponseBytes.Length,
                        DurationMs = stopwatch.ElapsedMilliseconds,
                        Exception = capturedException.Message,
                        CorrelationId = correlationId,
                        TraceId = traceId,
                        SpanId = spanId,
                        ParentSpanId = parentSpanId,
                        TraceParent = traceParent ?? string.Empty,
                        TraceState = traceState ?? string.Empty,
                        Baggage = baggage ?? string.Empty,
                        ServiceName = _options.ServiceName,
                        EnvironmentName = _options.EnvironmentName,
                        RequestBodyCaptured = requestBodyCaptured,
                        ResponseBodyCaptured = failureResponseBodyCaptured,
                        RequestBodySkippedReason = requestBodySkippedReason,
                        ResponseBodySkippedReason = failureResponseBodyCaptured
                            ? null
                            : "Endpoint threw before a response body was produced",
                        ClientIp = failureClientIp,
                        UserAgent = failureUserAgent,
                        CaptureScopeId = captureScopeId,
                    };

                    await _queue.EnqueueAsync(failureCapturedRequest);

                    if (_options.EnablePrivacyAudit && _options.EnablePiiMasking)
                    {
                        var failureTotalMasks = failureMaskedHeaderCount + failureMaskedQueryParameterCount + failureMaskedRequestBodyFieldCount + failureMaskedResponseBodyFieldCount;
                        if (failureTotalMasks > 0)
                        {
                            _privacyAuditStore.Add(new PrivacyAuditEntry(
                                TimestampUtc: DateTime.UtcNow,
                                Method: context.Request.Method,
                                Path: context.Request.Path,
                                CorrelationId: correlationId,
                                MaskedHeaders: failureMaskedHeaderCount,
                                MaskedQueryParameters: failureMaskedQueryParameterCount,
                                MaskedRequestBodyFields: failureMaskedRequestBodyFieldCount,
                                MaskedResponseBodyFields: failureMaskedResponseBodyFieldCount,
                                ClientIpAnonymized: failureClientIpAnonymized,
                                UserAgentAnonymized: failureUserAgentAnonymized,
                                RedactionValue: GetRedactionValue()), _options.PrivacyAuditMaxEntries);
                        }
                    }

                    throw capturedException;
                }

                bool responseBodyCaptured = false;
                bool responseTruncated = false;
                string? responseBodySkippedReason = null;

                if (!_options.CaptureResponseBody)
                {
                    responseBodySkippedReason = "Response body capture disabled";
                }
                else if (!ShouldCaptureContentType(context.Response.ContentType))
                {
                    responseBodySkippedReason = $"Unsupported response content type: {context.Response.ContentType}";
                }
                else
                {
                    // Every byte was already streamed straight to the client as the
                    // endpoint wrote it (see CappedTeeStream) - nothing left to copy here,
                    // just decode whatever the tee mirrored into its bounded side-buffer.
                    responseBody = Encoding.UTF8.GetString(teeStream.GetCapturedBytes());
                    responseTruncated = teeStream.CaptureTruncated;

                    responseBodyCaptured = true;
                }

                var headers = CaptureHeaders(context, out var maskedHeaderCount);
                var maskedPath = MaskPathAndQuery(context, out var maskedQueryParameterCount);

                var maskedRequestBodyFieldCount = 0;
                var maskedResponseBodyFieldCount = 0;
                var maskedRequestBody = requestBodyCaptured
                    ? TrimBody(MaskSensitiveBodyFields(requestBody, out maskedRequestBodyFieldCount), requestTruncated)
                    : null;
                var maskedResponseBody = responseBodyCaptured
                    ? TrimBody(MaskSensitiveBodyFields(responseBody, out maskedResponseBodyFieldCount), responseTruncated)
                    : null;
                var anonymizedClientIp = MaskClientIp(context.Connection.RemoteIpAddress?.ToString(), out var clientIpAnonymized);
                var anonymizedUserAgent = MaskUserAgent(context.Request.Headers.UserAgent.ToString(), out var userAgentAnonymized);


                var capturedRequest = new CapturedRequest
                {
                    Headers = headers,
                    Method = context.Request.Method,
                    Path = maskedPath,
                    RequestBody = maskedRequestBody,
                    ResponseBody = maskedResponseBody,
                    StatusCode = context.Response.StatusCode,
                    RequestContentType = context.Request.ContentType,
                    RequestSizeBytes = context.Request.ContentLength ?? (string.IsNullOrEmpty(requestBody) ? 0 : Encoding.UTF8.GetByteCount(requestBody)),
                    ResponseContentType = context.Response.ContentType,
                    ResponseSizeBytes = context.Response.ContentLength ?? (string.IsNullOrEmpty(responseBody) ? 0 : Encoding.UTF8.GetByteCount(responseBody)),
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Exception = null,
                    CorrelationId = correlationId,
                    TraceId = traceId,
                    SpanId = spanId,
                    ParentSpanId = parentSpanId,
                    TraceParent = traceParent ?? string.Empty,
                    TraceState = traceState ?? string.Empty,
                    Baggage = baggage ?? string.Empty,
                    ServiceName = _options.ServiceName,
                    EnvironmentName = _options.EnvironmentName,
                    RequestBodyCaptured = requestBodyCaptured,
                    ResponseBodyCaptured = responseBodyCaptured,
                    RequestBodySkippedReason = requestBodySkippedReason,
                    ResponseBodySkippedReason = responseBodySkippedReason,
                    ClientIp = anonymizedClientIp,
                    UserAgent = anonymizedUserAgent,
                    CaptureScopeId = captureScopeId,
                };

                await _queue.EnqueueAsync(capturedRequest);

                if (_options.EnablePrivacyAudit && _options.EnablePiiMasking)
                {
                    var totalMasks = maskedHeaderCount + maskedQueryParameterCount + maskedRequestBodyFieldCount + maskedResponseBodyFieldCount;
                    if (totalMasks > 0)
                    {
                        _privacyAuditStore.Add(new PrivacyAuditEntry(
                            TimestampUtc: DateTime.UtcNow,
                            Method: context.Request.Method,
                            Path: context.Request.Path,
                            CorrelationId: correlationId,
                            MaskedHeaders: maskedHeaderCount,
                            MaskedQueryParameters: maskedQueryParameterCount,
                            MaskedRequestBodyFields: maskedRequestBodyFieldCount,
                            MaskedResponseBodyFields: maskedResponseBodyFieldCount,
                            ClientIpAnonymized: clientIpAnonymized,
                            UserAgentAnonymized: userAgentAnonymized,
                            RedactionValue: GetRedactionValue()), _options.PrivacyAuditMaxEntries);
                    }
                }

                _logger.LogInformation("FootLook captured request {Method} {Path} with status {StatusCode} in {Duration}ms",
                    context.Request.Method,
                    context.Request.Path,
                    context.Response.StatusCode,
                    stopwatch.ElapsedMilliseconds);
            }
            finally
            {
                context.Response.Body = originalResponseBody;
            }
        }

        /// <param name="alreadyTruncated">
        /// True when the caller already knows more data existed than was read/captured
        /// (e.g. CappedTeeStream or the bounded request read hit their byte/char cap), even
        /// if <paramref name="body"/>'s length alone wouldn't reveal that after masking.
        /// </param>
        private string TrimBody(string body, bool alreadyTruncated = false)
        {
            if (string.IsNullOrEmpty(body))
            {
                return body;
            }

            if (body.Length <= _options.MaxBodyLength)
            {
                return alreadyTruncated ? body + "...(truncated)" : body;
            }

            return body[.._options.MaxBodyLength] + "...(truncated)";
        }

        private bool ShouldIgnorePath(HttpContext context)
        {
            var path = context.Request.Path.Value ?? string.Empty;

            return _options.IgnoredPaths.Any(ignoredPath =>
            {
                var candidate = (ignoredPath ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    return false;
                }

                // Wildcard suffix support, e.g. "/.env*"
                if (candidate.EndsWith("*", StringComparison.Ordinal))
                {
                    var prefix = candidate[..^1];
                    if (string.IsNullOrWhiteSpace(prefix))
                    {
                        return false;
                    }

                    return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                }

                // Special-case root path so ignoring "/" does not ignore every endpoint.
                if (string.Equals(candidate, "/", StringComparison.OrdinalIgnoreCase))
                {
                    return string.Equals(path, "/", StringComparison.OrdinalIgnoreCase);
                }

                // Prefix/path-segment support, e.g. "/.git" catches "/.git/HEAD"
                return string.Equals(path, candidate, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(candidate + "/", StringComparison.OrdinalIgnoreCase);
            });
        }

        private static string GetOrCreateCaptureScopeId(HttpContext context)
        {
            const string cookieName = "footlook_scope_id";

            if (context.Request.Cookies.TryGetValue(cookieName, out var existing)
                && !string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }

            var scopeId = Guid.NewGuid().ToString("D");

            context.Response.Cookies.Append(cookieName, scopeId, new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                Secure = context.Request.IsHttps,
                Expires = DateTimeOffset.UtcNow.AddYears(1)
            });

            return scopeId;
        }

        private Dictionary<string, string> CaptureHeaders(HttpContext context, out int maskedCount)
        {
            maskedCount = 0;
            var redaction = GetRedactionValue();
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var header in context.Request.Headers)
            {
                if (_options.EnablePiiMasking && IsSensitiveHeaderName(header.Key))
                {
                    maskedCount++;
                    result[header.Key] = redaction;
                    continue;
                }

                result[header.Key] = string.Join(", ", header.Value.ToArray());
            }

            return result;
        }

        private string MaskSensitiveBodyFields(string body, out int maskedFieldCount)
        {
            maskedFieldCount = 0;

            if (string.IsNullOrEmpty(body))
            {
                return body;
            }

            if (!_options.EnablePiiMasking)
            {
                return body;
            }

            var redaction = GetRedactionValue();

            foreach (var sensitiveField in _options.SensitiveBodyFields)
            {
                if (string.IsNullOrWhiteSpace(sensitiveField))
                {
                    continue;
                }

                var (jsonRegex, queryRegex) = GetOrCompileFieldRegex(sensitiveField.Trim());

                maskedFieldCount += jsonRegex.Matches(body).Count;
                maskedFieldCount += queryRegex.Matches(body).Count;

                body = jsonRegex.Replace(body, m => $"{m.Groups[1].Value}\"{redaction}\"");
                body = queryRegex.Replace(body, m => $"{m.Groups[1].Value}{redaction}");
            }

            return body;
        }

        private (Regex Json, Regex Query) GetOrCompileFieldRegex(string sensitiveField)
        {
            return _sensitiveFieldRegexCache.GetOrAdd(sensitiveField, field =>
            {
                var escapedField = Regex.Escape(field);
                var jsonPattern = $"(\"{escapedField}\"\\s*:\\s*)(\".*?\"|[^,\\}}\\]]+)";
                var queryPattern = $"((?:^|[&?]){escapedField}=)([^&\\s]*)";

                var json = new Regex(jsonPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
                var query = new Regex(queryPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                return (json, query);
            });
        }

        private string MaskPathAndQuery(HttpContext context, out int maskedQueryCount)
        {
            maskedQueryCount = 0;
            var path = context.Request.Path.Value ?? string.Empty;
            if (!context.Request.QueryString.HasValue)
            {
                return path;
            }

            if (!_options.EnablePiiMasking)
            {
                return path + context.Request.QueryString.Value;
            }

            var redaction = GetRedactionValue();

            var maskedValues = new List<KeyValuePair<string, string?>>();
            foreach (var queryItem in context.Request.Query)
            {
                var isSensitive = IsSensitiveQueryParameter(queryItem.Key);

                if (isSensitive)
                {
                    foreach (var _ in queryItem.Value)
                    {
                        maskedQueryCount++;
                        maskedValues.Add(new KeyValuePair<string, string?>(queryItem.Key, redaction));
                    }

                    continue;
                }

                foreach (var queryValue in queryItem.Value)
                {
                    maskedValues.Add(new KeyValuePair<string, string?>(queryItem.Key, queryValue ?? string.Empty));
                }
            }

            var maskedQuery = QueryString.Create(maskedValues);
            return path + maskedQuery.Value;
        }

        private string GetRedactionValue()
        {
            return string.IsNullOrWhiteSpace(_options.RedactionValue)
                ? "[REDACTED]"
                : _options.RedactionValue;
        }

        private bool IsSensitiveHeaderName(string headerName)
        {
            foreach (var sensitive in _options.SensitiveHeaders)
            {
                if (string.Equals(sensitive, headerName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsSensitiveQueryParameter(string queryParameterName)
        {
            foreach (var sensitive in _options.SensitiveQueryParameters)
            {
                if (string.Equals(sensitive, queryParameterName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private string MaskClientIp(string? clientIp, out bool anonymized)
        {
            anonymized = false;

            if (string.IsNullOrWhiteSpace(clientIp))
            {
                return string.Empty;
            }

            if (!_options.EnablePiiMasking || !_options.AnonymizeClientIp)
            {
                return clientIp;
            }

            anonymized = true;
            return HashForPrivacy(clientIp);
        }

        private string MaskUserAgent(string? userAgent, out bool anonymized)
        {
            anonymized = false;

            if (string.IsNullOrWhiteSpace(userAgent))
            {
                return string.Empty;
            }

            if (!_options.EnablePiiMasking || !_options.AnonymizeUserAgent)
            {
                return userAgent;
            }

            anonymized = true;
            return HashForPrivacy(userAgent);
        }

        private string HashForPrivacy(string value)
        {
            var salt = _options.PrivacyHashSalt ?? string.Empty;
            var raw = string.IsNullOrEmpty(salt) ? value : $"{salt}:{value}";
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            var hash = Convert.ToHexString(bytes).ToLowerInvariant();
            return $"sha256:{hash[..16]}";
        }

        private bool ShouldCaptureMethod(HttpContext context)
        {
            if (!_options.AllowedMethods.Any())
                return true;

            return _options.AllowedMethods.Any(method => string.Equals(method, context.Request.Method, StringComparison.OrdinalIgnoreCase));
        }

        private bool ShouldSample()
        {
            if (_options.SamplingRate >= 1.0)
            {
                return true;
            }

            if (_options.SamplingRate <= 0.0)
            {
                return false;
            }

            return new Random().NextDouble() < _options.SamplingRate;
        }

        public long GetSizeInBytes(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return 0;
            }

            return Encoding.UTF8.GetByteCount(value);
        }

        private bool ShouldCaptureContentType(string? contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType))
                return false;

            return _options.AllowedContentType.Any(allowed =>
                contentType.StartsWith(
                    allowed,
                    StringComparison.OrdinalIgnoreCase));
        }



    }
}
