using Godot;

namespace AntHill.Scripts;

public partial class GameCamera : Camera2D
{
    [Export] public float PanSpeed = 500f;
    [Export] public float ZoomSpeed = 0.1f;
    [Export] public float MinZoom = 0.01f;
    [Export] public float MaxZoom = 4.0f;

    private bool _dragging;

    public override void _UnhandledInput(InputEvent @event)
    {
        // Mouse wheel zoom (toward cursor)
        if (@event is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp)
            {
                Zoom *= new Vector2(1 + ZoomSpeed, 1 + ZoomSpeed);
            }
            else if (mb.ButtonIndex == MouseButton.WheelDown)
            {
                Zoom *= new Vector2(1 - ZoomSpeed, 1 - ZoomSpeed);
            }

            Zoom = Zoom.Clamp(new Vector2(MinZoom, MinZoom), new Vector2(MaxZoom, MaxZoom));
        }

        // Middle-mouse drag to pan
        if (@event is InputEventMouseButton mmb && mmb.ButtonIndex == MouseButton.Middle)
        {
            _dragging = mmb.Pressed;
        }

        if (@event is InputEventMouseMotion motion && _dragging)
        {
            Position -= motion.Relative / Zoom;
        }
    }

    public override void _Process(double delta)
    {
        // WASD keyboard pan
        var input = Vector2.Zero;
        if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up)) input.Y -= 1;
        if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down)) input.Y += 1;
        if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left)) input.X -= 1;
        if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) input.X += 1;

        if (input != Vector2.Zero)
        {
            Position += input * PanSpeed * (float)delta / Zoom.X;
        }
    }
}
