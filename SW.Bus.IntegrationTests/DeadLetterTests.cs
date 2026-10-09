using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RabbitMQ.Client;
using SW.PrimitiveTypes;
using Testcontainers.RabbitMq;

namespace SW.Bus.IntegrationTests;

/// <summary>
/// A message that fails every retry is parked in its bad queue against a real broker — never lost —
/// with the exception that stopped it recorded beside it.
/// </summary>
[TestClass]
public class DeadLetterTests
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
    public async Task A_message_that_fails_every_retry_is_parked_in_the_bad_queue_with_its_exception()
    {
        await using var harness = await BusHarness.StartAsync(_container.GetConnectionString(), o =>
        {
            o.DefaultRetryCount = 2;
            o.DefaultRetryAfter = 1;
        });
        var badQueue = $"{harness.Options.ProcessExchange}.itest.failingconsumer.poison.bad";

        await using (var scope = harness.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IPublish>().Publish("poison", "{\"order\":7}");

        using var connection = new ConnectionFactory { Uri = new Uri(_container.GetConnectionString()) }
            .CreateConnection();
        using var channel = connection.CreateModel();

        BasicGetResult? parked = null;
        var clock = Stopwatch.StartNew();
        while (parked is null && clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(250);
            parked = channel.BasicGet(badQueue, autoAck: true);
        }

        Assert.IsNotNull(parked, $"Nothing reached {badQueue}: the message was lost.");
        Assert.AreEqual("{\"order\":7}", Encoding.UTF8.GetString(parked.Body.ToArray()));
        Assert.IsTrue(clock.Elapsed >= TimeSpan.FromSeconds(1.5), "Parked before it was retried.");

        var header = parked.BasicProperties.Headers["exception1"];
        using var recorded = JsonDocument.Parse(Encoding.UTF8.GetString((byte[])header));
        Assert.AreEqual(typeof(InvalidOperationException).FullName, recorded.RootElement.GetProperty("ClassName").GetString());
        Assert.AreEqual("cannot process {\"order\":7}", recorded.RootElement.GetProperty("Message").GetString());
        Assert.IsFalse(string.IsNullOrEmpty(recorded.RootElement.GetProperty("StackTraceString").GetString()));
        Assert.AreEqual("inner cause", recorded.RootElement.GetProperty("InnerException").GetProperty("Message").GetString());

        Assert.IsNull(channel.BasicGet(badQueue, autoAck: true), "Parked more than once.");
    }

    [TestMethod]
    public void Every_thrown_exception_can_be_recorded()
    {
        foreach (var thrown in new Func<Exception>[]
                 {
                     () => Catch(() => throw new InvalidOperationException("plain")),
                     () => Catch(() => JsonSerializer.Deserialize<int>("not json")),
                     () => Catch(() => throw new AggregateException(new Exception("a"), new Exception("b"))),
                 })
        {
            var ex = thrown();
            using var recorded = JsonDocument.Parse(ConsumerRunner.ExceptionHeader(ex));
            Assert.AreEqual(ex.GetType().FullName, recorded.RootElement.GetProperty("ClassName").GetString());
        }

        static Exception Catch(Action action)
        {
            try { action(); }
            catch (Exception e) { return e; }
            throw new AssertFailedException("did not throw");
        }
    }
}
