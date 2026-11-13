namespace Ledgerline.Payments.Bank;

public sealed class BankOptions
{
    public string BaseUrl { get; set; } = "http://banksim:8080";
    public string ApiKey { get; set; } = "";
}
