using SW.PrimitiveTypes;
using System;


namespace SW.Bus
{
    public class BusException : SWException
    {
        public BusException(string message) : base(message)
        {
        }

        public BusException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// The user token a message was published with could not be validated by the consumer, typically because
    /// publisher and consumer are configured with different token parameters. Retrying cannot change the outcome,
    /// so the message goes straight to the dead-letter queue.
    /// </summary>
    public class InvalidUserTokenException : BusException
    {
        public InvalidUserTokenException(string messageName, Exception innerException)
            : base($"The user token of a '{messageName}' message could not be validated.", innerException)
        {
        }
    }
}
