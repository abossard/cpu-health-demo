using StressManager.Calculations;

namespace StressManager.Tests;

public class TreeLayoutTests
{
    private static Relationship[] Edges(params (string Parent, string Child)[] edges) =>
        edges.Select(e => new Relationship($"{e.Parent}-{e.Child}", e.Parent, e.Child)).ToArray();

    public static TheoryData<Relationship[], string, float, float> Cases => new()
    {
        // Demo shape: root -> flow -> three nodes, nodes ordered by name left to right.
        { Edges(("hm", "flow"), ("flow", "n2"), ("flow", "n0"), ("flow", "n1")), "hm", 300, 0 },
        { Edges(("hm", "flow"), ("flow", "n2"), ("flow", "n0"), ("flow", "n1")), "flow", 300, 250 },
        { Edges(("hm", "flow"), ("flow", "n2"), ("flow", "n0"), ("flow", "n1")), "n0", 0, 500 },
        { Edges(("hm", "flow"), ("flow", "n2"), ("flow", "n0"), ("flow", "n1")), "n1", 300, 500 },
        { Edges(("hm", "flow"), ("flow", "n2"), ("flow", "n0"), ("flow", "n1")), "n2", 600, 500 },
        // Root without children.
        { Edges(), "hm", 0, 0 },
        // Single child sits straight below its parent.
        { Edges(("hm", "flow"), ("flow", "n0")), "n0", 0, 500 },
        // Uneven subtrees: parent centred over its own children only.
        { Edges(("hm", "a"), ("hm", "b"), ("a", "a1"), ("a", "a2"), ("b", "b1")), "a", 150, 250 },
        { Edges(("hm", "a"), ("hm", "b"), ("a", "a1"), ("a", "a2"), ("b", "b1")), "b", 600, 250 },
        { Edges(("hm", "a"), ("hm", "b"), ("a", "a1"), ("a", "a2"), ("b", "b1")), "hm", 375, 0 },
        // Shared child is placed once, under the first parent reached.
        { Edges(("hm", "a"), ("hm", "b"), ("a", "s"), ("b", "s")), "s", 0, 500 },
        // A cycle back to the root does not recurse forever.
        { Edges(("hm", "a"), ("a", "hm")), "a", 0, 250 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Compute_PlacesEntity(Relationship[] relationships, string entity, float x, float y) =>
        Assert.Equal(new Position(x, y), TreeLayout.Compute("hm", relationships)[entity]);

    [Theory]
    [InlineData("n0,n1,n2")]
    [InlineData("n0")]
    [InlineData("")]
    public void Compute_SkipsUnreachableEntities(string nodes)
    {
        var relationships = Edges(nodes.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(n => ("other", n)).ToArray());

        Assert.Equal(new[] { "hm" }, TreeLayout.Compute("hm", relationships).Keys);
    }
}
