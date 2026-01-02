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
using System.Diagnostics.CodeAnalysis;

namespace StockScreener.Cli;

class Program
{
    public static async Task<int> Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((ctx, cfg) =>
            {
                cfg.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
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
                        _ => sp.GetRequiredService<StockScreener.Data.Price.StooqPriceProvider>()
                    };
                });

                // Fundamentals providers
                services.AddHttpClient<PolygonFundamentalsProvider>();
                services.AddHttpClient<AlphaVantageFundamentalsProvider>();
                services.AddSingleton<IFundamentalsProvider, PolygonFundamentalsProvider>();

                // Starter providers so we can run the scoring pipeline without external APIs.
                services.AddSingleton<IMacroDataProvider, MacroDataProvider>();
                services.AddSingleton<IOptionsDataProvider, StockOptionsDataProvider>();

                // Commands are resolved by DI through the Spectre TypeRegistrar.
                services.AddTransient<PricesCommand>();
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

            // Commands will be wired after implementations exist.
            // config.AddBranch("ingest", b => { /* ... */ });
            // config.AddCommand<ScreenCommand>("screen");
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
