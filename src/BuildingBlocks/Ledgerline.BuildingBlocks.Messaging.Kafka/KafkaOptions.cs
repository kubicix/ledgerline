using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Ledgerline.BuildingBlocks.Messaging.Kafka;

/// <summary>Settings of the Kafka outbox publisher, from the <c>Messaging:Kafka</c> configuration section.</summary>
public sealed class KafkaOptions
{
    public const string BootstrapServersKey = "Messaging:Kafka:BootstrapServers";
    public const string MessageTimeoutMsKey = "Messaging:Kafka:MessageTimeoutMs";

    public string BootstrapServers { get; set; } = "";

    /// <summary>
    /// Bounds one produce, librdkafka's own retries included. The librdkafka default is 300 s; 5 s matches the
    /// RabbitMQ confirm timeout, so a dead broker fails the row and it gets its backoff like a RabbitMQ row.
    /// </summary>
    public int MessageTimeoutMs { get; set; } = 5000;

    internal static KafkaOptions From(IConfiguration configuration) => new()
    {
        BootstrapServers = configuration[BootstrapServersKey] is { Length: > 0 } servers
            ? servers
            : throw new InvalidOperationException(BootstrapServersKey + " is required when events are published to Kafka."),
        MessageTimeoutMs = configuration[MessageTimeoutMsKey] is { } timeout ? int.Parse(timeout, CultureInfo.InvariantCulture) : 5000
    };
}
