using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SW.Bus.RabbitMqExtensions;
using SW.PrimitiveTypes;
using Testcontainers.RabbitMq;

namespace SW.Bus.IntegrationTests;

/// <summary>
/// The connection's life against a real broker that is stopped and started under it: publisher
/// confirms, recovery without duplicate consumers, and a host that boots while the broker is down.
/// </summary>
[TestClass]
public class ConnectionTests
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

    [TestCleanup]
    public async Task TestCleanup() => await Broker.StartApp(_container);

    [TestMethod]
    public async Task A_publish_against_a_stopped_broker_throws_and_publishing_resumes_when_it_returns()
    {
        var plan = new WorkPlan();
        plan.Types["confirmed"] = new ConsumerOptions();
        await using var harness = await BusHarness.StartAsync(_container.GetConnectionString(),
            configureServices: s => s.AddSingleton(plan));

        await ConcurrencyTests.Publish(harness, "confirmed", 1);
        Assert.IsTrue(await Broker.Eventually(() => plan.Log("confirmed").Completed.Count == 1), "The first publish never arrived.");

        await Broker.StopApp(_container);
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var publish = scope.ServiceProvider.GetRequiredService<IPublish>().Publish("confirmed", "lost");
            var finished = await Task.WhenAny(publish, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.AreSame(publish, finished, "The publish neither completed nor failed.");
            Assert.IsTrue(publish.IsFaulted, "A publish to a stopped broker returned as if it was sent.");
        }

        await Broker.StartApp(_container);
        Assert.IsTrue(await Broker.Eventually(async () =>
        {
            try
            {
                await ConcurrencyTests.Publish(harness, "confirmed", 1);
                return true;
            }
            catch
            {
                return false;
            }
        }, 60), "Publishing never resumed.");
        Assert.IsTrue(await Broker.Eventually(() => plan.Log("confirmed").Completed.Count >= 2), "The later publish never arrived.");
        Assert.IsFalse(plan.Log("confirmed").Completed.Contains("lost"));
    }

    [TestMethod]
    public async Task A_broker_restart_leaves_exactly_one_consumer_per_queue()
    {
        var plan = new WorkPlan();
        plan.Types["first"] = new ConsumerOptions { Concurrency = 2 };
        plan.Types["second"] = new ConsumerOptions();
        await using var harness = await BusHarness.StartAsync(_container.GetConnectionString(),
            configureBus: o => o.ManagementUrl = Broker.ManagementUrl(_container),
            configureServices: s => s.AddSingleton(plan));
        var reader = harness.Services.GetRequiredService<IConsumerReader>();
        var status = harness.Services.GetRequiredService<IBusConnectionStatus>();

        async Task<bool> OneEach()
        {
            var counts = (await reader.GetAllConsumersCount()).Where(c => c.Name == nameof(WorkConsumer)).ToList();
            return counts.Count == 2 && counts.All(c => c.TotalNodes == 1);
        }

        Assert.IsTrue(status.IsConnected);
        Assert.IsTrue(await Broker.Eventually(OneEach), "Before the restart, not one consumer per queue.");

        await Broker.StopApp(_container);
        Assert.IsTrue(await Broker.Eventually(() => !status.IsConnected), "The bus never noticed the broker went away.");
        await Broker.StartApp(_container);

        Assert.IsTrue(await Broker.Eventually(() => status.IsConnected, 60), "The bus never reconnected.");
        Assert.IsTrue(await Broker.Eventually(OneEach, 60), "After the restart, not one consumer per queue.");
        // A late second attach would show here: the reader caches for up to MonitoringCacheSeconds.
        await Task.Delay(TimeSpan.FromSeconds(12));
        Assert.IsTrue(await OneEach(), "A second consumer attached after the restart.");
        Assert.AreEqual(1, await Broker.ManagementConsumers(_container, harness.Options.NodeQueueName), "Node queue consumers.");

        await ConcurrencyTests.Publish(harness, "first", 3);
        await ConcurrencyTests.Publish(harness, "second", 3);
        Assert.IsTrue(await Broker.Eventually(() =>
                plan.Log("first").Completed.Count == 3 && plan.Log("second").Completed.Count == 3),
            "Messages published after the restart were not processed.");
    }

    [TestMethod]
    public async Task A_host_whose_broker_is_down_at_boot_starts_reports_not_connected_and_connects_when_it_returns()
    {
        var plan = new WorkPlan();
        plan.Types["late"] = new ConsumerOptions();
        await Broker.StopApp(_container);

        await using var harness = await BusHarness.StartAsync(_container.GetConnectionString(),
            configureServices: s => s.AddSingleton(plan), waitForTopology: false);
        var status = harness.Services.GetRequiredService<IBusConnectionStatus>();
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.IsFalse(status.IsConnected);
        Assert.IsNotNull(status.LastError);

        await Broker.StartApp(_container);
        Assert.IsTrue(await Broker.Eventually(() => status.IsConnected, 60), "The bus never connected.");
        var queue = $"{harness.Options.ProcessExchange}.itest.workconsumer.late";
        Assert.IsTrue(await Broker.Eventually(async () => (await Broker.Queue(_container, queue)).Consumers == 1, 60),
            "The consumer never attached.");

        await ConcurrencyTests.Publish(harness, "late", 1);
        Assert.IsTrue(await Broker.Eventually(() => plan.Log("late").Completed.Count == 1), "The message never arrived.");
    }

    [TestMethod]
    public async Task A_publish_the_broker_blocks_fails_after_the_publish_timeout_and_publishing_resumes()
    {
        var plan = new WorkPlan();
        plan.Types["blocked"] = new ConsumerOptions();
        await using var harness = await BusHarness.StartAsync(_container.GetConnectionString(),
            configureBus: o => o.PublishTimeout = TimeSpan.FromSeconds(2),
            configureServices: s => s.AddSingleton(plan));

        await ConcurrencyTests.Publish(harness, "blocked", 1);
        Assert.IsTrue(await Broker.Eventually(() => plan.Log("blocked").Completed.Count == 1), "The first publish never arrived.");

        await Broker.BlockPublishers(_container);
        try
        {
            await using var scope = harness.Services.CreateAsyncScope();
            var elapsed = Stopwatch.StartNew();
            var publish = scope.ServiceProvider.GetRequiredService<IPublish>().Publish("blocked", "held");
            var finished = await Task.WhenAny(publish, Task.Delay(TimeSpan.FromSeconds(30)));
            elapsed.Stop();

            Assert.AreSame(publish, finished, "A publish to a broker blocking publishers waited past its timeout.");
            Assert.IsTrue(publish.IsFaulted, "A publish the broker never took returned as if it was sent.");
            var chain = new List<Exception>();
            for (Exception? e = publish.Exception; e != null; e = e.InnerException) chain.Add(e);
            Assert.IsTrue(chain.OfType<BusException>().Any(), $"Expected a BusException, got: {publish.Exception}");
            Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"The publish took {elapsed.Elapsed}.");
        }
        finally
        {
            await Broker.UnblockPublishers(_container);
        }

        Assert.IsTrue(await Broker.Eventually(async () =>
        {
            try
            {
                await ConcurrencyTests.Publish(harness, "blocked", 1);
                return true;
            }
            catch
            {
                return false;
            }
        }, 60), "Publishing never resumed after the alarm cleared.");
    }

    [TestMethod]
    public async Task The_consumer_connection_is_named_after_the_application()
    {
        var plan = new WorkPlan();
        plan.Types["named"] = new ConsumerOptions();
        await using var harness = await BusHarness.StartAsync(_container.GetConnectionString(),
            configureBus: o => o.ManagementUrl = Broker.ManagementUrl(_container),
            configureServices: s => s.AddSingleton(plan));

        await ConcurrencyTests.Publish(harness, "named", 1);
        Assert.IsTrue(await Broker.Eventually(() => plan.Log("named").Completed.Count == 1), "The message never arrived.");

        var names = await Broker.ConnectionNames(_container);
        CollectionAssert.Contains(names, "SW.Bus.IntegrationTests Consumer", string.Join(", ", names));
        CollectionAssert.DoesNotContain(names, "SW.Bus Consumer");
    }
}
