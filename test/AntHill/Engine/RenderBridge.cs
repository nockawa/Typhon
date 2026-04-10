namespace AntHill;

/// <summary>
/// Immutable snapshot of entity render data for one tick.
/// Published by the Typhon CopyToRenderBuffer system, consumed by Godot's _Process.
/// </summary>
public sealed class RenderFrame
{
    /// <summary>
    /// MultiMesh buffer: 12 floats per instance (8 Transform2D as 2x4 GPU matrix + 4 Color RGBA).
    /// Layout per instance: [bx0, bx1, 0, ox, by0, by1, 0, oy, r, g, b, a]
    /// </summary>
    public float[] Buffer;

    /// <summary>Number of visible instances in the buffer.</summary>
    public int Count;
}

/// <summary>
/// Lock-free bridge between Typhon worker threads and Godot main thread.
/// Runtime publishes a new RenderFrame each tick via volatile write.
/// Godot reads the latest frame — no lock, no contention.
/// </summary>
public sealed class RenderBridge
{
    private volatile RenderFrame _latest;

    public void Publish(RenderFrame frame) => _latest = frame;
    public RenderFrame GetLatest() => _latest;
}
