using System.Globalization;
using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StockScreener.Core;

namespace StockScreener.Data;

/// <summary>
/// Macro snapshot built from FRED (Federal Reserve Economic Data) series.
/// Requires an API key (Providers:FredApiKey).
///
/// Uses a few common series:
/// - 10Y Treasury yield: DGS10
/// - 2Y Treasury yield: DGS2 (used to compute 2s10s spread)
/// - CPI YoY: CPIAUCSL (computed from monthly index)
/// - PMI: NAPM
/// - DXY: DTWEXBGS
/// - WTI: DCOILWTICO
/// </summary>
public sealed class FredMacroDataProvider(
    HttpClient http,
    IMemoryCache cache,
    IConfiguration config,
    ILogger<FredMacroDataProvider> logger)
    : IMacroDataProvider
{
    private const string Base = "https://api.stlouisfed.org";

    public async Task<MacroSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var apiKey = config["Providers:FredApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Missing Providers:FredApiKey in configuration");

        apiKey = apiKey.Trim();

        const string cacheKey = "fred:macro:snapshot";
        if (cache.TryGetValue(cacheKey, out MacroSnapshot? cached) && cached is not null)
            return cached;

        // Pull series independently (keeps code simple; can be optimized/batched later).
        var dgs10 = await GetLatestValueAsync("DGS10", apiKey, ct);
        var dgs2 = await GetLatestValueAsync("DGS2", apiKey, ct);
        var twoTenSpread = (dgs10.HasValue && dgs2.HasValue) ? (dgs10.Value - dgs2.Value) : 0m;

        var cpiYoY = await GetCpiYoYAsync(apiKey, ct);
        var pmi = await GetLatestValueAsync("NAPM", apiKey, ct) ?? 0m;
        var dxy = await GetLatestValueAsync("DTWEXBGS", apiKey, ct) ?? 0m;
        var wti = await GetLatestValueAsync("DCOILWTICO", apiKey, ct) ?? 0m;

        var snap = new MacroSnapshot(
            TenYearYield: dgs10 ?? 0m,
            TwoTenSpread: twoTenSpread,
            CpiYoY: cpiYoY,
            Pmi: pmi,
            Dxy: dxy,
            Wti: wti
        );

        cache.Set(cacheKey, snap, TimeSpan.FromHours(6));
        logger.LogInformation("Fetched macro snapshot from FRED");
        return snap;
    }

    private async Task<decimal?> GetLatestValueAsync(string seriesId, string apiKey, CancellationToken ct)
    {
        var url = $"{Base}/fred/series/observations?series_id={Uri.EscapeDataString(seriesId)}&api_key={Uri.EscapeDataString(apiKey)}&file_type=json&sort_order=desc&limit=10";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var root = await resp.Content.ReadFromJsonAsync<FredObservationsResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty FRED response");

        // FRED can return "." for missing values.
        foreach (var obs in root.Observations)
        {
            if (TryParseDecimal(obs.Value, out var d))
                return d;
        }

        return null;
    }

    private async Task<decimal> GetCpiYoYAsync(string apiKey, CancellationToken ct)
    {
        // CPIAUCSL is a monthly index level. Compute YoY from latest vs 12 months prior.
        var url = $"{Base}/fred/series/observations?series_id=CPIAUCSL&api_key={Uri.EscapeDataString(apiKey)}&file_type=json&sort_order=desc&limit=24";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var root = await resp.Content.ReadFromJsonAsync<FredObservationsResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty FRED response");

        // Observations are in descending order due to sort_order=desc.
        var parsed = root.Observations
            .Select(o => TryParseDecimal(o.Value, out var d) ? (decimal?)d : null)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .Take(13)
            .ToArray();

        if (parsed.Length < 13) return 0m;

        var latest = parsed[0];
        var prior = parsed[12];
        if (prior <= 0m) return 0m;

        return (latest / prior) - 1m;
    }

    private static bool TryParseDecimal(string? s, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(s) || s == ".") return false;
        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    private sealed class FredObservationsResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("observations")]
        public FredObservation[] Observations { get; set; } = Array.Empty<FredObservation>();
    }

    private sealed class FredObservation
    {
        [System.Text.Json.Serialization.JsonPropertyName("value")]
        public string? Value { get; set; }
    }
}
