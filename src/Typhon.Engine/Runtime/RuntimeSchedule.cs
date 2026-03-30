using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// Fluent builder for constructing a runtime schedule — the public API that game developers use to register systems and build a <see cref="DagScheduler"/>.
/// </summary>
/// <remarks>
/// <para>
/// Wraps <see cref="DagBuilder"/> with a developer-friendly interface that supports dependency declaration via <c>after:</c>/<c>afterAll:</c>,
/// <c>runIf:</c> predicates, typed event queues, and overload parameters.
/// </para>
/// <para>
/// Usage: <c>RuntimeSchedule.Create().Callback(...).Patate(...).Build(parent)</c>
/// </para>
/// </remarks>
[PublicAPI]
public sealed class RuntimeSchedule
{
    private readonly RuntimeOptions _options;
    private readonly List<SystemRegistration> _registrations = [];
    private readonly List<EventQueueBase> _eventQueues = [];
    private readonly Dictionary<string, List<EventQueueBase>> _produces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<EventQueueBase>> _consumes = new(StringComparer.Ordinal);
    private bool _built;

    private RuntimeSchedule(RuntimeOptions options)
    {
        _options = options ?? new RuntimeOptions();
    }

    /// <summary>
    /// Creates a new runtime schedule builder.
    /// </summary>
    /// <param name="options">Runtime configuration. If null, defaults are used.</param>
    public static RuntimeSchedule Create(RuntimeOptions options = null) => new(options);

    // ═══════════════════════════════════════════════════════════════
    // System registration
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Registers a Callback system — lightweight single-invocation, no entity input.
    /// </summary>
    public RuntimeSchedule Callback(string name, Action<TickContext> action, string after = null, string[] afterAll = null, 
        SystemPriority priority = SystemPriority.Normal, Func<bool> runIf = null)
    {
        ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(action);

        _registrations.Add(new SystemRegistration
        {
            Name = name,
            Type = SystemType.Callback,
            CallbackAction = action,
            Priority = priority,
            RunIf = runIf,
            After = after,
            AfterAll = afterAll
        });
        return this;
    }

    /// <summary>
    /// Registers a Simple system — single-worker entity iteration.
    /// </summary>
    public RuntimeSchedule Simple(string name, Action<TickContext> action, string after = null, string[] afterAll = null, 
        SystemPriority priority = SystemPriority.Normal, Func<bool> runIf = null, int tickDivisor = 1, int throttledTickDivisor = 1, bool canShed = false)
    {
        ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(action);

        _registrations.Add(new SystemRegistration
        {
            Name = name,
            Type = SystemType.Simple,
            CallbackAction = action,
            Priority = priority,
            RunIf = runIf,
            After = after,
            AfterAll = afterAll,
            TickDivisor = tickDivisor,
            ThrottledTickDivisor = throttledTickDivisor,
            CanShed = canShed
        });
        return this;
    }

    /// <summary>
    /// Registers a Patate system — multi-worker chunk-parallel execution.
    /// </summary>
    public RuntimeSchedule Patate(string name, Action<int, int> chunkAction, int totalChunks, string after = null, string[] afterAll = null,
        SystemPriority priority = SystemPriority.Normal, Func<bool> runIf = null, int tickDivisor = 1, int throttledTickDivisor = 1, bool canShed = false)
    {
        ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(chunkAction);

        _registrations.Add(new SystemRegistration
        {
            Name = name,
            Type = SystemType.Patate,
            PatateChunkAction = chunkAction,
            TotalChunks = totalChunks,
            Priority = priority,
            RunIf = runIf,
            After = after,
            AfterAll = afterAll,
            TickDivisor = tickDivisor,
            ThrottledTickDivisor = throttledTickDivisor,
            CanShed = canShed
        });
        return this;
    }

    // ═══════════════════════════════════════════════════════════════
    // Event queues
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a typed event queue for inter-system communication.
    /// </summary>
    /// <typeparam name="T">Event type.</typeparam>
    /// <param name="name">Diagnostic name for the queue.</param>
    /// <param name="capacity">Maximum events per tick. Must be a power of 2.</param>
    public EventQueue<T> CreateEventQueue<T>(string name, int capacity = 1024)
    {
        ThrowIfBuilt();
        var queue = new EventQueue<T>(name, capacity);
        _eventQueues.Add(queue);
        return queue;
    }

    /// <summary>
    /// Declares that a system produces (writes to) the specified event queues.
    /// </summary>
    public RuntimeSchedule Produces(string systemName, params EventQueueBase[] queues)
    {
        ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(systemName);

        if (!_produces.TryGetValue(systemName, out var list))
        {
            list = [];
            _produces[systemName] = list;
        }

        list.AddRange(queues);
        return this;
    }

    /// <summary>
    /// Declares that a system consumes (reads from) the specified event queues.
    /// </summary>
    public RuntimeSchedule Consumes(string systemName, params EventQueueBase[] queues)
    {
        ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(systemName);

        if (!_consumes.TryGetValue(systemName, out var list))
        {
            list = [];
            _consumes[systemName] = list;
        }

        list.AddRange(queues);
        return this;
    }

    // ═══════════════════════════════════════════════════════════════
    // Build
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates the schedule and builds a <see cref="DagScheduler"/>.
    /// </summary>
    /// <param name="parent">Parent resource node (typically <see cref="IResourceRegistry.Runtime"/>).</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>A ready-to-start <see cref="DagScheduler"/>.</returns>
    /// <exception cref="InvalidOperationException">DAG contains a cycle, duplicate names, or invalid references.</exception>
    public DagScheduler Build(IResource parent, ILogger logger = null)
    {
        ThrowIfBuilt();
        _built = true;

        var dagBuilder = new DagBuilder();

        // Phase 1: Register all systems
        foreach (var reg in _registrations)
        {
            switch (reg.Type)
            {
                case SystemType.Callback:
                    dagBuilder.AddCallback(reg.Name, reg.CallbackAction, reg.Priority, reg.RunIf);
                    break;
                case SystemType.Simple:
                    dagBuilder.AddSimple(reg.Name, reg.CallbackAction, reg.Priority, reg.RunIf);
                    break;
                case SystemType.Patate:
                    dagBuilder.AddPatate(reg.Name, reg.PatateChunkAction, reg.TotalChunks, reg.Priority, reg.RunIf);
                    break;
            }
        }

        // Phase 2: Add dependency edges
        foreach (var reg in _registrations)
        {
            if (reg.After != null)
            {
                dagBuilder.AddEdge(reg.After, reg.Name);
            }

            if (reg.AfterAll != null)
            {
                foreach (var dep in reg.AfterAll)
                {
                    dagBuilder.AddEdge(dep, reg.Name);
                }
            }
        }

        // Phase 3: Build DAG (validates acyclicity, computes predecessors/successors)
        var (systems, topologicalOrder) = dagBuilder.Build();

        // Phase 4: Wire event queue indices into SystemDefinitions
        var nameToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var sys in systems)
        {
            nameToIndex[sys.Name] = sys.Index;
        }

        var queueToIndex = new Dictionary<EventQueueBase, int>();
        for (var i = 0; i < _eventQueues.Count; i++)
        {
            queueToIndex[_eventQueues[i]] = i;
        }

        foreach (var (sysName, queues) in _produces)
        {
            if (!nameToIndex.TryGetValue(sysName, out var sysIdx))
            {
                throw new InvalidOperationException($"Produces: system '{sysName}' not found.");
            }

            var indices = new int[queues.Count];
            for (var i = 0; i < queues.Count; i++)
            {
                indices[i] = queueToIndex[queues[i]];
            }

            systems[sysIdx].ProducesQueueIndices = indices;
        }

        foreach (var (sysName, queues) in _consumes)
        {
            if (!nameToIndex.TryGetValue(sysName, out var sysIdx))
            {
                throw new InvalidOperationException($"Consumes: system '{sysName}' not found.");
            }

            var indices = new int[queues.Count];
            for (var i = 0; i < queues.Count; i++)
            {
                indices[i] = queueToIndex[queues[i]];
            }

            systems[sysIdx].ConsumesQueueIndices = indices;
        }

        // Phase 5: Store overload params from registrations
        foreach (var reg in _registrations)
        {
            if (!nameToIndex.TryGetValue(reg.Name, out var sysIdx))
            {
                continue;
            }

            // TickDivisor, ThrottledTickDivisor, CanShed are already set via init properties
            // in DagBuilder. But DagBuilder doesn't pass them through yet, so set them here.
            systems[sysIdx].TickDivisor = reg.TickDivisor;
            systems[sysIdx].ThrottledTickDivisor = reg.ThrottledTickDivisor;
            systems[sysIdx].CanShed = reg.CanShed;
        }

        // Phase 6: Create scheduler
        return new DagScheduler(systems, topologicalOrder, _options, parent, [.. _eventQueues], logger);
    }

    private void ThrowIfBuilt()
    {
        if (_built)
        {
            throw new InvalidOperationException("This schedule has already been built. Create a new RuntimeSchedule.");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Internal registration record
    // ═══════════════════════════════════════════════════════════════

    private sealed class SystemRegistration
    {
        public string Name;
        public SystemType Type;
        public Action<TickContext> CallbackAction;
        public Action<int, int> PatateChunkAction;
        public int TotalChunks = 1;
        public SystemPriority Priority;
        public Func<bool> RunIf;
        public string After;
        public string[] AfterAll;
        public int TickDivisor = 1;
        public int ThrottledTickDivisor = 1;
        public bool CanShed;
    }
}
