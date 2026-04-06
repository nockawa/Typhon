# AntHill Demo

Godot 4.6 C# application embedding Typhon engine directly (no client/server). Renders 100K+ ants whose positions are updated by parallel Typhon systems at 60Hz.

## Design Doc

Full design: `claude/demo/anthill/design.md`

## Godot Documentation

- Main docs: https://docs.godotengine.org/en/stable/index.html
- C# / .NET: https://docs.godotengine.org/en/stable/tutorials/scripting/c_sharp/index.html

## Godot Setup

The `godot/` directory is not committed (248 MB). After cloning, download and extract Godot 4.6 .NET:

1. Download **Godot 4.6 .NET** from https://godotengine.org/download
2. Extract into `test/AntHill/godot/` so the layout is:
   ```
   test/AntHill/godot/
   ├── godot.exe
   ├── godot_console.exe
   └── GodotSharp/
       └── Tools/nupkgs/   (required by NuGet.config for SDK packages)
   ```

Run the project:
```bash
test/AntHill/godot/godot.exe --path test/AntHill/
```

Build:
```bash
dotnet build test/AntHill/AntHill.csproj
```

## Architecture

Typhon Runtime ticks at 60Hz on worker threads. A `CopyToRenderBuffer` callback system copies entity positions into a shared float[] buffer via volatile reference swap. Godot's `_Process` reads the latest buffer and uploads to MultiMeshInstance2D.
