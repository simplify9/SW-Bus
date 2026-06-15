using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Testcontainers.RabbitMq;

namespace SW.Bus.IntegrationTests;

/// <summary>
/// Broker WITH the rabbitmq_delayed_message_exchange plugin enabled.
/// Uses the community image that bundles the plugin pre-enabled.
/// </summary>
[TestClass]
public class PluginAvailableTests
{
    private static RabbitMqContainer _container = null!;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext _)
    {
        _container = new RabbitMqBuilder()
            .WithImage("heidiks/rabbitmq-delayed-message-exchange:3.13.0-management")
            .Build();
        await _container.StartAsync();
    }

    [ClassCleanup]
    public static async Task ClassCleanup() => await _container.DisposeAsync();

    [TestMethod]
    public async Task Delivers_delayed_message_via_plugin()
        => await DelayedDeliveryScenario.RunAsync(_container.GetConnectionString(), expectPluginAvailable: true);
}
