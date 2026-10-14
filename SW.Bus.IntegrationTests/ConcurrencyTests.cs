using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SW.Bus.RabbitMqExtensions;
using SW.PrimitiveTypes;
using Testcontainers.RabbitMq;

namespace SW.Bus.IntegrationTests;

/// <summary>
/// Per-queue consumer concurrency against a real broker: a queue runs as many messages at once as
/// its concurrency allows (capped by prefetch), and at 1 it runs them one at a time, in order.
/// </summary>
[TestClass]
public class ConcurrencyTests
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

    private static Task Slow(string _) => Task.Delay(1000);

    [TestMethod]
    public async Task A_queue_runs_as_many_messages_at_once_as_its_concurrency()
    {
        var plan = new WorkPlan();
        plan.Types["parallel"] = new ConsumerOptions { Prefetch = 12, Concurrency = 4 };
        plan.Handlers["parallel"] = Slow;
        await using var harness = await BusHarness.StartAsync(_container.GetConnectionString(),
            configureServices: s => s.AddSingleton(plan));

        await Publish(harness, "parallel", 8);

        Assert.IsTrue(await Broker.Eventually(() => plan.Log("parallel").Completed.Count == 8), "The messages never completed.");
        Assert.AreEqual(4, plan.Log("parallel").MaxRunning);
    }

    [TestMethod]
    public async Task At_concurrency_one_a_queue_runs_one_message_at_a_time_in_order()
    {
        var plan = new WorkPlan();
        plan.Types["ordered"] = new ConsumerOptions { Prefetch = 12 };
        plan.Handlers["ordered"] = _ => Task.Delay(30);
        await using var harness = await BusHarness.StartAsync(_container.GetConnectionString(),
            configureServices: s => s.AddSingleton(plan));

        await Publish(harness, "ordered", 20);

        Assert.IsTrue(await Broker.Eventually(() => plan.Log("ordered").Completed.Count == 20), "The messages never completed.");
        Assert.AreEqual(1, plan.Log("ordered").MaxRunning);
        CollectionAssert.AreEqual(Enumerable.Range(0, 20).Select(i => i.ToString()).ToList(),
            plan.Log("ordered").Completed.ToList());
    }

    [TestMethod]
    public async Task Concurrency_comes_from_the_queue_option_then_the_bus_default_and_is_capped_by_prefetch()
    {
        var plan = new WorkPlan();
        plan.Types["byoption"] = new ConsumerOptions { Prefetch = 10 };
        plan.Types["bydefault"] = new ConsumerOptions { Prefetch = 10 };
        plan.Types["capped"] = new ConsumerOptions { Prefetch = 2, Concurrency = 6 };
        foreach (var type in plan.Types.Keys) plan.Handlers[type] = Slow;
        await using var harness = await BusHarness.StartAsync(_container.GetConnectionString(),
            configureBus: o =>
            {
                o.DefaultConsumerConcurrency = 3;
                o.AddQueueOption("workconsumer.byoption", concurrency: 2);
            },
            configureServices: s => s.AddSingleton(plan));

        foreach (var type in plan.Types.Keys) await Publish(harness, type, 6);

        Assert.IsTrue(await Broker.Eventually(() => plan.Types.Keys.All(t => plan.Log(t).Completed.Count == 6)),
            "The messages never completed.");
        Assert.AreEqual(2, plan.Log("byoption").MaxRunning, "AddQueueOption's concurrency");
        Assert.AreEqual(3, plan.Log("bydefault").MaxRunning, "DefaultConsumerConcurrency");
        Assert.AreEqual(2, plan.Log("capped").MaxRunning, "prefetch caps concurrency");
    }

    [TestMethod]
    public async Task Refresh_moves_a_consumer_to_its_new_concurrency_without_processing_a_message_twice()
    {
        var plan = new WorkPlan();
        plan.Types["moving"] = new ConsumerOptions { Prefetch = 12, Concurrency = 4 };
        plan.Handlers["moving"] = _ => Task.Delay(1500);
        await using var harness = await BusHarness.StartAsync(_container.GetConnectionString(),
            configureServices: s => s.AddSingleton(plan));
        var queue = $"{harness.Options.ProcessExchange}.itest.workconsumer.moving";

        await Publish(harness, "moving", 6);
        Assert.IsTrue(await Broker.Eventually(() => plan.Log("moving").Started.Count == 4), "The first messages never started.");

        // Changed while four are running and two wait in the old channel's prefetch.
        plan.Types["moving"] = new ConsumerOptions { Prefetch = 12, Concurrency = 2 };
        using (var scope = harness.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IBroadcast>().RefreshConsumers();

        Assert.IsTrue(await Broker.Eventually(() => plan.Log("moving").Completed.Count == 6), "The messages never completed.");
        await Task.Delay(2000);
        Assert.AreEqual(6, plan.Log("moving").Started.Count, "A message was processed twice.");
        Assert.AreEqual(0u, (await Broker.Queue(_container, queue)).Messages);
        Assert.AreEqual(1u, (await Broker.Queue(_container, queue)).Consumers);

        plan.Logs.TryRemove("moving", out _);
        await Publish(harness, "moving", 6);
        Assert.IsTrue(await Broker.Eventually(() => plan.Log("moving").Completed.Count == 6), "The later messages never completed.");
        Assert.AreEqual(2, plan.Log("moving").MaxRunning);
    }

    [TestMethod]
    public async Task Refresh_applies_a_new_prefetch_to_the_running_consumer()
    {
        var plan = new WorkPlan();
        plan.Types["refetch"] = new ConsumerOptions { Prefetch = 2 };
        await using var harness = await BusHarness.StartAsync(_container.GetConnectionString(),
            configureServices: s => s.AddSingleton(plan));
        var queue = $"{harness.Options.ProcessExchange}.itest.workconsumer.refetch";
        Assert.IsTrue(await Broker.Eventually(async () =>
            (await Broker.ManagementPrefetch(_container, queue)).SequenceEqual(new long[] { 2 })), "Prefetch 2 never showed.");

        plan.Types["refetch"] = new ConsumerOptions { Prefetch = 5 };
        using (var scope = harness.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IBroadcast>().RefreshConsumers();

        Assert.IsTrue(await Broker.Eventually(async () =>
            (await Broker.ManagementPrefetch(_container, queue)).SequenceEqual(new long[] { 5 })), "Prefetch 5 never showed.");
        await Publish(harness, "refetch", 3);
        Assert.IsTrue(await Broker.Eventually(() => plan.Log("refetch").Completed.Count == 3),
            "The re-attached consumer did not process.");
    }

    [TestMethod]
    public async Task Concurrent_publishes_from_one_scope_and_from_many_scopes_all_arrive()
    {
        var plan = new WorkPlan();
        plan.Types["many"] = new ConsumerOptions { Prefetch = 50, Concurrency = 8 };
        await using var harness = await BusHarness.StartAsync(_container.GetConnectionString(),
            configureServices: s => s.AddSingleton(plan));

        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var publish = scope.ServiceProvider.GetRequiredService<IPublish>();
            await Task.WhenAll(Enumerable.Range(0, 200).Select(i => publish.Publish("many", $"s{i}")));
        }

        await Task.WhenAll(Enumerable.Range(0, 50).Select(async i =>
        {
            await using var scope = harness.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IPublish>().Publish("many", $"m{i}");
        }));

        Assert.IsTrue(await Broker.Eventually(() => plan.Log("many").Completed.Count == 250),
            $"Only {plan.Log("many").Completed.Count} of 250 arrived.");
        Assert.AreEqual(250, plan.Log("many").Completed.Distinct().Count());
    }

    internal static async Task Publish(BusHarness harness, string type, int count)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var publish = scope.ServiceProvider.GetRequiredService<IPublish>();
        for (var i = 0; i < count; i++)
            await publish.Publish(type, i.ToString());
    }
}
