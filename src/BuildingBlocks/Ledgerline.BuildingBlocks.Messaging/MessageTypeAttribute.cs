namespace Ledgerline.BuildingBlocks.Messaging;

[AttributeUsage(AttributeTargets.Class)]
public sealed class MessageTypeAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
