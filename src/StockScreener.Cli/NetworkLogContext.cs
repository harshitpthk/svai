using System.Threading;

namespace StockScreener.Cli;

/// <summary>
/// Holds per-invocation flags for the CLI runtime.
/// Uses AsyncLocal to flow through async calls without passing flags everywhere.
/// </summary>
public interface INetworkLogContext
{
    bool Enabled { get; set; }
}

public sealed class NetworkLogContext : INetworkLogContext
{
    private static readonly AsyncLocal<bool> _enabled = new();

    public bool Enabled
    {
        get => _enabled.Value;
        set => _enabled.Value = value;
    }
}
