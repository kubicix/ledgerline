using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Ledgerline.BuildingBlocks.Messaging.Consuming;

public sealed class HandlerRegistry(MessageTypeRegistry types)
{
    private readonly Dictionary<string, Func<IServiceProvider, JsonElement, MessageContext, CancellationToken, Task>> handlers = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> TypeNames => handlers.Keys;

    public HandlerRegistry Add<TMessage, THandler>() where THandler : class, IMessageHandler<TMessage>
    {
        handlers.Add(types.NameOf<TMessage>(), (services, payload, context, ct) =>
            services.GetRequiredService<THandler>().HandleAsync((TMessage)EnvelopeSerializer.FromPayload(payload, typeof(TMessage)), context, ct));
        return this;
    }

    internal bool Contains(string typeName) => handlers.ContainsKey(typeName);
    internal Task InvokeAsync(MessageEnvelope envelope, IServiceProvider services, MessageContext context, CancellationToken ct) =>
        handlers[envelope.Type](services, envelope.Payload, context, ct);
}
