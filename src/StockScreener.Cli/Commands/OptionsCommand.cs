using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;
using StockScreener.Core;

namespace StockScreener.Cli.Commands;

public sealed class OptionsCommand(IOptionsDataProvider options, ILogger<OptionsCommand> logger) : AsyncCommand<OptionsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<TICKER>")]
        public required string Ticker { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var snap = await options.GetSnapshotAsync(settings.Ticker, cancellationToken);
        if (snap is null)
        {
            AnsiConsole.MarkupLine("[yellow]No options snapshot available (provider returned null).[/]");
            return 1;
        }

        var table = new Table().RoundedBorder().BorderColor(Color.Grey);
        table.AddColumn("Metric");
        table.AddColumn("Value");

        table.AddRow("Put/Call Ratio", snap.PutCallRatio.ToString("0.###"));
        table.AddRow("IV Rank", snap.ImpliedVolRank.ToString("0.###"));
        table.AddRow("Call Vol / Avg20d", snap.CallVolumeToAvg20d.ToString("0.###"));
        table.AddRow("Near OTM Call OI Δ", snap.NearOtMCallOiDelta.ToString("0.###"));

        AnsiConsole.Write(table);
        logger.LogInformation("Options snapshot displayed for {Ticker}", settings.Ticker);
        return 0;
    }
}
