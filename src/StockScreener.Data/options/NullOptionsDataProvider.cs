using StockScreener.Core;

namespace StockScreener.Data;

/// <summary>
/// Placeholder options provider.
/// Returns null to indicate options data is unavailable.
/// </summary>
public sealed class NullOptionsDataProvider : IOptionsDataProvider
{
    public Task<OptionsSnapshot?> GetSnapshotAsync(string ticker, CancellationToken ct = default)
        => Task.FromResult<OptionsSnapshot?>(null);
}
