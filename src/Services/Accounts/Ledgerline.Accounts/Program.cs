using Ledgerline.Accounts.Infrastructure;
using Ledgerline.Accounts.Features.LedgerCommands;
using Ledgerline.Accounts.Features.Accounts;
using Ledgerline.Accounts.Features.Ledger;
using Ledgerline.Accounts.Ledger;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.BuildingBlocks.Messaging.Kafka;
using Ledgerline.BuildingBlocks.Web;
using Ledgerline.BuildingBlocks.Web.Telemetry;
using Ledgerline.Contracts.Accounts;
using Microsoft.EntityFrameworkCore;

var command = args.FirstOrDefault();
var builder = WebApplication.CreateBuilder(command is "migrate" or "rebuild-projections" ? args[1..] : args);
if (args.FirstOrDefault() == "migrate")
{
    var connectionString = builder.Configuration.GetConnectionString("Database")
        ?? throw new InvalidOperationException("ConnectionStrings:Database is required.");
    var currencies = builder.Configuration.GetSection("Ledger:Currencies").Get<string[]>() ?? ["TRY", "EUR", "USD"];
    await MigrateCommand.RunAsync(connectionString, currencies);
    return;
}
if (command == "rebuild-projections")
{
    await RebuildProjectionsCommand.RunAsync(builder.Configuration.GetConnectionString("Database")
        ?? throw new InvalidOperationException("ConnectionStrings:Database is required."));
    return;
}
builder.AddLedgerlineTelemetry("accounts");
builder.AddLedgerlineWebDefaults("accounts");
builder.Services.AddHealthChecks().AddCheck<ReadinessCheck>("database", timeout: TimeSpan.FromSeconds(3));
builder.Services.AddDbContext<AccountsDbContext>((services, options) =>
    options.UseNpgsql(services.GetRequiredService<IConfiguration>().GetConnectionString("Database")));
builder.Services.AddScoped<EventStore>();
builder.Services.AddScoped<ILedger, Ledgerline.Accounts.Ledger.Ledger>();
builder.Services.AddScoped<Holds>();
builder.Services.AddSingleton(new LedgerMetricsOptions());
builder.Services.AddSingleton<LedgerMetrics>();
builder.Services.AddHostedService(services => services.GetRequiredService<LedgerMetrics>());
builder.Services.AddScoped<LedgerOperationRunner>();
builder.Services.AddScoped<IPostCommitHook>(services => services.GetRequiredService<LedgerOperationRunner>());
builder.Services.AddLedgerlineMessaging<AccountsDbContext>(builder.Configuration, messaging =>
{
    messaging.Service("accounts")
        .Consume<CreditTopUp, CreditTopUpHandler>().Consume<TransferFunds, TransferFundsHandler>()
        .Consume<ReserveFunds, ReserveFundsHandler>().Consume<CaptureFunds, CaptureFundsHandler>().Consume<ReleaseFunds, ReleaseFundsHandler>()
        .Consume<RefundFunds, RefundFundsHandler>();
    // Off by default: without it no Kafka row is written and no Kafka client is created (reporting.md section 2).
    if (builder.Configuration.GetValue<bool>("Messaging:Kafka:Enabled"))
        messaging
            .AlsoPublishToKafkaExpanded<JournalEntryPosted, JournalEntryLinePosted>("ledgerline.ledger.entries.v1",
                JournalEntryLinePosted.From, line => line.AccountId.ToString())
            .AlsoPublishToKafka<Ledgerline.Contracts.Accounts.AccountOpened>("ledgerline.accounts.events.v1", opened => opened.AccountId.ToString());
});

var app = builder.Build();
app.UseLedgerlineWebDefaults();
app.MapAccountsEndpoints();
app.MapTrialBalanceEndpoint();

app.Run();

namespace Ledgerline.Accounts
{
    /// <summary>Anchor type for WebApplicationFactory and architecture tests.</summary>
    public partial class Program;
}
