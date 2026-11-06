using Azure.Core;
using Azure.ResourceManager.CloudHealth;
using Azure.ResourceManager.CloudHealth.Models;
using Azure.ResourceManager.Models;

namespace StressManager.Calculations;

public sealed record NodeEntity(string Name, string DisplayName, ResourceIdentifier AzureResourceId);

public sealed record Relationship(string Name, string Parent, string Child);

public sealed record DesiredModel(
    string ModelName,
    IReadOnlyList<NodeEntity> Nodes,
    IReadOnlyList<Relationship> Relationships,
    IReadOnlyList<string> SkippedNodes)
{
    /// <summary>The root entity (named after the model) is created by the service and must be kept.</summary>
    public IReadOnlySet<string> EntityNames =>
        new[] { ModelName, HealthModelSpec.FlowEntityName }.Concat(Nodes.Select(n => n.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> RelationshipNames =>
        Relationships.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, Position> Layout => TreeLayout.Compute(ModelName, Relationships);
}

/// <summary>Builders of the health model ARM payloads.</summary>
public static class HealthModelSpec
{
    public const string FlowEntityName = "observe-cpu-pressure";
    public const string FlowDisplayName = "Observe per-node CPU pressure";
    public const string SignalDefinitionName = "average-cpu";
    public const string AuthenticationSettingName = "uami";
    public const string MetricNamespace = "Microsoft.Compute/virtualMachineScaleSets/virtualMachines";
    public const string MetricName = "Percentage CPU";
    public const string TimeGrain = "PT5M";
    public const double DegradedThreshold = 90;
    public const double UnhealthyThreshold = 98;

    public static string NodeDisplayName(string node) => $"AKS node: {node}";

    public static DesiredModel Compute(string modelName, IEnumerable<StressNode> nodes)
    {
        var ordered = nodes.OrderBy(n => n.Name, StringComparer.Ordinal).ToList();
        var entities = ordered
            .Select(n => (n.Name, ArmId: ProviderIds.ToArmId(n.ProviderId)))
            .Where(n => n.ArmId is not null)
            .Select(n => new NodeEntity(n.Name, NodeDisplayName(n.Name), new ResourceIdentifier(n.ArmId!)))
            .ToList();
        var skipped = ordered.Select(n => n.Name).Except(entities.Select(e => e.Name)).ToList();
        var relationships = new[] { new Relationship($"{modelName}-{FlowEntityName}", modelName, FlowEntityName) }
            .Concat(entities.Select(e => new Relationship($"{FlowEntityName}-{e.Name}", FlowEntityName, e.Name)))
            .ToList();
        return new DesiredModel(modelName, entities, relationships, skipped);
    }

    public static IReadOnlyList<string> Stale(IEnumerable<string> existing, IReadOnlySet<string> desired) =>
        existing.Where(name => !desired.Contains(name)).OrderBy(n => n, StringComparer.Ordinal).ToList();

    /// <summary>Changes whenever a stress node appears, disappears or gets a different VM.</summary>
    public static string Fingerprint(IEnumerable<StressNode> nodes) =>
        string.Join(",", nodes.OrderBy(n => n.Name, StringComparer.Ordinal).Select(n => $"{n.Name}={n.ProviderId}"));

    public static HealthModelData Model(string location, string managedIdentityId) => new(new AzureLocation(location))
    {
        Identity = new ManagedServiceIdentity(ManagedServiceIdentityType.UserAssigned)
        {
            UserAssignedIdentities = { [new ResourceIdentifier(managedIdentityId)] = new UserAssignedIdentity() },
        },
    };

    public static HealthModelAuthenticationSettingData AuthenticationSetting(string managedIdentityId) => new()
    {
        Properties = new ManagedIdentityAuthenticationSettingProperties(managedIdentityId)
        {
            DisplayName = "Stress manager managed identity",
        },
    };

    public static HealthModelSignalDefinitionData CpuSignalDefinition() => new()
    {
        Properties = new ResourceMetricSignalDefinitionProperties(
            new EntitySignalEvaluationRule(Threshold(UnhealthyThreshold)) { DegradedRule = Threshold(DegradedThreshold) },
            MetricNamespace,
            MetricName,
            TimeGrain,
            HealthModelMetricAggregationType.Average)
        {
            DisplayName = "Average CPU utilization",
            RefreshInterval = EntitySignalRefreshInterval.OneMinute,
            DataUnit = "Percent",
        },
    };

    /// <summary>The service-created root entity with only its canvas position changed.</summary>
    public static HealthModelEntityData WithPosition(HealthModelEntityData entity, Position position)
    {
        entity.Properties ??= new HealthModelEntityProperties();
        entity.Properties.CanvasPosition = Coordinates(position);
        return entity;
    }

    /// <summary>Write on startup, when the node fingerprint differs from the last successful pass, and not before a failed pass may be retried.</summary>
    public static bool ShouldReconcile(string fingerprint, string? reconciledFingerprint, DateTimeOffset now, DateTimeOffset retryAt) =>
        fingerprint != reconciledFingerprint && now >= retryAt;

    public static HealthModelEntityData FlowEntity(Position position) => new()
    {
        Properties = new HealthModelEntityProperties
        {
            DisplayName = FlowDisplayName,
            CanvasPosition = Coordinates(position),
            Icon = new HealthModelEntityIcon("UserFlow"),
            SignalGroups = new EntitySignalGroups
            {
                Dependencies = new HealthModelDependenciesSignalGroup(HealthStateAggregationType.WorstOf),
            },
        },
    };

    public static HealthModelEntityData NodeEntityData(NodeEntity node, Position position) => new()
    {
        Properties = new HealthModelEntityProperties
        {
            DisplayName = node.DisplayName,
            CanvasPosition = Coordinates(position),
            SignalGroups = new EntitySignalGroups
            {
                AzureResource = new HealthModelResourceSignals(AuthenticationSettingName, node.AzureResourceId)
                {
                    Signals = { new HealthModelResourceMetricSignal(SignalDefinitionName) { SignalDefinitionName = SignalDefinitionName } },
                },
            },
        },
    };

    public static HealthModelRelationshipData RelationshipData(Relationship relationship) => new()
    {
        Properties = new HealthModelRelationshipProperties(relationship.Parent, relationship.Child),
    };

    private static HealthModelEntityCoordinates Coordinates(Position position) => new(position.X, position.Y);

    private static EntitySignalThresholdRule Threshold(double value) =>
        new(EntitySignalOperator.GreaterThan) { Threshold = value };
}
