using Microsoft.Extensions.Options;

namespace Ledgerline.BankSimulator.Features;

public sealed class BankOptions
{
    public string ApiKey { get; set; } = "";
}

public sealed class ApiKeyFilter(IOptions<BankOptions> options) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var expected = options.Value.ApiKey;
        return expected.Length > 0 && context.HttpContext.Request.Headers["X-Api-Key"] == expected
            ? next(context)
            : ValueTask.FromResult<object?>(Results.Problem(statusCode: 401,
                type: "urn:ledgerline:problem:unauthorized", title: "API key required"));
    }
}
