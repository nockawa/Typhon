using NUnit.Framework;
using System;
using System.Linq;
using System.Reflection;

namespace Typhon.Engine.Tests.Observability;

/// <summary>
/// Enforces the structural invariant that makes the Tier-2 gating mechanism work: every gate
/// field on <see cref="TelemetryConfig"/> must be a <c>public static readonly bool</c>.
///
/// <para>
/// The whole point of the gating scheme is that <c>if (!TelemetryConfig.SomeActive) return default;</c>
/// gets dead-code-eliminated by the JIT at Tier-1 compilation when <c>SomeActive</c> is <c>false</c>.
/// That elimination only happens for <c>static readonly bool</c> fields. If someone accidentally
/// writes <c>static bool</c> (mutable), <c>const bool</c> (no runtime configurability), or
/// <c>public static bool ... { get; }</c> (a property — JIT may not fold), the optimization breaks
/// silently and a hot path turns into a measurable tax.
/// </para>
///
/// <para>
/// This test catches that at PR time. It runs in &lt;1 ms and is the entire automated guardrail
/// for Phase 1's "no regressions in JIT-elim" goal — the runtime benchmark in
/// <c>TelemetryToggleBenchmark</c> covers the behavioral side.
/// </para>
/// </summary>
[TestFixture]
public class TelemetryConfigGateShapeTests
{
    [Test]
    public void Every_Active_Field_Is_StaticReadonlyBool()
    {
        var fields = typeof(TelemetryConfig)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.Name.EndsWith("Active", StringComparison.Ordinal))
            .ToArray();

        Assert.That(fields, Is.Not.Empty,
            "Expected at least one *Active gate field on TelemetryConfig.");

        foreach (var field in fields)
        {
            Assert.That(field.FieldType, Is.EqualTo(typeof(bool)),
                $"{field.Name}: gate fields must be `bool` (got {field.FieldType.Name}). " +
                $"JIT can only fold boolean constant branches.");
            Assert.That(field.IsInitOnly, Is.True,
                $"{field.Name}: gate fields must be `readonly` so the JIT can fold `if (!{field.Name})` " +
                $"as a constant branch at Tier-1 compilation. Use `public static readonly bool {field.Name}`.");
            Assert.That(field.IsLiteral, Is.False,
                $"{field.Name}: must be `static readonly`, not `const`. `const` is fine for JIT-elim " +
                $"but breaks runtime configurability — the value is baked into every consumer at compile time.");
        }
    }
}
