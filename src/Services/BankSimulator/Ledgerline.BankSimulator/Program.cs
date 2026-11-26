using Ledgerline.BankSimulator.Features;
using Ledgerline.BankSimulator.Infrastructure;
using Ledgerline.BuildingBlocks.Web;
using Ledgerline.BuildingBlocks.Web.Telemetry;
using Microsoft.EntityFrameworkCore;

var migrate = args.FirstOrDefault() == "migrate";
var builder = WebApplication.CreateBuilder(migrate ? args[1..] : args);
if (migrate)
{
    await MigrateCommand.RunAsync(builder.Configuration.GetConnectionString("Database")
        ?? throw new InvalidOperationException("ConnectionStrings:Database is required."));
    return;
}
builder.AddLedgerlineTelemetry("banksim");
builder.AddLedgerlineWebDefaults("banksim");
builder.Services.AddHealthChecks().AddCheck<ReadinessCheck>("database", timeout: TimeSpan.FromSeconds(3));
builder.Services.Configure<BankOptions>(builder.Configuration.GetSection("Bank"));
builder.Services.AddSingleton<FaultState>();
builder.Services.AddSingleton<SettlementTampering>();
builder.Services.AddDbContext<BankDbContext>((services, options) =>
    options.UseNpgsql(services.GetRequiredService<IConfiguration>().GetConnectionString("Database")));

var app = builder.Build();
app.UseLedgerlineWebDefaults();
app.UseMiddleware<FaultMiddleware>();
app.MapBankEndpoints();
app.Run();

namespace Ledgerline.BankSimulator
{
    /// <summary>Anchor type for WebApplicationFactory and architecture tests.</summary>
    public partial class Program;
}
