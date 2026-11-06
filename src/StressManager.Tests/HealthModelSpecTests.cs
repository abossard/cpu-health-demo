using System.ClientModel.Primitives;
using System.Text.Json;
using Azure.ResourceManager.CloudHealth;
using StressManager.Calculations;

namespace StressManager.Tests;

public class HealthModelSpecTests
{
    private const string Vmss = "/subscriptions/s1/resourceGroups/mc/providers/Microsoft.Compute/virtualMachineScaleSets/aks-stress-vmss/virtualMachines/";
    private const string IdentityId = "/subscriptions/s1/resourceGroups/rg-demo/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-demo";

    private static StressNode Node(string name, int instance) => new(name, "azure://" + Vmss + instance);

    [Theory]
    [InlineData(new[] { "n3", "n1", "n2" }, new[] { "n1", "n2", "n3" })]
    [InlineData(new[] { "n1" }, new[] { "n1" })]
    [InlineData(new string[0], new string[0])]
    public void Compute_OneEntityAndRelationshipPerNode(string[] nodes, string[] expectedOrder)
    {
        var desired = HealthModelSpec.Compute("hm-demo", nodes.Select((n, i) => Node(n, i)));

        Assert.Equal(expectedOrder, desired.Nodes.Select(n => n.Name));
        Assert.All(desired.Nodes, n => Assert.Equal($"AKS node: {n.Name}", n.DisplayName));
        Assert.Equal(
            new[] { "hm-demo-observe-cpu-pressure" }.Concat(expectedOrder.Select(n => $"observe-cpu-pressure-{n}")),
            desired.Relationships.Select(r => r.Name));
        Assert.Equal(("hm-demo", "observe-cpu-pressure"), (desired.Relationships[0].Parent, desired.Relationships[0].Child));
        Assert.All(desired.Relationships.Skip(1), r => Assert.Equal("observe-cpu-pressure", r.Parent));
        Assert.Equal(
            new[] { "hm-demo", "observe-cpu-pressure" }.Concat(expectedOrder).ToHashSet(),
            desired.EntityNames.ToHashSet());
        Assert.Empty(desired.SkippedNodes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("kind://docker/kind/worker")]
    [InlineData("")]
    public void Compute_SkipsNodesWithoutAzureProviderId(string? providerId)
    {
        var desired = HealthModelSpec.Compute("hm", [Node("good", 0), new StressNode("bad", providerId)]);

        Assert.Equal(["good"], desired.Nodes.Select(n => n.Name));
        Assert.Equal(["bad"], desired.SkippedNodes);
        Assert.DoesNotContain("bad", desired.EntityNames);
    }

    [Theory]
    [InlineData(new[] { "hm", "observe-cpu-pressure", "n1", "gone" }, new[] { "gone" })]
    [InlineData(new[] { "hm", "observe-cpu-pressure", "N1" }, new string[0])]
    [InlineData(new[] { "z-old", "a-old" }, new[] { "a-old", "z-old" })]
    [InlineData(new string[0], new string[0])]
    public void Stale_IsExistingMinusDesired(string[] existing, string[] expected)
    {
        var desired = HealthModelSpec.Compute("hm", [Node("n1", 0)]);

        Assert.Equal(expected, HealthModelSpec.Stale(existing, desired.EntityNames));
    }

    [Theory]
    [InlineData(new[] { "b", "a" }, new[] { "a", "b" }, true)]
    [InlineData(new[] { "a", "b" }, new[] { "a" }, false)]
    [InlineData(new[] { "a" }, new[] { "c" }, false)]
    public void Fingerprint_ChangesOnlyWithNodeSet(string[] left, string[] right, bool same)
    {
        var l = HealthModelSpec.Fingerprint(left.Select(n => Node(n, n[0])));
        var r = HealthModelSpec.Fingerprint(right.Select(n => Node(n, n[0])));

        Assert.Equal(same, l == r);
    }

    [Theory]
    [InlineData("properties.signalKind", "AzureResourceMetric")]
    [InlineData("properties.displayName", "Average CPU utilization")]
    [InlineData("properties.metricNamespace", "Microsoft.Compute/virtualMachineScaleSets/virtualMachines")]
    [InlineData("properties.metricName", "Percentage CPU")]
    [InlineData("properties.aggregationType", "Average")]
    [InlineData("properties.timeGrain", "PT5M")]
    [InlineData("properties.refreshInterval", "PT1M")]
    [InlineData("properties.dataUnit", "Percent")]
    [InlineData("properties.evaluationRules.degradedRule.operator", "GreaterThan")]
    [InlineData("properties.evaluationRules.degradedRule.threshold", "90")]
    [InlineData("properties.evaluationRules.unhealthyRule.operator", "GreaterThan")]
    [InlineData("properties.evaluationRules.unhealthyRule.threshold", "98")]
    public void CpuSignalDefinition_WireFormat(string path, string expected) =>
        Assert.Equal(expected, At(Json(HealthModelSpec.CpuSignalDefinition()), path));

    [Theory]
    [InlineData("properties.displayName", "AKS node: aks-stress-vmss000001")]
    [InlineData("properties.signalGroups.azureResource.authenticationSetting", "uami")]
    [InlineData("properties.signalGroups.azureResource.azureResourceId", Vmss + "1")]
    [InlineData("properties.signalGroups.azureResource.signals.0.signalKind", "AzureResourceMetric")]
    [InlineData("properties.signalGroups.azureResource.signals.0.name", "average-cpu")]
    [InlineData("properties.signalGroups.azureResource.signals.0.signalDefinitionName", "average-cpu")]
    [InlineData("properties.canvasPosition.x", "600")]
    [InlineData("properties.canvasPosition.y", "500")]
    public void NodeEntity_WireFormat(string path, string expected)
    {
        var node = HealthModelSpec.Compute("hm", [Node("aks-stress-vmss000001", 1)]).Nodes[0];

        Assert.Equal(expected, At(Json(HealthModelSpec.NodeEntityData(node, new Position(600, 500))), path));
    }

    [Theory]
    [InlineData("properties.displayName", "Observe per-node CPU pressure")]
    [InlineData("properties.icon.iconName", "UserFlow")]
    [InlineData("properties.signalGroups.dependencies.aggregationType", "WorstOf")]
    [InlineData("properties.canvasPosition.x", "300")]
    [InlineData("properties.canvasPosition.y", "250")]
    public void FlowEntity_WireFormat(string path, string expected) =>
        Assert.Equal(expected, At(Json(HealthModelSpec.FlowEntity(new Position(300, 250))), path));

    [Theory]
    [InlineData("properties.displayName", "Observe per-node CPU pressure")]
    [InlineData("properties.icon.iconName", "UserFlow")]
    [InlineData("properties.signalGroups.dependencies.aggregationType", "WorstOf")]
    [InlineData("properties.canvasPosition.x", "300")]
    [InlineData("properties.canvasPosition.y", "0")]
    public void WithPosition_ChangesOnlyTheCanvasPosition(string path, string expected) =>
        Assert.Equal(expected, At(Json(HealthModelSpec.WithPosition(HealthModelSpec.FlowEntity(new Position(10, 20)), new Position(300, 0))), path));

    [Fact]
    public void WithPosition_CreatesMissingProperties() =>
        Assert.Equal("300", At(Json(HealthModelSpec.WithPosition(new Azure.ResourceManager.CloudHealth.HealthModelEntityData(), new Position(300, 0))),
            "properties.canvasPosition.x"));

    [Theory]
    [InlineData("a=1", null, 0, 0, true)]
    [InlineData("", null, 0, 0, true)]
    [InlineData("a=1", "a=1", 0, 0, false)]
    [InlineData("a=1,b=2", "a=1", 0, 0, true)]
    [InlineData("", "a=1", 0, 0, true)]
    [InlineData("a=1", null, 30, 60, false)]
    [InlineData("a=1", null, 60, 60, true)]
    [InlineData("a=1", null, 90, 60, true)]
    public void ShouldReconcile_OnlyOnChangeAndAfterRetryTime(string fingerprint, string? reconciled, int nowSeconds, int retryAtSeconds, bool expected)
    {
        var epoch = new DateTimeOffset(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);

        Assert.Equal(expected, HealthModelSpec.ShouldReconcile(
            fingerprint, reconciled, epoch.AddSeconds(nowSeconds), epoch.AddSeconds(retryAtSeconds)));
    }

    [Theory]
    [InlineData("properties.authenticationKind", "ManagedIdentity")]
    [InlineData("properties.managedIdentityName", IdentityId)]
    public void AuthenticationSetting_WireFormat(string path, string expected) =>
        Assert.Equal(expected, At(Json(HealthModelSpec.AuthenticationSetting(IdentityId)), path));

    [Theory]
    [InlineData("location", "swedencentral")]
    [InlineData("identity.type", "UserAssigned")]
    public void Model_WireFormat(string path, string expected)
    {
        var json = Json(HealthModelSpec.Model("swedencentral", IdentityId));

        Assert.Equal(expected, At(json, path));
        Assert.True(json.GetProperty("identity").GetProperty("userAssignedIdentities").TryGetProperty(IdentityId, out _));
    }

    [Theory]
    [InlineData("hm", "observe-cpu-pressure")]
    [InlineData("observe-cpu-pressure", "aks-stress-vmss000000")]
    public void Relationship_WireFormat(string parent, string child)
    {
        var json = Json(HealthModelSpec.RelationshipData(new Relationship($"{parent}-{child}", parent, child)));

        Assert.Equal((parent, child), (At(json, "properties.parentEntityName"), At(json, "properties.childEntityName")));
    }

    private static JsonElement Json<T>(T model) =>
        JsonDocument.Parse(ModelReaderWriter.Write(model, ModelReaderWriterOptions.Json, AzureResourceManagerCloudHealthContext.Default)).RootElement;

    private static string? At(JsonElement element, string path)
    {
        foreach (var part in path.Split('.'))
        {
            if (int.TryParse(part, out var index))
                element = element[index];
            else if (!element.TryGetProperty(part, out element))
                return null;
        }
        return element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
    }
}
