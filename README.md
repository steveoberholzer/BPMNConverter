# BPMN Diagram Viewer

A WPF application that renders BPMN 2.0 workflow diagrams from `.bpmn` files and exports them to Visio-compatible (`.vsdx`) and PDF formats via **[Aspose.Diagram](https://products.aspose.com/diagram/net/)**.

---

## The Problem

Aspose.Diagram does not natively support the BPMN 2.0 XML format (`.bpmn`).  
This project bridges that gap by parsing the BPMN XML directly and feeding the result into Aspose.Diagram — giving you proper Visio-compatible output from standard BPMN process files.

---

## Features

- **Reads any BPMN 2.0 file** — parses both process elements and the embedded `bpmndi:BPMNDiagram` layout section (positions, sizes, connector waypoints)
- **Live diagram preview** — rendered directly on a WPF Canvas using native shapes:
  - Circles for start / end / intermediate events
  - Diamonds for gateways (with X / + / ○ markers for exclusive / parallel / inclusive)
  - Rounded rectangles for tasks (colour-coded by type: service, user, send, call activity, etc.)
  - Polylines with arrowheads for sequence flows
  - Pool / participant containers with labels
- **Export via Aspose.Diagram**
  - `.vsdx` — fully editable Visio diagram with correct shape geometry (ellipses, diamonds, polyline connectors)
  - `.pdf` — print-ready PDF
- Ctrl + scroll to zoom; **Fit** button to auto-fit the diagram to the viewport
- Sidebar file browser — lists all `.bpmn` files found in the `Samples/` subfolder

---

## Screenshot

> _Select a BPMN file from the left panel to render its workflow. Use the toolbar to export._

![App layout: dark sidebar with file list on the left, workflow diagram canvas on the right, toolbar with Export buttons at the top](docs/screenshot-placeholder.png)

---

## Getting Started

### Prerequisites

| Requirement | Version |
|---|---|
| .NET SDK | 10.0+ |
| Windows | 10 / 11 / Server 2016+ |
| Aspose.Diagram for .NET | 25.4.0 (restored automatically via NuGet) |

### Build & Run

```bash
git clone https://github.com/steveoberholzer/BPMNConverter.git
cd BPMNConverter
dotnet run
```

Or open `DiagramApp.csproj` in Visual Studio 2022+ and press **F5**.

### Add Your Own BPMN Files

Drop any `.bpmn` file into the `Samples/` folder — the app lists all files there automatically on startup.

---

## Project Structure

```
BPMNConverter/
├── Samples/                   BPMN 2.0 sample workflow files
│   ├── GeneralNotification.Workflow.SendNotification.bpmn
│   ├── IQumulate.Broker.QuoteProcessing.bpmn
│   └── ...
│
├── Models/
│   └── BpmnModel.cs           Data model — BpmnNode, BpmnFlow, BpmnModel
│
├── Services/
│   ├── BpmnParser.cs          Parses BPMN XML (process elements + bpmndi layout)
│   ├── BpmnRenderer.cs        Renders the model onto a WPF Canvas
│   └── DiagramExporter.cs     Builds an Aspose.Diagram document and saves VSDX / PDF
│
├── MainWindow.xaml/.cs        Main WPF window — file list, diagram view, toolbar
├── App.xaml/.cs               Application entry point
└── DiagramApp.csproj          .NET 10 WPF project (net10.0-windows)
```

---

## How It Works

```
.bpmn file
    │
    ▼
BpmnParser          — reads bpmn:process elements and bpmndi:BPMNDiagram layout
    │
    ▼
BpmnModel           — typed model: nodes (with pixel coords) + flows (with waypoints)
    │
    ├──► BpmnRenderer     → WPF Canvas (live preview)
    │
    └──► DiagramExporter  → Aspose.Diagram → .vsdx / .pdf
                              • Ellipse geometry  for events
                              • Diamond geometry  for gateways
                              • Polyline geometry for connectors
                              • Color-coded fills by node type
```

### BPMN → Aspose.Diagram mapping

| BPMN element | Aspose.Diagram shape | Fill |
|---|---|---|
| `startEvent` | Ellipse geometry | Light green |
| `endEvent` | Ellipse geometry | Light red |
| `intermediateCatchEvent` | Ellipse geometry | Light blue |
| `serviceTask` | Rectangle | Light blue |
| `userTask` | Rectangle | Light green |
| `sendTask` | Rectangle | Light orange |
| `callActivity` | Rectangle (thick border) | Light purple |
| `exclusiveGateway` | Diamond geometry | Light yellow |
| `parallelGateway` | Diamond geometry | Light green |
| `inclusiveGateway` | Diamond geometry | Light orange |
| `sequenceFlow` | Polyline (MoveTo + LineTo) | Transparent |
| `participant` | Large rectangle | Light grey-blue |

---

## Aspose.Diagram Notes

The project uses **Aspose.Diagram 25.4.0** in evaluation mode (watermark on exports).  
To remove the watermark, apply a licence before saving:

```csharp
var license = new Aspose.Diagram.License();
license.SetLicense("Aspose.Diagram.lic");
```

Key API patterns discovered while building this integration:

```csharp
// Correct enum values
diagram.Save(path, SaveFileFormat.Vsdx);   // not .VSDX
diagram.Save(path, SaveFileFormat.Pdf);    // not .PDF

// Geometry collection
geom.CoordinateCol.Add(new MoveTo { ... });
geom.CoordinateCol.Add(new LineTo { ... });
geom.CoordinateCol.Add(new Ellipse { ... });

// Text
shape.Text.Value.SetWholeText("Label");

// No fill for connectors
shape.Fill.FillPattern.Value = 0;
```

---

## Licence

This project is provided as-is for demonstration purposes.  
Aspose.Diagram is a commercial library — see [Aspose licensing](https://purchase.aspose.com/policies/license-types) for production use.
