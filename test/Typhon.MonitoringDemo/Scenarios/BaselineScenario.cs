using System.Diagnostics;
using Typhon.Engine;

namespace Typhon.MonitoringDemo.Scenarios;

/// <summary>
/// Simple baseline scenario adapted from unit tests.
/// Single-threaded, synchronous, no async - mirrors exactly what unit tests do.
/// Used to verify the DI/container setup works correctly.
/// </summary>
public class BaselineScenario : IScenario
{
    public string Name => "Baseline (Unit Test Style)";
    public string Description => "Single-threaded, synchronous CRUD. Mirrors unit test patterns to verify engine works.";

    public Task RunAsync(TyphonContext context, ScenarioConfig config, ScenarioStats stats, CancellationToken ct)
    {
        var engine = context.Engine;
        var rand = new Random(42);

        int loopCount = 0;

        // Run synchronously on the current thread - exactly like unit tests
        while (!ct.IsCancellationRequested)
        {
            var sw = Stopwatch.GetTimestamp();
            try
            {
                // Pattern 1: Create entity in one transaction, read in another (unit test pattern)
                long entityId;
                {
                    using var t = engine.CreateQuickTransaction();

                    var building = FactoryBuilding.Create(rand, rand.Next(0, 5));
                    entityId = t.CreateEntity(ref building);

                    var committed = t.Commit();
                    if (committed)
                    {
                        stats.RecordCommit();
                        stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                    }
                    else
                    {
                        stats.RecordRollback();
                        stats.RecordFailure(new Exception("Commit returned false"));
                        continue;
                    }
                }

                // Pattern 2: Read the entity back in a new transaction
                sw = Stopwatch.GetTimestamp();
                {
                    using var t = engine.CreateQuickTransaction();

                    var success = t.ReadEntity<FactoryBuilding>(entityId, out var readBuilding);
                    if (success)
                    {
                        stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                    }
                    else
                    {
                        stats.RecordFailure(new Exception($"Failed to read entity {entityId}"));
                        continue;
                    }

                    // Pattern 3: Update the entity
                    sw = Stopwatch.GetTimestamp();
                    readBuilding.Progress = (readBuilding.Progress + 0.1f) % 1.0f;
                    t.UpdateEntity(entityId, ref readBuilding);

                    var committed = t.Commit();
                    if (committed)
                    {
                        stats.RecordCommit();
                        stats.RecordSuccess((Stopwatch.GetTimestamp() - sw) * 1_000_000 / Stopwatch.Frequency);
                    }
                    else
                    {
                        stats.RecordRollback();
                        stats.RecordFailure(new Exception("Update commit returned false"));
                    }
                }
            }
            catch (Exception ex)
            {
                stats.RecordFailure(ex);
            }

            ++loopCount;
        }

        return Task.CompletedTask;
    }
}
