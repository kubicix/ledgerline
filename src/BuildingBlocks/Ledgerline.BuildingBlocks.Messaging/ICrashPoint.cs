namespace Ledgerline.BuildingBlocks.Messaging;

public interface ICrashPoint
{
    void Hit(string name);
}

public sealed class NoCrashPoint : ICrashPoint
{
    public void Hit(string name) { }
}
