using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DiagramApp.Models;

namespace DiagramApp.Services;

public class BpmnRenderer
{
    private const double Margin = 30;

    private double _offsetX;
    private double _offsetY;

    public (double Width, double Height) Render(BpmnModel model, Canvas canvas)
    {
        canvas.Children.Clear();

        if (model.Nodes.Count == 0)
            return (400, 300);

        var (minX, minY, maxX, maxY) = model.GetBounds();
        _offsetX = (minX < 0 ? -minX : 0) + Margin;
        _offsetY = (minY < 0 ? -minY : 0) + Margin;

        double w = maxX - minX + Margin * 2;
        double h = maxY - minY + Margin * 2;
        canvas.Width = w;
        canvas.Height = h;
        canvas.Background = new SolidColorBrush(Color.FromRgb(248, 249, 250));

        DrawParticipants(model, canvas);
        DrawFlows(model, canvas);
        DrawNodes(model, canvas);

        return (w, h);
    }

    // ── Participants (pools) ──────────────────────────────────────────────────

    private void DrawParticipants(BpmnModel model, Canvas canvas)
    {
        foreach (var node in model.Nodes.Where(n => n.Type == BpmnNodeType.Participant && n.Width > 0))
        {
            double x = _offsetX + node.X;
            double y = _offsetY + node.Y;

            var border = new Rectangle
            {
                Width = node.Width, Height = node.Height,
                Fill = new SolidColorBrush(Color.FromRgb(245, 247, 252)),
                Stroke = new SolidColorBrush(Color.FromRgb(144, 164, 174)),
                StrokeThickness = 1.5
            };
            Place(canvas, border, x, y);

            // Label band (left 30px)
            if (!string.IsNullOrWhiteSpace(node.Name))
            {
                var band = new Rectangle
                {
                    Width = 30, Height = node.Height,
                    Fill = new SolidColorBrush(Color.FromRgb(224, 231, 247)),
                    Stroke = new SolidColorBrush(Color.FromRgb(144, 164, 174)),
                    StrokeThickness = 1
                };
                Place(canvas, band, x, y);

                // Rotated label inside band
                var tb = new TextBlock
                {
                    Text = node.Name,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(55, 71, 79)),
                    Width = node.Height - 8,
                    TextAlignment = TextAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    RenderTransform = new RotateTransform(-90),
                    RenderTransformOrigin = new Point(0, 0)
                };
                Canvas.SetLeft(tb, x + 15);
                Canvas.SetTop(tb, y + node.Height - 4);
                canvas.Children.Add(tb);
            }
        }
    }

    // ── Sequence flows ────────────────────────────────────────────────────────

    private void DrawFlows(BpmnModel model, Canvas canvas)
    {
        var flowBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80));

        foreach (var flow in model.Flows)
        {
            if (flow.Waypoints.Count < 2) continue;

            var pts = flow.Waypoints
                .Select(p => new Point(_offsetX + p.X, _offsetY + p.Y))
                .ToArray();

            for (int i = 0; i < pts.Length - 1; i++)
            {
                canvas.Children.Add(new Line
                {
                    X1 = pts[i].X, Y1 = pts[i].Y,
                    X2 = pts[i + 1].X, Y2 = pts[i + 1].Y,
                    Stroke = flowBrush,
                    StrokeThickness = 1.5
                });
            }

            DrawArrowhead(canvas, pts[^2], pts[^1], flowBrush);

            // Flow label
            if (!string.IsNullOrWhiteSpace(flow.Name))
            {
                var mid = pts[pts.Length / 2];
                var lbl = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)),
                    Padding = new Thickness(2, 0, 2, 0),
                    Child = new TextBlock
                    {
                        Text = flow.Name, FontSize = 10,
                        Foreground = new SolidColorBrush(Color.FromRgb(60, 60, 60))
                    }
                };
                Canvas.SetLeft(lbl, mid.X - 20);
                Canvas.SetTop(lbl, mid.Y - 9);
                canvas.Children.Add(lbl);
            }
        }
    }

    private static void DrawArrowhead(Canvas canvas, Point from, Point to, Brush fill)
    {
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.001) return;

        double ux = dx / len, uy = dy / len;
        const double aLen = 9, aHalf = 4.5;
        double bx = to.X - aLen * ux, by = to.Y - aLen * uy;

        canvas.Children.Add(new Polygon
        {
            Points = new PointCollection
            {
                to,
                new(bx + aHalf * uy, by - aHalf * ux),
                new(bx - aHalf * uy, by + aHalf * ux)
            },
            Fill = fill,
            Stroke = fill,
            StrokeLineJoin = PenLineJoin.Round
        });
    }

    // ── BPMN nodes ────────────────────────────────────────────────────────────

    private void DrawNodes(BpmnModel model, Canvas canvas)
    {
        foreach (var node in model.Nodes.Where(n => n.Width > 0 && n.Height > 0))
        {
            double x = _offsetX + node.X;
            double y = _offsetY + node.Y;
            double w = node.Width, h = node.Height;

            switch (node.Type)
            {
                case BpmnNodeType.StartEvent:
                case BpmnNodeType.EndEvent:
                case BpmnNodeType.IntermediateCatchEvent:
                case BpmnNodeType.IntermediateThrowEvent:
                case BpmnNodeType.BoundaryEvent:
                    DrawEvent(canvas, node, x, y, w, h);
                    break;

                case BpmnNodeType.ExclusiveGateway:
                case BpmnNodeType.ParallelGateway:
                case BpmnNodeType.InclusiveGateway:
                case BpmnNodeType.EventBasedGateway:
                case BpmnNodeType.ComplexGateway:
                    DrawGateway(canvas, node, x, y, w, h);
                    break;

                case BpmnNodeType.Participant:
                case BpmnNodeType.Lane:
                    // Already drawn
                    break;

                default:
                    DrawTask(canvas, node, x, y, w, h);
                    break;
            }
        }
    }

    private static void DrawEvent(Canvas canvas, BpmnNode node, double x, double y, double w, double h)
    {
        var (fill, stroke, strokeW) = node.Type switch
        {
            BpmnNodeType.StartEvent => (Color.FromRgb(200, 230, 201), Color.FromRgb(46, 125, 50), 2.0),
            BpmnNodeType.EndEvent => (Color.FromRgb(255, 205, 210), Color.FromRgb(183, 28, 28), 3.0),
            BpmnNodeType.IntermediateThrowEvent => (Color.FromRgb(200, 220, 255), Color.FromRgb(21, 101, 192), 1.5),
            _ => (Colors.White, Color.FromRgb(21, 101, 192), 1.5)
        };

        Place(canvas, new Ellipse
        {
            Width = w, Height = h,
            Fill = new SolidColorBrush(fill),
            Stroke = new SolidColorBrush(stroke),
            StrokeThickness = strokeW
        }, x, y);

        // Double ring for intermediate events
        if (node.Type is BpmnNodeType.IntermediateCatchEvent or BpmnNodeType.IntermediateThrowEvent)
        {
            Place(canvas, new Ellipse
            {
                Width = w - 6, Height = h - 6,
                Fill = Brushes.Transparent,
                Stroke = new SolidColorBrush(Color.FromRgb(21, 101, 192)),
                StrokeThickness = 1
            }, x + 3, y + 3);
        }

        // Label below event
        if (!string.IsNullOrWhiteSpace(node.Name))
        {
            var tb = new TextBlock
            {
                Text = node.Name, FontSize = 10,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Width = Math.Max(w + 30, 60),
                Foreground = new SolidColorBrush(Color.FromRgb(30, 30, 30))
            };
            Canvas.SetLeft(tb, x + w / 2 - tb.Width / 2);
            Canvas.SetTop(tb, y + h + 2);
            canvas.Children.Add(tb);
        }
    }

    private static void DrawGateway(Canvas canvas, BpmnNode node, double x, double y, double w, double h)
    {
        double cx = x + w / 2, cy = y + h / 2;

        canvas.Children.Add(new Polygon
        {
            Points = new PointCollection
            {
                new(cx, y), new(x + w, cy), new(cx, y + h), new(x, cy)
            },
            Fill = new SolidColorBrush(Color.FromRgb(255, 249, 196)),
            Stroke = new SolidColorBrush(Color.FromRgb(245, 127, 23)),
            StrokeThickness = 1.5
        });

        double ms = w * 0.3;
        var black = new SolidColorBrush(Colors.Black);
        var darkOrange = new SolidColorBrush(Color.FromRgb(100, 60, 0));

        switch (node.Type)
        {
            case BpmnNodeType.ExclusiveGateway:
                canvas.Children.Add(new Line { X1 = cx - ms, Y1 = cy - ms, X2 = cx + ms, Y2 = cy + ms, Stroke = darkOrange, StrokeThickness = 2.5 });
                canvas.Children.Add(new Line { X1 = cx + ms, Y1 = cy - ms, X2 = cx - ms, Y2 = cy + ms, Stroke = darkOrange, StrokeThickness = 2.5 });
                break;

            case BpmnNodeType.ParallelGateway:
                canvas.Children.Add(new Line { X1 = cx - ms, Y1 = cy, X2 = cx + ms, Y2 = cy, Stroke = black, StrokeThickness = 2.5 });
                canvas.Children.Add(new Line { X1 = cx, Y1 = cy - ms, X2 = cx, Y2 = cy + ms, Stroke = black, StrokeThickness = 2.5 });
                break;

            case BpmnNodeType.InclusiveGateway:
                Place(canvas, new Ellipse
                {
                    Width = ms * 1.6, Height = ms * 1.6,
                    Fill = Brushes.Transparent,
                    Stroke = black, StrokeThickness = 2.5
                }, cx - ms * 0.8, cy - ms * 0.8);
                break;

            case BpmnNodeType.EventBasedGateway:
                Place(canvas, new Ellipse
                {
                    Width = ms * 1.4, Height = ms * 1.4,
                    Fill = Brushes.Transparent,
                    Stroke = black, StrokeThickness = 1.5
                }, cx - ms * 0.7, cy - ms * 0.7);
                break;
        }

        // Gateway label (below)
        if (!string.IsNullOrWhiteSpace(node.Name))
        {
            var tb = new TextBlock
            {
                Text = node.Name, FontSize = 10,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Width = Math.Max(w + 30, 80)
            };
            Canvas.SetLeft(tb, cx - tb.Width / 2);
            Canvas.SetTop(tb, y + h + 2);
            canvas.Children.Add(tb);
        }
    }

    private static void DrawTask(Canvas canvas, BpmnNode node, double x, double y, double w, double h)
    {
        bool isCall = node.Type == BpmnNodeType.CallActivity;
        bool isSub = node.Type == BpmnNodeType.SubProcess;

        var (fillClr, strokeClr) = node.Type switch
        {
            BpmnNodeType.ServiceTask => (Color.FromRgb(227, 242, 253), Color.FromRgb(21, 101, 192)),
            BpmnNodeType.UserTask => (Color.FromRgb(232, 245, 233), Color.FromRgb(46, 125, 50)),
            BpmnNodeType.SendTask => (Color.FromRgb(255, 243, 224), Color.FromRgb(230, 81, 0)),
            BpmnNodeType.ReceiveTask => (Color.FromRgb(225, 245, 254), Color.FromRgb(1, 87, 155)),
            BpmnNodeType.CallActivity => (Color.FromRgb(237, 231, 246), Color.FromRgb(74, 20, 140)),
            BpmnNodeType.SubProcess => (Color.FromRgb(248, 248, 248), Color.FromRgb(100, 100, 100)),
            BpmnNodeType.ScriptTask => (Color.FromRgb(255, 253, 231), Color.FromRgb(130, 119, 23)),
            BpmnNodeType.ManualTask => (Color.FromRgb(240, 244, 255), Color.FromRgb(63, 81, 181)),
            _ => (Color.FromRgb(240, 240, 240), Color.FromRgb(90, 90, 90))
        };

        Place(canvas, new Rectangle
        {
            Width = w, Height = h,
            Fill = new SolidColorBrush(fillClr),
            Stroke = new SolidColorBrush(strokeClr),
            StrokeThickness = isCall ? 3 : 1,
            StrokeDashArray = isSub ? new DoubleCollection([4, 2]) : null,
            RadiusX = 5, RadiusY = 5
        }, x, y);

        // Type badge (top-left)
        string badge = node.Type switch
        {
            BpmnNodeType.ServiceTask => "⚙",
            BpmnNodeType.UserTask => "👤",
            BpmnNodeType.SendTask => "✉",
            BpmnNodeType.ReceiveTask => "📨",
            BpmnNodeType.ScriptTask => "≡",
            BpmnNodeType.ManualTask => "✋",
            BpmnNodeType.CallActivity => "⊞",
            _ => ""
        };

        if (badge.Length > 0)
        {
            var badgeTb = new TextBlock
            {
                Text = badge, FontSize = 10,
                Foreground = new SolidColorBrush(strokeClr)
            };
            Canvas.SetLeft(badgeTb, x + 3);
            Canvas.SetTop(badgeTb, y + 2);
            canvas.Children.Add(badgeTb);
        }

        // Loop marker
        if (node.HasLoopMarker)
        {
            var loopTb = new TextBlock { Text = "↺", FontSize = 10, Foreground = Brushes.DimGray };
            Canvas.SetLeft(loopTb, x + w / 2 - 5);
            Canvas.SetTop(loopTb, y + h - 14);
            canvas.Children.Add(loopTb);
        }

        // Task name
        if (!string.IsNullOrWhiteSpace(node.Name))
            AddCenteredText(canvas, node.Name, x, y, w, h, 11, strokeClr);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void AddCenteredText(Canvas canvas, string text,
        double x, double y, double w, double h, double fontSize, Color foreground)
    {
        double padH = 14;
        double padV = 4;
        var tb = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Width = Math.Max(1, w - padH),
            Foreground = new SolidColorBrush(foreground)
        };
        tb.Measure(new Size(tb.Width, double.PositiveInfinity));
        double th = tb.DesiredSize.Height;
        Canvas.SetLeft(tb, x + padH / 2);
        Canvas.SetTop(tb, y + Math.Max(padV, (h - th) / 2));
        canvas.Children.Add(tb);
    }

    private static void Place(Canvas canvas, UIElement elem, double x, double y)
    {
        Canvas.SetLeft(elem, x);
        Canvas.SetTop(elem, y);
        canvas.Children.Add(elem);
    }
}
