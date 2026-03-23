using JetBrains.Annotations;

namespace Typhon.Schema.Definition;

/// <summary>
/// Determines how a component's data is stored, persisted, and recovered.
/// Immutable per ComponentTable after registration.
/// </summary>
[PublicAPI]
public enum StorageMode : byte
{
    /// <summary>Full MVCC snapshot isolation, WAL per-transaction, crash recovery to exact pre-crash state.</summary>
    Versioned = 0,

    /// <summary>In-place writes (last-writer-wins), tick-fence WAL durability, crash recovery to last tick boundary.</summary>
    SingleVersion = 1,

    /// <summary>Heap memory only — no persistence, no WAL, all data lost on crash. Developer owns concurrency.</summary>
    Transient = 2,
}
