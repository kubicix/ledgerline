namespace Ledgerline.BuildingBlocks.Messaging.Outbox;

public sealed class RelayOptions
{
    public int PollIntervalMs { get; set; } = 250;
    public int BatchSize { get; set; } = 100;
    /// <summary>The owning service; tags the outbox metrics.</summary>
    public string Service { get; set; } = "";
}
