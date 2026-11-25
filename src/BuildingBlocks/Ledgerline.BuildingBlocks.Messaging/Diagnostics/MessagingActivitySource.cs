using System.Diagnostics;
using Ledgerline.BuildingBlocks.Messaging.Outbox;
using Microsoft.Extensions.Hosting;

namespace Ledgerline.BuildingBlocks.Messaging.Diagnostics;

/// <summary>
/// The single activity source for outbox publishing and inbox consumption (ADR 0009).
/// Attribute names follow the OpenTelemetry messaging conventions, which are still in Development status:
/// <c>messaging.system</c> is the name the ADR uses, <c>messaging.system.name</c> is its v1.45.0 replacement.
/// </summary>
public static class MessagingActivitySource
{
    public const string Name = "Ledgerline.Messaging";

    internal static readonly ActivitySource Source = new(Name);

    /// <summary>Starts the PRODUCER span for one outbox row, as a child of the context captured at enqueue.</summary>
    internal static Activity? StartPublish(OutboxMessage message) =>
        Start("publish", "send", MessageTypeRegistry.ExchangeFor(message.Type) + ":" + message.Type, ActivityKind.Producer,
            message.TraceParent, message.MessageId, message.CorrelationId);

    /// <summary>Starts the CONSUMER span for one delivery, as a child of the context the publisher sent.</summary>
    internal static Activity? StartProcess(MessageEnvelope envelope, string inboxQueue) =>
        Start("process", "process", MessageTypeRegistry.ExchangeFor(envelope.Type) + ":" + envelope.Type + ":" + inboxQueue,
            ActivityKind.Consumer, envelope.TraceParent, envelope.MessageId, envelope.CorrelationId);

    /// <summary>The context to send with a message: the current PRODUCER span, or the enqueue context when none is recorded.</summary>
    internal static string? OutgoingTraceParent(string? enqueueTraceParent) =>
        Activity.Current is { Kind: ActivityKind.Producer } current && current.Source == Source ? current.Id : enqueueTraceParent;

    /// <summary>Marks the span as failed. Returns false so it can run in an exception filter without catching.</summary>
    internal static bool RecordError(Activity? activity, Exception error)
    {
        // Cancellation and host shutdown are not failures of the message; their spans keep an unset status.
        if (error is OperationCanceledException or HostAbortedException) return false;
        // The exception message is left out on purpose: it may carry personal data.
        activity?.SetStatus(ActivityStatusCode.Error).SetTag("error.type", error.GetType().FullName);
        return false;
    }

    private static Activity? Start(string operation, string operationType, string destination, ActivityKind kind,
        string? traceParent, Guid messageId, Guid correlationId)
    {
        if (!Source.HasListeners()) return null;
        // A null or unparsable traceparent leaves parent default: the span then parents on Activity.Current or starts a new root.
        ActivityContext.TryParse(traceParent, null, isRemote: true, out var parent);
        return Source.StartActivity(operation + " " + destination, kind, parent,
        [
            new("messaging.system", "rabbitmq"),
            new("messaging.system.name", "rabbitmq"),
            new("messaging.destination.name", destination),
            new("messaging.operation.name", operation),
            new("messaging.operation.type", operationType),
            new("messaging.message.id", messageId.ToString()),
            new("messaging.message.conversation_id", correlationId.ToString())
        ]);
    }
}
