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
/// <see cref="IPublishWithValues"/> against a real broker: values sent beside the body reach the
/// consumer's request context, and the body is left exactly as it was.
/// </summary>
[TestClass]
public class PublishedValuesTests
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
    public async Task Values_published_beside_the_body_reach_the_consumer_as_bus_values()
    {
        await using var harness = await BusHarness.StartAsync(_container.GetConnectionString());
        var received = harness.Services.GetRequiredService<ReceivedValues>();

        await using (var scope = harness.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPublishWithValues>().Publish("carried", "{}",
                new Dictionary<string, string>
                {
                    ["source"] = "{\"order\":\"SO-1\"}",
                    // The bus's own name: a publisher must not be able to tell a consumer it has
                    // retries left that it does not.
                    ["RemainingRetries"] = "99",
                });
            await scope.ServiceProvider.GetRequiredService<IPublish>().Publish("carried", "{}");
        }

        Assert.IsTrue(await Eventually(() => received.Messages.Count == 2), "The messages never arrived.");
        var withValues = received.Messages.Single(m => m.ContainsKey("source"));
        Assert.AreEqual("{\"order\":\"SO-1\"}", withValues["source"]);
        Assert.AreEqual(harness.Options.DefaultRetryCount.ToString(), withValues["RemainingRetries"]);

        // A plain publish carries nothing of the kind.
        Assert.AreEqual(1, received.Messages.Count(m => !m.ContainsKey("source")));
    }

    [TestMethod]
    public async Task Values_too_large_for_a_frame_are_refused_before_anything_is_sent()
    {
        await using var harness = await BusHarness.StartAsync(_container.GetConnectionString());
        await using var scope = harness.Services.CreateAsyncScope();
        var publish = scope.ServiceProvider.GetRequiredService<IPublishWithValues>();

        await Assert.ThrowsExceptionAsync<ArgumentException>(() => publish.Publish("carried", "{}",
            new Dictionary<string, string> { ["big"] = new string('x', IPublishWithValues.MaxValuesBytes) }));

        // The channel every publish shares is still open.
        await publish.Publish("carried", "{}", new Dictionary<string, string> { ["small"] = "1" });
        var received = harness.Services.GetRequiredService<ReceivedValues>();
        Assert.IsTrue(await Eventually(() => received.Messages.Any(m => m.ContainsKey("small"))),
            "A publish after the refused one never arrived.");
    }

    private static async Task<bool> Eventually(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < TimeSpan.FromSeconds(15))
            await Task.Delay(250);
        return condition();
    }
}
