using System.Runtime.InteropServices;
using StressManager.Actions;
using StressManager.Calculations;

namespace StressManager.Services;

/// <summary>
/// `annotate` mode, run as native sidecar of every stress pod: waits for the stress container's started file,
/// writes `start`, waits for SIGTERM (sent by kubelet after stress-ng exits, on deadline or deletion), writes
/// `end` if `start` was written. Always exits 0.
/// </summary>
public static class Annotator
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StartGraceAfterSigterm = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EndTimeout = TimeSpan.FromSeconds(20); // inside terminationGracePeriodSeconds 30
    private static readonly TimeSpan StartedFilePoll = TimeSpan.FromSeconds(1);

    public static async Task<int> RunAsync(ILoggerFactory loggers)
    {
        var log = loggers.CreateLogger("Annotator");
        var terminated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSignal(PosixSignalContext context)
        {
            context.Cancel = true;
            log.LogInformation("annotate: received {Signal}", context.Signal);
            terminated.TrySetResult();
        }
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);

        try
        {
            var settings = Settings.From(Environment.GetEnvironmentVariable);
            var (run, node, error) = AnnotatorInput.From(Environment.GetEnvironmentVariable, DateTimeOffset.UtcNow);
            if (run is null || node is null)
            {
                log.LogError("annotate: {Error}, no annotations will be written", error);
                await terminated.Task;
                return 0;
            }

            log.LogInformation("annotate: run {RunId} on {Node}, {Minutes} min @ {Load}% ({Source})",
                run.RunId, node, run.Minutes, run.Load, run.Source);
            var writer = new AnnotationWriter(ArmClients.AnnotationClientOrNull(settings), settings, log);

            var startedFile = AnnotatorInput.StartedFile(Environment.GetEnvironmentVariable);
            if (!await WaitForStartedFileAsync(startedFile, terminated.Task, log))
            {
                log.LogWarning("annotate: terminated before {File} appeared, no annotations written", startedFile);
                return 0;
            }

            using var startCts = new CancellationTokenSource();
            var start = writer.TryWriteAsync(run, Annotations.Start, node, StartTimeout, ct: startCts.Token);
            await terminated.Task;
            startCts.CancelAfter(StartGraceAfterSigterm);
            if (await start)
                await writer.TryWriteAsync(run, Annotations.End, node, EndTimeout);
            else
                log.LogWarning("annotate: start annotation was not written, skipping end");
            log.LogInformation("annotate: done");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "annotate: unexpected failure, exiting 0 so the stress is not affected");
        }
        return 0;
    }

    /// <summary>True once the file exists (or immediately when no file is configured); false if SIGTERM comes first.</summary>
    private static async Task<bool> WaitForStartedFileAsync(string? startedFile, Task terminated, ILogger log)
    {
        if (startedFile is null)
            return true;
        log.LogInformation("annotate: waiting for {File}", startedFile);
        while (!terminated.IsCompleted)
        {
            if (File.Exists(startedFile))
            {
                log.LogInformation("annotate: {File} exists, stress started", startedFile);
                return true;
            }
            await Task.WhenAny(terminated, Task.Delay(StartedFilePoll));
        }
        return false;
    }
}
