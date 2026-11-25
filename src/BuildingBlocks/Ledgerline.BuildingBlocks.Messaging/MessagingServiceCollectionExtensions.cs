using System.Globalization;
using Ledgerline.BuildingBlocks.Messaging.Consuming;
using Ledgerline.BuildingBlocks.Messaging.Outbox;
using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Ledgerline.BuildingBlocks.Messaging;

public static class MessagingServiceCollectionExtensions
{
    public static IServiceCollection AddLedgerlineMessaging<TDbContext>(this IServiceCollection services,
        IConfiguration configuration, Action<MessagingBuilder> configure) where TDbContext : DbContext
        => AddMessaging<TDbContext>(services, configuration, configure, true);

    public static IServiceCollection AddLedgerlineInbox<TDbContext>(this IServiceCollection services,
        IConfiguration configuration, Action<MessagingBuilder> configure) where TDbContext : DbContext
        => AddMessaging<TDbContext>(services, configuration, configure, false);

    private static IServiceCollection AddMessaging<TDbContext>(IServiceCollection services,
        IConfiguration configuration, Action<MessagingBuilder> configure, bool useOutbox) where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configure);
        var types = new MessageTypeRegistry();
        var handlers = new HandlerRegistry(types);
        var builder = new MessagingBuilder(services, handlers);
        configure(builder);
        if (string.IsNullOrWhiteSpace(builder.ServiceName)) throw new InvalidOperationException("Messaging service name is required.");
        services.AddSingleton(types);
        services.AddSingleton(handlers);
        RegisterOptions(services, configuration, builder.ServiceName);
        RegisterRuntime<TDbContext>(services, useOutbox);
        return services;
    }

    private static void RegisterRuntime<TDbContext>(IServiceCollection services, bool useOutbox) where TDbContext : DbContext
    {
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<ICrashPoint, NoCrashPoint>();
        services.TryAddSingleton<IAckStrategy, DefaultAckStrategy>();
        services.TryAddSingleton<RabbitMqConnection>();
        if (useOutbox)
        {
            services.TryAddSingleton<OutboxSignal>();
            services.TryAddSingleton<IOutboxPublisher, RabbitMqOutboxPublisher>();
            services.TryAddScoped<IOutbox, EfOutbox<TDbContext>>();
            services.TryAddScoped<OutboxSaveChangesInterceptor>();
            services.AddDbContext<TDbContext>((sp, options) => options.AddInterceptors(sp.GetRequiredService<OutboxSaveChangesInterceptor>()));
            services.AddHostedService<OutboxRelay<TDbContext>>();
        }
        services.AddHostedService<MessagingTopologyInitializer>();
        services.AddHostedService<ConsumerHost<TDbContext>>();
        services.AddHostedService<MessagingRetentionJob<TDbContext>>();
    }

    private static void RegisterOptions(IServiceCollection services, IConfiguration configuration, string service)
    {
        services.AddSingleton(_ => new RabbitMqOptions
        {
            Uri = configuration["Messaging:RabbitMq:Uri"] ?? throw new InvalidOperationException("Messaging:RabbitMq:Uri is required.")
        });
        services.AddSingleton(_ => new RelayOptions
        {
            PollIntervalMs = ReadInt(configuration, "Relay:PollIntervalMs", 250),
            BatchSize = ReadInt(configuration, "Relay:BatchSize", 100),
            Service = service
        });
        services.AddSingleton(_ => new ConsumerOptions
        {
            Service = service,
            Prefetch = checked((ushort)ReadInt(configuration, "Consumer:Prefetch", 16)),
            MaxAttempts = ReadInt(configuration, "Consumer:MaxAttempts", 5),
            RetryTtlMs = ReadInt(configuration, "Consumer:RetryTtlMs", 5000)
        });
        services.AddSingleton(_ => new RetentionOptions { Days = ReadInt(configuration, "Retention:Days", 7) });
    }

    private static int ReadInt(IConfiguration configuration, string key, int defaultValue) =>
        configuration["Messaging:" + key] is { } value ? int.Parse(value, CultureInfo.InvariantCulture) : defaultValue;
}

internal sealed class MessagingTopologyInitializer(RabbitMqConnection connection, HandlerRegistry handlers, ConsumerOptions options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var channel = await connection.CreateChannelAsync(false, cancellationToken);
        await Topology.DeclareAsync(channel, options.Service, handlers.TypeNames, options.RetryTtlMs, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
