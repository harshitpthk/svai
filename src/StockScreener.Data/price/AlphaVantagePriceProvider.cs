using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StockScreener.Core;

namespace StockScreener.Data.Price;

/// <summary>
/// Alpha Vantage daily adjusted time series (US equities/ETFs). Requires API key.
/// Free tier is heavily rate-limited.
/// </summary>
public sealed class AlphaVantagePriceProvider(HttpClient http, IMemoryCache cache, IConfiguration config, ILogger<AlphaVantagePriceProvider> logger)
    : IPriceDataProvider
{
    private const string Base = "https://www.alphavantage.co";

    public async Task<IReadOnlyList<PriceBar>> GetDailyAsync(string ticker, DateOnly start, DateOnly end, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ticker)) throw new ArgumentException("Ticker is required", nameof(ticker));

        var apiKey = config["Providers:AlphaVantageApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Missing Providers:AlphaVantageApiKey in configuration");

        // AlphaVantage supports outputsize=compact|full. Use full so we can slice date ranges locally.
        var url = $"{Base}/query?function=TIME_SERIES_DAILY_ADJUSTED&symbol={Uri.EscapeDataString(ticker)}&outputsize=full&apikey={Uri.EscapeDataString(apiKey)}";

        var cacheKey = $"av:daily_adjusted:{ticker}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<PriceBar>? cached) && cached is not null)
            return FilterByDates(cached, start, end);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var root = await resp.Content.ReadFromJsonAsync<AlphaVantageDailyAdjustedResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty Alpha Vantage response");

        if (root.ErrorMessage is not null)
            throw new InvalidOperationException($"Alpha Vantage error: {root.ErrorMessage}");

        if (root.Note is not null)
            throw new InvalidOperationException($"Alpha Vantage throttled: {root.Note}");

        if (root.TimeSeriesDailyAdjusted is null || root.TimeSeriesDailyAdjusted.Count == 0)
            throw new InvalidOperationException("Alpha Vantage returned no time series data");

        var list = new List<PriceBar>(root.TimeSeriesDailyAdjusted.Count);
        foreach (var (dateStr, bar) in root.TimeSeriesDailyAdjusted)
        {
            if (!DateOnly.TryParse(dateStr, out var date)) continue;

            var open = bar.Open;
            var high = bar.High;
            var low = bar.Low;
            var close = bar.Close;
            var volume = bar.Volume;

            list.Add(new PriceBar(date, open, high, low, close, volume));
        }

        list.Sort((a, b) => a.Date.CompareTo(b.Date));

        // Cache relatively long; AV calls are precious on the free tier.
        cache.Set(cacheKey, list, TimeSpan.FromHours(12));
        logger.LogInformation("Fetched {Count} AlphaVantage bars for {Ticker}", list.Count, ticker);

        return FilterByDates(list, start, end);
    }

    private static IReadOnlyList<PriceBar> FilterByDates(IReadOnlyList<PriceBar> bars, DateOnly start, DateOnly end)
        => bars
            .Where(b => b.Date >= start && b.Date <= end)
            .OrderBy(b => b.Date)
            .ToArray();

    // AlphaVantage JSON is dynamic keys; model only what we need.
    private sealed class AlphaVantageDailyAdjustedResponse
    {
        // When throttled:
        // { "Note": "Thank you for using Alpha Vantage! Our standard API rate limit is ..." }
        public string? Note { get; set; }

        // When error:
        // { "Error Message": "Invalid API call." }
        public string? ErrorMessage { get; set; }

        // Actual key: "Time Series (Daily)" for this function.
        [System.Text.Json.Serialization.JsonPropertyName("Time Series (Daily)")]
        public Dictionary<string, AlphaVantageBar>? TimeSeriesDailyAdjusted { get; set; }
    }

    private sealed class AlphaVantageBar
    {
        [System.Text.Json.Serialization.JsonPropertyName("1. open")]
        public decimal Open { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("2. high")]
        public decimal High { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("3. low")]
        public decimal Low { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("4. close")]
        public decimal Close { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("6. volume")]
        public long Volume { get; set; }
    }
}
