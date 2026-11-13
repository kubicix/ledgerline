using Ledgerline.BuildingBlocks.Messaging.Consuming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ledgerline.BuildingBlocks.Messaging;

public sealed class MessagingBuilder
{
    private readonly IServiceCollection services;
    private readonly HandlerRegistry handlers;
    internal string ServiceName { get; private set; } = "";

    internal MessagingBuilder(IServiceCollection services, HandlerRegistry handlers)
    {
        this.services = services;
        this.handlers = handlers;
    }

    public MessagingBuilder Service(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ServiceName = name;
        return this;
    }

    public MessagingBuilder Consume<TMessage, THandler>() where THandler : class, IMessageHandler<TMessage>
    {
        handlers.Add<TMessage, THandler>();
        services.TryAddScoped<THandler>();
        return this;
    }
}
