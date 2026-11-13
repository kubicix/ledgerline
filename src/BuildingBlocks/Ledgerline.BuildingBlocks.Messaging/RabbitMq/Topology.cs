using RabbitMQ.Client;

namespace Ledgerline.BuildingBlocks.Messaging.RabbitMq;

public static class Topology
{
    public static string InboxQueue(string service) => service + ".inbox";
    public static string RetryQueue(string service) => InboxQueue(service) + ".retry";
    public static string DeadQueue(string service) => InboxQueue(service) + ".dead";

    public static async Task DeclareAsync(IChannel channel, string service, IReadOnlyCollection<string> consumedTypeNames, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(consumedTypeNames);
        var exchanges = consumedTypeNames.Select(MessageTypeRegistry.ExchangeFor).Append("ledgerline." + service).Distinct(StringComparer.Ordinal);
        foreach (var exchange in exchanges)
            await channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, durable: true, cancellationToken: ct);
        await channel.QueueDeclareAsync(InboxQueue(service), true, false, false, QuorumArguments(), cancellationToken: ct);
        var retry = QuorumArguments();
        retry["x-message-ttl"] = 5000;
        retry["x-dead-letter-exchange"] = "";
        retry["x-dead-letter-routing-key"] = InboxQueue(service);
        await channel.QueueDeclareAsync(RetryQueue(service), true, false, false, retry, cancellationToken: ct);
        await channel.QueueDeclareAsync(DeadQueue(service), true, false, false, QuorumArguments(), cancellationToken: ct);
        foreach (var type in consumedTypeNames)
            await channel.QueueBindAsync(InboxQueue(service), MessageTypeRegistry.ExchangeFor(type), type, cancellationToken: ct);
    }

    private static Dictionary<string, object?> QuorumArguments() => new() { ["x-queue-type"] = "quorum" };
}
