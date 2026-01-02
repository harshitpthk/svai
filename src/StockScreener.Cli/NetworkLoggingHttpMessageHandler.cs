using System.Diagnostics;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace StockScreener.Cli;

/// <summary>
/// Logs outgoing HTTP requests and incoming HTTP responses.
/// Intended for interactive debugging; enable via --log-network.
/// </summary>
public sealed class NetworkLoggingHttpMessageHandler : DelegatingHandler
{
    private readonly ILogger<NetworkLoggingHttpMessageHandler> _logger;
    private readonly INetworkLogContext _ctx;

    public NetworkLoggingHttpMessageHandler(
        ILogger<NetworkLoggingHttpMessageHandler> logger,
        INetworkLogContext ctx)
    {
        _logger = logger;
        _ctx = ctx;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!_ctx.Enabled)
            return await base.SendAsync(request, cancellationToken);

        var sw = Stopwatch.StartNew();

        _logger.LogInformation("HTTP {Method} {Url}", request.Method.Method, request.RequestUri);

        if (request.Headers is not null)
        {
            var auth = request.Headers.Authorization;
            if (auth is not null)
            {
                // Avoid leaking secrets.
                _logger.LogInformation("HTTP request header: Authorization: {Scheme} ***", auth.Scheme);
            }

            LogHeaders("HTTP request header", request.Headers);

            if (request.Content?.Headers is not null)
                LogHeaders("HTTP request content header", request.Content.Headers);
        }

        var response = await base.SendAsync(request, cancellationToken);
        sw.Stop();

        _logger.LogInformation(
            "HTTP {StatusCode} ({ElapsedMs} ms) {Method} {Url}",
            (int)response.StatusCode,
            sw.ElapsedMilliseconds,
            request.Method.Method,
            request.RequestUri);

        if (response.Headers is not null)
            LogHeaders("HTTP response header", response.Headers);

        if (response.Content?.Headers is not null)
            LogHeaders("HTTP response content header", response.Content.Headers);

        return response;
    }

    private void LogHeaders(string prefix, HttpHeaders headers)
    {
        foreach (var (k, v) in headers)
        {
            if (string.Equals(k, "Authorization", StringComparison.OrdinalIgnoreCase))
                continue;

            _logger.LogInformation("{Prefix}: {Key}: {Value}", prefix, k, string.Join(",", v));
        }
    }
}
