using Azure;
using Azure.ResourceManager;
using Azure.ResourceManager.CloudHealth;
using Azure.ResourceManager.Resources;
using StressManager.Calculations;

namespace StressManager.Actions;

public sealed class HealthModelClient(ArmClient? arm, Settings settings, ILogger<HealthModelClient> log)
{
    public bool Enabled => arm is not null && settings.HealthModelEnabled;

    /// <summary>Idempotent: creates or updates every desired resource, then deletes stale ones.</summary>
    public async Task ReconcileAsync(IReadOnlyList<StressNode> nodes, CancellationToken ct)
    {
        if (!Enabled)
        {
            log.LogWarning("ARM: health model disabled, missing env {Missing}", string.Join(", ", settings.MissingHealthModelVariables));
            return;
        }

        var desired = HealthModelSpec.Compute(settings.HealthModelName!, nodes);
        foreach (var skipped in desired.SkippedNodes)
            log.LogWarning("ARM: node {Node} has no Azure providerID, no entity created", skipped);

        var rg = arm!.GetResourceGroupResource(
            ResourceGroupResource.CreateResourceIdentifier(settings.SubscriptionId!, settings.ResourceGroup!));

        var model = await Arm($"create or update health model {settings.HealthModelName} in {settings.ResourceGroup}",
            async () => (await rg.GetHealthModels().CreateOrUpdateAsync(WaitUntil.Completed, settings.HealthModelName!,
                HealthModelSpec.Model(settings.HealthModelLocation!, settings.ManagedIdentityId!), ct)).Value);

        await Arm($"create or update authentication setting {HealthModelSpec.AuthenticationSettingName}",
            () => model.GetHealthModelAuthenticationSettings().CreateOrUpdateAsync(WaitUntil.Completed,
                HealthModelSpec.AuthenticationSettingName, HealthModelSpec.AuthenticationSetting(settings.ManagedIdentityId!), ct));

        await Arm($"create or update signal definition {HealthModelSpec.SignalDefinitionName}",
            () => model.GetHealthModelSignalDefinitions().CreateOrUpdateAsync(WaitUntil.Completed,
                HealthModelSpec.SignalDefinitionName, HealthModelSpec.CpuSignalDefinition(), ct));

        var entities = model.GetHealthModelEntities();
        var layout = desired.Layout;
        await Arm($"position root entity {settings.HealthModelName}", async () =>
        {
            var root = (await entities.GetAsync(settings.HealthModelName!, ct)).Value.Data;
            return await entities.CreateOrUpdateAsync(WaitUntil.Completed, settings.HealthModelName!,
                HealthModelSpec.WithPosition(root, layout[settings.HealthModelName!]), ct);
        });
        await Arm($"create or update entity {HealthModelSpec.FlowEntityName}",
            () => entities.CreateOrUpdateAsync(WaitUntil.Completed, HealthModelSpec.FlowEntityName,
                HealthModelSpec.FlowEntity(layout[HealthModelSpec.FlowEntityName]), ct));

        foreach (var node in desired.Nodes)
            await Arm($"create or update entity {node.Name} -> {node.AzureResourceId}",
                () => entities.CreateOrUpdateAsync(WaitUntil.Completed, node.Name, HealthModelSpec.NodeEntityData(node, layout[node.Name]), ct));

        var relationships = model.GetHealthModelRelationships();
        foreach (var relationship in desired.Relationships)
            await Arm($"create or update relationship {relationship.Name} ({relationship.Parent} -> {relationship.Child})",
                () => relationships.CreateOrUpdateAsync(WaitUntil.Completed, relationship.Name,
                    HealthModelSpec.RelationshipData(relationship), ct));

        var existingRelationships = await Arm("list relationships", () => ListNamesAsync(relationships.GetAllAsync(cancellationToken: ct)));
        foreach (var stale in HealthModelSpec.Stale(existingRelationships, desired.RelationshipNames))
            await Arm($"delete stale relationship {stale}",
                async () => await (await relationships.GetAsync(stale, ct)).Value.DeleteAsync(WaitUntil.Completed, ct));

        var existingEntities = await Arm("list entities", () => ListNamesAsync(entities.GetAllAsync(cancellationToken: ct)));
        foreach (var stale in HealthModelSpec.Stale(existingEntities, desired.EntityNames))
            await Arm($"delete stale entity {stale}",
                async () => await (await entities.GetAsync(stale, ct)).Value.DeleteAsync(WaitUntil.Completed, ct));

        log.LogInformation("ARM: health model {Model} reconciled with {Count} node entities: {Nodes}",
            settings.HealthModelName, desired.Nodes.Count, string.Join(", ", desired.Nodes.Select(n => n.Name)));
    }

    private static async Task<IReadOnlyList<string>> ListNamesAsync<T>(AsyncPageable<T> pages) where T : ArmResource
    {
        var names = new List<string>();
        await foreach (var item in pages)
            names.Add(item.Id.Name);
        return names;
    }

    private Task Arm(string what, Func<Task> call) => Arm(what, async () => { await call(); return true; });

    private Task<T> Arm<T>(string what, Func<Task<T>> call)
    {
        log.LogInformation("ARM: {What}", what);
        return call();
    }
}
