using System;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Configuration builder for class-based system definitions.
/// Used by <see cref="CallbackSystem"/>, <see cref="QuerySystem"/>, and <see cref="PipelineSystem"/> in their Configure method.
/// </summary>
[PublicAPI]
public sealed class SystemBuilder
{
    internal string _name;
    internal string _after;
    internal string[] _afterAll;
    internal SystemPriority _priority = SystemPriority.Normal;
    internal Func<bool> _runIf;
    internal Func<ViewBase> _inputFactory;
    internal Type[] _changeFilter;
    internal int _tickDivisor = 1;
    internal int _throttledTickDivisor = 1;
    internal bool _canShed;
    internal bool _parallel;
    internal bool _writesVersioned;
    internal SimTier _tierFilter = SimTier.All;
    internal int _cellAmortize;

    /// <summary>Set the system's unique name in the DAG.</summary>
    public void Name(string name) => _name = name;

    /// <summary>Declare a dependency on another system (this system runs after it).</summary>
    public void After(string dependency) => _after = dependency;

    /// <summary>Declare dependencies on multiple systems (this system runs after all of them).</summary>
    public void AfterAll(params string[] dependencies) => _afterAll = dependencies;

    /// <summary>Set the system's overload priority.</summary>
    public void Priority(SystemPriority priority) => _priority = priority;

    /// <summary>Set a predicate that must return true for the system to execute. Evaluated before any input processing.</summary>
    public void RunIf(Func<bool> predicate) => _runIf = predicate;

    /// <summary>Set the View factory providing the system's entity input.</summary>
    public void Input(Func<ViewBase> viewFactory) => _inputFactory = viewFactory;

    /// <summary>Set the component types for change-filtered reactive input. OR logic: entity included if any filtered component was written.</summary>
    public void ChangeFilter(params Type[] componentTypes) => _changeFilter = componentTypes;

    /// <summary>Set the tick divisor (system runs every Nth tick at normal load).</summary>
    public void TickDivisor(int divisor) => _tickDivisor = divisor;

    /// <summary>Set the throttled tick divisor (system runs every Nth tick under overload).</summary>
    public void ThrottledTickDivisor(int divisor) => _throttledTickDivisor = divisor;

    /// <summary>Set whether this system can be shed entirely under severe overload.</summary>
    public void CanShed(bool value) => _canShed = value;

    /// <summary>Enable automatic chunk-parallel execution across workers. QuerySystem only.</summary>
    public void Parallel() => _parallel = true;

    /// <summary>Declare that this parallel QuerySystem writes Versioned components. Forces per-chunk Transaction fallback instead of the optimized PointInTimeAccessor path.</summary>
    public void WritesVersioned() => _writesVersioned = true;

    /// <summary>
    /// Set the simulation-tier dispatch filter (issue #231). Default <see cref="SimTier.All"/> matches pre-#231 behaviour (all clusters dispatched).
    /// Single-tier (e.g. <see cref="SimTier.Tier0"/>) or multi-tier flag combinations (<see cref="SimTier.Near"/>, <see cref="SimTier.Active"/>) are both
    /// supported.
    /// </summary>
    public void Tier(SimTier tier) => _tierFilter = tier;

    /// <summary>
    /// Set the cell-level amortization denominator (issue #231). When greater than 0, the system processes <c>1/N</c> of the tier's clusters per tick,
    /// and <see cref="TickContext.AmortizedDeltaTime"/> becomes <c>DeltaTime × N</c>. Requires a non-<see cref="SimTier.All"/> <see cref="Tier"/>.
    /// </summary>
    public void CellAmortize(int denominator) => _cellAmortize = denominator;
}
