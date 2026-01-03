using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;
using StockScreener.Core;

namespace StockScreener.Cli.Commands;

public sealed class ScreenCommand(
    IConfiguration config,
    StockScreenerEngine engine,
    INetworkLogContext networkLog,
    ILogger<ScreenCommand> logger) : AsyncCommand<ScreenCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<TICKERS>")]
        [System.ComponentModel.Description("Comma-separated tickers (e.g. AAPL,MSFT,GOOG).")]
        public required string Tickers { get; init; }

        [CommandOption("--days <DAYS>")]
        [System.ComponentModel.Description("Lookback window (days) used for price history and momentum calculations.")]
        public int Days { get; init; } = 90;

        [CommandOption("--top <N>")]
        [System.ComponentModel.Description("How many top-ranked tickers to display.")]
        public int Top { get; init; } = 10;

        [CommandOption("--explain <TICKER>")]
        [System.ComponentModel.Description("Show a scoring breakdown for a single ticker from the result set.")]
        public string? Explain { get; init; }

        [CommandOption("--min-fcf-yield <YIELD>")]
        [System.ComponentModel.Description("Filter: minimum free cash flow yield (e.g. 0.05 for 5%).")]
        public decimal? MinFcfYield { get; init; }

        [CommandOption("--max-pe <PE>")]
        [System.ComponentModel.Description("Filter: maximum P/E ratio.")]
        public decimal? MaxPe { get; init; }

        [CommandOption("--min-roic <ROIC>")]
        [System.ComponentModel.Description("Filter: minimum ROIC (e.g. 0.10 for 10%).")]
        public decimal? MinRoic { get; init; }

        [CommandOption("--max-netdebt-ebitda <X>")]
        [System.ComponentModel.Description("Filter: maximum net debt / EBITDA multiple.")]
        public decimal? MaxNetDebtToEbitda { get; init; }

        [CommandOption("--min-momentum <R>")]
        [System.ComponentModel.Description("Filter: minimum momentum over the lookback window (e.g. 0.20 for +20%).")]
        public double? MinMomentum { get; init; }

        [CommandOption("--normalize <MODE>")]
        [System.ComponentModel.Description("Normalize scoring inputs using per-run z-scores. Modes: none|global|sector.")]
        public string Normalize { get; init; } = "none";

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(Tickers))
                return ValidationResult.Error("Please provide one or more tickers (comma-separated).");

            if (Days <= 0)
                return ValidationResult.Error("--days must be a positive integer.");

            if (Top <= 0)
                return ValidationResult.Error("--top must be a positive integer.");

            if (MinFcfYield is < 0)
                return ValidationResult.Error("--min-fcf-yield must be >= 0.");

            if (MaxPe is <= 0)
                return ValidationResult.Error("--max-pe must be > 0.");

            if (MinRoic is < 0)
                return ValidationResult.Error("--min-roic must be >= 0.");

            if (MaxNetDebtToEbitda is < 0)
                return ValidationResult.Error("--max-netdebt-ebitda must be >= 0.");

            var mode = (Normalize ?? "none").Trim().ToLowerInvariant();
            if (mode is not ("none" or "global" or "sector"))
                return ValidationResult.Error("--normalize must be one of: none, global, sector.");

            return ValidationResult.Success();
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var prevNetworkLog = networkLog.Enabled;
        networkLog.Enabled = settings.LogNetwork;
        try
        {
            var tickers = settings.Tickers
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => t.ToUpperInvariant())
                .Distinct()
                .ToArray();

            if (tickers.Length == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No tickers provided.[/]");
                return 1;
            }

            // Resolve scoring weights from config.
            var w = new ScoringWeights(
                Value: GetDouble(config, "Scoring:Weights:Value", 0.4),
                Quality: GetDouble(config, "Scoring:Weights:Quality", 0.2),
                Momentum: GetDouble(config, "Scoring:Weights:Momentum", 0.15),
                Options: GetDouble(config, "Scoring:Weights:Options", 0.15),
                Macro: GetDouble(config, "Scoring:Weights:Macro", 0.1)
            );

            var end = DateOnly.FromDateTime(DateTime.Today);
            var start = end.AddDays(-settings.Days);

            var normalizationMode = (settings.Normalize ?? "none").Trim().ToLowerInvariant() switch
            {
                "sector" => NormalizationMode.SectorZScore,
                "global" => NormalizationMode.GlobalZScore,
                _ => NormalizationMode.None
            };

            var filters = new ScreenFilters(
                MinFcfYield: settings.MinFcfYield,
                MaxPe: settings.MaxPe,
                MinRoic: settings.MinRoic,
                MaxNetDebtToEbitda: settings.MaxNetDebtToEbitda,
                MinMomentum: settings.MinMomentum
            );

            // Only send Filters if at least one is set, so behavior is identical by default.
            var effectiveFilters = (settings.MinFcfYield, settings.MaxPe, settings.MinRoic, settings.MaxNetDebtToEbitda, settings.MinMomentum) switch
            {
                (null, null, null, null, null) => null,
                _ => filters
            };

            IReadOnlyList<ScreenResult> results;

            try
            {
                var included = 0;
                var skippedBlank = 0;
                var skippedNoFundamentals = 0;
                var skippedNoPrices = 0;
                var skippedFiltered = 0;
                var failedFundamentals = 0;
                var failedPrices = 0;
                var failed = 0;

                results = await AnsiConsole.Progress()
                    .AutoClear(true)
                    .HideCompleted(false)
                    .Columns(
                        new ProgressBarColumn(),
                        new PercentageColumn(),
                        new SpinnerColumn(),
                        new TaskDescriptionColumn())
                    .StartAsync(async ctx =>
                    {
                        var task = ctx.AddTask("Screening tickers", maxValue: tickers.Length);

                        var progress = new Progress<ScreenProgress>(p =>
                        {
                            task.Value = p.Completed;

                            switch (p.Disposition)
                            {
                                case ScreenDisposition.Included:
                                    included++;
                                    break;
                                case ScreenDisposition.SkippedBlank:
                                    skippedBlank++;
                                    break;
                                case ScreenDisposition.SkippedNoFundamentals:
                                    skippedNoFundamentals++;
                                    break;
                                case ScreenDisposition.SkippedNoPrices:
                                    skippedNoPrices++;
                                    break;
                                case ScreenDisposition.SkippedFilteredOut:
                                    skippedFiltered++;
                                    break;
                                case ScreenDisposition.FailedFundamentals:
                                    failedFundamentals++;
                                    break;
                                case ScreenDisposition.FailedPrices:
                                    failedPrices++;
                                    break;
                                default:
                                    failed++;
                                    break;
                            }

                            var head = string.IsNullOrWhiteSpace(p.Ticker)
                                ? "Screening"
                                : $"Screening [bold]{Markup.Escape(p.Ticker)}[/]";

                            task.Description =
                                $"{head} ({p.Completed}/{p.Total})  " +
                                $"[grey]inc={included} " +
                                $"blank={skippedBlank} " +
                                $"noFund={skippedNoFundamentals} " +
                                $"noPrices={skippedNoPrices} " +
                                $"filtered={skippedFiltered} " +
                                $"failFund={failedFundamentals} " +
                                $"failPrices={failedPrices} " +
                                $"fail={failed}[/]";
                        });

                        var req = new ScreenRequest
                        {
                            Tickers = tickers,
                            Start = start,
                            End = end,
                            Weights = w,
                            Filters = effectiveFilters,
                            NormalizationMode = normalizationMode
                        };

                        var r = await engine.ScreenAsync(req, cancellationToken, progress);

                        task.Value = tickers.Length;
                        task.StopTask();

                        return r;
                    });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Screen command failed");
                AnsiConsole.MarkupLine("[red]Screen failed.[/]");
                return 2;
            }

            if (results.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No results (no ticker had both prices and fundamentals).[/]");
                return 1;
            }

            var ordered = results.OrderByDescending(r => r.Score.Total).ToList();

            // UI-only score scaling: map raw totals to a 0..100 range for display.
            // Sorting remains by raw Score.Total.
            var minTotal = ordered.Min(r => r.Score.Total);
            var maxTotal = ordered.Max(r => r.Score.Total);

            static double ToUi100(double value, double min, double max)
            {
                if (max <= min) return 50d;
                var t = (value - min) / (max - min);
                t = Math.Clamp(t, 0d, 1d);
                return t * 100d;
            }

            if (!string.IsNullOrWhiteSpace(settings.Explain))
            {
                var target = settings.Explain.Trim().ToUpperInvariant();
                var r = ordered.FirstOrDefault(x => x.Ticker.Equals(target, StringComparison.OrdinalIgnoreCase));
                if (r is null)
                {
                    AnsiConsole.MarkupLine($"[yellow]Ticker not found in results:[/] [bold]{Markup.Escape(target)}[/]");
                    return 1;
                }

                // NOTE: Engine currently uses options/macro internally; explain mode focuses on prices+fundamentals
                // and shows a breakdown using a neutral macro snapshot.
                var breakdown = Scoring.Explain(r.Fundamentals, r.Prices, r.Options, r.Macro, w);

                var info = new Grid();
                info.AddColumn();
                info.AddColumn();
                info.AddRow("[bold]Ticker[/]", Markup.Escape(r.Ticker));
                info.AddRow("[bold]Sector[/]", Markup.Escape(r.Fundamentals.Sector));
                info.AddRow("[bold]Bars[/]", r.Prices.Count.ToString());
                info.AddRow("[bold]Range[/]", $"{start}..{end}");
                info.AddRow("[bold]Options snapshot[/]", r.Options is null ? "[yellow]none[/]" : "[green]present[/]");
                AnsiConsole.Write(new Panel(info).Header("explain", Justify.Left));

                var fundamentalsTable = new Table().Border(TableBorder.Rounded);
                fundamentalsTable.AddColumn("Fundamental");
                fundamentalsTable.AddColumn(new TableColumn("Value").RightAligned());
                fundamentalsTable.AddRow("PE", r.Fundamentals.Pe.ToString("0.###"));
                fundamentalsTable.AddRow("EV/EBITDA", r.Fundamentals.EvToEbitda.ToString("0.###"));
                fundamentalsTable.AddRow("FCF Yield", r.Fundamentals.FcfYield.ToString("0.####"));
                fundamentalsTable.AddRow("P/B", r.Fundamentals.Pb.ToString("0.###"));
                fundamentalsTable.AddRow("ROIC", r.Fundamentals.Roic.ToString("0.####"));
                fundamentalsTable.AddRow("Gross Margin", r.Fundamentals.GrossMargin.ToString("0.####"));
                fundamentalsTable.AddRow("Net Debt/EBITDA", r.Fundamentals.NetDebtToEbitda.ToString("0.###"));
                AnsiConsole.Write(fundamentalsTable);

                var scoreTable = new Table().Border(TableBorder.Rounded);
                scoreTable.AddColumn("Factor");
                scoreTable.AddColumn(new TableColumn("Raw").RightAligned());
                scoreTable.AddColumn(new TableColumn("Weight").RightAligned());
                scoreTable.AddColumn(new TableColumn("Weighted").RightAligned());

                scoreTable.AddRow("Value", breakdown.ValueRaw.ToString("0.###"), breakdown.Weights.Value.ToString("0.###"), breakdown.WeightedScore.Value.ToString("0.###"));
                scoreTable.AddRow("Quality", breakdown.QualityRaw.ToString("0.###"), breakdown.Weights.Quality.ToString("0.###"), breakdown.WeightedScore.Quality.ToString("0.###"));
                scoreTable.AddRow("Momentum", breakdown.MomentumRaw.ToString("0.###"), breakdown.Weights.Momentum.ToString("0.###"), breakdown.WeightedScore.Momentum.ToString("0.###"));
                scoreTable.AddRow("Options", breakdown.OptionsRaw.ToString("0.###"), breakdown.Weights.Options.ToString("0.###"), breakdown.WeightedScore.Options.ToString("0.###"));
                scoreTable.AddRow("Macro", breakdown.MacroRaw.ToString("0.###"), breakdown.Weights.Macro.ToString("0.###"), breakdown.WeightedScore.Macro.ToString("0.###"));
                scoreTable.AddRow("[bold]Total[/]", breakdown.TotalRaw.ToString("0.###"), "", breakdown.WeightedScore.Total.ToString("0.###"));

                AnsiConsole.Write(scoreTable);
                return 0;
            }

            var topN = Math.Min(settings.Top, ordered.Count);

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("#");
            table.AddColumn("Ticker");
            table.AddColumn(new TableColumn("Score").RightAligned());
            table.AddColumn(new TableColumn("Total (raw)").RightAligned());
            table.AddColumn(new TableColumn("Value").RightAligned());
            table.AddColumn(new TableColumn("Quality").RightAligned());
            table.AddColumn(new TableColumn("Momentum").RightAligned());
            table.AddColumn(new TableColumn("Sector").LeftAligned());

            for (var i = 0; i < topN; i++)
            {
                var r = ordered[i];
                var ui = ToUi100(r.Score.Total, minTotal, maxTotal);

                table.AddRow(
                    (i + 1).ToString(),
                    Markup.Escape(r.Ticker),
                    ui.ToString("0.0"),
                    r.Score.Total.ToString("0.###"),
                    r.Score.Value.ToString("0.###"),
                    r.Score.Quality.ToString("0.###"),
                    r.Score.Momentum.ToString("0.###"),
                    Markup.Escape(r.Fundamentals.Sector)
                );
            }

            AnsiConsole.MarkupLine($"[bold]Screen results[/] (days={settings.Days}, tickers={tickers.Length}, shown={topN})");
            AnsiConsole.Write(table);

            logger.LogInformation("Screened {Tickers} tickers", tickers.Length);
            return 0;
        }
        finally
        {
            networkLog.Enabled = prevNetworkLog;
        }
    }

    private static double GetDouble(IConfiguration cfg, string key, double fallback)
        => double.TryParse(cfg[key], out var d) ? d : fallback;
}
