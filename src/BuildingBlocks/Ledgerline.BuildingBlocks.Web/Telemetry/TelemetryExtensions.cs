using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Ledgerline.BuildingBlocks.Web.Telemetry;

public static class TelemetryExtensions
{
    /// <summary>
    /// Name of the activity source owned by Ledgerline.BuildingBlocks.Messaging (MessagingActivitySource.Name).
    /// Repeated here as a string because Gateway and BankSimulator use this project and must not reference Messaging.
    /// </summary>
    public const string MessagingSourceName = "Ledgerline.Messaging";

    /// <summary>
    /// Wires traces (ASP.NET Core, HttpClient, Npgsql, Ledgerline.Messaging), metrics (runtime, ASP.NET Core,
    /// the Ledgerline meters) and logs. Export goes to OTLP only when OTEL_EXPORTER_OTLP_ENDPOINT is configured.
    /// </summary>
    public static IHostApplicationBuilder AddLedgerlineTelemetry(this IHostApplicationBuilder builder, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddNpgsql()
                .AddSource(MessagingSourceName))
            .WithMetrics(metrics => metrics
                .AddMeter("System.Runtime")
                .AddAspNetCoreInstrumentation()
                .AddMeter("Ledgerline", "Ledgerline.*"))
            .WithLogging(configureBuilder: null, configureOptions: logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = true;
            });
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])) telemetry.UseOtlpExporter();
        return builder;
    }
}
