using Godot;

namespace AntHill;

public partial class Main : Node2D
{
    private TyphonBridge _bridge;
    private AntRenderer _antRenderer;

    public override void _Ready()
    {
        GD.Print("AntHill: Initializing Typhon engine...");

        _bridge = new TyphonBridge();
        _bridge.Initialize();

        GD.Print($"AntHill: Spawned {TyphonBridge.AntCount:N0} ants, WorldSize={TyphonBridge.WorldSize}. Starting runtime...");

        _antRenderer = GetNode<AntRenderer>("AntRenderer");
        _antRenderer.SetBridge(_bridge.RenderBridge);

        _bridge.Start();

        GD.Print("AntHill: Runtime started at 60Hz. Use WASD to pan, mouse wheel to zoom.");
    }

    private double _timeSinceLastLog;

    public override void _Process(double delta)
    {
        _antRenderer?.UpdateFromBridge();

        // Title bar update
        if (Godot.Engine.GetFramesDrawn() % 120 == 0)
        {
            var cam = GetNode<Camera2D>("Camera");
            var timing = _bridge?.GetTimingInfo() ?? "N/A";
            DisplayServer.WindowSetTitle(
                $"AntHill {Godot.Engine.GetFramesPerSecond():F0}fps | " +
                $"{TyphonBridge.AntCount:N0} ants | " +
                $"Cam({cam.Position.X:F0},{cam.Position.Y:F0}) z={cam.Zoom.X:F2} | " +
                $"{timing}");
        }

        // Console log every 2 seconds
        _timeSinceLastLog += delta;
        if (_timeSinceLastLog >= 2.0)
        {
            _timeSinceLastLog = 0;
            var timing = _bridge?.GetTimingInfo() ?? "N/A";
            GD.Print($"[Perf] {Godot.Engine.GetFramesPerSecond():F0}fps | {TyphonBridge.AntCount:N0} ants | {timing}");
        }
    }

    public override void _ExitTree()
    {
        GD.Print("AntHill: Shutting down...");
        _bridge?.Dispose();
        _bridge = null;
    }
}
