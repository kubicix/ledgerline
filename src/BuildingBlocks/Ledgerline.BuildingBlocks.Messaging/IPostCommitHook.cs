namespace Ledgerline.BuildingBlocks.Messaging;

public interface IPostCommitHook
{
    void OnCommitted();
}
