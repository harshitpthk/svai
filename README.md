# svai (Stock Screener)

A small .NET CLI for fetching market data (prices + fundamentals stubs) and building toward a stock screening workflow.

## Solution layout

- `src/StockScreener.Cli/` — CLI app (`svai`) built with Spectre.Console.Cli
- `src/StockScreener.Core/` — domain models + scoring logic (`Scoring`)
- `src/StockScreener.Data/` — data providers (prices, fundamentals, macro, options)
- `tests/StockScreener.Tests/` — unit tests (xUnit)

## Prerequisites

- .NET SDK (targets `net10.0`)

## Run

Show CLI help:

```bash
dotnet run --project src/StockScreener.Cli/StockScreener.Cli.csproj -- --help
```

Fetch recent daily prices (default provider: Stooq):

```bash
dotnet run --project src/StockScreener.Cli/StockScreener.Cli.csproj -- prices AAPL --days 5
```

## Configuration

Edit `src/StockScreener.Cli/appsettings.json`.

### Price provider

```json
{
  "Providers": {
    "PriceProvider": "Stooq"
  }
}
```

Supported values:
- `Stooq` (no API key)
- `Yahoo` (unofficial endpoint)
- `AlphaVantage` (requires API key)

### API keys

```json
{
  "Providers": {
    "AlphaVantageApiKey": "YOUR_KEY",
    "PolygonApiKey": "YOUR_KEY"
  }
}
```

## Notes

- This repo is intentionally incremental: some providers (fundamentals/options/macro) are placeholders while the core workflow is built out.
- If you enable `AlphaVantage` you may hit rate limits on the free tier.
