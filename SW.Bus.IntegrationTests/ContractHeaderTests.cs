using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RabbitMQ.Client;
using SW.PrimitiveTypes;
using Testcontainers.RabbitMq;

namespace SW.Bus.IntegrationTests;

[Contract("orders.msg.placed", Major = 2)]
public class ContractedPlaced { public string Number { get; set; } = "SO-1"; }

public class UncontractedPlaced { public string Number { get; set; } = "SO-2"; }

[Contract("Not A Key")]
public class BadlyContractedPlaced { public string Number { get; set; } = "SO-3"; }

/// <summary>
/// A message whose type declares a contract carries it in the "contract" header, so a consumer can
/// tell which shape the body has without guessing from the class name.
/// </summary>
[TestClass]
public class ContractHeaderTests
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
    public async Task A_contracted_message_carries_key_and_major_and_others_carry_nothing()
    {
        await using var harness = await BusHarness.StartAsync(_container.GetConnectionString());
        await using var conn = await new ConnectionFactory { Uri = new Uri(_container.GetConnectionString()) }.CreateConnectionAsync();
        await using var channel = await conn.CreateChannelAsync();
        var queue = (await channel.QueueDeclareAsync()).QueueName;
        foreach (var type in new[] { nameof(ContractedPlaced), nameof(UncontractedPlaced), nameof(BadlyContractedPlaced) })
            await channel.QueueBindAsync(queue, harness.Options.ProcessExchange, type.ToLower());

        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var publish = scope.ServiceProvider.GetRequiredService<IPublish>();
            await publish.Publish(new ContractedPlaced());
            await publish.Publish(new UncontractedPlaced());
            await publish.Publish(new BadlyContractedPlaced());
        }

        Assert.AreEqual("orders.msg.placed@2", await ContractHeaderOf(channel, queue, "SO-1"));
        Assert.IsNull(await ContractHeaderOf(channel, queue, "SO-2"), "a type with no contract gets no header");
        Assert.IsNull(await ContractHeaderOf(channel, queue, "SO-3"), "a malformed key is left out, not published");
    }

    private static async Task<string?> ContractHeaderOf(IChannel channel, string queue, string number)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var message = await channel.BasicGetAsync(queue, autoAck: false);
            if (message == null) { await Task.Delay(100); continue; }
            if (!Encoding.UTF8.GetString(message.Body.Span).Contains(number))
            {
                await channel.BasicNackAsync(message.DeliveryTag, false, requeue: true);
                continue;
            }
            await channel.BasicAckAsync(message.DeliveryTag, false);
            return message.BasicProperties.Headers != null
                   && message.BasicProperties.Headers.TryGetValue(BusOptions.ContractHeaderName, out var value)
                ? Encoding.UTF8.GetString((byte[])value!)
                : null;
        }
        Assert.Fail($"The message with {number} never arrived.");
        return null;
    }
}
