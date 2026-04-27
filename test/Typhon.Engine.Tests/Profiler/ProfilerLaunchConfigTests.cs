using System;
using NUnit.Framework;
using Typhon.Engine.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Unit tests for <see cref="ProfilerLaunchConfig"/> — the engine's host-launch convention parser. Pure data
/// transformations: argv → record, env → record, merge of two records. Each test runs in isolation; the env-var
/// tests save/restore the underlying environment so concurrent fixtures aren't disturbed.
/// </summary>
[TestFixture]
[NonParallelizable] // env-var tests touch process-global state; serialize within fixture and across fixtures.
public sealed class ProfilerLaunchConfigTests
{
    private const string EnvTrace = "TYPHON_PROFILER_TRACE";
    private const string EnvLive = "TYPHON_PROFILER_LIVE";
    private const string EnvWait = "TYPHON_PROFILER_LIVE_WAIT_MS";

    [Test]
    public void DefaultConfig_IsInactive()
    {
        var cfg = new ProfilerLaunchConfig();
        Assert.That(cfg.TraceFilePath, Is.Null);
        Assert.That(cfg.LivePort, Is.EqualTo(-1));
        Assert.That(cfg.LiveWaitMs, Is.EqualTo(0));
        Assert.That(cfg.IsActive, Is.False);
    }

    [Test]
    public void FromArgs_NoArgs_AllSentinels()
    {
        var cfg = ProfilerLaunchConfig.FromArgs(Array.Empty<string>());
        Assert.That(cfg.IsActive, Is.False);
    }

    [Test]
    public void FromArgs_NullArgs_AllSentinels()
    {
        var cfg = ProfilerLaunchConfig.FromArgs(null);
        Assert.That(cfg.IsActive, Is.False);
    }

    [Test]
    public void FromArgs_TraceOnly()
    {
        var cfg = ProfilerLaunchConfig.FromArgs(new[] { "--trace", "out.bin" });
        Assert.That(cfg.TraceFilePath, Is.EqualTo("out.bin"));
        Assert.That(cfg.LivePort, Is.EqualTo(-1));
        Assert.That(cfg.IsActive, Is.True);
    }

    [Test]
    public void FromArgs_LiveWithExplicitPort()
    {
        var cfg = ProfilerLaunchConfig.FromArgs(new[] { "--live", "9001" });
        Assert.That(cfg.LivePort, Is.EqualTo(9001));
        Assert.That(cfg.IsActive, Is.True);
    }

    [Test]
    public void FromArgs_LiveWithoutPort_UsesDefault()
    {
        var cfg = ProfilerLaunchConfig.FromArgs(new[] { "--live" });
        Assert.That(cfg.LivePort, Is.EqualTo(ProfilerLaunchConfig.DefaultLivePort));
    }

    [Test]
    public void FromArgs_LiveWithNonNumericFollowing_TreatsAsNoPort()
    {
        var cfg = ProfilerLaunchConfig.FromArgs(new[] { "--live", "--trace", "out.bin" });
        Assert.That(cfg.LivePort, Is.EqualTo(ProfilerLaunchConfig.DefaultLivePort));
        Assert.That(cfg.TraceFilePath, Is.EqualTo("out.bin"));
    }

    [Test]
    public void FromArgs_LiveWaitMs()
    {
        var cfg = ProfilerLaunchConfig.FromArgs(new[] { "--live", "9100", "--live-wait", "5000" });
        Assert.That(cfg.LivePort, Is.EqualTo(9100));
        Assert.That(cfg.LiveWaitMs, Is.EqualTo(5000));
    }

    [Test]
    public void FromArgs_LiveWaitMs_NegativeIsRejected()
    {
        var cfg = ProfilerLaunchConfig.FromArgs(new[] { "--live-wait", "-1" });
        Assert.That(cfg.LiveWaitMs, Is.EqualTo(0), "negative wait is silently dropped to 0 (no wait)");
    }

    [Test]
    public void FromArgs_LiveWaitMs_NonNumericIsIgnored()
    {
        var cfg = ProfilerLaunchConfig.FromArgs(new[] { "--live-wait", "garbage" });
        Assert.That(cfg.LiveWaitMs, Is.EqualTo(0));
    }

    [Test]
    public void FromArgs_DualOutput()
    {
        var cfg = ProfilerLaunchConfig.FromArgs(new[] { "--trace", "out.bin", "--live", "9200" });
        Assert.That(cfg.TraceFilePath, Is.EqualTo("out.bin"));
        Assert.That(cfg.LivePort, Is.EqualTo(9200));
    }

    [Test]
    public void FromArgs_UnknownArgsIgnored()
    {
        var cfg = ProfilerLaunchConfig.FromArgs(new[] { "--duration", "30", "--live", "9100", "--unknown" });
        Assert.That(cfg.LivePort, Is.EqualTo(9100));
    }

    [Test]
    public void FromEnvironment_AllUnset_ReturnsInactive()
    {
        using var _ = WithEnvVar(EnvTrace, null);
        using var _2 = WithEnvVar(EnvLive, null);
        using var _3 = WithEnvVar(EnvWait, null);
        var cfg = ProfilerLaunchConfig.FromEnvironment();
        Assert.That(cfg.IsActive, Is.False);
    }

    [Test]
    public void FromEnvironment_TraceOnly()
    {
        using var _ = WithEnvVar(EnvTrace, "/tmp/out.bin");
        using var _2 = WithEnvVar(EnvLive, null);
        using var _3 = WithEnvVar(EnvWait, null);
        var cfg = ProfilerLaunchConfig.FromEnvironment();
        Assert.That(cfg.TraceFilePath, Is.EqualTo("/tmp/out.bin"));
        Assert.That(cfg.LivePort, Is.EqualTo(-1));
    }

    [Test]
    public void FromEnvironment_LiveWithPort()
    {
        using var _ = WithEnvVar(EnvLive, "9300");
        using var _2 = WithEnvVar(EnvTrace, null);
        using var _3 = WithEnvVar(EnvWait, null);
        var cfg = ProfilerLaunchConfig.FromEnvironment();
        Assert.That(cfg.LivePort, Is.EqualTo(9300));
    }

    [Test]
    public void FromEnvironment_LiveNonNumeric_UsesDefault()
    {
        using var _ = WithEnvVar(EnvLive, "yes");
        using var _2 = WithEnvVar(EnvTrace, null);
        using var _3 = WithEnvVar(EnvWait, null);
        var cfg = ProfilerLaunchConfig.FromEnvironment();
        Assert.That(cfg.LivePort, Is.EqualTo(ProfilerLaunchConfig.DefaultLivePort));
    }

    [Test]
    public void FromEnvironment_LiveWaitMs()
    {
        using var _ = WithEnvVar(EnvLive, "9100");
        using var _2 = WithEnvVar(EnvTrace, null);
        using var _3 = WithEnvVar(EnvWait, "7500");
        var cfg = ProfilerLaunchConfig.FromEnvironment();
        Assert.That(cfg.LiveWaitMs, Is.EqualTo(7500));
    }

    [Test]
    public void FromEnvironment_LiveWaitMs_Negative_StaysZero()
    {
        using var _ = WithEnvVar(EnvWait, "-100");
        using var _2 = WithEnvVar(EnvLive, null);
        using var _3 = WithEnvVar(EnvTrace, null);
        var cfg = ProfilerLaunchConfig.FromEnvironment();
        Assert.That(cfg.LiveWaitMs, Is.EqualTo(0));
    }

    [Test]
    public void MergedWith_OverrideTraceWinsWhenSet()
    {
        var baseCfg = new ProfilerLaunchConfig { TraceFilePath = "/base.bin" };
        var over = new ProfilerLaunchConfig { TraceFilePath = "/over.bin" };
        Assert.That(baseCfg.MergedWith(over).TraceFilePath, Is.EqualTo("/over.bin"));
    }

    [Test]
    public void MergedWith_BaseRetainedWhenOverrideUnset()
    {
        var baseCfg = new ProfilerLaunchConfig { TraceFilePath = "/base.bin", LivePort = 9100 };
        var over = new ProfilerLaunchConfig();    // all sentinels
        var merged = baseCfg.MergedWith(over);
        Assert.That(merged.TraceFilePath, Is.EqualTo("/base.bin"));
        Assert.That(merged.LivePort, Is.EqualTo(9100));
    }

    [Test]
    public void MergedWith_OverridePortWinsWhenSet()
    {
        var baseCfg = new ProfilerLaunchConfig { LivePort = 9100 };
        var over = new ProfilerLaunchConfig { LivePort = 9200 };
        Assert.That(baseCfg.MergedWith(over).LivePort, Is.EqualTo(9200));
    }

    [Test]
    public void MergedWith_OverrideWaitWinsWhenSet()
    {
        var baseCfg = new ProfilerLaunchConfig { LiveWaitMs = 1000 };
        var over = new ProfilerLaunchConfig { LiveWaitMs = 5000 };
        Assert.That(baseCfg.MergedWith(over).LiveWaitMs, Is.EqualTo(5000));
    }

    [Test]
    public void MergedWith_NullOverride_ReturnsBase()
    {
        var baseCfg = new ProfilerLaunchConfig { LivePort = 9100 };
        Assert.That(baseCfg.MergedWith(null), Is.SameAs(baseCfg));
    }

    [Test]
    public void TypicalLayering_EnvFirstThenArgsOverride()
    {
        // The standard CLI-tooling pattern: env provides the user's defaults, args take precedence.
        // Setup: env says trace + port 9100. CLI overrides with port 9200.
        using var _ = WithEnvVar(EnvTrace, "/env.bin");
        using var _2 = WithEnvVar(EnvLive, "9100");
        using var _3 = WithEnvVar(EnvWait, null);
        var env = ProfilerLaunchConfig.FromEnvironment();
        var cli = ProfilerLaunchConfig.FromArgs(new[] { "--live", "9200" });
        var final = env.MergedWith(cli);
        Assert.That(final.TraceFilePath, Is.EqualTo("/env.bin"), "trace from env preserved");
        Assert.That(final.LivePort, Is.EqualTo(9200), "port overridden by CLI");
    }

    /// <summary>
    /// Helper: temporarily set an env var, restore on Dispose. Tolerates parallel test execution because each
    /// test only touches its own three env vars and we restore deterministically. (The fixture is also
    /// non-parallel-safe in the strict sense, but this RAII pattern is the simplest defense.)
    /// </summary>
    private static IDisposable WithEnvVar(string name, string value)
    {
        var prev = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        return new Restore(name, prev);
    }

    private sealed class Restore : IDisposable
    {
        private readonly string _name;
        private readonly string _value;
        public Restore(string name, string value) { _name = name; _value = value; }
        public void Dispose() => Environment.SetEnvironmentVariable(_name, _value);
    }
}
