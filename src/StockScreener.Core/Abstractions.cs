namespace StockScreener.Core;

public interface IPriceDataProvider
{
    Task<IReadOnlyList<PriceBar>> GetDailyAsync(string ticker, DateOnly start, DateOnly end, CancellationToken ct = default);
}

public interface IOptionsDataProvider
{
    Task<OptionsSnapshot?> GetSnapshotAsync(string ticker, CancellationToken ct = default);
}

public interface IFundamentalsProvider
{
    Task<Fundamentals?> GetAsync(string ticker, CancellationToken ct = default);
}

public interface IMacroDataProvider
{
    Task<MacroSnapshot> GetSnapshotAsync(CancellationToken ct = default);
}

public record PriceBar(DateOnly Date, decimal Open, decimal High, decimal Low, decimal Close, long Volume);

public record OptionsSnapshot(
    decimal PutCallRatio,
    decimal ImpliedVolRank,
    decimal CallVolumeToAvg20d,
    decimal NearOtMCallOiDelta
);

public record Fundamentals(
    decimal Pe,
    decimal EvToEbitda,
    decimal FcfYield,
    decimal Pb,
    decimal Roic,
    decimal GrossMargin,
    decimal NetDebtToEbitda,
    string Sector
);

public record MacroSnapshot(
    decimal TenYearYield,
    decimal TwoTenSpread,
    decimal CpiYoY,
    decimal Pmi,
    decimal Dxy,
    decimal Wti
);

public record ScoringWeights(double Value, double Quality, double Momentum, double Options, double Macro);

public sealed class Score
{
    public double Value { get; init; }
    public double Quality { get; init; }
    public double Momentum { get; init; }
    public double Options { get; init; }
    public double Macro { get; init; }
    public double Total => Value + Quality + Momentum + Options + Macro;
}
