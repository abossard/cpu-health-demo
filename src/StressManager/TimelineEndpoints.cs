using System.Text.Json;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;
using StressManager.Actions;
using StressManager.Calculations;
using StressManager.Services;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace StressManager;

/// <summary>Health timeline API (docs/aks/timeline.md): GET /api/timeline and the SSE stream.</summary>
public static class TimelineEndpoints
{
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);

    public static IServiceCollection AddTimeline(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<Settings>();
            return new HealthHistoryReader(ArmClients.ClientOrNull(settings), settings);
        });
        services.AddSingleton<TimelineCache>();
        services.AddHostedService<TimelineRefresher>();
        services.AddResponseCompression(o =>
        {
            o.EnableForHttps = true;
            o.Providers.Add<GzipCompressionProvider>();
        });
        return services;
    }

    public static WebApplication MapTimeline(this WebApplication app)
    {
        app.MapGet("/api/timeline", (string? since, TimelineCache cache) =>
            Timeline.TryParseSince(since, out var s)
                ? Results.Ok(cache.Read(s))
                : Results.Json(new { error = $"since '{since}' is not an ISO 8601 time" }, statusCode: StatusCodes.Status400BadRequest));

        app.MapGet("/api/timeline/stream", StreamAsync);
        return app;
    }

    private static async Task StreamAsync(HttpContext http, TimelineCache cache, TimeProvider clock, IOptions<JsonOptions> json)
    {
        var ct = http.RequestAborted;
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        using var subscription = cache.Subscribe();
        try
        {
            await http.Response.Body.FlushAsync(ct);
            var next = subscription.Reader.WaitToReadAsync(ct).AsTask();
            while (true)
            {
                using var pingTimer = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var ping = Task.Delay(PingInterval, clock, pingTimer.Token);
                if (await Task.WhenAny(next, ping) == ping)
                {
                    await WriteEventAsync(http, "ping", $"{{\"t\":{clock.GetUtcNow().ToUnixTimeMilliseconds()}}}", ct);
                    continue;
                }
                await pingTimer.CancelAsync();
                if (!await next)
                    return;
                while (subscription.Reader.TryRead(out var delta))
                    await WriteEventAsync(http, "delta", JsonSerializer.Serialize(delta, json.Value.SerializerOptions), ct);
                next = subscription.Reader.WaitToReadAsync(ct).AsTask();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private static async Task WriteEventAsync(HttpContext http, string name, string data, CancellationToken ct)
    {
        await http.Response.WriteAsync($"event: {name}\ndata: {data}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }
}
