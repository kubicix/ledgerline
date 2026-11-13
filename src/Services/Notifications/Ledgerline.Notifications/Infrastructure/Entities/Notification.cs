namespace Ledgerline.Notifications.Infrastructure.Entities;

public sealed class Notification
{
    public Guid Id { get; set; }
    public Guid SourceMessageId { get; set; }
    public Guid RecipientOwnerId { get; set; }
    public string RecipientEmail { get; set; } = "";
    public string Template { get; set; } = "";
    public Guid? PaymentId { get; set; }
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public string? Reason { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}
