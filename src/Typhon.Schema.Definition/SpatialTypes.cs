using JetBrains.Annotations;

namespace Typhon.Schema.Definition;

// Float32 AABB types

[PublicAPI]
public struct AABB2F
{
    public float MinX;
    public float MinY;
    public float MaxX;
    public float MaxY;
}

[PublicAPI]
public struct AABB3F
{
    public float MinX;
    public float MinY;
    public float MinZ;
    public float MaxX;
    public float MaxY;
    public float MaxZ;
}

// Float32 BoundingSphere types

[PublicAPI]
public struct BSphere2F
{
    public float CenterX;
    public float CenterY;
    public float Radius;
}

[PublicAPI]
public struct BSphere3F
{
    public float CenterX;
    public float CenterY;
    public float CenterZ;
    public float Radius;
}

// Float64 AABB types

[PublicAPI]
public struct AABB2D
{
    public double MinX;
    public double MinY;
    public double MaxX;
    public double MaxY;
}

[PublicAPI]
public struct AABB3D
{
    public double MinX;
    public double MinY;
    public double MinZ;
    public double MaxX;
    public double MaxY;
    public double MaxZ;
}

// Float64 BoundingSphere types

[PublicAPI]
public struct BSphere2D
{
    public double CenterX;
    public double CenterY;
    public double Radius;
}

[PublicAPI]
public struct BSphere3D
{
    public double CenterX;
    public double CenterY;
    public double CenterZ;
    public double Radius;
}
