using Ledgerline.BuildingBlocks.Web;
using Ledgerline.BuildingBlocks.Web.Telemetry;
using Ledgerline.Reporting.Consuming;
using Ledgerline.Reporting.Infrastructure;
using Ledgerline.Reporting.Projections;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);
builder.AddLedgerlineTelemetry("reporting");
builder.AddLedgerlineWebDefaults("reporting");
// MongoDB only: no PostgreSQL, and no RabbitMQ messaging (it would add the broker check). Reporting reads the
// envelope and serializer types from Messaging and consumes Kafka with Confluent.Kafka directly.
builder.Services.AddHealthChecks().AddCheck<ReadinessCheck>("database", timeout: TimeSpan.FromSeconds(3));
// Settings are read when first resolved, so configuration added after this point (tests) still applies.
builder.Services.AddSingleton<IMongoClient>(services => new MongoClient(
    services.GetRequiredService<IConfiguration>().GetConnectionString("Mongo")
    ?? throw new InvalidOperationException("ConnectionStrings:Mongo is required.")));
builder.Services.AddSingleton(services => services.GetRequiredService<IMongoClient>().GetDatabase(
    services.GetRequiredService<IConfiguration>()["Reporting:Mongo:Database"] ?? "ledgerline_reporting"));
builder.Services.AddSingleton<MongoCollections>();
builder.Services.AddSingleton(services => ConsumerOptions.From(services.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<DeadLetterProducer>();
builder.Services.AddSingleton<IProjection, AccountStatementProjection>();
builder.Services.AddHostedService<KafkaConsumerLoop>();

var app = builder.Build();
app.UseLedgerlineWebDefaults();
app.Run();

namespace Ledgerline.Reporting
{
    /// <summary>Anchor type for WebApplicationFactory and architecture tests.</summary>
    public partial class Program;
}
