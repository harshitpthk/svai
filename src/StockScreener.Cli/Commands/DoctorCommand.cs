using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;
using StockScreener.Core;

namespace StockScreener.Cli.Commands;

public sealed class DoctorCommand(
    IConfiguration config,
    IPriceDataProvider price,
    IFundamentalsProvider fundamentals,
    IMacroDataProvider macro,
    IOptionsDataProvider options,
    ILogger<DoctorCommand> logger) : Command<DoctorCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        // --verbose and --log-network come from GlobalSettings
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        return AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("green"))
            .Start("Checking configuration...", _ =>
            {
                // Provider selections (configured)
                var priceProvider = (config["Providers:PriceProvider"] ?? "").Trim();
                var fundamentalsProvider = (config["Providers:FundamentalsProvider"] ?? "").Trim();
                var optionsProvider = (config["Providers:OptionsProvider"] ?? "").Trim();
                var macroProvider = (config["Providers:MacroProvider"] ?? "").Trim();

                // Effective providers (actual resolved types)
                var effectivePrice = EffectiveName(price);
                var effectiveFundamentals = EffectiveName(fundamentals);
                var effectiveOptions = EffectiveName(options);
                var effectiveMacro = EffectiveName(macro);

                // Secret presence (do NOT print actual values)
                var hasAlphaKey = !string.IsNullOrWhiteSpace(config["Providers:AlphaVantageApiKey"]);
                var hasFredKey = !string.IsNullOrWhiteSpace(config["Providers:FredApiKey"]);
                var hasPolygonKey = !string.IsNullOrWhiteSpace(config["Providers:PolygonApiKey"]);

                var grid = new Grid();
                grid.AddColumn();
                grid.AddColumn();

                grid.AddRow("[bold]PriceProvider (configured)[/]", MarkValue(priceProvider));
                grid.AddRow("[bold]PriceProvider (effective)[/]", $"[cyan]{Markup.Escape(effectivePrice)}[/]");

                grid.AddEmptyRow();

                grid.AddRow("[bold]FundamentalsProvider (configured)[/]", MarkValue(fundamentalsProvider));
                grid.AddRow("[bold]FundamentalsProvider (effective)[/]", $"[cyan]{Markup.Escape(effectiveFundamentals)}[/]");

                grid.AddEmptyRow();

                grid.AddRow("[bold]OptionsProvider (configured)[/]", MarkValue(optionsProvider));
                grid.AddRow("[bold]OptionsProvider (effective)[/]", $"[cyan]{Markup.Escape(effectiveOptions)}[/]");

                grid.AddEmptyRow();

                grid.AddRow("[bold]MacroProvider (configured)[/]", MarkValue(macroProvider));
                grid.AddRow("[bold]MacroProvider (effective)[/]", $"[cyan]{Markup.Escape(effectiveMacro)}[/]");

                grid.AddEmptyRow();
                grid.AddRow("[bold]AlphaVantageApiKey[/]", MarkBool(hasAlphaKey));
                grid.AddRow("[bold]FredApiKey[/]", MarkBool(hasFredKey));
                grid.AddRow("[bold]PolygonApiKey[/]", MarkBool(hasPolygonKey));

                AnsiConsole.Write(new Panel(grid).Header("doctor", Justify.Left));

                if (settings.Verbose)
                {
                    var keys = new[]
                    {
                        "Providers:PriceProvider",
                        "Providers:FundamentalsProvider",
                        "Providers:OptionsProvider",
                        "Providers:MacroProvider",
                        "Providers:AlphaVantageApiKey",
                        "Providers:FredApiKey",
                        "Providers:PolygonApiKey",
                    };

                    var table = new Table().Border(TableBorder.Rounded);
                    table.AddColumn("Key");
                    table.AddColumn("Present?");
                    table.AddColumn("Value (masked)");
                    table.AddColumn("Source");

                    foreach (var k in keys)
                    {
                        var v = config[k];
                        var present = !string.IsNullOrWhiteSpace(v);
                        var source = DescribeSourceVerbose(config, k);
                        table.AddRow(k, present ? "yes" : "no", MaskValue(k, v), source);
                    }

                    AnsiConsole.Write(table);
                }

                logger.LogInformation("Doctor command ran");
                return 0;
            });
    }

    private static string EffectiveName(object service)
    {
        // Avoid dumping full namespaces; show just the concrete type name.
        var t = service.GetType();
        return t.Name;
    }

    private static string MarkValue(string v)
        => string.IsNullOrWhiteSpace(v) ? "[yellow](not set)[/]" : $"[green]{Markup.Escape(v)}[/]";

    private static string MarkBool(bool b)
        => b ? "[green]present[/]" : "[yellow]missing[/]";

    private static string MaskValue(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        // Anything that looks like a secret is masked.
        if (key.EndsWith("ApiKey", StringComparison.OrdinalIgnoreCase) || key.Contains("Key", StringComparison.OrdinalIgnoreCase) || key.Contains("Token", StringComparison.OrdinalIgnoreCase))
            return $"{new string('*', Math.Min(4, value.Length))}… (len={value.Length})";

        // Safe config values can be shown.
        return value;
    }

    private static string DescribeSource(IConfiguration cfg, string key)
    {
        if (cfg is not IConfigurationRoot root)
            return "(unknown)";

        string? foundProvider = null;
        foreach (var p in root.Providers)
        {
            if (p.TryGet(key, out var _))
                foundProvider = p.ToString();
        }

        if (string.IsNullOrWhiteSpace(foundProvider))
            return "(not set)";

        var fp = foundProvider;
        if (fp.Contains("EnvironmentVariables", StringComparison.OrdinalIgnoreCase))
            return "env";
        if (fp.Contains("JsonConfigurationProvider", StringComparison.OrdinalIgnoreCase))
            return "json";
        if (fp.Contains("ChainedConfigurationProvider", StringComparison.OrdinalIgnoreCase))
            return "chained";

        return fp;
    }

    private static string DescribeSourceVerbose(IConfiguration cfg, string key)
    {
        if (cfg is not IConfigurationRoot root)
            return DescribeSource(cfg, key);

        var hits = new List<string>();
        foreach (var p in root.Providers)
        {
            if (!p.TryGet(key, out var _))
                continue;

            var s = p.ToString() ?? string.Empty;
            if (s.Contains("EnvironmentVariables", StringComparison.OrdinalIgnoreCase)) hits.Add("env");
            else if (s.Contains("JsonConfigurationProvider", StringComparison.OrdinalIgnoreCase)) hits.Add("json");
            else if (s.Contains("ChainedConfigurationProvider", StringComparison.OrdinalIgnoreCase)) hits.Add("chained");
            else hits.Add(s);
        }

        return hits.Count == 0 ? "(not set)" : string.Join(" -> ", hits);
    }
}
