using System.Linq;
using NUnit.Framework;
using Typhon.Engine.Profiler.Generated;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Validates the build-time output of <c>SourceLocationGenerator</c>: the generated <c>SourceLocations</c>
/// table inside Typhon.Engine. These tests run against the *already-compiled* engine assembly, so they
/// exercise the generator's output as the runtime sees it.
///
/// Phase 1 vertical slice: only the <c>BeginBTreeInsert</c> factory has a corresponding
/// <c>BeginBTreeInsertWithSiteId</c> overload, so only its call sites get attributed in this slice.
/// Other Begin* call sites are skipped (no WithSiteId factory yet) and contribute to the "skipped"
/// count in the generator's TPH9000 build summary.
/// </summary>
[TestFixture]
public class SourceLocationGeneratorTests
{
    [Test]
    public void Generator_EmitsAtLeastOneAttributedSite()
    {
        Assert.That(SourceLocations.All.Length, Is.GreaterThanOrEqualTo(1),
            "Phase 1 vertical slice: at least the single BeginBTreeInsert call site at "
            + "src/Typhon.Engine/Data/Index/BTree.cs:1124 should be attributed.");
    }

    [Test]
    public void Generator_AssignsIdsStartingAtOne()
    {
        // Id 0 is reserved for "unknown source"; entries start at 1.
        Assert.That(SourceLocations.All[0].Id, Is.EqualTo(1));
    }

    [Test]
    public void Generator_FilesArrayUsesRepoRelativePaths()
    {
        // PathMap in Directory.Build.props should rewrite absolute build-machine paths to "/_/..." form.
        Assert.That(SourceLocations.Files, Is.Not.Empty);
        foreach (var file in SourceLocations.Files)
        {
            Assert.That(file, Does.StartWith("/_/"),
                $"Expected repo-relative path with /_/ prefix, got: {file}");
        }
    }

    [Test]
    public void Generator_BTreeInsertSiteIsAttributed()
    {
        // The vertical slice's signature attribution: BTree.cs:1124 calling BeginBTreeInsert.
        var btreeFile = SourceLocations.Files
            .Select((path, idx) => (path, idx))
            .FirstOrDefault(t => t.path.EndsWith("Data/Index/BTree.cs"));
        Assert.That(btreeFile.path, Is.Not.Null,
            "Expected /_/src/Typhon.Engine/Data/Index/BTree.cs in the Files table.");

        var entry = SourceLocations.All.FirstOrDefault(e => e.FileId == btreeFile.idx);
        Assert.That(entry.Id, Is.GreaterThan((ushort)0),
            "Expected at least one attributed site in BTree.cs.");
        Assert.That(entry.Line, Is.GreaterThan(0));
    }

    [Test]
    public void Generator_EntriesAreSortedDeterministically()
    {
        // Per design §4.4: deterministic IDs require the generator to sort by (filePath, line, column)
        // before assigning ids. Verify the table reads in non-decreasing (file, line) order.
        for (int i = 1; i < SourceLocations.All.Length; i++)
        {
            var prev = SourceLocations.All[i - 1];
            var curr = SourceLocations.All[i];
            if (prev.FileId == curr.FileId)
            {
                Assert.That(curr.Line, Is.GreaterThanOrEqualTo(prev.Line),
                    $"Entries within the same file must be in line order. "
                    + $"Entry {i - 1} (FileId={prev.FileId}, Line={prev.Line}) → entry {i} (Line={curr.Line}).");
            }
            else
            {
                Assert.That(curr.FileId, Is.GreaterThan(prev.FileId),
                    "FileIds must be monotonically increasing in the entry table.");
            }
        }
    }
}
