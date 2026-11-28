using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ledgerline.BuildingBlocks.Messaging.Kafka;

/// <summary>One Kafka row per message, with a new message ID: the row is written once, in the enqueue transaction.</summary>
internal sealed class KafkaRoute<TMessage> : IOutboxRoute
{
    public Type MessageType => typeof(TMessage);

    public IEnumerable<OutboxRecord> Records(object message, Guid sourceMessageId) =>
        [new OutboxRecord(KafkaOutboxPublisher.TransportName, Ids.New(), typeof(TMessage), message)];
}

/// <summary>
/// One Kafka row per record of the expanded message. Each record's message ID is derived from the source row's
/// message ID and the record's position, so running the same expansion for the same source gives the same IDs.
/// </summary>
internal sealed class KafkaExpandedRoute<TMessage, TRecord>(Func<TMessage, IEnumerable<TRecord>> expand) : IOutboxRoute
{
    public Type MessageType => typeof(TMessage);

    public IEnumerable<OutboxRecord> Records(object message, Guid sourceMessageId) =>
        expand((TMessage)message).Select((record, index) => new OutboxRecord(KafkaOutboxPublisher.TransportName,
            NameBasedId.Create(sourceMessageId, index.ToString(CultureInfo.InvariantCulture)), typeof(TRecord),
            record ?? throw new InvalidOperationException("An expanded Kafka record cannot be null.")));
}

/// <summary>Name-based UUID version 5 (RFC 9562): SHA-1 over the namespace ID and the name, in network byte order.</summary>
internal static class NameBasedId
{
    public static Guid Create(Guid namespaceId, string name)
    {
        var input = new byte[16 + Encoding.UTF8.GetByteCount(name)];
        namespaceId.TryWriteBytes(input, bigEndian: true, out _);
        Encoding.UTF8.GetBytes(name, input.AsSpan(16));
        // SHA-1 is what version 5 is defined with; the hash only derives an identifier and protects nothing.
#pragma warning disable CA5350
        var hash = SHA1.HashData(input);
#pragma warning restore CA5350
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}
