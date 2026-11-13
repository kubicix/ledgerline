using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Ledgerline.BuildingBlocks.Messaging;

public sealed partial class MessageTypeRegistry
{
    private readonly ConcurrentDictionary<string, Type> types = new(StringComparer.Ordinal);

    public string NameOf<T>() => NameOf(typeof(T));

    public string NameOf(Type type)
    {
        Register(type);
        return type.GetCustomAttribute<MessageTypeAttribute>()!.Name;
    }

    public bool TryResolve(string name, out Type type) => types.TryGetValue(name, out type!);

    public void Register(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var name = type.GetCustomAttribute<MessageTypeAttribute>()?.Name;
        if (name is null || !TypeNamePattern().IsMatch(name))
            throw new ArgumentException("Message type must declare a valid MessageType attribute.", nameof(type));
        if (types.GetOrAdd(name, type) != type)
            throw new ArgumentException("Message type name is already registered to another type.", nameof(type));
    }

    public static string ExchangeFor(string typeName) => "ledgerline." + typeName.Split('.')[0];

    [GeneratedRegex(@"^[a-z]+\.(command|event)\.[a-z0-9-]+\.v[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TypeNamePattern();
}
