using Ledgerline.Accounts.Infrastructure;
using Ledgerline.BuildingBlocks.Web;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
if (args.FirstOrDefault() == "migrate")
{
    var connectionString = builder.Configuration.GetConnectionString("Database")
        ?? throw new InvalidOperationException("ConnectionStrings:Database is required.");
    var currencies = builder.Configuration.GetSection("Ledger:Currencies").Get<string[]>() ?? ["TRY", "EUR", "USD"];
    await MigrateCommand.RunAsync(connectionString, currencies);
    return;
}
builder.AddLedgerlineWebDefaults("accounts");
builder.Services.AddDbContext<AccountsDbContext>((services, options) =>
    options.UseNpgsql(services.GetRequiredService<IConfiguration>().GetConnectionString("Database")));

var app = builder.Build();
app.UseLedgerlineWebDefaults();

app.Run();

namespace Ledgerline.Accounts
{
    /// <summary>Anchor type for WebApplicationFactory and architecture tests.</summary>
    public partial class Program;
}
