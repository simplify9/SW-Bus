using SW.HttpExtensions;
using System;
using System.Collections.Generic;
using SW.Bus.RabbitMqExtensions;

namespace SW.Bus
{
    /// <summary>
    /// Provides configuration options for the message bus including queue settings, retry policies, and RabbitMQ connection parameters.
    /// </summary>
    public class BusOptions
    {
        private const string NodeMessageName = "__broadcast";
        internal const string SourceNodeIdHeaderName = "source-node-id";
        const string versionPrefix = "v3.";
        private readonly string environment;
        
        /// <summary>
        /// Initializes a new instance of the <see cref="BusOptions"/> class.
        /// Sets default values for queue prefetch, retry policies, and creates unique node identifiers.
        /// </summary>
        /// <param name="environment">The deployment environment name (e.g., "dev", "staging", "production"). Used to create isolated exchanges.</param>
        public BusOptions(string environment)
        {
            Options = new Dictionary<string, QueueOptions>(StringComparer.OrdinalIgnoreCase);
            DefaultQueuePrefetch = 4;
            DefaultRetryCount = 5;
            DefaultRetryAfter = 60;
            ListenRetryCount = 5;
            ListenRetryAfter = 60;
            HeartBeatTimeOut = 0;
            Token = new JwtTokenParameters();
            this.environment = environment;
            ProcessExchange = $"{versionPrefix}{environment}".ToLower();
            DeadLetterExchange = $"{versionPrefix}{environment}.deadletter".ToLower();
            NodeId = Guid.NewGuid().ToString("N");
        }
        
        private int monitoringCacheSeconds = 5;
        
        /// <summary>
        /// Gets or sets the cache duration in seconds for RabbitMQ management API monitoring data.
        /// Used by <see cref="ConsumerReader"/> to cache queue statistics and reduce load on RabbitMQ.
        /// Valid range: 3-60 seconds. Values outside this range are automatically clamped for safety.
        /// Default: 5 seconds.
        /// </summary>
        /// <remarks>
        /// Lower values (3-5 seconds) provide fresher data but increase API calls to RabbitMQ.
        /// Higher values (30-60 seconds) reduce API load but may show stale statistics.
        /// </remarks>
        /// <example>
        /// <code>
        /// services.AddBus(options => 
        /// {
        ///     options.MonitoringCacheSeconds = 10; // Cache for 10 seconds
        /// });
        /// </code>
        /// </example>
        public int MonitoringCacheSeconds 
        { 
            get => monitoringCacheSeconds;
            // Clamp the value between 3 and 60 seconds for safety
            set => monitoringCacheSeconds = Math.Clamp(value, 3, 60); 
        }
        
        /// <summary>
        /// Gets or sets the heartbeat timeout in seconds for RabbitMQ connections.
        /// Used to detect network or broker failures. Set to 0 to disable heartbeats.
        /// Default: 0 (disabled).
        /// </summary>
        /// <remarks>
        /// When set to a positive value, the client and server will exchange heartbeat frames
        /// to ensure the connection is alive. Recommended value is 60 seconds for production.
        /// </remarks>
        public int HeartBeatTimeOut { get; set; }

        /// <summary>
        /// Gets or sets the base URL for the RabbitMQ Management API.
        /// If not set, defaults to http://{hostname}:15672 based on the connection string.
        /// </summary>
        /// <example>http://localhost:15672</example>
        public string ManagementUrl { get; set; }
        
        /// <summary>
        /// Gets or sets the username for RabbitMQ Management API authentication.
        /// If not set, defaults to the username from the connection string.
        /// </summary>
        public string ManagementUsername { get; set; }
        
        /// <summary>
        /// Gets or sets the password for RabbitMQ Management API authentication.
        /// If not set, defaults to the password from the connection string.
        /// </summary>
        public string ManagementPassword { get; set; }
        
        /// <summary>
        /// Gets or sets the RabbitMQ virtual host name.
        /// If not set, defaults to the virtual host from the connection string.
        /// </summary>
        public string VirtualHost { get; set; }

        internal TimeSpan RequestedHeartbeat =>
            HeartBeatTimeOut > 0 ? TimeSpan.FromSeconds(HeartBeatTimeOut) : TimeSpan.Zero;
            
        /// <summary>
        /// Gets or sets the JWT token parameters for authentication when publishing or consuming messages.
        /// Used to include authentication information in message headers for cross-service authorization.
        /// </summary>
        public JwtTokenParameters Token { get; set; }
        
        /// <summary>
        /// Gets or sets the application name used in queue and exchange naming.
        /// When set, queues are named: {Exchange}.{ApplicationName}.{ConsumerName}.{MessageName}
        /// When not set, queues are named: {Exchange}.{ConsumerName}.{MessageName}
        /// This allows multiple applications to have isolated queues in the same environment.
        /// </summary>
        public string ApplicationName { get; set; }
        
        /// <summary>
        /// Gets or sets the default prefetch count (QoS) for all queues.
        /// Determines how many unacknowledged messages a consumer can have at once.
        /// Default: 4.
        /// </summary>
        /// <remarks>
        /// Higher values increase throughput but use more memory.
        /// Lower values reduce memory usage but may decrease throughput.
        /// Can be overridden per queue using <see cref="AddQueueOption"/>.
        /// </remarks>
        public ushort DefaultQueuePrefetch { get; set; }
        
        /// <summary>
        /// Gets or sets the default number of retry attempts for failed messages.
        /// After this many failures, messages are moved to the bad/dead letter queue.
        /// Default: 5.
        /// </summary>
        /// <remarks>
        /// Can be overridden per queue using <see cref="AddQueueOption"/>.
        /// Set to 0 to disable retries (messages go directly to bad queue on failure).
        /// </remarks>
        public ushort DefaultRetryCount { get; set; }
        
        /// <summary>
        /// Gets or sets the default delay in seconds before retrying a failed message.
        /// Default: 60 seconds.
        /// </summary>
        /// <remarks>
        /// Can be overridden per queue using <see cref="AddQueueOption"/>.
        /// Uses RabbitMQ's TTL (time-to-live) mechanism for delayed redelivery.
        /// </remarks>
        public uint DefaultRetryAfter { get; set; }
        
        /// <summary>
        /// Gets or sets the default maximum priority level for queues.
        /// When set to a value greater than 0, enables priority queues where messages can have priorities 0 to this value.
        /// Default: 0 (priority disabled).
        /// </summary>
        /// <remarks>
        /// Priority queues have performance overhead. Use only when needed.
        /// Higher priority messages are delivered before lower priority ones.
        /// </remarks>
        public int DefaultMaxPriority { get; set; }
        
        /// <summary>
        /// Gets or sets the unique identifier for this application node/instance.
        /// Used for broadcast message routing to ensure each instance gets its own copy.
        /// Auto-generated as a new GUID on startup.
        /// </summary>
        public string NodeId { get; set; }
        
        /// <summary>
        /// Gets or sets the number of retry attempts for broadcast listener messages.
        /// Default: 5.
        /// </summary>
        public int ListenRetryCount { get; set; }
        
        /// <summary>
        /// Gets or sets the delay in seconds before retrying a failed broadcast listener message.
        /// Default: 60 seconds.
        /// </summary>
        public ushort ListenRetryAfter { get; set; }
        
        /// <summary>
        /// Gets the dictionary of per-queue configuration options.
        /// Use <see cref="AddQueueOption"/> to add queue-specific settings.
        /// </summary>
        public IDictionary<string, QueueOptions> Options { get; }
        public string NodeQueueName => $"{NodeExchange}:{NodeId}".ToLower();
        public string NodeRoutingKey => $"{NodeExchange}{NodeMessageName}";
        public string NodeRetryQueueName => $"{NodeQueueName}_retry".ToLower();
        public string NodeRetryRoutingKey => $"{NodeRoutingKey}_{NodeId}_retry".ToLower();
        public string NodeBadQueueName => $"{NodeExchange}_bad".ToLower();
        public string NodeBadRoutingKey => NodeBadQueueName;
        public IDictionary<string, object> NodeRetryArgs => ListenRetryCount == 0 ? null : new Dictionary<string, object>
        {
            { "x-dead-letter-exchange", NodeExchange},
            { "x-dead-letter-routing-key", NodeRetryRoutingKey},
            { "x-message-ttl", ListenRetryAfter == 0 ? 100 : ListenRetryAfter * 1000 }
        };

        public IDictionary<string, object> NodeProcessArgs => new Dictionary<string, object>
        {
            { "x-dead-letter-exchange", NodeDeadLetterExchange },
            { "x-dead-letter-routing-key", NodeRetryRoutingKey },
            
        };
        
        public string ProcessExchange { get; }
        public string DeadLetterExchange { get; }

        public string NodeExchange =>
            $"{versionPrefix}{environment}{(string.IsNullOrWhiteSpace(ApplicationName) ? "" : $".{ApplicationName}")}.node".ToLower();
        public string NodeDeadLetterExchange =>
            $"{versionPrefix}{environment}{(string.IsNullOrWhiteSpace(ApplicationName) ? "" : $".{ApplicationName}")}.node.deadletter".ToLower();

        /// <summary>
        /// Adds or updates queue-specific configuration options, overriding the default settings.
        /// </summary>
        /// <param name="queueName">The name of the queue to configure. Case-insensitive.</param>
        /// <param name="prefetch">The prefetch count (QoS) for this queue. If null, uses <see cref="DefaultQueuePrefetch"/>.</param>
        /// <param name="retryCount">The number of retry attempts for this queue. If null, uses <see cref="DefaultRetryCount"/>.</param>
        /// <param name="retryAfterSeconds">The delay in seconds before retrying. If null, uses <see cref="DefaultRetryAfter"/>.</param>
        /// <param name="priority">The maximum priority level for this queue. If null, uses <see cref="DefaultMaxPriority"/>.</param>
        /// <example>
        /// <code>
        /// services.AddBus(options => 
        /// {
        ///     // High-priority queue with more prefetch for throughput
        ///     options.AddQueueOption("OrderConsumer.OrderCreated", prefetch: 10, retryCount: 3);
        ///     
        ///     // Critical queue with priority support
        ///     options.AddQueueOption("PaymentConsumer.PaymentProcessed", priority: 10);
        /// });
        /// </code>
        /// </example>
        public void AddQueueOption(string queueName, ushort? prefetch = null, int? retryCount = null, uint? retryAfterSeconds = null,int? priority = null)
        {
            Options[queueName.ToLower()] = new QueueOptions
            {
                Prefetch = prefetch,
                RetryCount = retryCount,
                RetryAfterSeconds = retryAfterSeconds,
                Priority = priority
            };
        }

    }
}