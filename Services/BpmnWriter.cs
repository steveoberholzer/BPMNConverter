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

    private static string Fmt(double v) =>
        v.ToString("F1", CultureInfo.InvariantCulture);
}
