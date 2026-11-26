using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.BuildingBlocks.Web;
using Ledgerline.BuildingBlocks.Web.Telemetry;
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
using Ledgerline.Payments.Bank;
using Ledgerline.Payments.Features.Bank;
using Ledgerline.Contracts.Payments;
using Ledgerline.Contracts.Risk;
using Ledgerline.Payments.Features.RiskReplies;
using Ledgerline.Payments.Features.Refunds;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Polly;

var command = args.FirstOrDefault();
var builder = WebApplication.CreateBuilder(command == "migrate" ? args[1..] : args);
if (command == "migrate")
{
    await MigrateCommand.RunAsync(builder.Configuration.GetConnectionString("Database")
        ?? throw new InvalidOperationException("ConnectionStrings:Database is required."));
    return;
}
builder.AddLedgerlineTelemetry("payments");
builder.AddLedgerlineWebDefaults("payments");
builder.Services.AddHealthChecks().AddCheck<ReadinessCheck>("database", timeout: TimeSpan.FromSeconds(3));
builder.Services.AddDbContext<PaymentsDbContext>((services, options) =>
    options.UseNpgsql(services.GetRequiredService<IConfiguration>().GetConnectionString("Database")));
builder.Services.AddSingleton<SagaMetrics>();
builder.Services.AddScoped<SagaRunner>();
builder.Services.AddScoped<RefundRunner>();
builder.Services.AddScoped<RefundRecovery>();
builder.Services.AddSingleton(provider =>
{
    var configuration = provider.GetRequiredService<IConfiguration>();
    return new PaymentSagaOptions
    {
        RiskEnabled = configuration.GetValue<bool?>("Saga:RiskEnabled") ?? true,
        BankEnabled = configuration.GetValue<bool?>("Saga:BankEnabled") ?? true,
        BankCompensationMaxAttempts = configuration.GetValue<int?>("Saga:BankCompensationMaxAttempts") ?? 8,
        AuthorizationTtl = configuration.GetValue<TimeSpan?>("Saga:AuthorizationTtl") ?? TimeSpan.FromDays(7)
    };
});
builder.Services.Configure<BankOptions>(builder.Configuration.GetSection("Bank"));
builder.Services.AddHttpClient<IBankClient, BankClient>((provider, client) =>
{
    var options = provider.GetRequiredService<IOptions<BankOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl);
    client.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
    client.Timeout = Timeout.InfiniteTimeSpan;
}).AddResilienceHandler("bank-timeout", pipeline => pipeline.AddTimeout(TimeSpan.FromSeconds(5)));
builder.Services.AddSingleton(services => services.GetRequiredService<IConfiguration>().GetSection("Saga:Timeouts").Get<TimeoutOptions>() ?? new());
builder.Services.AddHostedService<SagaTimeoutPoller>();
builder.Services.AddScoped<IPostCommitHook>(services => services.GetRequiredService<SagaRunner>());
builder.Services.AddLedgerlineMessaging<PaymentsDbContext>(builder.Configuration, messaging => messaging.Service("payments")
    .Consume<AccountOpened, AccountOpenedHandler>()
    .Consume<AccountStatusChanged, AccountStatusChangedHandler>()
    .Consume<RiskApproved, RiskApprovedHandler>().Consume<RiskRejected, RiskRejectedHandler>()
    .Consume<CallBank, CallBankHandler>()
    .Consume<LedgerOperationSucceeded, LedgerOperationSucceededHandler>()
    .Consume<LedgerOperationRejected, LedgerOperationRejectedHandler>());

var app = builder.Build();
app.UseLedgerlineWebDefaults();
app.MapPost("/topups", CreateTopUpEndpoint.HandleAsync).RequireAuthorization(Policies.Customer).RequireIdempotencyKey();
app.MapPost("/transfers", CreateTransferEndpoint.HandleAsync).RequireAuthorization(Policies.Customer).RequireIdempotencyKey();
app.MapGet("/payments/{id:guid}", GetPaymentEndpoint.HandleAsync).RequireAuthorization();
app.MapPost("/payments", CreatePaymentEndpoint.HandleAsync).RequireAuthorization(Policies.Customer).RequireIdempotencyKey();
app.MapPost("/payments/{id:guid}/capture", CapturePaymentEndpoint.HandleAsync).RequireAuthorization(Policies.Merchant).RequireIdempotencyKey();
app.MapPost("/payments/{id:guid}/void", VoidPaymentEndpoint.HandleAsync).RequireAuthorization(policy => policy.RequireRole("merchant", "admin")).RequireIdempotencyKey();
app.MapGet("/payments", ListPaymentsEndpoint.HandleAsync).RequireAuthorization();
app.MapPost("/payments/{id:guid}/refunds", CreateRefundEndpoint.HandleAsync).RequireAuthorization(Policies.Merchant).RequireIdempotencyKey();
app.MapGet("/payments/{id:guid}/refunds", ListRefundsEndpoint.HandleAsync).RequireAuthorization();

app.Run();

namespace Ledgerline.Payments
{
    /// <summary>Anchor type for WebApplicationFactory and architecture tests.</summary>
    public partial class Program;
}
