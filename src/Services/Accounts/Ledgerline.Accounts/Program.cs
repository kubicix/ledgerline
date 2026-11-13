using Ledgerline.Accounts.Infrastructure;
using Ledgerline.Accounts.Features.LedgerCommands;
using Ledgerline.Accounts.Features.Accounts;
using Ledgerline.Accounts.Ledger;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.BuildingBlocks.Web;
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
builder.AddLedgerlineWebDefaults("accounts");
builder.Services.AddHealthChecks().AddCheck<ReadinessCheck>("dependencies", timeout: TimeSpan.FromSeconds(3));
builder.Services.AddDbContext<AccountsDbContext>((services, options) =>
    options.UseNpgsql(services.GetRequiredService<IConfiguration>().GetConnectionString("Database")));
builder.Services.AddScoped<EventStore>();
builder.Services.AddScoped<ILedger, Ledgerline.Accounts.Ledger.Ledger>();
builder.Services.AddScoped<Holds>();
builder.Services.AddScoped<LedgerOperationRunner>();
builder.Services.AddScoped<IPostCommitHook>(services => services.GetRequiredService<LedgerOperationRunner>());
builder.Services.AddLedgerlineMessaging<AccountsDbContext>(builder.Configuration, messaging => messaging.Service("accounts")
    .Consume<CreditTopUp, CreditTopUpHandler>().Consume<TransferFunds, TransferFundsHandler>()
    .Consume<ReserveFunds, ReserveFundsHandler>().Consume<CaptureFunds, CaptureFundsHandler>().Consume<ReleaseFunds, ReleaseFundsHandler>());

var app = builder.Build();
app.UseLedgerlineWebDefaults();
app.MapAccountsEndpoints();

app.Run();

namespace Ledgerline.Accounts
{
    /// <summary>Anchor type for WebApplicationFactory and architecture tests.</summary>
    public partial class Program;
}
