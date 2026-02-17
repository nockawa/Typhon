using NUnit.Framework;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Tests;

/// <summary>
/// Verifies binary layout, size, and default values of <see cref="PageBaseHeader"/>
/// to catch any accidental struct changes that would break the on-disk page format.
/// </summary>
[TestFixture]
public class PageBaseHeaderTests
{
    #region Size

    [Test]
    public void Size_Is16Bytes() =>
        Assert.That(Unsafe.SizeOf<PageBaseHeader>(), Is.EqualTo(16));

    [Test]
    public void Size_FitsWithinReservation() =>
        Assert.That(PageBaseHeader.Size, Is.LessThanOrEqualTo(PagedMMF.PageBaseHeaderSize));

    #endregion

    #region Constants

    [Test]
    public void PageChecksumOffset_IsCorrect() =>
        Assert.That(PageBaseHeader.PageChecksumOffset, Is.EqualTo(8));

    [Test]
    public void PageChecksumSize_IsCorrect() =>
        Assert.That(PageBaseHeader.PageChecksumSize, Is.EqualTo(4));

    #endregion

    #region Field Offsets

    [Test]
    public void FieldOffset_Flags_Is0() =>
        Assert.That(Marshal.OffsetOf<PageBaseHeader>(nameof(PageBaseHeader.Flags)).ToInt32(), Is.EqualTo(0));

    [Test]
    public void FieldOffset_Type_Is1() =>
        Assert.That(Marshal.OffsetOf<PageBaseHeader>(nameof(PageBaseHeader.Type)).ToInt32(), Is.EqualTo(1));

    [Test]
    public void FieldOffset_FormatRevision_Is2() =>
        Assert.That(Marshal.OffsetOf<PageBaseHeader>(nameof(PageBaseHeader.FormatRevision)).ToInt32(), Is.EqualTo(2));

    [Test]
    public void FieldOffset_ChangeRevision_Is4() =>
        Assert.That(Marshal.OffsetOf<PageBaseHeader>(nameof(PageBaseHeader.ChangeRevision)).ToInt32(), Is.EqualTo(4));

    [Test]
    public void FieldOffset_PageChecksum_Is8() =>
        Assert.That(Marshal.OffsetOf<PageBaseHeader>(nameof(PageBaseHeader.PageChecksum)).ToInt32(), Is.EqualTo(8));

    [Test]
    public void FieldOffset_ModificationCounter_Is12() =>
        Assert.That(Marshal.OffsetOf<PageBaseHeader>(nameof(PageBaseHeader.ModificationCounter)).ToInt32(), Is.EqualTo(12));

    #endregion

    #region Default Values (zero sentinel)

    [Test]
    public void NewHeader_HasZeroChecksum()
    {
        var header = new PageBaseHeader();
        Assert.That(header.PageChecksum, Is.EqualTo(0u));
    }

    [Test]
    public void NewHeader_HasZeroModificationCounter()
    {
        var header = new PageBaseHeader();
        Assert.That(header.ModificationCounter, Is.EqualTo(0));
    }

    #endregion

    #region Layout Verification via Pointer Arithmetic

    [Test]
    public unsafe void FieldLayout_MatchesExpectedOffsets()
    {
        var header = new PageBaseHeader
        {
            Flags = PageBlockFlags.IsLogicalSegment,
            Type = PageBlockType.OccupancyMap,
            FormatRevision = 42,
            ChangeRevision = 12345,
            PageChecksum = 0xDEADBEEF,
            ModificationCounter = 7
        };

        byte* p = (byte*)&header;

        // Flags at offset 0 (1 byte)
        Assert.That(*(PageBlockFlags*)p, Is.EqualTo(PageBlockFlags.IsLogicalSegment));

        // Type at offset 1 (1 byte)
        Assert.That(*(PageBlockType*)(p + 1), Is.EqualTo(PageBlockType.OccupancyMap));

        // FormatRevision at offset 2 (2 bytes)
        Assert.That(*(short*)(p + 2), Is.EqualTo(42));

        // ChangeRevision at offset 4 (4 bytes)
        Assert.That(*(int*)(p + 4), Is.EqualTo(12345));

        // PageChecksum at offset 8 (4 bytes)
        Assert.That(*(uint*)(p + 8), Is.EqualTo(0xDEADBEEF));

        // ModificationCounter at offset 12 (4 bytes)
        Assert.That(*(int*)(p + 12), Is.EqualTo(7));
    }

    #endregion
}
