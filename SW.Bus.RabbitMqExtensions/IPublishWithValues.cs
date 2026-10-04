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
/// </remarks>
public interface IPublishWithValues
{
    Task Publish(string messageTypeName, string message, IReadOnlyDictionary<string, string> values);
}
