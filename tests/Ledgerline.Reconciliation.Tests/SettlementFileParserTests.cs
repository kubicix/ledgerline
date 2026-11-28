using System.Globalization;
using Ledgerline.Reconciliation.Matching;

namespace Ledgerline.Reconciliation.Tests;

public sealed class SettlementFileParserTests
{
    private const string Header = "record_type,bank_id,reference,amount_minor,currency,occurred_at";

    private static DateTimeOffset Utc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    [Fact]
    public void Parses_spec_sample()
    {
        const string csv = Header + "\n" +
            "CAPTURE,cap_0193a7f2,0193a7f2-5a00-7000-8000-000000000001,150000,TRY,2026-11-13T10:15:31Z\n" +
            "REFUND,ref_0193a800,0193a7f2-5a00-7000-8000-000000000009,150000,TRY,2026-11-13T14:02:10Z\n";

        var records = SettlementFileParser.Parse(csv);

        records.ShouldBe(
        [
            new BankRecord(BankRecordType.Capture, "cap_0193a7f2", "0193a7f2-5a00-7000-8000-000000000001", 150000, "TRY",
                Utc("2026-11-13T10:15:31Z")),
            new BankRecord(BankRecordType.Refund, "ref_0193a800", "0193a7f2-5a00-7000-8000-000000000009", 150000, "TRY",
                Utc("2026-11-13T14:02:10Z")),
        ]);
    }

    [Fact]
    public void Rejects_missing_column_with_line_number()
    {
        const string csv = Header + "\n" +
            "CAPTURE,cap_1,0193a7f2-5a00-7000-8000-000000000001,150000,TRY,2026-11-13T10:15:31Z\n" +
            "CAPTURE,cap_2,0193a7f2-5a00-7000-8000-000000000002,150000,TRY\n";

        var error = Should.Throw<SettlementFormatException>(() => SettlementFileParser.Parse(csv));

        error.LineNumber.ShouldBe(3);
        error.Message.ShouldContain("line 3");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12.50")]
    [InlineData("-5")]
    [InlineData("")]
    [InlineData("9223372036854775808")]
    public void Rejects_non_numeric_amount(string amount)
    {
        var csv = Header + "\n" + $"CAPTURE,cap_1,0193a7f2-5a00-7000-8000-000000000001,{amount},TRY,2026-11-13T10:15:31Z\n";

        var error = Should.Throw<SettlementFormatException>(() => SettlementFileParser.Parse(csv));

        error.LineNumber.ShouldBe(2);
        error.Message.ShouldContain("line 2");
        error.Message.ShouldContain("amount_minor");
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Empty_file_with_header_is_valid(string tail) =>
        SettlementFileParser.Parse(Header + tail).ShouldBeEmpty();

    [Fact]
    public void Accepts_crlf_and_the_bank_round_trip_timestamp_format()
    {
        const string csv = Header + "\r\n" +
            "CAPTURE,cap_1,ref-1,100,TRY,2026-11-13T10:15:31.1234567+00:00\r\n";

        var record = SettlementFileParser.Parse(csv).ShouldHaveSingleItem();

        record.OccurredAt.ShouldBe(Utc("2026-11-13T10:15:31.1234567Z"));
        record.Reference.ShouldBe("ref-1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("record_type,bank_id,reference,amount_minor,currency")]
    [InlineData("record_type,bank_id,reference,amount_minor,currency,occurred_at,extra")]
    public void Rejects_a_wrong_header_at_line_1(string header) =>
        Should.Throw<SettlementFormatException>(() => SettlementFileParser.Parse(header + "\n")).LineNumber.ShouldBe(1);

    [Theory]
    [InlineData("SETTLE,cap_1,r1,100,TRY,2026-11-13T10:15:31Z")]
    [InlineData("CAPTURE,cap_1,r1,100,TR,2026-11-13T10:15:31Z")]
    [InlineData("CAPTURE,cap_1,r1,100,T1Y,2026-11-13T10:15:31Z")]
    [InlineData("CAPTURE,cap_1,r1,100,TRY,yesterday")]
    [InlineData("CAPTURE,cap_1,r1,100,TRY,2026-11-13T10:15:31+03:00")]
    [InlineData("CAPTURE,cap_1,r1,100,TRY,2026-11-13T10:15:31")]
    [InlineData("CAPTURE,cap_1,r1,100,TRY,2026-11-13T10:15:31.5")]
    [InlineData("CAPTURE,cap_1,r1,100,try,2026-11-13T10:15:31Z")]
    [InlineData("CAPTURE,cap_1,r1,100,Try,2026-11-13T10:15:31Z")]
    [InlineData("CAPTURE,,r1,100,TRY,2026-11-13T10:15:31Z")]
    [InlineData("CAPTURE,cap_1,,100,TRY,2026-11-13T10:15:31Z")]
    [InlineData("CAPTURE,\"cap_1\",r1,100,TRY,2026-11-13T10:15:31Z")]
    [InlineData("")]
    public void Rejects_a_bad_data_line_with_its_line_number(string line)
    {
        var csv = Header + "\n" +
            "CAPTURE,cap_0,r0,1,TRY,2026-11-13T10:15:31Z\n" + line + "\n";

        Should.Throw<SettlementFormatException>(() => SettlementFileParser.Parse(csv)).LineNumber.ShouldBe(3);
    }
}
