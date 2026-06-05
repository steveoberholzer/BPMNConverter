using System.IO;
using System.Windows;
using System.Windows.Input;
using DiagramApp.Models;
using DiagramApp.Services;
using Microsoft.Win32;

namespace DiagramApp;

public partial class MainWindow : Window
{
    private readonly BpmnParser    _parser   = new();
    private readonly BpmnRenderer  _renderer = new();
    private readonly DiagramExporter _exporter = new();

    private BpmnModel? _currentModel;
    private string?    _currentFile;
    private double     _zoom = 1.0;
    private (double W, double H) _diagramSize;

    private const string SamplesFolder = "Samples";

    public MainWindow()
    {
        InitializeComponent();
        LoadFileList();
    }

    // ── File list ─────────────────────────────────────────────────────────────

    private void LoadFileList()
    {
        string samplesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SamplesFolder);
        if (!Directory.Exists(samplesPath))
            samplesPath = Path.Combine(
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
                             .Select(Path.GetFileName)
                             .Where(f => f != null)
                             .Cast<string>()
                             .ToList();

        FileListBox.ItemsSource = files;
        StatusLeft.Text = $"Found {files.Count} BPMN file(s) in Samples folder.";
    }

    private void FileListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (FileListBox.SelectedItem is not string fileName) return;

        string samplesPath = ResolveSamplesPath();
        string fullPath    = Path.Combine(samplesPath, fileName);

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
        _currentFile = path;
        Mouse.OverrideCursor = Cursors.Wait;

        try
        {
            _currentModel = _parser.Parse(path);
            _diagramSize  = _renderer.Render(_currentModel, DiagramCanvas);

            BtnExportVsdx.IsEnabled = true;
            BtnExportPdf.IsEnabled  = true;
            BtnFit.IsEnabled        = true;

            int nodes  = _currentModel.Nodes.Count(n => n.Type is not BpmnNodeType.Participant and not BpmnNodeType.Lane);
            int flows  = _currentModel.Flows.Count;
            int pools  = _currentModel.Nodes.Count(n => n.Type == BpmnNodeType.Participant);

            StatusLeft.Text = $"{Path.GetFileName(path)}  —  {_currentModel.Name}";
            StatusRight.Text = $"{nodes} elements · {flows} flows" +
                               (pools > 0 ? $" · {pools} pool(s)" : "");

            // Auto-fit on first load
            FitDiagram();
        }
        catch (Exception ex)
        {
            StatusLeft.Text = $"Error loading {Path.GetFileName(path)}: {ex.Message}";
            MessageBox.Show($"Failed to load BPMN file:\n{ex.Message}", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
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
            Filter   = filter,
            FileName = Path.GetFileNameWithoutExtension(_currentFile ?? "diagram"),
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

    private void BtnFit_Click(object sender, RoutedEventArgs e) => FitDiagram();

    private void FitDiagram()
    {
        if (_diagramSize.W < 1 || _diagramSize.H < 1) return;

        DiagramScrollViewer.UpdateLayout();
        double vw = DiagramScrollViewer.ViewportWidth  - 20;
        double vh = DiagramScrollViewer.ViewportHeight - 20;
        if (vw < 1 || vh < 1) return;

        double scaleX = vw / _diagramSize.W;
        double scaleY = vh / _diagramSize.H;
        ApplyZoom(Math.Min(scaleX, scaleY));
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ResolveSamplesPath()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string direct  = Path.Combine(baseDir, "Samples");
        if (Directory.Exists(direct)) return direct;

        // Walk up to find Samples when running from bin/ subfolder
        var dir = new DirectoryInfo(baseDir);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "Samples");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return direct;
    }
}
