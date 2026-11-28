using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.BuildingBlocks.Web;
using Ledgerline.BuildingBlocks.Web.Telemetry;
using Ledgerline.Reconciliation.Infrastructure;
using Ledgerline.Reconciliation.Runs;
using Ledgerline.Reconciliation.Staging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var migrate = args.FirstOrDefault() == "migrate";
var builder = WebApplication.CreateBuilder(migrate ? args[1..] : args);
if (migrate)
{
    await MigrateCommand.RunAsync(builder.Configuration.GetConnectionString("Database")
        ?? throw new InvalidOperationException("ConnectionStrings:Database is required."));
    return;
}
builder.AddLedgerlineTelemetry("reconciliation");
builder.AddLedgerlineWebDefaults("reconciliation");
// Readiness is the service's own database plus the broker check that messaging registers (handoff C4). Kafka, Redis and
// the bank are not readiness dependencies: a lagging stream only flags a run, a bank outage fails it and it is retried.
builder.Services.AddHealthChecks().AddCheck<ReadinessCheck>("database", timeout: TimeSpan.FromSeconds(3));
builder.Services.AddDbContext<ReconciliationDbContext>((services, options) =>
    options.UseNpgsql(services.GetRequiredService<IConfiguration>().GetConnectionString("Database")));
// Publish only, through the outbox: the service consumes nothing from RabbitMQ.
builder.Services.AddLedgerlineMessaging<ReconciliationDbContext>(builder.Configuration, messaging => messaging.Service("reconciliation"));
// Settings are read when first resolved, so configuration added after this point (tests) still applies.
builder.Services.AddSingleton(services => StagingOptions.From(services.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<StagingProgress>();
builder.Services.AddSingleton<IStagingConsumer, PaymentStagingConsumer>();
builder.Services.AddSingleton<IStagingConsumer, LedgerStagingConsumer>();
builder.Services.AddHostedService<StagingConsumerLoop>();
builder.Services.AddSingleton(services => RunOptions.From(services.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(services => LockOptions.From(services.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(services => RetryOptions.From(services.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<IRunLock, RunLock>();
builder.Services.AddSingleton<KafkaInputWait>();
builder.Services.AddScoped<ReconciliationRunner>();
builder.Services.Configure<BankClientOptions>(builder.Configuration.GetSection("Bank"));
builder.Services.AddHttpClient<BankSettlementClient>((services, client) =>
{
    var options = services.GetRequiredService<IOptions<BankClientOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl);
    client.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
});
// Read before Build, so a test host sets it through host settings.
builder.Services.AddReconciliationScheduler(builder.Configuration.GetValue<bool?>("Reconciliation:Scheduler:Enabled") ?? true);

var app = builder.Build();
app.UseLedgerlineWebDefaults();
app.Run();

namespace Ledgerline.Reconciliation
{
    /// <summary>Anchor type for WebApplicationFactory and architecture tests.</summary>
    public partial class Program;
}
