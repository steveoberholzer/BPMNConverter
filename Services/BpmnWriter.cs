using System.Globalization;
using System.Xml.Linq;
using DiagramApp.Models;

namespace DiagramApp.Services;

/// <summary>
/// Writes an updated BPMN file by loading the original XML and patching only the
/// BPMNDI position data (shape bounds and edge waypoints).  All semantic content,
/// extensions, and custom attributes are preserved unchanged.
/// </summary>
public class BpmnWriter
{
    // Same namespace URIs as BpmnParser
    private static readonly XNamespace Bpmn   = "http://www.omg.org/spec/BPMN/20100524/MODEL";
    private static readonly XNamespace BpmnDi = "http://www.omg.org/spec/BPMN/20100524/DI";
    private static readonly XNamespace Dc     = "http://www.omg.org/spec/DD/20100524/DC";
    private static readonly XNamespace Di     = "http://www.omg.org/spec/DD/20100524/DI";

    public void Save(BpmnModel model, string sourcePath, string destPath)
    {
        var doc   = XDocument.Load(sourcePath);
        var plane = doc.Descendants(BpmnDi + "BPMNPlane").FirstOrDefault()
                    ?? throw new InvalidOperationException("No BPMNPlane found in source file.");

        var nodeMap = model.Nodes.ToDictionary(n => n.Id);
        var flowMap = model.Flows.ToDictionary(f => f.Id);

        // ── Update shape bounds (x / y only — layout never resizes nodes) ─────
        foreach (var shape in plane.Elements(BpmnDi + "BPMNShape"))
        {
            var elemId = shape.Attribute("bpmnElement")?.Value;
            if (elemId == null || !nodeMap.TryGetValue(elemId, out var node)) continue;

            var bounds = shape.Element(Dc + "Bounds");
            if (bounds == null) continue;

            bounds.SetAttributeValue("x", Fmt(node.X));
            bounds.SetAttributeValue("y", Fmt(node.Y));
        }

        // ── Update edge waypoints ─────────────────────────────────────────────
        foreach (var edge in plane.Elements(BpmnDi + "BPMNEdge"))
        {
            var elemId = edge.Attribute("bpmnElement")?.Value;
            if (elemId == null || !flowMap.TryGetValue(elemId, out var flow)) continue;
            if (flow.Waypoints.Count < 2) continue;

            // Remove old waypoints, leave other children (e.g. BPMNLabel) intact
            edge.Elements(Di + "waypoint").Remove();

            // Insert new waypoints before the first non-waypoint child (preserves label elements)
            var anchor = edge.Elements().FirstOrDefault();
            foreach (var wp in flow.Waypoints)
            {
                var wpElem = new XElement(Di + "waypoint",
                    new XAttribute("x", Fmt(wp.X)),
                    new XAttribute("y", Fmt(wp.Y)));

                if (anchor != null) anchor.AddBeforeSelf(wpElem);
                else                edge.Add(wpElem);
            }
        }

        doc.Save(destPath);
    }

    /// <summary>
    /// Generates a complete BPMN 2.0 XML file from scratch — used for new documents
    /// that have no original source file to patch.
    /// </summary>
    public void Create(BpmnModel model, string destPath)
    {
        var processId = "Process_1";

        // ── Build process element ─────────────────────────────────────────────
        var process = new XElement(Bpmn + "process",
            new XAttribute("id", processId),
            new XAttribute("name", model.Name),
            new XAttribute("isExecutable", "false"));

        foreach (var node in model.Nodes.Where(n =>
            n.Type is not BpmnNodeType.Participant and not BpmnNodeType.Lane))
        {
            var el = new XElement(Bpmn + ElementTag(node.Type),
                new XAttribute("id", node.Id));
            if (!string.IsNullOrWhiteSpace(node.Name))
                el.SetAttributeValue("name", node.Name);
            process.Add(el);
        }

        foreach (var flow in model.Flows)
        {
            var el = new XElement(Bpmn + "sequenceFlow",
                new XAttribute("id",        flow.Id),
                new XAttribute("sourceRef", flow.SourceRef),
                new XAttribute("targetRef", flow.TargetRef));
            if (!string.IsNullOrWhiteSpace(flow.Name))
                el.SetAttributeValue("name", flow.Name);
            process.Add(el);
        }

        // ── Build DI section ─────────────────────────────────────────────────
        var plane = new XElement(BpmnDi + "BPMNPlane",
            new XAttribute("id", "BPMNPlane_1"),
            new XAttribute("bpmnElement", processId));

        foreach (var node in model.Nodes.Where(n => n.Width > 0 && n.Height > 0))
        {
            var shape = new XElement(BpmnDi + "BPMNShape",
                new XAttribute("id",          $"BPMNShape_{node.Id}"),
                new XAttribute("bpmnElement", node.Id));
            shape.Add(new XElement(Dc + "Bounds",
                new XAttribute("x",      Fmt(node.X)),
                new XAttribute("y",      Fmt(node.Y)),
                new XAttribute("width",  Fmt(node.Width)),
                new XAttribute("height", Fmt(node.Height))));
            plane.Add(shape);
        }

        foreach (var flow in model.Flows)
        {
            var edge = new XElement(BpmnDi + "BPMNEdge",
                new XAttribute("id",          $"BPMNEdge_{flow.Id}"),
                new XAttribute("bpmnElement", flow.Id));
            foreach (var wp in flow.Waypoints)
                edge.Add(new XElement(Di + "waypoint",
                    new XAttribute("x", Fmt(wp.X)),
                    new XAttribute("y", Fmt(wp.Y))));
            plane.Add(edge);
        }

        var diagramEl = new XElement(BpmnDi + "BPMNDiagram",
            new XAttribute("id", "BPMNDiagram_1"),
            plane);

        var root = new XElement(Bpmn + "definitions",
            new XAttribute(XNamespace.Xmlns + "bpmndi", BpmnDi.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "dc",     Dc.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "di",     Di.NamespaceName),
            new XAttribute("id",              "Definitions_1"),
            new XAttribute("targetNamespace", "http://bpmn.io/schema/bpmn"),
            process,
            diagramEl);

        new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root)
            .Save(destPath);
    }

    private static string ElementTag(BpmnNodeType type) => type switch
    {
        BpmnNodeType.StartEvent             => "startEvent",
        BpmnNodeType.EndEvent               => "endEvent",
        BpmnNodeType.IntermediateCatchEvent => "intermediateCatchEvent",
        BpmnNodeType.IntermediateThrowEvent => "intermediateThrowEvent",
        BpmnNodeType.BoundaryEvent          => "boundaryEvent",
        BpmnNodeType.Task                   => "task",
        BpmnNodeType.ServiceTask            => "serviceTask",
        BpmnNodeType.UserTask               => "userTask",
        BpmnNodeType.SendTask               => "sendTask",
        BpmnNodeType.ReceiveTask            => "receiveTask",
        BpmnNodeType.ScriptTask             => "scriptTask",
        BpmnNodeType.BusinessRuleTask       => "businessRuleTask",
        BpmnNodeType.ManualTask             => "manualTask",
        BpmnNodeType.CallActivity           => "callActivity",
        BpmnNodeType.SubProcess             => "subProcess",
        BpmnNodeType.ExclusiveGateway       => "exclusiveGateway",
        BpmnNodeType.ParallelGateway        => "parallelGateway",
        BpmnNodeType.InclusiveGateway       => "inclusiveGateway",
        BpmnNodeType.EventBasedGateway      => "eventBasedGateway",
        BpmnNodeType.ComplexGateway         => "complexGateway",
        _                                   => "task"
    };

    private static string Fmt(double v) =>
        v.ToString("F1", CultureInfo.InvariantCulture);
}
