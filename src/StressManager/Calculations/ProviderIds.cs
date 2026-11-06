namespace StressManager.Calculations;

public static class ProviderIds
{
    private const string Prefix = "azure://";

    /// <summary>
    /// Kubernetes node spec.providerID to ARM resource ID, e.g.
    /// azure:///subscriptions/s/resourceGroups/mc/providers/Microsoft.Compute/virtualMachineScaleSets/v/virtualMachines/0
    /// becomes /subscriptions/s/resourceGroups/mc/providers/Microsoft.Compute/virtualMachineScaleSets/v/virtualMachines/0.
    /// Returns null for anything that is not an Azure ARM ID.
    /// </summary>
    public static string? ToArmId(string? providerId)
    {
        if (providerId is null || !providerId.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var id = providerId[Prefix.Length..];
        return id.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)
               && id.Contains("/providers/", StringComparison.OrdinalIgnoreCase)
            ? id
            : null;
    }
}
