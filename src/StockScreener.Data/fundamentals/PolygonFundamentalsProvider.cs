using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StockScreener.Core;

namespace StockScreener.Data;

/// <summary>
/// Fundamentals provider using Polygon.io.
///
/// NOTE: Polygon's fundamentals coverage/endpoints vary by subscription.
/// This implementation is a placeholder that currently falls back to config defaults
/// until the exact Polygon fundamentals endpoint/schema is finalized.
/// </summary>
public sealed class PolygonFundamentalsProvider : IFundamentalsProvider
{
    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _config;
    private readonly ILogger<PolygonFundamentalsProvider> _logger;

    public PolygonFundamentalsProvider(
        HttpClient http,
        IMemoryCache cache,
        IConfiguration config,
        ILogger<PolygonFundamentalsProvider> logger)
    {
        _http = http;
        _cache = cache;
        _config = config;
        _logger = logger;
    }

    public async Task<Fundamentals?> GetAsync(string ticker, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ticker)) throw new ArgumentException("Ticker is required", nameof(ticker));

        var apiKey = _config["Providers:PolygonApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Missing Providers:PolygonApiKey in configuration");

        var t = ticker.Trim().ToUpperInvariant();
        var cacheKey = $"polygon:fundamentals:{t}";
        if (_cache.TryGetValue(cacheKey, out Fundamentals? cached) && cached is not null)
            return cached;

        // Placeholder behavior:
        // Until we lock down a specific Polygon fundamentals endpoint + response model,
        // use the same config-backed defaults used by ConfigFundamentalsProvider.
        //
        // When we implement Polygon fundamentals properly, replace this section with:
        // - an HTTP call to Polygon using _http
        // - mapping from Polygon fields -> Fundamentals record
        // - appropriate caching + error handling

        var cfgFallback = new ConfigFundamentalsProvider(_config);
        var f = await cfgFallback.GetAsync(t, ct);

        _cache.Set(cacheKey, f, TimeSpan.FromHours(12));
        _logger.LogInformation("(Placeholder) Served Polygon fundamentals for {Ticker} from config defaults", t);
        return f;
    }
}
