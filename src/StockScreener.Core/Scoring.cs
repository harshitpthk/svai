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

    /// <summary>
    /// Rescore an in-memory result set using per-run z-scores (global or sector).
    /// This keeps options/macro logic as-is, and only normalizes Value/Quality/Momentum inputs.
    /// </summary>
    public static IReadOnlyList<ScreenResult> NormalizeAndRescore(
        IReadOnlyList<ScreenResult> results,
        ScoringWeights w,
        NormalizationMode mode,
        int minGroupSizeForSector = 5)
    {
        if (results.Count <= 1) return results;
        if (mode == NormalizationMode.None) return results;

        // Extract per-ticker features.
        var feats = results
            .Select(r => new Features(
                Ticker: r.Ticker,
                Sector: r.Fundamentals.Sector,
                Pe: (double)r.Fundamentals.Pe,
                EvToEbitda: (double)r.Fundamentals.EvToEbitda,
                FcfYield: (double)r.Fundamentals.FcfYield,
                Pb: (double)r.Fundamentals.Pb,
                Roic: (double)r.Fundamentals.Roic,
                GrossMargin: (double)r.Fundamentals.GrossMargin,
                NetDebtToEbitda: (double)r.Fundamentals.NetDebtToEbitda,
                Momentum20d: RecentMomentum(r.Prices)))
            .ToDictionary(x => x.Ticker, StringComparer.OrdinalIgnoreCase);

        // Global stats (fallback + for momentum).
        var global = StatsPack.From(feats.Values);

        // Sector stats (only if requested).
        Dictionary<string, StatsPack>? bySector = null;
        if (mode == NormalizationMode.SectorZScore)
        {
            bySector = feats.Values
                .GroupBy(f => (f.Sector ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => StatsPack.From(g),
                    StringComparer.OrdinalIgnoreCase);
        }

        // Produce rescored results, keeping all other fields.
        var rescored = new List<ScreenResult>(results.Count);

        foreach (var r in results)
        {
            var f = feats[r.Ticker];

            var pack = global;
            if (mode == NormalizationMode.SectorZScore)
            {
                var key = (f.Sector ?? string.Empty).Trim();
                if (bySector is not null && bySector.TryGetValue(key, out var sectorPack))
                {
                    pack = sectorPack.Count >= minGroupSizeForSector ? sectorPack : global;
                }
            }

            // Z-scores (clamped). For "lower is better" metrics, invert sign.
            var zPe = pack.ZPe(f.Pe);
            var zEv = pack.ZEvToEbitda(f.EvToEbitda);
            var zFcf = pack.ZFcfYield(f.FcfYield);
            var zPb = pack.ZPb(f.Pb);

            var zRoic = pack.ZRoic(f.Roic);
            var zMargin = pack.ZGrossMargin(f.GrossMargin);
            var zLeverage = pack.ZNetDebtToEbitda(f.NetDebtToEbitda);

            var zMom = global.ZMomentum20d(f.Momentum20d);

            // Factor aggregates.
            var valueRaw = Clamp(Avg(zFcf, -zPe, -zEv, -zPb));
            var qualityRaw = Clamp(Avg(zRoic, zMargin, -zLeverage));
            var momentumRaw = Clamp(zMom);

            // Preserve original options/macro raw logic.
            var opt = r.Options;
            var optionsRaw = opt is null ? 0 : (
                (double)(1m - opt.PutCallRatio) +
                (opt.ImpliedVolRank is >= 0 and <= 100 ? 1 - Math.Abs((double)opt.ImpliedVolRank - 40) / 60 : 0) +
                (double)(opt.CallVolumeToAvg20d - 1) +
                (double)opt.NearOtMCallOiDelta);

            var macroRaw = MacroSectorTilt(r.Fundamentals.Sector, r.Macro);

            var score = new Score
            {
                Value = valueRaw * w.Value,
                Quality = qualityRaw * w.Quality,
                Momentum = momentumRaw * w.Momentum,
                Options = optionsRaw * w.Options,
                Macro = macroRaw * w.Macro
            };

            rescored.Add(new ScreenResult
            {
                Ticker = r.Ticker,
                Fundamentals = r.Fundamentals,
                Prices = r.Prices,
                Macro = r.Macro,
                Options = r.Options,
                Score = score
            });
        }

        return rescored;
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

    private static double Avg(params double[] xs)
    {
        double sum = 0;
        var n = 0;
        foreach (var x in xs)
        {
            if (double.IsNaN(x) || double.IsInfinity(x)) continue;
            sum += x;
            n++;
        }
        return n == 0 ? 0 : sum / n;
    }

    private static double Clamp(double z, double lo = -3, double hi = 3)
        => z < lo ? lo : (z > hi ? hi : z);

    private sealed record Features(
        string Ticker,
        string Sector,
        double Pe,
        double EvToEbitda,
        double FcfYield,
        double Pb,
        double Roic,
        double GrossMargin,
        double NetDebtToEbitda,
        double Momentum20d);

    private sealed class StatsPack
    {
        public int Count { get; init; }

        private Stats Pe { get; init; }
        private Stats EvToEbitda { get; init; }
        private Stats FcfYield { get; init; }
        private Stats Pb { get; init; }
        private Stats Roic { get; init; }
        private Stats GrossMargin { get; init; }
        private Stats NetDebtToEbitda { get; init; }
        private Stats Momentum20d { get; init; }

        public static StatsPack From(IEnumerable<Features> items)
        {
            var list = items.ToList();
            return new StatsPack
            {
                Count = list.Count,
                Pe = Stats.From(list.Select(x => x.Pe)),
                EvToEbitda = Stats.From(list.Select(x => x.EvToEbitda)),
                FcfYield = Stats.From(list.Select(x => x.FcfYield)),
                Pb = Stats.From(list.Select(x => x.Pb)),
                Roic = Stats.From(list.Select(x => x.Roic)),
                GrossMargin = Stats.From(list.Select(x => x.GrossMargin)),
                NetDebtToEbitda = Stats.From(list.Select(x => x.NetDebtToEbitda)),
                Momentum20d = Stats.From(list.Select(x => x.Momentum20d))
            };
        }

        public double ZPe(double x) => Clamp(Pe.Z(x));
        public double ZEvToEbitda(double x) => Clamp(EvToEbitda.Z(x));
        public double ZFcfYield(double x) => Clamp(FcfYield.Z(x));
        public double ZPb(double x) => Clamp(Pb.Z(x));
        public double ZRoic(double x) => Clamp(Roic.Z(x));
        public double ZGrossMargin(double x) => Clamp(GrossMargin.Z(x));
        public double ZNetDebtToEbitda(double x) => Clamp(NetDebtToEbitda.Z(x));
        public double ZMomentum20d(double x) => Clamp(Momentum20d.Z(x));
    }

    private readonly struct Stats
    {
        public double Mean { get; }
        public double StdDev { get; }

        private Stats(double mean, double stdDev)
        {
            Mean = mean;
            StdDev = stdDev;
        }

        public static Stats From(IEnumerable<double> xs)
        {
            var vals = xs
                .Where(x => !double.IsNaN(x) && !double.IsInfinity(x))
                .ToArray();

            if (vals.Length == 0) return new Stats(0, 0);
            if (vals.Length == 1) return new Stats(vals[0], 0);

            var mean = vals.Average();
            double sumSq = 0;
            for (var i = 0; i < vals.Length; i++)
            {
                var d = vals[i] - mean;
                sumSq += d * d;
            }

            // Population stddev (N).
            var std = Math.Sqrt(sumSq / vals.Length);
            return new Stats(mean, std);
        }

        public double Z(double x)
        {
            if (StdDev <= 1e-12) return 0;
            return (x - Mean) / StdDev;
        }
    }
}
