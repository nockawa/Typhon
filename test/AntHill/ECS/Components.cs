using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace AntHill.ECS;

[Component("AntHill.Position", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct Position
{
    public float X;
    public float Y;

    public Position(float x, float y)
    {
        X = x;
        Y = y;
    }
}

[Component("AntHill.Movement", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct Movement
{
    public float VX;
    public float VY;

    public Movement(float vx, float vy)
    {
        VX = vx;
        VY = vy;
    }
}
