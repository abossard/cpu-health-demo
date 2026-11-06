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
builder.Services.AddHostedService<HealthModelReconciler>();
builder.Services.AddHostedService<RhythmReconciler>();
builder.Services.AddHostedService<EndAnnotationBackstop>();
builder.Services.AddTimeline();

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
