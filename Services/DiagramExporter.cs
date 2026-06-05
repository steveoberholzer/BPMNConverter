using Aspose.Diagram;
using DiagramApp.Models;

namespace DiagramApp.Services;

/// <summary>
/// Converts a parsed BpmnModel into an Aspose.Diagram document and exports to
/// VSDX or PDF.  BPMN is not natively supported by Aspose.Diagram, so we bridge
/// the gap by constructing shapes programmatically from the parsed model.
/// </summary>
public class DiagramExporter
{
    // BPMN layout coords are device-independent pixels (96 dpi).
    // Aspose.Diagram works in inches.
    private const double DPI = 96.0;

    public void ExportToVsdx(BpmnModel model, string outputPath) =>
        BuildDiagram(model).Save(outputPath, SaveFileFormat.Vsdx);

    public void ExportToPdf(BpmnModel model, string outputPath) =>
        BuildDiagram(model).Save(outputPath, SaveFileFormat.Pdf);

    // ── Build Aspose.Diagram ──────────────────────────────────────────────────

    private static Diagram BuildDiagram(BpmnModel model)
    {
        var diagram = new Diagram();
        var page    = diagram.Pages[0];
        page.Name   = Truncate(model.Name, 31);   // Visio page name limit

        var (minX, minY, maxX, maxY) = model.GetBounds();
        double ox     = (minX < 0 ? -minX : 0) + 40;  // pixel offset
        double oy     = (minY < 0 ? -minY : 0) + 40;
        double pageW  = (maxX - minX + ox * 2) / DPI;
        double pageH  = (maxY - minY + oy * 2) / DPI;

        page.PageSheet.PageProps.PageWidth.Value  = pageW;
        page.PageSheet.PageProps.PageHeight.Value = pageH;

        int nextId = 1;

        // ── Shapes ────────────────────────────────────────────────────────────
        foreach (var node in model.Nodes.Where(n => n.Width > 0 && n.Height > 0))
        {
            var shape = new Shape { ID = nextId++, Name = node.Id };

            // Centre of shape in page inches (Aspose: origin bottom-left, Y up)
            double w    = node.Width  / DPI;
            double h    = node.Height / DPI;
            double pinX = (ox + node.X + node.Width  / 2) / DPI;
            double pinY = pageH - (oy + node.Y + node.Height / 2) / DPI;

            shape.XForm.PinX.Value   = pinX;
            shape.XForm.PinY.Value   = pinY;
            shape.XForm.Width.Value  = w;
            shape.XForm.Height.Value = h;

            var (fillRgb, lineRgb, lineW) = GetStyle(node.Type);
            shape.Fill.FillForegnd.Value = fillRgb;
            shape.Fill.FillBkgnd.Value   = fillRgb;
            shape.Fill.FillPattern.Value = 1;          // solid fill
            shape.Line.LineColor.Value   = lineRgb;
            shape.Line.LineWeight.Value  = lineW;
            shape.Line.LinePattern.Value = 1;          // solid line

            if (node.Type == BpmnNodeType.CallActivity)
                shape.Line.LineWeight.Value = 0.03;

            // Custom geometry for circles and diamonds
            if (IsEvent(node.Type))
                ApplyEllipse(shape, w, h);
            else if (IsGateway(node.Type))
                ApplyDiamond(shape, w, h);

            SetShapeText(shape, node.Name);
            page.Shapes.Add(shape);
        }

        // ── Connectors ────────────────────────────────────────────────────────
        foreach (var flow in model.Flows.Where(f => f.Waypoints.Count >= 2))
        {
            double minWx = flow.Waypoints.Min(p => p.X);
            double maxWx = flow.Waypoints.Max(p => p.X);
            double minWy = flow.Waypoints.Min(p => p.Y);
            double maxWy = flow.Waypoints.Max(p => p.Y);

            double cW    = Math.Max((maxWx - minWx) / DPI, 0.002);
            double cH    = Math.Max((maxWy - minWy) / DPI, 0.002);
            double cPinX = (ox + (minWx + maxWx) / 2) / DPI;
            double cPinY = pageH - (oy + (minWy + maxWy) / 2) / DPI;

            var conn = new Shape { ID = nextId++, Name = flow.Id };
            conn.XForm.PinX.Value    = cPinX;
            conn.XForm.PinY.Value    = cPinY;
            conn.XForm.Width.Value   = cW;
            conn.XForm.Height.Value  = cH;

            // No fill for connector
            conn.Fill.FillPattern.Value = 0;

            conn.Line.LineWeight.Value   = 0.01;
            conn.Line.LineColor.Value    = "RGB(60,60,60)";
            conn.Line.LinePattern.Value  = 1;
            conn.Line.EndArrow.Value     = 1;   // open arrowhead

            ApplyPolyline(conn, flow.Waypoints, minWx, maxWy, cW, cH);

            SetShapeText(conn, flow.Name);
            page.Shapes.Add(conn);
        }

        return diagram;
    }

    // ── Geometry ──────────────────────────────────────────────────────────────

    private static void ApplyEllipse(Shape shape, double w, double h)
    {
        // Visio Ellipse row: X,Y = centre; A,B = point on axis; C,D = point on axis
        var ellRow = new Ellipse();
        ellRow.X.Value = w / 2;  // centre X (shape-local inches)
        ellRow.Y.Value = h / 2;  // centre Y
        ellRow.A.Value = w;      // right-edge X
        ellRow.B.Value = h / 2;  // right-edge Y
        ellRow.C.Value = w / 2;  // top-edge X
        ellRow.D.Value = h;      // top-edge Y

        var geom = new Geom();
        geom.CoordinateCol.Add(ellRow);
        shape.Geoms.Add(geom);
    }

    private static void ApplyDiamond(Shape shape, double w, double h)
    {
        // Diamond = rotated square: bottom → right → top → left → back
        var moveTo = new MoveTo();
        moveTo.X.Value = w / 2; moveTo.Y.Value = 0;       // bottom

        var l1 = new LineTo(); l1.X.Value = w;     l1.Y.Value = h / 2;  // right
        var l2 = new LineTo(); l2.X.Value = w / 2; l2.Y.Value = h;      // top
        var l3 = new LineTo(); l3.X.Value = 0;     l3.Y.Value = h / 2;  // left
        var l4 = new LineTo(); l4.X.Value = w / 2; l4.Y.Value = 0;      // close

        var geom = new Geom();
        geom.CoordinateCol.Add(moveTo);
        geom.CoordinateCol.Add(l1);
        geom.CoordinateCol.Add(l2);
        geom.CoordinateCol.Add(l3);
        geom.CoordinateCol.Add(l4);
        shape.Geoms.Add(geom);
    }

    private static void ApplyPolyline(Shape conn,
        List<(double X, double Y)> waypoints,
        double minWx, double maxWy, double cW, double cH)
    {
        var geom = new Geom();
        geom.NoFill.Value = BOOL.True;

        for (int i = 0; i < waypoints.Count; i++)
        {
            // Shape-local inches: X from left, Y from bottom (flip BPMN Y)
            double lx = Math.Clamp((waypoints[i].X - minWx) / DPI, 0, cW);
            double ly = Math.Clamp((maxWy - waypoints[i].Y) / DPI, 0, cH);

            if (i == 0)
            {
                var m = new MoveTo();
                m.X.Value = lx; m.Y.Value = ly;
                geom.CoordinateCol.Add(m);
            }
            else
            {
                var l = new LineTo();
                l.X.Value = lx; l.Y.Value = ly;
                geom.CoordinateCol.Add(l);
            }
        }

        conn.Geoms.Add(geom);
    }

    // ── Text ──────────────────────────────────────────────────────────────────

    private static void SetShapeText(Shape shape, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try { shape.Text.Value.SetWholeText(text); }
        catch { /* non-critical */ }
    }

    // ── Style ─────────────────────────────────────────────────────────────────

    private static (string fill, string line, double lineW) GetStyle(BpmnNodeType t) => t switch
    {
        BpmnNodeType.StartEvent             => ("RGB(200,230,201)", "RGB(46,125,50)",   0.02),
        BpmnNodeType.EndEvent               => ("RGB(255,205,210)", "RGB(183,28,28)",   0.03),
        BpmnNodeType.IntermediateCatchEvent => ("RGB(227,242,253)", "RGB(21,101,192)",  0.015),
        BpmnNodeType.IntermediateThrowEvent => ("RGB(200,220,255)", "RGB(21,101,192)",  0.015),
        BpmnNodeType.ServiceTask            => ("RGB(227,242,253)", "RGB(21,101,192)",  0.01),
        BpmnNodeType.UserTask               => ("RGB(232,245,233)", "RGB(46,125,50)",   0.01),
        BpmnNodeType.SendTask               => ("RGB(255,243,224)", "RGB(230,81,0)",    0.01),
        BpmnNodeType.ReceiveTask            => ("RGB(225,245,254)", "RGB(1,87,155)",    0.01),
        BpmnNodeType.CallActivity           => ("RGB(237,231,246)", "RGB(74,20,140)",   0.01),
        BpmnNodeType.ScriptTask             => ("RGB(255,253,231)", "RGB(130,119,23)",  0.01),
        BpmnNodeType.ManualTask             => ("RGB(240,244,255)", "RGB(63,81,181)",   0.01),
        BpmnNodeType.ExclusiveGateway       => ("RGB(255,249,196)", "RGB(245,127,23)",  0.015),
        BpmnNodeType.ParallelGateway        => ("RGB(232,255,232)", "RGB(46,125,50)",   0.015),
        BpmnNodeType.InclusiveGateway       => ("RGB(255,236,210)", "RGB(230,81,0)",    0.015),
        BpmnNodeType.EventBasedGateway      => ("RGB(255,249,196)", "RGB(245,127,23)",  0.015),
        BpmnNodeType.Participant            => ("RGB(243,244,252)", "RGB(120,144,164)", 0.015),
        BpmnNodeType.Lane                   => ("RGB(248,249,255)", "RGB(144,164,174)", 0.01),
        _                                   => ("RGB(240,240,240)", "RGB(90,90,90)",    0.01)
    };

    private static bool IsEvent(BpmnNodeType t) =>
        t is BpmnNodeType.StartEvent or BpmnNodeType.EndEvent
          or BpmnNodeType.IntermediateCatchEvent or BpmnNodeType.IntermediateThrowEvent
          or BpmnNodeType.BoundaryEvent;

    private static bool IsGateway(BpmnNodeType t) =>
        t is BpmnNodeType.ExclusiveGateway or BpmnNodeType.ParallelGateway
          or BpmnNodeType.InclusiveGateway or BpmnNodeType.EventBasedGateway
          or BpmnNodeType.ComplexGateway;

    private static string Truncate(string s, int max) =>
        s.Length > max ? s[..max] : s;
}
