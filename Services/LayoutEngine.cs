using DiagramApp.Models;

namespace DiagramApp.Services;

public enum LayoutType
{
    LeftToRight,      // Layered, left → right
    TopToBottom,      // Layered, top  → bottom
    SnakeHorizontal,  // Topo-sorted, wraps in rows  (left→right, right→left, ...)
    SnakeVertical     // Topo-sorted, wraps in columns (top→bottom, bottom→top, ...)
}

public static class LayoutEngine
{
    private const double Margin = 40;
    private const double HGap   = 70;
    private const double VGap   = 40;

    public static void ApplyLayout(BpmnModel model, LayoutType type)
    {
        switch (type)
        {
            case LayoutType.LeftToRight:     ApplyLayered(model, leftToRight: true);  break;
            case LayoutType.TopToBottom:     ApplyLayered(model, leftToRight: false); break;
            case LayoutType.SnakeHorizontal: ApplySnake(model,   horizontal: true);   break;
            case LayoutType.SnakeVertical:   ApplySnake(model,   horizontal: false);  break;
        }
    }

    // ── Layered (Sugiyama-inspired: longest-path rank + barycenter crossing minimisation) ──

    private static void ApplyLayered(BpmnModel model, bool leftToRight)
    {
        var nodes = LayoutNodes(model);
        if (nodes.Count == 0) return;

        var ids      = nodes.Select(n => n.Id).ToHashSet();
        var outEdges = nodes.ToDictionary(n => n.Id, _ => new List<string>());
        var inEdges  = nodes.ToDictionary(n => n.Id, _ => new List<string>());
        var inDeg    = nodes.ToDictionary(n => n.Id, _ => 0);

        foreach (var flow in model.Flows)
        {
            if (!ids.Contains(flow.SourceRef) || !ids.Contains(flow.TargetRef)) continue;
            outEdges[flow.SourceRef].Add(flow.TargetRef);
            inEdges[flow.TargetRef].Add(flow.SourceRef);
            inDeg[flow.TargetRef]++;
        }

        // Longest-path ranking — ensures sources are left/top of their targets
        var rank   = nodes.ToDictionary(n => n.Id, _ => 0);
        var queue  = new Queue<string>(nodes.Where(n => inDeg[n.Id] == 0).Select(n => n.Id));
        int safety = nodes.Count * nodes.Count + 1;
        while (queue.Count > 0 && safety-- > 0)
        {
            var id = queue.Dequeue();
            foreach (var nxt in outEdges[id])
            {
                if (rank[nxt] < rank[id] + 1)
                {
                    rank[nxt] = rank[id] + 1;
                    queue.Enqueue(nxt);
                }
            }
        }

        // Build layers — GroupBy is gap-proof even when the safety counter
        // exhausted early and some ranks were skipped.
        var layers = nodes
            .GroupBy(n => rank[n.Id])
            .OrderBy(g => g.Key)
            .Select(g => g.ToList())
            .ToList();

        // Barycenter crossing minimisation (8 forward+backward sweeps)
        ReduceCrossings(layers, inEdges, outEdges, iterations: 8);

        // Assign X/Y positions
        double primary = Margin;
        foreach (var layer in layers)
        {
            double thick     = layer.Max(n => leftToRight ? n.Width : n.Height);
            double secondary = Margin;

            foreach (var n in layer)
            {
                if (leftToRight)
                {
                    n.X = primary + (thick - n.Width)  / 2.0;
                    n.Y = secondary;
                    secondary += n.Height + VGap;
                }
                else
                {
                    n.X = secondary;
                    n.Y = primary + (thick - n.Height) / 2.0;
                    secondary += n.Width + VGap;
                }
            }
            primary += thick + (leftToRight ? HGap : VGap);
        }

        RebuildWaypoints(model, nodes.ToDictionary(n => n.Id));
    }

    /// <summary>
    /// Iteratively sorts each layer by the average position of its
    /// neighbours in the adjacent layer (barycenter heuristic).
    /// Forward pass reduces crossings caused by downward edges;
    /// backward pass cleans up the rest.
    /// </summary>
    private static void ReduceCrossings(
        List<List<BpmnNode>> layers,
        Dictionary<string, List<string>> inEdges,
        Dictionary<string, List<string>> outEdges,
        int iterations)
    {
        if (layers.Count <= 1) return;

        // pos[id] = current fractional position within its layer (0 = top of layer)
        var pos = new Dictionary<string, double>();
        foreach (var layer in layers)
            for (int i = 0; i < layer.Count; i++)
                pos[layer[i].Id] = i;

        for (int iter = 0; iter < iterations; iter++)
        {
            // Forward: sort layer[i] by average pos of predecessors in layer[i-1]
            for (int li = 1; li < layers.Count; li++)
            {
                layers[li].Sort((a, b) =>
                    Barycenter(a.Id, inEdges, pos)
                    .CompareTo(Barycenter(b.Id, inEdges, pos)));
                for (int i = 0; i < layers[li].Count; i++)
                    pos[layers[li][i].Id] = i;
            }

            // Backward: sort layer[i] by average pos of successors in layer[i+1]
            for (int li = layers.Count - 2; li >= 0; li--)
            {
                layers[li].Sort((a, b) =>
                    Barycenter(a.Id, outEdges, pos)
                    .CompareTo(Barycenter(b.Id, outEdges, pos)));
                for (int i = 0; i < layers[li].Count; i++)
                    pos[layers[li][i].Id] = i;
            }
        }
    }

    private static double Barycenter(string id,
        Dictionary<string, List<string>> adj,
        Dictionary<string, double> pos)
    {
        if (!adj.TryGetValue(id, out var nbrs)) return pos.GetValueOrDefault(id, 0);
        var known = nbrs.Where(pos.ContainsKey).ToList();
        return known.Count == 0 ? pos.GetValueOrDefault(id, 0) : known.Average(n => pos[n]);
    }

    // ── Snake / wrap layout ───────────────────────────────────────────────────

    /// <summary>
    /// Flattens the diagram into topological order, then wraps it into a
    /// grid. Odd rows (or columns for vertical) reverse direction to create
    /// the "snake" reading path — good for long sequential flows.
    /// </summary>
    private static void ApplySnake(BpmnModel model, bool horizontal)
    {
        var nodes  = LayoutNodes(model);
        if (nodes.Count == 0) return;

        var sorted = TopologicalSort(model, nodes);

        // Target aspect ratio: 16:9 for horizontal, 9:16 for vertical
        double ratio   = horizontal ? 1.78 : 0.56;
        int    count   = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(sorted.Count * ratio)));

        // Uniform cell size based on the 90th-percentile node dimensions
        // so a single huge node doesn't dominate the grid
        double cellW = Percentile(sorted.Select(n => n.Width),  0.90);
        double cellH = Percentile(sorted.Select(n => n.Height), 0.90);

        double stepX = cellW + HGap;
        double stepY = cellH + VGap;

        for (int i = 0; i < sorted.Count; i++)
        {
            int row = i / count;
            int col = i % count;

            // Reverse on odd rows/columns for the snake effect
            if (row % 2 == 1) col = count - 1 - col;

            var n = sorted[i];
            if (horizontal)
            {
                n.X = Margin + col * stepX + (cellW - n.Width)  / 2;
                n.Y = Margin + row * stepY + (cellH - n.Height) / 2;
            }
            else
            {
                // Vertical snake: primary axis is Y
                n.X = Margin + row * stepX + (cellW - n.Width)  / 2;
                n.Y = Margin + col * stepY + (cellH - n.Height) / 2;
            }
        }

        RebuildWaypoints(model, sorted.ToDictionary(n => n.Id));
    }

    // ── Graph helpers ─────────────────────────────────────────────────────────

    private static List<BpmnNode> TopologicalSort(BpmnModel model, List<BpmnNode> nodes)
    {
        var ids     = nodes.Select(n => n.Id).ToHashSet();
        var outEdgs = nodes.ToDictionary(n => n.Id, _ => new List<string>());
        var inDeg   = nodes.ToDictionary(n => n.Id, _ => 0);
        var nodeMap = nodes.ToDictionary(n => n.Id);

        foreach (var flow in model.Flows)
        {
            if (!ids.Contains(flow.SourceRef) || !ids.Contains(flow.TargetRef)) continue;
            outEdgs[flow.SourceRef].Add(flow.TargetRef);
            inDeg[flow.TargetRef]++;
        }

        var sorted  = new List<BpmnNode>();
        var visited = new HashSet<string>();
        // Priority queue approximation: process by inDegree order so nodes with
        // fewer incoming edges come first, giving a more natural reading sequence.
        var queue   = new Queue<BpmnNode>(
            nodes.Where(n => inDeg[n.Id] == 0).OrderBy(n => n.X).ThenBy(n => n.Y));

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (!visited.Add(node.Id)) continue;
            sorted.Add(node);
            foreach (var nextId in outEdgs[node.Id].Where(ids.Contains))
            {
                inDeg[nextId]--;
                if (inDeg[nextId] <= 0 && !visited.Contains(nextId))
                    queue.Enqueue(nodeMap[nextId]);
            }
        }

        // Append any nodes left over from cycles
        foreach (var n in nodes.Where(n => !visited.Contains(n.Id)))
            sorted.Add(n);

        return sorted;
    }

    private static List<BpmnNode> LayoutNodes(BpmnModel model) =>
        model.Nodes
             .Where(n => n.Type is not BpmnNodeType.Participant and not BpmnNodeType.Lane)
             .Where(n => n.Width > 0 && n.Height > 0)
             .ToList();

    private static void RebuildWaypoints(BpmnModel model, Dictionary<string, BpmnNode> map)
    {
        foreach (var flow in model.Flows)
        {
            if (!map.TryGetValue(flow.SourceRef, out var src) ||
                !map.TryGetValue(flow.TargetRef, out var tgt)) continue;
            flow.Waypoints =
            [
                (src.X + src.Width  / 2, src.Y + src.Height / 2),
                (tgt.X + tgt.Width  / 2, tgt.Y + tgt.Height / 2)
            ];
        }
    }

    private static double Percentile(IEnumerable<double> values, double p)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        int idx = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }
}
