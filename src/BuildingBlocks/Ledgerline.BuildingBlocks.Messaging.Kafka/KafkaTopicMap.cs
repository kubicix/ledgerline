using System.Diagnostics.CodeAnalysis;

namespace Ledgerline.BuildingBlocks.Messaging.Kafka;

/// <summary>
/// The topic and key of every record type this service relays to Kafka, by message type name. Built at
/// configuration time, so the publisher can map a pending row even before anything was enqueued in this process.
/// </summary>
public sealed class KafkaTopicMap
{
    private readonly Dictionary<string, KafkaTopic> topics = new(StringComparer.Ordinal);
    private readonly HashSet<Type> sources = [];

    public bool TryGet(string typeName, [MaybeNullWhen(false)] out KafkaTopic topic) => topics.TryGetValue(typeName, out topic);

    internal void Add<TRecord>(string typeName, string topic, Func<TRecord, string> key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(key);
        if (!topics.TryAdd(typeName, new KafkaTopic(topic, typeof(TRecord), record => key((TRecord)record))))
            throw new InvalidOperationException($"A Kafka topic is already configured for {typeName}.");
    }

    /// <summary>One Kafka route per enqueued type: a second one would write its rows twice.</summary>
    internal void AddSource(Type messageType)
    {
        if (!sources.Add(messageType))
            throw new InvalidOperationException($"{messageType.Name} is already published to Kafka.");
    }
}

/// <summary>Where a record type goes on Kafka: its topic, its CLR type, and the function that gives its key.</summary>
public sealed record KafkaTopic(string Topic, Type RecordType, Func<object, string> Key);
