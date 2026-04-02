using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// Non-generic base class for typed event queues. Allows the scheduler to reset all queues at tick start without knowing their generic type.
/// </summary>
[PublicAPI]
public abstract class EventQueueBase
{
    /// <summary>Name of this event queue (for diagnostics).</summary>
    public abstract string Name { get; }

    /// <summary>Number of items currently in the queue.</summary>
    public abstract int Count { get; }

    /// <summary>True if the queue has no items.</summary>
    public abstract bool IsEmpty { get; }

    /// <summary>Resets the queue to empty. Called at the start of each tick.</summary>
    public abstract void Reset();
}

/// <summary>
/// Typed event queue for inter-system communication. Producer systems push events;
/// consumer systems drain them. DAG ordering guarantees producer completes before consumer starts, so no concurrent access occurs — this is a simple SPSC buffer.
/// </summary>
/// <remarks>
/// <para>
/// Capacity must be a power of 2. If the queue fills up, <see cref="Push"/> throws.
/// The queue is reset at the start of each tick by the scheduler.
/// </para>
/// <para>
/// Push is O(1). Drain copies all items to the output span and resets the count.
/// </para>
/// </remarks>
/// <typeparam name="T">The event type. No constraints — can be any struct or class.</typeparam>
[PublicAPI]
public sealed class EventQueue<T> : EventQueueBase
{
    private readonly T[] _buffer;
    private readonly int _capacity;
    private int _count;

    /// <summary>
    /// Creates a new event queue with the specified capacity.
    /// </summary>
    /// <param name="name">Diagnostic name for this queue.</param>
    /// <param name="capacity">Maximum number of events per tick. Must be a power of 2.</param>
    public EventQueue(string name, int capacity = 1024)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (capacity < 1 || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentException("Capacity must be a power of 2.", nameof(capacity));
        }

        Name = name;
        _capacity = capacity;
        _buffer = new T[capacity];
    }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override int Count => _count;

    /// <inheritdoc />
    public override bool IsEmpty => _count == 0;

    /// <summary>
    /// Pushes an event into the queue. Called by producer systems during tick execution.
    /// </summary>
    /// <param name="item">The event to enqueue.</param>
    /// <exception cref="InvalidOperationException">The queue is full.</exception>
    public void Push(T item)
    {
        if (_count >= _capacity)
        {
            throw new InvalidOperationException($"Event queue '{Name}' is full (capacity: {_capacity}).");
        }

        _buffer[_count++] = item;
    }

    /// <summary>
    /// Drains all events into the output span. Returns the number of events copied.
    /// After drain, the queue is empty.
    /// </summary>
    /// <param name="output">Destination span. Must be large enough to hold all events.</param>
    /// <returns>Number of events copied.</returns>
    public int Drain(Span<T> output)
    {
        var count = _count;
        if (count == 0)
        {
            return 0;
        }

        _buffer.AsSpan(0, count).CopyTo(output);
        _count = 0;

        // Clear references to allow GC collection (for reference types)
        if (!typeof(T).IsValueType)
        {
            Array.Clear(_buffer, 0, count);
        }

        return count;
    }

    /// <summary>
    /// Returns a read-only span over the current queue contents without draining.
    /// </summary>
    public ReadOnlySpan<T> AsSpan() => _buffer.AsSpan(0, _count);

    /// <inheritdoc />
    public override void Reset()
    {
        if (!typeof(T).IsValueType && _count > 0)
        {
            Array.Clear(_buffer, 0, _count);
        }

        _count = 0;
    }
}
