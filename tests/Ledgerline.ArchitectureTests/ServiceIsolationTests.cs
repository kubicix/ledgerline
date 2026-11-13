using System.Reflection;

namespace Ledgerline.ArchitectureTests;

/// <summary>
/// Guards the service boundaries described in docs/design.md:
/// a deployable never references another deployable, only building blocks and contracts.
/// </summary>
public class ServiceIsolationTests
{
    [Fact]
    public void Consumers_are_registered_only_through_MessagingBuilder()
    {
        foreach (var assembly in ProductionAssemblies().Where(x => x.GetName().Name != "Ledgerline.BuildingBlocks.Messaging"))
            assembly.GetReferencedAssemblies().ShouldNotContain(x => x.Name == "RabbitMQ.Client", assembly.GetName().Name);
    }

    [Fact]
    public void Only_ledger_namespace_writes_account_balances()
    {
        var account = typeof(Ledgerline.Accounts.Infrastructure.Entities.Account);
        account.GetProperty("BalanceMinor")!.SetMethod!.IsPrivate.ShouldBeTrue();
        account.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName && (method.IsPublic || method.IsAssembly) &&
                method.GetParameters().Any(parameter => parameter.ParameterType == typeof(long)))
            .Select(method => method.Name).ShouldBe(["ApplyPosting"]);
    }

    [Fact]
    public void Amounts_are_never_floating_point()
    {
        foreach (var type in ProductionAssemblies().SelectMany(assembly => assembly.GetTypes()))
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                if (MoneyName(property.Name)) FloatingPoint(property.PropertyType).ShouldBeFalse(type.FullName + "." + property.Name);
            foreach (var parameter in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                .SelectMany(method => method.GetParameters()).Concat(type.GetConstructors().SelectMany(constructor => constructor.GetParameters())))
                if (MoneyName(parameter.Name ?? "")) FloatingPoint(parameter.ParameterType).ShouldBeFalse(type.FullName + "." + parameter.Name);
        }
    }

    private static bool MoneyName(string name) => name.Contains("Amount", StringComparison.OrdinalIgnoreCase) || name.Contains("Minor", StringComparison.OrdinalIgnoreCase);
    private static bool FloatingPoint(Type type) => (Nullable.GetUnderlyingType(type) ?? type) is var actual &&
        (actual == typeof(decimal) || actual == typeof(double) || actual == typeof(float));
    private static IEnumerable<Assembly> ProductionAssemblies() => Deployables.Concat([
        typeof(Ledgerline.BuildingBlocks.Money).Assembly, typeof(Ledgerline.BuildingBlocks.Messaging.MessageContext).Assembly,
        typeof(Ledgerline.BuildingBlocks.Web.WebDefaults).Assembly, typeof(Ledgerline.Contracts.Accounts.AccountOpened).Assembly]).Distinct();

    private static readonly Assembly[] Deployables =
    [
        typeof(Ledgerline.Accounts.Program).Assembly,
        typeof(Ledgerline.Payments.Program).Assembly,
        typeof(Ledgerline.Risk.Program).Assembly,
        typeof(Ledgerline.Notifications.Program).Assembly,
        typeof(Ledgerline.BankSimulator.Program).Assembly,
        typeof(Ledgerline.Gateway.Program).Assembly,
    ];

    [Fact]
    public void Deployables_do_not_reference_each_other()
    {
        var names = Deployables.Select(a => a.GetName().Name!).ToHashSet();

        foreach (var assembly in Deployables)
        {
            var forbidden = assembly.GetReferencedAssemblies()
                .Select(r => r.Name!)
                .Where(names.Contains)
                .ToArray();

            forbidden.ShouldBeEmpty($"{assembly.GetName().Name} must not reference another deployable");
        }
    }

    [Fact]
    public void BankSimulator_and_Gateway_do_not_reference_contracts_or_messaging()
    {
        string[] forbidden = ["Ledgerline.Contracts", "Ledgerline.BuildingBlocks.Messaging"];
        Assembly[] edge = [typeof(Ledgerline.BankSimulator.Program).Assembly, typeof(Ledgerline.Gateway.Program).Assembly];

        foreach (var assembly in edge)
        {
            assembly.GetReferencedAssemblies()
                .Select(r => r.Name!)
                .Where(forbidden.Contains)
                .ShouldBeEmpty($"{assembly.GetName().Name} talks HTTP only");
        }
    }
}
