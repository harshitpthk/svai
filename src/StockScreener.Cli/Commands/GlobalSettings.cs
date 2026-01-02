using System.ComponentModel;
using Spectre.Console.Cli;

namespace StockScreener.Cli.Commands;

public class GlobalSettings : CommandSettings
{
    [CommandOption("--verbose")]
    [Description("Show additional diagnostic output (keeps normal output unchanged).")]
    public bool Verbose { get; init; }

    [CommandOption("--log-network")]
    [Description("Log HTTP requests/responses (for debugging; may be noisy).")]
    public bool LogNetwork { get; init; }
}
