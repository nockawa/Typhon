using System.Reflection;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.Workbench.Schema;

/// <summary>
/// Attempts to register component types from a <see cref="LoadedSchema"/> against a running
/// <see cref="DatabaseEngine"/> and classifies the outcome as one of Ready / MigrationRequired /
/// Incompatible.
///
/// <para>Each component is registered independently — a failure on one (e.g., a test-only
/// <c>TransientBad</c> fixture with an unsupported attribute combination, or a schema name
/// collision) does NOT abort the rest. The engine remains usable for components that DO load
/// cleanly; the aggregate <see cref="State"/> still reflects the worst-case outcome so the UI
/// can surface a Migration/Incompatibility banner while still populating the Schema Inspector
/// with the components that did succeed.</para>
/// </summary>
public static class SchemaCompatibility
{
    public enum State
    {
        Ready,
        MigrationRequired,
        Incompatible,
    }

    public sealed record Diagnostic(string ComponentName, string Kind, string Detail);

    public sealed record Result(State State, Diagnostic[] Diagnostics, int RegisteredCount);

    public static Result ClassifyAndRegister(DatabaseEngine engine, LoadedSchema loaded)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(loaded);

        if (loaded.ComponentTypes.Length == 0)
        {
            return new Result(State.Ready, [], 0);
        }

        var diagnostics = new List<Diagnostic>();
        var registered = 0;
        var hadDowngrade = false;
        var hadMigrationFailed = false;
        var hadBreakingChange = false;
        var hadOther = false;

        foreach (var type in loaded.ComponentTypes)
        {
            var name = type.GetCustomAttribute<ComponentAttribute>()?.Name ?? type.Name;
            try
            {
                engine.RegisterComponentByType(type, schemaValidation: SchemaValidationMode.Enforce);
                registered++;
            }
            catch (SchemaDowngradeException sd)
            {
                diagnostics.Add(new Diagnostic(name, "schema_downgrade", sd.Message));
                hadDowngrade = true;
            }
            catch (SchemaValidationException sv)
            {
                diagnostics.Add(new Diagnostic(name, "breaking_change", sv.Diff.FormatDetailedMessage()));
                hadBreakingChange = true;
            }
            catch (SchemaMigrationException sm)
            {
                diagnostics.Add(new Diagnostic(name, "migration_failed", sm.Message));
                hadMigrationFailed = true;
            }
            catch (Exception ex)
            {
                diagnostics.Add(new Diagnostic(name, "schema_error", ex.Message));
                hadOther = true;
            }
        }

        var state = ClassifyAggregate(registered, hadDowngrade, hadMigrationFailed, hadBreakingChange, hadOther);
        return new Result(state, diagnostics.ToArray(), registered);
    }

    /// <summary>
    /// Classify the overall session state from per-component outcomes. Key principle: <see cref="State.Incompatible"/>
    /// means "the session is unusable" — reserve it for <b>total</b> failure or unrecoverable errors (downgrade /
    /// migration-failed) which imply the on-disk data is mismatched vs. binaries in ways the user cannot navigate
    /// around. A mix of successes + per-component errors is <see cref="State.MigrationRequired"/>: the UI can still
    /// show the components that loaded while warning about the ones that didn't.
    /// </summary>
    private static State ClassifyAggregate(
        int registered,
        bool hadDowngrade,
        bool hadMigrationFailed,
        bool hadBreakingChange,
        bool hadOther)
    {
        // Catastrophic kinds: the session is not navigable regardless of how many siblings loaded.
        if (hadDowngrade || hadMigrationFailed)
        {
            return State.Incompatible;
        }
        // Total failure: no component registered at all.
        if (registered == 0 && (hadBreakingChange || hadOther))
        {
            return State.Incompatible;
        }
        // Partial: at least one succeeded, but something needs attention.
        if (hadBreakingChange || hadOther)
        {
            return State.MigrationRequired;
        }
        return State.Ready;
    }
}
