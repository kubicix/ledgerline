using Ledgerline.BuildingBlocks.Web;
using Ledgerline.BuildingBlocks.Web.Telemetry;
using Ledgerline.Reconciliation.Infrastructure;
using Ledgerline.Reconciliation.Staging;
using Microsoft.EntityFrameworkCore;

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
// Readiness is the service's own database; the broker check arrives with messaging (handoff C4). Kafka is not a
// readiness dependency: the staging loop runs on its own thread and a lagging stream only flags a run.
builder.Services.AddHealthChecks().AddCheck<ReadinessCheck>("database", timeout: TimeSpan.FromSeconds(3));
builder.Services.AddDbContext<ReconciliationDbContext>((services, options) =>
    options.UseNpgsql(services.GetRequiredService<IConfiguration>().GetConnectionString("Database")));
// Settings are read when first resolved, so configuration added after this point (tests) still applies.
builder.Services.AddSingleton(services => StagingOptions.From(services.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<StagingProgress>();
builder.Services.AddSingleton<IStagingConsumer, PaymentStagingConsumer>();
builder.Services.AddSingleton<IStagingConsumer, LedgerStagingConsumer>();
builder.Services.AddHostedService<StagingConsumerLoop>();

var app = builder.Build();
app.UseLedgerlineWebDefaults();
app.Run();

namespace Ledgerline.Reconciliation
{
    /// <summary>Anchor type for WebApplicationFactory and architecture tests.</summary>
    public partial class Program;
}
