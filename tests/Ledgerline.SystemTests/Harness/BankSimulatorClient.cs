using System.Globalization;
using System.Text.Json;

namespace Ledgerline.SystemTests.Harness;

/// <summary>One row of the Bank Simulator settlement file (<c>GET /settlements/{date}</c>).</summary>
public sealed record SettlementRecord(string RecordType, string BankId, string Reference, long AmountMinor, string Currency, DateTimeOffset OccurredAt);

/// <summary>
/// Calls the Bank Simulator's test endpoints (<c>PUT /faults</c>, <c>DELETE /faults</c>, <c>GET /settlements/{date}</c>).
/// The simulator publishes no host port, so each call runs inside its own container through <c>docker compose exec</c>:
/// bash opens <c>/dev/tcp/127.0.0.1/8080</c> and sends an HTTP/1.0 request (no chunked body) with the container's own
/// <c>Bank__ApiKey</c>, so the key never passes through the test. The image has bash and base64, not curl.
/// </summary>
public sealed class BankSimulatorClient(ComposeStack stack)
{
    private const string Service = "banksim";

    // $1 method, $2 path, $3 base64 body (may be empty). Content-Length is ${#body}, a character count, which equals the
    // byte count only because the body is ASCII: System.Text.Json escapes every non-ASCII character by default.
    private const string Script =
        "exec 3<>/dev/tcp/127.0.0.1/8080 || exit 1; body=$(printf %s \"$3\" | base64 -d); " +
        "printf '%s %s HTTP/1.0\\r\\nHost: localhost\\r\\nX-Api-Key: %s\\r\\nContent-Type: application/json\\r\\nContent-Length: %s\\r\\nConnection: close\\r\\n\\r\\n%s' " +
        "\"$1\" \"$2\" \"$Bank__ApiKey\" \"${#body}\" \"$body\" >&3; cat <&3";

    /// <summary>Holds one fault in the simulator's memory until <paramref name="count"/> matching calls took it or it is cleared.</summary>
    public Task SetFaultAsync(string operation, string mode, int count, int? delayMs = null, CancellationToken ct = default) =>
        SendAsync("PUT", "/faults", new { operation, mode, count, delayMs }, ct);

    public Task ClearFaultAsync(CancellationToken ct = default) => SendAsync("DELETE", "/faults", null, ct);

    /// <summary>
    /// Leaves the capture <paramref name="captureId"/> out of the settlement file of <paramref name="date"/>
    /// (<c>PUT /settlement-tampering</c>). Held in the simulator's memory; it replaces any earlier tampering of that date.
    /// </summary>
    public Task DropCaptureFromSettlementAsync(DateOnly date, string captureId, CancellationToken ct = default) =>
        SendAsync("PUT", "/settlement-tampering", new { date, dropCaptureId = captureId }, ct);

    /// <summary>The settlement file of one UTC day: every successful capture and refund.</summary>
    public async Task<IReadOnlyList<SettlementRecord>> GetSettlementAsync(DateOnly date, CancellationToken ct = default)
    {
        var csv = await SendAsync("GET", "/settlements/" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null, ct);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0 || lines[0] != "record_type,bank_id,reference,amount_minor,currency,occurred_at")
            throw new InvalidOperationException($"Unexpected settlement file header for {date}: {lines.FirstOrDefault()}");
        return lines.Skip(1).Select(line =>
        {
            // References are payment GUIDs and IDs are cap_/ref_ tokens, so no field is quoted.
            var fields = line.Split(',');
            if (fields.Length != 6) throw new InvalidOperationException($"Unexpected settlement line: {line}");
            return new SettlementRecord(fields[0], fields[1], fields[2], long.Parse(fields[3], CultureInfo.InvariantCulture), fields[4],
                DateTimeOffset.Parse(fields[5], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        }).ToArray();
    }

    /// <summary>The settlement files of every UTC day from <paramref name="from"/> to <paramref name="to"/>, so a run across midnight is covered.</summary>
    public async Task<IReadOnlyList<SettlementRecord>> GetSettlementsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var records = new List<SettlementRecord>();
        for (var day = DateOnly.FromDateTime(from.UtcDateTime); day <= DateOnly.FromDateTime(to.UtcDateTime); day = day.AddDays(1))
            records.AddRange(await GetSettlementAsync(day, ct));
        return records;
    }

    private async Task<string> SendAsync(string method, string path, object? body, CancellationToken ct)
    {
        var encoded = body is null ? "" : Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(body, JsonSerializerOptions.Web));
        var response = await stack.ExecAsync(Service, ["bash", "-c", Script, "bash", method, path, encoded], ct);
        var lines = response.Split('\n').Select(x => x.TrimEnd('\r')).ToArray();
        var status = lines[0].Split(' ');
        var blank = Array.IndexOf(lines, "");
        var content = blank < 0 ? "" : string.Join('\n', lines.Skip(blank + 1));
        if (status.Length < 2 || !status[0].StartsWith("HTTP/", StringComparison.Ordinal) || !status[1].StartsWith('2'))
            throw new InvalidOperationException($"Bank Simulator {method} {path} answered '{lines[0]}': {content}");
        return content;
    }
}
