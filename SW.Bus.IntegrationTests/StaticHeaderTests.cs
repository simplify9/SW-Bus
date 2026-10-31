using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RabbitMQ.Client;
using SW.PrimitiveTypes;
using Testcontainers.RabbitMq;

namespace SW.Bus.IntegrationTests;

public class HeaderedPlaced { public string Number { get; set; } = "SO-H"; }

/// <summary>BusOptions.Headers go on every published message, without replacing a header the bus sets itself.</summary>
[TestClass]
public class StaticHeaderTests
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
    public async Task Every_published_message_carries_the_configured_headers()
    {
        await using var harness = await BusHarness.StartAsync(_container.GetConnectionString(), o =>
        {
            o.Headers["deployment"] = "acme";
            o.Headers["Id"] = "not-the-message-id";
            o.Headers["empty"] = "";
        });
        await using var conn = await new ConnectionFactory { Uri = new Uri(_container.GetConnectionString()) }.CreateConnectionAsync();
        await using var channel = await conn.CreateChannelAsync();
        var queue = (await channel.QueueDeclareAsync()).QueueName;
        await channel.QueueBindAsync(queue, harness.Options.ProcessExchange, nameof(HeaderedPlaced).ToLower());

        await using (var scope = harness.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IPublish>().Publish(new HeaderedPlaced());

        BasicGetResult message = null;
        for (var attempt = 0; attempt < 50 && message == null; attempt++)
        {
            message = await channel.BasicGetAsync(queue, autoAck: true);
            if (message == null) await Task.Delay(100);
        }
        Assert.IsNotNull(message, "the message never arrived");
        var headers = message.BasicProperties.Headers!;
        Assert.AreEqual("acme", Encoding.UTF8.GetString((byte[])headers["deployment"]!));
        Assert.AreEqual(message.BasicProperties.MessageId, Encoding.UTF8.GetString((byte[])headers["Id"]!), "the bus's own Id header wins");
        Assert.IsFalse(headers.ContainsKey("empty"), "an empty value is left out");
    }
}
