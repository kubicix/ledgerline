using System.Globalization;
using Ledgerline.Reconciliation.Runs;
using Quartz;
using Quartz.Extensibility;

namespace Ledgerline.Reconciliation.Tests;

/// <summary>
/// The business date a firing of the daily job reconciles, including a recovery: the re-execution of a firing whose
/// scheduler instance died. Contexts are built with Quartz's own <see cref="JobExecutionContextBuilder"/>, which gives a
/// recovering context the data a job store's recovery trigger carries.
/// </summary>
public sealed class DailyReconciliationJobTests
{
    private static readonly DateTimeOffset Scheduled = new(2026, 11, 28, 0, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Cron_fire_reconciles_the_day_before_now()
    {
        var context = Context(Cron(), recovering: false, fired: Scheduled.AddSeconds(2));

        DailyReconciliationJob.BusinessDateOf(context, Scheduled.AddSeconds(2)).ShouldBe(new DateOnly(2026, 11, 27));
    }

    [Fact]
    public void Recovered_cron_fire_reconciles_the_date_of_the_interrupted_firing()
    {
        // The node died at 00:30 on the 28th; the recovery runs after the next midnight.
        var context = Context(Cron(), recovering: true, fired: Scheduled.AddSeconds(2));

        context.Recovering.ShouldBeTrue();
        DailyReconciliationJob.BusinessDateOf(context, new DateTimeOffset(2026, 11, 29, 0, 5, 0, TimeSpan.Zero))
            .ShouldBe(new DateOnly(2026, 11, 27));
    }

    [Fact]
    public void Recovered_firing_reads_the_original_scheduled_time_from_the_recovery_data()
    {
        // A recovery trigger as a job store may write it: the original times as Unix milliseconds, while the recovery
        // trigger itself fires later.
        var milliseconds = Scheduled.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var recovery = TriggerBuilder.Create().WithIdentity("recover_node-1_1", SchedulerConstants.DefaultRecoveryGroup)
            .ForJob(DailyReconciliationJob.Key)
            .UsingJobData(SchedulerConstants.FailedJobOriginalTriggerName, DailyReconciliationJob.DailyTriggerKey.Name)
            .UsingJobData(SchedulerConstants.FailedJobOriginalTriggerGroup, DailyReconciliationJob.DailyTriggerKey.Group)
            .UsingJobData(SchedulerConstants.FailedJobOriginalTriggerFireTime, milliseconds)
            .UsingJobData(SchedulerConstants.FailedJobOriginalTriggerScheduledFireTime, milliseconds)
            .StartAt(Scheduled.AddDays(1)).Build();
        // The context reports the recovery trigger's own times; the recovery data must win.
        var context = Context(recovery, recovering: true, fired: Scheduled.AddDays(1).AddMinutes(10), scheduled: Scheduled.AddDays(1));

        DailyReconciliationJob.BusinessDateOf(context, Scheduled.AddDays(1).AddMinutes(10)).ShouldBe(new DateOnly(2026, 11, 27));
    }

    [Fact]
    public void Recovered_one_shot_firing_keeps_its_business_date()
    {
        var oneShot = TriggerBuilder.Create().ForJob(DailyReconciliationJob.Key)
            .UsingJobData(DailyReconciliationJob.BusinessDateKey, "2026-11-13").UsingJobData(DailyReconciliationJob.AttemptKey, "2")
            .StartAt(Scheduled).Build();
        var context = Context(oneShot, recovering: true, fired: Scheduled);

        DailyReconciliationJob.BusinessDateOf(context, Scheduled.AddDays(3)).ShouldBe(new DateOnly(2026, 11, 13));
    }

    private static ITrigger Cron() => TriggerBuilder.Create().WithIdentity(DailyReconciliationJob.DailyTriggerKey).ForJob(DailyReconciliationJob.Key)
        .WithSchedule(CronScheduleBuilder.Create(DailyReconciliationJob.CronExpression).InTimeZone(TimeZoneInfo.Utc)).Build();

    private static Quartz.Impl.JobExecutionContextImpl Context(ITrigger trigger, bool recovering, DateTimeOffset fired, DateTimeOffset? scheduled = null) =>
        JobExecutionContextBuilder.For(new NoJob())
            .WithJob(JobBuilder.Create<NoJob>().WithIdentity(DailyReconciliationJob.Key).Build())
            .WithTrigger(trigger)
            .FiredAt(fired, scheduled ?? Scheduled)
            .Recovering(recovering)
            .Build();

    private sealed class NoJob : IJob
    {
        public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
