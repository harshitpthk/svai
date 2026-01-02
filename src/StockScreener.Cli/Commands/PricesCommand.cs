using Spectre.Console;
using Spectre.Console.Cli;
using StockScreener.Core;

namespace StockScreener.Cli.Commands;

public sealed class PricesCommand(IPriceDataProvider prices) : AsyncCommand<PricesCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<TICKER>")]
        public string Ticker { get; init; } = "";

        [CommandOption("--start <START>")]
        public DateTime? Start { get; init; }

        [CommandOption("--end <END>")]
        public DateTime? End { get; init; }

        [CommandOption("--days <DAYS>")]
        public int? Days { get; init; }

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(Ticker))
                return ValidationResult.Error("Ticker is required.");

            if (Days is <= 0)
                return ValidationResult.Error("--days must be a positive integer.");

            if (Start is not null && End is not null && End < Start)
                return ValidationResult.Error("--end must be >= --start.");

            return ValidationResult.Success();
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var end = settings.End ?? DateTime.Today;
        var start = settings.Start ?? (settings.Days is int d ? end.AddDays(-d) : end.AddDays(-30));

        var startDate = DateOnly.FromDateTime(start);
        var endDate = DateOnly.FromDateTime(end);

        var bars = await prices.GetDailyAsync(settings.Ticker, startDate, endDate, cancellationToken);

        if (bars.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No bars returned for[/] [bold]{settings.Ticker}[/] {startDate}..{endDate}.");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Date");
        table.AddColumn(new TableColumn("Open").RightAligned());
        table.AddColumn(new TableColumn("High").RightAligned());
        table.AddColumn(new TableColumn("Low").RightAligned());
        table.AddColumn(new TableColumn("Close").RightAligned());
        table.AddColumn(new TableColumn("Volume").RightAligned());

        foreach (var b in bars.OrderBy(b => b.Date))
        {
            table.AddRow(
                b.Date.ToString("yyyy-MM-dd"),
                b.Open.ToString("0.####"),
                b.High.ToString("0.####"),
                b.Low.ToString("0.####"),
                b.Close.ToString("0.####"),
                b.Volume.ToString("0")
            );
        }

        AnsiConsole.MarkupLine($"[bold]{settings.Ticker}[/] ({bars.Count} bars) {startDate}..{endDate}");
        AnsiConsole.Write(table);
        return 0;
    }
}
