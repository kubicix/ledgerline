// Walking skeleton: no endpoints and no business logic yet. See docs/plan.md.
var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.Run();

namespace Ledgerline.Payments
{
    /// <summary>Anchor type for WebApplicationFactory and architecture tests.</summary>
    public partial class Program;
}
