# D2 Diagram Conventions

This document defines the D2 syntax patterns, color palette, and styling conventions used in Typhon documentation diagrams.

> **Quick start:** Copy a similar existing diagram from `claude/assets/src/` and adapt it. This document provides the reference for consistent styling.

---

## Table of Contents

1. [File Locations & Rendering](#file-locations--rendering)
2. [Typhon Color Palette](#typhon-color-palette)
3. [D2 Language Reference](#d2-language-reference)
4. [Typhon Diagram Patterns](#typhon-diagram-patterns)
5. [Existing Diagrams](#existing-diagrams-by-category)
6. [Checklist: New Diagram](#checklist-new-diagram)

---

## File Locations & Rendering

| Path | Purpose |
|------|---------|
| `claude/assets/src/*.d2` | D2 source files (editable) |
| `claude/assets/*.svg` | Rendered SVG output |
| `claude/assets/viewer.html` | Interactive pan-zoom viewer |

### Rendering Command

```bash
"/c/Program Files/D2/d2.exe" --theme 0 assets/src/my-diagram.d2 assets/my-diagram.svg
```

### Layout Engines

```bash
# Default (dagre) - hierarchical layouts
"/c/Program Files/D2/d2.exe" --layout dagre ...

# ELK - better for complex directed graphs
"/c/Program Files/D2/d2.exe" --layout elk ...

# TALA - optimized for software architecture (supports more positioning)
"/c/Program Files/D2/d2.exe" --layout tala ...
```

After rendering, add the new diagram to the `DIAGRAMS` array in `viewer.html`.

---

## Typhon Color Palette

Typhon uses Material Design colors organized by architectural layer.

### Layer Colors (Containers)

| Layer | Fill | Stroke | Usage |
|-------|------|--------|-------|
| **API Layer** | `#e3f2fd` | `#1565c0` | DatabaseEngine, UnitOfWork, Transaction |
| **Data Engine** | `#e8f5e9` | `#2e7d32` | ComponentTable, Indexes, Schema, MVCC |
| **Durability** | `#fce4ec` | `#c62828` | WAL, Checkpoint, Recovery |
| **Storage Engine** | `#fff3e0` | `#e65100` | PagedMMF, Buffer Pool, Segments |
| **Concurrency** | `#f3e5f5` | `#6a1b9a` | AccessControl, Epochs, Locks |

### State Colors (State Machines)

| State | Fill | Stroke | Font Color | Usage |
|-------|------|--------|------------|-------|
| **Free/Available** | `#c8e6c9` | `#388e3c` | default | Available slots, success, completed |
| **Pending/Warning** | `#fff3e0` | `#f57c00` | `#e65100` | In-progress, awaiting |
| **Active/Durable** | `#bbdefb` | `#1976d2` | default | WalDurable, active |
| **Committed/Success** | `#1565c0` | `#0d47a1` | `white` | Final success state |
| **Error/Void** | `#ffcdd2` | `#c62828` | `#b71c1c` | Failures, voided |

### Component Colors (Within Layers)

Use progressively darker shades within a layer:

```d2
# API Layer example (blue gradient)
dbe: DatabaseEngine { style.fill: "#bbdefb" }
uow: UnitOfWork { style.fill: "#90caf9" }
tx: Transaction { style.fill: "#64b5f6" }

# Data Engine example (green gradient)
ct: ComponentTable { style.fill: "#c8e6c9" }
rev: "Revision Chains" { style.fill: "#a5d6a7" }
idx: "B+Tree Indexes" { style.fill: "#81c784" }
```

---

## D2 Language Reference

### All Available Shapes

| Shape | Syntax | Best For |
|-------|--------|----------|
| `rectangle` | default | Components, classes, processes |
| `square` | `shape: square` | Equal-dimension boxes (1:1 ratio enforced) |
| `circle` | `shape: circle` | States, endpoints (1:1 ratio enforced) |
| `oval` | `shape: oval` | Alternative state representation |
| `diamond` | `shape: diamond` | Decision points, conditionals |
| `hexagon` | `shape: hexagon` | Preparation steps, special processes |
| `parallelogram` | `shape: parallelogram` | Input/output operations |
| `cylinder` | `shape: cylinder` | Databases, storage, buffers |
| `queue` | `shape: queue` | Message queues, FIFO structures |
| `page` | `shape: page` | Documents, files |
| `document` | `shape: document` | Multi-page documents |
| `step` | `shape: step` | Sequential process steps |
| `callout` | `shape: callout` | Annotations, speech bubbles |
| `stored_data` | `shape: stored_data` | Data storage (alternative to cylinder) |
| `person` | `shape: person` | User/actor representation |
| `cloud` | `shape: cloud` | External systems, internet |
| `package` | `shape: package` | Modules, packages |
| `text` | `shape: text` | Standalone text, notes, titles |
| `image` | `shape: image` | Standalone icons/images |
| `sequence_diagram` | `shape: sequence_diagram` | Temporal message flows |
| `sql_table` | `shape: sql_table` | Database table schemas |
| `class` | `shape: class` | UML class diagrams |
| `c4-person` | `shape: c4-person` | C4 model person |

### Connection Types

```d2
# Directed (arrow pointing right)
a -> b: "label"

# Directed (arrow pointing left)
a <- b: "label"

# Bidirectional
a <-> b: "both ways"

# Undirected (no arrows)
a -- b: "connected"

# Connection chaining
a -> b -> c -> d: "flows through"
```

### Arrowhead Styles

```d2
a -> b: "default triangle"

# Custom arrowheads
a -> b: {
  source-arrowhead: diamond
  target-arrowhead: arrow
}

# Available arrowhead types:
# triangle (default), arrow, diamond, circle, 
# cf-one, cf-one-required, cf-many, cf-many-required (cardinality)

# Filled vs unfilled
a -> b: {
  target-arrowhead: {
    shape: diamond
    style.filled: true
  }
}
```

### All Style Properties

```d2
node: "Label" {
  # Shape
  shape: rectangle
  
  # Colors
  style.fill: "#hex"              # Background color (or CSS gradient)
  style.stroke: "#hex"            # Border/outline color
  style.font-color: "#hex"        # Text color
  
  # Dimensions
  width: 200                      # Fixed width
  height: 100                     # Fixed height
  
  # Border
  style.stroke-width: 2           # Border thickness
  style.stroke-dash: 3            # Dash pattern (0 = solid)
  style.border-radius: 8          # Rounded corners (pixels)
  style.double-border: true       # Double outline (rectangles, ovals)
  
  # Effects
  style.shadow: true              # Drop shadow
  style.3d: true                  # 3D effect (rectangles, squares only)
  style.multiple: true            # Stacked/multiple copies effect
  style.opacity: 0.8              # Transparency (0-1)
  
  # Typography
  style.font: "mono"              # Font family
  style.font-size: 16             # Font size (pixels)
  style.bold: true                # Bold text
  style.italic: true              # Italic text
  style.underline: true           # Underlined text
  style.text-transform: uppercase # Text case transform
  
  # Animation (SVG only)
  style.animated: true            # Enable animations
}
```

### Connection Styles

```d2
a -> b: "label" {
  style.stroke: "#hex"            # Line color
  style.stroke-width: 2           # Line thickness
  style.stroke-dash: 3            # Dash pattern
  style.opacity: 0.8              # Transparency
  style.animated: true            # Animated dashes (SVG)
  style.bold: true                # Bold label
  style.font-size: 12             # Label font size
  style.font-color: "#hex"        # Label color
}
```

### Direction

```d2
direction: down     # Top-to-bottom (default)
direction: right    # Left-to-right
direction: up       # Bottom-to-top
direction: left     # Right-to-left
```

### Containers and Nesting

```d2
# Explicit nesting
outer: "Container" {
  inner1: "Child 1"
  inner2: "Child 2"
  
  inner1 -> inner2
}

# Dot notation (equivalent)
outer.inner1: "Child 1"
outer.inner2: "Child 2"
outer.inner1 -> outer.inner2

# Parent reference from inside container
outer: {
  child: "Child"
  child -> _: "connects to parent"
}
```

### Positioning with `near`

```d2
# Position text/elements at specific locations
title: "My Title" {
  near: top-center
}

legend: "Legend" {
  near: bottom-right
}

# Available positions:
# top-left, top-center, top-right
# center-left, center-right
# bottom-left, bottom-center, bottom-right

# Position label/icon within a shape
node: "Component" {
  icon: https://icons.terrastruct.com/essentials/092-network.svg
  label.near: bottom-center
  icon.near: top-center
}
```

### Icons

```d2
# Icon from URL
server: "Server" {
  icon: https://icons.terrastruct.com/tech/server.svg
}

# Local icon file
app: "App" {
  icon: ./icons/app.png
}

# Standalone icon (no text)
logo: {
  shape: image
  icon: https://icons.terrastruct.com/logo.svg
}

# Free icons: https://icons.terrastruct.com
```

### Markdown and Text

```d2
# Multiline with markdown
description: |md
  # Heading
  **Bold** and *italic* text
  
  - Bullet 1
  - Bullet 2
  
  `inline code`
|

# Code blocks with syntax highlighting
code_example: |go
  func main() {
    fmt.Println("Hello")
  }
|

# LaTeX math (use 'latex' or 'tex')
formula: |latex
  E = mc^2
|
```

### Variables

```d2
vars: {
  primary-color: "#1565c0"
  secondary-color: "#2e7d32"
}

api: "API" {
  style.fill: ${primary-color}
}

data: "Data" {
  style.fill: ${secondary-color}
}

# Nested variables
vars: {
  colors: {
    api: "#1565c0"
    data: "#2e7d32"
  }
}
node: { style.fill: ${colors.api} }

# Spread operator for maps
vars: {
  common-style: {
    style.border-radius: 8
    style.stroke-width: 2
  }
}
node: {
  ...${common-style}
  style.fill: "#fff"
}
```

### Classes (Reusable Styles)

```d2
classes: {
  api-component: {
    style.fill: "#e3f2fd"
    style.stroke: "#1565c0"
    style.border-radius: 8
  }
  storage: {
    shape: cylinder
    style.fill: "#fff3e0"
    style.stroke: "#e65100"
  }
}

# Apply class
dbe: DatabaseEngine { class: api-component }
buffer: "Buffer Pool" { class: storage }

# Multiple classes (processed left-to-right)
node: { class: [api-component; storage] }
```

### Sequence Diagrams

```d2
shape: sequence_diagram

# Actors (order of declaration = display order)
client: Client
server: Server
db: Database

# Messages (order = sequence)
client -> server: "HTTP Request"
server -> db: "Query"
db -> server: "Results"
server -> client: "HTTP Response"

# Self-message
server -> server: "Process data"

# Spans (activation boxes)
client -> server: "Request" {
  server.processing: "Handle request"
  server.processing -> db: "Fetch"
}

# Notes (no connections)
client: {
  note: "User's browser"
}

# Groups (fragments/frames)
group: "Authentication Flow" {
  client -> server: "Login"
  server -> client: "Token"
}
```

### SQL Table Diagrams

```d2
users: {
  shape: sql_table
  id: int {constraint: primary_key}
  email: varchar(255) {constraint: unique}
  created_at: timestamp
}

orders: {
  shape: sql_table
  id: int {constraint: primary_key}
  user_id: int {constraint: foreign_key}
  total: decimal(10,2)
}

# Foreign key relationship (connects to exact row with ELK/TALA)
orders.user_id -> users.id
```

### Grid Diagrams

```d2
grid: {
  grid-rows: 3
  grid-columns: 4
  grid-gap: 10
  
  cell1: "A"
  cell2: "B"
  cell3: "C"
  # ... cells fill left-to-right, then top-to-bottom
}

# Control gaps separately
layout: {
  horizontal-gap: 20
  vertical-gap: 10
}
```

---

## Typhon Diagram Patterns

### 1. Title Block

```d2
title: "Diagram Title — optional subtitle" {
  shape: text
  style.font-size: 22
  style.bold: true
  style.font-color: "#37474f"
  near: top-center
}
```

### 2. Container (Layer/Group)

```d2
layer_name: "Layer Name" {
  style.fill: "#e3f2fd"
  style.stroke: "#1565c0"
  style.border-radius: 8

  component1: "Component" {
    shape: rectangle
    style.fill: "#bbdefb"
  }
  
  storage1: "Storage" {
    shape: cylinder
    style.fill: "#90caf9"
  }
}
```

### 3. Connections (Typhon Conventions)

```d2
# Normal flow
a -> b: "label" { style.stroke: "#1565c0" }

# Dashed (optional, async, or secondary)
a -> b: "async" { style.stroke: "#78909c"; style.stroke-dash: 3 }

# Long dash (GC, cleanup, or rare paths)
a -> b: "GC" { style.stroke: "#757575"; style.stroke-dash: 5 }

# Emphasized (critical path)
a -> b: "commit" { style.stroke: "#c62828"; style.bold: true }
```

### 4. State Machine

```d2
direction: down

free: "Free\n(slot available)" {
  shape: circle
  style.fill: "#c8e6c9"
  style.stroke: "#388e3c"
  style.bold: true
}

pending: "Pending\n(in progress)" {
  shape: rectangle
  style.fill: "#fff3e0"
  style.stroke: "#f57c00"
  style.font-color: "#e65100"
  style.bold: true
}

committed: "Committed\n(complete)" {
  shape: rectangle
  style.fill: "#1565c0"
  style.font-color: white
  style.stroke: "#0d47a1"
  style.bold: true
}

free -> pending: "allocate" { style.stroke: "#388e3c"; style.bold: true }
pending -> committed: "commit" { style.stroke: "#1976d2" }
committed -> free: "GC" { style.stroke: "#757575"; style.stroke-dash: 5 }
```

### 5. Sequence Diagram (Typhon Style)

```d2
shape: sequence_diagram

app: Application
tx: Transaction
wal: "WAL Writer"
disk: "WAL Segment"

app -> tx: "tx.Commit()"
tx -> tx: "conflict check"
tx -> wal: "SerializeToBuffer()"
wal -> disk: "FUA write"
disk -> wal: "complete"
wal -> tx: "LSN confirmed"
tx -> app: "return true"
```

### 6. Notes/Annotations

```d2
note: |md
  **Key point in bold**
  
  - Bullet point 1
  - Bullet point 2
  
  `code snippet`
| {
  shape: text
  style.font-size: 13
}
```

### 7. Layered Architecture (Vertical)

```d2
direction: down

api: "API Layer" {
  style.fill: "#e3f2fd"
  style.stroke: "#1565c0"
  # ... components
}
data: "Data Engine" {
  style.fill: "#e8f5e9"
  style.stroke: "#2e7d32"
  # ... components
}
storage: "Storage Engine" {
  style.fill: "#fff3e0"
  style.stroke: "#e65100"
  # ... components
}

api -> data: "CRUD operations" { style.stroke: "#1565c0" }
data -> storage: "page access" { style.stroke: "#e65100" }
```

### 8. Pipeline (Horizontal Steps)

```d2
direction: right

step1: "1. Input" { style.fill: "#e3f2fd"; style.border-radius: 6 }
step2: "2. Process" { style.fill: "#e8f5e9"; style.border-radius: 6 }
step3: "3. Output" { style.fill: "#c8e6c9"; style.border-radius: 6 }

step1 -> step2 -> step3
```

### 9. Pipeline with Inputs/Outputs

```d2
direction: down

# Inputs on the left
inputs: "Inputs" {
  style.fill: "#eceff1"
  dirty: "Dirty Pages" { shape: cylinder }
  registry: "Registry" { shape: cylinder }
}

# Pipeline in center
pipeline: "" {
  style.fill: "#fafafa"
  step1: "1. Collect" { style.fill: "#e3f2fd" }
  step2: "2. Write" { style.fill: "#e8f5e9" }
  step3: "3. Sync" { style.fill: "#fff3e0" }
  
  step1 -> step2 -> step3
}

# Outputs on the right
outputs: "Outputs" {
  style.fill: "#eceff1"
  file: "Data File" { shape: page; style.fill: "#a5d6a7" }
}

inputs.dirty -> pipeline.step1 { style.stroke-dash: 3 }
pipeline.step3 -> outputs.file { style.stroke-dash: 3 }
```

---

## Existing Diagrams by Category

Reference these for consistent styling patterns:

### Architecture
- `typhon-architecture-layers.d2` — Layered system overview
- `typhon-dependency-flow.d2` — Component dependencies

### Execution & Durability
- `typhon-commit-path.d2` — Transaction commit flow
- `typhon-immediate-commit-sequence.d2` — Sequence diagram example
- `typhon-checkpoint-pipeline.d2` — Pipeline with inputs/outputs
- `typhon-uow-registry-lifecycle.d2` — State machine example
- `typhon-durability-overview.d2` — Durability layer components

### Data & MVCC
- `typhon-data-mvcc-read.d2` — Read path
- `typhon-data-mvcc-write.d2` — Write path
- `typhon-data-snapshot-isolation.d2` — Isolation concepts
- `typhon-data-engine-overview.d2` — Data engine components

### Storage
- `typhon-storage-overview.d2` — Storage layer components

### Resources
- `typhon-resource-graph-overview.d2` — Resource management
- `typhon-resource-tree-topology.d2` — Tree structures

### Backup
- `typhon-pitbackup-architecture.d2` — Backup system overview
- `typhon-pitbackup-file-format.d2` — File structure
- `typhon-pitbackup-creation-flow.d2` — Backup creation sequence

---

## Checklist: New Diagram

1. **Create source file:** `claude/assets/src/typhon-{area}-{description}.d2`
2. **Follow naming convention:** `typhon-{layer}-{topic}.d2`
3. **Add title block** with descriptive subtitle
4. **Use layer colors** from Typhon palette above
5. **Use appropriate shapes:** cylinder for storage, page for files, etc.
6. **Render:**
   ```bash
   "/c/Program Files/D2/d2.exe" --theme 0 assets/src/typhon-xxx.d2 assets/typhon-xxx.svg
   ```
7. **Update `viewer.html`:** Add to `DIAGRAMS` array in appropriate category
8. **Embed in markdown:** Use thumbnail pattern from `claude/README.md`

---

## Resources

- **D2 Playground:** https://play.d2lang.com
- **D2 Icons:** https://icons.terrastruct.com
- **D2 Documentation:** https://d2lang.com/tour/intro
- **Typhon Viewer:** Open `claude/assets/viewer.html` in browser
