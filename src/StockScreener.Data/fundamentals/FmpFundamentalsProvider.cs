using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StockScreener.Core;

namespace StockScreener.Data;

/// <summary>
/// Fundamentals from Financial Modeling Prep (FMP).
/// Uses the /stable/profile and /stable/ratios-ttm endpoints.
/// Free tier: 250 requests/day — much more generous than AlphaVantage.
/// Config key: Providers:FmpApiKey
/// </summary>
public sealed class FmpFundamentalsProvider(
    HttpClient http,
    IMemoryCache cache,
    IConfiguration config,
    ILogger<FmpFundamentalsProvider> logger) : IFundamentalsProvider
{
    private const string Base = "https://financialmodelingprep.com/stable";

    public async Task<Fundamentals?> GetAsync(string ticker, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ticker))
            throw new ArgumentException("Ticker is required", nameof(ticker));

        var apiKey = config["Providers:FmpApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Missing Providers:FmpApiKey in configuration");

        var t = ticker.Trim().ToUpperInvariant();
        var cacheKey = $"fmp:fundamentals:{t}";
        if (cache.TryGetValue(cacheKey, out Fundamentals? cached) && cached is not null)
            return cached;

        // 1) Fetch company profile (PE, PB, sector, market cap)
        var profileUrl = $"{Base}/profile?symbol={Uri.EscapeDataString(t)}&apikey={Uri.EscapeDataString(apiKey)}";
        var profiles = await FetchAsync<FmpProfile[]>(profileUrl, ct);
        if (profiles is null || profiles.Length == 0)
        {
            logger.LogWarning("FMP returned no profile for {Ticker}", t);
            return null;
        }
        var profile = profiles[0];

        // 2) Fetch TTM key metrics (ROIC, EV/EBITDA, FCF yield, net debt/EBITDA)
        //    ratios-ttm may be gated on some plans; key-metrics-ttm is more broadly available.
        var metricsUrl = $"{Base}/key-metrics-ttm?symbol={Uri.EscapeDataString(t)}&apikey={Uri.EscapeDataString(apiKey)}";
        var metrics = await FetchAsync<FmpKeyMetricsTtm[]>(metricsUrl, ct);
        var metric = metrics?.FirstOrDefault();

        // 3) Optionally try ratios-ttm for gross margin / ROIC if key-metrics doesn't have them.
        var ratiosUrl = $"{Base}/ratios-ttm?symbol={Uri.EscapeDataString(t)}&apikey={Uri.EscapeDataString(apiKey)}";
        var ratioArr = await FetchAsync<FmpRatiosTtm[]>(ratiosUrl, ct);
        var ratio = ratioArr?.FirstOrDefault();

        // Build the Fundamentals record, preferring key-metrics-ttm, falling back to ratios-ttm, then profile.
        var pe = SafeDec(profile.Pe);
        var pb = SafeDec(profile.PriceToBookRatio);
        var sector = string.IsNullOrWhiteSpace(profile.Sector) ? "Unknown" : profile.Sector;

        var evToEbitda = PickFirst(metric?.EnterpriseValueOverEBITDA, ratio?.EnterpriseValueOverEBITDA);
        var fcfYield = PickFirst(metric?.FreeCashFlowYield, ratio?.FreeCashFlowYield);
        var roic = PickFirst(ratio?.ReturnOnInvestedCapital, metric?.Roic);
        var grossMargin = PickFirst(ratio?.GrossProfitMargin, profile.GrossProfitMargin);
        var netDebtToEbitda = PickFirst(metric?.NetDebtToEBITDA, ratio?.NetDebtToEBITDA);

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
        logger.LogInformation("Fetched FMP fundamentals for {Ticker} (PE={Pe}, ROIC={Roic}, Sector={Sector})", t, pe, roic, sector);

        return fundamentals;
    }

    private async Task<T?> FetchAsync<T>(string url, CancellationToken ct) where T : class
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await http.SendAsync(req, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            logger.LogWarning("FMP returned {StatusCode} for {Url}: {Body}",
                (int)resp.StatusCode,
                url.Split('?')[0], // avoid logging the API key in query params
                body.Length <= 500 ? body : body[..500]);
            return null;
        }

        return await resp.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
    }

    private static decimal SafeDec(double? v) => v.HasValue && !double.IsNaN(v.Value) && !double.IsInfinity(v.Value)
        ? (decimal)v.Value
        : 0m;

    /// <summary>Pick the first non-zero/non-null value from multiple nullable sources.</summary>
    private static decimal PickFirst(params double?[] sources)
    {
        foreach (var s in sources)
        {
            var d = SafeDec(s);
            if (d != 0m) return d;
        }
        return 0m;
    }

    // ── FMP response DTOs ──────────────────────────────────────────────

    private sealed class FmpProfile
    {
        [JsonPropertyName("pe")]
        public double? Pe { get; set; }

        [JsonPropertyName("priceToBookRatio")]
        public double? PriceToBookRatio { get; set; }

        [JsonPropertyName("sector")]
        public string? Sector { get; set; }

        [JsonPropertyName("mktCap")]
        public double? MarketCap { get; set; }

        [JsonPropertyName("grossProfitMargin")]
        public double? GrossProfitMargin { get; set; }
    }

    private sealed class FmpRatiosTtm
    {
        [JsonPropertyName("returnOnInvestedCapitalTTM")]
        public double? ReturnOnInvestedCapital { get; set; }

        [JsonPropertyName("grossProfitMarginTTM")]
        public double? GrossProfitMargin { get; set; }

        [JsonPropertyName("freeCashFlowYieldTTM")]
        public double? FreeCashFlowYield { get; set; }

        [JsonPropertyName("enterpriseValueOverEBITDATTM")]
        public double? EnterpriseValueOverEBITDA { get; set; }

        [JsonPropertyName("netDebtToEBITDATTM")]
        public double? NetDebtToEBITDA { get; set; }
    }

    private sealed class FmpKeyMetricsTtm
    {
        [JsonPropertyName("enterpriseValueOverEBITDATTM")]
        public double? EnterpriseValueOverEBITDA { get; set; }

        [JsonPropertyName("freeCashFlowYieldTTM")]
        public double? FreeCashFlowYield { get; set; }

        [JsonPropertyName("netDebtToEBITDATTM")]
        public double? NetDebtToEBITDA { get; set; }

        [JsonPropertyName("roicTTM")]
        public double? Roic { get; set; }
    }
}
