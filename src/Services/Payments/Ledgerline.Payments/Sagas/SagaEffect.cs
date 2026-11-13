namespace Ledgerline.Payments.Sagas;

public abstract record SagaEffect
{
    public sealed record SendCommand(object Message) : SagaEffect;
    public sealed record PublishEvent(object Message) : SagaEffect;
    public sealed record ScheduleTimeout(SagaStep Step, TimeSpan After) : SagaEffect;
    public sealed record CancelTimeouts : SagaEffect;
}
