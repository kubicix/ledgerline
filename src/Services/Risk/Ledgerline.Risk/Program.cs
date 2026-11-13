using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.BuildingBlocks.Web;
using Ledgerline.Contracts.Risk;
using Ledgerline.Risk.Features.Evaluate;
using Ledgerline.Risk.Features.Rules;
using Ledgerline.Risk.Infrastructure;
using Microsoft.EntityFrameworkCore;

var migrate = args.FirstOrDefault() == "migrate";
var builder = WebApplication.CreateBuilder(migrate ? args[1..] : args);
if (migrate)
{
    await MigrateCommand.RunAsync(builder.Configuration.GetConnectionString("Database")
        ?? throw new InvalidOperationException("ConnectionStrings:Database is required."));
    return;
}
builder.AddLedgerlineWebDefaults("risk");
builder.Services.AddHealthChecks().AddCheck<ReadinessCheck>("dependencies", timeout: TimeSpan.FromSeconds(3));
builder.Services.AddDbContext<RiskDbContext>((services, options) =>
    options.UseNpgsql(services.GetRequiredService<IConfiguration>().GetConnectionString("Database")));
builder.Services.AddSingleton<RuleCache>();
builder.Services.AddScoped<DatabaseVelocityCounter>();
builder.Services.AddScoped<RedisVelocityCounter>();
builder.Services.AddScoped<IVelocityCounter, FallbackVelocityCounter>();
builder.Services.AddLedgerlineMessaging<RiskDbContext>(builder.Configuration,
    messaging => messaging.Service("risk").Consume<EvaluateRisk, EvaluateRiskHandler>());

var app = builder.Build();
app.UseLedgerlineWebDefaults();
app.MapRiskEndpoints();
app.Run();

namespace Ledgerline.Risk
{
    /// <summary>Anchor type for WebApplicationFactory and architecture tests.</summary>
    public partial class Program;
}
