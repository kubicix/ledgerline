using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Accounts;

namespace Ledgerline.BuildingBlocks.Tests.Contracts;

public sealed class ContractSampleTests
{
    public static IEnumerable<object[]> ContractTypes => typeof(CreditTopUp).Assembly.GetTypes()
        .Where(t => t.GetCustomAttribute<MessageTypeAttribute>() is not null).Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(ContractTypes))]
    public async Task Every_contract_type_round_trips_its_sample(Type type)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Contracts", "Samples", type.Name + ".json");
        File.Exists(path).ShouldBeTrue($"Contract {type.Name} needs a JSON sample.");
        var sample = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(sample);
        var payload = EnvelopeSerializer.FromPayload(document.RootElement, type);
        var actual = EnvelopeSerializer.ToPayload(payload).GetRawText();
        JsonNode.DeepEquals(JsonNode.Parse(sample), JsonNode.Parse(actual)).ShouldBeTrue($"Contract {type.Name} changed its wire shape.");
    }

    [Fact]
    public void Every_contract_type_name_is_unique()
    {
        var names = ContractTypes.Select(row => ((Type)row[0]).GetCustomAttribute<MessageTypeAttribute>()!.Name).ToArray();
        names.Length.ShouldBeGreaterThan(0);
        names.Distinct(StringComparer.Ordinal).Count().ShouldBe(names.Length);
    }
}
