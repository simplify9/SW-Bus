namespace SW.Bus.RabbitMqExtensions;

/// <summary>
/// Whether the bus is connected to the broker. The bus connects in the background after the host
/// starts, and reconnects on its own after an outage, so an application can start while the broker
/// is down and use this to report itself not ready until the bus is connected.
/// </summary>
public interface IBusConnectionStatus
{
    /// <summary>
    /// True when the exchanges are declared and every connection the bus has opened is open.
    /// False before the first connection and while the client is recovering from a lost one.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// The error of the last failed attempt to connect or declare the exchanges, or null.
    /// </summary>
    string? LastError { get; }
}
