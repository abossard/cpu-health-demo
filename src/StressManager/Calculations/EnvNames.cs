namespace StressManager.Calculations;

/// <summary>Environment variable names shared by the stress Job template (writer) and the annotator and Settings (readers).</summary>
public static class EnvNames
{
    public const string NodeName = "NODE_NAME";
    public const string RunId = "RUN_ID";
    public const string Minutes = "MINUTES";
    public const string Load = "LOAD";
    public const string Source = "SOURCE";
    public const string StartedFile = "STARTED_FILE";

    public const string SubscriptionId = "AZURE_SUBSCRIPTION_ID";
    public const string ResourceGroup = "AZURE_RESOURCE_GROUP";
    public const string ManagedIdentityId = "AZURE_MANAGED_IDENTITY_ID";
    public const string HealthModelName = "HEALTH_MODEL_NAME";
    public const string HealthModelLocation = "HEALTH_MODEL_LOCATION";

    public static readonly IReadOnlyList<string> HealthModel =
        [SubscriptionId, ResourceGroup, ManagedIdentityId, HealthModelName, HealthModelLocation];
}
