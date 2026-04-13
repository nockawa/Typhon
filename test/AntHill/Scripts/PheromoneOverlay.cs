using Godot;

namespace AntHill;

/// <summary>
/// Single combined pheromone heatmap overlay: green=food trail, blue=home trail.
/// H key toggles visibility. One Image.SetData call per frame — no per-pixel overhead.
/// </summary>
public partial class PheromoneOverlay : Node2D
{
    private Sprite2D _sprite;
    private Image _img;
    private ImageTexture _tex;
    private RenderBridge _bridge;
    private TyphonBridge _typhonBridge;
    private bool _visible;

    public void SetBridge(RenderBridge bridge, TyphonBridge typhonBridge)
    {
        _bridge = bridge;
        _typhonBridge = typhonBridge;
    }

    public void Toggle()
    {
        _visible = !_visible;
        if (_sprite != null) _sprite.Visible = _visible;
        _typhonBridge?.SetHeatmapEnabled(_visible);
    }

    public override void _Ready()
    {
        const int hs = RenderFrame.HeatmapSize;

        _img = Image.CreateEmpty(hs, hs, false, Image.Format.Rgba8);
        _tex = ImageTexture.CreateFromImage(_img);

        _sprite = new Sprite2D();
        _sprite.Texture = _tex;
        _sprite.Centered = false;
        _sprite.Scale = new Vector2(TyphonBridge.WorldSize / hs, TyphonBridge.WorldSize / hs);
        _sprite.ZIndex = -1;
        _sprite.Visible = _visible;
        AddChild(_sprite);
    }

    public void UpdateFromFrame()
    {
        if (_bridge == null || !_visible) return;

        var frame = _bridge.GetLatest();
        if (frame?.HeatmapRGBA == null) return;

        const int hs = RenderFrame.HeatmapSize;
        _img.SetData(hs, hs, false, Image.Format.Rgba8, frame.HeatmapRGBA);
        _tex.Update(_img);
    }
}
