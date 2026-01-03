namespace StockScreener.Core;

public enum ScreenDisposition
{
    Included,
    SkippedBlank,
    SkippedNoFundamentals,
    SkippedNoPrices,
    SkippedFilteredOut,
    Failed,
    FailedFundamentals,
    FailedPrices
}

public enum NormalizationMode
{
    None,
    GlobalZScore,
    SectorZScore
}

public sealed record ScreenProgress(string Ticker, int Completed, int Total, ScreenDisposition Disposition);

public class StockScreenerEngine
{
    private readonly IPriceDataProvider _prices;
    private readonly IFundamentalsProvider _fundamentals;
    private readonly IMacroDataProvider _macro;
    private readonly IOptionsDataProvider _options;

    public StockScreenerEngine(
        IPriceDataProvider prices,
        IFundamentalsProvider fundamentals,
        IMacroDataProvider macro,
        IOptionsDataProvider options)
    {
        _prices = prices;
        _fundamentals = fundamentals;
        _macro = macro;
        _options = options;
    }

    public async Task<IReadOnlyList<ScreenResult>> ScreenAsync(
        ScreenRequest req,
        CancellationToken ct = default,
        IProgress<ScreenProgress>? progress = null)
    {
        if (req is null) throw new ArgumentNullException(nameof(req));
        if (req.Tickers is null || req.Tickers.Count == 0) return Array.Empty<ScreenResult>();
        if (req.End < req.Start) throw new ArgumentException("End must be >= Start");

        // Snapshot macro once per run. Treat macro as optional in v0.
        MacroSnapshot m;
        try
        {
            m = await _macro.GetSnapshotAsync(ct);
        }
        catch
        {
            // Neutral-ish defaults that yield MacroSectorTilt ~ 0 for most sectors.
            m = new MacroSnapshot(
                TenYearYield: 0m,
                TwoTenSpread: 0m,
                CpiYoY: 3m,
                Pmi: 50m,
                Dxy: 100m,
                Wti: 0m
            );
        }

        var total = req.Tickers.Count;
        var completed = 0;

        // Bounded concurrency. Keep conservative because providers may rate-limit.
        // AlphaVantage free tier is extremely burst-sensitive (1 req/sec). Cap hard when it's in use.
        var usingAlphaVantageFundamentals = _fundamentals.GetType().Name.Contains("AlphaVantage", StringComparison.OrdinalIgnoreCase);

        var maxConcurrency = usingAlphaVantageFundamentals
            ? 1
            : Math.Min(Environment.ProcessorCount, 8);

        var results = new List<ScreenResult>(req.Tickers.Count);
        var gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var tasks = new List<Task>(req.Tickers.Count);
        var resultsLock = new object();

        foreach (var raw in req.Tickers)
        {
            ct.ThrowIfCancellationRequested();
            await gate.WaitAsync(ct);

            tasks.Add(Task.Run(async () =>
            {
                string ticker = "";
                ScreenDisposition disposition = ScreenDisposition.Failed;

                try
                {
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        disposition = ScreenDisposition.SkippedBlank;
                        return;
                    }

                    ticker = raw.Trim().ToUpperInvariant();

                    Fundamentals? f;
                    IReadOnlyList<PriceBar> p;

                    try
                    {
                        f = await _fundamentals.GetAsync(ticker, ct);

                        if (usingAlphaVantageFundamentals)
                        {
                            // AlphaVantage free tier is ~1 request/sec. Even with maxConcurrency=1, add spacing.
                            await Task.Delay(TimeSpan.FromSeconds(1), ct);
                        }

                        if (f is null)
                        {
                            disposition = ScreenDisposition.SkippedNoFundamentals;
                            return;
                        }
                    }
                    catch
                    {
                        disposition = ScreenDisposition.FailedFundamentals;
                        return;
                    }

                    try
                    {
                        p = await _prices.GetDailyAsync(ticker, req.Start, req.End, ct);
                        if (p.Count == 0)
                        {
                            disposition = ScreenDisposition.SkippedNoPrices;
                            return;
                        }
                    }
                    catch
                    {
                        disposition = ScreenDisposition.FailedPrices;
                        return;
                    }

                    // Apply optional filters before doing any optional/expensive work (like options).
                    if (req.Filters is not null && !req.Filters.Matches(f, p))
                    {
                        disposition = ScreenDisposition.SkippedFilteredOut;
                        return;
                    }

                    // Options can be expensive / unavailable; treat null as fine.
                    OptionsSnapshot? opt = null;
                    try
                    {
                        opt = await _options.GetSnapshotAsync(ticker, ct);
                    }
                    catch
                    {
                        opt = null;
                    }

                    var s = Scoring.Compute(f, p, opt, m, req.Weights);

                    lock (resultsLock)
                    {
                        results.Add(new ScreenResult
                        {
                            Ticker = ticker,
                            Fundamentals = f,
                            Prices = p,
                            Score = s,
                            Macro = m,
                            Options = opt
                        });
                    }

                    disposition = ScreenDisposition.Included;
                }
                finally
                {
                    var done = Interlocked.Increment(ref completed);
                    progress?.Report(new ScreenProgress(ticker, done, total, disposition));
                    gate.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks);

        if (req.NormalizationMode != NormalizationMode.None && results.Count > 1)
        {
            // Second pass: normalize metrics over the *run universe* and recompute scores.
            // Keep macro/options logic as-is; only value/quality/momentum are z-scored.
            var normalized = Scoring.NormalizeAndRescore(results, req.Weights, req.NormalizationMode);
            return normalized;
        }

        return results;
    }
}

public sealed class ScreenResult
{
    public required string Ticker { get; init; }
    public required Fundamentals Fundamentals { get; init; }
    public required IReadOnlyList<PriceBar> Prices { get; init; }
    public required Score Score { get; init; }

    public required MacroSnapshot Macro { get; init; }
    public OptionsSnapshot? Options { get; init; }
}

public sealed class ScreenRequest
{
    public required IReadOnlyList<string> Tickers { get; init; }
    public required DateOnly Start { get; init; }
    public required DateOnly End { get; init; }
    public required ScoringWeights Weights { get; init; }

    public ScreenFilters? Filters { get; init; }

    public NormalizationMode NormalizationMode { get; init; } = NormalizationMode.None;
}

public sealed record ScreenFilters(
    decimal? MinFcfYield = null,
    decimal? MaxPe = null,
    decimal? MinRoic = null,
    decimal? MaxNetDebtToEbitda = null,
    double? MinMomentum = null)
{
    public bool Matches(Fundamentals f, IReadOnlyList<PriceBar> prices)
    {
        if (MinFcfYield is not null && f.FcfYield < MinFcfYield.Value) return false;
        if (MaxPe is not null && f.Pe > MaxPe.Value) return false;
        if (MinRoic is not null && f.Roic < MinRoic.Value) return false;
        if (MaxNetDebtToEbitda is not null && f.NetDebtToEbitda > MaxNetDebtToEbitda.Value) return false;

        if (MinMomentum is not null)
        {
            // 20d momentum in the same shape as Scoring.RecentMomentum.
            if (prices.Count < 21) return false;
            var last = prices[^1].Close;
            var prev = prices[^21].Close;
            if (prev <= 0) return false;

            var mom = (double)((last - prev) / prev);
            if (mom < MinMomentum.Value) return false;
        }

        return true;
    }
}
