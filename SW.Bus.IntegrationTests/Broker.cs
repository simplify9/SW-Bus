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
