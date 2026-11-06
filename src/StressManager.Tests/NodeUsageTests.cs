using System.Globalization;
using k8s.Models;
using StressManager.Calculations;

namespace StressManager.Tests;

public class NodeUsageTests
{
    private static decimal? Dec(string? value) =>
        value is null ? null : decimal.Parse(value, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("1500m", "1.5")]
    [InlineData("2", "2")]
    [InlineData("1900m", "1.9")]
    [InlineData("123456n", "0.000123456")]
    [InlineData("2000033201n", "2.000033201")]
    [InlineData("250u", "0.00025")]
    [InlineData("4Ki", "4096")]
    [InlineData("1141716Ki", "1169117184")]
    [InlineData("1Gi", "1073741824")]
    [InlineData("1k", "1000")]
    [InlineData("1e3", "1000")]
    [InlineData(null, null)]
    public void Value_OfQuantity(string? quantity, string? expected) =>
        Assert.Equal(Dec(expected), NodeUsage.Value(quantity is null ? null : new ResourceQuantity(quantity)));

    [Theory]
    [InlineData("0.95", "1.9", 50.0)]
    [InlineData("1.9", "1.9", 100.0)]
    [InlineData("2.000033201", "1.9", 105.3)]
    [InlineData("0.000123456", "2", 0.0)]
    [InlineData("0.123", "2", 6.2)]
    [InlineData("1", "0", null)]
    [InlineData(null, "2", null)]
    [InlineData("1", null, null)]
    public void CpuPercent_OfAllocatable(string? usage, string? allocatable, double? expected) =>
        Assert.Equal(expected, NodeUsage.CpuPercent(Dec(usage), Dec(allocatable)));

    [Theory]
    [InlineData("1.999933596", "1226338304", "1.9", 2000L, 105.3, 1226338304L)]
    [InlineData("0.95", "1073741824", "1.9", 950L, 50.0, 1073741824L)]
    [InlineData("0.000123456", "4096", "2", 0L, 0.0, 4096L)]
    [InlineData("1.5", "1048576", null, 1500L, null, 1048576L)]
    public void Combine_MapsSampleToNode(string cpu, string memory, string? allocatable, long millicores, double? percent, long bytes)
    {
        var at = new DateTimeOffset(2026, 11, 5, 20, 23, 22, TimeSpan.Zero);

        var result = Assert.Single(NodeUsage.Combine(
            new Dictionary<string, decimal?> { ["n1"] = Dec(allocatable) },
            [new MetricSample("n1", Dec(cpu), Dec(memory), at)]));

        Assert.Equal(new NodeCpu("n1", millicores, percent, bytes, at), result);
    }

    [Theory]
    [InlineData(new[] { "c", "a", "b" }, new[] { "a" }, new[] { "a", "b", "c" }, 1)]
    [InlineData(new[] { "a" }, new[] { "a", "gone" }, new[] { "a" }, 1)]
    [InlineData(new[] { "a", "b" }, new string[0], new[] { "a", "b" }, 0)]
    [InlineData(new string[0], new[] { "a" }, new string[0], 0)]
    public void Combine_OneEntryPerNodeSortedAndNullWithoutSample(
        string[] nodes, string[] sampled, string[] expectedOrder, int withMetrics)
    {
        var result = NodeUsage.Combine(
            nodes.ToDictionary(n => n, _ => (decimal?)2m),
            sampled.Select(n => new MetricSample(n, 1m, 1024m, null)));

        Assert.Equal(expectedOrder, result.Select(r => r.Node));
        Assert.Equal(withMetrics, result.Count(r => r.CpuPercent is not null));
        Assert.All(result.Where(r => r.CpuPercent is null), r => Assert.Equal(new NodeCpu(r.Node, null, null, null, null), r));
    }
}
