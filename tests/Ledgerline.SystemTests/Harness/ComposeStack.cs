using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Ledgerline.SystemTests.Harness;

[CollectionDefinition(Name)]
public sealed class ComposeTests : ICollectionFixture<ComposeStack>
{
    public const string Name = "Compose stack";
}

/// <summary>
/// Drives the real Docker Compose stack (project <c>ledgerline-systest</c>, profile <c>app</c>).
/// Construction does nothing; the stack starts on the first <see cref="UpAsync"/> and goes down
/// (volumes kept) when the collection ends, unless LEDGERLINE_SYSTEM_TESTS_KEEP=1.
/// </summary>
[SuppressMessage("Naming", "CA1711", Justification = "Name fixed by the plan; a Docker Compose stack, not a Stack<T>.")]
public sealed class ComposeStack : IAsyncDisposable
{
    public const string ProjectName = "ledgerline-systest";
    public const string SkipReason = "System tests run only when LEDGERLINE_SYSTEM_TESTS=1.";
    private static readonly TimeSpan UpTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(3);
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool attempted; // An up ran in this session: logs are worth collecting and down is ours to run.
    private bool built;     // An up with --build succeeded in this session.

    public static bool Enabled => Environment.GetEnvironmentVariable("LEDGERLINE_SYSTEM_TESTS") == "1";

    public static bool KeepRunning => Environment.GetEnvironmentVariable("LEDGERLINE_SYSTEM_TESTS_KEEP") == "1";

    public string RepositoryRoot { get; } = FindRepositoryRoot();

    public string LogDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "compose-logs");

    /// <summary>
    /// Brings the whole stack to running and healthy. Until one call has succeeded it builds the images
    /// (<c>up -d --build --wait</c>); later calls run <c>up -d --wait</c>, which also brings back a service that an
    /// earlier test killed or stopped. Every call converges or throws; a failed call is retried by the next one.
    /// </summary>
    public async Task UpAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            await EnsureNoOtherLedgerlineProjectAsync(ct);
            attempted = true;
            string[] arguments = built
                ? ["up", "-d", "--wait", "--wait-timeout", Seconds(UpTimeout)]
                : ["up", "-d", "--build", "--wait", "--wait-timeout", Seconds(UpTimeout)];
            CommandResult up;
            try
            {
                up = await ComposeAsync(arguments, UpTimeout + TimeSpan.FromMinutes(1), ct);
            }
            catch (TimeoutException error)
            {
                throw new InvalidOperationException($"{error.Message} Logs: {await CollectLogsAsync("up-timeout")}", error);
            }
            if (up.ExitCode != 0)
            {
                var logs = await CollectLogsAsync("up-failed");
                throw new InvalidOperationException($"docker compose up failed with exit code {up.ExitCode}. Logs: {logs}{Environment.NewLine}{Tail(up.Output)}");
            }
            built = true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary><c>docker compose stop service</c>: graceful stop (SIGTERM, then SIGKILL after the grace period).</summary>
    public Task StopAsync(string service, CancellationToken ct = default) => RunCheckedAsync(["stop", service], ct);

    /// <summary><c>docker compose start --wait service</c>: starts a stopped or killed service and waits until it is healthy.</summary>
    public Task StartAsync(string service, CancellationToken ct = default) =>
        RunCheckedAsync(["start", "--wait", "--wait-timeout", "300", service], ct);

    /// <summary><c>docker compose kill -s KILL service</c>: the process dies without any shutdown code.</summary>
    public Task KillAsync(string service, CancellationToken ct = default) => RunCheckedAsync(["kill", "-s", "KILL", service], ct);

    /// <summary>Writes <c>docker compose logs</c> to a file under the test output directory and returns its path.</summary>
    public async Task<string> CollectLogsAsync(string name)
    {
        Directory.CreateDirectory(LogDirectory);
        var safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        var path = Path.Combine(LogDirectory, $"{safe}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmss}.log");
        var logs = await ComposeAsync(["logs", "--no-color", "--timestamps"], CommandTimeout, CancellationToken.None);
        await File.WriteAllTextAsync(path, logs.Output, CancellationToken.None);
        return path;
    }

    /// <summary>Call from a test class's DisposeAsync: collects logs when the current test failed.</summary>
    public async Task CollectLogsIfFailedAsync()
    {
        if (attempted && TestContext.Current.TestState?.Result == TestResult.Failed)
        {
            var path = await CollectLogsAsync(TestContext.Current.Test?.TestDisplayName ?? "failed-test");
            TestContext.Current.SendDiagnosticMessage($"Compose logs written to {path}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (attempted && !KeepRunning)
        {
            var down = await ComposeAsync(["down"], CommandTimeout, CancellationToken.None);
            if (down.ExitCode != 0) // Teardown must not fail the session; leave a trace instead.
                TestContext.Current.SendDiagnosticMessage($"docker compose -p {ProjectName} down failed with exit code {down.ExitCode}: {Tail(down.Output)}");
        }
        gate.Dispose();
    }

    private async Task RunCheckedAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var result = await ComposeAsync(arguments, CommandTimeout + TimeSpan.FromMinutes(3), ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"docker compose {string.Join(' ', arguments)} failed with exit code {result.ExitCode}.{Environment.NewLine}{Tail(result.Output)}");
    }

    private Task<CommandResult> ComposeAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct) =>
        RunAsync("docker", ["compose", "-p", ProjectName, "--profile", "app", .. arguments], timeout, ct);

    private async Task EnsureNoOtherLedgerlineProjectAsync(CancellationToken ct)
    {
        var result = await RunAsync("docker", ["ps", "--format", "{{.Label \"com.docker.compose.project\"}}"], CommandTimeout, ct);
        if (result.ExitCode != 0) throw new InvalidOperationException($"docker ps failed: {Tail(result.Output)}");
        var others = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(project => project.StartsWith("ledgerline", StringComparison.OrdinalIgnoreCase) && project != ProjectName)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (others.Length != 0)
            throw new InvalidOperationException(
                $"Another Ledgerline Compose project is running ({string.Join(", ", others)}). It publishes the same host ports. " +
                $"Bring it down first (docker compose -p <name> --profile app down, without -v); the system tests do not stop it.");
    }

    private async Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = RepositoryRoot, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"{fileName} {string.Join(' ', arguments)} did not finish within {timeout}.");
        }
        process.WaitForExit(); // Flushes the asynchronous output handlers.
        lock (output) return new CommandResult(process.ExitCode, output.ToString());
    }

    private static string Seconds(TimeSpan value) => ((int)value.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Tail(string output) => output.Length <= 4000 ? output : output[^4000..];

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "compose.yaml")) && File.Exists(Path.Combine(directory.FullName, "Ledgerline.slnx")))
                return directory.FullName;
        throw new InvalidOperationException("Repository root with compose.yaml not found above " + AppContext.BaseDirectory);
    }

    private sealed record CommandResult(int ExitCode, string Output);
}
