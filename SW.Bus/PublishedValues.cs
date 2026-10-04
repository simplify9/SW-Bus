using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using SW.Bus.RabbitMqExtensions;

namespace SW.Bus;

/// <summary>How <see cref="IPublishWithValues"/> values are written into their one header.</summary>
internal static class PublishedValues
{
    /// <summary>The header value, or null when there is nothing to send.</summary>
    /// <exception cref="ArgumentException">The values come to more than <see cref="IPublishWithValues.MaxValuesBytes"/>.</exception>
    public static string ToHeader(IReadOnlyDictionary<string, string> values)
    {
        if (values is not { Count: > 0 }) return null;

        var header = JsonSerializer.Serialize(values);
        var bytes = Encoding.UTF8.GetByteCount(header);
        if (bytes > IPublishWithValues.MaxValuesBytes)
            throw new ArgumentException(
                $"The values come to {bytes} bytes; at most {IPublishWithValues.MaxValuesBytes} can be published.",
                nameof(values));
        return header;
    }
}
