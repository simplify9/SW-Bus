using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SW.Bus.RabbitMqExtensions;

namespace SW.Bus.IntegrationTests;

/// <summary>
/// Shared assertions exercised against both broker variants (plugin available / not available).
/// </summary>
internal static class DelayedDeliveryScenario
{
    public static async Task RunAsync(string connectionString, bool expectPluginAvailable)
    {
        await using var harness = await BusHarness.StartAsync(connectionString);

        Assert.AreEqual(expectPluginAvailable, harness.Options.DelayedPluginAvailable,
            "Detected plugin availability did not match the broker variant under test.");

        var delay = TimeSpan.FromSeconds(5);
        var id = Guid.NewGuid().ToString("N");

        using (var scope = harness.Services.CreateScope())
        {
            var delayed = scope.ServiceProvider.GetRequiredService<IDelayedPublish>();
            await delayed.PublishDelayed(new DelayDto { Id = id }, nameof(DelayedConsumer), delay);
        }

        var sw = Stopwatch.StartNew();

        // It must NOT arrive early. Check shortly before the delay elapses.
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.IsFalse(harness.Sink.ReceivedAtUtc.ContainsKey(id),
            "Delayed message was delivered before its delay elapsed.");

        // It MUST arrive after the delay (allow generous slack for TTL-bucket rounding + scheduling).
        var deadline = TimeSpan.FromSeconds(25);
        while (!harness.Sink.ReceivedAtUtc.ContainsKey(id) && sw.Elapsed < deadline)
            await Task.Delay(250);

        Assert.IsTrue(harness.Sink.ReceivedAtUtc.ContainsKey(id),
            $"Delayed message was never delivered within {deadline.TotalSeconds:0}s.");
    }
}
