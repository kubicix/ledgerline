using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Ledgerline.Reporting.Infrastructure;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;

namespace Ledgerline.Reporting.IntegrationTests;

/// <summary>Switches injected MongoDB failures on and off and counts the calls it failed.</summary>
public sealed class MongoFaults
{
    private int failures;
    private volatile bool failing;

    public bool Failing { get => failing; set => failing = value; }
    public int Failures => Volatile.Read(ref failures);

    /// <summary>The exception each failed call throws; by default the driver's connection failure.</summary>
    public Func<Exception> Error { get; init; } = ConnectionFailure;

    internal Exception Fail()
    {
        Interlocked.Increment(ref failures);
        return Error();
    }

    /// <summary>The exception the driver throws when the server goes away mid-operation.</summary>
    public static Exception ConnectionFailure() => new MongoConnectionException(ConnectionId(), "Injected connection failure.");

    /// <summary>
    /// What <c>ReplaceOne</c> throws on a write concern timeout (code 64, no error label): a write exception with a
    /// write concern error and no write error. The driver builds <see cref="WriteConcernError"/> internally only, so
    /// its constructor is called by reflection, with each parameter filled by its type.
    /// </summary>
    public static Exception WriteConcernTimeout()
    {
        var constructor = typeof(WriteConcernError).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single();
        var arguments = constructor.GetParameters().Select(parameter => parameter.ParameterType switch
        {
            var type when type == typeof(int) => (object?)64,
            var type when type == typeof(string) => parameter.Name == "codeName" ? "WriteConcernFailed" : "waiting for replication timed out",
            var type when type == typeof(MongoDB.Bson.BsonDocument) => new MongoDB.Bson.BsonDocument("wtimeout", true),
            var type when type.IsAssignableFrom(typeof(string[])) => Array.Empty<string>(),
            var type => throw new InvalidOperationException("Unexpected WriteConcernError parameter " + type)
        }).ToArray();
        return new MongoWriteException(ConnectionId(), null, (WriteConcernError)constructor.Invoke(arguments), null);
    }

    private static ConnectionId ConnectionId() => new(new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017)));
}

/// <summary>Collections whose named collection fails every operation while <see cref="MongoFaults.Failing"/> is on.</summary>
public sealed class FaultyMongoCollections(IMongoDatabase database, MongoFaults faults, string failingCollection) : MongoCollections(database)
{
    protected override IMongoCollection<TDocument> Open<TDocument>(string name)
    {
        var collection = base.Open<TDocument>(name);
        return name == failingCollection ? FaultyCollectionProxy<TDocument>.Wrap(collection, faults) : collection;
    }
}

/// <summary>
/// Delegates every member of the real collection. While failing, every method call (writes, reads, bulk writes)
/// throws; property getters such as <c>Indexes</c> still pass through, so index creation is unaffected.
/// </summary>
public class FaultyCollectionProxy<TDocument> : DispatchProxy
{
    private IMongoCollection<TDocument> inner = null!;
    private MongoFaults faults = null!;

    internal static IMongoCollection<TDocument> Wrap(IMongoCollection<TDocument> inner, MongoFaults faults)
    {
        var proxy = Create<IMongoCollection<TDocument>, FaultyCollectionProxy<TDocument>>();
        var self = (FaultyCollectionProxy<TDocument>)(object)proxy;
        self.inner = inner;
        self.faults = faults;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (!targetMethod.IsSpecialName && faults.Failing) throw faults.Fail();
        try
        {
            return targetMethod.Invoke(inner, args);
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Throw(error.InnerException);
            throw;
        }
    }
}
