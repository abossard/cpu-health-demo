using System.Globalization;
using Azure.ResourceManager;
using Azure.ResourceManager.CloudHealth;
using Azure.ResourceManager.CloudHealth.Models;
using StressManager.Calculations;

namespace StressManager.Actions;

/// <summary>ARM reads of health model structure and history for the timeline. Pages are followed via nextMarker.</summary>
public sealed class HealthHistoryReader(ArmClient? arm, Settings settings)
{
    public bool Enabled => arm is not null && settings.HealthModelEnabled;

    public async Task<(IReadOnlyList<ModelEntity> Entities, IReadOnlyList<Relationship> Relationships)> ReadModelAsync(CancellationToken ct)
    {
        var model = Model();
        var entities = new List<ModelEntity>();
        await foreach (var entity in model.GetHealthModelEntities().GetAllAsync(cancellationToken: ct))
        {
            var props = entity.Data.Properties;
            var signals = props?.SignalGroups?.AzureResource?.Signals.Select(s => s.Name).ToList() ?? [];
            entities.Add(new ModelEntity(entity.Id.Name, props?.DisplayName ?? entity.Id.Name, signals));
        }
        var relationships = new List<Relationship>();
        await foreach (var rel in model.GetHealthModelRelationships().GetAllAsync(cancellationToken: ct))
        {
            if (rel.Data.Properties is { } p)
                relationships.Add(new Relationship(rel.Id.Name, p.ParentEntityName, p.ChildEntityName));
        }
        return (entities, relationships);
    }

    // The API rejects nextMarker combined with startAt/endAt, so only the first page sends startAt.
    public async Task<IReadOnlyList<StatePoint>> ReadStatesAsync(string entity, DateTimeOffset from, CancellationToken ct)
    {
        var points = new List<StatePoint>();
        string? marker = null;
        do
        {
            var page = (await Entity(entity).GetHistoryAsync(new EntityHistoryContent { StartsOn = marker is null ? from : null, NextMarker = marker }, ct)).Value;
            points.AddRange(page.History.Select(h => new StatePoint(h.OccurredOn.ToUnixTimeMilliseconds(), Timeline.StateCode(h.NewState.ToString()))));
            marker = page.NextMarker;
        } while (!string.IsNullOrEmpty(marker));
        return points;
    }

    public async Task<IReadOnlyList<SignalPoint>> ReadSignalAsync(string entity, string signal, DateTimeOffset from, CancellationToken ct)
    {
        var points = new List<SignalPoint>();
        string? marker = null;
        do
        {
            var page = (await Entity(entity).GetSignalHistoryAsync(
                new EntitySignalHistoryContent(signal) { StartsOn = marker is null ? from : null, NextMarker = marker }, ct)).Value;
            points.AddRange(page.History.Select(h =>
                new SignalPoint(h.OccurredOn.ToUnixTimeMilliseconds(), Timeline.RoundValue(h.Value), Timeline.StateCode(h.HealthState.ToString()))));
            marker = page.NextMarker;
        } while (!string.IsNullOrEmpty(marker));
        return points;
    }

    public async Task<IReadOnlyList<TimelineAnnotation>> ReadAnnotationsAsync(string entity, DateTimeOffset from, CancellationToken ct)
    {
        var annotations = new List<TimelineAnnotation>();
        string? marker = null;
        do
        {
            var page = (await Entity(entity).GetDataAnnotationsAsync(new EntityGetDataAnnotationsContent { StartsOn = marker is null ? from : null, NextMarker = marker }, ct)).Value;
            annotations.AddRange(page.Annotations.Where(a => a.CreatedOn is not null).Select(a => ToAnnotation(entity, a)));
            marker = page.NextMarker;
        } while (!string.IsNullOrEmpty(marker));
        return annotations;
    }

    private static TimelineAnnotation ToAnnotation(string entity, EntityDataAnnotation a)
    {
        string? Detail(string key) => a.AnnotationDetails.TryGetValue(key, out var v) ? v : null;
        int? Int(string key) => int.TryParse(Detail(key), CultureInfo.InvariantCulture, out var v) ? v : null;
        return new TimelineAnnotation(entity, a.AnnotationId, a.CreatedOn!.Value.ToUnixTimeMilliseconds(),
            Detail("phase") ?? "", Detail("runId") ?? "", Int("minutes"), Int("load"), Detail("source") ?? "", a.Description ?? "");
    }

    private HealthModelResource Model() =>
        arm!.GetHealthModelResource(HealthModelResource.CreateResourceIdentifier(
            settings.SubscriptionId!, settings.ResourceGroup!, settings.HealthModelName!));

    private HealthModelEntityResource Entity(string entity) =>
        arm!.GetHealthModelEntityResource(HealthModelEntityResource.CreateResourceIdentifier(
            settings.SubscriptionId!, settings.ResourceGroup!, settings.HealthModelName!, entity));
}
