using StressManager.Calculations;

namespace StressManager.Tests;

public class ProviderIdsTests
{
    [Theory]
    [InlineData(
        "azure:///subscriptions/s1/resourceGroups/mc_rg_aks_swedencentral/providers/Microsoft.Compute/virtualMachineScaleSets/aks-stress-1-vmss/virtualMachines/0",
        "/subscriptions/s1/resourceGroups/mc_rg_aks_swedencentral/providers/Microsoft.Compute/virtualMachineScaleSets/aks-stress-1-vmss/virtualMachines/0")]
    [InlineData(
        "AZURE:///subscriptions/s1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm1",
        "/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm1")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("kind://docker/kind/kind-worker", null)]
    [InlineData("azure://", null)]
    [InlineData("azure:///subscriptions/s1", null)]
    [InlineData("/subscriptions/s1/resourceGroups/rg/providers/x/y/z", null)]
    public void ToArmId_StripsAzurePrefix(string? providerId, string? expected) =>
        Assert.Equal(expected, ProviderIds.ToArmId(providerId));
}
