using System.Reflection;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.Workbench.Schema;

/// <summary>
/// Attempts to register component types from a <see cref="LoadedSchema"/> against a running
/// <see cref="DatabaseEngine"/> and classifies the outcome as one of Ready / MigrationRequired /
/// Incompatible. Stops at the first failure so we don't leave the engine in a half-registered
/// state — the session is already non-functional at that point.
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
        var aggregate = State.Ready;
        var registered = 0;

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
                aggregate = State.Incompatible;
                break;
            }
            catch (SchemaValidationException sv)
            {
                diagnostics.Add(new Diagnostic(name, "breaking_change", sv.Diff.FormatDetailedMessage()));
                if (aggregate != State.Incompatible)
                {
                    aggregate = State.MigrationRequired;
                }
                break;
            }
            catch (SchemaMigrationException sm)
            {
                diagnostics.Add(new Diagnostic(name, "migration_failed", sm.Message));
                aggregate = State.Incompatible;
                break;
            }
            catch (Exception ex)
            {
                diagnostics.Add(new Diagnostic(name, "schema_error", ex.Message));
                aggregate = State.Incompatible;
                break;
            }
        }

        return new Result(aggregate, diagnostics.ToArray(), registered);
    }
}
