using NUnit.Framework;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Tests;

/// <summary>
/// Verifies binary layout, size, and alignment of <see cref="WalRecordHeader"/>
/// and <see cref="WalFrameHeader"/> to catch any accidental struct changes
/// that would break the on-disk WAL format.
/// </summary>
[TestFixture]
public class WalRecordHeaderTests
{
    #region WalRecordHeader — Size

    [Test]
    public void SizeOf_Is48Bytes()
    {
        Assert.That(Unsafe.SizeOf<WalRecordHeader>(), Is.EqualTo(48));
        Assert.That(Unsafe.SizeOf<WalRecordHeader>(), Is.EqualTo(WalRecordHeader.SizeInBytes));
    }

    #endregion

    #region WalRecordHeader — Field Offsets

    [Test]
    public void FieldOffset_LSN_Is0() =>
        Assert.That(Marshal.OffsetOf<WalRecordHeader>(nameof(WalRecordHeader.LSN)).ToInt32(), Is.EqualTo(0));

    [Test]
    public void FieldOffset_TransactionTSN_Is8() =>
        Assert.That(Marshal.OffsetOf<WalRecordHeader>(nameof(WalRecordHeader.TransactionTSN)).ToInt32(), Is.EqualTo(8));

    [Test]
    public void FieldOffset_TotalRecordLength_Is16() =>
        Assert.That(Marshal.OffsetOf<WalRecordHeader>(nameof(WalRecordHeader.TotalRecordLength)).ToInt32(), Is.EqualTo(16));

    [Test]
    public void FieldOffset_UowEpoch_Is20() =>
        Assert.That(Marshal.OffsetOf<WalRecordHeader>(nameof(WalRecordHeader.UowEpoch)).ToInt32(), Is.EqualTo(20));

    [Test]
    public void FieldOffset_ComponentTypeId_Is22() =>
        Assert.That(Marshal.OffsetOf<WalRecordHeader>(nameof(WalRecordHeader.ComponentTypeId)).ToInt32(), Is.EqualTo(22));

    [Test]
    public void FieldOffset_EntityId_Is24() =>
        Assert.That(Marshal.OffsetOf<WalRecordHeader>(nameof(WalRecordHeader.EntityId)).ToInt32(), Is.EqualTo(24));

    [Test]
    public void FieldOffset_PayloadLength_Is32() =>
        Assert.That(Marshal.OffsetOf<WalRecordHeader>(nameof(WalRecordHeader.PayloadLength)).ToInt32(), Is.EqualTo(32));

    [Test]
    public void FieldOffset_OperationType_Is34() =>
        Assert.That(Marshal.OffsetOf<WalRecordHeader>(nameof(WalRecordHeader.OperationType)).ToInt32(), Is.EqualTo(34));

    [Test]
    public void FieldOffset_Flags_Is35() =>
        Assert.That(Marshal.OffsetOf<WalRecordHeader>(nameof(WalRecordHeader.Flags)).ToInt32(), Is.EqualTo(35));

    [Test]
    public void FieldOffset_PrevCRC_Is36() =>
        Assert.That(Marshal.OffsetOf<WalRecordHeader>(nameof(WalRecordHeader.PrevCRC)).ToInt32(), Is.EqualTo(36));

    [Test]
    public void FieldOffset_CRC_Is40() =>
        Assert.That(Marshal.OffsetOf<WalRecordHeader>(nameof(WalRecordHeader.CRC)).ToInt32(), Is.EqualTo(40));

    [Test]
    public void FieldOffset_Reserved_Is44() =>
        Assert.That(Marshal.OffsetOf<WalRecordHeader>(nameof(WalRecordHeader.Reserved)).ToInt32(), Is.EqualTo(44));

    #endregion

    #region WalFrameHeader — Size

    [Test]
    public void FrameHeader_SizeOf_Is8Bytes()
    {
        Assert.That(Unsafe.SizeOf<WalFrameHeader>(), Is.EqualTo(8));
        Assert.That(Unsafe.SizeOf<WalFrameHeader>(), Is.EqualTo(WalFrameHeader.SizeInBytes));
    }

    #endregion

    #region WalRecordFlags

    [Test]
    public void Flags_CombineCorrectly()
    {
        var flags = WalRecordFlags.UowBegin | WalRecordFlags.Compressed;

        Assert.That(flags.HasFlag(WalRecordFlags.UowBegin), Is.True);
        Assert.That(flags.HasFlag(WalRecordFlags.Compressed), Is.True);
        Assert.That(flags.HasFlag(WalRecordFlags.UowCommit), Is.False);
    }

    [Test]
    public void Flags_None_IsZero() =>
        Assert.That((byte)WalRecordFlags.None, Is.EqualTo(0));

    #endregion

    #region WalOperationType

    [Test]
    public void OperationType_Values_MatchSpec()
    {
        Assert.That((byte)WalOperationType.Create, Is.EqualTo(1));
        Assert.That((byte)WalOperationType.Update, Is.EqualTo(2));
        Assert.That((byte)WalOperationType.Delete, Is.EqualTo(3));
    }

    #endregion
}
