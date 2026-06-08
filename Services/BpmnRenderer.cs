using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DiagramApp.Models;

namespace DiagramApp.Services;

public class BpmnRenderer
{
    private const double Margin = 30;

    // Exposed so MainWindow can map canvas coords ↔ model coords
    public double OffsetX { get; private set; }
    public double OffsetY { get; private set; }

    private readonly List<UIElement> _flowElements = new();

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>Full render. Returns canvas dimensions and per-node container map.</summary>
    public (double Width, double Height, Dictionary<string, Canvas> NodeContainers)
        Render(BpmnModel model, Canvas canvas)
    {
        canvas.Children.Clear();
        _flowElements.Clear();
        var containers = new Dictionary<string, Canvas>();

        if (model.Nodes.Count == 0)
            return (400, 300, containers);

        var (minX, minY, maxX, maxY) = model.GetBounds();
        OffsetX = (minX < 0 ? -minX : 0) + Margin;
        OffsetY = (minY < 0 ? -minY : 0) + Margin;

        double w = maxX - minX + Margin * 2;
        double h = maxY - minY + Margin * 2;
        canvas.Width  = w;
        canvas.Height = h;
        canvas.Background = new SolidColorBrush(Color.FromRgb(248, 249, 250));

        DrawParticipants(model, canvas);
        DrawFlows(model, canvas);
        DrawNodes(model, canvas, containers);

        return (w, h, containers);
    }

    /// <summary>
    /// Removes and redraws only the flow layer using the current container positions.
    /// Call this after any node has been moved.
    /// </summary>
    public void RedrawFlows(BpmnModel model, Canvas canvas,
        IReadOnlyDictionary<string, Canvas> containers)
    {
        foreach (var e in _flowElements)
            canvas.Children.Remove(e);
        _flowElements.Clear();

        DrawFlowsFromContainers(model, canvas, containers);
    }

    // ── Participants (pools / lanes) ──────────────────────────────────────────

    private void DrawParticipants(BpmnModel model, Canvas canvas)
    {
        foreach (var node in model.Nodes.Where(n => n.Type == BpmnNodeType.Participant && n.Width > 0))
        {
            double x = OffsetX + node.X, y = OffsetY + node.Y;

            PlaceOnCanvas(canvas, new Rectangle
            {
                Width = node.Width, Height = node.Height,
                Fill   = new SolidColorBrush(Color.FromRgb(245, 247, 252)),
                Stroke = new SolidColorBrush(Color.FromRgb(144, 164, 174)),
                StrokeThickness = 1.5
            }, x, y, zIndex: 0);

            if (!string.IsNullOrWhiteSpace(node.Name))
            {
                PlaceOnCanvas(canvas, new Rectangle
                {
                    Width = 30, Height = node.Height,
                    Fill   = new SolidColorBrush(Color.FromRgb(224, 231, 247)),
                    Stroke = new SolidColorBrush(Color.FromRgb(144, 164, 174)),
                    StrokeThickness = 1
                }, x, y, zIndex: 0);

                var tb = new TextBlock
                {
                    Text = node.Name, FontSize = 11, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(55, 71, 79)),
                    Width = node.Height - 8,
                    TextAlignment   = TextAlignment.Center,
                    TextTrimming    = TextTrimming.CharacterEllipsis,
                    RenderTransform = new RotateTransform(-90),
                    RenderTransformOrigin = new Point(0, 0)
                };
                Canvas.SetLeft(tb, x + 15);
                Canvas.SetTop(tb,  y + node.Height - 4);
                canvas.Children.Add(tb);
            }
        }
    }

    // ── Flows (initial render from stored waypoints) ──────────────────────────

    private void DrawFlows(BpmnModel model, Canvas canvas)
    {
        var brush = FlowBrush();

        foreach (var flow in model.Flows)
        {
            if (flow.Waypoints.Count < 2) continue;

            var pts = flow.Waypoints
                .Select(p => new Point(OffsetX + p.X, OffsetY + p.Y))
                .ToArray();

            RenderFlowLines(canvas, pts, brush);

            if (!string.IsNullOrWhiteSpace(flow.Name))
                RenderFlowLabel(canvas, pts[pts.Length / 2], flow.Name);
        }
    }

    // ── Flows (redrawn after node moves, using live container positions) ───────

    private void DrawFlowsFromContainers(BpmnModel model, Canvas canvas,
        IReadOnlyDictionary<string, Canvas> containers)
    {
        var brush = FlowBrush();

        foreach (var flow in model.Flows)
        {
            Point from, to;

            if (containers.TryGetValue(flow.SourceRef, out var srcC) &&
                containers.TryGetValue(flow.TargetRef, out var tgtC))
            {
                var srcCenter = ContainerCenter(srcC);
                var tgtCenter = ContainerCenter(tgtC);
                from = ClipToEdge(srcCenter, tgtCenter, srcC);
                to   = ClipToEdge(tgtCenter, srcCenter, tgtC);
            }
            else if (flow.Waypoints.Count >= 2)
            {
                from = new Point(OffsetX + flow.Waypoints[0].X,  OffsetY + flow.Waypoints[0].Y);
                to   = new Point(OffsetX + flow.Waypoints[^1].X, OffsetY + flow.Waypoints[^1].Y);
            }
            else continue;

            RenderFlowLines(canvas, [from, to], brush);

            if (!string.IsNullOrWhiteSpace(flow.Name))
                RenderFlowLabel(canvas, new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2), flow.Name);
        }
    }

    private void RenderFlowLines(Canvas canvas, Point[] pts, SolidColorBrush brush)
    {
        for (int i = 0; i < pts.Length - 1; i++)
        {
            AddFlowElement(canvas, new Line
            {
                X1 = pts[i].X, Y1 = pts[i].Y,
                X2 = pts[i + 1].X, Y2 = pts[i + 1].Y,
                Stroke = brush, StrokeThickness = 1.5
            });
        }
        DrawArrowhead(canvas, pts[^2], pts[^1], brush);
    }

    private void RenderFlowLabel(Canvas canvas, Point mid, string text)
    {
        var lbl = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)),
            Padding = new Thickness(2, 0, 2, 0),
            Child = new TextBlock
            {
                Text = text, FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(60, 60, 60))
            }
        };
        Canvas.SetLeft(lbl, mid.X - 20);
        Canvas.SetTop(lbl,  mid.Y - 9);
        AddFlowElement(canvas, lbl);
    }

    private void DrawArrowhead(Canvas canvas, Point from, Point to, Brush fill)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.001) return;
        double ux = dx / len, uy = dy / len;
        const double aLen = 9, aHalf = 4.5;
        double bx = to.X - aLen * ux, by = to.Y - aLen * uy;
        AddFlowElement(canvas, new Polygon
        {
            Points = new PointCollection
            {
                to,
                new(bx + aHalf * uy, by - aHalf * ux),
                new(bx - aHalf * uy, by + aHalf * ux)
            },
            Fill = fill, Stroke = fill, StrokeLineJoin = PenLineJoin.Round
        });
    }

    private void AddFlowElement(Canvas canvas, UIElement element)
    {
        _flowElements.Add(element);
        Canvas.SetZIndex(element, 1);
        canvas.Children.Add(element);
    }

    // ── Nodes ─────────────────────────────────────────────────────────────────

    private void DrawNodes(BpmnModel model, Canvas canvas,
        Dictionary<string, Canvas> containers)
    {
        foreach (var node in model.Nodes.Where(n =>
            n.Width > 0 && n.Height > 0 &&
            n.Type is not BpmnNodeType.Participant and not BpmnNodeType.Lane))
        {
            double x = OffsetX + node.X, y = OffsetY + node.Y;
            double w = node.Width,        h = node.Height;

            // Each node lives in its own Canvas so it can be dragged as a unit.
            // ClipToBounds=false lets event labels bleed below the node boundary.
            var container = new Canvas
            {
                Width  = w,
                Height = h,
                Background      = Brushes.Transparent,
                IsHitTestVisible = true,
                Tag              = node.Id,
                ClipToBounds     = false
            };
            Canvas.SetLeft(container, x);
            Canvas.SetTop(container,  y);
            Canvas.SetZIndex(container, 2);

            switch (node.Type)
            {
                case BpmnNodeType.StartEvent:
                case BpmnNodeType.EndEvent:
                case BpmnNodeType.IntermediateCatchEvent:
                case BpmnNodeType.IntermediateThrowEvent:
                case BpmnNodeType.BoundaryEvent:
                    DrawEventInContainer(container, node, w, h);
                    break;

                case BpmnNodeType.ExclusiveGateway:
                case BpmnNodeType.ParallelGateway:
                case BpmnNodeType.InclusiveGateway:
                case BpmnNodeType.EventBasedGateway:
                case BpmnNodeType.ComplexGateway:
                    DrawGatewayInContainer(container, node, w, h);
                    break;

                default:
                    DrawTaskInContainer(container, node, w, h);
                    break;
            }

            // Selection highlight — shown/hidden by the editor
            AddSelectionRect(container, w, h);

            canvas.Children.Add(container);
            containers[node.Id] = container;
        }
    }

    // ── Node draw helpers ─────────────────────────────────────────────────────

    private static void AddSelectionRect(Canvas c, double w, double h)
    {
        var r = new Rectangle
        {
            Width  = w + 6,
            Height = h + 6,
            Stroke = new SolidColorBrush(Color.FromRgb(25, 118, 210)),
            StrokeThickness = 2.5,
            Fill = new SolidColorBrush(Color.FromArgb(20, 25, 118, 210)),
            Visibility = Visibility.Hidden,
            IsHitTestVisible = false,
            Tag = "sel-overlay"
        };
        Canvas.SetLeft(r, -3);
        Canvas.SetTop(r,  -3);
        c.Children.Add(r);
    }

    private static void DrawEventInContainer(Canvas c, BpmnNode node, double w, double h)
    {
        var (fill, stroke, strokeW) = node.Type switch
        {
            BpmnNodeType.StartEvent           => (Color.FromRgb(200, 230, 201), Color.FromRgb(46, 125, 50),   2.0),
            BpmnNodeType.EndEvent             => (Color.FromRgb(255, 205, 210), Color.FromRgb(183, 28, 28),   3.0),
            BpmnNodeType.IntermediateThrowEvent => (Color.FromRgb(200, 220, 255), Color.FromRgb(21, 101, 192), 1.5),
            _                                 => (Colors.White,                  Color.FromRgb(21, 101, 192), 1.5)
        };

        PutIn(c, new Ellipse
        {
            Width = w, Height = h,
            Fill   = new SolidColorBrush(fill),
            Stroke = new SolidColorBrush(stroke),
            StrokeThickness = strokeW
        }, 0, 0);

        if (node.Type is BpmnNodeType.IntermediateCatchEvent or BpmnNodeType.IntermediateThrowEvent)
        {
            PutIn(c, new Ellipse
            {
                Width = w - 6, Height = h - 6,
                Fill   = Brushes.Transparent,
                Stroke = new SolidColorBrush(Color.FromRgb(21, 101, 192)),
                StrokeThickness = 1
            }, 3, 3);
        }

        if (!string.IsNullOrWhiteSpace(node.Name))
        {
            double lw = Math.Max(w + 30, 60);
            var tb = new TextBlock
            {
                Text = node.Name, FontSize = 10,
                TextAlignment = TextAlignment.Center,
                TextWrapping  = TextWrapping.Wrap,
                Width = lw,
                Foreground = new SolidColorBrush(Color.FromRgb(30, 30, 30))
            };
            Canvas.SetLeft(tb, w / 2 - lw / 2);
            Canvas.SetTop(tb,  h + 2);
            c.Children.Add(tb);
        }
    }

    private static void DrawGatewayInContainer(Canvas c, BpmnNode node, double w, double h)
    {
        double cx = w / 2, cy = h / 2;

        c.Children.Add(new Polygon
        {
            Points = new PointCollection { new(cx, 0), new(w, cy), new(cx, h), new(0, cy) },
            Fill   = new SolidColorBrush(Color.FromRgb(255, 249, 196)),
            Stroke = new SolidColorBrush(Color.FromRgb(245, 127, 23)),
            StrokeThickness = 1.5
        });

        double ms         = w * 0.3;
        var black         = new SolidColorBrush(Colors.Black);
        var darkOrange    = new SolidColorBrush(Color.FromRgb(100, 60, 0));

        switch (node.Type)
        {
            case BpmnNodeType.ExclusiveGateway:
                c.Children.Add(new Line { X1 = cx-ms, Y1 = cy-ms, X2 = cx+ms, Y2 = cy+ms, Stroke = darkOrange, StrokeThickness = 2.5 });
                c.Children.Add(new Line { X1 = cx+ms, Y1 = cy-ms, X2 = cx-ms, Y2 = cy+ms, Stroke = darkOrange, StrokeThickness = 2.5 });
                break;
            case BpmnNodeType.ParallelGateway:
                c.Children.Add(new Line { X1 = cx-ms, Y1 = cy, X2 = cx+ms, Y2 = cy, Stroke = black, StrokeThickness = 2.5 });
                c.Children.Add(new Line { X1 = cx, Y1 = cy-ms, X2 = cx, Y2 = cy+ms, Stroke = black, StrokeThickness = 2.5 });
                break;
            case BpmnNodeType.InclusiveGateway:
                PutIn(c, new Ellipse
                {
                    Width = ms * 1.6, Height = ms * 1.6,
                    Fill = Brushes.Transparent, Stroke = black, StrokeThickness = 2.5
                }, cx - ms * 0.8, cy - ms * 0.8);
                break;
            case BpmnNodeType.EventBasedGateway:
                PutIn(c, new Ellipse
                {
                    Width = ms * 1.4, Height = ms * 1.4,
                    Fill = Brushes.Transparent, Stroke = black, StrokeThickness = 1.5
                }, cx - ms * 0.7, cy - ms * 0.7);
                break;
        }

        if (!string.IsNullOrWhiteSpace(node.Name))
        {
            double lw = Math.Max(w + 30, 80);
            var tb = new TextBlock
            {
                Text = node.Name, FontSize = 10,
                TextAlignment = TextAlignment.Center,
                TextWrapping  = TextWrapping.Wrap,
                Width = lw
            };
            Canvas.SetLeft(tb, cx - lw / 2);
            Canvas.SetTop(tb,  h + 2);
            c.Children.Add(tb);
        }
    }

    private static void DrawTaskInContainer(Canvas c, BpmnNode node, double w, double h)
    {
        bool isCall = node.Type == BpmnNodeType.CallActivity;
        bool isSub  = node.Type == BpmnNodeType.SubProcess;

        var (fillClr, strokeClr) = node.Type switch
        {
            BpmnNodeType.ServiceTask  => (Color.FromRgb(227, 242, 253), Color.FromRgb(21, 101, 192)),
            BpmnNodeType.UserTask     => (Color.FromRgb(232, 245, 233), Color.FromRgb(46, 125, 50)),
            BpmnNodeType.SendTask     => (Color.FromRgb(255, 243, 224), Color.FromRgb(230, 81, 0)),
            BpmnNodeType.ReceiveTask  => (Color.FromRgb(225, 245, 254), Color.FromRgb(1, 87, 155)),
            BpmnNodeType.CallActivity => (Color.FromRgb(237, 231, 246), Color.FromRgb(74, 20, 140)),
            BpmnNodeType.SubProcess   => (Color.FromRgb(248, 248, 248), Color.FromRgb(100, 100, 100)),
            BpmnNodeType.ScriptTask   => (Color.FromRgb(255, 253, 231), Color.FromRgb(130, 119, 23)),
            BpmnNodeType.ManualTask   => (Color.FromRgb(240, 244, 255), Color.FromRgb(63, 81, 181)),
            _                         => (Color.FromRgb(240, 240, 240), Color.FromRgb(90, 90, 90))
        };

        PutIn(c, new Rectangle
        {
            Width = w, Height = h,
            Fill   = new SolidColorBrush(fillClr),
            Stroke = new SolidColorBrush(strokeClr),
            StrokeThickness = isCall ? 3 : 1,
            StrokeDashArray = isSub ? new DoubleCollection([4, 2]) : null,
            RadiusX = 5, RadiusY = 5
        }, 0, 0);

        string badge = node.Type switch
        {
            BpmnNodeType.ServiceTask  => "⚙",
            BpmnNodeType.UserTask     => "👤",
            BpmnNodeType.SendTask     => "✉",
            BpmnNodeType.ReceiveTask  => "📨",
            BpmnNodeType.ScriptTask   => "≡",
            BpmnNodeType.ManualTask   => "✋",
            BpmnNodeType.CallActivity => "⊞",
            _ => ""
        };

        if (badge.Length > 0)
        {
            var badgeTb = new TextBlock { Text = badge, FontSize = 10, Foreground = new SolidColorBrush(strokeClr) };
            Canvas.SetLeft(badgeTb, 3);
            Canvas.SetTop(badgeTb,  2);
            c.Children.Add(badgeTb);
        }

        if (node.HasLoopMarker)
        {
            var loopTb = new TextBlock { Text = "↺", FontSize = 10, Foreground = Brushes.DimGray };
            Canvas.SetLeft(loopTb, w / 2 - 5);
            Canvas.SetTop(loopTb,  h - 14);
            c.Children.Add(loopTb);
        }

        if (!string.IsNullOrWhiteSpace(node.Name))
            AddCenteredText(c, node.Name, w, h, 11, strokeClr);
    }

    private static void AddCenteredText(Canvas c, string text,
        double w, double h, double fontSize, Color foreground)
    {
        double padH = 14, padV = 4;
        var tb = new TextBlock
        {
            Text = text, FontSize = fontSize,
            TextAlignment = TextAlignment.Center,
            TextWrapping  = TextWrapping.Wrap,
            Width = Math.Max(1, w - padH),
            Foreground = new SolidColorBrush(foreground)
        };
        tb.Measure(new Size(tb.Width, double.PositiveInfinity));
        double th = tb.DesiredSize.Height;
        Canvas.SetLeft(tb, padH / 2);
        Canvas.SetTop(tb,  Math.Max(padV, (h - th) / 2));
        c.Children.Add(tb);
    }

    // ── Static geometry helpers ───────────────────────────────────────────────

    private static void PutIn(Canvas c, UIElement elem, double x, double y)
    {
        Canvas.SetLeft(elem, x);
        Canvas.SetTop(elem,  y);
        c.Children.Add(elem);
    }

    private static void PlaceOnCanvas(Canvas canvas, UIElement elem, double x, double y, int zIndex)
    {
        Canvas.SetLeft(elem, x);
        Canvas.SetTop(elem,  y);
        Canvas.SetZIndex(elem, zIndex);
        canvas.Children.Add(elem);
    }

    private static Point ContainerCenter(Canvas c) =>
        new(Canvas.GetLeft(c) + c.Width / 2, Canvas.GetTop(c) + c.Height / 2);

    // Finds where the line from center→target exits the container's bounding box.
    private static Point ClipToEdge(Point center, Point target, Canvas c)
    {
        double l = Canvas.GetLeft(c), t = Canvas.GetTop(c);
        double r = l + c.Width,       b = t + c.Height;

        double dx = target.X - center.X, dy = target.Y - center.Y;
        if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001) return center;

        double tMin = double.MaxValue;
        if (dx > 0) tMin = Math.Min(tMin, (r - center.X) / dx);
        else if (dx < 0) tMin = Math.Min(tMin, (l - center.X) / dx);
        if (dy > 0) tMin = Math.Min(tMin, (b - center.Y) / dy);
        else if (dy < 0) tMin = Math.Min(tMin, (t - center.Y) / dy);

        return new Point(center.X + dx * tMin, center.Y + dy * tMin);
    }

    private static SolidColorBrush FlowBrush() =>
        new(Color.FromRgb(80, 80, 80));
}
