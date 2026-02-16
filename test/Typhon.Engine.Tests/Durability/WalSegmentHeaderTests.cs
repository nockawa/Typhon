using NUnit.Framework;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for <see cref="WalSegmentHeader"/> layout, initialization, and CRC validation.
/// </summary>
[TestFixture]
public class WalSegmentHeaderTests
{
    #region Layout

    [Test]
    public void Layout_SizeIs4096Bytes()
    {
        Assert.That(Unsafe.SizeOf<WalSegmentHeader>(), Is.EqualTo(4096));
        Assert.That(WalSegmentHeader.SizeInBytes, Is.EqualTo(4096));
    }

    [Test]
    public void Layout_MagicIsFirst4Bytes()
    {
        Assert.That(Marshal.OffsetOf<WalSegmentHeader>(nameof(WalSegmentHeader.Magic)).ToInt32(), Is.EqualTo(0));
    }

    [Test]
    public void Layout_VersionIsAt4()
    {
        Assert.That(Marshal.OffsetOf<WalSegmentHeader>(nameof(WalSegmentHeader.Version)).ToInt32(), Is.EqualTo(4));
    }

    [Test]
    public void Layout_SegmentIdIsAt8()
    {
        Assert.That(Marshal.OffsetOf<WalSegmentHeader>(nameof(WalSegmentHeader.SegmentId)).ToInt32(), Is.EqualTo(8));
    }

    [Test]
    public void Layout_FirstLsnIsAt16()
    {
        Assert.That(Marshal.OffsetOf<WalSegmentHeader>(nameof(WalSegmentHeader.FirstLSN)).ToInt32(), Is.EqualTo(16));
    }

    [Test]
    public void Layout_PrevSegmentLsnIsAt24()
    {
        Assert.That(Marshal.OffsetOf<WalSegmentHeader>(nameof(WalSegmentHeader.PrevSegmentLSN)).ToInt32(), Is.EqualTo(24));
    }

    [Test]
    public void Layout_SegmentSizeIsAt32()
    {
        Assert.That(Marshal.OffsetOf<WalSegmentHeader>(nameof(WalSegmentHeader.SegmentSize)).ToInt32(), Is.EqualTo(32));
    }

    [Test]
    public void Layout_HeaderCrcIsAt36()
    {
        Assert.That(Marshal.OffsetOf<WalSegmentHeader>(nameof(WalSegmentHeader.HeaderCRC)).ToInt32(), Is.EqualTo(36));
        Assert.That(WalSegmentHeader.HeaderCrcOffset, Is.EqualTo(36));
    }

    #endregion

    #region Initialize

    [Test]
    public void Initialize_SetsAllFields()
    {
        var header = new WalSegmentHeader();
        header.Initialize(segmentId: 42, firstLsn: 1000, prevSegmentLsn: 999, segmentSize: 64 * 1024 * 1024);

        Assert.That(header.Magic, Is.EqualTo(WalSegmentHeader.MagicValue));
        Assert.That(header.Version, Is.EqualTo(WalSegmentHeader.CurrentVersion));
        Assert.That(header.SegmentId, Is.EqualTo(42));
        Assert.That(header.FirstLSN, Is.EqualTo(1000));
        Assert.That(header.PrevSegmentLSN, Is.EqualTo(999));
        Assert.That(header.SegmentSize, Is.EqualTo(64u * 1024 * 1024));
        Assert.That(header.HeaderCRC, Is.EqualTo(0u));
    }

    [Test]
    public void Initialize_MagicValueMatchesTYFW()
    {
        // "TYFW" as little-endian uint32
        // T=0x54, Y=0x59, F=0x46, W=0x57 → stored as 0x57464954 in LE? No:
        // The magic is defined as 0x54594657 which in LE bytes is: 57 46 59 54 → "WFYT"
        // This is the convention from the design doc: 0x54594657
        Assert.That(WalSegmentHeader.MagicValue, Is.EqualTo(0x54594657u));
    }

    #endregion

    #region CRC

    [Test]
    public void ComputeAndSetCrc_ProducesNonZeroCrc()
    {
        var header = new WalSegmentHeader();
        header.Initialize(1, 1, 0, 64 * 1024 * 1024);

        header.ComputeAndSetCrc();

        Assert.That(header.HeaderCRC, Is.Not.EqualTo(0u));
    }

    [Test]
    public void Validate_ValidHeader_ReturnsTrue()
    {
        var header = new WalSegmentHeader();
        header.Initialize(1, 1, 0, 64 * 1024 * 1024);
        header.ComputeAndSetCrc();

        Assert.That(header.Validate(), Is.True);
    }

    [Test]
    public void Validate_CorruptedMagic_ReturnsFalse()
    {
        var header = new WalSegmentHeader();
        header.Initialize(1, 1, 0, 64 * 1024 * 1024);
        header.ComputeAndSetCrc();

        header.Magic = 0xDEADBEEF;

        Assert.That(header.Validate(), Is.False);
    }

    [Test]
    public void Validate_CorruptedVersion_ReturnsFalse()
    {
        var header = new WalSegmentHeader();
        header.Initialize(1, 1, 0, 64 * 1024 * 1024);
        header.ComputeAndSetCrc();

        header.Version = 99;

        Assert.That(header.Validate(), Is.False);
    }

    [Test]
    public void Validate_CorruptedData_ReturnsFalse()
    {
        var header = new WalSegmentHeader();
        header.Initialize(1, 1, 0, 64 * 1024 * 1024);
        header.ComputeAndSetCrc();

        // Corrupt a data field without changing CRC
        header.SegmentId = 999;

        Assert.That(header.Validate(), Is.False);
    }

    [Test]
    public void Validate_NoCrc_ReturnsFalse()
    {
        var header = new WalSegmentHeader();
        header.Initialize(1, 1, 0, 64 * 1024 * 1024);
        // Don't call ComputeAndSetCrc — HeaderCRC stays 0

        // Should fail because the computed CRC won't match 0
        // (Unless the data happens to CRC to 0, which is astronomically unlikely)
        Assert.That(header.Validate(), Is.False);
    }

    [Test]
    public void ComputeAndSetCrc_DifferentData_DifferentCrc()
    {
        var header1 = new WalSegmentHeader();
        header1.Initialize(1, 1, 0, 64 * 1024 * 1024);
        header1.ComputeAndSetCrc();

        var header2 = new WalSegmentHeader();
        header2.Initialize(2, 100, 99, 128 * 1024 * 1024);
        header2.ComputeAndSetCrc();

        Assert.That(header1.HeaderCRC, Is.Not.EqualTo(header2.HeaderCRC));
    }

    [Test]
    public void ComputeAndSetCrc_Idempotent()
    {
        var header = new WalSegmentHeader();
        header.Initialize(1, 1, 0, 64 * 1024 * 1024);

        header.ComputeAndSetCrc();
        var crc1 = header.HeaderCRC;

        header.ComputeAndSetCrc();
        var crc2 = header.HeaderCRC;

        Assert.That(crc1, Is.EqualTo(crc2));
    }

    #endregion
}
