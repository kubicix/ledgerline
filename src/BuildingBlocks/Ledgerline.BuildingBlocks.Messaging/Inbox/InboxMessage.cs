namespace Ledgerline.BuildingBlocks.Messaging.Inbox;

public sealed class InboxMessage
{
    public Guid MessageId { get; set; }
    public string Consumer { get; set; } = "";
    public string Type { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; }
}
