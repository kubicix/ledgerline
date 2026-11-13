namespace Ledgerline.Payments.Sagas;

public enum PaymentStatus
{
    Processing, Authorized, Capturing, Captured, Voiding, Voided,
    Completed, Compensating, Compensated, Rejected, Failed, ManualReview
}
