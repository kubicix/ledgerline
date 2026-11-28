using Ledgerline.BuildingBlocks.Web;
using Ledgerline.BuildingBlocks.Web.Telemetry;

var builder = WebApplication.CreateBuilder(args);
builder.AddLedgerlineTelemetry("reconciliation");
builder.AddLedgerlineWebDefaults("reconciliation");

var app = builder.Build();
app.UseLedgerlineWebDefaults();
app.Run();

namespace Ledgerline.Reconciliation
{
    /// <summary>Anchor type for WebApplicationFactory and architecture tests.</summary>
    public partial class Program;
}
