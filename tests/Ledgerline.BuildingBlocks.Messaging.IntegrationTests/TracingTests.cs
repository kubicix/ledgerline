using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using Ledgerline.BuildingBlocks.Messaging.Diagnostics;
using Ledgerline.BuildingBlocks.Messaging.IntegrationTests.Support;
using Ledgerline.BuildingBlocks.Messaging.Outbox;
using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Ledgerline.BuildingBlocks.Web.Telemetry;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using OpenTelemetry.Trace;

namespace Ledgerline.BuildingBlocks.Messaging.IntegrationTests;

public sealed class TracingTests(InfrastructureFixture infrastructure)
{
    private const string Exchange = "ledgerline.tracing";
    private static readonly ActivitySource EnqueueScope = new("Ledgerline.Tests.Tracing");

    [Fact]
    public async Task Consumer_span_is_child_of_the_span_that_enqueued_the_message()
    {
        var spans = new SpanCollection();
        var cs = await CreateDatabaseAsync();
        using var host = await StartHostAsync(cs, cs, "trace" + Ids.New().ToString("N"), m => m.Consume<Ping, PingHandler>(), spans);
        var enqueue = await EnqueueAsync(host, new Ping("child", 0));

        var consumer = await WaitForSpanAsync(spans, enqueue.TraceId, s => s.Kind == ActivityKind.Consumer);
        var producer = await WaitForSpanAsync(spans, enqueue.TraceId, s => s.Kind == ActivityKind.Producer);

        producer.Source.Name.ShouldBe(MessagingActivitySource.Name);
        producer.ParentSpanId.ShouldBe(enqueue.SpanId);
        consumer.Source.Name.ShouldBe(MessagingActivitySource.Name);
        consumer.TraceId.ShouldBe(enqueue.TraceId);
        consumer.ParentSpanId.ShouldBe(producer.SpanId);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Producer_and_consumer_spans_carry_messaging_attributes()
    {
        var spans = new SpanCollection();
        var cs = await CreateDatabaseAsync();
        var service = "trace" + Ids.New().ToString("N");
        using var host = await StartHostAsync(cs, cs, service, m => m.Consume<Ping, PingHandler>(), spans);
        var enqueue = await EnqueueAsync(host, new Ping("attributes", 0));

        var consumer = await WaitForSpanAsync(spans, enqueue.TraceId, s => s.Kind == ActivityKind.Consumer);
        var producer = await WaitForSpanAsync(spans, enqueue.TraceId, s => s.Kind == ActivityKind.Producer);
        var messageId = (await SingleOutboxRowAsync(cs)).MessageId.ToString();

        producer.GetTagItem("messaging.system").ShouldBe("rabbitmq");
        producer.GetTagItem("messaging.destination.name").ShouldBe(Exchange + ":tracing.command.ping.v1");
        producer.GetTagItem("messaging.message.id").ShouldBe(messageId);
        consumer.GetTagItem("messaging.system").ShouldBe("rabbitmq");
        consumer.GetTagItem("messaging.destination.name").ShouldBe(Exchange + ":tracing.command.ping.v1:" + Topology.InboxQueue(service));
        consumer.GetTagItem("messaging.message.id").ShouldBe(messageId);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Retry_delivery_stays_in_the_same_trace()
    {
        var spans = new SpanCollection();
        var cs = await CreateDatabaseAsync();
        using var host = await StartHostAsync(cs, cs, "trace" + Ids.New().ToString("N"), m => m.Consume<Ping, PingHandler>(), spans);
        var enqueue = await EnqueueAsync(host, new Ping("retried", 2));

        await Eventually.ShouldAsync(() => Task.FromResult(
            spans.Of(enqueue.TraceId).Count(s => s.Kind == ActivityKind.Consumer) == 3), because: "Expected three delivery attempts");
        var producer = await WaitForSpanAsync(spans, enqueue.TraceId, s => s.Kind == ActivityKind.Producer);
        var trace = spans.Of(enqueue.TraceId);
        trace.Count(s => s.Kind == ActivityKind.Producer).ShouldBe(1);
        var deliveries = trace.Where(s => s.Kind == ActivityKind.Consumer).OrderBy(s => s.StartTimeUtc).ToArray();

        producer.ParentSpanId.ShouldBe(enqueue.SpanId);
        deliveries.ShouldAllBe(s => s.ParentSpanId == producer.SpanId);
        deliveries.Select(s => s.GetTagItem("messaging.message.id")).Distinct().ShouldHaveSingleItem()
            .ShouldBe(producer.GetTagItem("messaging.message.id"));
        deliveries.Select(s => s.Status).ShouldBe([ActivityStatusCode.Error, ActivityStatusCode.Error, ActivityStatusCode.Unset]);
        await using var db = OpenDb(cs);
        (await db.Widgets.SingleAsync(TestContext.Current.CancellationToken)).Name.ShouldBe("retried");
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Trace_spans_two_services_in_round_trip()
    {
        var spans = new SpanCollection();
        var requesterDb = await CreateDatabaseAsync();
        var responderDb = await CreateDatabaseAsync();
        var requester = "requester" + Ids.New().ToString("N");
        var responder = "responder" + Ids.New().ToString("N");
        // Both hosts wire AddLedgerlineTelemetry; one in-memory exporter sees both, because activity listeners are process-wide.
        using var requesterHost = await StartHostAsync(requesterDb, requesterDb, requester, m => m.Consume<Reply, ReplyHandler>(), spans);
        using var responderHost = await StartHostAsync(responderDb, requesterDb, responder, m => m.Consume<Request, RequestHandler>());
        var enqueue = await EnqueueAsync(requesterHost, new Request("round-trip"));

        var replyConsumer = await WaitForSpanAsync(spans, enqueue.TraceId, s => s.Kind == ActivityKind.Consumer
            && Destination(s) == Exchange + ":tracing.event.reply.v1:" + Topology.InboxQueue(requester));
        var requestProducer = await WaitForSpanAsync(spans, enqueue.TraceId,
            s => s.Kind == ActivityKind.Producer && Destination(s) == Exchange + ":tracing.command.request.v1");
        var requestConsumer = await WaitForSpanAsync(spans, enqueue.TraceId, s => s.Kind == ActivityKind.Consumer
            && Destination(s) == Exchange + ":tracing.command.request.v1:" + Topology.InboxQueue(responder));
        var replyProducer = await WaitForSpanAsync(spans, enqueue.TraceId,
            s => s.Kind == ActivityKind.Producer && Destination(s) == Exchange + ":tracing.event.reply.v1");

        requestProducer.ParentSpanId.ShouldBe(enqueue.SpanId);
        requestConsumer.ParentSpanId.ShouldBe(requestProducer.SpanId);
        replyProducer.ParentSpanId.ShouldBe(requestConsumer.SpanId);
        replyConsumer.ParentSpanId.ShouldBe(replyProducer.SpanId);
        await using var db = OpenDb(requesterDb);
        (await db.Widgets.SingleAsync(TestContext.Current.CancellationToken)).Name.ShouldBe("round-trip");
        await responderHost.StopAsync(TestContext.Current.CancellationToken);
        await requesterHost.StopAsync(TestContext.Current.CancellationToken);
    }

    private async Task<IHost> StartHostAsync(string cs, string vhostDb, string service, Action<MessagingBuilder> configure,
        SpanCollection? spans = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Messaging:RabbitMq:Uri"] = await infrastructure.CreateRabbitMqUriAsync(new NpgsqlConnectionStringBuilder(vhostDb).Database!),
            ["Messaging:Relay:PollIntervalMs"] = "20",
            ["Messaging:Consumer:RetryTtlMs"] = "200"
        });
        builder.AddLedgerlineTelemetry(service);
        if (spans is not null)
            builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddSource(EnqueueScope.Name).AddInMemoryExporter(spans));
        builder.Services.AddDbContext<TestDbContext>(options => options.UseNpgsql(cs));
        builder.Services.AddLedgerlineMessaging<TestDbContext>(builder.Configuration, m => { m.Service(service); configure(m); });
        var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    // The scope wraps only Enqueue and SaveChanges, after the host started, so background loops never inherit it.
    private static async Task<ActivityContext> EnqueueAsync<T>(IHost host, T payload)
    {
        await using var scope = host.Services.CreateAsyncScope();
        using var activity = EnqueueScope.StartActivity("enqueue");
        activity.ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IOutbox>().Enqueue(payload, Ids.New());
        await scope.ServiceProvider.GetRequiredService<TestDbContext>().SaveChangesAsync(TestContext.Current.CancellationToken);
        return activity.Context;
    }

    private static async Task<Activity> WaitForSpanAsync(SpanCollection spans, ActivityTraceId traceId, Func<Activity, bool> predicate)
    {
        Activity? found = null;
        await Eventually.ShouldAsync(() => Task.FromResult((found = spans.Of(traceId).FirstOrDefault(predicate)) is not null),
            because: "Expected a span in trace " + traceId);
        return found!;
    }

    private static string? Destination(Activity span) => span.GetTagItem("messaging.destination.name") as string;

    private async Task<string> CreateDatabaseAsync()
    {
        var cs = await infrastructure.CreateDatabaseAsync("tracing");
        await using var db = OpenDb(cs);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return cs;
    }

    private static TestDbContext OpenDb(string cs) => new(new DbContextOptionsBuilder<TestDbContext>().UseNpgsql(cs).Options);

    private static async Task<OutboxMessage> SingleOutboxRowAsync(string cs)
    {
        await using var db = OpenDb(cs);
        return await db.Set<OutboxMessage>().SingleAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Thread-safe sink for the in-memory exporter; spans from other test classes also arrive, so reads filter by trace.</summary>
    private sealed class SpanCollection : ICollection<Activity>
    {
        private readonly ConcurrentQueue<Activity> spans = new();
        public int Count => spans.Count;
        public bool IsReadOnly => false;
        public Activity[] Of(ActivityTraceId traceId) => spans.Where(s => s.TraceId == traceId).ToArray();
        public void Add(Activity item) => spans.Enqueue(item);
        public void Clear() => spans.Clear();
        public bool Contains(Activity item) => spans.Contains(item);
        public void CopyTo(Activity[] array, int arrayIndex) => spans.CopyTo(array, arrayIndex);
        public bool Remove(Activity item) => throw new NotSupportedException();
        public IEnumerator<Activity> GetEnumerator() => spans.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public sealed class PingHandler(TestDbContext db) : IMessageHandler<Ping>
    {
        public Task HandleAsync(Ping message, MessageContext context, CancellationToken ct)
        {
            if (context.Attempt <= message.Failures) throw new InvalidOperationException("injected failure");
            db.Widgets.Add(new Widget(context.MessageId, message.Name));
            return Task.CompletedTask;
        }
    }

    public sealed class RequestHandler(IOutbox outbox) : IMessageHandler<Request>
    {
        public Task HandleAsync(Request message, MessageContext context, CancellationToken ct)
        {
            outbox.Enqueue(new Reply(message.Name), context.CorrelationId, context.MessageId);
            return Task.CompletedTask;
        }
    }

    public sealed class ReplyHandler(TestDbContext db) : IMessageHandler<Reply>
    {
        public Task HandleAsync(Reply message, MessageContext context, CancellationToken ct)
        {
            db.Widgets.Add(new Widget(context.MessageId, message.Name));
            return Task.CompletedTask;
        }
    }

    [MessageType("tracing.command.ping.v1")]
    public sealed record Ping(string Name, int Failures);
    [MessageType("tracing.command.request.v1")]
    public sealed record Request(string Name);
    [MessageType("tracing.event.reply.v1")]
    public sealed record Reply(string Name);
}
