using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SW.Bus.SampleWeb.Models;
using SW.PrimitiveTypes;

namespace SW.Bus.SampleWeb.Consumers
{
    /// <summary>
    /// Scenario 13 — PersonDto consumer that fails ~50 % of the time (original BuggyConsumer, activated).
    /// Generates a continuous flow of retries and dead-letters for PersonDto messages,
    /// showing the retry/DL dashboard sections always have some data.
    /// </summary>
    public class BuggyConsumer : IConsume<PersonDto>
    {
        private static readonly Random Rng = Random.Shared;
        private readonly ILogger<BuggyConsumer> logger;

        public BuggyConsumer(ILogger<BuggyConsumer> logger) => this.logger = logger;

        public async Task Process(PersonDto message)
        {
            await Task.Delay(Rng.Next(20, 150));

            if (Rng.NextDouble() < 0.50)
                throw new Exception($"Simulated 50 % transient failure processing person '{message.Name}'.");

            logger.LogInformation("Person processed: {Name}", message.Name);
        }

        public Task OnFail(Exception ex)
        {
            logger.LogError(ex, "BuggyConsumer OnFail.");
            return Task.CompletedTask;
        }
    }
}

