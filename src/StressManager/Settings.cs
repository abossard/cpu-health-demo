using StressManager.Calculations;

namespace StressManager;

public sealed record Settings(
    string? SubscriptionId,
    string? ResourceGroup,
    string? ManagedIdentityId,
    string? HealthModelName,
    string? HealthModelLocation,
    string Namespace,
    string NodeLabel,
    string? ManagerImage)
{
    public const string NamespaceVariable = "STRESS_NAMESPACE";
    public const string NodeLabelVariable = "STRESS_NODE_LABEL";
    public const string ManagerImageVariable = "MANAGER_IMAGE";

    /// <summary>Throws <see cref="FormatException"/> when STRESS_NODE_LABEL is not exactly one `key=value`.</summary>
    public static Settings From(Func<string, string?> env)
    {
        string? Get(string key) => string.IsNullOrWhiteSpace(env(key)) ? null : env(key)!.Trim();
        var settings = new Settings(
            Get(EnvNames.SubscriptionId),
            Get(EnvNames.ResourceGroup),
            Get(EnvNames.ManagedIdentityId),
            Get(EnvNames.HealthModelName),
            Get(EnvNames.HealthModelLocation),
            Get(NamespaceVariable) ?? "stress-manager",
            Get(NodeLabelVariable) ?? "workload=stress",
            Get(ManagerImageVariable));
        ParseNodeLabel(settings.NodeLabel);
        return settings;
    }

    /// <summary>`key=value` with a non-empty key and value, nothing else (no selector operators, lists or spaces).</summary>
    public static (string Key, string Value) ParseNodeLabel(string label)
    {
        var parts = label.Split('=');
        if (parts.Length != 2 || parts.Any(p => p.Length == 0 || p.Any(c => char.IsWhiteSpace(c) || c is ',' or '!' or '(' or ')')))
            throw new FormatException($"{NodeLabelVariable} must be exactly one key=value, got '{label}'");
        return (parts[0], parts[1]);
    }

    private IEnumerable<(string Name, string? Value)> HealthModelVariables =>
    [
        (EnvNames.SubscriptionId, SubscriptionId),
        (EnvNames.ResourceGroup, ResourceGroup),
        (EnvNames.ManagedIdentityId, ManagedIdentityId),
        (EnvNames.HealthModelName, HealthModelName),
        (EnvNames.HealthModelLocation, HealthModelLocation),
    ];

    public IReadOnlyList<string> MissingHealthModelVariables =>
        HealthModelVariables.Where(v => v.Value is null).Select(v => v.Name).ToList();

    public bool HealthModelEnabled => MissingHealthModelVariables.Count == 0;

    public JobTemplateConfig JobTemplate
    {
        get
        {
            var (key, value) = ParseNodeLabel(NodeLabel);
            return new JobTemplateConfig(
                ManagerImage ?? throw new InvalidOperationException($"{ManagerImageVariable} is not set, cannot build stress Jobs"),
                StressJobs.StressNgImage,
                key,
                value,
                HealthModelVariables.Where(v => v.Value is not null).ToDictionary(v => v.Name, v => v.Value!));
        }
    }
}
