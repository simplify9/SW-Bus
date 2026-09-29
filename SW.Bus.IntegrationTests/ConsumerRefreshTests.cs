using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using SW.PrimitiveTypes;
using Testcontainers.RabbitMq;

namespace SW.Bus.IntegrationTests;

/// <summary>
/// <see cref="IBroadcast.RefreshConsumers"/> against a real broker: a consumer whose definition
/// disappears must stop consuming, without its queue being deleted.
/// </summary>
[TestClass]
public class ConsumerRefreshTests
{
    private static RabbitMqContainer _container = null!;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext _)
    {
        _container = new RabbitMqBuilder()
            .WithImage("rabbitmq:3.13-management")
            .Build();
        await _container.StartAsync();
    }

    [ClassCleanup]
    public static async Task ClassCleanup() => await _container.DisposeAsync();

    [TestMethod]
    public async Task Refresh_detaches_a_consumer_that_is_no_longer_defined()
    {
        await using var harness = await BusHarness.StartAsync(_container.GetConnectionString());
        var prefix = $"{harness.Options.ProcessExchange}.itest.dynamicconsumer";

        using var conn = new ConnectionFactory { Uri = new Uri(_container.GetConnectionString()) }.CreateConnection();

        // A channel per check: a passive declare of a queue that doesn't exist yet closes its channel.
        uint Consumers(string name)
        {
            using var model = conn.CreateModel();
            try
            {
                return model.QueueDeclarePassive($"{prefix}.{name}").ConsumerCount;
            }
            catch (OperationInterruptedException)
            {
                return 0;
            }
        }

        // Consumers attach on a background task, so both are waited for rather than assumed.
        Assert.IsTrue(await Eventually(() => Consumers("kept") == 1 && Consumers("removed") == 1),
            "The consumers never attached.");

        harness.Services.GetRequiredService<DynamicMessageTypes>().Names.TryRemove("removed", out _);
        using (var scope = harness.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IBroadcast>().RefreshConsumers();

        Assert.IsTrue(await Eventually(() => Consumers("removed") == 0), "The removed consumer is still attached.");
        Assert.AreEqual(1u, Consumers("kept"), "Refresh detached a consumer that is still defined.");
        // Passive declare throws if the queue is gone: stopping the consumer must leave it in place.
        using var check = conn.CreateModel();
        check.QueueDeclarePassive($"{prefix}.removed");
        check.QueueDeclarePassive($"{prefix}.removed.retry");
        check.QueueDeclarePassive($"{prefix}.removed.bad");
    }

    private static async Task<bool> Eventually(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < TimeSpan.FromSeconds(15))
            await Task.Delay(250);
        return condition();
    }
}
