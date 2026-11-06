using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using k8s;
using StressManager;
using StressManager.Actions;
using StressManager.Calculations;
using StressManager.Services;

if (args.FirstOrDefault() == "annotate")
{
    using var loggers = LoggerFactory.Create(b => b.AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ ";
        o.UseUtcTimestamp = true;
    }));
    return await Annotator.RunAsync(loggers);
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(8080));
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ ";
    o.UseUtcTimestamp = true;
});

var settings = Settings.From(Environment.GetEnvironmentVariable);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(TimeProvider.System);
// In cluster: service account. Locally: $KUBECONFIG, then ~/.kube/config.
builder.Services.AddSingleton<IKubernetes>(_ => new Kubernetes(KubernetesClientConfiguration.BuildDefaultConfig()));
builder.Services.AddSingleton(sp => new HealthModelClient(
    ArmClients.ClientOrNull(settings), settings, sp.GetRequiredService<ILogger<HealthModelClient>>()));
builder.Services.AddSingleton<StressNodes>();
builder.Services.AddSingleton<NodeMetricsReader>();
builder.Services.AddSingleton<CronJobs>();
builder.Services.AddSingleton<RhythmManager>();
builder.Services.AddSingleton<RunManager>();
// Local runs against a live cluster (demos, screen recordings): serve live data, never write it.
var readOnly = Environment.GetEnvironmentVariable("STRESS_READ_ONLY") == "true";
if (!readOnly)
{
    builder.Services.AddHostedService<HealthModelReconciler>();
    builder.Services.AddHostedService<RhythmReconciler>();
    builder.Services.AddHostedService<EndAnnotationBackstop>();
}
builder.Services.AddTimeline();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = (ctx, _) =>
    {
        ctx.HttpContext.Response.Redirect($"{AccessGate.LoginPage}?limit=1");
        return ValueTask.CompletedTask;
    };
    // Behind the ingress every request comes from nginx, so partition by the forwarded client address.
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Request.Headers["X-Forwarded-For"].ToString().Split(',')[0].Trim() is { Length: > 0 } ip ? ip : ctx.Connection.RemoteIpAddress?.ToString() ?? "",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

var accessCode = Environment.GetEnvironmentVariable(AccessGate.CodeVariable) is { Length: > 0 } code ? code : null;

var app = builder.Build();
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("StressManager");
log.LogInformation(
    "config: namespace={Namespace} nodeLabel={NodeLabel} managerImage={ManagerImage} stressImage={StressImage} healthModel={Model} rg={ResourceGroup} location={Location} identity={Identity}",
    settings.Namespace, settings.NodeLabel, settings.ManagerImage, StressJobs.StressNgImage, settings.HealthModelName,
    settings.ResourceGroup, settings.HealthModelLocation, settings.ManagedIdentityId);
if (settings.ManagerImage is null)
    log.LogWarning("config: MANAGER_IMAGE not set, applying a rhythm will fail");
if (!settings.HealthModelEnabled)
    log.LogWarning("config: health model and annotations disabled, missing {Missing}", string.Join(", ", settings.MissingHealthModelVariables));

app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (BadHttpRequestException ex) when (!context.Response.HasStarted)
    {
        log.LogWarning("http: {Method} {Path} bad request: {Message}", context.Request.Method, context.Request.Path, ex.Message);
        context.Response.StatusCode = ex.StatusCode;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
    catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested && !context.Response.HasStarted)
    {
        log.LogError(ex, "http: {Method} {Path} failed", context.Request.Method, context.Request.Path);
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});

if (accessCode is null)
    log.LogWarning("config: {Variable} not set, the site is open", AccessGate.CodeVariable);
else
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path.Value ?? "/";
        if (AccessGate.IsPublic(path) || AccessGate.TokenMatches(context.Request.Cookies[AccessGate.CookieName], accessCode))
        {
            await next(context);
            return;
        }
        if (AccessGate.IsApi(path))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "access code required" });
            return;
        }
        context.Response.Redirect($"{AccessGate.LoginPage}?next={Uri.EscapeDataString(path + context.Request.QueryString)}");
    });

app.UseRateLimiter();

if (readOnly)
{
    log.LogWarning("config: STRESS_READ_ONLY, background writers off and rhythm changes rejected");
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path.Value ?? "";
        if (AccessGate.IsApi(path) && !HttpMethods.IsGet(context.Request.Method) && path != "/api/rhythm/preview")
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "read-only mode" });
            return;
        }
        await next(context);
    });
}

app.MapPost(AccessGate.LoginEndpoint, async (HttpContext context) =>
{
    var form = await context.Request.ReadFormAsync();
    var target = AccessGate.SafeNext(form["next"]);
    if (accessCode is null || !AccessGate.CodeMatches(form["code"], accessCode))
    {
        log.LogWarning("http: login failed");
        return Results.Redirect($"{AccessGate.LoginPage}?error=1&next={Uri.EscapeDataString(target)}");
    }
    context.Response.Cookies.Append(AccessGate.CookieName, AccessGate.Token(accessCode), new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = context.Request.IsHttps,
        MaxAge = TimeSpan.FromDays(30),
        Path = "/",
    });
    return Results.Redirect(target);
}).RequireRateLimiting("login").DisableAntiforgery();

app.UseResponseCompression();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapTimeline();

app.MapGet("/healthz", () => Results.Ok("ok"));

app.MapGet("/api/runs/active", async (RunManager runs, CancellationToken ct) =>
    await runs.GetActiveAsync(ct) is { } run ? Results.Ok(run) : Results.NoContent());

app.MapGet("/api/rhythm", async (RhythmManager rhythm, CancellationToken ct) => Results.Ok(await rhythm.GetAsync(ct)));

app.MapPost("/api/rhythm/preview", (RhythmRequest? body, RhythmManager rhythm) => ToResult(rhythm.Preview(body?.Steps)));

app.MapPut("/api/rhythm", async (RhythmRequest? body, RhythmManager rhythm) => ToResult(await rhythm.ApplyAsync(body?.Steps)));

app.MapPost("/api/rhythm/pause", async (RhythmManager rhythm) => ToResult(await rhythm.PauseAsync()));

app.MapPost("/api/rhythm/resume", async (RhythmManager rhythm) => ToResult(await rhythm.ResumeAsync()));

app.MapDelete("/api/rhythm", async (RhythmManager rhythm) => ToResult(await rhythm.ClearAsync()));

app.MapGet("/api/metrics/nodes", async (NodeMetricsReader metrics, CancellationToken ct) =>
    Results.Ok(await metrics.ReadAsync(ct)));

app.Run();
return 0;

static IResult ToResult(RhythmOutcome outcome) =>
    outcome.View is { } view ? Results.Ok(view) : Error(outcome.Status, outcome.Error ?? "rhythm request failed");

static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);

internal sealed record RhythmRequest(IReadOnlyList<Step?>? Steps);
