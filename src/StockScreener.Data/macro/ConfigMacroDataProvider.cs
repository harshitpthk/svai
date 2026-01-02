using Microsoft.Extensions.Configuration;
using StockScreener.Core;

namespace StockScreener.Data;

/// <summary>
/// Simple macro provider backed by configuration.
/// This is intentionally a "starter" provider so the screener can run end-to-end
/// without depending on external macro APIs.
/// </summary>
public sealed class ConfigMacroDataProvider(IConfiguration config) : IMacroDataProvider
{
    public Task<MacroSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        // Defaults are intentionally reasonable-ish placeholders.
        var s = new MacroSnapshot(
            TenYearYield: GetDec("Macro:TenYearYield", 4.0m),
            TwoTenSpread: GetDec("Macro:TwoTenSpread", 0.5m),
            CpiYoY: GetDec("Macro:CpiYoY", 3.0m),
            Pmi: GetDec("Macro:Pmi", 50.0m),
            Dxy: GetDec("Macro:Dxy", 100.0m),
            Wti: GetDec("Macro:Wti", 70.0m)
        );

        return Task.FromResult(s);
    }

    private decimal GetDec(string key, decimal fallback)
    {
        var v = config[key];
        return decimal.TryParse(v, out var d) ? d : fallback;
    }
}
