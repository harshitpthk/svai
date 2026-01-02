using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using StockScreener.Core;

namespace StockScreener.Data.Price;

public sealed class YahooPriceProvider(HttpClient http, IMemoryCache cache, ILogger<YahooPriceProvider> logger) : IPriceDataProvider
{
    private const string Base = "https://query1.finance.yahoo.com";

    public async Task<IReadOnlyList<PriceBar>> GetDailyAsync(string ticker, DateOnly start, DateOnly end, CancellationToken ct = default)
    {
        // NOTE: Unofficial endpoint. For production, replace with paid data provider.
        var period1 = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue)).ToUnixTimeSeconds();
        var period2 = new DateTimeOffset(end.ToDateTime(TimeOnly.MaxValue)).ToUnixTimeSeconds();
        var url = $"{Base}/v8/finance/chart/{ticker}?interval=1d&period1={period1}&period2={period2}&includePrePost=false";

        var cacheKey = $"prices:{ticker}:{period1}:{period2}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<PriceBar>? cached) && cached is not null)
            return cached;

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var root = await resp.Content.ReadFromJsonAsync<YahooChartRoot>(cancellationToken: ct) ?? throw new InvalidOperationException("Empty Yahoo response");
        var r = root.chart.result?.FirstOrDefault() ?? throw new InvalidOperationException("No result");
        var ts = r.timestamp ?? Array.Empty<long>();
        var o = r.indicators.quote.First();
        var list = new List<PriceBar>(ts.Length);
        for (int i = 0; i < ts.Length; i++)
        {
            var date = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(ts[i]).UtcDateTime);
            var bar = new PriceBar(
                date,
                ToDec(o.open, i),
                ToDec(o.high, i),
                ToDec(o.low, i),
                ToDec(o.close, i),
                ToLong(o.volume, i)
            );
            list.Add(bar);
        }
        cache.Set(cacheKey, list, TimeSpan.FromMinutes(30));
        logger.LogInformation("Fetched {Count} bars for {Ticker}", list.Count, ticker);
        return list;
    }

    private static decimal ToDec(decimal?[] arr, int i) => arr.Length > i && arr[i].HasValue ? arr[i]!.Value : 0m;
    private static long ToLong(long?[] arr, int i) => arr.Length > i && arr[i].HasValue ? arr[i]!.Value : 0L;

    private sealed class YahooChartRoot
    {
        public Chart chart { get; set; } = new();
        public sealed class Chart
        {
            public Result[]? result { get; set; }
        }
        public sealed class Result
        {
            public long[]? timestamp { get; set; }
            public required Indicator indicators { get; set; }
        }
        public sealed class Indicator
        {
            public required Quote[] quote { get; set; }
        }
        public sealed class Quote
        {
            public required decimal?[] open { get; set; }
            public required decimal?[] high { get; set; }
            public required decimal?[] low { get; set; }
            public required decimal?[] close { get; set; }
            public required long?[] volume { get; set; }
        }
    }
}
