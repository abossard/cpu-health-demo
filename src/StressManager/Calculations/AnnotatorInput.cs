using System.Globalization;

namespace StressManager.Calculations;

public static class AnnotatorInput
{
    /// <summary>The run as one node sees it, or an error naming the missing/invalid variables.</summary>
    public static (Run? Run, string? Node, string? Error) From(Func<string, string?> env, DateTimeOffset now)
    {
        string? Get(string key) => string.IsNullOrWhiteSpace(env(key)) ? null : env(key)!.Trim();
        int? Int(string key) => int.TryParse(Get(key), CultureInfo.InvariantCulture, out var v) ? v : null;

        var node = Get(EnvNames.NodeName);
        var runId = Get(EnvNames.RunId);
        var minutes = Int(EnvNames.Minutes);
        var load = Int(EnvNames.Load);
        var source = Get(EnvNames.Source);
        var missing = new (string Name, bool Ok)[]
        {
            (EnvNames.NodeName, node is not null), (EnvNames.RunId, runId is not null), (EnvNames.Minutes, minutes is not null),
            (EnvNames.Load, load is not null), (EnvNames.Source, source is not null),
        }.Where(v => !v.Ok).Select(v => v.Name).ToList();
        if (missing.Count > 0)
            return (null, null, "missing or invalid env: " + string.Join(", ", missing));
        return (new Run(runId!, minutes!.Value, load!.Value, source!, now, now.AddMinutes(minutes.Value), [node!]), node, null);
    }

    /// <summary>Path the stress container creates right before it starts stress-ng. Null means start immediately.</summary>
    public static string? StartedFile(Func<string, string?> env) =>
        string.IsNullOrWhiteSpace(env(EnvNames.StartedFile)) ? null : env(EnvNames.StartedFile)!.Trim();
}
