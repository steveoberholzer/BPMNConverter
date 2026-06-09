namespace DiagramApp.Models;

public enum BpmnNodeType
{
    Unknown,
    StartEvent,
    EndEvent,
    IntermediateCatchEvent,
    IntermediateThrowEvent,
    BoundaryEvent,
    Task,
    ServiceTask,
    UserTask,
    SendTask,
    ReceiveTask,
    ScriptTask,
    BusinessRuleTask,
    ManualTask,
    CallActivity,
    SubProcess,
    ExclusiveGateway,
    ParallelGateway,
    InclusiveGateway,
    EventBasedGateway,
    ComplexGateway,
    Participant,
    Lane
}

public class BpmnNode
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public BpmnNodeType Type { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool HasLoopMarker { get; set; }
    public bool HasParallelMarker { get; set; }
}

public class BpmnFlow
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string SourceRef { get; set; } = "";
    public string TargetRef { get; set; } = "";
    public List<(double X, double Y)> Waypoints { get; set; } = [];
}

public class BpmnModel
{
    public string Name { get; set; } = "";
    public List<BpmnNode> Nodes { get; set; } = [];
    public List<BpmnFlow> Flows { get; set; } = [];

    public BpmnNode? GetNode(string id) => Nodes.FirstOrDefault(n => n.Id == id);
    public BpmnFlow? GetFlow(string id) => Flows.FirstOrDefault(f => f.Id == id);

    public (double minX, double minY, double maxX, double maxY) GetBounds()
    {
        if (Nodes.Count == 0) return (0, 0, 800, 600);

        var relevant = Nodes.Where(n => n.Width > 0 && n.Height > 0).ToList();
        if (relevant.Count == 0) return (0, 0, 800, 600);

        double minX = relevant.Min(n => n.X);
        double minY = relevant.Min(n => n.Y);
        double maxX = relevant.Max(n => n.X + n.Width);
        double maxY = relevant.Max(n => n.Y + n.Height);

        // Include waypoints
        var wps = Flows.SelectMany(f => f.Waypoints).ToList();
        if (wps.Count > 0)
        {
            minX = Math.Min(minX, wps.Min(p => p.X));
            minY = Math.Min(minY, wps.Min(p => p.Y));
            maxX = Math.Max(maxX, wps.Max(p => p.X));
            maxY = Math.Max(maxY, wps.Max(p => p.Y));
        }

        return (minX, minY, maxX, maxY);
    }
}
