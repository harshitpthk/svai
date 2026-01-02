using Microsoft.Extensions.Configuration;
using StockScreener.Core;

namespace StockScreener.Data;

/// <summary>
/// Starter fundamentals provider backed by configuration.
/// Supports per-ticker overrides under Fundamentals:{TICKER}:* and global defaults under Fundamentals:Default:*.
///
/// This is a placeholder until a real fundamentals data source is integrated.
/// </summary>
public sealed class ConfigFundamentalsProvider(IConfiguration config) : IFundamentalsProvider
{
    public Task<Fundamentals?> GetAsync(string ticker, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ticker)) throw new ArgumentException("Ticker is required", nameof(ticker));
        var t = ticker.Trim().ToUpperInvariant();

        // Allow per-ticker override, else fall back to defaults.
        var prefix = $"Fundamentals:{t}:";
        var dprefix = "Fundamentals:Default:";

        var f = new Fundamentals(
            Pe: GetDec(prefix + "Pe", GetDec(dprefix + "Pe", 18m)),
            EvToEbitda: GetDec(prefix + "EvToEbitda", GetDec(dprefix + "EvToEbitda", 12m)),
            FcfYield: GetDec(prefix + "FcfYield", GetDec(dprefix + "FcfYield", 0.04m)),
            Pb: GetDec(prefix + "Pb", GetDec(dprefix + "Pb", 3m)),
            Roic: GetDec(prefix + "Roic", GetDec(dprefix + "Roic", 0.12m)),
            GrossMargin: GetDec(prefix + "GrossMargin", GetDec(dprefix + "GrossMargin", 0.45m)),
            NetDebtToEbitda: GetDec(prefix + "NetDebtToEbitda", GetDec(dprefix + "NetDebtToEbitda", 1.5m)),
            Sector: GetStr(prefix + "Sector", GetStr(dprefix + "Sector", "Technology"))
        );

        return Task.FromResult<Fundamentals?>(f);
    }

    private decimal GetDec(string key, decimal fallback)
    {
        var v = config[key];
        return decimal.TryParse(v, out var d) ? d : fallback;
    }

    private string GetStr(string key, string fallback)
        => string.IsNullOrWhiteSpace(config[key]) ? fallback : config[key]!;
}
