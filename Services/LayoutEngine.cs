using DiagramApp.Models;

namespace DiagramApp.Services;

public static class LayoutEngine
{
    private const double HGap     = 70;   // horizontal gap between columns
    private const double VGap     = 40;   // vertical gap between rows in a column
    private const double MarginXY = 40;

    public static void ApplyLayeredLayout(BpmnModel model)
    {
        var nodes = model.Nodes
            .Where(n => n.Type is not BpmnNodeType.Participant and not BpmnNodeType.Lane)
            .Where(n => n.Width > 0 && n.Height > 0)
            .ToList();

        if (nodes.Count == 0) return;

        var ids = nodes.Select(n => n.Id).ToHashSet();

        // Build adjacency
        var outEdges = nodes.ToDictionary(n => n.Id, _ => new List<string>());
        var inDegree = nodes.ToDictionary(n => n.Id, _ => 0);

        foreach (var flow in model.Flows)
        {
            if (!ids.Contains(flow.SourceRef) || !ids.Contains(flow.TargetRef)) continue;
            outEdges[flow.SourceRef].Add(flow.TargetRef);
            inDegree[flow.TargetRef]++;
        }

        // Longest-path ranking via iterative relaxation
        var rank = nodes.ToDictionary(n => n.Id, _ => 0);
        var queue = new Queue<string>(nodes.Where(n => inDegree[n.Id] == 0).Select(n => n.Id));
        int safety = nodes.Count * nodes.Count + 1;

        while (queue.Count > 0 && safety-- > 0)
        {
            var id = queue.Dequeue();
            foreach (var next in outEdges[id])
            {
                if (rank[next] < rank[id] + 1)
                {
                    rank[next] = rank[id] + 1;
                    queue.Enqueue(next);
                }
            }
        }

        // Group by rank, preserving original relative Y order within each column
        var byRank = nodes
            .GroupBy(n => rank[n.Id])
            .OrderBy(g => g.Key)
            .Select(g => g.OrderBy(n => n.Y).ToList())
            .ToList();

        // Assign positions
        double x = MarginXY;
        foreach (var col in byRank)
        {
            double colW = col.Max(n => n.Width);
            double y    = MarginXY;
            foreach (var n in col)
            {
                n.X = x + (colW - n.Width) / 2.0;
                n.Y = y;
                y  += n.Height + VGap;
            }
            x += colW + HGap;
        }

        // Rebuild waypoints as straight center-to-center lines
        var nodeMap = nodes.ToDictionary(n => n.Id);
        foreach (var flow in model.Flows)
        {
            if (!nodeMap.TryGetValue(flow.SourceRef, out var src) ||
                !nodeMap.TryGetValue(flow.TargetRef, out var tgt)) continue;

            flow.Waypoints =
            [
                (src.X + src.Width  / 2, src.Y + src.Height / 2),
                (tgt.X + tgt.Width  / 2, tgt.Y + tgt.Height / 2)
            ];
        }
    }
}
