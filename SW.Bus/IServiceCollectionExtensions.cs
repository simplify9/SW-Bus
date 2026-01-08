﻿using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using SW.HttpExtensions;
using SW.PrimitiveTypes;
using SW.Bus.RabbitMqExtensions;
using System;
using System.Linq;
using System.Reflection;

namespace SW.Bus
{
    public static class IServiceCollectionExtensions
    {
        /// <summary>
        /// Configures the message bus infrastructure including exchanges, consumer reader, and HTTP client for RabbitMQ management API.
        /// This method declares all required exchanges and sets up the consumer discovery and statistics reader.
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
        /// <param name="configure">Optional action to configure <see cref="BusOptions"/>.</param>
        /// <param name="environmentName">Optional environment name. If not provided, uses the current environment from <see cref="IHostEnvironment"/>.</param>
        /// <returns>The <see cref="IServiceCollection"/> for chaining.</returns>
        /// <exception cref="BusException">Thrown when the RabbitMQ connection string is not configured.</exception>
        public static IServiceCollection AddBus(this IServiceCollection services, Action<BusOptions> configure = null,
            string environmentName = null)
        {
            var serviceProvider = services.BuildServiceProvider();
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            var envName = string.IsNullOrEmpty(environmentName)
                ? serviceProvider.GetRequiredService<IHostEnvironment>().EnvironmentName
                : environmentName;


            var busOptions = new BusOptions(envName);

            configure?.Invoke(busOptions);

            services.AddSingleton(busOptions);

            var rabbitUrl = serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("RabbitMQ");

            if (!busOptions.Token.IsValid)
                configuration.GetSection(JwtTokenParameters.ConfigurationSection).Bind(busOptions.Token);

            if (string.IsNullOrEmpty(rabbitUrl))
            {
                throw new BusException("Connection string named 'RabbitMQ' is required.");
            }

            var factory = new ConnectionFactory
            {
                Uri = new Uri(rabbitUrl),
                ClientProvidedName = $"{Assembly.GetCallingAssembly().GetName().Name} Exchange Declarer"
            };

            if (string.IsNullOrEmpty(busOptions.ManagementUrl))
            {
                busOptions.ManagementUrl = $"http://{factory.HostName}:15672";
            }
            if (string.IsNullOrEmpty(busOptions.ManagementUsername))
            {
                busOptions.ManagementUsername = factory.UserName;
            }
            if (string.IsNullOrEmpty(busOptions.ManagementPassword))
            {
                busOptions.ManagementPassword = factory.Password;
            }
            if (string.IsNullOrEmpty(busOptions.VirtualHost))
            {
                busOptions.VirtualHost = factory.VirtualHost;
            }

            services.AddSingleton<IConsumerReader, ConsumerReader>();
            services.AddSingleton<ConsumerDiscovery>();
            services.AddMemoryCache();

            using var conn = factory.CreateConnection();
            using var model = conn.CreateModel();

            model.ExchangeDeclare(busOptions.ProcessExchange, ExchangeType.Direct, true);
            model.ExchangeDeclare(busOptions.DeadLetterExchange, ExchangeType.Direct, true);
            model.ExchangeDeclare(busOptions.NodeExchange, ExchangeType.Direct, true);
            model.ExchangeDeclare(busOptions.NodeDeadLetterExchange, ExchangeType.Direct, true);

            model.Close();
            conn.Close();

            services.AddHttpClient<IConsumerReader, ConsumerReader>((sp, client) =>
            {
                var options = sp.GetRequiredService<BusOptions>();
    
                // Set the BaseAddress here so it's ready for the ManagementClient
                client.BaseAddress = new Uri(options.ManagementUrl);
            });
            
            services.AddHttpClient<IErrorQueueReader, ErrorQueueReader>((sp, client) =>
            {
                var options = sp.GetRequiredService<BusOptions>();
    
                // Set the BaseAddress here so it's ready for the ManagementClient
                client.BaseAddress = new Uri(options.ManagementUrl);
            });

            
            return services;
        }

        /// <summary>
        /// Registers publishing services for sending messages to the message bus.
        /// Configures the publisher for both direct messaging (<see cref="IPublish"/>) and broadcasting (<see cref="IBroadcast"/>).
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
        /// <returns>The <see cref="IServiceCollection"/> for chaining.</returns>
        public static IServiceCollection AddBusPublish(this IServiceCollection services)
        {
            var sp = services.BuildServiceProvider();
            var rabbitUrl = sp.GetRequiredService<IConfiguration>().GetConnectionString("RabbitMQ");
            var busOptions = sp.GetRequiredService<BusOptions>();

            var factory = new ConnectionFactory
            {
                Uri = new Uri(rabbitUrl),
                ClientProvidedName = $"{Assembly.GetCallingAssembly().GetName().Name} Publisher"
            };

            var conn = factory.CreateConnection();
            var model = conn.CreateModel();

            return services.AddScoped(serviceProvider => new BasicPublisher(
                model,
                serviceProvider.GetRequiredService<BusOptions>(),
                serviceProvider.GetRequiredService<RequestContext>()))
            .AddScoped<IPublish, Publisher>(serviceProvider => new Publisher(
                serviceProvider.GetRequiredService<BasicPublisher>(),
                busOptions.ProcessExchange))
            .AddScoped<IBroadcast, Broadcaster>(serviceProvider => new Broadcaster(
                serviceProvider.GetRequiredService<BasicPublisher>(),
                busOptions.NodeExchange, busOptions.NodeRoutingKey));

        }

        /// <summary>
        /// Registers a mock publisher for testing purposes.
        /// The mock publisher does not send messages to RabbitMQ.
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
        /// <returns>The <see cref="IServiceCollection"/> for chaining.</returns>
        public static IServiceCollection AddBusPublishMock(this IServiceCollection services)
        {
            services.AddScoped<IPublish, MockPublisher>();
            return services;
        }

        /// <summary>
        /// Registers consumers and listeners for processing messages from the message bus.
        /// Scans the specified assemblies for types implementing <see cref="IConsume"/>, <see cref="IConsumeExtended"/>, 
        /// <see cref="IConsume{T}"/>, and <see cref="IListen{T}"/> interfaces and registers them with scoped lifetime.
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
        /// <param name="assemblies">The assemblies to scan for consumers and listeners. If not provided, scans the calling assembly.</param>
        /// <returns>The <see cref="IServiceCollection"/> for chaining.</returns>
        public static IServiceCollection AddBusConsume(this IServiceCollection services, params Assembly[] assemblies)
        {
            if (assemblies.Length == 0) assemblies = new[] { Assembly.GetCallingAssembly() };

            return services.Scan(scan => scan
                .FromAssemblies(assemblies)
                .AddClasses(classes => classes.AssignableTo<IConsume>())
                .As<IConsume>().AsSelf().WithScopedLifetime())
            .Scan(scan => scan
                .FromAssemblies(assemblies)
                .AddClasses(classes => classes.AssignableTo<IConsumeExtended>())
                .As<IConsumeExtended>().AsSelf().WithScopedLifetime())
            .Scan(scan => scan
                .FromAssemblies(assemblies)
                .AddClasses(classes => classes.AssignableTo(typeof(IConsume<>)))
                .AsImplementedInterfaces().AsSelf().WithScopedLifetime())
            .RegisterListeners(assemblies)
            .AddConsumerService();
        }

        /// <summary>
        /// Registers listeners only for processing broadcast messages from the message bus.
        /// Scans the specified assemblies for types implementing <see cref="IListen{T}"/> interface and registers them with scoped lifetime.
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
        /// <param name="assemblies">The assemblies to scan for listeners. If not provided, scans the calling assembly.</param>
        /// <returns>The <see cref="IServiceCollection"/> for chaining.</returns>
        public static IServiceCollection AddBusListen(this IServiceCollection services, params Assembly[] assemblies)
        {
            if (assemblies.Length == 0) assemblies = new[] { Assembly.GetCallingAssembly() };
            
            return services.Scan(scan => scan
                .FromAssemblies(assemblies)
                .AddClasses(classes => classes.AssignableTo(typeof(IListen<>)))
                .AsImplementedInterfaces().AsSelf().WithScopedLifetime())
            .AddConsumerService();
        }


        private static IServiceCollection AddConsumerService(this IServiceCollection services)
        {
            var clientProvidedName = $"{Assembly.GetCallingAssembly().GetName().Name} Consumer";
            services.AddSingleton(sp =>
            {
                var rabbitUrl = sp.GetRequiredService<IConfiguration>().GetConnectionString("RabbitMQ");


                var factory = new ConnectionFactory
                {
                    //AutomaticRecoveryEnabled = false,
                    Uri = new Uri(rabbitUrl),
                    DispatchConsumersAsync = true,
                    RequestedHeartbeat = sp.GetRequiredService<BusOptions>().RequestedHeartbeat,
                    ClientProvidedName = clientProvidedName
                };

                return factory;
            });

            services.AddHostedService<ConsumersService>();
            services.AddSingleton<ConsumerRunner>();
            return services;
        }

        private static IServiceCollection RegisterListeners(this IServiceCollection services, Assembly[] assemblies) =>
            services.Scan(scan => scan
                .FromAssemblies(assemblies)
                .AddClasses(classes => classes.AssignableTo(typeof(IListen<>)))
                .AsImplementedInterfaces().AsSelf().WithScopedLifetime());
    }
}