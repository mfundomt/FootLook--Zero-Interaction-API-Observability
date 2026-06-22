using FootLook.Core.Interfaces;
using FootLook.Core.Models;
using FootLook.Core.Options;
using MongoDB.Driver;

namespace FootLook.Data.Repositories;

public class MongoCaptureRepository : ICaptureRepository
{
    private readonly IMongoCollection<CapturedRequest>? _collection;
    private readonly bool _isEnabled;

    public MongoCaptureRepository(FootLookOptions options)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(options.MongoConnectionString))
            {
                Console.WriteLine("[MongoCaptureRepository] MongoDB connection string is not configured. Repository will be disabled.");
                _isEnabled = false;
                return;
            }

            var client = new MongoClient(options.MongoConnectionString);

            var database = client.GetDatabase(options.MongoDatabaseName);

            _collection = database.GetCollection<CapturedRequest>(
                options.MongoCollectionName);

            _isEnabled = true;
            Console.WriteLine("[MongoCaptureRepository] MongoDB connection initialized successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MongoCaptureRepository] Failed to initialize MongoDB connection: {ex.Message}");
            Console.WriteLine("[MongoCaptureRepository] Repository will be disabled. Application will continue without MongoDB.");
            _isEnabled = false;
        }
    }

    public async Task<List<CapturedRequest>> GetRecentAsync(int count)
    {
        if (!_isEnabled || _collection == null)
        {
            Console.WriteLine("[MongoCaptureRepository] Repository is disabled. Returning empty list.");
            return new List<CapturedRequest>();
        }

        try
        {
            return await _collection
                .Find(_ => true)
                .SortByDescending(x => x.TimestampUtc)
                .Limit(count)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MongoCaptureRepository] Error getting recent captures: {ex.Message}");
            return new List<CapturedRequest>();
        }
    }

    public async Task<CaptureStats> GetStatsAsync()
    {
        if (!_isEnabled || _collection == null)
        {
            Console.WriteLine("[MongoCaptureRepository] Repository is disabled. Returning empty stats.");
            return new CaptureStats
            {
                TotalRequests = 0,
                FailedRequests = 0,
                AverageDurationMs = 0,
                SlowRequests = 0,
                TopEndpoints = new List<TopEndpointStats>(),
                TopSlowEndpoints = new List<TopEndpointStats>()
            };
        }

        try
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
        catch (Exception ex)
        {
            Console.WriteLine($"[MongoCaptureRepository] Error getting stats: {ex.Message}");
            return new CaptureStats
            {
                TotalRequests = 0,
                FailedRequests = 0,
                AverageDurationMs = 0,
                SlowRequests = 0,
                TopEndpoints = new List<TopEndpointStats>(),
                TopSlowEndpoints = new List<TopEndpointStats>()
            };
        }
    }

    public async Task<List<CapturedRequest>> SearchAsync(
        string? path = null,
        int? minStatusCode = null,
        long? minDuration = null,
        string? correlationId = null)
    {
        if (!_isEnabled || _collection == null)
        {
            Console.WriteLine("[MongoCaptureRepository] Repository is disabled. Returning empty list.");
            return new List<CapturedRequest>();
        }

        try
        {
            return await _collection
                .Find(_ => true)
                .Limit(100)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MongoCaptureRepository] Error searching captures: {ex.Message}");
            return new List<CapturedRequest>();
        }
    }
}