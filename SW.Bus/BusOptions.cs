using SW.HttpExtensions;
using System;
using System.Collections.Generic;
using SW.Bus.RabbitMqExtensions;

namespace SW.Bus
{
    public class BusOptions
    {
        private const string NodeMessageName = "__broadcast";
        internal const string SourceNodeIdHeaderName = "source-node-id";
        const string versionPrefix = "v3.";
        private readonly string environment;
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
        public int HeartBeatTimeOut { get; set; }

        internal TimeSpan RequestedHeartbeat =>
            HeartBeatTimeOut > 0 ? TimeSpan.FromSeconds(HeartBeatTimeOut) : TimeSpan.Zero; 
        public JwtTokenParameters Token { get; set; }
        public string ApplicationName { get; set; }
        public ushort DefaultQueuePrefetch { get; set; }
        public ushort DefaultRetryCount { get; set; }
        public uint DefaultRetryAfter { get; set; }
        public int DefaultMaxPriority { get; set; }
        public string NodeId { get; set; }
        public int ListenRetryCount { get; set; }
        public ushort ListenRetryAfter { get; set; }
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