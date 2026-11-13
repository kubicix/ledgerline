using Microsoft.Extensions.Options;

namespace Ledgerline.BankSimulator.Features;

public sealed class FaultMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, FaultState state, IOptions<BankOptions> options)
    {
        var operation = context.GetEndpoint()?.Metadata.GetMetadata<BankOperationMetadata>()?.Operation;
        if (operation is null || options.Value.ApiKey.Length == 0 || context.Request.Headers["X-Api-Key"] != options.Value.ApiKey)
        {
            await next(context);
            return;
        }
        var fault = state.TryTake(operation);
        if (fault is null) { await next(context); return; }
        try { await ApplyAsync(context, fault); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    }

    private async Task ApplyAsync(HttpContext context, FaultDefinition fault)
    {
        var delay = TimeSpan.FromMilliseconds(fault.DelayMs ?? 30_000);
        switch (fault.Mode)
        {
            case "Error500":
                await Results.Problem(statusCode: 500, title: "Injected bank failure").ExecuteAsync(context);
                break;
            case "Timeout":
                await Task.Delay(delay, context.RequestAborted);
                await Results.Problem(statusCode: 504, title: "Injected bank timeout").ExecuteAsync(context);
                break;
            case "SuccessThenTimeout":
                await HoldResponseAsync(context, delay);
                break;
            case "Delay":
                await Task.Delay(delay, context.RequestAborted);
                await next(context);
                break;
        }
    }

    private async Task HoldResponseAsync(HttpContext context, TimeSpan delay)
    {
        var original = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context);
            await Task.Delay(delay, context.RequestAborted);
            buffer.Position = 0;
            await buffer.CopyToAsync(original, context.RequestAborted);
        }
        finally { context.Response.Body = original; }
    }
}
