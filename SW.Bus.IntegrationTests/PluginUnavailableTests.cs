using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Testcontainers.RabbitMq;

namespace SW.Bus.IntegrationTests;

/// <summary>
/// Stock broker WITHOUT the delayed-message plugin. Exercises the TTL delay-bucket fallback path.
/// </summary>
[TestClass]
public class PluginUnavailableTests
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
    public async Task Delivers_delayed_message_via_ttl_buckets()
        => await DelayedDeliveryScenario.RunAsync(_container.GetConnectionString(), expectPluginAvailable: false);
}
