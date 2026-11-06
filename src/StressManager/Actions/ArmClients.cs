using Azure.Identity;
using Azure.ResourceManager;

namespace StressManager.Actions;

/// <summary>The only place that builds an <see cref="ArmClient"/>. Null when the health model env is incomplete.</summary>
public static class ArmClients
{
    public static ArmClient? ClientOrNull(Settings settings) =>
        settings.HealthModelEnabled ? new ArmClient(new DefaultAzureCredential(), settings.SubscriptionId) : null;

    /// <summary>
    /// Client without SDK retries. A retried POST can add a second annotation after a lost response, so
    /// <see cref="AnnotationWriter"/> retries itself and checks for the annotation first.
    /// </summary>
    public static ArmClient? AnnotationClientOrNull(Settings settings)
    {
        if (!settings.HealthModelEnabled)
            return null;
        var options = new ArmClientOptions();
        options.Retry.MaxRetries = 0;
        return new ArmClient(new DefaultAzureCredential(), settings.SubscriptionId, options);
    }
}
