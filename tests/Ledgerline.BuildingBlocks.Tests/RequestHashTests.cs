using System.Text;
using Ledgerline.BuildingBlocks.Web.Idempotency;

namespace Ledgerline.BuildingBlocks.Tests;

public sealed class RequestHashTests
{
    [Fact]
    public void Hash_is_independent_of_property_order_and_whitespace()
    {
        var first = RequestHash.Compute("POST", "/accounts", Encoding.UTF8.GetBytes("{\"type\":\"CustomerWallet\",\"currency\":\"TRY\"}"));
        var second = RequestHash.Compute("POST", "/accounts", Encoding.UTF8.GetBytes("{ \"currency\" : \"TRY\", \"type\" : \"CustomerWallet\" }"));
        first.ShouldBe(second);
        first.Length.ShouldBe(64);
    }

    [Fact]
    public void Hash_differs_for_different_path() => RequestHash.Compute("POST", "/accounts", "{}"u8)
        .ShouldNotBe(RequestHash.Compute("POST", "/transfers", "{}"u8));

    [Fact]
    public void Hash_supports_empty_body() => RequestHash.Compute("POST", "/accounts/id/freeze", [])
        .ShouldNotBe(RequestHash.Compute("POST", "/accounts/id/unfreeze", []));
}
