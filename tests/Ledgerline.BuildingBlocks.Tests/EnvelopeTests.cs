using System.Text.Json;
using Ledgerline.BuildingBlocks.Messaging;

namespace Ledgerline.BuildingBlocks.Tests;

public sealed class EnvelopeTests
{
    private const string Sample = """
        {"messageId":"0193a7f2-6c1e-7b3a-9d2e-5f8a1c4b7e90","type":"accounts.command.reserve-funds.v1","occurredAt":"2026-11-13T10:15:30.123Z","correlationId":"0193a7f2-5a00-7000-8000-000000000001","causationId":"0193a7f2-5b11-7000-8000-000000000002","traceParent":"00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01","payload":{}}
        """;

    [Fact]
    public void Serialize_uses_camel_case_and_iso_timestamps()
    {
        var envelope = new MessageEnvelope(
            Guid.Parse("0193a7f2-6c1e-7b3a-9d2e-5f8a1c4b7e90"), "accounts.command.reserve-funds.v1",
            DateTimeOffset.Parse("2026-11-13T10:15:30.123Z", System.Globalization.CultureInfo.InvariantCulture),
            Guid.Parse("0193a7f2-5a00-7000-8000-000000000001"), Guid.Parse("0193a7f2-5b11-7000-8000-000000000002"),
            "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", JsonSerializer.SerializeToElement(new { }));
        using var actual = JsonDocument.Parse(EnvelopeSerializer.Serialize(envelope));
        using var expected = JsonDocument.Parse(Sample);
        actual.RootElement.EnumerateObject().Select(p => p.Name).ShouldBe(expected.RootElement.EnumerateObject().Select(p => p.Name));
        foreach (var property in expected.RootElement.EnumerateObject())
        {
            var value = actual.RootElement.GetProperty(property.Name);
            if (property.Name == "occurredAt")
                value.GetDateTimeOffset().ShouldBe(envelope.OccurredAt);
            else
                value.ToString().ShouldBe(property.Value.ToString());
        }
    }

    [Fact]
    public void Deserialize_ignores_unknown_fields()
    {
        var json = Sample.Replace("\"payload\":{}", "\"payload\":{},\"future\":42", StringComparison.Ordinal);
        EnvelopeSerializer.Deserialize(System.Text.Encoding.UTF8.GetBytes(json)).MessageId
            .ShouldBe(Guid.Parse("0193a7f2-6c1e-7b3a-9d2e-5f8a1c4b7e90"));
    }

    [Fact]
    public void Deserialize_tolerates_missing_optional_fields()
    {
        var json = """{"messageId":"0193a7f2-6c1e-7b3a-9d2e-5f8a1c4b7e90","type":"accounts.command.reserve-funds.v1","occurredAt":"2026-11-13T10:15:30.123Z","correlationId":"0193a7f2-5a00-7000-8000-000000000001","payload":{}}""";
        var envelope = EnvelopeSerializer.Deserialize(System.Text.Encoding.UTF8.GetBytes(json));
        envelope.CausationId.ShouldBeNull();
        envelope.TraceParent.ShouldBeNull();
    }

    [Fact]
    public void NameOf_reads_MessageType_attribute()
    {
        var registry = new MessageTypeRegistry();
        registry.NameOf<ReserveFunds>().ShouldBe("accounts.command.reserve-funds.v1");
        registry.TryResolve("accounts.command.reserve-funds.v1", out var type).ShouldBeTrue();
        type.ShouldBe(typeof(ReserveFunds));
        registry.NameOf(type).ShouldBe("accounts.command.reserve-funds.v1");
        registry.TryResolve("unknown", out _).ShouldBeFalse();
    }

    [Fact]
    public void NameOf_throws_for_type_without_attribute() =>
        Should.Throw<ArgumentException>(() => new MessageTypeRegistry().NameOf<UnannotatedMessage>());

    [Fact]
    public void ExchangeFor_uses_first_segment() =>
        MessageTypeRegistry.ExchangeFor("accounts.command.reserve-funds.v1").ShouldBe("ledgerline.accounts");

    [Theory]
    [InlineData(typeof(InvalidType))]
    [InlineData(typeof(InvalidOwner))]
    [InlineData(typeof(InvalidVersion))]
    public void Type_name_must_match_pattern(Type type) =>
        Should.Throw<ArgumentException>(() => new MessageTypeRegistry().Register(type));

    [Fact]
    public void Payload_round_trips_with_camel_case_and_string_enums()
    {
        var payload = EnvelopeSerializer.ToPayload(new ReserveFunds(123, TestState.Ready));
        payload.GetProperty("amountMinor").GetInt64().ShouldBe(123);
        payload.GetProperty("state").GetString().ShouldBe("Ready");
        EnvelopeSerializer.FromPayload(payload, typeof(ReserveFunds)).ShouldBe(new ReserveFunds(123, TestState.Ready));
    }

    [MessageType("accounts.command.reserve-funds.v1")]
    public sealed record ReserveFunds(long AmountMinor, TestState State);
    public enum TestState { Ready }
    public sealed record UnannotatedMessage;
    [MessageType("accounts.query.reserve.v1")]
    public sealed record InvalidType;
    [MessageType("Accounts.command.reserve.v1")]
    public sealed record InvalidOwner;
    [MessageType("accounts.command.reserve.vx")]
    public sealed record InvalidVersion;
}

