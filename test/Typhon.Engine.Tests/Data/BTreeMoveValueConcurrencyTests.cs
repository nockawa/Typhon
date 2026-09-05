using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>BTree.MoveValue</c> — the AllowMultiple key-change — under concurrent writers, with a CENSUS oracle rather than a presence probe.
/// </summary>
/// <remarks>
/// <para>
/// This is the operation the tick fence's shadow drain runs, and since #886 sliced Prep it runs from W workers at once.
/// <c>OlcBTreeStressTests.Stress_MoveValueTailConsistency</c> already hammers it, but its assertion is only "the destination key is readable" — an element
/// removed from its old buffer and never appended to the new one passes that test. #887 sees exactly that shape at the fence: an entity the index no longer
/// lists, and a leaf entry naming a slot nobody occupies.
/// </para>
/// <para>
/// So this fixture owns the whole population: every (key, value) pair is moved by exactly one thread, to a destination other threads also write, and the final
/// census must be the permutation those moves define — every value present, exactly once, under the key its move named.
/// </para>
/// <para>
/// It reproduces in about two runs in three, so the quarantined arm's PASS carries no information and its FAIL carries all of it. The serial arm exists to
/// keep that honest: it runs the identical code on one thread and must always pass, so a day when both go green is a day the tree changed, not a day the
/// race hid.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class BTreeMoveValueConcurrencyTests
{
    private IServiceProvider _serviceProvider;

    private const int KeyCount = 64;
    private const int ValuesPerKey = 8;

    // Oversubscribed on purpose, for the reason OlcBTreeStressTests scales with the box: the interleavings that matter happen when a thread is preempted
    // inside the optimistic read/validate window.
    private static readonly int Threads = Math.Clamp(Environment.ProcessorCount * 2, 8, 32);

    [SetUp]
    public void Setup()
    {
        var name = $"btmvc_{TestContext.CurrentContext.Test.Name}";
        var services = new ServiceCollection();
        services
            .AddLogging()
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = name[..Math.Min(63, name.Length)];
                options.DatabaseCacheSize = (ulong)(PagedMMF.MinimumMemPageCount * PagedMMF.PageSize);
            });

        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    /// <summary>The destination of a value: a rotation, so every key is both a source and a destination and the leaves overlap.</summary>
    private static int DestinationOf(int key) => 1 + (key + 7) % KeyCount;

    /// <summary>
    /// The control arm and the case under test share every line: one thread must produce the exact census the oracle predicts, which is what makes a failure
    /// at <c>Threads</c> a CONCURRENCY defect rather than a wrong expectation.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void SerialMoveValue_KeepsEveryElement_UnderItsNewKey() => RunMoveCensus(1);

    /// <summary>
    /// 🔴 Red against #887 and quarantined, at a MEASURED 8 failures in 12 on a 16-core box (~100 ms per run) — not every run, so a single green run proves
    /// nothing and must not be read as a fix. When it fails it fails large: whole runs of one key's values gone.
    /// </summary>
    // #887 — BTree.MoveValue loses elements under concurrent writers
    [Test]
    [Category("Quarantine")]
    [CancelAfter(30_000)]
    public void ConcurrentMoveValue_KeepsEveryElement_UnderItsNewKey() => RunMoveCensus(Threads);

    private unsafe void RunMoveCensus(int threadCount)
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));

        var depth = epochManager.EnterScope();
        try
        {
            var tree = new IntMultipleBTree<PersistentStore>(segment);
            var setup = segment.CreateChunkAccessor();
            var elementIds = new int[KeyCount, ValuesPerKey];
            for (var k = 0; k < KeyCount; k++)
            {
                for (var v = 0; v < ValuesPerKey; v++)
                {
                    elementIds[k, v] = tree.Add(k + 1, ((k + 1) * 1000) + v, ref setup);
                }
            }

            setup.Dispose();

            // Every (key, value) pair belongs to exactly one thread, so a negative return is a LOST move, not a benign race with a peer.
            var pairs = new List<(int Key, int Value, int ElementId)>(KeyCount * ValuesPerKey);
            for (var k = 0; k < KeyCount; k++)
            {
                for (var v = 0; v < ValuesPerKey; v++)
                {
                    pairs.Add((k + 1, ((k + 1) * 1000) + v, elementIds[k, v]));
                }
            }

            var notFound = 0;
            using var barrier = new Barrier(threadCount);
            var tasks = new Task[threadCount];
            for (var t = 0; t < threadCount; t++)
            {
                var threadId = t;
                tasks[t] = Task.Factory.StartNew(() =>
                {
                    var scope = epochManager.EnterScope();
                    var accessor = segment.CreateChunkAccessor();
                    try
                    {
                        barrier.SignalAndWait();
                        for (var i = threadId; i < pairs.Count; i += threadCount)
                        {
                            var (key, value, elementId) = pairs[i];
                            if (tree.MoveValue(key, DestinationOf(key), elementId, value, ref accessor, out _, out _) < 0)
                            {
                                Interlocked.Increment(ref notFound);
                            }
                        }
                    }
                    finally
                    {
                        // In a finally, not at the end of the try: a throw from MoveValue would otherwise leak this accessor's slot refcounts and the next
                        // assertion would be about the leak rather than about the tree.
                        accessor.Dispose();
                        epochManager.ExitScope(scope);
                    }
                }, TaskCreationOptions.LongRunning);
            }

            Task.WaitAll(tasks);

            // The census: what the tree holds, walked the two-level way EnumerateRangeMultiple requires.
            var actual = new Dictionary<int, List<int>>();
            var e = tree.EnumerateRangeMultiple(int.MinValue, int.MaxValue);
            while (e.MoveNextKey())
            {
                do
                {
                    var values = e.CurrentValues;
                    for (var i = 0; i < values.Length; i++)
                    {
                        (actual.TryGetValue(e.CurrentKey, out var list) ? list : actual[e.CurrentKey] = []).Add(values[i]);
                    }
                }
                while (e.NextChunk());
            }

            var problems = new List<string>();
            var seen = new HashSet<int>();
            foreach (var kv in actual)
            {
                foreach (var value in kv.Value)
                {
                    if (!seen.Add(value))
                    {
                        problems.Add($"value {value} appears more than once (last under key {kv.Key})");
                    }

                    var wanted = DestinationOf(value / 1000);
                    if (kv.Key != wanted)
                    {
                        problems.Add($"value {value} is listed under key {kv.Key}, but its move named {wanted}");
                    }
                }
            }

            foreach (var (key, value, _) in pairs)
            {
                if (!seen.Contains(value))
                {
                    problems.Add($"value {value} (moved {key} -> {DestinationOf(key)}) is GONE from the tree");
                }
            }

            Assert.Multiple(() =>
            {
                Assert.That(notFound, Is.EqualTo(0), "every pair is owned by exactly one thread, so no move may report the element missing");
                Assert.That(problems, Is.Empty, $"the tree lost or misplaced elements ({problems.Count} findings):{Environment.NewLine}"
                    + string.Join(Environment.NewLine, problems.GetRange(0, Math.Min(20, problems.Count))));
                Assert.That(seen.Count, Is.EqualTo(KeyCount * ValuesPerKey), "the census must hold every element the setup inserted");
            });
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }
}
