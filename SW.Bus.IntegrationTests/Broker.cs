using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Testcontainers.RabbitMq;

namespace SW.Bus.IntegrationTests;

/// <summary>Helpers over a Testcontainers broker shared by the tests of a class.</summary>
public static class Broker
{
    public static RabbitMqContainer Create() => new RabbitMqBuilder()
        .WithImage("rabbitmq:3.13-management")
        .WithPortBinding(15672, true)
        .Build();

    public static string ManagementUrl(RabbitMqContainer container) =>
        $"http://{container.Hostname}:{container.GetMappedPublicPort(15672)}";

    /// <summary>Stops the broker application inside the container: every client connection drops.</summary>
    public static async Task StopApp(RabbitMqContainer container) =>
        await Exec(container, "rabbitmqctl", "stop_app");

    public static async Task StartApp(RabbitMqContainer container)
    {
        await Exec(container, "rabbitmqctl", "start_app");
        await Exec(container, "rabbitmqctl", "await_startup");
    }

    /// <summary>
    /// Raises a memory alarm, as a broker short of memory does: it blocks every connection that publishes,
    /// and stops reading from it, until the alarm clears. Consumers are not blocked.
    /// </summary>
    public static async Task BlockPublishers(RabbitMqContainer container) =>
        await Exec(container, "rabbitmqctl", "set_vm_memory_high_watermark", "absolute", "1");

    public static async Task UnblockPublishers(RabbitMqContainer container) =>
        await Exec(container, "rabbitmqctl", "set_vm_memory_high_watermark", "0.4");

    /// <summary>The client-provided names of the broker's connections, read through the management API.</summary>
    public static async Task<string[]> ConnectionNames(RabbitMqContainer container)
    {
        using var all = await Management(container, "/api/connections");
        if (all == null) return [];
        return all.RootElement.EnumerateArray()
            .Select(c => c.TryGetProperty("client_properties", out var p) && p.TryGetProperty("connection_name", out var n)
                ? n.GetString()
                : null)
            .Where(n => n != null)
            .ToArray()!;
    }

    private static async Task Exec(RabbitMqContainer container, params string[] command)
    {
        var result = await container.ExecAsync(command);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{string.Join(' ', command)} failed: {result.Stderr}");
    }

    /// <summary>Ready plus unacknowledged messages, and consumers, of a queue, read on a channel of its own.</summary>
    public static async Task<(uint Messages, uint Consumers)> Queue(RabbitMqContainer container, string queue)
    {
        await using var conn = await new ConnectionFactory { Uri = new Uri(container.GetConnectionString()) }
            .CreateConnectionAsync();
        await using var channel = await conn.CreateChannelAsync();
        try
        {
            var ok = await channel.QueueDeclarePassiveAsync(queue);
            return (ok.MessageCount, ok.ConsumerCount);
        }
        catch (OperationInterruptedException)
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// Consumers of a queue read through the management API, which also sees exclusive queues that
    /// a passive declare from another connection is refused.
    /// </summary>
    public static async Task<long> ManagementConsumers(RabbitMqContainer container, string queue)
    {
        using var json = await Management(container, $"/api/queues/%2F/{Uri.EscapeDataString(queue)}");
        return json != null && json.RootElement.TryGetProperty("consumers", out var consumers) ? consumers.GetInt64() : 0;
    }

    /// <summary>The prefetch of each consumer of a queue, as the broker holds it.</summary>
    public static async Task<long[]> ManagementPrefetch(RabbitMqContainer container, string queue)
    {
        using var all = await Management(container, "/api/consumers");
        if (all == null) return [];
        return all.RootElement.EnumerateArray()
            .Where(c => c.GetProperty("queue").GetProperty("name").GetString() == queue)
            .Select(c => c.GetProperty("prefetch_count").GetInt64())
            .ToArray();
    }

    private static async Task<JsonDocument?> Management(RabbitMqContainer container, string path)
    {
        var user = new Uri(container.GetConnectionString()).UserInfo;
        using var http = new HttpClient { BaseAddress = new Uri(ManagementUrl(container)) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(user))));
        var response = await http.GetAsync(path);
        if (!response.IsSuccessStatusCode) return null;
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    public static async Task<bool> Eventually(Func<Task<bool>> condition, int seconds = 30)
    {
        var sw = Stopwatch.StartNew();
        while (!await condition() && sw.Elapsed < TimeSpan.FromSeconds(seconds))
            await Task.Delay(250);
        return await condition();
    }

    public static Task<bool> Eventually(Func<bool> condition, int seconds = 30) =>
        Eventually(() => Task.FromResult(condition()), seconds);
}
