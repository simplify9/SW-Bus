using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SW.PrimitiveTypes;

namespace SW.Bus.IntegrationTests;

/// <summary>Fails every "poison" message it is given, as a consumer meeting a message it cannot read.</summary>
public class FailingConsumer : IConsume
{
    public Task<IEnumerable<string>> GetMessageTypeNames() => Task.FromResult<IEnumerable<string>>(["poison"]);

    public Task Process(string messageTypeName, string message) =>
        throw new InvalidOperationException($"cannot process {message}", new FormatException("inner cause"));
}
