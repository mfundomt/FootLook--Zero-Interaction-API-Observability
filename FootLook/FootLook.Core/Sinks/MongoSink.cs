using FootLook.Core.Interfaces;
using FootLook.Core.Models;
using FootLook.Core.Options;
using MongoDB.Driver;

namespace FootLook.Core.Sinks;

public class MongoSink : IShadowSink
{
    private readonly IMongoCollection<MongoCapturedRequest> _collection;

    public MongoSink(FootLookOptions options)
    {
        var client = new MongoClient(options.MongoConnectionString);
        var database = client.GetDatabase(options.MongoDatabaseName);

        _collection = database.GetCollection<MongoCapturedRequest>(
            options.MongoCollectionName);
    }

    public async Task WriteAsync(CapturedRequest request)
    {
        Console.WriteLine($"[MongoSink] Writing {request.Method} {request.Path}");

        var document = new MongoCapturedRequest
        {
            Id = request.Id.ToString(),
            TimestampUtc = request.TimestampUtc,
            Method = request.Method,
            Path = request.Path,
            Headers = request.Headers,
            RequestBody = request.RequestBody,
            ResponseBody = request.ResponseBody,
            StatusCode = request.StatusCode,
            DurationMs = request.DurationMs,
            Exception = request.Exception,
            CorrelationId = request.CorrelationId,
            ServiceName = request.ServiceName,
            EnvironmentName = request.EnvironmentName,
            RequestSizeBytes = request.RequestSizeBytes,
            ResponseSizeBytes = request.ResponseSizeBytes,
            RequestContentType = request.RequestContentType,
            ResponseContentType = request.ResponseContentType,
            RequestBodyCaptured = request.RequestBodyCaptured,
            ResponseBodyCaptured = request.ResponseBodyCaptured,
            RequestBodySkippedReason = request.RequestBodySkippedReason,
            ResponseBodySkippedReason = request.ResponseBodySkippedReason,
            ClientIp = request.ClientIp,
            UserAgent = request.UserAgent
        };

        await _collection.InsertOneAsync(document);

        Console.WriteLine("[MongoSink] Written successfully");
    }

    private class MongoCapturedRequest
    {
        public string Id { get; set; } = string.Empty;
        public DateTime TimestampUtc { get; set; }

        public string Method { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;

        public Dictionary<string, string> Headers { get; set; } = new();

        public string? RequestBody { get; set; }
        public string? ResponseBody { get; set; }

        public int StatusCode { get; set; }
        public long DurationMs { get; set; }

        public string? Exception { get; set; }
        public string CorrelationId { get; set; } = string.Empty;

        public string ServiceName { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = string.Empty;

        public long RequestSizeBytes { get; set; }
        public long ResponseSizeBytes { get; set; }

        public string? RequestContentType { get; set; }
        public string? ResponseContentType { get; set; }

        public bool RequestBodyCaptured { get; set; }
        public bool ResponseBodyCaptured { get; set; }

        public string? RequestBodySkippedReason { get; set; }
        public string? ResponseBodySkippedReason { get; set; }

        public string? ClientIp { get; set; }
        public string? UserAgent { get; set; }
    }
}