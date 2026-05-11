using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SW.Bus.SampleWeb.Models;
using SW.PrimitiveTypes;

namespace SW.Bus.SampleWeb.Consumers
{
    /// <summary>
    /// Original ConsumeCarDto — updated to simulate 15–60 ms processing.
    /// </summary>
    public class ConsumeCarDto : IConsume<CarDto>
    {
        private static readonly Random Rng = Random.Shared;
        private readonly ILogger<ConsumeCarDto> logger;
        private readonly RequestContext requestContext;

        public ConsumeCarDto(ILogger<ConsumeCarDto> logger, RequestContext requestContext)
        {
            this.logger = logger;
            this.requestContext = requestContext;
        }

        public async Task Process(CarDto message)
        {
            await Task.Delay(Rng.Next(15, 60));
            logger.LogInformation("Car processed: Model={Model}, User={User}",
                message.Model, requestContext.User?.Identity?.Name ?? "anonymous");
        }
    }
}

