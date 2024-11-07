using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RabbitMQ.Client;
using SW.HttpExtensions;
using SW.PrimitiveTypes;

namespace SW.Bus;

internal class BasicPublisher
{
    private readonly IModel model;
    private readonly BusOptions busOptions;
    private readonly RequestContext requestContext;

    public BasicPublisher(IModel model, BusOptions busOptions, RequestContext requestContext)
    {
        this.model = model;
        this.busOptions = busOptions;
        this.requestContext = requestContext;
    }

    async public Task Publish<TMessage>(TMessage message, string exchange)
    {
        var body = JsonConvert.SerializeObject(message,
            new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore });
        await Publish(message.GetType().Name, body,exchange);
    }

    public async Task Publish(string messageTypeName, string message,string exchange)
    {
        try
        {
            var body = Encoding.UTF8.GetBytes(message);
            await Publish(messageTypeName, body,exchange);
        }
        catch (Exception e)
        {
            throw new Exception($"Publish message to exchange {exchange} failed {messageTypeName} : {message} ", e);
        }
        
    }

    public Task Publish(string messageTypeName, byte[] message,string exchange)
    {
        IBasicProperties props = null;
        props = model.CreateBasicProperties();
        props.Headers = new Dictionary<string, object>();
        if (requestContext.IsValid && busOptions.Token.IsValid)
        {
            var jwt = busOptions.Token.WriteJwt((ClaimsIdentity)requestContext.User.Identity);
            props.Headers.Add(RequestContext.UserHeaderName, jwt);
        }

        if (requestContext.CorrelationId != null)
            props.Headers.Add(RequestContext.CorrelationIdHeaderName, requestContext.CorrelationId);

        props.Headers.Add(BusOptions.SourceNodeIdHeaderName,busOptions.NodeId);

        model.BasicPublish(exchange, messageTypeName.ToLower(), props, message);

        return Task.CompletedTask;
    }
}