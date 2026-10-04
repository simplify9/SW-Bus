using System.Collections.Generic;
using System.Threading.Tasks;
using SW.PrimitiveTypes;

namespace SW.Bus.RabbitMqExtensions;

/// <summary>
/// Publishes a message with values that travel beside its body rather than in it.
/// </summary>
/// <remarks>
/// <para>
/// For application data a consumer needs about where a message came from, when the body is not
/// the publisher's to change — it may be read by consumers that know nothing of the values.
/// </para>
/// <para>
/// The consumer reads each one back from its <see cref="RequestContext"/> as a
/// <see cref="RequestValueType.ServiceBusValue"/>, the same way it reads <c>RemainingRetries</c>.
/// Those names belong to the bus, so a value given one of them is not delivered.
/// </para>
/// <para>
/// They are message headers: anything that can read the queue can read them, and a message that
/// fails for good keeps them in its dead-letter queue. Never put a secret in one.
/// </para>
/// </remarks>
public interface IPublishWithValues
{
    /// <summary>
    /// The most the values may come to, as UTF-8 JSON. Every header shares one AMQP frame (128 KB
    /// unless the broker says otherwise), and a frame the broker refuses closes the channel every
    /// publish in the application shares, so values past this are refused before anything is sent.
    /// </summary>
    const int MaxValuesBytes = 64 * 1024;

    /// <exception cref="System.ArgumentException">The values come to more than <see cref="MaxValuesBytes"/>.</exception>
    Task Publish(string messageTypeName, string message, IReadOnlyDictionary<string, string> values);
}
