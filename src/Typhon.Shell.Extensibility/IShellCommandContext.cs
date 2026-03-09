using System.Collections.Generic;
using Typhon.Engine;

namespace Typhon.Shell.Extensibility;

/// <summary>
/// Context provided to extension commands. Exposes engine, transaction management,
/// and basic session state without coupling to shell internals.
/// </summary>
public interface IShellCommandContext
{
    /// <summary>The active database engine instance.</summary>
    DatabaseEngine Engine { get; }

    /// <summary>The current transaction, or null if none is active.</summary>
    Transaction CurrentTransaction { get; }

    /// <summary>Create or reuse a transaction (respects auto-commit setting).</summary>
    Transaction GetOrCreateTransaction(out bool isAutoCommit);

    /// <summary>Begin a new explicit transaction.</summary>
    Transaction BeginTransaction();

    /// <summary>Commit the current transaction. Returns false if commit fails.</summary>
    bool CommitTransaction();

    /// <summary>Rollback the current transaction.</summary>
    void RollbackTransaction();

    /// <summary>Mark the session as having uncommitted changes.</summary>
    void MarkDirty();

    /// <summary>Whether a database is currently open.</summary>
    bool IsOpen { get; }

    /// <summary>Current output format setting (table, json, csv, etc.).</summary>
    string Format { get; }

    /// <summary>Whether verbose output is enabled.</summary>
    bool Verbose { get; }

    /// <summary>Loaded component .NET types by component name.</summary>
    IReadOnlyDictionary<string, System.Type> ComponentTypes { get; }
}
