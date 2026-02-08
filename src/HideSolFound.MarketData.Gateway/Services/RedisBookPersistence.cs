using System.Text.Json;
using System.Text.Json.Serialization;
using HideSolFound.MarketData.Gateway.Models;
using StackExchange.Redis;

namespace HideSolFound.MarketData.Gateway.Services;

public sealed class RedisBookPersistence : IRedisBookPersistence
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisBookPersistence> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly TimeSpan SnapshotExpiry = TimeSpan.FromHours(24);
    private const int MaxStreamEntries = 1000;

    public RedisBookPersistence(IConnectionMultiplexer redis, ILogger<RedisBookPersistence> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task SaveSnapshotAsync(string symbol, BookSnapshotDto snapshot)
    {
        try
        {
            IDatabase db = _redis.GetDatabase();
            string json = JsonSerializer.Serialize(snapshot, JsonOptions);
            await db.StringSetAsync($"book:snapshot:{symbol}", json, SnapshotExpiry);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save snapshot for {Symbol}", symbol);
        }
    }

    public async Task SaveIncrementalAsync(string symbol, BookIncrementalDto incremental)
    {
        try
        {
            IDatabase db = _redis.GetDatabase();
            string json = JsonSerializer.Serialize(incremental, JsonOptions);

            await db.StreamAddAsync(
                $"book:incremental:{symbol}",
                [new NameValueEntry("data", json)],
                maxLength: MaxStreamEntries,
                useApproximateMaxLength: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save incremental for {Symbol}", symbol);
        }
    }

    public async Task<BookSnapshotDto?> GetSnapshotAsync(string symbol)
    {
        try
        {
            IDatabase db = _redis.GetDatabase();
            RedisValue json = await db.StringGetAsync($"book:snapshot:{symbol}");
            return json.IsNullOrEmpty ? null : JsonSerializer.Deserialize<BookSnapshotDto>((string)json!, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get snapshot for {Symbol}", symbol);
            return null;
        }
    }

    public async Task PublishUpdateAsync(string channel, string message)
    {
        try
        {
            ISubscriber subscriber = _redis.GetSubscriber();
            await subscriber.PublishAsync(RedisChannel.Literal(channel), message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish to {Channel}", channel);
        }
    }
}
