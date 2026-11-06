using Azure.ResourceManager;
using Azure.ResourceManager.CloudHealth;
using StressManager.Calculations;

namespace StressManager.Actions;

/// <summary>
/// Never throws: a failed write must not fail the stress. Retries itself (the client has no SDK retries)
/// and checks the entity for the same runId and phase before each retry, so a lost response cannot duplicate.
/// </summary>
public sealed class AnnotationWriter(ArmClient? arm, Settings settings, ILogger log)
{
    private const int Attempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DedupeLookBehind = TimeSpan.FromMinutes(10);

    public bool Enabled => arm is not null && settings.HealthModelEnabled;

    public async Task<bool> TryWriteAsync(
        Run run, string phase, string node, TimeSpan timeout, bool backstop = false, CancellationToken ct = default)
    {
        if (!Enabled)
        {
            log.LogWarning("ARM: health model disabled (missing {Missing}), skipping {Phase} annotation for run {RunId}",
                string.Join(", ", settings.MissingHealthModelVariables), phase, run.RunId);
            return false;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var entity = Entity(node);
        try
        {
            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                try
                {
                    if (attempt > 1 && await AlreadyWrittenAsync(run, phase, node, cts.Token))
                    {
                        log.LogInformation("ARM: {Phase} annotation for run {RunId} already on entity {Node}", phase, run.RunId, node);
                        return true;
                    }
                    log.LogInformation("ARM: add {Phase} annotation on entity {Node} for run {RunId}, attempt {Attempt}",
                        phase, node, run.RunId, attempt);
                    var result = await entity.AddDataAnnotationAsync(Annotations.Build(run, phase, node, backstop), cts.Token);
                    log.LogInformation("ARM: {Phase} annotation {AnnotationId} written on entity {Node} for run {RunId}",
                        phase, result.Value.AnnotationId, node, run.RunId);
                    return true;
                }
                catch (Exception ex) when (!cts.IsCancellationRequested)
                {
                    log.LogWarning(ex, "ARM: {Phase} annotation on entity {Node} for run {RunId} failed, attempt {Attempt} of {Attempts}",
                        phase, node, run.RunId, attempt, Attempts);
                    if (attempt < Attempts)
                        await Task.Delay(RetryDelay * attempt, cts.Token);
                }
            }
            log.LogError("ARM: {Phase} annotation on entity {Node} for run {RunId} not written after {Attempts} attempts",
                phase, node, run.RunId, Attempts);
        }
        catch (Exception ex) when (cts.IsCancellationRequested)
        {
            log.LogError(ex, "ARM: {Phase} annotation on entity {Node} for run {RunId} cancelled or timed out after {Timeout}",
                phase, node, run.RunId, timeout);
        }
        return false;
    }

    /// <summary>Annotations on a node entity since <paramref name="from"/>.</summary>
    public async Task<IReadOnlyList<StoredAnnotation>> ListAsync(string node, DateTimeOffset from, CancellationToken ct)
    {
        var annotations = await new HealthHistoryReader(arm, settings).ReadAnnotationsAsync(node, from, ct);
        return annotations
            .Select(a => new StoredAnnotation(
                a.RunId, a.Phase, DateTimeOffset.FromUnixTimeMilliseconds(a.T), a.Minutes ?? 0, a.Load ?? 0, a.Source))
            .ToList();
    }

    private async Task<bool> AlreadyWrittenAsync(Run run, string phase, string node, CancellationToken ct)
    {
        try
        {
            return Annotations.Contains(await ListAsync(node, run.StartedAt - DedupeLookBehind, ct), run.RunId, phase);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "ARM: could not check entity {Node} for an existing {Phase} annotation of run {RunId}", node, phase, run.RunId);
            return false;
        }
    }

    private HealthModelEntityResource Entity(string node) =>
        arm!.GetHealthModelEntityResource(HealthModelEntityResource.CreateResourceIdentifier(
            settings.SubscriptionId!, settings.ResourceGroup!, settings.HealthModelName!, node));
}
