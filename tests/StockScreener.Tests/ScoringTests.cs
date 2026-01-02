using FluentAssertions;
using StockScreener.Core;

namespace StockScreener.Tests;

public class ScoringTests
{
    private static Fundamentals MakeFundamentals(
        decimal pe = 20m,
        decimal fcfYield = 0.05m,
        decimal roic = 0.15m,
        decimal netDebtToEbitda = 1.0m,
        string sector = "TECHNOLOGY")
        => new(
            Pe: pe,
            EvToEbitda: 12m,
            FcfYield: fcfYield,
            Pb: 3m,
            Roic: roic,
            GrossMargin: 0.4m,
            NetDebtToEbitda: netDebtToEbitda,
            Sector: sector);

    private static IReadOnlyList<PriceBar> MakePrices(int days = 30, decimal start = 100m, decimal end = 110m)
    {
        // Creates monotonically increasing (or decreasing) closes so momentum is deterministic.
        var bars = new List<PriceBar>(days);
        var today = DateOnly.FromDateTime(DateTime.Today);

        var step = days <= 1 ? 0m : (end - start) / (days - 1);
        for (var i = 0; i < days; i++)
        {
            var close = start + step * i;
            bars.Add(new PriceBar(
                Date: today.AddDays(-(days - 1 - i)),
                Open: close,
                High: close,
                Low: close,
                Close: close,
                Volume: 1));
        }

        return bars;
    }

    private static MacroSnapshot NeutralMacro()
        => new(
            TenYearYield: 0m,
            TwoTenSpread: 0m,
            CpiYoY: 3m,
            Pmi: 50m,
            Dxy: 100m,
            Wti: 0m);

    [Fact]
    public void ScreenFilters_WhenAllNull_ShouldMatch()
    {
        var f = MakeFundamentals();
        var p = MakePrices();

        var filters = new ScreenFilters();
        filters.Matches(f, p).Should().BeTrue();
    }

    [Fact]
    public void ScreenFilters_MinFcfYield_ShouldFilterOutLowerYield()
    {
        var f = MakeFundamentals(fcfYield: 0.03m);
        var p = MakePrices();

        var filters = new ScreenFilters(MinFcfYield: 0.04m);
        filters.Matches(f, p).Should().BeFalse();
    }

    [Fact]
    public void ScreenFilters_MaxPe_ShouldFilterOutHigherPe()
    {
        var f = MakeFundamentals(pe: 35m);
        var p = MakePrices();

        var filters = new ScreenFilters(MaxPe: 30m);
        filters.Matches(f, p).Should().BeFalse();
    }

    [Fact]
    public void ScreenFilters_MinRoic_ShouldFilterOutLowerRoic()
    {
        var f = MakeFundamentals(roic: 0.05m);
        var p = MakePrices();

        var filters = new ScreenFilters(MinRoic: 0.10m);
        filters.Matches(f, p).Should().BeFalse();
    }

    [Fact]
    public void ScreenFilters_MaxNetDebtToEbitda_ShouldFilterOutHigherLeverage()
    {
        var f = MakeFundamentals(netDebtToEbitda: 4m);
        var p = MakePrices();

        var filters = new ScreenFilters(MaxNetDebtToEbitda: 3m);
        filters.Matches(f, p).Should().BeFalse();
    }

    [Fact]
    public void ScreenFilters_MinMomentum_ShouldFilterOutWhenTooFewBars()
    {
        var f = MakeFundamentals();
        var p = MakePrices(days: 10, start: 100m, end: 120m);

        var filters = new ScreenFilters(MinMomentum: 0.0);
        filters.Matches(f, p).Should().BeFalse();
    }

    [Fact]
    public void ScreenFilters_MinMomentum_ShouldFilterOutNegativeMomentum()
    {
        var f = MakeFundamentals();
        var p = MakePrices(days: 30, start: 110m, end: 100m); // downtrend

        var filters = new ScreenFilters(MinMomentum: 0.0);
        filters.Matches(f, p).Should().BeFalse();
    }

    [Fact]
    public void ScreenFilters_MinMomentum_ShouldPassPositiveMomentum()
    {
        var f = MakeFundamentals();
        var p = MakePrices(days: 30, start: 100m, end: 110m); // uptrend

        var filters = new ScreenFilters(MinMomentum: 0.0);
        filters.Matches(f, p).Should().BeTrue();
    }

    [Fact]
    public void Scoring_ExplainAndCompute_ShouldAgreeOnWeightedScore()
    {
        var f = MakeFundamentals(sector: "ENERGY");
        var p = MakePrices(days: 30, start: 100m, end: 110m);
        var opt = (OptionsSnapshot?)null;
        var macro = NeutralMacro();
        var w = new ScoringWeights(Value: 0.4, Quality: 0.2, Momentum: 0.15, Options: 0.15, Macro: 0.1);

        var score = Scoring.Compute(f, p, opt, macro, w);
        var breakdown = Scoring.Explain(f, p, opt, macro, w);

        breakdown.WeightedScore.Total.Should().BeApproximately(score.Total, 1e-12);
        breakdown.WeightedScore.Value.Should().BeApproximately(score.Value, 1e-12);
        breakdown.WeightedScore.Quality.Should().BeApproximately(score.Quality, 1e-12);
        breakdown.WeightedScore.Momentum.Should().BeApproximately(score.Momentum, 1e-12);
        breakdown.WeightedScore.Options.Should().BeApproximately(score.Options, 1e-12);
        breakdown.WeightedScore.Macro.Should().BeApproximately(score.Macro, 1e-12);
    }

    [Fact]
    public void Scoring_MacroSectorTilt_ShouldAffectCyclicals_WhenMacroIsRiskOn()
    {
        var fEnergy = MakeFundamentals(sector: "ENERGY");
        var fUtilities = MakeFundamentals(sector: "UTILITIES");
        var p = MakePrices(days: 30, start: 100m, end: 100m); // flat; isolate macro effect
        var opt = (OptionsSnapshot?)null;

        var riskOn = new MacroSnapshot(
            TenYearYield: 4m,
            TwoTenSpread: 0.5m,
            CpiYoY: 2.5m,
            Pmi: 55m,
            Dxy: 100m,
            Wti: 80m);

        var w = new ScoringWeights(Value: 0, Quality: 0, Momentum: 0, Options: 0, Macro: 1);

        var sEnergy = Scoring.Compute(fEnergy, p, opt, riskOn, w);
        var sUtilities = Scoring.Compute(fUtilities, p, opt, riskOn, w);

        sEnergy.Macro.Should().BeGreaterThan(0);
        sUtilities.Macro.Should().BeLessThan(0);
    }
}
