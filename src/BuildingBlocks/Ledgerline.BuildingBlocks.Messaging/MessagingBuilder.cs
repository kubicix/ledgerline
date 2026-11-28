using Ledgerline.BuildingBlocks.Messaging.Consuming;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ledgerline.BuildingBlocks.Messaging;

public sealed class MessagingBuilder
{
    private readonly IServiceCollection services;
    private readonly HandlerRegistry handlers;
    internal string ServiceName { get; private set; } = "";

    /// <summary>For transport packages (Ledgerline.BuildingBlocks.Messaging.Kafka) that register routes and publishers.</summary>
    internal IServiceCollection Services => services;

    /// <summary>The registry of this service's message types; transport packages register their record types in it.</summary>
    internal MessageTypeRegistry Types { get; }

    /// <summary>The configuration passed to <c>AddLedgerlineMessaging</c>, read by transport packages for their section.</summary>
    internal IConfiguration Configuration { get; }

    internal MessagingBuilder(IServiceCollection services, HandlerRegistry handlers, MessageTypeRegistry types,
        IConfiguration configuration)
    {
        this.services = services;
        this.handlers = handlers;
        Types = types;
        Configuration = configuration;
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
