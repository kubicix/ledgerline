using System.Globalization;
using Ledgerline.BuildingBlocks;
using Quartz;

namespace Ledgerline.Reconciliation.Runs;

/// <summary>Starts a run of a business date on demand (<c>POST /runs</c>), through the same Quartz job as the daily run.</summary>
public interface IRunTrigger
{
    /// <summary>Schedules a one-shot firing of <see cref="DailyReconciliationJob"/> for <paramref name="businessDate"/>, now.</summary>
    Task TriggerAsync(DateOnly businessDate, CancellationToken ct = default);
}

/// <summary>The <c>Reconciliation:Retry</c> section (reconciliation.md section 8): a Failed run is retried every 10 minutes, up to 6 times.</summary>
public sealed class RetryOptions
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(10);
    public int MaxAttempts { get; init; } = 6;

    public static RetryOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var defaults = new RetryOptions();
        return new RetryOptions
        {
            Interval = configuration.GetValue<TimeSpan?>("Reconciliation:Retry:Interval") ?? defaults.Interval,
            MaxAttempts = configuration.GetValue<int?>("Reconciliation:Retry:MaxAttempts") ?? defaults.MaxAttempts
        };
    }
}

/// <summary>
/// The Quartz job of reconciliation.md section 3. Fired by the daily cron trigger it reconciles yesterday (UTC, by the
/// injected clock); fired by a one-shot trigger it reconciles the date in the trigger's data. A run that ends Failed
/// schedules a one-shot retry of the same date <see cref="RetryOptions.Interval"/> later; a run that found the date's
/// lock held schedules one a lock TTL later (the holder may be a node that died with the lock). Both carry the attempt
/// count and share one budget of <see cref="RetryOptions.MaxAttempts"/> retries. An unexpected failure ends the run
/// Failed inside the runner; if even that cannot be written (the database away), or nothing was claimed yet, the
/// exception reaches the job, which logs it and schedules the same retry, so the date is never dropped. The exception
/// is not rethrown, so the cron trigger never counts a failed fire.
/// </summary>
/// <remarks>
/// The job requests recovery: when the scheduler instance running it dies mid-run, another node of the cluster (or this
/// one on restart) re-executes it through a recovery trigger, and the re-execution reconciles the same business date
/// (<see cref="BusinessDateOf"/>), so the crashed run's Running row is taken over by step 2. A shutdown interrupts the
/// job: the run stops where it is (Running), the lock is released, and the cancellation is rethrown so the store hands
/// the firing back for recovery (<see cref="ReconciliationScheduling"/>).
/// </remarks>
[DisallowConcurrentExecution]
public sealed partial class DailyReconciliationJob(
    IServiceScopeFactory scopes, RetryOptions retry, LockOptions lockOptions, IClock clock, ILogger<DailyReconciliationJob> logger) : IJob
{
    public const string CronExpression = "0 30 0 * * ?";
    public const string BusinessDateKey = "businessDate";
    public const string AttemptKey = "attempt";
    public const string SchedulerName = "ledgerline-reconciliation";
    public static readonly JobKey Key = new("daily-reconciliation", "reconciliation");
    public static readonly TriggerKey DailyTriggerKey = new("daily-0030-utc", "reconciliation");

    /// <summary>The trigger of retry number <paramref name="attempt"/> (1-based) of a date; each retry has its own key.</summary>
    public static TriggerKey RetryTriggerKey(DateOnly businessDate, int attempt) =>
        new($"retry-{Format(businessDate)}-{attempt.ToString(CultureInfo.InvariantCulture)}", "reconciliation-retry");

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var businessDate = BusinessDateOf(context, clock.UtcNow);
        var attempt = context.MergedJobDataMap.TryGetString(AttemptKey, out var count) && count is not null
            ? int.Parse(count, CultureInfo.InvariantCulture) : 0;
        try
        {
            var outcome = await RunAsync(businessDate, cancellationToken);
            RunEnded(logger, businessDate, outcome, attempt);
            if (RetryDelay(outcome) is { } delay) await ScheduleRetryAsync(context.Scheduler, businessDate, attempt, delay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RunInterrupted(logger, businessDate);
            // Stopping by throwing is what makes the job store hand the firing back for recovery.
            throw;
        }
        catch (Exception error)
        {
            // A Running row left behind (the failure could not be recorded) is taken over by the retry at step 2.
            RunCrashed(logger, businessDate, error);
            await ScheduleRetryAsync(context.Scheduler, businessDate, attempt, retry.Interval, cancellationToken);
        }
    }

    /// <summary>
    /// The business date a firing reconciles. A one-shot firing (manual or retry) carries it in its data, and so does its
    /// recovery, which keeps the original trigger's data. A cron firing reconciles the day before the 00:30 it was
    /// scheduled for (<see cref="IJobExecutionContext.ScheduledFireTimeUtc"/>), not the day before now, so a missed fire
    /// made up after the next midnight still reconciles its own date. A recovery reads the scheduled time of the firing
    /// it replays from the recovery trigger's data (which <see cref="IJobExecutionContext.ScheduledFireTimeUtc"/>
    /// repeats). Now is the last resort, for a context that reports no scheduled time.
    /// </summary>
    public static DateOnly BusinessDateOf(IJobExecutionContext context, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(context);
        var data = context.MergedJobDataMap;
        if (data.TryGetString(BusinessDateKey, out var date) && date is not null)
            return DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var scheduled = context.Recovering ? OriginalScheduledFireTime(data) ?? context.ScheduledFireTimeUtc : context.ScheduledFireTimeUtc;
        return DateOnly.FromDateTime((scheduled ?? now).UtcDateTime).AddDays(-1);
    }

    /// <summary>The recovery trigger's record of the replayed firing's scheduled time: Unix milliseconds or a date-time text.</summary>
    private static DateTimeOffset? OriginalScheduledFireTime(JobDataMap data)
    {
        if (!data.TryGetString(SchedulerConstants.FailedJobOriginalTriggerScheduledFireTime, out var text) || text is null) return null;
        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds))
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
    }

    /// <summary>The one-shot trigger that fires this job for a date.</summary>
    internal static ITrigger OneShot(TriggerKey? key, DateOnly businessDate, int attempt, DateTimeOffset at)
    {
        var builder = TriggerBuilder.Create().ForJob(Key)
            .UsingJobData(BusinessDateKey, Format(businessDate))
            .UsingJobData(AttemptKey, attempt.ToString(CultureInfo.InvariantCulture))
            .StartAt(at);
        if (key is not null) builder = builder.WithIdentity(key);
        return builder.Build();
    }

    private async Task<RunOutcome> RunAsync(DateOnly businessDate, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ReconciliationRunner>().RunAsync(businessDate, ct);
    }

    /// <summary>
    /// When the date is tried again: a Failed run after the retry interval (section 8); a held lock after a lock TTL, by
    /// when a holder that died has lost it (and a live holder has finished, so the retry exits AlreadyCompleted). Any
    /// firing gets the second, a manual one included: a manual start blocked by a dead holder deserves the same recovery.
    /// </summary>
    private TimeSpan? RetryDelay(RunOutcome outcome) => outcome switch
    {
        RunOutcome.Failed => retry.Interval,
        RunOutcome.LockNotAcquired => lockOptions.Ttl,
        _ => null
    };

    private async Task ScheduleRetryAsync(IScheduler scheduler, DateOnly businessDate, int attempt, TimeSpan delay, CancellationToken ct)
    {
        if (attempt >= retry.MaxAttempts)
        {
            RetriesExhausted(logger, businessDate, attempt);
            return;
        }
        var next = attempt + 1;
        var at = clock.UtcNow + delay;
        try
        {
            await scheduler.ScheduleTrigger(OneShot(RetryTriggerKey(businessDate, next), businessDate, next, at), TriggerConflict.Replace, ct);
            RetryScheduled(logger, businessDate, next, at);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A manual start re-runs the date.
            RetryNotScheduled(logger, businessDate, next, error);
        }
    }

    private static string Format(DateOnly businessDate) => businessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [LoggerMessage(1, LogLevel.Information, "The run of {BusinessDate} ended {Outcome} (retry {Attempt})")]
    private static partial void RunEnded(ILogger logger, DateOnly businessDate, RunOutcome outcome, int attempt);
    [LoggerMessage(2, LogLevel.Warning, "The run of {BusinessDate} was interrupted by shutdown; it stays Running and the firing is handed back for recovery")]
    private static partial void RunInterrupted(ILogger logger, DateOnly businessDate);
    [LoggerMessage(3, LogLevel.Error, "The run of {BusinessDate} crashed before its failure could be recorded; it is retried")]
    private static partial void RunCrashed(ILogger logger, DateOnly businessDate, Exception error);
    [LoggerMessage(4, LogLevel.Error, "The run of {BusinessDate} did not complete on retry {Attempt}, the last one; no further retry")]
    private static partial void RetriesExhausted(ILogger logger, DateOnly businessDate, int attempt);
    [LoggerMessage(5, LogLevel.Information, "Retry {Attempt} of the run of {BusinessDate} is scheduled at {At}")]
    private static partial void RetryScheduled(ILogger logger, DateOnly businessDate, int attempt, DateTimeOffset at);
    [LoggerMessage(6, LogLevel.Error, "Retry {Attempt} of the run of {BusinessDate} could not be scheduled")]
    private static partial void RetryNotScheduled(ILogger logger, DateOnly businessDate, int attempt, Exception error);
}

/// <summary>Schedules an on-demand run through Quartz, so it obeys the same clustering and concurrency rules as the daily one.</summary>
public sealed class QuartzRunTrigger(ISchedulerFactory schedulers, IClock clock) : IRunTrigger
{
    public async Task TriggerAsync(DateOnly businessDate, CancellationToken ct = default)
    {
        var scheduler = await schedulers.GetScheduler(ct);
        // A key of its own per request: a second request while the first fires is a second run, which then exits
        // AlreadyCompleted or re-runs a Failed date.
        await scheduler.ScheduleJob(DailyReconciliationJob.OneShot(null, businessDate, 0, clock.UtcNow), cancellationToken: ct);
    }
}

public static class ReconciliationScheduling
{
    /// <summary>
    /// The clustered Quartz scheduler on the service's own database (ADR 0010) with the daily job and its cron trigger.
    /// <paramref name="enabled"/> false registers the scheduler without the hosted service that starts it: on-demand
    /// triggers are stored but nothing fires in this process.
    /// </summary>
    public static IServiceCollection AddReconciliationScheduler(this IServiceCollection services, bool enabled)
    {
        services.AddSingleton<IRunTrigger, QuartzRunTrigger>();
        services.AddQuartz(quartz =>
        {
            quartz.ConfigureScheduler(scheduler =>
            {
                scheduler.InstanceName = DailyReconciliationJob.SchedulerName;
                scheduler.GenerateInstanceId = true;
                // A shutdown cancels the running job's token, so the run stops and releases its lock within the
                // host's shutdown budget instead of being cut off by it.
                scheduler.ShutdownJobInterruption = ShutdownJobInterruption.Always;
            });
            // Every start declares the job and the cron trigger again. Once stored they are kept as they are, next fire
            // time included, so an 00:30 missed while every node was down is still due at the next start and fires once
            // then (the misfire instruction). Replacing the trigger would compute its next fire from the new trigger's
            // start time, which is now, and skip the missed day; ScheduleTriggerRelativeToReplacedTrigger does not help,
            // as the next fire is still bounded by that start time. Cost: a changed job or trigger definition reaches
            // an existing database only once the stored one is deleted.
            quartz.ConfigureOptions<QuartzOptions>(options => options.Scheduling.IgnoreDuplicates = true);
            quartz.UsePersistentStore(store =>
            {
                // Resolved from configuration when the scheduler is built, so the connection string may arrive late.
                store.UsePostgres(source => source.ConnectionStringName = "Database");
                store.UseSystemTextJsonSerializer();
                store.UseClustering();
                store.ConfigureStore(options =>
                {
                    options.TablePrefix = "qrtz_";
                    options.StoreJobDataAsStrings = true;
                    // A firing the shutdown interrupted is stored as a recovery trigger, which this or another node
                    // fires; the job sees Recovering and reconciles the same date.
                    options.RecoverFiringsCancelledByShutdown = true;
                });
            });
            quartz.AddJob<DailyReconciliationJob>(job => job.WithIdentity(DailyReconciliationJob.Key)
                .StoreDurably().DisallowConcurrentExecution().RequestRecovery());
            quartz.AddTrigger(trigger => trigger.ForJob(DailyReconciliationJob.Key).WithIdentity(DailyReconciliationJob.DailyTriggerKey)
                .WithSchedule(CronScheduleBuilder.Create(DailyReconciliationJob.CronExpression)
                    .InTimeZone(TimeZoneInfo.Utc)
                    .WithMisfireInstruction(CronTriggerMisfireInstruction.FireAndProceed)));
        });
        // Shutdown interrupts a running job and waits for it to unwind, within the host's shutdown timeout.
        if (enabled) services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);
        return services;
    }
}
