using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using StockScreener.Core;

namespace StockScreener.Data.Price;

/// <summary>
/// Free daily US EOD bars from Stooq (CSV). No API key.
/// Symbols are mapped as {TICKER}.US (e.g., AAPL.US).
/// </summary>
public sealed class StooqPriceProvider(HttpClient http, IMemoryCache cache, ILogger<StooqPriceProvider> logger) : IPriceDataProvider
{
    private const string Base = "https://stooq.com";

    public async Task<IReadOnlyList<PriceBar>> GetDailyAsync(string ticker, DateOnly start, DateOnly end, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ticker)) throw new ArgumentException("Ticker is required", nameof(ticker));

        // Stooq uses {symbol}.US for US equities/ETFs.
        var symbol = ToStooqSymbol(ticker);

        // Stooq returns daily history as CSV, usually in descending order (newest first).
        // Example: https://stooq.com/q/d/l/?s=aapl.us&i=d
        var url = $"{Base}/q/d/l/?s={Uri.EscapeDataString(symbol)}&i=d";

        var cacheKey = $"stooq:prices:{symbol}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<PriceBar>? cached) && cached is not null)
            return FilterByDates(cached, start, end);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var csv = await resp.Content.ReadAsStringAsync(ct);

        var parsed = ParseCsv(csv);
        cache.Set(cacheKey, parsed, TimeSpan.FromHours(6));
        logger.LogInformation("Fetched {Count} Stooq bars for {Ticker}", parsed.Count, ticker);

        return FilterByDates(parsed, start, end);
    }

    private static string ToStooqSymbol(string ticker)
    {
        // Keep it conservative: US only as requested.
        var t = ticker.Trim().ToLowerInvariant();
        return t.EndsWith(".us", StringComparison.Ordinal) ? t : $"{t}.us";
    }

    private static IReadOnlyList<PriceBar> FilterByDates(IReadOnlyList<PriceBar> bars, DateOnly start, DateOnly end)
        => bars
            .Where(b => b.Date >= start && b.Date <= end)
            .OrderBy(b => b.Date)
            .ToArray();

    private static IReadOnlyList<PriceBar> ParseCsv(string csv)
    {
        // Expected header: Date,Open,High,Low,Close,Volume
        // Example row: 2025-12-31,195.23,197.12,193.80,194.50,53453453
        using var reader = new StringReader(csv);
        string? header = reader.ReadLine();
        if (header is null) throw new InvalidOperationException("Empty Stooq CSV response");

        var list = new List<PriceBar>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(',');
            if (parts.Length < 6) continue;

            // Some Stooq symbols may return "No data" rows; skip if date can't parse.
            if (!DateOnly.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                continue;

            // Stooq uses '.' decimal separator.
            if (!decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var open)) open = 0m;
            if (!decimal.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var high)) high = 0m;
            if (!decimal.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var low)) low = 0m;
            if (!decimal.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var close)) close = 0m;
            if (!long.TryParse(parts[5], NumberStyles.Any, CultureInfo.InvariantCulture, out var volume)) volume = 0L;

            list.Add(new PriceBar(date, open, high, low, close, volume));
        }

        // Normalize to ascending.
        list.Sort((a, b) => a.Date.CompareTo(b.Date));
        return list;
    }
}
