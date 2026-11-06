namespace StressManager.Actions;

/// <summary>
/// Logs each k8s call before it runs. Failures propagate to the top-level handler, which logs them once.
/// Polling reads pass LogLevel.Debug to keep `kubectl logs` readable.
/// </summary>
public static class Kube
{
    public static Task<T> Call<T>(ILogger log, string what, Func<Task<T>> call, LogLevel level = LogLevel.Information)
    {
        log.Log(level, "k8s: {What}", what);
        return call();
    }
}
