using System.Xml.Linq;
using DiagramApp.Models;

namespace DiagramApp.Services;

public class BpmnParser
{
    private static readonly XNamespace Bpmn = "http://www.omg.org/spec/BPMN/20100524/MODEL";
    private static readonly XNamespace BpmnDi = "http://www.omg.org/spec/BPMN/20100524/DI";
    private static readonly XNamespace Dc = "http://www.omg.org/spec/DD/20100524/DC";
    private static readonly XNamespace Di = "http://www.omg.org/spec/DD/20100524/DI";

    public BpmnModel Parse(string filePath)
    {
        var doc = XDocument.Load(filePath);
        var root = doc.Root!;
        var model = new BpmnModel();

        // Participant names from collaboration
        var participantNames = new Dictionary<string, string>();
        foreach (var collab in root.Elements(Bpmn + "collaboration"))
        foreach (var part in collab.Elements(Bpmn + "participant"))
        {
            var pid = part.Attribute("id")?.Value;
            var pname = part.Attribute("name")?.Value ?? "";
            if (pid != null) participantNames[pid] = pname;
        }

        // Process elements
        var nodeMap = new Dictionary<string, BpmnNode>();
        foreach (var process in root.Elements(Bpmn + "process"))
        {
            if (string.IsNullOrEmpty(model.Name))
                model.Name = process.Attribute("name")?.Value
                          ?? System.IO.Path.GetFileNameWithoutExtension(filePath);

            ParseProcessElements(process, model, nodeMap);
        }

        if (string.IsNullOrEmpty(model.Name))
            model.Name = System.IO.Path.GetFileNameWithoutExtension(filePath);

        // BPMNDI layout
        var diagram = root.Element(BpmnDi + "BPMNDiagram");
        if (diagram != null)
            ParseDiagram(diagram, model, nodeMap, participantNames);

        // Fallback: auto-layout flows with no waypoints
        FillMissingWaypoints(model);

        return model;
    }

    private void ParseProcessElements(XElement process, BpmnModel model, Dictionary<string, BpmnNode> nodeMap)
    {
        foreach (var elem in process.Elements())
        {
            var type = GetNodeType(elem.Name.LocalName);
            if (type == BpmnNodeType.Unknown) continue;

            var node = new BpmnNode
            {
                Id = elem.Attribute("id")?.Value ?? "",
                Name = elem.Attribute("name")?.Value ?? "",
                Type = type,
                HasLoopMarker = elem.Element(Bpmn + "standardLoopCharacteristics") != null,
                HasParallelMarker = elem.Element(Bpmn + "multiInstanceLoopCharacteristics") != null
            };

            model.Nodes.Add(node);
            if (!string.IsNullOrEmpty(node.Id))
                nodeMap[node.Id] = node;

            // Recurse into subProcesses
            if (type == BpmnNodeType.SubProcess)
                ParseProcessElements(elem, model, nodeMap);
        }

        foreach (var flow in process.Elements(Bpmn + "sequenceFlow"))
        {
            model.Flows.Add(new BpmnFlow
            {
                Id = flow.Attribute("id")?.Value ?? "",
                Name = flow.Attribute("name")?.Value ?? "",
                SourceRef = flow.Attribute("sourceRef")?.Value ?? "",
                TargetRef = flow.Attribute("targetRef")?.Value ?? ""
            });
        }
    }

    private void ParseDiagram(XElement diagram, BpmnModel model,
        Dictionary<string, BpmnNode> nodeMap, Dictionary<string, string> participantNames)
    {
        var plane = diagram.Element(BpmnDi + "BPMNPlane");
        if (plane == null) return;

        var flowMap = model.Flows.ToDictionary(f => f.Id);

        foreach (var shape in plane.Elements(BpmnDi + "BPMNShape"))
        {
            var elemRef = shape.Attribute("bpmnElement")?.Value;
            if (elemRef == null) continue;

            var bounds = shape.Element(Dc + "Bounds");
            if (bounds == null) continue;

            double x = D(bounds.Attribute("x")?.Value);
            double y = D(bounds.Attribute("y")?.Value);
            double w = D(bounds.Attribute("width")?.Value);
            double h = D(bounds.Attribute("height")?.Value);

            if (nodeMap.TryGetValue(elemRef, out var node))
            {
                node.X = x; node.Y = y;
                node.Width = w; node.Height = h;
            }
            else
            {
                // Participant or Lane not in process elements
                var newNode = new BpmnNode
                {
                    Id = elemRef,
                    Name = participantNames.TryGetValue(elemRef, out var pname) ? pname : "",
                    Type = BpmnNodeType.Participant,
                    X = x, Y = y, Width = w, Height = h
                };
                model.Nodes.Add(newNode);
                nodeMap[elemRef] = newNode;
            }
        }

        foreach (var edge in plane.Elements(BpmnDi + "BPMNEdge"))
        {
            var elemRef = edge.Attribute("bpmnElement")?.Value;
            if (elemRef == null) continue;

            var waypoints = edge.Elements(Di + "waypoint")
                .Select(wp => (X: D(wp.Attribute("x")?.Value), Y: D(wp.Attribute("y")?.Value)))
                .ToList();

            if (flowMap.TryGetValue(elemRef, out var flow))
                flow.Waypoints = waypoints;
        }
    }

    private void FillMissingWaypoints(BpmnModel model)
    {
        var nodeMap = model.Nodes.ToDictionary(n => n.Id);
        foreach (var flow in model.Flows.Where(f => f.Waypoints.Count < 2))
        {
            if (!nodeMap.TryGetValue(flow.SourceRef, out var src)) continue;
            if (!nodeMap.TryGetValue(flow.TargetRef, out var tgt)) continue;
            if (src.Width <= 0 || tgt.Width <= 0) continue;

            flow.Waypoints =
            [
                (src.X + src.Width / 2, src.Y + src.Height / 2),
                (tgt.X + tgt.Width / 2, tgt.Y + tgt.Height / 2)
            ];
        }
    }

    private static BpmnNodeType GetNodeType(string localName) => localName switch
    {
        "startEvent" => BpmnNodeType.StartEvent,
        "endEvent" => BpmnNodeType.EndEvent,
        "intermediateCatchEvent" => BpmnNodeType.IntermediateCatchEvent,
        "intermediateThrowEvent" => BpmnNodeType.IntermediateThrowEvent,
        "boundaryEvent" => BpmnNodeType.BoundaryEvent,
        "task" => BpmnNodeType.Task,
        "serviceTask" => BpmnNodeType.ServiceTask,
        "userTask" => BpmnNodeType.UserTask,
        "sendTask" => BpmnNodeType.SendTask,
        "receiveTask" => BpmnNodeType.ReceiveTask,
        "scriptTask" => BpmnNodeType.ScriptTask,
        "businessRuleTask" => BpmnNodeType.BusinessRuleTask,
        "manualTask" => BpmnNodeType.ManualTask,
        "callActivity" => BpmnNodeType.CallActivity,
        "subProcess" => BpmnNodeType.SubProcess,
        "exclusiveGateway" => BpmnNodeType.ExclusiveGateway,
        "parallelGateway" => BpmnNodeType.ParallelGateway,
        "inclusiveGateway" => BpmnNodeType.InclusiveGateway,
        "eventBasedGateway" => BpmnNodeType.EventBasedGateway,
        "complexGateway" => BpmnNodeType.ComplexGateway,
        _ => BpmnNodeType.Unknown
    };

    private static double D(string? s) =>
        double.TryParse(s, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
}
