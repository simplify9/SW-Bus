using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SW.PrimitiveTypes;

namespace SW.Bus.IntegrationTests;

/// <summary>
/// Boots a real generic host wired with AddBus/AddBusConsume/AddBusPublish against a given RabbitMQ
/// connection string, then waits for consumer topology to settle.
/// </summary>
public sealed class BusHarness : IAsyncDisposable
{
    private readonly IHost host;

    private BusHarness(IHost host) => this.host = host;

    public IServiceProvider Services => host.Services;
    public BusOptions Options => host.Services.GetRequiredService<BusOptions>();

    public static async Task<BusHarness> StartAsync(string amqpConnectionString, Action<BusOptions>? configure = null)
    {
        var host = Host.CreateDefaultBuilder()
            .UseEnvironment("itest")
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:RabbitMQ"] = amqpConnectionString
            }))
            .ConfigureServices(services =>
            {
                services.AddSingleton<DynamicMessageTypes>();
                services.AddSingleton<ReceivedValues>();
                services.AddScoped<RequestContext>();
                services.AddBus(o =>
                {
                    o.ApplicationName = "itest";
                    configure?.Invoke(o);
                });
                services.AddBusConsume(typeof(BusHarness).Assembly);
                services.AddBusPublish();
            })
            .Build();

        await host.StartAsync();

        // ConsumersService attaches topology on a background task; give it time to declare and bind
        // the consumer queues before the test touches them.
        await Task.Delay(TimeSpan.FromSeconds(4));
        return new BusHarness(host);
    }

    public async ValueTask DisposeAsync()
    {
        await host.StopAsync();
        host.Dispose();
    }
}
