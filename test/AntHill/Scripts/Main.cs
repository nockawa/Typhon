using Godot;

namespace AntHill;

public partial class Main : Node2D
{
    private TyphonBridge _bridge;
    private AntRenderer _antRenderer;
    private Camera2D _camera;
    private PheromoneOverlay _pheromoneOverlay;

    // HUD
    private Label _hudLeft;
    private Label _hudRight;

    public override void _Ready()
    {
        GD.Print("AntHill: Initializing Typhon engine...");

        // Profiler activation (runs BEFORE TyphonBridge construction so the JIT gate
        // is open when DagScheduler's hot methods are compiled).
        // Two input channels, both optional, env vars take precedence:
        //   1. Env vars: TYPHON_PROFILER_LIVE=9001 or TYPHON_PROFILER_TRACE=<path>
        //   2. Godot cmdline user args: launch with "++ --live 9001" or "++ --trace <path>"
        var (envTrace, envPort) = ProfilerSetup.ReadEnvVars();
        var (argTrace, argPort) = ProfilerSetup.ParseArgs(OS.GetCmdlineUserArgs());
        string traceFile = envTrace ?? argTrace;
        int livePort = envPort >= 0 ? envPort : argPort;
        _inspector = ProfilerSetup.TryCreateInspector(traceFile, livePort);
        if (_inspector != null)
        {
            GD.Print(traceFile != null
                ? $"AntHill: Profiler enabled -> file mode: {traceFile}"
                : $"AntHill: Profiler enabled -> live mode: TCP listener on port {livePort}");
        }

        _bridge = new TyphonBridge();
        _bridge.Initialize(_inspector);

        GD.Print($"AntHill: Spawned {TyphonBridge.AntCount:N0} ants. Starting runtime...");

        _antRenderer = GetNode<AntRenderer>("AntRenderer");
        _antRenderer.SetBridge(_bridge.RenderBridge);

        _camera = GetNode<Camera2D>("Camera");

        // Pheromone heatmap overlay (H to toggle)
        _pheromoneOverlay = new PheromoneOverlay();
        _pheromoneOverlay.SetBridge(_bridge.RenderBridge, _bridge);
        AddChild(_pheromoneOverlay);

        // HUD
        var hud = new CanvasLayer();
        hud.Layer = 10;

        _hudLeft = new Label();
        _hudLeft.Position = new Vector2(10, 10);
        _hudLeft.AddThemeColorOverride("font_color", Colors.White);
        _hudLeft.AddThemeColorOverride("font_shadow_color", Colors.Black);
        _hudLeft.AddThemeConstantOverride("shadow_offset_x", 1);
        _hudLeft.AddThemeConstantOverride("shadow_offset_y", 1);
        _hudLeft.AddThemeFontSizeOverride("font_size", 16);
        hud.AddChild(_hudLeft);

        _hudRight = new Label();
        _hudRight.HorizontalAlignment = HorizontalAlignment.Right;
        _hudRight.AnchorLeft = 0.5f;
        _hudRight.AnchorRight = 1.0f;
        _hudRight.OffsetRight = -10;
        _hudRight.OffsetTop = 10;
        _hudRight.AddThemeColorOverride("font_color", Colors.White);
        _hudRight.AddThemeColorOverride("font_shadow_color", Colors.Black);
        _hudRight.AddThemeConstantOverride("shadow_offset_x", 1);
        _hudRight.AddThemeConstantOverride("shadow_offset_y", 1);
        _hudRight.AddThemeFontSizeOverride("font_size", 14);
        hud.AddChild(_hudRight);

        AddChild(hud);

        _bridge.Start();
        GD.Print("AntHill: Runtime started. WASD=pan, wheel=zoom, `=pause, 1-4=speed, H=pheromone overlay.");

        // Telemetry diagnostics — prints current TelemetryConfig state, inspector type,
        // and most importantly whether TyphonActivitySource has an ActivityListener attached.
        // Call AFTER _bridge.Start() so the scheduler has had a chance to register the listener.
        ProfilerSetup.PrintDiagnostics(GD.Print, _inspector);
    }

    private Typhon.Engine.IRuntimeInspector _inspector;

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && !key.Echo)
        {
            switch (key.Keycode)
            {
                case Key.Quoteleft:
                    if (_bridge != null)
                    {
                        _bridge.TimeScale = _bridge.TimeScale == 0f ? _lastTimeScale : 0f;
                    }
                    break;
                case Key.Key1: SetSpeed(1f); break;
                case Key.Key2: SetSpeed(2f); break;
                case Key.Key3: SetSpeed(4f); break;
                case Key.Key4: SetSpeed(10f); break;
                case Key.H: _pheromoneOverlay?.Toggle(); break;
            }
        }
    }

    private float _lastTimeScale = 1f;

    private void SetSpeed(float speed)
    {
        if (_bridge == null) return;
        _lastTimeScale = speed;
        _bridge.TimeScale = speed;
    }

    public override void _Process(double delta)
    {
        _antRenderer?.UpdateFromBridge();
        _pheromoneOverlay?.UpdateFromFrame();

        if (_camera != null && _bridge != null)
        {
            var vpSize = GetViewportRect().Size;
            float halfW = vpSize.X / (2f * _camera.Zoom.X);
            float halfH = vpSize.Y / (2f * _camera.Zoom.Y);
            _bridge.UpdateCamera(
                _camera.Position.X - halfW, _camera.Position.Y - halfH,
                _camera.Position.X + halfW, _camera.Position.Y + halfH);
        }

        // HUD update
        if (Engine.GetFramesDrawn() % 10 == 0 && _bridge != null)
        {
            var tiers = _bridge.TierCounts;
            var states = _bridge.StateCounts;
            int foraging = states[0];
            int carrying = states[1];

            string speedLabel = _bridge.TimeScale == 0f ? "PAUSED" : $"{_bridge.TimeScale:G}x";
            _hudLeft.Text =
                $"[{speedLabel}]  Ants: {TyphonBridge.AntCount:N0}  ({TyphonBridge.NestCount} nests)\n" +
                $"Foraging: {foraging:N0}   Returning: {carrying:N0}\n" +
                $"Food: {_bridge.FoodSourcesRemaining}/{TyphonBridge.FoodCount} sources   Delivered: {_bridge.FoodDelivered:N0}\n" +
                $"Nest reserves: {_bridge.TotalNestFood:N0}   Deaths: {_bridge.DeathCount:N0}";

            int drawCalls = (int)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
            var timing = _bridge.GetTimingInfo() ?? "N/A";
            _hudRight.Text =
                $"{Engine.GetFramesPerSecond():F0} fps  |  Draw: {drawCalls}  Visible: {_bridge.VisibleAnts:N0}\n" +
                $"T0: {tiers[0]:N0}  T1: {tiers[1]:N0}  T2: {tiers[2]:N0}  T3: {tiers[3]:N0}\n" +
                $"{timing}";

            DisplayServer.WindowSetTitle(
                $"AntHill {Engine.GetFramesPerSecond():F0}fps | {TyphonBridge.AntCount:N0} ants | " +
                $"F:{foraging:N0} C:{carrying:N0}");
        }
    }

    public override void _ExitTree()
    {
        GD.Print("AntHill: Shutting down...");
        _bridge?.Dispose();
        _bridge = null;
    }
}
