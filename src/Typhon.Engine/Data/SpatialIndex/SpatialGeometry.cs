using System;
using System.Runtime.CompilerServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// Geometry helper methods for spatial index operations. All methods are aggressively inlined for hot-path performance.
/// v1 ships with scalar implementations; SOA layout enables future drop-in SIMD replacements.
/// </summary>
internal static class SpatialGeometry
{
    // ── AABB2F ──────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Overlaps(AABB2F a, AABB2F b) => a.MinX <= b.MaxX && a.MaxX >= b.MinX && a.MinY <= b.MaxY && a.MaxY >= b.MinY;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Contains(AABB2F outer, AABB2F inner) => 
        outer.MinX <= inner.MinX && outer.MaxX >= inner.MaxX && outer.MinY <= inner.MinY && outer.MaxY >= inner.MaxY;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AABB2F Enlarge(AABB2F box, float margin) => new()
    {
        MinX = box.MinX - margin,
        MinY = box.MinY - margin,
        MaxX = box.MaxX + margin,
        MaxY = box.MaxY + margin,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AABB2F Union(AABB2F a, AABB2F b) => new()
    {
        MinX = Math.Min(a.MinX, b.MinX),
        MinY = Math.Min(a.MinY, b.MinY),
        MaxX = Math.Max(a.MaxX, b.MaxX),
        MaxY = Math.Max(a.MaxY, b.MaxY),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Area(AABB2F box) => (box.MaxX - box.MinX) * (box.MaxY - box.MinY);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AABB2F Enclosing(BSphere2F s) => new()
    {
        MinX = s.CenterX - s.Radius,
        MinY = s.CenterY - s.Radius,
        MaxX = s.CenterX + s.Radius,
        MaxY = s.CenterY + s.Radius,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDegenerate(AABB2F box) => 
        float.IsNaN(box.MinX) || float.IsNaN(box.MinY) || float.IsNaN(box.MaxX) || float.IsNaN(box.MaxY) || box.MinX > box.MaxX || box.MinY > box.MaxY;

    // ── AABB3F ──────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Overlaps(AABB3F a, AABB3F b) => 
        a.MinX <= b.MaxX && a.MaxX >= b.MinX && a.MinY <= b.MaxY && a.MaxY >= b.MinY && a.MinZ <= b.MaxZ && a.MaxZ >= b.MinZ;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Contains(AABB3F outer, AABB3F inner) =>
        outer.MinX <= inner.MinX && outer.MaxX >= inner.MaxX && outer.MinY <= inner.MinY && outer.MaxY >= inner.MaxY && outer.MinZ <= inner.MinZ && outer.MaxZ >= inner.MaxZ;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AABB3F Enlarge(AABB3F box, float margin) => new()
    {
        MinX = box.MinX - margin,
        MinY = box.MinY - margin,
        MinZ = box.MinZ - margin,
        MaxX = box.MaxX + margin,
        MaxY = box.MaxY + margin,
        MaxZ = box.MaxZ + margin,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AABB3F Union(AABB3F a, AABB3F b) => new()
    {
        MinX = Math.Min(a.MinX, b.MinX),
        MinY = Math.Min(a.MinY, b.MinY),
        MinZ = Math.Min(a.MinZ, b.MinZ),
        MaxX = Math.Max(a.MaxX, b.MaxX),
        MaxY = Math.Max(a.MaxY, b.MaxY),
        MaxZ = Math.Max(a.MaxZ, b.MaxZ),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Volume(AABB3F box) =>
        (box.MaxX - box.MinX) * (box.MaxY - box.MinY) * (box.MaxZ - box.MinZ);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AABB3F Enclosing(BSphere3F s) => new()
    {
        MinX = s.CenterX - s.Radius,
        MinY = s.CenterY - s.Radius,
        MinZ = s.CenterZ - s.Radius,
        MaxX = s.CenterX + s.Radius,
        MaxY = s.CenterY + s.Radius,
        MaxZ = s.CenterZ + s.Radius,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDegenerate(AABB3F box) =>
        float.IsNaN(box.MinX) || float.IsNaN(box.MinY) || float.IsNaN(box.MinZ) ||
        float.IsNaN(box.MaxX) || float.IsNaN(box.MaxY) || float.IsNaN(box.MaxZ) ||
        box.MinX > box.MaxX || box.MinY > box.MaxY || box.MinZ > box.MaxZ;

    // ── AABB2D ──────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Overlaps(AABB2D a, AABB2D b) =>
        a.MinX <= b.MaxX && a.MaxX >= b.MinX && a.MinY <= b.MaxY && a.MaxY >= b.MinY;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Contains(AABB2D outer, AABB2D inner) =>
        outer.MinX <= inner.MinX && outer.MaxX >= inner.MaxX && outer.MinY <= inner.MinY && outer.MaxY >= inner.MaxY;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AABB2D Enlarge(AABB2D box, double margin) => new()
    {
        MinX = box.MinX - margin,
        MinY = box.MinY - margin,
        MaxX = box.MaxX + margin,
        MaxY = box.MaxY + margin,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AABB2D Union(AABB2D a, AABB2D b) => new()
    {
        MinX = Math.Min(a.MinX, b.MinX),
        MinY = Math.Min(a.MinY, b.MinY),
        MaxX = Math.Max(a.MaxX, b.MaxX),
        MaxY = Math.Max(a.MaxY, b.MaxY),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Area(AABB2D box) => (box.MaxX - box.MinX) * (box.MaxY - box.MinY);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AABB2D Enclosing(BSphere2D s) => new()
    {
        MinX = s.CenterX - s.Radius,
        MinY = s.CenterY - s.Radius,
        MaxX = s.CenterX + s.Radius,
        MaxY = s.CenterY + s.Radius,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDegenerate(AABB2D box) =>
        double.IsNaN(box.MinX) || double.IsNaN(box.MinY) || double.IsNaN(box.MaxX) || double.IsNaN(box.MaxY) || box.MinX > box.MaxX || box.MinY > box.MaxY;

    // ── AABB3D ──────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Overlaps(AABB3D a, AABB3D b) =>
        a.MinX <= b.MaxX && a.MaxX >= b.MinX && a.MinY <= b.MaxY && a.MaxY >= b.MinY && a.MinZ <= b.MaxZ && a.MaxZ >= b.MinZ;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Contains(AABB3D outer, AABB3D inner) =>
        outer.MinX <= inner.MinX && outer.MaxX >= inner.MaxX && outer.MinY <= inner.MinY && outer.MaxY >= inner.MaxY && outer.MinZ <= inner.MinZ && outer.MaxZ >= inner.MaxZ;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AABB3D Enlarge(AABB3D box, double margin) => new()
    {
        MinX = box.MinX - margin,
        MinY = box.MinY - margin,
        MinZ = box.MinZ - margin,
        MaxX = box.MaxX + margin,
        MaxY = box.MaxY + margin,
        MaxZ = box.MaxZ + margin,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AABB3D Union(AABB3D a, AABB3D b) => new()
    {
        MinX = Math.Min(a.MinX, b.MinX),
        MinY = Math.Min(a.MinY, b.MinY),
        MinZ = Math.Min(a.MinZ, b.MinZ),
        MaxX = Math.Max(a.MaxX, b.MaxX),
        MaxY = Math.Max(a.MaxY, b.MaxY),
        MaxZ = Math.Max(a.MaxZ, b.MaxZ),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Volume(AABB3D box) =>
        (box.MaxX - box.MinX) * (box.MaxY - box.MinY) * (box.MaxZ - box.MinZ);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AABB3D Enclosing(BSphere3D s) => new()
    {
        MinX = s.CenterX - s.Radius,
        MinY = s.CenterY - s.Radius,
        MinZ = s.CenterZ - s.Radius,
        MaxX = s.CenterX + s.Radius,
        MaxY = s.CenterY + s.Radius,
        MaxZ = s.CenterZ + s.Radius,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDegenerate(AABB3D box) =>
        double.IsNaN(box.MinX) || double.IsNaN(box.MinY) || double.IsNaN(box.MinZ) ||
        double.IsNaN(box.MaxX) || double.IsNaN(box.MaxY) || double.IsNaN(box.MaxZ) ||
        box.MinX > box.MaxX || box.MinY > box.MaxY || box.MinZ > box.MaxZ;
}
