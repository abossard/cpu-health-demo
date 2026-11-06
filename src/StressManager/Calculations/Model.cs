namespace StressManager.Calculations;

/// <summary>Schedulable means Ready=True and not spec.unschedulable.</summary>
public sealed record StressNode(string Name, string? ProviderId, bool Schedulable = true);

/// <summary>One stress Job.</summary>
public sealed record Run(
    string RunId,
    int Minutes,
    int Load,
    string Source,
    DateTimeOffset StartedAt,
    DateTimeOffset EndsAt,
    IReadOnlyList<string> Nodes);
