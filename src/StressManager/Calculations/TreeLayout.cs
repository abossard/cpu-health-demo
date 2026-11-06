namespace StressManager.Calculations;

public sealed record Position(float X, float Y);

/// <summary>
/// Layered tree layout for the health model canvas. Leaves take consecutive columns in name order,
/// a parent sits centred above its first and last child, each depth is one row.
/// </summary>
public static class TreeLayout
{
    public const float ColumnWidth = 300;
    public const float RowHeight = 250;

    /// <summary>Positions for every entity reachable from <paramref name="root"/>. A child with several parents is placed under the first one reached.</summary>
    public static IReadOnlyDictionary<string, Position> Compute(string root, IEnumerable<Relationship> relationships)
    {
        var children = relationships
            .GroupBy(r => r.Parent, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => r.Child).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.Ordinal).ToList(),
                StringComparer.OrdinalIgnoreCase);
        var positions = new Dictionary<string, Position>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nextColumn = 0;

        float Place(string name, int depth)
        {
            visited.Add(name);
            var childXs = new List<float>();
            foreach (var child in children.GetValueOrDefault(name, []))
                if (!visited.Contains(child))
                    childXs.Add(Place(child, depth + 1));
            var x = childXs.Count == 0 ? nextColumn++ * ColumnWidth : (childXs[0] + childXs[^1]) / 2;
            positions[name] = new Position(x, depth * RowHeight);
            return x;
        }

        Place(root, 0);
        return positions;
    }
}
