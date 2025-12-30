using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SW.PrimitiveTypes;
using SW.Bus.RabbitMqExtensions;

namespace SW.Bus
{
    public class ConsumerDiscovery
    {
        private readonly IServiceProvider sp;
        private readonly BusOptions busOptions;
        private readonly ILogger<ConsumerDiscovery> logger;

        public ConsumerDiscovery(IServiceProvider sp, BusOptions busOptions, ILogger<ConsumerDiscovery> logger)
        {
            this.sp = sp;
            this.busOptions = busOptions;
            this.logger = logger;
        }

        internal async Task<ICollection<ConsumerDefinition>> Load(bool consumersOnly = false)
        {
            var consumerDefinitions = new List<ConsumerDefinition>();
            var queueNamePrefix = $"{busOptions.ProcessExchange}{(string.IsNullOrWhiteSpace(busOptions.ApplicationName) ? "" : $".{busOptions.ApplicationName}")}";

            using var scope = sp.CreateScope();
            var consumers = scope.ServiceProvider.GetServices<IConsume>();
            foreach (var svc in consumers)
            {
                if (svc is IConsumeExtended consumeExtended)
                {
                    var options = await consumeExtended.GetQueOptions();
                    foreach (var kvp in options)
                    {
                        var messageTypeName = kvp.Key;
                        var extOptions = kvp.Value;

                        if (extOptions.MaxPriority > 5)
                        {
                            logger.LogError($"MaxPriority for {messageTypeName} is {extOptions.MaxPriority}, which is greater than 5. It will be capped at 5.");
                        }

                        var queueOptions = extOptions;
                        
                        consumerDefinitions.Add(new ConsumerDefinition(queueNamePrefix, busOptions, $"{svc.GetType().Name}.{messageTypeName}".ToLower(), queueOptions)
                        {
                            ServiceType = svc.GetType(),
                            MessageTypeName = messageTypeName,
                        });
                    }
                }
                else
                {
                    foreach (var messageTypeName in await svc.GetMessageTypeNames())
                        consumerDefinitions.Add(new ConsumerDefinition(queueNamePrefix, busOptions, $"{svc.GetType().Name}.{messageTypeName}".ToLower())
                        {
                            ServiceType = svc.GetType(),
                            MessageTypeName = messageTypeName,
                        });
                }
            }

            if (consumersOnly)
                return consumerDefinitions;
            var genericConsumers = scope.ServiceProvider.GetServices<IConsumeGenericBase>();
            foreach (var svc in genericConsumers)
            foreach (var type in svc.GetType().GetTypeInfo().ImplementedInterfaces.Where(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IConsume<>)))

                consumerDefinitions.Add(new ConsumerDefinition(queueNamePrefix, busOptions, $"{svc.GetType().Name}.{type.GetGenericArguments()[0].Name}".ToLower())
                {
                    ServiceType = svc.GetType(),
                    MessageType = type.GetGenericArguments()[0],
                    MessageTypeName = type.GetGenericArguments()[0].Name,
                    Method = type.GetMethod("Process"),
                });

            return consumerDefinitions;
        }
        
        internal ICollection<ListenerDefinition> LoadListeners()
        {
            var consumerDefinitions = new List<ListenerDefinition>();
            using var scope = sp.CreateScope();
            
            var genericConsumers = scope.ServiceProvider.GetServices<IListenGenericBase>();
            foreach (var svc in genericConsumers)
            foreach (var type in svc.GetType().GetTypeInfo().ImplementedInterfaces.Where(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IListen<>)))
                consumerDefinitions.Add(new ListenerDefinition
                {
                    ServiceType = svc.GetType(),
                    MessageType = type.GetGenericArguments()[0],
                    MessageTypeName = type.GetGenericArguments()[0].Name,
                    Method = type.GetMethod("Process"),
                    FailMethod= type.GetMethod("OnFail")
                });

            return consumerDefinitions;
        }

    }
}
