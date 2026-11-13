namespace Ledgerline.BuildingBlocks;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
