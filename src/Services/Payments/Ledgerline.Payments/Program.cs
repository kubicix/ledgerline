using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.BuildingBlocks.Web;
using Ledgerline.BuildingBlocks.Web.Auth;
using Ledgerline.BuildingBlocks.Web.Idempotency;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Payments.Features.AccountReplica;
using Ledgerline.Payments.Features.LedgerReplies;
using Ledgerline.Payments.Features.Payments;
using Ledgerline.Payments.Features.Topups;
using Ledgerline.Payments.Features.Transfers;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Payments.Sagas;
using Microsoft.EntityFrameworkCore;

var command = args.FirstOrDefault();
var builder = WebApplication.CreateBuilder(command == "migrate" ? args[1..] : args);
if (command == "migrate")
{
    await MigrateCommand.RunAsync(builder.Configuration.GetConnectionString("Database")
        ?? throw new InvalidOperationException("ConnectionStrings:Database is required."));
    return;
}
builder.AddLedgerlineWebDefaults("payments");
builder.Services.AddDbContext<PaymentsDbContext>((services, options) =>
    options.UseNpgsql(services.GetRequiredService<IConfiguration>().GetConnectionString("Database")));
builder.Services.AddScoped<SagaRunner>();
builder.Services.AddScoped<IPostCommitHook>(services => services.GetRequiredService<SagaRunner>());
builder.Services.AddLedgerlineMessaging<PaymentsDbContext>(builder.Configuration, messaging => messaging.Service("payments")
    .Consume<AccountOpened, AccountOpenedHandler>()
    .Consume<LedgerOperationSucceeded, LedgerOperationSucceededHandler>()
    .Consume<LedgerOperationRejected, LedgerOperationRejectedHandler>());

var app = builder.Build();
app.UseLedgerlineWebDefaults();
app.MapPost("/topups", CreateTopUpEndpoint.HandleAsync).RequireAuthorization(Policies.Customer).RequireIdempotencyKey();
app.MapPost("/transfers", CreateTransferEndpoint.HandleAsync).RequireAuthorization(Policies.Customer).RequireIdempotencyKey();
app.MapGet("/payments/{id:guid}", GetPaymentEndpoint.HandleAsync).RequireAuthorization();

app.Run();

namespace Ledgerline.Payments
{
    /// <summary>Anchor type for WebApplicationFactory and architecture tests.</summary>
    public partial class Program;
}
