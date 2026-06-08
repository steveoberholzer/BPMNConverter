using System.IO;
using SysPath = System.IO.Path;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DiagramApp.Models;
using DiagramApp.Services;
using Microsoft.Win32;

namespace DiagramApp;

public partial class MainWindow : Window
{
    private readonly BpmnParser     _parser   = new();
    private readonly BpmnRenderer   _renderer = new();
    private readonly DiagramExporter _exporter = new();

    private BpmnModel? _currentModel;
    private string?    _currentFile;
    private double     _zoom = 1.0;
    private (double W, double H) _diagramSize;

    private const string SamplesFolder = "Samples";

    // ── Edit state ────────────────────────────────────────────────────────────

    private Dictionary<string, Canvas> _nodeContainers = new();

    // Selection
    private readonly HashSet<string> _selectedIds = new();

    // Drag
    private bool   _isDragging;
    private Point  _dragAnchor;                              // canvas coords at drag start
    private Dictionary<string, (double L, double T)> _dragOrigins = new();

    public MainWindow()
    {
        InitializeComponent();

        // Attach canvas-level mouse events (fire even when cursor is over children)
        DiagramCanvas.MouseLeftButtonDown += Canvas_MouseLeftButtonDown;
        DiagramCanvas.MouseMove           += Canvas_MouseMove;
        DiagramCanvas.MouseLeftButtonUp   += Canvas_MouseLeftButtonUp;

        LoadFileList();
    }

    // ── File list ─────────────────────────────────────────────────────────────

    private void LoadFileList()
    {
        string samplesPath = SysPath.Combine(AppDomain.CurrentDomain.BaseDirectory, SamplesFolder);
        if (!Directory.Exists(samplesPath))
            samplesPath = SysPath.Combine(
                Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory)?.FullName
                ?? AppDomain.CurrentDomain.BaseDirectory,
                SamplesFolder);

        if (!Directory.Exists(samplesPath))
        {
            StatusLeft.Text = $"Samples folder not found at: {samplesPath}";
            return;
        }

        var files = Directory.GetFiles(samplesPath, "*.bpmn")
                             .OrderBy(f => f)
                             .Select(SysPath.GetFileName)
                             .Where(f => f != null)
                             .Cast<string>()
                             .ToList();

        FileListBox.ItemsSource = files;
        StatusLeft.Text = $"Found {files.Count} BPMN file(s) in Samples folder.";
    }

    private void FileListBox_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (FileListBox.SelectedItem is not string fileName) return;

        string samplesPath = ResolveSamplesPath();
        string fullPath    = SysPath.Combine(samplesPath, fileName);

        if (!File.Exists(fullPath))
        {
            StatusLeft.Text = $"File not found: {fullPath}";
            return;
        }

        LoadBpmnFile(fullPath);
    }

    // ── BPMN loading & rendering ──────────────────────────────────────────────

    private void LoadBpmnFile(string path)
    {
        _currentFile  = path;
        Mouse.OverrideCursor = Cursors.Wait;

        try
        {
            _currentModel = _parser.Parse(path);
            RenderModel();

            int nodes = _currentModel.Nodes.Count(n => n.Type is not BpmnNodeType.Participant
                                                                 and not BpmnNodeType.Lane);
            int flows = _currentModel.Flows.Count;
            int pools = _currentModel.Nodes.Count(n => n.Type == BpmnNodeType.Participant);

            StatusLeft.Text  = $"{SysPath.GetFileName(path)}  —  {_currentModel.Name}";
            StatusRight.Text = $"{nodes} elements · {flows} flows" +
                               (pools > 0 ? $" · {pools} pool(s)" : "");

            BtnExportVsdx.IsEnabled  = true;
            BtnExportPdf.IsEnabled   = true;
            BtnFit.IsEnabled         = true;
            BtnAutoLayout.IsEnabled  = true;
            CboLayout.IsEnabled      = true;

            FitDiagram();
        }
        catch (Exception ex)
        {
            StatusLeft.Text = $"Error loading {SysPath.GetFileName(path)}: {ex.Message}";
            MessageBox.Show($"Failed to load BPMN file:\n{ex.Message}", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    /// <summary>Re-render the current model (e.g. after layout or undo).</summary>
    private void RenderModel()
    {
        if (_currentModel == null) return;

        DeselectAll(notify: false);
        _nodeContainers.Clear();

        var (w, h, containers) = _renderer.Render(_currentModel, DiagramCanvas);
        _nodeContainers = containers;
        _diagramSize    = (w, h);

        UpdateEditToolbarState();
    }

    // ── Export ────────────────────────────────────────────────────────────────

    private void BtnExportVsdx_Click(object sender, RoutedEventArgs e) =>
        RunExport("Visio Files|*.vsdx", ".vsdx",
                  path => _exporter.ExportToVsdx(_currentModel!, path));

    private void BtnExportPdf_Click(object sender, RoutedEventArgs e) =>
        RunExport("PDF Files|*.pdf", ".pdf",
                  path => _exporter.ExportToPdf(_currentModel!, path));

    private void RunExport(string filter, string ext, Action<string> export)
    {
        if (_currentModel == null) return;

        var dlg = new SaveFileDialog
        {
            Filter     = filter,
            FileName   = SysPath.GetFileNameWithoutExtension(_currentFile ?? "diagram"),
            DefaultExt = ext
        };
        if (dlg.ShowDialog() != true) return;

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            export(dlg.FileName);
            StatusLeft.Text = $"Exported → {dlg.FileName}";
            MessageBox.Show($"Saved to:\n{dlg.FileName}", "Export Complete",
                            MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Export failed:\n{ex.Message}", "Export Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    // ── Zoom ──────────────────────────────────────────────────────────────────

    private void BtnZoomIn_Click(object sender, RoutedEventArgs e)  => ApplyZoom(_zoom * 1.2);
    private void BtnZoomOut_Click(object sender, RoutedEventArgs e) => ApplyZoom(_zoom / 1.2);
    private void BtnFit_Click(object sender, RoutedEventArgs e)     => FitDiagram();

    private void FitDiagram()
    {
        if (_diagramSize.W < 1 || _diagramSize.H < 1) return;

        DiagramScrollViewer.UpdateLayout();
        double vw = DiagramScrollViewer.ViewportWidth  - 20;
        double vh = DiagramScrollViewer.ViewportHeight - 20;
        if (vw < 1 || vh < 1) return;

        ApplyZoom(Math.Min(vw / _diagramSize.W, vh / _diagramSize.H));
    }

    private void ApplyZoom(double newZoom)
    {
        _zoom = Math.Max(0.08, Math.Min(4.0, newZoom));
        ZoomTransform.ScaleX = _zoom;
        ZoomTransform.ScaleY = _zoom;
        ZoomLabel.Text = $"{_zoom:P0}";
    }

    private void DiagramScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;
        ApplyZoom(e.Delta > 0 ? _zoom * 1.1 : _zoom / 1.1);
    }

    // ── Canvas mouse events (selection + drag) ────────────────────────────────

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_currentModel == null) return;

        var pos       = e.GetPosition(DiagramCanvas);
        var container = FindNodeContainer(e.OriginalSource as DependencyObject);

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (container?.Tag is string nodeId)
        {
            if (ctrl)
            {
                ToggleSelection(nodeId);
            }
            else
            {
                if (!_selectedIds.Contains(nodeId))
                {
                    DeselectAll(notify: false);
                    AddToSelection(nodeId);
                }
                // Always start a potential drag when clicking a selected node
                BeginDrag(pos);
            }
        }
        else
        {
            // Click on background — clear selection
            DeselectAll();
        }

        e.Handled = true;
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || e.LeftButton != MouseButtonState.Pressed) return;

        var pos   = e.GetPosition(DiagramCanvas);
        double dx = pos.X - _dragAnchor.X;
        double dy = pos.Y - _dragAnchor.Y;

        foreach (var id in _selectedIds)
        {
            if (!_nodeContainers.TryGetValue(id, out var c)) continue;
            if (!_dragOrigins.TryGetValue(id, out var origin)) continue;

            double newL = origin.L + dx;
            double newT = origin.T + dy;

            Canvas.SetLeft(c, newL);
            Canvas.SetTop(c,  newT);
        }

        // Live-redraw flows as shapes move
        _renderer.RedrawFlows(_currentModel!, DiagramCanvas, _nodeContainers);
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;

        _isDragging  = false;
        _dragOrigins.Clear();
        DiagramCanvas.ReleaseMouseCapture();

        // Commit moved positions back to the model
        SyncContainerPositionsToModel();

        // Final flow redraw and canvas resize
        _renderer.RedrawFlows(_currentModel!, DiagramCanvas, _nodeContainers);
        ResizeCanvasToFitContent();
    }

    // ── Drag helpers ──────────────────────────────────────────────────────────

    private void BeginDrag(Point anchor)
    {
        _isDragging  = true;
        _dragAnchor  = anchor;
        _dragOrigins = _selectedIds
            .Where(id => _nodeContainers.ContainsKey(id))
            .ToDictionary(
                id => id,
                id => (Canvas.GetLeft(_nodeContainers[id]), Canvas.GetTop(_nodeContainers[id])));

        DiagramCanvas.CaptureMouse();
    }

    // ── Selection helpers ─────────────────────────────────────────────────────

    private Canvas? FindNodeContainer(DependencyObject? hit)
    {
        var current = hit;
        while (current != null && !ReferenceEquals(current, DiagramCanvas))
        {
            if (current is Canvas c && c.Tag is string id && _nodeContainers.ContainsKey(id))
                return c;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void AddToSelection(string nodeId)
    {
        _selectedIds.Add(nodeId);
        SetSelectionOverlay(nodeId, true);
        UpdateEditToolbarState();
    }

    private void ToggleSelection(string nodeId)
    {
        if (_selectedIds.Contains(nodeId))
        {
            _selectedIds.Remove(nodeId);
            SetSelectionOverlay(nodeId, false);
        }
        else
        {
            _selectedIds.Add(nodeId);
            SetSelectionOverlay(nodeId, true);
        }
        UpdateEditToolbarState();
    }

    private void DeselectAll(bool notify = true)
    {
        foreach (var id in _selectedIds.ToList())
            SetSelectionOverlay(id, false);
        _selectedIds.Clear();
        if (notify) UpdateEditToolbarState();
    }

    private void SetSelectionOverlay(string nodeId, bool visible)
    {
        if (!_nodeContainers.TryGetValue(nodeId, out var c)) return;
        var overlay = c.Children.OfType<Rectangle>()
                       .FirstOrDefault(r => r.Tag is "sel-overlay");
        if (overlay != null)
            overlay.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
    }

    private void UpdateEditToolbarState()
    {
        int n = _selectedIds.Count;
        LblSelection.Text = n == 0 ? "No selection"
                          : n == 1 ? "1 node selected"
                          : $"{n} nodes selected";

        bool two = n >= 2;
        BtnAlignLeft.IsEnabled  = two;
        BtnAlignCtrH.IsEnabled  = two;
        BtnAlignRight.IsEnabled = two;
        BtnAlignTop.IsEnabled   = two;
        BtnAlignMidV.IsEnabled  = two;
        BtnAlignBot.IsEnabled   = two;
        BtnDistribH.IsEnabled   = n >= 3;
        BtnDistribV.IsEnabled   = n >= 3;
    }

    // ── Align operations ──────────────────────────────────────────────────────

    private void BtnAlignLeft_Click(object sender, RoutedEventArgs e)
    {
        double target = SelectedContainers().Min(p => Canvas.GetLeft(p.C));
        foreach (var (_, c) in SelectedContainers())
            Canvas.SetLeft(c, target);
        CommitAndRefresh();
    }

    private void BtnAlignCtrH_Click(object sender, RoutedEventArgs e)
    {
        double target = SelectedContainers().Average(p => Canvas.GetLeft(p.C) + p.C.Width / 2);
        foreach (var (_, c) in SelectedContainers())
            Canvas.SetLeft(c, target - c.Width / 2);
        CommitAndRefresh();
    }

    private void BtnAlignRight_Click(object sender, RoutedEventArgs e)
    {
        double target = SelectedContainers().Max(p => Canvas.GetLeft(p.C) + p.C.Width);
        foreach (var (_, c) in SelectedContainers())
            Canvas.SetLeft(c, target - c.Width);
        CommitAndRefresh();
    }

    private void BtnAlignTop_Click(object sender, RoutedEventArgs e)
    {
        double target = SelectedContainers().Min(p => Canvas.GetTop(p.C));
        foreach (var (_, c) in SelectedContainers())
            Canvas.SetTop(c, target);
        CommitAndRefresh();
    }

    private void BtnAlignMidV_Click(object sender, RoutedEventArgs e)
    {
        double target = SelectedContainers().Average(p => Canvas.GetTop(p.C) + p.C.Height / 2);
        foreach (var (_, c) in SelectedContainers())
            Canvas.SetTop(c, target - c.Height / 2);
        CommitAndRefresh();
    }

    private void BtnAlignBot_Click(object sender, RoutedEventArgs e)
    {
        double target = SelectedContainers().Max(p => Canvas.GetTop(p.C) + p.C.Height);
        foreach (var (_, c) in SelectedContainers())
            Canvas.SetTop(c, target - c.Height);
        CommitAndRefresh();
    }

    private void BtnDistribH_Click(object sender, RoutedEventArgs e)
    {
        var sorted = SelectedContainers()
            .OrderBy(p => Canvas.GetLeft(p.C) + p.C.Width / 2)
            .ToList();
        if (sorted.Count < 3) return;

        double left  = Canvas.GetLeft(sorted[0].C);
        double right = Canvas.GetLeft(sorted[^1].C) + sorted[^1].C.Width;
        double totalNodeWidth = sorted.Sum(p => p.C.Width);
        double gap = (right - left - totalNodeWidth) / (sorted.Count - 1);

        double x = left;
        foreach (var (_, c) in sorted)
        {
            Canvas.SetLeft(c, x);
            x += c.Width + gap;
        }
        CommitAndRefresh();
    }

    private void BtnDistribV_Click(object sender, RoutedEventArgs e)
    {
        var sorted = SelectedContainers()
            .OrderBy(p => Canvas.GetTop(p.C) + p.C.Height / 2)
            .ToList();
        if (sorted.Count < 3) return;

        double top    = Canvas.GetTop(sorted[0].C);
        double bottom = Canvas.GetTop(sorted[^1].C) + sorted[^1].C.Height;
        double totalNodeHeight = sorted.Sum(p => p.C.Height);
        double gap = (bottom - top - totalNodeHeight) / (sorted.Count - 1);

        double y = top;
        foreach (var (_, c) in sorted)
        {
            Canvas.SetTop(c, y);
            y += c.Height + gap;
        }
        CommitAndRefresh();
    }

    // ── Auto layout ───────────────────────────────────────────────────────────

    private void BtnAutoLayout_Click(object sender, RoutedEventArgs e)
    {
        if (_currentModel == null) return;

        var layoutType = (LayoutType)CboLayout.SelectedIndex;

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            LayoutEngine.ApplyLayout(_currentModel, layoutType);
            RenderModel();
            FitDiagram();
            StatusLeft.Text = $"Layout applied: {((ComboBoxItem)CboLayout.SelectedItem).Content}";
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private IEnumerable<(string Id, Canvas C)> SelectedContainers() =>
        _selectedIds
            .Where(id => _nodeContainers.ContainsKey(id))
            .Select(id => (id, _nodeContainers[id]));

    /// <summary>
    /// After any alignment or drag, sync container Left/Top back into the model
    /// and refresh the flow layer and canvas size.
    /// </summary>
    private void CommitAndRefresh()
    {
        SyncContainerPositionsToModel();
        _renderer.RedrawFlows(_currentModel!, DiagramCanvas, _nodeContainers);
        ResizeCanvasToFitContent();
    }

    private void SyncContainerPositionsToModel()
    {
        if (_currentModel == null) return;

        foreach (var (id, c) in _nodeContainers)
        {
            var node = _currentModel.GetNode(id);
            if (node == null) continue;
            node.X = Canvas.GetLeft(c) - _renderer.OffsetX;
            node.Y = Canvas.GetTop(c)  - _renderer.OffsetY;
        }
    }

    private void ResizeCanvasToFitContent()
    {
        if (_currentModel == null) return;

        var (minX, minY, maxX, maxY) = _currentModel.GetBounds();

        // Apply offset to get canvas coords
        double right  = _renderer.OffsetX + maxX + 30;
        double bottom = _renderer.OffsetY + maxY + 30;

        // Only grow, never shrink below current size (preserves scrollable area)
        DiagramCanvas.Width  = Math.Max(DiagramCanvas.Width,  right);
        DiagramCanvas.Height = Math.Max(DiagramCanvas.Height, bottom);

        _diagramSize = (DiagramCanvas.Width, DiagramCanvas.Height);
    }

    private static string ResolveSamplesPath()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string direct  = SysPath.Combine(baseDir, "Samples");
        if (Directory.Exists(direct)) return direct;

        var dir = new DirectoryInfo(baseDir);
        while (dir != null)
        {
            string candidate = SysPath.Combine(dir.FullName, "Samples");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return direct;
    }
}
