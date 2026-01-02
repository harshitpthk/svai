# svai (Stock Screener)

A small .NET CLI for fetching market data and running a v0 screening workflow (prices + fundamentals, with optional macro/options inputs).

## Solution layout

- `src/StockScreener.Cli/`  CLI app (`svai`) built with Spectre.Console.Cli
- `src/StockScreener.Core/`  domain models + scoring + orchestration (`StockScreenerEngine`)
- `src/StockScreener.Data/`  data providers (prices, fundamentals, macro, options)
- `tests/StockScreener.Tests/`  unit tests (xUnit)

## Prerequisites

- .NET SDK (targets `net10.0`)

## Install (macOS/Linux)

### Option A: install via scripts (recommended)

This repo includes helper scripts that publish and install `svai` onto your `PATH` via a symlink.

Default install location: `/usr/local/bin/svai`.

```bash
./install.sh
```

Uninstall:

```bash
./uninstall.sh
```

If you prefer a user-local install (no sudo), use:

```bash
PREFIX=~/.local ./install.sh
PREFIX=~/.local ./uninstall.sh
# ensure ~/.local/bin is on your PATH
```

### Option B: Makefile shortcuts

```bash
make publish
make install
make uninstall
```

### Option C: dotnet tool-style install (local tool manifest)

This is convenient for development machines because it pins the tool in a repo-local manifest.

```bash
make tool-install
# run:
dotnet tool run svai -- --help
# or (dotnet supports this shorthand for local tools):
dotnet svai -- --help
```

Uninstall:

```bash
make tool-uninstall
```

## Run

Show CLI help:

```bash
dotnet run --project src/StockScreener.Cli/StockScreener.Cli.csproj -- --help
```

### Prices

Fetch recent daily prices (default provider: Stooq):

```bash
dotnet run --project src/StockScreener.Cli/StockScreener.Cli.csproj -- prices AAPL --days 5
```

### Doctor

Print configured vs effective providers and whether required API keys are present:

```bash
dotnet run --project src/StockScreener.Cli/StockScreener.Cli.csproj -- doctor --verbose
```

### Screen (v0)

Run a simple screen over tickers. This fetches fundamentals + price history per ticker and computes a toy score.

```bash
dotnet run --project src/StockScreener.Cli/StockScreener.Cli.csproj -- screen AAPL,MSFT,NVDA --days 90 --top 10
```

Explain a single ticker (factor breakdown + the inputs used):

```bash
dotnet run --project src/StockScreener.Cli/StockScreener.Cli.csproj -- screen AAPL,MSFT,NVDA --days 90 --explain MSFT
```

#### Simple filters

Filters exclude tickers *before* scoring (and before optional/expensive calls like options):

- `--min-fcf-yield <YIELD>`
- `--max-pe <PE>`
- `--min-roic <ROIC>`
- `--max-netdebt-ebitda <X>`
- `--min-momentum <R>` (20d momentum, e.g. `0` for non-negative)

Example:

```bash
dotnet run --project src/StockScreener.Cli/StockScreener.Cli.csproj -- screen AAPL,MSFT,NVDA --days 90 --max-pe 30 --min-roic 0.1 --min-momentum 0
```

## Configuration

Edit `src/StockScreener.Cli/appsettings.json`.

### Providers

Providers are selected via:

- `Providers:PriceProvider`
- `Providers:FundamentalsProvider`
- `Providers:MacroProvider`
- `Providers:OptionsProvider`

Supported implementations (current):

- Prices: `Stooq`, `Yahoo` (unofficial), `AlphaVantage`
- Fundamentals: `AlphaVantage` (recommended) or config fallback
- Macro: `Fred` or config fallback
- Options: `Polygon` (may require paid entitlements) or config fallback (returns null)

### Environment variables / .env (recommended for API keys)

This project loads environment variables via .NET configuration and (for local dev convenience) also loads a local `.env` file at startup.

- Copy `.env.example` to `.env`
- Fill in your real keys in `.env`
- `.env` is gitignored (do not commit secrets)

Example keys:

- `Providers__AlphaVantageApiKey`
- `Providers__FredApiKey`
- `Providers__PolygonApiKey`

## Notes

- Scoring is intentionally naive (toy heuristics). It will evolve toward normalized, sector-aware factors.
- Some APIs have free-tier rate limits.
- Polygon options snapshots may return `403 NOT_AUTHORIZED` depending on your plan; the CLI degrades gracefully (options snapshot will be `none`).
