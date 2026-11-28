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

    internal Exception Fail()
    {
        Interlocked.Increment(ref failures);
        // The exception the driver throws when the server goes away mid-operation.
        return new MongoConnectionException(new ConnectionId(new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017))),
            "Injected connection failure.");
    }
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
