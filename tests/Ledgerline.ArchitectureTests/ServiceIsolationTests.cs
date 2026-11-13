using System.Reflection;

namespace Ledgerline.ArchitectureTests;

/// <summary>
/// Guards the service boundaries described in docs/design.md:
/// a deployable never references another deployable, only building blocks and contracts.
/// </summary>
public class ServiceIsolationTests
{
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
