using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RabbitMQ.Client;
using SW.Bus.RabbitMqExtensions;
using Testcontainers.RabbitMq;

namespace SW.Bus.IntegrationTests;

/// <summary>
/// What happens to messages when the host stops, and when processing fails, against a real broker.
/// </summary>
[TestClass]
public class DrainAndRetryTests
{
    private static RabbitMqContainer _container = null!;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext _)
    {
        _container = Broker.Create();
        await _container.StartAsync();
    }

    [ClassCleanup]
    public static async Task ClassCleanup() => await _container.DisposeAsync();

    [TestMethod]
    public async Task Stopping_lets_running_messages_finish_and_ack_and_returns_the_ones_not_started()
    {
        var plan = new WorkPlan();
        plan.Types["draining"] = new ConsumerOptions { Prefetch = 4, Concurrency = 2 };
        plan.Handlers["draining"] = _ => Task.Delay(3000);
        var harness = await BusHarness.StartAsync(_container.GetConnectionString(),
            configureServices: s => s.AddSingleton(plan));
        var queue = $"{harness.Options.ProcessExchange}.itest.workconsumer.draining";

        await ConcurrencyTests.Publish(harness, "draining", 3);
        Assert.IsTrue(await Broker.Eventually(() => plan.Log("draining").Started.Count == 2), "The messages never started.");

        await harness.DisposeAsync();

        Assert.AreEqual(2, plan.Log("draining").Completed.Count, "A running message did not finish before the stop.");
        Assert.AreEqual(2, plan.Log("draining").Started.Count, "A message started after the stop began.");
        Assert.AreEqual(1u, (await Broker.Queue(_container, queue)).Messages,
            "Only the message that never started should be back in the queue.");
    }

    [TestMethod]
    public async Task A_message_still_running_at_the_drain_timeout_returns_to_its_queue()
    {
        var plan = new WorkPlan();
        plan.Types["hung"] = new ConsumerOptions();
        plan.Handlers["hung"] = _ => Task.Delay(8000);
        var harness = await BusHarness.StartAsync(_container.GetConnectionString(),
            configureBus: o => o.DrainTimeout = TimeSpan.FromSeconds(1),
            configureServices: s => s.AddSingleton(plan));
        var queue = $"{harness.Options.ProcessExchange}.itest.workconsumer.hung";

        await ConcurrencyTests.Publish(harness, "hung", 1);
        Assert.IsTrue(await Broker.Eventually(() => plan.Log("hung").Started.Count == 1), "The message never started.");

        var stop = System.Diagnostics.Stopwatch.StartNew();
        await harness.DisposeAsync();
        Assert.AreEqual(1u, (await Broker.Queue(_container, queue)).Messages);
    }

    [TestMethod]
    public async Task A_failing_message_is_retried_its_retry_count_then_moved_to_the_bad_queue()
    {
        var plan = new WorkPlan();
        plan.Types["flaky"] = new ConsumerOptions();
        plan.Handlers["flaky"] = _ => throw new InvalidOperationException("always fails");
        await using var harness = await BusHarness.StartAsync(_container.GetConnectionString(),
            configureBus: o => o.AddQueueOption("workconsumer.flaky", retryCount: 2, retryAfterSeconds: 0),
            configureServices: s => s.AddSingleton(plan));
        var bad = $"{harness.Options.ProcessExchange}.itest.workconsumer.flaky.bad";

        await ConcurrencyTests.Publish(harness, "flaky", 1);

        Assert.IsTrue(await Broker.Eventually(async () => (await Broker.Queue(_container, bad)).Messages == 1),
            "The message never reached the bad queue.");
        CollectionAssert.AreEqual(new[] { "2", "1", "0" }, plan.Log("flaky").RemainingRetries.ToArray());

        await using var conn = await new ConnectionFactory { Uri = new Uri(_container.GetConnectionString()) }
            .CreateConnectionAsync();
        await using var channel = await conn.CreateChannelAsync();
        var message = await channel.BasicGetAsync(bad, true);
        Assert.IsNotNull(message);
        Assert.AreEqual("0", Encoding.UTF8.GetString(message.Body.Span));
        var headers = message.BasicProperties.Headers!;
        Assert.IsTrue(headers.ContainsKey("exception1"));
        Assert.IsTrue(headers.ContainsKey("source-node-id"));
        Assert.IsFalse(headers.ContainsKey("x-death"));
        Assert.IsFalse(headers.ContainsKey(Constants.PublishSequenceNumberHeader));
        Assert.AreEqual(DeliveryModes.Persistent, message.BasicProperties.DeliveryMode);
    }
}
