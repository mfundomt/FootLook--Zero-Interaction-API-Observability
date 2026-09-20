using FootLook.Core.Interfaces;
using FootLook.Core.Models;
using FootLook.Core.Options;
using MongoDB.Driver;

namespace FootLook.Data.Repositories;

/// <summary>
/// Reads captures from MongoDB. Not used by any FootLook endpoint and not session-aware:
/// the queries are unfiltered and ObserverSessionIds is not persisted, so it cannot purge or
/// scope by observation session. Anything exposing it to developers must first add session
/// scoping and purge-on-session-end; until then it must not be wired to a route.
/// </summary>
public class MongoCaptureRepository : ICaptureRepository
{
    private readonly IMongoCollection<CapturedRequest> _collection;

    public MongoCaptureRepository(FootLookOptions options)
    {
        var client = new MongoClient(options.MongoConnectionString);

        var database = client.GetDatabase(options.MongoDatabaseName);

        _collection = database.GetCollection<CapturedRequest>(
            options.MongoCollectionName);
    }

    public async Task<List<CapturedRequest>> GetRecentAsync(int count)
    {
        return await _collection
            .Find(_ => true)
            .SortByDescending(x => x.TimestampUtc)
            .Limit(count)
            .ToListAsync();
    }

    public async Task<CaptureStats> GetStatsAsync()
    {
        var captures = await _collection
            .Find(_ => true)
            .ToListAsync();

        var totalRequests = captures.Count;

        var failedRequests = captures.Count(c =>
            c.StatusCode >= 400 ||
            !string.IsNullOrWhiteSpace(c.Exception));

        var averageDuration = captures.Any()
            ? captures.Average(c => c.DurationMs)
            : 0;

        var slowRequests = captures.Count(c =>
            c.DurationMs >= 1000);

        var topEndpoints = captures
            .GroupBy(c => c.Path)
            .Select(g => new TopEndpointStats
            {
                Path = g.Key,
                Count = g.Count(),
                AverageDuration = g.Average(x => x.DurationMs),
                Failures = g.Count(x =>
                    x.StatusCode >= 400 ||
                    !string.IsNullOrWhiteSpace(x.Exception))
            })
            .OrderByDescending(x => x.Count)
            .Take(10)
            .ToList();

        var topSlowEndpoints = captures
            .GroupBy(c => c.Path)
            .Select(g => new TopEndpointStats
            {
                Path = g.Key,
                Count = g.Count(),
                AverageDuration = g.Average(x => x.DurationMs),
                Failures = g.Count(x =>
                    x.StatusCode >= 400 ||
                    !string.IsNullOrWhiteSpace(x.Exception))
            })
            .OrderByDescending(x => x.AverageDuration)
            .Take(10)
            .ToList();

        return new CaptureStats
        {
            TotalRequests = totalRequests,
            FailedRequests = failedRequests,
            AverageDurationMs = averageDuration,
            SlowRequests = slowRequests,
            TopEndpoints = topEndpoints,
            TopSlowEndpoints = topSlowEndpoints
        };
    }

    public async Task<List<CapturedRequest>> SearchAsync(
        string? path = null,
        int? minStatusCode = null,
        long? minDuration = null,
        string? correlationId = null)
    {
        // Previously ignored every parameter and just returned the first 100 documents
        // unconditionally - callers asking for a specific path/status/duration/correlation
        // got unrelated results back with no indication their filters did nothing.
        var filters = new List<FilterDefinition<CapturedRequest>>();

        if (!string.IsNullOrWhiteSpace(path))
        {
            filters.Add(Builders<CapturedRequest>.Filter.Regex(
                x => x.Path,
                new MongoDB.Bson.BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(path), "i")));
        }

        if (minStatusCode.HasValue)
        {
            filters.Add(Builders<CapturedRequest>.Filter.Gte(x => x.StatusCode, minStatusCode.Value));
        }

        if (minDuration.HasValue)
        {
            filters.Add(Builders<CapturedRequest>.Filter.Gte(x => x.DurationMs, minDuration.Value));
        }

        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            filters.Add(Builders<CapturedRequest>.Filter.Eq(x => x.CorrelationId, correlationId));
        }

        var filter = filters.Count == 0
            ? Builders<CapturedRequest>.Filter.Empty
            : Builders<CapturedRequest>.Filter.And(filters);

        return await _collection
            .Find(filter)
            .SortByDescending(x => x.TimestampUtc)
            .Limit(100)
            .ToListAsync();
    }

    public async Task<(IReadOnlyList<CapturedRequest> Items, long TotalCount)> GetRecentPagedAsync(int page, int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var filter = Builders<CapturedRequest>.Filter.Empty;
        var totalCount = await _collection.CountDocumentsAsync(filter);

        var items = await _collection
            .Find(filter)
            .SortByDescending(x => x.TimestampUtc)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();

        return (items, totalCount);
    }
}