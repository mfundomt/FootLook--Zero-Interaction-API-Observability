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


        public ShadowMiddleware(RequestDelegate next, IShadowQueue queue, FootLookOptions options, ILogger<ShadowMiddleware> logger)
        {
            _next = next;
            _queue = queue;
            _options = options;
            _logger = logger;
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

            var correlationId =
                context.Request.Headers[CorrelationHeader].FirstOrDefault()
                ?? Guid.NewGuid().ToString();

            context.Response.Headers[CorrelationHeader] = correlationId;

            var stopwatch = Stopwatch.StartNew();

            string requestBody = string.Empty;

            bool requestBodyCaptured = false;
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

                requestBody = await reader.ReadToEndAsync();
                context.Request.Body.Position = 0;

                requestBodyCaptured = true;
            }

            var originalResponseBody = context.Response.Body;

            await using var responseBodyCopy = new MemoryStream();

            Exception? capturedException = null;
            string responseBody = string.Empty;

            try
            {
                context.Response.Body = responseBodyCopy;

                try
                {
                    await _next(context);
                }
                catch (Exception ex)
                {
                    capturedException = ex;
                    context.Response.StatusCode = 500;
                    _logger.LogError(ex, "FootLook captured failure for {Method} {Path}", context.Request.Method, context.Request.Path);
                }

                stopwatch.Stop();

                //if (_options.CaptureResponseBody && ShouldCaptureContentType(context.Response.ContentType))
                //{
                //    responseBodyCopy.Position = 0;

                //    responseBody = await new StreamReader(responseBodyCopy)
                //        .ReadToEndAsync();

                //    responseBodyCopy.Position = 0;
                //}

                bool responseBodyCaptured = false;
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
                    responseBodyCopy.Position = 0;

                    responseBody = await new StreamReader(responseBodyCopy)
                        .ReadToEndAsync();

                    responseBodyCopy.Position = 0;

                    responseBodyCaptured = true;
                }

                await responseBodyCopy.CopyToAsync(originalResponseBody);

                var headers = CaptureHeaders(context);


                var capturedRequest = new CapturedRequest
                {
                    Headers = headers,
                    Method = context.Request.Method,
                    Path = context.Request.Path,
                    RequestBody = requestBodyCaptured ? TrimBody(MaskSensitiveBodyFields(requestBody)): null,
                    ResponseBody = responseBodyCaptured ? TrimBody(MaskSensitiveBodyFields(responseBody)):null,
                    StatusCode = context.Response.StatusCode,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Exception = capturedException?.Message,
                    CorrelationId = correlationId,
                    ServiceName = _options.ServiceName,
                    EnvironmentName = _options.EnvironmentName,
                    RequestSizeBytes = GetSizeInBytes(requestBody),
                    ResponseSizeBytes = GetSizeInBytes(responseBody),
                    RequestContentType = context.Request.ContentType,
                    ResponseContentType = context.Response.ContentType,
                    RequestBodyCaptured = requestBodyCaptured,
                    ResponseBodyCaptured = responseBodyCaptured,
                    RequestBodySkippedReason = requestBodySkippedReason,
                    ResponseBodySkippedReason = responseBodySkippedReason,
                    ClientIp = context.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
                    UserAgent = context.Request.Headers.UserAgent.ToString(),
                };

                await _queue.EnqueueAsync(capturedRequest);

                _logger.LogInformation("FootLook captured request {Method} {Path} with status {StatusCode} in {Duration}ms",
                    context.Request.Method,
                    context.Request.Path,
                    context.Response.StatusCode,
                    stopwatch.ElapsedMilliseconds);

                if (capturedException is not null)
                {
                    throw capturedException;
                }
            }
            finally
            {
                context.Response.Body = originalResponseBody;
            }
        }

        private string TrimBody(string body)
        {
            if (string.IsNullOrEmpty(body))
            {
                return body;
            }

            if (body.Length <= _options.MaxBodyLength)
            {
                return body;
            }

            return body[.._options.MaxBodyLength] + "...(truncated)";
        }

        private bool ShouldIgnorePath(HttpContext context)
        {
            var path = context.Request.Path.Value ?? string.Empty;

            return _options.IgnoredPaths.Any(ignoredPath =>
                path.StartsWith(ignoredPath, StringComparison.OrdinalIgnoreCase));
        }

        private Dictionary<string, string> CaptureHeaders(HttpContext context)
        {
            return context.Request.Headers
                .ToDictionary(h => h.Key,
                              h =>
                              {
                                  if (_options.SensitiveHeaders.Any(sensitive =>
                                  string.Equals(h.Key, sensitive, StringComparison.OrdinalIgnoreCase)))
                                  {
                                      return "[REDACTED]";
                                  }

                                  return string.Join(", ", h.Value.ToArray());
                              });
        }

        private string MaskSensitiveBodyFields(string body)
        {
            if (string.IsNullOrEmpty(body))
            {
                return body;
            }
            //foreach of the sensitive fields specified in the options, we use a regular expression to find and replace the value of that field in the body
            //with a placeholder like [REDACTED]. This helps to ensure that sensitive information is not stored or logged in its original form, enhancing security and privacy.
            foreach (var sensitiveField in _options.SensitiveBodyFields)
            {
                body = System.Text.RegularExpressions.Regex.Replace(
                    body,
                    $"\"{sensitiveField}\"\\s*:\\s*\".*?\"",
                    $"\"{sensitiveField}\":\"[REDACTED]\"",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            return body;
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
