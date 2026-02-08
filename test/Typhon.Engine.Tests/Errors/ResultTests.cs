using NUnit.Framework;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for <see cref="Result{TValue, TStatus}"/> struct, <see cref="BTreeLookupStatus"/>,
/// and <see cref="RevisionReadStatus"/> enums.
/// </summary>
[TestFixture]
public class ResultTests
{
    #region Result struct — Success

    [Test]
    public void Result_Success_IsSuccessTrue()
    {
        var result = new Result<int, BTreeLookupStatus>(42);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.IsFailure, Is.False);
    }

    [Test]
    public void Result_Success_ValuePreserved()
    {
        var result = new Result<int, BTreeLookupStatus>(123);

        Assert.That(result.Value, Is.EqualTo(123));
        Assert.That(result.Status, Is.EqualTo(BTreeLookupStatus.Success));
    }

    #endregion

    #region Result struct — Failure

    [Test]
    public void Result_Failure_IsFailureTrue()
    {
        var result = new Result<int, BTreeLookupStatus>(BTreeLookupStatus.NotFound);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.IsSuccess, Is.False);
    }

    [Test]
    public void Result_Failure_ValueIsDefault()
    {
        var result = new Result<int, BTreeLookupStatus>(BTreeLookupStatus.NotFound);

        Assert.That(result.Value, Is.EqualTo(0));
    }

    #endregion

    #region Result struct — Value with explicit status

    [Test]
    public void Result_ValueWithStatus_BothPreserved()
    {
        var result = new Result<int, RevisionReadStatus>(99, RevisionReadStatus.Deleted);

        Assert.That(result.Value, Is.EqualTo(99));
        Assert.That(result.Status, Is.EqualTo(RevisionReadStatus.Deleted));
        Assert.That(result.IsFailure, Is.True);
    }

    #endregion

    #region BTreeLookupStatus

    [Test]
    public void BTreeLookupStatus_NotFound_IsFailure()
    {
        var result = new Result<int, BTreeLookupStatus>(BTreeLookupStatus.NotFound);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Status, Is.EqualTo(BTreeLookupStatus.NotFound));
    }

    #endregion

    #region RevisionReadStatus

    [Test]
    public void RevisionReadStatus_AllNonZero_AreFailure()
    {
        var notFound = new Result<int, RevisionReadStatus>(RevisionReadStatus.NotFound);
        var invisible = new Result<int, RevisionReadStatus>(RevisionReadStatus.SnapshotInvisible);
        var deleted = new Result<int, RevisionReadStatus>(RevisionReadStatus.Deleted);

        Assert.That(notFound.IsFailure, Is.True);
        Assert.That(invisible.IsFailure, Is.True);
        Assert.That(deleted.IsFailure, Is.True);
    }

    [Test]
    public void RevisionReadStatus_Deleted_CarriesValue()
    {
        var result = new Result<int, RevisionReadStatus>(42, RevisionReadStatus.Deleted);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Value, Is.EqualTo(42));
        Assert.That(result.Status, Is.EqualTo(RevisionReadStatus.Deleted));
    }

    #endregion

    #region No-boxing enum check

    [Test]
    public void Result_NoBoxing_EnumStatusCheck()
    {
        // Verify that Unsafe.As<TStatus, byte> path works for both byte enums
        var btreeSuccess = new Result<int, BTreeLookupStatus>(10);
        var revSuccess = new Result<int, RevisionReadStatus>(20);

        // Both should be success (status byte == 0)
        Assert.That(btreeSuccess.IsSuccess, Is.True);
        Assert.That(revSuccess.IsSuccess, Is.True);

        // Verify underlying byte is 0 for success
        Assert.That(Unsafe.As<BTreeLookupStatus, byte>(ref Unsafe.AsRef(in btreeSuccess.Status)), Is.EqualTo(0));
        Assert.That(Unsafe.As<RevisionReadStatus, byte>(ref Unsafe.AsRef(in revSuccess.Status)), Is.EqualTo(0));

        // Verify underlying byte is non-zero for failures
        var btreeFail = new Result<int, BTreeLookupStatus>(BTreeLookupStatus.NotFound);
        var revFail = new Result<int, RevisionReadStatus>(RevisionReadStatus.SnapshotInvisible);
        Assert.That(Unsafe.As<BTreeLookupStatus, byte>(ref Unsafe.AsRef(in btreeFail.Status)), Is.EqualTo(1));
        Assert.That(Unsafe.As<RevisionReadStatus, byte>(ref Unsafe.AsRef(in revFail.Status)), Is.EqualTo(2));
    }

    #endregion
}
