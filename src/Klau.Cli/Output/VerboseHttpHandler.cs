using System.Diagnostics;

namespace Klau.Cli.Output;

/// <summary>
/// DelegatingHandler that logs HTTP request/response details to stderr
/// when --verbose mode is active. Designed for support troubleshooting.
/// </summary>
internal sealed class VerboseHttpHandler : DelegatingHandler
{
    public VerboseHttpHandler() : base(new HttpClientHandler()) { }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var method = request.Method.Method;
        var uri = request.RequestUri?.PathAndQuery ?? request.RequestUri?.ToString() ?? "?";

        Console.Error.Write($"  [verbose] {method} {uri}");

        var sw = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.Error.WriteLine($" -> ERROR ({sw.ElapsedMilliseconds}ms) {ex.GetType().Name}: {ex.Message}");
            throw;
        }

        sw.Stop();
        var status = (int)response.StatusCode;
        Console.Error.WriteLine($" -> {status} ({sw.ElapsedMilliseconds}ms)");

        return response;
    }
}

/// <summary>
/// Factory for creating HttpClient instances with verbose logging when enabled.
/// </summary>
internal static class CliHttp
{
    /// <summary>
    /// Create an HttpClient with the given timeout. When --verbose is active,
    /// wraps with a handler that logs HTTP details to stderr.
    /// </summary>
    public static HttpClient CreateClient(TimeSpan? timeout = null)
    {
        var client = OutputMode.Verbose
            ? new HttpClient(new VerboseHttpHandler())
            : new HttpClient();

        if (timeout is { } t)
            client.Timeout = t;

        return client;
    }
}
