using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.BuildingBlocks.Web;
using Ledgerline.BuildingBlocks.Web.Telemetry;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Contracts.Payments;
using Ledgerline.Notifications.Features.List;
using Ledgerline.Notifications.Features.PaymentEvents;
using Ledgerline.Notifications.Features.Recipients;
using Ledgerline.Notifications.Infrastructure;
using Ledgerline.Notifications.Sending;
using Microsoft.EntityFrameworkCore;

var migrate = args.FirstOrDefault() == "migrate";
var builder = WebApplication.CreateBuilder(migrate ? args[1..] : args);
if (migrate)
{
    await MigrateCommand.RunAsync(builder.Configuration.GetConnectionString("Database")
        ?? throw new InvalidOperationException("ConnectionStrings:Database is required."));
    return;
}
builder.AddLedgerlineTelemetry("notifications");
builder.AddLedgerlineWebDefaults("notifications");
builder.Services.AddHealthChecks().AddCheck<ReadinessCheck>("database", timeout: TimeSpan.FromSeconds(3));
builder.Services.AddDbContext<NotificationsDbContext>((services, options) =>
    options.UseNpgsql(services.GetRequiredService<IConfiguration>().GetConnectionString("Database")));
builder.Services.AddScoped<NotificationPlanner>();
builder.Services.AddSingleton(new SenderOptions());
builder.Services.AddSingleton(builder.Configuration.GetSection("Smtp").Get<SmtpOptions>() ?? new SmtpOptions());
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddHostedService<NotificationSender>();
builder.Services.AddLedgerlineInbox<NotificationsDbContext>(builder.Configuration, messaging => messaging.Service("notifications")
    .Consume<AccountOpened, AccountOpenedHandler>()
    .Consume<PaymentCompleted, PaymentCompletedHandler>()
    .Consume<PaymentAuthorized, PaymentAuthorizedHandler>()
    .Consume<PaymentCaptured, PaymentCapturedHandler>()
    .Consume<PaymentVoided, PaymentVoidedHandler>()
    .Consume<PaymentFailed, PaymentFailedHandler>()
    .Consume<PaymentRefunded, PaymentRefundedHandler>());
var app = builder.Build();
app.UseLedgerlineWebDefaults();
app.MapNotifications();
app.Run();

namespace Ledgerline.Notifications
{
    /// <summary>Anchor type for WebApplicationFactory and architecture tests.</summary>
    public partial class Program;
}
