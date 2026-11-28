using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Web;
using Ledgerline.BuildingBlocks.Web.Telemetry;
using Ledgerline.Reporting.Consuming;
using Ledgerline.Reporting.Features;
using Ledgerline.Reporting.Infrastructure;
using Ledgerline.Reporting.Projections;
using MongoDB.Driver;

var command = args.FirstOrDefault();
var builder = WebApplication.CreateBuilder(command == "rebuild" ? args[1..] : args);
if (command == "rebuild")
{
    await RebuildCommand.RunAsync(builder.Configuration);
    return;
}
builder.AddLedgerlineTelemetry("reporting");
builder.AddLedgerlineWebDefaults("reporting");
// MongoDB only: no PostgreSQL, and no RabbitMQ messaging (it would add the broker check). Reporting reads the
// envelope and serializer types from Messaging and consumes Kafka with Confluent.Kafka directly.
builder.Services.AddHealthChecks().AddCheck<ReadinessCheck>("database", timeout: TimeSpan.FromSeconds(3));
// Settings are read when first resolved, so configuration added after this point (tests) still applies.
builder.Services.AddSingleton<IMongoClient>(services => MongoCollections.CreateClient(services.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(services => MongoCollections.OpenDatabase(
    services.GetRequiredService<IMongoClient>(), services.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<MongoCollections>();
builder.Services.AddSingleton(services => ConsumerOptions.From(services.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<LagTracker>();
builder.Services.AddSingleton<DeadLetterProducer>();
builder.Services.AddSingleton<IProjection, AccountStatementProjection>();
builder.Services.AddSingleton<IProjection, PaymentTimelineProjection>();
builder.Services.AddSingleton<IProjection, MerchantDailyTotalsProjection>();
builder.Services.AddSingleton<IProjection, AccountOwnersProjection>();
builder.Services.AddHostedService<KafkaConsumerLoop>();

var app = builder.Build();
// First, so every response carries the header: errors, 401 and 404 included (reporting.md section 7).
app.Use((context, next) =>
{
    var lag = context.RequestServices.GetRequiredService<LagTracker>();
    context.Response.OnStarting(() =>
    {
        context.Response.Headers[LagTracker.HeaderName] = lag.HeaderValue;
        return Task.CompletedTask;
    });
    return next(context);
});
app.UseLedgerlineWebDefaults();
app.MapReportingEndpoints();
app.Run();

namespace Ledgerline.Reporting
{
    /// <summary>Anchor type for WebApplicationFactory and architecture tests.</summary>
    public partial class Program;
}
