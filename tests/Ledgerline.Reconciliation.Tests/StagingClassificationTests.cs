using System.Text.Json;
using Ledgerline.Reconciliation.Staging;
using Npgsql;

namespace Ledgerline.Reconciliation.Tests;

/// <summary>Which staging failures skip a record (poison) and which retry it without committing its offset.</summary>
public sealed class StagingClassificationTests
{
    public static TheoryData<Exception> PayloadReadErrors => new()
    {
        new ArgumentException("bad"), new ArgumentOutOfRangeException("at"), new FormatException(), new OverflowException()
    };

    [Theory]
    [MemberData(nameof(PayloadReadErrors))]
    public void Argument_format_and_overflow_errors_while_reading_a_payload_are_poison(Exception error)
    {
        var raised = Should.Throw<InvalidDataException>(() => StagingConsumerLoop.ReadPayload<int>(() => throw error));
        raised.InnerException.ShouldBeSameAs(error);
        StagingConsumerLoop.IsPoison(raised).ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(PayloadReadErrors))]
    public void The_same_errors_outside_payload_reading_are_retried(Exception error) =>
        // For example a connection string keyword the driver rejects with an ArgumentException.
        StagingConsumerLoop.IsPoison(error).ShouldBeFalse();

    [Fact]
    public void Parse_and_validation_errors_are_poison()
    {
        StagingConsumerLoop.IsPoison(new JsonException("bad", new FormatException())).ShouldBeTrue();
        StagingConsumerLoop.IsPoison(new InvalidDataException("missing field")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("22001", true)]
    [InlineData("22P02", true)]
    [InlineData("23505", true)]
    [InlineData("23503", true)]
    [InlineData("42P01", false)]
    [InlineData("3D000", false)]
    [InlineData("28P01", false)]
    [InlineData("53300", false)]
    [InlineData("57P01", false)]
    [InlineData("40001", false)]
    [InlineData("40P01", false)]
    public void Only_data_and_constraint_errors_of_the_database_are_poison(string sqlState, bool poison) =>
        StagingConsumerLoop.IsPoison(new PostgresException("synthetic", "ERROR", "ERROR", sqlState)).ShouldBe(poison);

    [Fact]
    public void Driver_errors_are_classified_before_their_inner_exception()
    {
        // A client-side driver failure retries even when what it wraps looks like bad data.
        StagingConsumerLoop.IsPoison(new NpgsqlException("connection failed", new FormatException())).ShouldBeFalse();
        StagingConsumerLoop.IsPoison(new NpgsqlException("connection failed", new InvalidDataException())).ShouldBeFalse();
        StagingConsumerLoop.IsPoison(new TimeoutException()).ShouldBeFalse();
        StagingConsumerLoop.IsPoison(new InvalidOperationException()).ShouldBeFalse();
    }
}
