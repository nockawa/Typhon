using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Build-time rejections that keep <see cref="EventQueue{T}"/>'s single-consumer contract enforceable (#861).
/// </summary>
[TestFixture]
public class EventQueueValidationTests
{
    private ResourceRegistry _registry;

    [SetUp]
    public void SetUp() => _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "EventQueueValidation" });

    [TearDown]
    public void TearDown() => _registry?.Dispose();

    [Test]
    [VerifiesRule("EQ-04")]
    public void TwoConsumers_AreRejectedAtBuild()
    {
        var dag = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 })
            .PublicTrack.DeclareDag("Test");
        var queue = dag.CreateEventQueue<int>("Shared");

        dag.CallbackSystem("Producer", ctx => ctx.Writer(queue).Push(1))
            .CallbackSystem("ConsumerA", _ => { })
            .CallbackSystem("ConsumerB", _ => { });

        dag.Produces("Producer", queue);
        dag.Consumes("ConsumerA", queue);
        dag.Consumes("ConsumerB", queue);

        // Two consumers get no derived edge between them — ED-03 only relates producers to consumers — so both flip ready on the producer's completion
        // and race inside Drain: the same prefix is copied out twice, both store Count = 0, and `Consumed +=` loses an update.
        var ex = Assert.Throws<InvalidOperationException>(() => dag.Build(_registry.Runtime));
        Assert.That(ex.Message, Does.Contain("single-consumer").And.Contain("Shared"));
    }

    [Test]
    public void OneConsumer_BuildsFine()
    {
        var dag = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 })
            .PublicTrack.DeclareDag("Test");
        var queue = dag.CreateEventQueue<int>("Solo");

        dag.CallbackSystem("Producer", ctx => ctx.Writer(queue).Push(1))
            .CallbackSystem("Consumer", _ => { });

        dag.Produces("Producer", queue);
        dag.Consumes("Consumer", queue);

        using var scheduler = dag.Build(_registry.Runtime);
        Assert.That(scheduler, Is.Not.Null);
    }

    [Test]
    public void SameConsumerDeclaredTwice_IsNotMistakenForTwoConsumers()
    {
        // `Consumes` appends without dedup, so a double declaration used to trip the single-consumer guard and report a second system that never existed.
        var dag = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 })
            .PublicTrack.DeclareDag("Test");
        var queue = dag.CreateEventQueue<int>("Doubled");

        dag.CallbackSystem("Producer", ctx => ctx.Writer(queue).Push(1))
            .CallbackSystem("Consumer", _ => { });

        dag.Produces("Producer", queue);
        dag.Consumes("Consumer", queue);
        dag.Consumes("Consumer", queue);

        using var scheduler = dag.Build(_registry.Runtime);
        Assert.That(scheduler, Is.Not.Null);
    }

    [Test]
    public void MultipleProducers_AreAllowed()
    {
        // The point of #861: several systems — and several chunk workers within one system — may produce into one queue.
        var dag = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 })
            .PublicTrack.DeclareDag("Test");
        var queue = dag.CreateEventQueue<int>("MultiProducer");

        dag.CallbackSystem("ProducerA", ctx => ctx.Writer(queue).Push(1))
            .CallbackSystem("ProducerB", ctx => ctx.Writer(queue).Push(2))
            .CallbackSystem("Consumer", _ => { });

        dag.Produces("ProducerA", queue);
        dag.Produces("ProducerB", queue);
        dag.Consumes("Consumer", queue);

        using var scheduler = dag.Build(_registry.Runtime);
        Assert.That(scheduler, Is.Not.Null);
    }
}
