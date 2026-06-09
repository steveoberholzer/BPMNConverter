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
    private EditMode     _editMode        = EditMode.Select;
    private BpmnNodeType _placingType;
    private string?      _connectSourceId;

    private readonly HashSet<string> _selectedFlowIds = new();

    // ── Edit state ────────────────────────────────────────────────────────────

    private Dictionary<string, Canvas> _nodeContainers = new();
    private readonly HashSet<string>   _selectedIds    = new();

    // Drag
    private bool   _isDragging;
    private Point  _dragAnchor;
    private Dictionary<string, (double L, double T)> _dragOrigins = new();

    // Snap guides
    private readonly List<Line> _snapGuides = new();

    // Property panel state
    private string? _propsNodeId;
    private string? _propsFlowId;
    private bool    _applyingProperty;

    public MainWindow()
    {
        InitializeComponent();
        DiagramCanvas.MouseLeftButtonDown += Canvas_MouseLeftButtonDown;
        DiagramCanvas.MouseMove           += Canvas_MouseMove;
        DiagramCanvas.MouseLeftButtonUp   += Canvas_MouseLeftButtonUp;
        LoadFileList();
    }

    // ── File list / Recent files ──────────────────────────────────────────────

    private void LoadFileList()
    {
        string samplesPath = ResolveSamplesPath();
        if (!Directory.Exists(samplesPath))
        {
            StatusLeft.Text = "Use File → Open to browse for a .bpmn file.";
            return;
        }

        _recentFiles = Directory.GetFiles(samplesPath, "*.bpmn")
                                .OrderBy(f => f)
                                .Select(p => new FileItem(SysPath.GetFileName(p), p))
                                .ToList();

        RefreshRecentFilesMenu();
        StatusLeft.Text = _recentFiles.Count > 0
            ? $"Found {_recentFiles.Count} sample file(s) — use File → Recent Files or File → Open."
            : "Use File → New or File → Open to get started.";
    }

    private void RefreshRecentFilesMenu()
    {
        MenuRecentFiles.Items.Clear();
        if (_recentFiles.Count == 0)
        {
            MenuRecentFiles.IsEnabled = false;
            return;
        }
        MenuRecentFiles.IsEnabled = true;
        foreach (var item in _recentFiles)
        {
            var mi      = new MenuItem { Header = item.Name, ToolTip = item.FullPath };
            var capture = item;
            mi.Click   += (_, _) => LoadBpmnFile(capture.FullPath);
            MenuRecentFiles.Items.Add(mi);
        }
    }

    private void BtnOpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Open BPMN File",
            Filter = "BPMN Files|*.bpmn|All Files|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        var newItem = new FileItem(SysPath.GetFileName(dlg.FileName), dlg.FileName);
        _recentFiles = _recentFiles
            .Where(f => !string.Equals(f.FullPath, dlg.FileName, StringComparison.OrdinalIgnoreCase))
            .Prepend(newItem)
            .Take(30)
            .ToList();

        RefreshRecentFilesMenu();
        LoadBpmnFile(dlg.FileName);
    }

    private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();

    // ── BPMN loading & rendering ──────────────────────────────────────────────

    private void LoadBpmnFile(string path)
    {
        _currentFile         = path;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            _currentModel = _parser.Parse(path);
            RenderModel();

            int nodes = _currentModel.Nodes.Count(n =>
                n.Type is not BpmnNodeType.Participant and not BpmnNodeType.Lane);
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

    private void ReRenderPreservingSelection()
    {
        var nodeIds = _selectedIds.ToHashSet();
        var flowIds = _selectedFlowIds.ToHashSet();

        RenderModel();

        foreach (var id in nodeIds.Where(_nodeContainers.ContainsKey))
            AddToSelection(id);

        foreach (var id in flowIds)
        {
            _selectedFlowIds.Add(id);
            _renderer.SetFlowSelected(id, true);
        }

        UpdateEditToolbarState();
        UpdatePropertiesPanel();
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
        if (_currentModel == null) return;

        var dlg = new SaveFileDialog
        {
            Title      = "Save As BPMN",
            Filter     = "BPMN Files|*.bpmn|All Files|*.*",
            FileName   = _currentFile != null
                ? SysPath.GetFileNameWithoutExtension(_currentFile) + "_updated.bpmn"
                : "diagram.bpmn",
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

            StatusLeft.Text = $"Saved → {dlg.FileName}";
            MessageBox.Show($"Saved to:\n{dlg.FileName}", "BPMN Saved",
                            MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Save failed:\n{ex.Message}", "Save Error",
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

    // ── Canvas mouse events ───────────────────────────────────────────────────

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_currentModel == null) return;

        var  pos  = e.GetPosition(DiagramCanvas);
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

        var    pos = e.GetPosition(DiagramCanvas);
        double dx  = pos.X - _dragAnchor.X;
        double dy  = pos.Y - _dragAnchor.Y;

        // Single-node drag: check snap
        if (_selectedIds.Count == 1)
        {
            var dragId = _selectedIds.First();
            if (_nodeContainers.TryGetValue(dragId, out var dragC) &&
                _dragOrigins.TryGetValue(dragId, out var orig))
                (dx, dy) = ApplySnap(dragC, orig.L, orig.T, dx, dy, dragId);
        }
        else
        {
            ClearSnapGuides();
        }

        foreach (var id in _selectedIds)
        {
            if (!_nodeContainers.TryGetValue(id, out var c)) continue;
            if (!_dragOrigins.TryGetValue(id, out var orig)) continue;
            Canvas.SetLeft(c, orig.L + dx);
            Canvas.SetTop(c,  orig.T + dy);
        }

        _renderer.RedrawFlows(_currentModel!, DiagramCanvas, _nodeContainers);
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;

        ClearSnapGuides();
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

    // ── Snap-to alignment ─────────────────────────────────────────────────────

    private (double dx, double dy) ApplySnap(Canvas dragC,
        double origL, double origT, double dx, double dy, string dragId)
    {
        const double Threshold = 8.0;
        ClearSnapGuides();

        double newL  = origL + dx;
        double newR  = newL  + dragC.Width;
        double newCX = newL  + dragC.Width  / 2;
        double newT  = origT + dy;
        double newB  = newT  + dragC.Height;
        double newCY = newT  + dragC.Height / 2;

        double? snapL = null, snapT = null;
        double? guideX = null, guideY = null;

        foreach (var (id, c) in _nodeContainers)
        {
            if (id == dragId) continue;
            double cL  = Canvas.GetLeft(c);
            double cT  = Canvas.GetTop(c);
            double cR  = cL + c.Width;
            double cCX = cL + c.Width  / 2;
            double cB  = cT + c.Height;
            double cCY = cT + c.Height / 2;

            if (snapL == null)
            {
                if      (Math.Abs(newL  - cL)  < Threshold) { snapL = cL;                   guideX = cL;  }
                else if (Math.Abs(newL  - cR)  < Threshold) { snapL = cR;                   guideX = cR;  }
                else if (Math.Abs(newCX - cCX) < Threshold) { snapL = cCX - dragC.Width/2;  guideX = cCX; }
                else if (Math.Abs(newR  - cR)  < Threshold) { snapL = cR  - dragC.Width;    guideX = cR;  }
                else if (Math.Abs(newR  - cL)  < Threshold) { snapL = cL  - dragC.Width;    guideX = cL;  }
            }

            if (snapT == null)
            {
                if      (Math.Abs(newT  - cT)  < Threshold) { snapT = cT;                   guideY = cT;  }
                else if (Math.Abs(newT  - cB)  < Threshold) { snapT = cB;                   guideY = cB;  }
                else if (Math.Abs(newCY - cCY) < Threshold) { snapT = cCY - dragC.Height/2; guideY = cCY; }
                else if (Math.Abs(newB  - cB)  < Threshold) { snapT = cB  - dragC.Height;   guideY = cB;  }
                else if (Math.Abs(newB  - cT)  < Threshold) { snapT = cT  - dragC.Height;   guideY = cT;  }
            }

            if (snapL != null && snapT != null) break;
        }

        if (guideX.HasValue) AddSnapGuide(vertical: true,  position: guideX.Value);
        if (guideY.HasValue) AddSnapGuide(vertical: false, position: guideY.Value);

        return (snapL.HasValue ? snapL.Value - origL : dx,
                snapT.HasValue ? snapT.Value - origT : dy);
    }

    private void ClearSnapGuides()
    {
        foreach (var g in _snapGuides) DiagramCanvas.Children.Remove(g);
        _snapGuides.Clear();
    }

    private void AddSnapGuide(bool vertical, double position)
    {
        var g = new Line
        {
            Stroke           = new SolidColorBrush(Color.FromRgb(25, 118, 210)),
            StrokeThickness  = 0.75,
            StrokeDashArray  = new DoubleCollection([6, 4]),
            Opacity          = 0.85,
            IsHitTestVisible = false
        };
        if (vertical) { g.X1 = position; g.Y1 = 0;    g.X2 = position; g.Y2 = 5000; }
        else          { g.X1 = 0;        g.Y1 = position; g.X2 = 5000; g.Y2 = position; }
        Canvas.SetZIndex(g, 99);
        DiagramCanvas.Children.Add(g);
        _snapGuides.Add(g);
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

        UpdatePropertiesPanel();
    }

    // ── Properties panel ──────────────────────────────────────────────────────

    private void UpdatePropertiesPanel()
    {
        if (_currentModel == null)
        {
            PropertiesPanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (_selectedIds.Count == 1)
        {
            var node = _currentModel.GetNode(_selectedIds.First());
            if (node == null) { PropertiesPanel.Visibility = Visibility.Collapsed; return; }

            PropertiesPanel.Visibility = Visibility.Visible;
            PropTitle.Text  = "Node Properties";
            _propsNodeId    = node.Id;
            _propsFlowId    = null;

            var (badgeColor, typeText) = NodeBadge(node.Type);
            PropTypeBadge.Background   = new SolidColorBrush(badgeColor);
            PropType.Text              = typeText;

            PropName.Text = node.Name;
            PropX.Text    = node.X.ToString("F0");
            PropY.Text    = node.Y.ToString("F0");
            PropW.Text    = node.Width.ToString("F0");
            PropH.Text    = node.Height.ToString("F0");

            NodeOnlyProps.Visibility = Visibility.Visible;
            FlowOnlyProps.Visibility = Visibility.Collapsed;
        }
        else if (_selectedFlowIds.Count == 1)
        {
            var flow = _currentModel.GetFlow(_selectedFlowIds.First());
            if (flow == null) { PropertiesPanel.Visibility = Visibility.Collapsed; return; }

            PropertiesPanel.Visibility = Visibility.Visible;
            PropTitle.Text  = "Flow Properties";
            _propsNodeId    = null;
            _propsFlowId    = flow.Id;

            PropTypeBadge.Background = new SolidColorBrush(Color.FromRgb(80, 80, 80));
            PropType.Text            = "Sequence Flow";
            PropName.Text            = flow.Name;

            PropFlowFrom.Text = _currentModel.GetNode(flow.SourceRef)?.Name ?? flow.SourceRef;
            PropFlowTo.Text   = _currentModel.GetNode(flow.TargetRef)?.Name ?? flow.TargetRef;

            NodeOnlyProps.Visibility = Visibility.Collapsed;
            FlowOnlyProps.Visibility = Visibility.Visible;
        }
        else
        {
            PropertiesPanel.Visibility = Visibility.Collapsed;
            _propsNodeId = _propsFlowId = null;
        }
    }

    private static (Color color, string label) NodeBadge(BpmnNodeType type) => type switch
    {
        BpmnNodeType.StartEvent or BpmnNodeType.EndEvent or
        BpmnNodeType.IntermediateCatchEvent or BpmnNodeType.IntermediateThrowEvent =>
            (Color.FromRgb(46, 125, 50),  "Event"),
        BpmnNodeType.ExclusiveGateway or BpmnNodeType.ParallelGateway or
        BpmnNodeType.InclusiveGateway or BpmnNodeType.EventBasedGateway =>
            (Color.FromRgb(245, 127, 23), "Gateway"),
        BpmnNodeType.SubProcess   => (Color.FromRgb(100, 100, 100), "Sub-Process"),
        BpmnNodeType.CallActivity => (Color.FromRgb(74,  20,  140), "Call Activity"),
        BpmnNodeType.UserTask     => (Color.FromRgb(46,  125, 50),  "User Task"),
        BpmnNodeType.ServiceTask  => (Color.FromRgb(21,  101, 192), "Service Task"),
        BpmnNodeType.SendTask     => (Color.FromRgb(230, 81,  0),   "Send Task"),
        BpmnNodeType.ReceiveTask  => (Color.FromRgb(1,   87,  155), "Receive Task"),
        BpmnNodeType.ScriptTask   => (Color.FromRgb(130, 119, 23),  "Script Task"),
        BpmnNodeType.ManualTask   => (Color.FromRgb(63,  81,  181), "Manual Task"),
        _                         => (Color.FromRgb(90,  90,  90),  "Task")
    };

    private void BtnCloseProps_Click(object sender, RoutedEventArgs e)
    {
        DeselectAll();
        DeselectAllFlows();
        PropertiesPanel.Visibility = Visibility.Collapsed;
    }

    private void Prop_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) ApplyPropertyChange(tb);
    }

    private void Prop_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (e.Key == Key.Enter)  { ApplyPropertyChange(tb); e.Handled = true; }
        else if (e.Key == Key.Escape) { UpdatePropertiesPanel(); e.Handled = true; }
    }

    private void ApplyPropertyChange(TextBox tb)
    {
        if (_applyingProperty || _currentModel == null) return;
        _applyingProperty = true;
        try
        {
            string tag = tb.Tag as string ?? "";

            if (_propsNodeId != null)
            {
                var node = _currentModel.GetNode(_propsNodeId);
                if (node == null) return;
                bool posChanged = false;

                switch (tag)
                {
                    case "Name": node.Name = tb.Text; break;
                    case "X":
                        if (double.TryParse(tb.Text, out double x)) { node.X = x; posChanged = true; }
                        break;
                    case "Y":
                        if (double.TryParse(tb.Text, out double y)) { node.Y = y; posChanged = true; }
                        break;
                    case "W":
                        if (double.TryParse(tb.Text, out double w) && w > 0) { node.Width  = w; posChanged = true; }
                        break;
                    case "H":
                        if (double.TryParse(tb.Text, out double h) && h > 0) { node.Height = h; posChanged = true; }
                        break;
                }

                if (posChanged)
                {
                    var map = _currentModel.Nodes.ToDictionary(n => n.Id);
                    foreach (var flow in _currentModel.Flows
                        .Where(f => f.SourceRef == _propsNodeId || f.TargetRef == _propsNodeId))
                    {
                        if (map.TryGetValue(flow.SourceRef, out var src) &&
                            map.TryGetValue(flow.TargetRef, out var tgt))
                            flow.Waypoints = LayoutEngine.ComputeElbow(src, tgt);
                    }
                }

                ReRenderPreservingSelection();
            }
            else if (_propsFlowId != null)
            {
                var flow = _currentModel.GetFlow(_propsFlowId);
                if (flow != null && tag == "Name")
                {
                    flow.Name = tb.Text;
                    ReRenderPreservingSelection();
                }
            }
        }
        finally
        {
            _applyingProperty = false;
        }
    }

    // ── Align operations ──────────────────────────────────────────────────────

    private void BtnAlignLeft_Click(object sender, RoutedEventArgs e)
    {
        double target = SelectedContainers().Min(p => Canvas.GetLeft(p.C));
        foreach (var (_, c) in SelectedContainers()) Canvas.SetLeft(c, target);
        CommitAndRefresh();
    }

    private void BtnAlignCtrH_Click(object sender, RoutedEventArgs e)
    {
        double target = SelectedContainers().Average(p => Canvas.GetLeft(p.C) + p.C.Width / 2);
        foreach (var (_, c) in SelectedContainers()) Canvas.SetLeft(c, target - c.Width / 2);
        CommitAndRefresh();
    }

    private void BtnAlignRight_Click(object sender, RoutedEventArgs e)
    {
        double target = SelectedContainers().Max(p => Canvas.GetLeft(p.C) + p.C.Width);
        foreach (var (_, c) in SelectedContainers()) Canvas.SetLeft(c, target - c.Width);
        CommitAndRefresh();
    }

    private void BtnAlignTop_Click(object sender, RoutedEventArgs e)
    {
        double target = SelectedContainers().Min(p => Canvas.GetTop(p.C));
        foreach (var (_, c) in SelectedContainers()) Canvas.SetTop(c, target);
        CommitAndRefresh();
    }

    private void BtnAlignMidV_Click(object sender, RoutedEventArgs e)
    {
        double target = SelectedContainers().Average(p => Canvas.GetTop(p.C) + p.C.Height / 2);
        foreach (var (_, c) in SelectedContainers()) Canvas.SetTop(c, target - c.Height / 2);
        CommitAndRefresh();
    }

    private void BtnAlignBot_Click(object sender, RoutedEventArgs e)
    {
        double target = SelectedContainers().Max(p => Canvas.GetTop(p.C) + p.C.Height);
        foreach (var (_, c) in SelectedContainers()) Canvas.SetTop(c, target - c.Height);
        CommitAndRefresh();
    }

    private void BtnDistribH_Click(object sender, RoutedEventArgs e)
    {
        var sorted = SelectedContainers()
            .OrderBy(p => Canvas.GetLeft(p.C) + p.C.Width / 2).ToList();
        if (sorted.Count < 3) return;
        double left  = Canvas.GetLeft(sorted[0].C);
        double right = Canvas.GetLeft(sorted[^1].C) + sorted[^1].C.Width;
        double gap   = (right - left - sorted.Sum(p => p.C.Width)) / (sorted.Count - 1);
        double x     = left;
        foreach (var (_, c) in sorted) { Canvas.SetLeft(c, x); x += c.Width + gap; }
        CommitAndRefresh();
    }

    private void BtnDistribV_Click(object sender, RoutedEventArgs e)
    {
        var sorted = SelectedContainers()
            .OrderBy(p => Canvas.GetTop(p.C) + p.C.Height / 2).ToList();
        if (sorted.Count < 3) return;
        double top    = Canvas.GetTop(sorted[0].C);
        double bottom = Canvas.GetTop(sorted[^1].C) + sorted[^1].C.Height;
        double gap    = (bottom - top - sorted.Sum(p => p.C.Height)) / (sorted.Count - 1);
        double y      = top;
        foreach (var (_, c) in sorted) { Canvas.SetTop(c, y); y += c.Height + gap; }
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
        finally { Mouse.OverrideCursor = null; }
    }

    // ── New document ──────────────────────────────────────────────────────────

    private void BtnNew_Click(object sender, RoutedEventArgs e)
    {
        var model = new BpmnModel { Name = "New Process" };
        var start = MakeNode(BpmnNodeType.StartEvent, "Start",  80, 182);
        var task  = MakeNode(BpmnNodeType.Task,       "Task",  200, 160);
        var end   = MakeNode(BpmnNodeType.EndEvent,   "End",   380, 182);
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
        StatusLeft.Text  = "New BPMN process — add shapes from the palette, connect them, then save.";
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
        double mx  = canvasPos.X - _renderer.OffsetX - w / 2;
        double my  = canvasPos.Y - _renderer.OffsetY - h / 2;
        var node   = MakeNode(_placingType, TypeLabel(_placingType), mx, my);
        _currentModel.Nodes.Add(node);
        RenderModel();
        StatusLeft.Text = $"Placed {TypeLabel(_placingType)} — click again to place another, or press Esc.";
    }

    // ── Flow creation ─────────────────────────────────────────────────────────

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

    // ── Connection highlight ──────────────────────────────────────────────────

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
            X = x, Y = y,
            Width = w, Height = h
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
        BtnExportVsdx.IsEnabled  = true;
        BtnExportPdf.IsEnabled   = true;
        BtnExportBpmn.IsEnabled  = true;
        BtnFit.IsEnabled         = true;
        BtnAutoLayout.IsEnabled  = true;
        CboLayout.IsEnabled      = true;
        ChkBridges.IsEnabled     = true;
        MenuSaveBpmn.IsEnabled   = true;
        MenuExportVsdx.IsEnabled = true;
        MenuExportPdf.IsEnabled  = true;
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private IEnumerable<(string Id, Canvas C)> SelectedContainers() =>
        _selectedIds
            .Where(id => _nodeContainers.ContainsKey(id))
            .Select(id => (id, _nodeContainers[id]));

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
        var (_, _, maxX, maxY) = _currentModel.GetBounds();
        double right  = _renderer.OffsetX + maxX + 30;
        double bottom = _renderer.OffsetY + maxY + 30;
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
