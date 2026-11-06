using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.ResourceManager;
using StressManager.Calculations;

namespace StressManager.Actions;

/// <summary>Raw 1-min Percentage CPU from the Azure Monitor Metrics API, sent through an ARM pipeline with retries.</summary>
public sealed class RawCpuReader(HttpPipeline? pipeline)
{
    private static readonly Uri Arm = new("https://management.azure.com");

    /// <summary>Null pipeline when the health model env is incomplete; the node resource IDs come from the model.</summary>
    public static RawCpuReader Create(Settings settings) => new(settings.HealthModelEnabled
        ? HttpPipelineBuilder.Build(new ArmClientOptions(),
            new BearerTokenAuthenticationPolicy(new DefaultAzureCredential(), "https://management.azure.com/.default"))
        : null);

    public bool Enabled => pipeline is not null;

    public async Task<IReadOnlyList<RawCpuPoint>> ReadAsync(string resourceId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        using var message = pipeline!.CreateMessage();
        message.Request.Method = RequestMethod.Get;
        message.Request.Uri.Reset(new Uri(Arm, Timeline.RawCpuPath(resourceId, from, to)));
        await pipeline.SendAsync(message, ct);
        if (message.Response.IsError)
            throw new RequestFailedException(message.Response);
        return Timeline.ParseRawCpu(message.Response.Content.ToString());
    }
}
