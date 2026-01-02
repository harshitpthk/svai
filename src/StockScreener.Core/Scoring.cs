using System.Collections.Immutable;

namespace StockScreener.Core;

public static class Scoring
{
    public sealed record ScoreBreakdown(
        double ValueRaw,
        double QualityRaw,
        double MomentumRaw,
        double OptionsRaw,
        double MacroRaw,
        ScoringWeights Weights,
        Score WeightedScore
    )
    {
        public double TotalRaw => ValueRaw + QualityRaw + MomentumRaw + OptionsRaw + MacroRaw;
    }

    public static ScoreBreakdown Explain(
        Fundamentals f,
        IReadOnlyList<PriceBar> prices,
        OptionsSnapshot? opt,
        MacroSnapshot macro,
        ScoringWeights w)
    {
        // Mirror Compute() logic so we can show the unweighted components.
        double value = (double)(f.FcfYield - f.Pe / 100m - f.Pb / 10m - f.EvToEbitda / 20m);
        double quality = (double)(f.Roic / 10m + f.GrossMargin / 50m - f.NetDebtToEbitda / 5m);
        double momentum = RecentMomentum(prices);
        double options = opt is null ? 0 : (
            (double)(1m - opt.PutCallRatio) +
            (opt.ImpliedVolRank is >= 0 and <= 100 ? 1 - Math.Abs((double)opt.ImpliedVolRank - 40) / 60 : 0) +
            (double)(opt.CallVolumeToAvg20d - 1) +
            (double)opt.NearOtMCallOiDelta);
        double macroFit = MacroSectorTilt(f.Sector, macro);

        var weighted = new Score
        {
            Value = value * w.Value,
            Quality = quality * w.Quality,
            Momentum = momentum * w.Momentum,
            Options = options * w.Options,
            Macro = macroFit * w.Macro
        };

        return new ScoreBreakdown(
            ValueRaw: value,
            QualityRaw: quality,
            MomentumRaw: momentum,
            OptionsRaw: options,
            MacroRaw: macroFit,
            Weights: w,
            WeightedScore: weighted
        );
    }

    public static Score Compute(
        Fundamentals f,
        IReadOnlyList<PriceBar> prices,
        OptionsSnapshot? opt,
        MacroSnapshot macro,
        ScoringWeights w)
    {
        // Very naive initial scoring to make the project compile.
        // TODO: replace with sector-normalized z-scores and robust factor calculations.
        double value = (double)(f.FcfYield - f.Pe / 100m - f.Pb / 10m - f.EvToEbitda / 20m);
        double quality = (double)(f.Roic / 10m + f.GrossMargin / 50m - f.NetDebtToEbitda / 5m);
        double momentum = RecentMomentum(prices);
        double options = opt is null ? 0 : (
            (double)(1m - opt.PutCallRatio) +
            (opt.ImpliedVolRank is >= 0 and <= 100 ? 1 - Math.Abs((double)opt.ImpliedVolRank - 40) / 60 : 0) +
            (double)(opt.CallVolumeToAvg20d - 1) +
            (double)opt.NearOtMCallOiDelta);
        double macroFit = MacroSectorTilt(f.Sector, macro);

        return new Score
        {
            Value = value * w.Value,
            Quality = quality * w.Quality,
            Momentum = momentum * w.Momentum,
            Options = options * w.Options,
            Macro = macroFit * w.Macro
        };
    }

    private static double RecentMomentum(IReadOnlyList<PriceBar> prices)
    {
        if (prices.Count < 21) return 0;
        var last = prices[^1].Close;
        var prev = prices[^21].Close;
        if (prev <= 0) return 0;
        return (double)((last - prev) / prev);
    }

    private static double MacroSectorTilt(string sector, MacroSnapshot m)
    {
        // Toy heuristic: favor cyclicals if PMI rising and oil strong; favor defensives otherwise
        double tilt = 0;
        if (m.Pmi > 50) tilt += 0.2;
        if (m.Wti > 60) tilt += 0.1;
        if (m.TwoTenSpread > 0) tilt += 0.1; else tilt -= 0.05;
        if (m.CpiYoY < 3) tilt += 0.05; else tilt -= 0.05;

        return sector.ToLowerInvariant() switch
        {
            "energy" or "materials" or "industrials" => tilt,
            "utilities" or "staples" => -tilt / 2,
            _ => 0
        };
    }
}
