using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;
using StockScreener.Cli.Commands;
using StockScreener.Core;
using StockScreener.Data;
using StockScreener.Data.Price;
using StockScreener.Data.Options;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace StockScreener.Cli;

class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Load local .env (if present). This is a dev convenience and should not be used for production secrets.
        DotNetEnv.Env.Load();

        var host = Host.CreateDefaultBuilder(args)
            .UseContentRoot(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..")))
            .ConfigureAppConfiguration((ctx, cfg) =>
            {
                var appSettingsPath = Path.Combine(ctx.HostingEnvironment.ContentRootPath, "appsettings.json");
                cfg.AddJsonFile(appSettingsPath, optional: true, reloadOnChange: true);
                cfg.AddEnvironmentVariables();
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
            })
            .ConfigureServices((ctx, services) =>
            {
                services.AddMemoryCache();

                // Enables typed HttpClient injection into providers.
                services.AddHttpClient();

                // Register all price providers (we'll choose at runtime).
                services.AddHttpClient<StockScreener.Data.Price.YahooPriceProvider>();
                services.AddHttpClient<StockScreener.Data.Price.StooqPriceProvider>();
                services.AddHttpClient<StockScreener.Data.Price.AlphaVantagePriceProvider>();

                services.AddSingleton<IPriceDataProvider>(sp =>
                {
                    var cfg = sp.GetRequiredService<IConfiguration>();
                    var which = (cfg["Providers:PriceProvider"] ?? "Stooq").Trim();

                    return which.ToLowerInvariant() switch
                    {
                        "yahoo" => sp.GetRequiredService<StockScreener.Data.Price.YahooPriceProvider>(),
                        "alphavantage" or "alpha" or "alpha-vantage" => sp.GetRequiredService<StockScreener.Data.Price.AlphaVantagePriceProvider>(),
                        "stooq" => sp.GetRequiredService<StockScreener.Data.Price.StooqPriceProvider>(),
                        _ => sp.GetRequiredService<StockScreener.Data.Price.StooqPriceProvider>()
                    };
                });

                // Fundamentals providers
                services.AddHttpClient<AlphaVantageFundamentalsProvider>();
                services.AddSingleton<ConfigFundamentalsProvider>();

                services.AddSingleton<IFundamentalsProvider>(sp =>
                {
                    var cfg = sp.GetRequiredService<IConfiguration>();

                    // 1) Respect explicit selection
                    var which = (cfg["Providers:FundamentalsProvider"] ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(which))
                    {
                        return which.ToLowerInvariant() switch
                        {
                            "alphavantage" or "alpha" or "alpha-vantage" => sp.GetRequiredService<AlphaVantageFundamentalsProvider>(),
                            "config" => sp.GetRequiredService<ConfigFundamentalsProvider>(),
                            _ => sp.GetRequiredService<ConfigFundamentalsProvider>()
                        };
                    }

                    // 2) Fallback: prefer AlphaVantage if key present
                    var apiKey = cfg["Providers:AlphaVantageApiKey"];
                    if (!string.IsNullOrWhiteSpace(apiKey))
                        return sp.GetRequiredService<AlphaVantageFundamentalsProvider>();

                    // 3) Final fallback
                    return sp.GetRequiredService<ConfigFundamentalsProvider>();
                });

                // Macro providers
                services.AddHttpClient<FredMacroDataProvider>();
                services.AddSingleton<ConfigMacroDataProvider>();

                services.AddSingleton<IMacroDataProvider>(sp =>
                {
                    var cfg = sp.GetRequiredService<IConfiguration>();

                    // 1) Respect explicit selection
                    var which = (cfg["Providers:MacroProvider"] ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(which))
                    {
                        return which.ToLowerInvariant() switch
                        {
                            "fred" => sp.GetRequiredService<FredMacroDataProvider>(),
                            "config" => sp.GetRequiredService<ConfigMacroDataProvider>(),
                            _ => sp.GetRequiredService<ConfigMacroDataProvider>()
                        };
                    }

                    // 2) Fallback: prefer FRED if key present
                    var apiKey = cfg["Providers:FredApiKey"];
                    if (!string.IsNullOrWhiteSpace(apiKey))
                        return sp.GetRequiredService<FredMacroDataProvider>();

                    // 3) Final fallback
                    return sp.GetRequiredService<ConfigMacroDataProvider>();
                });

                // Options providers
                services.AddHttpClient<PolygonOptionsDataProvider>();
                services.AddSingleton<ConfigStockOptionsDataProvider>();

                services.AddSingleton<IOptionsDataProvider>(sp =>
                {
                    var cfg = sp.GetRequiredService<IConfiguration>();

                    // 1) Respect explicit selection
                    var which = (cfg["Providers:OptionsProvider"] ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(which))
                    {
                        return which.ToLowerInvariant() switch
                        {
                            "polygon" => sp.GetRequiredService<PolygonOptionsDataProvider>(),
                            "config" => sp.GetRequiredService<ConfigStockOptionsDataProvider>(),
                            _ => sp.GetRequiredService<ConfigStockOptionsDataProvider>()
                        };
                    }

                    // 2) Fallback: prefer Polygon if key present
                    var apiKey = cfg["Providers:PolygonApiKey"];
                    if (!string.IsNullOrWhiteSpace(apiKey))
                        return sp.GetRequiredService<PolygonOptionsDataProvider>();

                    // 3) Final fallback
                    return sp.GetRequiredService<ConfigStockOptionsDataProvider>();
                });

                // Core orchestration
                services.AddSingleton<StockScreenerEngine>();

                // Commands are resolved by DI through the Spectre TypeRegistrar.
                services.AddTransient<PricesCommand>();
                services.AddTransient<OptionsCommand>();
                services.AddTransient<DoctorCommand>();
                services.AddTransient<ScreenCommand>();
            })
            .Build();

        var app = new CommandApp(new TypeRegistrar(host.Services));
        app.Configure(config =>
        {
            config.SetApplicationName("svai");
            config.SetExceptionHandler((ex, _) =>
            {
                AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
                return -1;
            });

            config.AddCommand<PricesCommand>("prices")
                .WithDescription("Fetch and display daily OHLCV bars for a ticker.");

            config.AddCommand<OptionsCommand>("options")
                .WithDescription("Fetch and display a lightweight options snapshot for a ticker.");

            config.AddCommand<DoctorCommand>("doctor")
                .WithDescription("Show provider selection and whether required API keys are present.");

            config.AddCommand<ScreenCommand>("screen")
                .WithDescription("Run a v0 screen over one or more tickers using prices + fundamentals.");

            // Commands will be wired after implementations exist.
            // config.AddBranch("ingest", b => { /* ... */ });
            // config.AddCommand<ExplainCommand>("explain");
        });

        return await app.RunAsync(args);
    }
}

// Simple Spectre TypeRegistrar to bridge Microsoft DI
class TypeRegistrar : ITypeRegistrar
{
    private readonly IServiceProvider _provider;
    public TypeRegistrar(IServiceProvider provider) => _provider = provider;
    public ITypeResolver Build() => new TypeResolver(_provider);
    public void Register(Type service, Type implementation) { /* not used, DI handles */ }
    public void RegisterInstance(Type service, object implementation) { /* not used */ }
    public void RegisterLazy(Type service, Func<object> factory) { /* not used */ }
}

class TypeResolver : ITypeResolver, IDisposable
{
    private readonly IServiceProvider _provider;
    public TypeResolver(IServiceProvider provider) => _provider = provider;
    public object? Resolve(Type? type) => _provider.GetService(type!);
    public void Dispose() { /* no-op */ }
}
