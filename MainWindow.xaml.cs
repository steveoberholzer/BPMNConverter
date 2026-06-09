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
    private readonly BpmnParser      _parser   = new();
    private readonly BpmnRenderer    _renderer = new();
    private readonly DiagramExporter _exporter = new();
    private readonly BpmnWriter      _writer   = new();

    private BpmnModel? _currentModel;
    private string?    _currentFile;
    private double     _zoom = 1.0;
    private (double W, double H) _diagramSize;

    private List<FileItem> _recentFiles = new();

    private const string SamplesFolder = "Samples";

    private record FileItem(string Name, string FullPath);

    // ── Edit modes ────────────────────────────────────────────────────────────

    private enum EditMode { Select, Place, Connect }
    private EditMode    _editMode        = EditMode.Select;
    private BpmnNodeType _placingType;
    private string?     _connectSourceId;                    // first click in Connect mode

    // Flow selection (separate from node selection)
    private readonly HashSet<string> _selectedFlowIds = new();

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
        string samplesPath = ResolveSamplesPath();
        if (!Directory.Exists(samplesPath))
        {
            StatusLeft.Text = $"Samples folder not found — use Open File to browse.";
            return;
        }

        _recentFiles = Directory.GetFiles(samplesPath, "*.bpmn")
                                .OrderBy(f => f)
                                .Select(p => new FileItem(SysPath.GetFileName(p), p))
                                .ToList();

        FileListBox.ItemsSource = _recentFiles;
        StatusLeft.Text = $"Found {_recentFiles.Count} BPMN file(s). Use Open File to browse for more.";
    }

    private void FileListBox_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (FileListBox.SelectedItem is not FileItem item) return;

        if (!File.Exists(item.FullPath))
        {
            StatusLeft.Text = $"File not found: {item.FullPath}";
            return;
        }

        LoadBpmnFile(item.FullPath);
    }

    private void BtnOpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Open BPMN File",
            Filter = "BPMN Files|*.bpmn|All Files|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        // Prepend to recent list, deduplicate by full path, keep most recent 30
        var newItem = new FileItem(SysPath.GetFileName(dlg.FileName), dlg.FileName);
        _recentFiles = _recentFiles
            .Where(f => !string.Equals(f.FullPath, dlg.FileName, StringComparison.OrdinalIgnoreCase))
            .Prepend(newItem)
            .Take(30)
            .ToList();

        FileListBox.ItemsSource = null;
        FileListBox.ItemsSource = _recentFiles;
        FileListBox.SelectedItem = _recentFiles[0];

        LoadBpmnFile(dlg.FileName);
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

            EnableAllTools();
            SetEditMode(EditMode.Select);

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
        DeselectAllFlows(notify: false);
        _nodeContainers.Clear();

        var (w, h, containers) = _renderer.Render(_currentModel, DiagramCanvas);
        _nodeContainers = containers;
        _diagramSize    = (w, h);

        AttachFlowHitEvents();
        UpdateEditToolbarState();
    }

    // ── Export ────────────────────────────────────────────────────────────────

    private void BtnExportVsdx_Click(object sender, RoutedEventArgs e) =>
        RunExport("Visio Files|*.vsdx", ".vsdx",
                  path => _exporter.ExportToVsdx(_currentModel!, path));

    private void BtnExportPdf_Click(object sender, RoutedEventArgs e) =>
        RunExport("PDF Files|*.pdf", ".pdf",
                  path => _exporter.ExportToPdf(_currentModel!, path));

    private void BtnExportBpmn_Click(object sender, RoutedEventArgs e)
    {
        if (_currentModel == null || _currentFile == null) return;

        var dlg = new SaveFileDialog
        {
            Title      = "Export Updated BPMN Layout",
            Filter     = "BPMN Files|*.bpmn|All Files|*.*",
            FileName   = SysPath.GetFileNameWithoutExtension(_currentFile) + "_updated.bpmn",
            DefaultExt = ".bpmn"
        };
        if (dlg.ShowDialog() != true) return;

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            SyncContainerPositionsToModel();
            if (_currentFile != null)
                _writer.Save(_currentModel, _currentFile, dlg.FileName);
            else
                _writer.Create(_currentModel, dlg.FileName);
            StatusLeft.Text = $"BPMN exported → {dlg.FileName}";
            MessageBox.Show($"Saved to:\n{dlg.FileName}", "BPMN Export Complete",
                            MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"BPMN export failed:\n{ex.Message}", "Export Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private void ChkBridges_Toggled(object sender, RoutedEventArgs e)
    {
        if (_currentModel == null) return;
        _renderer.ShowBridges = ChkBridges.IsChecked == true;
        RenderModel();
    }

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

        var pos  = e.GetPosition(DiagramCanvas);
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        switch (_editMode)
        {
            case EditMode.Place:
                PlaceNodeAt(pos);
                e.Handled = true;
                return;

            case EditMode.Connect:
            {
                var container = FindNodeContainer(e.OriginalSource as DependencyObject);
                if (container?.Tag is string nodeId)
                {
                    if (_connectSourceId == null)
                    {
                        _connectSourceId = nodeId;
                        SetConnectionHighlight(nodeId, true);
                        LblEditMode.Text = "Connect: now click the target node";
                    }
                    else if (nodeId != _connectSourceId)
                    {
                        CreateFlow(_connectSourceId, nodeId);
                        SetConnectionHighlight(_connectSourceId, false);
                        _connectSourceId = null;
                        SetEditMode(EditMode.Select);
                    }
                }
                else
                {
                    // Background click — cancel
                    if (_connectSourceId != null)
                        SetConnectionHighlight(_connectSourceId, false);
                    _connectSourceId = null;
                    SetEditMode(EditMode.Select);
                }
                e.Handled = true;
                return;
            }

            default: // Select
            {
                var container = FindNodeContainer(e.OriginalSource as DependencyObject);
                if (container?.Tag is string nodeId)
                {
                    if (ctrl)
                    {
                        DeselectAllFlows(notify: false);
                        ToggleSelection(nodeId);
                    }
                    else
                    {
                        if (!_selectedIds.Contains(nodeId))
                        {
                            DeselectAllFlows(notify: false);
                            DeselectAll(notify: false);
                            AddToSelection(nodeId);
                        }
                        BeginDrag(pos);
                    }
                }
                else
                {
                    DeselectAllFlows();
                    DeselectAll();
                }
                e.Handled = true;
                return;
            }
        }
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

        SyncContainerPositionsToModel();
        _renderer.RedrawFlows(_currentModel!, DiagramCanvas, _nodeContainers);
        AttachFlowHitEvents();
        foreach (var fid in _selectedFlowIds) _renderer.SetFlowSelected(fid, true);
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
        int n  = _selectedIds.Count;
        int nf = _selectedFlowIds.Count;

        LblSelection.Text = (n, nf) switch
        {
            (0, 0) => "No selection",
            (1, 0) => "1 node selected",
            (0, 1) => "1 flow selected",
            _ when nf == 0 => $"{n} nodes selected",
            _ when n  == 0 => $"{nf} flows selected",
            _              => $"{n} nodes, {nf} flows selected"
        };

        bool two = n >= 2;
        BtnAlignLeft.IsEnabled  = two;
        BtnAlignCtrH.IsEnabled  = two;
        BtnAlignRight.IsEnabled = two;
        BtnAlignTop.IsEnabled   = two;
        BtnAlignMidV.IsEnabled  = two;
        BtnAlignBot.IsEnabled   = two;
        BtnDistribH.IsEnabled   = n >= 3;
        BtnDistribV.IsEnabled   = n >= 3;

        BtnConnectSelected.IsEnabled = n == 2 && nf == 0;
        BtnDeleteSelected.IsEnabled  = n > 0 || nf > 0;
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
        catch (Exception ex)
        {
            MessageBox.Show($"Layout failed:\n{ex.Message}", "Layout Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    // ── New document ──────────────────────────────────────────────────────────

    private void BtnNew_Click(object sender, RoutedEventArgs e)
    {
        var model = new BpmnModel { Name = "New Process" };

        var start = MakeNode(BpmnNodeType.StartEvent,  "Start",  80,  182);
        var task  = MakeNode(BpmnNodeType.Task,        "Task",  200,  160);
        var end   = MakeNode(BpmnNodeType.EndEvent,    "End",   380,  182);
        model.Nodes.AddRange([start, task, end]);
        model.Flows.Add(new BpmnFlow { Id = "Flow_1",
            SourceRef = start.Id, TargetRef = task.Id,
            Waypoints = LayoutEngine.ComputeElbow(start, task) });
        model.Flows.Add(new BpmnFlow { Id = "Flow_2",
            SourceRef = task.Id,  TargetRef = end.Id,
            Waypoints = LayoutEngine.ComputeElbow(task, end) });

        _currentModel = model;
        _currentFile  = null;

        EnableAllTools();
        RenderModel();
        FitDiagram();
        SetEditMode(EditMode.Select);
        StatusLeft.Text  = "New BPMN process — add shapes from the palette, connect them, then export.";
        StatusRight.Text = "3 elements · 2 flows · Unsaved";
    }

    // ── Edit mode management ──────────────────────────────────────────────────

    private void SetEditMode(EditMode mode, BpmnNodeType placing = default)
    {
        _editMode    = mode;
        _placingType = placing;

        if (mode != EditMode.Connect && _connectSourceId != null)
        {
            SetConnectionHighlight(_connectSourceId, false);
            _connectSourceId = null;
        }

        DiagramCanvas.Cursor = mode is EditMode.Place or EditMode.Connect
            ? Cursors.Cross : null;

        LblEditMode.Text = mode switch
        {
            EditMode.Place   => $"Click canvas to place {TypeLabel(placing)}  (Esc = cancel)",
            EditMode.Connect => "Click source node, then target node  (Esc = cancel)",
            _                => "Select mode"
        };

        // Visual active-state on mode buttons
        var active   = new SolidColorBrush(Color.FromRgb(187, 222, 251));
        var inactive = SystemColors.ControlBrush;
        BtnModeSelect.Background  = mode == EditMode.Select  ? active : inactive;
        BtnModeConnect.Background = mode == EditMode.Connect ? active : inactive;
    }

    private void BtnModeSelect_Click(object sender, RoutedEventArgs e)  => SetEditMode(EditMode.Select);
    private void BtnModeConnect_Click(object sender, RoutedEventArgs e) => SetEditMode(EditMode.Connect);

    private void BtnPlaceShape_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (!Enum.TryParse<BpmnNodeType>(btn.Tag as string ?? "", out var type)) return;
        SetEditMode(EditMode.Place, type);
    }

    // ── Shape placement ───────────────────────────────────────────────────────

    private void PlaceNodeAt(Point canvasPos)
    {
        if (_currentModel == null) return;

        var (w, h) = ShapeSize(_placingType);
        double mx = canvasPos.X - _renderer.OffsetX - w / 2;
        double my = canvasPos.Y - _renderer.OffsetY - h / 2;
        var node = MakeNode(_placingType, TypeLabel(_placingType), mx, my);
        _currentModel.Nodes.Add(node);

        RenderModel();
        StatusLeft.Text = $"Placed {TypeLabel(_placingType)} — click again to place another, or press Esc.";
    }

    // ── Flow creation / connection ────────────────────────────────────────────

    private void CreateFlow(string sourceId, string targetId)
    {
        if (_currentModel == null) return;
        var src = _currentModel.GetNode(sourceId);
        var tgt = _currentModel.GetNode(targetId);
        if (src == null || tgt == null) return;

        var flow = new BpmnFlow
        {
            Id        = $"Flow_{Guid.NewGuid():N}"[..13],
            SourceRef = sourceId,
            TargetRef = targetId,
            Waypoints = LayoutEngine.ComputeElbow(src, tgt)
        };
        _currentModel.Flows.Add(flow);
        RenderModel();
        StatusLeft.Text = $"Flow created: {src.Name} → {tgt.Name}";
    }

    private void BtnConnectSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedIds.Count != 2 || _currentModel == null) return;
        // Connect left-to-right (by X position)
        var ordered = _selectedIds
            .Select(id => _currentModel.GetNode(id)!)
            .Where(n => n != null)
            .OrderBy(n => n.X)
            .ToList();
        if (ordered.Count == 2) CreateFlow(ordered[0].Id, ordered[1].Id);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    private void BtnDeleteSelected_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    private void DeleteSelected()
    {
        if (_currentModel == null) return;
        bool changed = false;

        foreach (var fid in _selectedFlowIds.ToList())
        {
            var f = _currentModel.GetFlow(fid);
            if (f == null) continue;
            _currentModel.Flows.Remove(f);
            changed = true;
        }
        _selectedFlowIds.Clear();

        foreach (var nid in _selectedIds.ToList())
        {
            var n = _currentModel.GetNode(nid);
            if (n == null) continue;
            // Remove connected flows first
            foreach (var f in _currentModel.Flows
                .Where(f => f.SourceRef == nid || f.TargetRef == nid).ToList())
                _currentModel.Flows.Remove(f);
            _currentModel.Nodes.Remove(n);
            changed = true;
        }
        _selectedIds.Clear();

        if (changed) RenderModel();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SetEditMode(EditMode.Select);
            e.Handled = true;
            return;
        }
        if ((e.Key == Key.Delete || e.Key == Key.Back)
            && Keyboard.FocusedElement is not TextBox)
        {
            DeleteSelected();
            e.Handled = true;
        }
    }

    // ── Flow selection ────────────────────────────────────────────────────────

    private void AttachFlowHitEvents()
    {
        foreach (var (flowId, hitTarget) in _renderer.FlowHitTargets)
        {
            var id = flowId;
            hitTarget.MouseLeftButtonDown += (_, e) =>
            {
                if (_editMode != EditMode.Select) return;
                bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                if (ctrl)
                {
                    DeselectAll(notify: false);
                    if (_selectedFlowIds.Contains(id)) { _selectedFlowIds.Remove(id); _renderer.SetFlowSelected(id, false); }
                    else                               { _selectedFlowIds.Add(id);    _renderer.SetFlowSelected(id, true);  }
                }
                else
                {
                    DeselectAll(notify: false);
                    DeselectAllFlows(notify: false);
                    _selectedFlowIds.Add(id);
                    _renderer.SetFlowSelected(id, true);
                }
                UpdateEditToolbarState();
                e.Handled = true;
            };
        }
    }

    private void DeselectAllFlows(bool notify = true)
    {
        foreach (var id in _selectedFlowIds)
            _renderer.SetFlowSelected(id, false);
        _selectedFlowIds.Clear();
        if (notify) UpdateEditToolbarState();
    }

    // ── Connection highlight (orange overlay when in Connect mode) ────────────

    private void SetConnectionHighlight(string nodeId, bool on)
    {
        if (!_nodeContainers.TryGetValue(nodeId, out var c)) return;
        var overlay = c.Children.OfType<Rectangle>()
                       .FirstOrDefault(r => r.Tag is "sel-overlay");
        if (overlay == null) return;
        overlay.Stroke     = on ? new SolidColorBrush(Color.FromRgb(230, 81, 0))
                                : new SolidColorBrush(Color.FromRgb(25, 118, 210));
        overlay.Fill       = on ? new SolidColorBrush(Color.FromArgb(30, 230, 81, 0))
                                : new SolidColorBrush(Color.FromArgb(20, 25, 118, 210));
        overlay.Visibility = Visibility.Visible;
    }

    // ── Shape helpers ─────────────────────────────────────────────────────────

    private static BpmnNode MakeNode(BpmnNodeType type, string name, double x, double y)
    {
        var (w, h) = ShapeSize(type);
        return new BpmnNode
        {
            Id     = $"{type}_{Guid.NewGuid():N}"[..(type.ToString().Length + 9)],
            Name   = name,
            Type   = type,
            X      = x, Y = y,
            Width  = w, Height = h
        };
    }

    private static (double W, double H) ShapeSize(BpmnNodeType type) => type switch
    {
        BpmnNodeType.StartEvent or BpmnNodeType.EndEvent or
        BpmnNodeType.IntermediateCatchEvent or BpmnNodeType.IntermediateThrowEvent => (36, 36),
        BpmnNodeType.ExclusiveGateway or BpmnNodeType.ParallelGateway or
        BpmnNodeType.InclusiveGateway or BpmnNodeType.EventBasedGateway or
        BpmnNodeType.ComplexGateway => (50, 50),
        BpmnNodeType.SubProcess => (150, 100),
        _ => (100, 80)
    };

    private static string TypeLabel(BpmnNodeType type) => type switch
    {
        BpmnNodeType.StartEvent             => "Start Event",
        BpmnNodeType.EndEvent               => "End Event",
        BpmnNodeType.IntermediateCatchEvent => "Intermediate Event",
        BpmnNodeType.Task                   => "Task",
        BpmnNodeType.UserTask               => "User Task",
        BpmnNodeType.ServiceTask            => "Service Task",
        BpmnNodeType.ScriptTask             => "Script Task",
        BpmnNodeType.ExclusiveGateway       => "Exclusive Gateway",
        BpmnNodeType.ParallelGateway        => "Parallel Gateway",
        BpmnNodeType.SubProcess             => "Sub-Process",
        _                                   => type.ToString()
    };

    private void EnableAllTools()
    {
        BtnExportVsdx.IsEnabled = true;
        BtnExportPdf.IsEnabled  = true;
        BtnExportBpmn.IsEnabled = true;
        BtnFit.IsEnabled        = true;
        BtnAutoLayout.IsEnabled = true;
        CboLayout.IsEnabled     = true;
        ChkBridges.IsEnabled    = true;
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
        AttachFlowHitEvents();
        foreach (var fid in _selectedFlowIds) _renderer.SetFlowSelected(fid, true);
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
