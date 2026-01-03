using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StockScreener.Core;

namespace StockScreener.Data;

/// <summary>
/// Fundamentals from Alpha Vantage "OVERVIEW" endpoint.
/// NOTE: Free tier is rate-limited.
/// </summary>
public sealed class AlphaVantageFundamentalsProvider(
    HttpClient http,
    IMemoryCache cache,
    IConfiguration config,
    ILogger<AlphaVantageFundamentalsProvider> logger) : IFundamentalsProvider
{
    private const string Base = "https://www.alphavantage.co";

    public async Task<Fundamentals?> GetAsync(string ticker, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ticker)) throw new ArgumentException("Ticker is required", nameof(ticker));

        var apiKey = config["Providers:AlphaVantageApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Missing Providers:AlphaVantageApiKey in configuration");

        var t = ticker.Trim().ToUpperInvariant();
        var cacheKey = $"av:overview:{t}";
        if (cache.TryGetValue(cacheKey, out Fundamentals? cached) && cached is not null)
            return cached;

        var url = $"{Base}/query?function=OVERVIEW&symbol={Uri.EscapeDataString(t)}&apikey={Uri.EscapeDataString(apiKey)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        // Read the raw JSON first so we can emit useful diagnostics if parsing succeeds but Symbol is empty.
        var rawJson = await resp.Content.ReadAsStringAsync(ct);

        OverviewResponse? root = null;
        try
        {
            root = System.Text.Json.JsonSerializer.Deserialize<OverviewResponse>(rawJson);
        }
        catch (Exception ex)
        {
            // Avoid logging secrets: do NOT log the full URL (contains apikey).
            logger.LogWarning(ex, "Failed to parse AlphaVantage fundamentals JSON for {Ticker}", t);
            throw;
        }

        if (root is null)
            throw new InvalidOperationException("Empty Alpha Vantage response");

        if (!string.IsNullOrWhiteSpace(root.Note))
            throw new InvalidOperationException($"Alpha Vantage throttled: {root.Note}");

        if (!string.IsNullOrWhiteSpace(root.ErrorMessage))
            throw new InvalidOperationException($"Alpha Vantage error: {root.ErrorMessage}");

        if (!string.IsNullOrWhiteSpace(root.Information))
            throw new InvalidOperationException($"Alpha Vantage throttled: {root.Information}");

        // Alpha Vantage returns an empty object for unknown symbols.
        if (string.IsNullOrWhiteSpace(root.Symbol))
        {
            // Log a small snippet (trimmed) for debugging. Bodies do not contain the API key.
            var snippet = rawJson.Length <= 600 ? rawJson : rawJson[..600] + "...";
            logger.LogWarning(
                "AlphaVantage fundamentals missing Symbol for {Ticker}. Body snippet: {Snippet}",
                t,
                snippet);
            return null;
        }

        // Map into our simplified Fundamentals record.
        // Many fields may be missing; we fall back to reasonable defaults.
        var pe = ParseDec(root.PERatio, 0m);
        var pb = ParseDec(root.PriceToBookRatio, 0m);
        var evToEbitda = ParseDec(root.EVToEBITDA, 0m);

        // FCF yield is not directly returned; approximate from FCF/MarketCap if both exist.
        var fcf = ParseDec(root.FreeCashFlowTTM, 0m);
        var mcap = ParseDec(root.MarketCapitalization, 0m);
        var fcfYield = (fcf > 0m && mcap > 0m) ? (fcf / mcap) : 0m;

        // ROIC isn't directly returned; approximate using ReturnOnEquityTTM as a placeholder.
        var roic = ParseDec(root.ReturnOnEquityTTM, 0m);

        // Gross margin not directly returned; approximate from ProfitMargin if present.
        var grossMargin = ParseDec(root.ProfitMargin, 0m);

        // NetDebtToEbitda not available; leave as 0.
        var netDebtToEbitda = 0m;

        var sector = root.Sector ?? "Unknown";

        var fundamentals = new Fundamentals(
            Pe: pe,
            EvToEbitda: evToEbitda,
            FcfYield: fcfYield,
            Pb: pb,
            Roic: roic,
            GrossMargin: grossMargin,
            NetDebtToEbitda: netDebtToEbitda,
            Sector: sector
        );

        cache.Set(cacheKey, fundamentals, TimeSpan.FromHours(12));
        logger.LogInformation("Fetched AlphaVantage fundamentals for {Ticker}", t);

        return fundamentals;
    }

    private static decimal ParseDec(string? s, decimal fallback)
        => decimal.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : fallback;

    private sealed class OverviewResponse
    {
        public string? Note { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("Error Message")]
        public string? ErrorMessage { get; set; }

        public string? Information { get; set; }

        public string? Symbol { get; set; }
        public string? Sector { get; set; }

        public string? PERatio { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("PriceToBookRatio")]
        public string? PriceToBookRatio { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("EVToEBITDA")]
        public string? EVToEBITDA { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("MarketCapitalization")]
        public string? MarketCapitalization { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("FreeCashFlowTTM")]
        public string? FreeCashFlowTTM { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("ReturnOnEquityTTM")]
        public string? ReturnOnEquityTTM { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("ProfitMargin")]
        public string? ProfitMargin { get; set; }
    }
}
