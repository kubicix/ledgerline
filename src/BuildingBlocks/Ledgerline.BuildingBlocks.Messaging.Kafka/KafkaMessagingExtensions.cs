using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ledgerline.BuildingBlocks.Messaging.Kafka;

/// <summary>
/// Routes selected events to Kafka next to RabbitMQ, through the outbox. The first call registers the Kafka publisher,
/// and with it the relay's Kafka loop; a service that never calls these loads no Kafka code and writes no Kafka rows.
/// </summary>
public static class KafkaMessagingExtensions
{
    /// <summary>Also relays every <typeparamref name="TMessage"/> to <paramref name="topic"/>, keyed by <paramref name="key"/>.</summary>
    public static MessagingBuilder AlsoPublishToKafka<TMessage>(this MessagingBuilder m, string topic, Func<TMessage, string> key)
    {
        ArgumentNullException.ThrowIfNull(m);
        var map = UseKafka(m);
        map.AddSource(typeof(TMessage));
        map.Add(m.Types.NameOf<TMessage>(), topic, key);
        m.Services.AddSingleton<IOutboxRoute>(new KafkaRoute<TMessage>());
        return m;
    }

    /// <summary>
    /// Also relays every <typeparamref name="TMessage"/> to <paramref name="topic"/> as one record per item of
    /// <paramref name="expand"/>, each keyed by <paramref name="key"/>. Each record is its own outbox row and message.
    /// </summary>
    public static MessagingBuilder AlsoPublishToKafkaExpanded<TMessage, TRecord>(this MessagingBuilder m, string topic,
        Func<TMessage, IEnumerable<TRecord>> expand, Func<TRecord, string> key)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(expand);
        var map = UseKafka(m);
        map.AddSource(typeof(TMessage));
        map.Add(m.Types.NameOf<TRecord>(), topic, key);
        m.Services.AddSingleton<IOutboxRoute>(new KafkaExpandedRoute<TMessage, TRecord>(expand));
        return m;
    }

    private static KafkaTopicMap UseKafka(MessagingBuilder m)
    {
        if (m.Services.FirstOrDefault(x => x.ServiceType == typeof(KafkaTopicMap))?.ImplementationInstance is KafkaTopicMap existing)
            return existing;
        var map = new KafkaTopicMap();
        var configuration = m.Configuration;
        m.Services.AddSingleton(map);
        m.Services.TryAddSingleton(_ => KafkaOptions.From(configuration));
        m.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IOutboxPublisher, KafkaOutboxPublisher>());
        return map;
    }
}
