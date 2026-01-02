using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StockScreener.Core;

namespace StockScreener.Data.Options;

/// <summary>
/// Polygon.io options snapshot provider.
///
/// Notes:
/// - Requires an API key (set via Providers:PolygonApiKey / Providers__PolygonApiKey).
/// - Implements only a lightweight <see cref="OptionsSnapshot"/> (not full chain).
/// - Uses snapshot endpoints (delayed data depending on Polygon plan).
/// </summary>
public sealed class PolygonOptionsDataProvider(
    HttpClient http,
    IConfiguration config,
    IMemoryCache cache,
    ILogger<PolygonOptionsDataProvider> logger) : IOptionsDataProvider
{
    private const string Base = "https://api.polygon.io";

    public async Task<OptionsSnapshot?> GetSnapshotAsync(string ticker, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ticker)) throw new ArgumentException("Ticker is required", nameof(ticker));

        var apiKey = config["Providers:PolygonApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning("Polygon options requested but Providers:PolygonApiKey is missing");
            return null;
        }
        apiKey = apiKey.Trim();

        var underlying = ticker.Trim().ToUpperInvariant();

        // Cache per underlying to avoid repeatedly pulling large snapshots.
        var cacheKey = $"polygon:options:snapshot:{underlying}";
        if (cache.TryGetValue(cacheKey, out OptionsSnapshot? cached) && cached is not null)
            return cached;

        // Polygon snapshots API:
        // https://polygon.io/docs/options/get_v3_snapshot_options__underlyingasset
        // Example:
        //   /v3/snapshot/options/AAPL?limit=250&apiKey=...
        // We compute:
        // - PutCallRatio: total_put_volume / total_call_volume (fallback to 1 if missing)
        // - ImpliedVolRank: not directly available on all plans; we return 0 when not derivable
        // - CallVolumeToAvg20d: not available; return 0
        // - NearOtMCallOiDelta: not available; return 0
        var url = $"{Base}/v3/snapshot/options/{Uri.EscapeDataString(underlying)}?limit=250&apiKey={Uri.EscapeDataString(apiKey)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            var trimmed = body.Length > 2000 ? body[..2000] + "…(truncated)" : body;

            logger.LogWarning(
                "Polygon options request failed: {StatusCode} {ReasonPhrase}. Body: {Body}",
                (int)resp.StatusCode,
                resp.ReasonPhrase,
                trimmed
            );

            // Common real-world failure: a valid key without entitlements for this endpoint.
            // In that case, degrade gracefully so callers can fall back or show "no data".
            if (resp.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
            {
                // If it's some other auth-ish failure, still treat it as non-fatal for the app.
                return null;
            }

            // Unexpected non-success: keep failing fast.
            resp.EnsureSuccessStatusCode();
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            logger.LogWarning("Polygon options snapshot returned no 'results' array for {Ticker}", underlying);
            return null;
        }

        decimal callVol = 0m;
        decimal putVol = 0m;

        foreach (var item in results.EnumerateArray())
        {
            // Shape varies slightly; we defensively probe known locations.
            // Prefer: item.day.volume or item.session.volume (if present), else skip.
            var contractType = GetString(item, "details", "contract_type")?.ToLowerInvariant();
            if (contractType is not ("call" or "put"))
                continue;

            var vol = GetDecimal(item, "day", "volume")
                      ?? GetDecimal(item, "session", "volume")
                      ?? 0m;

            if (contractType == "call") callVol += vol;
            else putVol += vol;
        }

        var pcr = callVol > 0m ? (putVol / callVol) : 1m;

        // Placeholders until we decide how to model full chain + history for ranking.
        var snapshot = new OptionsSnapshot(
            PutCallRatio: pcr,
            ImpliedVolRank: 0m,
            CallVolumeToAvg20d: 0m,
            NearOtMCallOiDelta: 0m
        );

        cache.Set(cacheKey, snapshot, TimeSpan.FromMinutes(30));
        logger.LogInformation("Fetched Polygon options snapshot for {Ticker} (callsVol={CallsVol}, putsVol={PutsVol})", underlying, callVol, putVol);

        return snapshot;
    }

    private static string? GetString(JsonElement root, string obj, string prop)
    {
        if (!root.TryGetProperty(obj, out var o) || o.ValueKind != JsonValueKind.Object)
            return null;
        if (!o.TryGetProperty(prop, out var p))
            return null;
        return p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString();
    }

    private static decimal? GetDecimal(JsonElement root, string obj, string prop)
    {
        if (!root.TryGetProperty(obj, out var o) || o.ValueKind != JsonValueKind.Object)
            return null;
        if (!o.TryGetProperty(prop, out var p))
            return null;

        return p.ValueKind switch
        {
            JsonValueKind.Number => p.TryGetDecimal(out var d) ? d : (decimal?)p.GetDouble(),
            JsonValueKind.String => decimal.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null,
            _ => null
        };
    }
}
