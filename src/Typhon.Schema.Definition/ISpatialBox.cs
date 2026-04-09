namespace Typhon.Schema.Definition;

/// <summary>
/// Marker interface narrowing generic cluster-spatial-query box parameters to the supported AABB variants.
/// Implemented by <see cref="AABB2F"/>, <see cref="AABB3F"/>, <see cref="AABB2D"/>, and <see cref="AABB3D"/>.
/// Consumed by <c>Typhon.Engine.ClusterSpatialQuery&lt;TArch&gt;.AABB&lt;TBox&gt;</c> (issue #230 Phase 2.5) to accept any of the 4 dimensionality × precision
/// combinations as a query region while preserving native-precision reads at the API boundary.
/// </summary>
/// <remarks>
/// <para>
/// <b>Marker-only by design.</b> This interface intentionally has no members. Each query method that takes an <see cref="ISpatialBox"/> dispatches
/// on <c>typeof(TBox) == typeof(Concrete)</c> at JIT specialization time and reads box fields
/// via <see cref="System.Runtime.CompilerServices.Unsafe.As{TFrom,TTo}(ref TFrom)"/>.
/// </para>
/// <para>
/// Why no accessor methods: a methoded interface would need to commit to a single return type (<c>float</c> or <c>double</c>) for any <c>MinX</c>/<c>MaxX</c>
/// accessor, which would either narrow (losing precision for f64 variants) or widen (wasting work for f32 variants) at the interface boundary. That is exactly
/// the precision-smearing problem issue #230 exists to prevent. Keeping the interface as a pure marker avoids the trade-off entirely and lets each dispatch
/// branch read its concrete struct fields at native precision.
/// </para>
/// <para>
/// <b>Location rationale.</b> This interface lives in <c>Typhon.Schema.Definition</c> alongside the AABB value types it marks, rather than in
/// <c>Typhon.Engine</c>, because <c>Typhon.Engine</c> references <c>Typhon.Schema.Definition</c> (not the other way around). Placing the marker here keeps the
/// schema project self-contained and lets engine code consume it through the existing project reference.
/// </para>
/// <para>
/// <b>Adding a new box variant.</b> (1) Add <c>: ISpatialBox</c> to the variant's <c>partial struct</c> declaration in <c>SpatialTypes.cs</c>. (2) Add a
/// <c>typeof(TBox) == typeof(NewVariant)</c> branch to <c>Typhon.Engine.SpatialTierExtensions.TBoxToTier&lt;TBox&gt;</c>. (3) Add a dispatch branch to every
/// query method that operates on <see cref="ISpatialBox"/> (currently just <c>Typhon.Engine.ClusterSpatialQuery&lt;TArch&gt;.AABB&lt;TBox&gt;</c>).
/// The compiler will NOT catch a missed dispatch branch — follow the same discipline used by <c>Typhon.Engine.SpatialMaintainer.ReadAndValidateBoundsFromPtr</c>,
/// which has maintained an 8-case <c>SpatialFieldType</c> switch without a bug for the life of the legacy per-entity R-Tree.
/// </para>
/// </remarks>
public interface ISpatialBox { }
