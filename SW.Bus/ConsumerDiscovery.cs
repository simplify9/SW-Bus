using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SW.Bus.RabbitMqExtensions;
using SW.PrimitiveTypes;

namespace SW.Bus;

public class ConsumerDiscovery(IServiceProvider sp, BusOptions busOptions)
{
    internal async Task<ICollection<ConsumerDefinition>> Load(bool consumersOnly = false)
    {
        var consumerDefinitions = new List<ConsumerDefinition>();
        var queueNamePrefix =
            $"{busOptions.ProcessExchange}{(string.IsNullOrWhiteSpace(busOptions.ApplicationName) ? "" : $".{busOptions.ApplicationName}")}";

        using var scope = sp.CreateScope();
        var consumers = scope.ServiceProvider.GetServices<IConsume>();
        foreach (var svc in consumers)
        {
            if (svc is IConsumeExtended extendedSvc)
            {
                var messageTypesWithOptions = await extendedSvc.GetMessageTypeNamesWithOptions();
                foreach (var kvp in messageTypesWithOptions)
                {
                    consumerDefinitions.Add(new ConsumerDefinition(queueNamePrefix, busOptions, kvp.Value,
                        $"{svc.GetType().Name}.{kvp.Key}".ToLower())
                    {
                        ServiceType = svc.GetType(),
                        MessageTypeName = kvp.Key,
                    });
                }
            }
            else
            {
                foreach (var messageTypeName in await svc.GetMessageTypeNames())

                    consumerDefinitions.Add(new ConsumerDefinition(queueNamePrefix, busOptions,
                        $"{svc.GetType().Name}.{messageTypeName}".ToLower())
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
            foreach (var type in svc.GetType().GetTypeInfo().ImplementedInterfaces
                         .Where(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IConsume<>)))
            {
                var messageType = type.GetGenericArguments()[0];
                ConsumerOptions options = null;
                var extendedInterface = typeof(IConsumeExtended<>).MakeGenericType(messageType);

                if (extendedInterface.IsAssignableFrom(svc.GetType()))
                {
                    var method = extendedInterface.GetMethod(nameof(IConsumeExtended<object>.GetConsumerOptions));
                    if(method == null)
                        throw new InvalidOperationException(
                            $"Method {nameof(IConsumeExtended<object>.GetConsumerOptions)} not found in {extendedInterface.Name}");
                    
                    options = await ((Task<ConsumerOptions>)method.Invoke(svc, null))!;
                }

                if (options != null)
                {
                    consumerDefinitions.Add(new ConsumerDefinition(queueNamePrefix, busOptions, options,
                        $"{svc.GetType().Name}.{messageType.Name}".ToLower())
                    {
                        ServiceType = svc.GetType(),
                        MessageType = messageType,
                        MessageTypeName = messageType.Name,
                        Method = type.GetMethod("Process"),
                    });
                }
                else
                {
                    consumerDefinitions.Add(new ConsumerDefinition(queueNamePrefix, busOptions,
                        $"{svc.GetType().Name}.{messageType.Name}".ToLower())
                    {
                        ServiceType = svc.GetType(),
                        MessageType = messageType,
                        MessageTypeName = messageType.Name,
                        Method = type.GetMethod("Process"),
                    });
                }
            }

        return consumerDefinitions;
    }

    internal ICollection<ListenerDefinition> LoadListeners()
    {
        var consumerDefinitions = new List<ListenerDefinition>();
        using var scope = sp.CreateScope();

        var genericConsumers = scope.ServiceProvider.GetServices<IListenGenericBase>();
        foreach (var svc in genericConsumers)
        foreach (var type in svc.GetType().GetTypeInfo().ImplementedInterfaces
                     .Where(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IListen<>)))
            consumerDefinitions.Add(new ListenerDefinition
            {
                ServiceType = svc.GetType(),
                MessageType = type.GetGenericArguments()[0],
                MessageTypeName = type.GetGenericArguments()[0].Name,
                Method = type.GetMethod("Process"),
                FailMethod = type.GetMethod("OnFail")
            });

        return consumerDefinitions;
    }
}