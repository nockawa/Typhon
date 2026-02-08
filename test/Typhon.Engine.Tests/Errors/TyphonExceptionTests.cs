using NUnit.Framework;
using System;
using System.Linq;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for the TyphonException hierarchy, error codes, and catch granularity.
/// </summary>
[TestFixture]
public class TyphonExceptionTests
{
    #region TyphonException Base

    [Test]
    public void TyphonException_HasCorrectErrorCode()
    {
        var ex = new TyphonException(TyphonErrorCode.Unspecified, "test error");

        Assert.That(ex.ErrorCode, Is.EqualTo(TyphonErrorCode.Unspecified));
        Assert.That(ex.Message, Is.EqualTo("test error"));
    }

    [Test]
    public void TyphonException_IsTransientDefaultsFalse()
    {
        var ex = new TyphonException(TyphonErrorCode.Unspecified, "test");

        Assert.That(ex.IsTransient, Is.False);
    }

    [Test]
    public void TyphonException_InnerExceptionPropagated()
    {
        var inner = new InvalidOperationException("inner cause");
        var ex = new TyphonException(TyphonErrorCode.Unspecified, "outer", inner);

        Assert.That(ex.InnerException, Is.SameAs(inner));
        Assert.That(ex.Message, Is.EqualTo("outer"));
    }

    #endregion

    #region LockTimeoutException

    [Test]
    public void LockTimeoutException_Properties()
    {
        var duration = TimeSpan.FromMilliseconds(500);
        var ex = new LockTimeoutException("PageCache/Page42", duration);

        Assert.That(ex.ResourceName, Is.EqualTo("PageCache/Page42"));
        Assert.That(ex.WaitDuration, Is.EqualTo(duration));
        Assert.That(ex.ErrorCode, Is.EqualTo(TyphonErrorCode.LockTimeout));
        Assert.That(ex.Message, Does.Contain("PageCache/Page42"));
        Assert.That(ex.Message, Does.Contain("500"));
    }

    [Test]
    public void LockTimeoutException_IsTransient()
    {
        var ex = new LockTimeoutException("res", TimeSpan.FromMilliseconds(100));

        Assert.That(ex.IsTransient, Is.True);
    }

    #endregion

    #region TransactionTimeoutException

    [Test]
    public void TransactionTimeoutException_Properties()
    {
        var duration = TimeSpan.FromSeconds(5);
        var ex = new TransactionTimeoutException(42L, duration);

        Assert.That(ex.TransactionId, Is.EqualTo(42L));
        Assert.That(ex.WaitDuration, Is.EqualTo(duration));
        Assert.That(ex.ErrorCode, Is.EqualTo(TyphonErrorCode.TransactionTimeout));
        Assert.That(ex.Message, Does.Contain("42"));
        Assert.That(ex.Message, Does.Contain("5000"));
    }

    #endregion

    #region StorageException

    [Test]
    public void StorageException_PassesErrorCode()
    {
        var ex = new StorageException(TyphonErrorCode.StorageCapacityExceeded, "disk full");

        Assert.That(ex.ErrorCode, Is.EqualTo(TyphonErrorCode.StorageCapacityExceeded));
        Assert.That(ex.Message, Is.EqualTo("disk full"));
        Assert.That(ex.IsTransient, Is.False);
    }

    #endregion

    #region CorruptionException

    [Test]
    public void CorruptionException_Properties()
    {
        var ex = new CorruptionException("PlayerHealth", 17, "CRC32C mismatch");

        Assert.That(ex.ComponentName, Is.EqualTo("PlayerHealth"));
        Assert.That(ex.PageIndex, Is.EqualTo(17));
        Assert.That(ex.ErrorCode, Is.EqualTo(TyphonErrorCode.DataCorruption));
        Assert.That(ex.Message, Does.Contain("PlayerHealth"));
        Assert.That(ex.Message, Does.Contain("17"));
        Assert.That(ex.Message, Does.Contain("CRC32C mismatch"));
    }

    [Test]
    public void CorruptionException_IsNotTransient()
    {
        var ex = new CorruptionException("comp", 0, "bad");

        Assert.That(ex.IsTransient, Is.False);
    }

    #endregion

    #region ResourceExhaustedException Re-parenting

    [Test]
    public void ResourceExhaustedException_IsNowTyphonException()
    {
        var ex = new ResourceExhaustedException("Pool/Tx", ResourceType.Service, 100, 100);

        Assert.That(ex, Is.InstanceOf<TyphonException>());
        Assert.That(ex.ErrorCode, Is.EqualTo(TyphonErrorCode.ResourceExhausted));
    }

    [Test]
    public void ResourceExhaustedException_PreservesExistingProperties()
    {
        // Constructor 1: Full details
        var ex1 = new ResourceExhaustedException("Pool/Tx", ResourceType.Service, 80, 100);
        Assert.That(ex1.ResourcePath, Is.EqualTo("Pool/Tx"));
        Assert.That(ex1.ResourceType, Is.EqualTo(ResourceType.Service));
        Assert.That(ex1.CurrentUsage, Is.EqualTo(80));
        Assert.That(ex1.Limit, Is.EqualTo(100));
        Assert.That(ex1.Utilization, Is.EqualTo(0.8).Within(0.001));

        // Constructor 2: Custom message
        var ex2 = new ResourceExhaustedException("custom msg", "Pool/Tx", ResourceType.Service, 80, 100);
        Assert.That(ex2.Message, Is.EqualTo("custom msg"));
        Assert.That(ex2.ResourcePath, Is.EqualTo("Pool/Tx"));

        // Constructor 3: Custom message + inner exception
        var inner = new Exception("cause");
        var ex3 = new ResourceExhaustedException("custom msg", inner, "Pool/Tx", ResourceType.Service, 80, 100);
        Assert.That(ex3.InnerException, Is.SameAs(inner));
        Assert.That(ex3.ResourcePath, Is.EqualTo("Pool/Tx"));
    }

    [Test]
    public void ResourceExhaustedException_IsTransient()
    {
        var ex = new ResourceExhaustedException("Pool/Tx", ResourceType.Service, 100, 100);

        Assert.That(ex.IsTransient, Is.True);
    }

    #endregion

    #region Catch Granularity

    [Test]
    public void CatchGranularity_TimeoutCatchesBothLockAndTransaction()
    {
        var lockEx = new LockTimeoutException("res", TimeSpan.FromMilliseconds(100));
        var txEx = new TransactionTimeoutException(1L, TimeSpan.FromSeconds(1));

        Assert.That(lockEx, Is.InstanceOf<TyphonTimeoutException>());
        Assert.That(txEx, Is.InstanceOf<TyphonTimeoutException>());

        // Verify catch block works
        var caughtCount = 0;
        try
        {
            throw lockEx;
        }
        catch (TyphonTimeoutException)
        {
            caughtCount++;
        }

        try
        {
            throw txEx;
        }
        catch (TyphonTimeoutException)
        {
            caughtCount++;
        }

        Assert.That(caughtCount, Is.EqualTo(2));
    }

    [Test]
    public void CatchGranularity_TyphonExceptionCatchesAll()
    {
        Exception[] exceptions =
        [
            new TyphonException(TyphonErrorCode.Unspecified, "base"),
            new LockTimeoutException("res", TimeSpan.FromMilliseconds(100)),
            new TransactionTimeoutException(1L, TimeSpan.FromSeconds(1)),
            new StorageException(TyphonErrorCode.StorageCapacityExceeded, "full"),
            new CorruptionException("comp", 0, "bad"),
            new ResourceExhaustedException("Pool", ResourceType.Service, 100, 100),
        ];

        foreach (var ex in exceptions)
        {
            Assert.That(ex, Is.InstanceOf<TyphonException>(), $"{ex.GetType().Name} should be a TyphonException");
        }
    }

    #endregion

    #region Error Code Uniqueness

    [Test]
    public void ErrorCodeUniqueness_NoDuplicateValues()
    {
        var values = Enum.GetValues(typeof(TyphonErrorCode)).Cast<int>().ToList();
        var duplicates = values.GroupBy(v => v).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        Assert.That(duplicates, Is.Empty, $"Duplicate error code values: {string.Join(", ", duplicates)}");
    }

    #endregion
}
