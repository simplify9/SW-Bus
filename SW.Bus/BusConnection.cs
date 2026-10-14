using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using SW.Bus.RabbitMqExtensions;

namespace SW.Bus;

/// <summary>Client names the broker shows for the bus's connections.</summary>
internal sealed class BusConnectionNames
{
    public string Publisher { get; set; }
    public string Consumer { get; set; }
}

/// <summary>
/// The bus's connections to the broker: one for publishing and one for consuming, so the broker's
/// flow control on a busy publisher never holds back consumer acknowledgements. Both are opened
/// asynchronously, never inside service registration, and the client's automatic recovery owns
/// reconnects and topology recovery (exchanges, queues, bindings and consumers) after an outage.
/// </summary>
/// <remarks>
/// As a hosted service it declares the exchanges at start, retrying in the background until the
/// broker answers, so a host whose broker is down at boot still starts and connects later.
/// </remarks>
internal sealed class BusConnection : IHostedService, IBusConnectionStatus, IAsyncDisposable, IDisposable
{
    /// <summary>
    /// How long a close waits. The client's channel and connection close wait for consumer handlers
    /// still running; past the drain the broker has requeued their messages, so nothing is gained by
    /// waiting longer, and a hung handler must not hold the host's shutdown.
    /// </summary>
    internal static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(1);

    private readonly BusOptions busOptions;
    private readonly BusConnectionNames names;
    private readonly ILogger<BusConnection> logger;
    private readonly ConnectionFactory publisherFactory;
    private readonly ConnectionFactory consumerFactory;
    private readonly SemaphoreSlim connectLock = new(1, 1);
    private readonly TaskCompletionSource exchangesDeclared = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource stopping = new();

    private IConnection publisherConnection;
    private IConnection consumerConnection;
    private Task startTask = Task.CompletedTask;

    public BusConnection(BusOptions busOptions, BusConnectionNames names, ILogger<BusConnection> logger)
    {
        this.busOptions = busOptions;
        this.names = names;
        this.logger = logger;
        var uri = new Uri(busOptions.ConnectionString);

        // The publisher keeps the client's default heartbeat, the consumer takes HeartBeatTimeOut,
        // as the two connections did on client 6.
        publisherFactory = new ConnectionFactory
        {
            Uri = uri,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true
        };
        consumerFactory = new ConnectionFactory
        {
            Uri = uri,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            RequestedHeartbeat = busOptions.RequestedHeartbeat
        };
    }

    public bool IsConnected =>
        exchangesDeclared.Task.IsCompletedSuccessfully &&
        publisherConnection is { IsOpen: true } &&
        consumerConnection is null or { IsOpen: true };

    public string LastError { get; private set; }

    /// <summary>
    /// Set by the consumer service when it stopped with handlers still running past the drain. Their
    /// messages are already requeued by the broker; closing the consumer connection would only wait
    /// for the handlers, so it closes in the background instead.
    /// </summary>
    internal bool ConsumerHandlersStillRunning { get; set; }

    /// <summary>Completes once the exchanges are declared; consumer queues bind to them.</summary>
    public Task WaitForExchangesAsync(CancellationToken cancellationToken) =>
        exchangesDeclared.Task.WaitAsync(cancellationToken);

    public Task<IConnection> GetPublisherConnectionAsync(CancellationToken cancellationToken = default) =>
        GetConnectionAsync(publisher: true, cancellationToken);

    public Task<IConnection> GetConsumerConnectionAsync(CancellationToken cancellationToken = default) =>
        GetConnectionAsync(publisher: false, cancellationToken);

    /// <summary>
    /// The connection, opened on first use. Once opened, the same connection is returned for the life
    /// of the bus, also while the client is recovering it: channels created on it recover with it.
    /// Throws when the broker cannot be reached and the connection was never opened.
    /// </summary>
    private async Task<IConnection> GetConnectionAsync(bool publisher, CancellationToken cancellationToken)
    {
        var existing = publisher ? publisherConnection : consumerConnection;
        if (existing != null) return existing;

        await connectLock.WaitAsync(cancellationToken);
        try
        {
            existing = publisher ? publisherConnection : consumerConnection;
            if (existing != null) return existing;

            var factory = publisher ? publisherFactory : consumerFactory;
            var connection = await factory.CreateConnectionAsync(
                publisher ? names.Publisher : names.Consumer, cancellationToken);
            connection.ConnectionShutdownAsync += (_, args) =>
            {
                if (args.Initiator != ShutdownInitiator.Application)
                    logger.LogWarning("RabbitMQ {Connection} connection lost, the client is recovering it. {Cause}",
                        publisher ? "publisher" : "consumer", args.ReplyText);
                return Task.CompletedTask;
            };
            connection.RecoverySucceededAsync += (_, _) =>
            {
                logger.LogInformation("RabbitMQ {Connection} connection recovered.", publisher ? "publisher" : "consumer");
                return Task.CompletedTask;
            };
            connection.ConnectionRecoveryErrorAsync += (_, args) =>
            {
                LastError = args.Exception.Message;
                logger.LogWarning(args.Exception, "RabbitMQ {Connection} connection recovery attempt failed.",
                    publisher ? "publisher" : "consumer");
                return Task.CompletedTask;
            };

            if (publisher) publisherConnection = connection;
            else consumerConnection = connection;
            return connection;
        }
        finally
        {
            connectLock.Release();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Not awaited: the host starts whether or not the broker is up.
        startTask = Task.Run(() => DeclareExchangesAsync(stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task DeclareExchangesAsync(CancellationToken cancellationToken)
    {
        var retryCount = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var connection = await GetPublisherConnectionAsync(cancellationToken);
                await using (var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken))
                {
                    await channel.ExchangeDeclareAsync(busOptions.ProcessExchange, ExchangeType.Direct, true,
                        cancellationToken: cancellationToken);
                    await channel.ExchangeDeclareAsync(busOptions.DeadLetterExchange, ExchangeType.Direct, true,
                        cancellationToken: cancellationToken);
                    await channel.ExchangeDeclareAsync(busOptions.NodeExchange, ExchangeType.Direct, true,
                        cancellationToken: cancellationToken);
                    await channel.ExchangeDeclareAsync(busOptions.NodeDeadLetterExchange, ExchangeType.Direct, true,
                        cancellationToken: cancellationToken);
                    await channel.CloseAsync(cancellationToken);
                }

                LastError = null;
                exchangesDeclared.TrySetResult();
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                retryCount++;
                var delay = Math.Min(retryCount * 2, 30);
                logger.LogError(ex, "Failed to connect to RabbitMQ and declare the bus exchanges. Retrying in {Delay}s.", delay);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Runs after the consumers have drained (hosted services stop in reverse order of registration,
    /// and this one is registered first), so a message finishing during the drain can still publish.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await stopping.CancelAsync();
        try
        {
            await startTask;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Exchange declaration ended with an error during stop.");
        }

        if (ConsumerHandlersStillRunning) InBackground(consumerConnection, c => c.CloseAsync(), "close");
        else await CloseAsync(consumerConnection, cancellationToken);
        await CloseAsync(publisherConnection, cancellationToken);
    }

    private async Task CloseAsync(IConnection connection, CancellationToken cancellationToken)
    {
        if (connection == null) return;
        await Bounded(async () =>
        {
            if (connection.IsOpen) await connection.CloseAsync(cancellationToken);
        }, "close", cancellationToken);
    }

    private void InBackground(IConnection connection, Func<IConnection, Task> action, string what)
    {
        if (connection == null) return;
        _ = Task.Run(() => action(connection)).ContinueWith(
            t => logger.LogDebug(t.Exception, "RabbitMQ connection {What} failed.", what),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task Bounded(Func<Task> action, string what, CancellationToken cancellationToken = default)
    {
        var task = Task.Run(action, CancellationToken.None);
        var finished = await Task.WhenAny(task, Task.Delay(CloseTimeout, cancellationToken)).ConfigureAwait(false);
        if (finished != task)
        {
            logger.LogWarning("A RabbitMQ connection {What} did not finish in {Timeout}; a consumer handler is still running.",
                what, CloseTimeout);
            _ = task.ContinueWith(t => logger.LogDebug(t.Exception, "RabbitMQ connection {What} failed.", what),
                TaskContinuationOptions.OnlyOnFaulted);
            return;
        }

        try
        {
            await task;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to {What} a RabbitMQ connection.", what);
        }
    }

    public async ValueTask DisposeAsync()
    {
        var consumer = consumerConnection;
        var publisher = publisherConnection;
        consumerConnection = null;
        publisherConnection = null;
        if (ConsumerHandlersStillRunning) InBackground(consumer, c => c.DisposeAsync().AsTask(), "dispose");
        else if (consumer != null) await Bounded(() => consumer.DisposeAsync().AsTask(), "dispose");
        if (publisher != null) await Bounded(() => publisher.DisposeAsync().AsTask(), "dispose");
        stopping.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
