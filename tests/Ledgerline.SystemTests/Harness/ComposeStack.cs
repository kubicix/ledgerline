using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Ledgerline.Testing;

// The two stack shapes publish the same host ports, so only one Compose project may be up at a time: the collections run
// one after another, never in parallel.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace Ledgerline.SystemTests.Harness;

[CollectionDefinition(Name)]
public sealed class ComposeTests : ICollectionFixture<ComposeStack>
{
    public const string Name = "Compose stack";
}

[CollectionDefinition(Name)]
public sealed class Phase2ComposeTests : ICollectionFixture<Phase2ComposeStack>
{
    public const string Name = "Phase 2 Compose stack";
}

/// <summary>
/// One shape of the Compose stack: its project name, the Compose files (none means Compose's own default, compose.yaml)
/// and the profiles. Each shape has its own project and therefore its own volumes.
/// </summary>
public sealed record StackShape(string ProjectName, IReadOnlyList<string> Files, IReadOnlyList<string> Profiles)
{
    /// <summary>The M1 to M4 system: compose.yaml with profile <c>app</c>, project <c>ledgerline-systest</c>.</summary>
    public static StackShape Default { get; } = new(ComposeStack.ProjectName, [], ["app"]);

    /// <summary>
    /// Phase 2: compose.yaml plus compose.phase2.yaml with profiles <c>app</c> and <c>phase2</c>, project
    /// <c>ledgerline-systest-phase2</c>. A completed reconciliation day cannot be run again, so on the kept volumes of one
    /// project the Phase 2 reconciliation test passes once per UTC day. To rerun it the same day, set
    /// LEDGERLINE_SYSTEM_TESTS_PHASE2_PROJECT to a fresh project name (new volumes; the old ones are kept).
    /// </summary>
    public static StackShape Phase2 { get; } = new(
        Environment.GetEnvironmentVariable("LEDGERLINE_SYSTEM_TESTS_PHASE2_PROJECT") ?? ComposeStack.ProjectName + "-phase2",
        ["compose.yaml", "compose.phase2.yaml"], ["app", "phase2"]);

    /// <summary>The arguments that select this shape: <c>compose -p name [-f file]... --profile p...</c>.</summary>
    public IReadOnlyList<string> ComposeArguments =>
        ["compose", "-p", ProjectName, .. Files.SelectMany(file => new[] { "-f", file }), .. Profiles.SelectMany(profile => new[] { "--profile", profile })];
}

/// <summary>The collection fixture of <see cref="Phase2ComposeTests"/>: a <see cref="ComposeStack"/> of <see cref="StackShape.Phase2"/>.</summary>
[SuppressMessage("Naming", "CA1711", Justification = "A Docker Compose stack, not a Stack<T>, named like ComposeStack.")]
public sealed class Phase2ComposeStack : IAsyncDisposable
{
    public ComposeStack Stack { get; } = new(StackShape.Phase2);

    public ValueTask DisposeAsync() => Stack.DisposeAsync();
}

/// <summary>
/// Drives the real Docker Compose stack of one <see cref="StackShape"/> (by default project <c>ledgerline-systest</c>,
/// profile <c>app</c>). Construction does nothing; the stack starts on the first <see cref="UpAsync"/> and goes down
/// (volumes kept) when the collection ends, unless LEDGERLINE_SYSTEM_TESTS_KEEP=1. A kept stack of the other shape that
/// this test process started is brought down (volumes kept, logs written first) before this one starts.
/// </summary>
[SuppressMessage("Naming", "CA1711", Justification = "Name fixed by the plan; a Docker Compose stack, not a Stack<T>.")]
public sealed class ComposeStack : IAsyncDisposable
{
    public const string ProjectName = "ledgerline-systest";
    public const string SkipReason = "System tests run only when LEDGERLINE_SYSTEM_TESTS=1.";
    private static readonly TimeSpan UpTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(3);
    // Stacks this test process brought up, by project name, so one shape can take down a kept stack of the other.
    private static readonly ConcurrentDictionary<string, ComposeStack> StartedStacks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool attempted; // An up ran in this session: logs are worth collecting and down is ours to run.
    private bool built;     // An up with --build succeeded in this session.
    private readonly SemaphoreSlim velocityGate = new(1, 1);
    private RiskRuleDto? velocityRestore; // Set once a raise was sent; DisposeAsync puts these values back.
    private bool velocityRaised;          // The raise was sent and Risk restarted with it.

    public static bool Enabled => Environment.GetEnvironmentVariable("LEDGERLINE_SYSTEM_TESTS") == "1";

    public static bool KeepRunning => Environment.GetEnvironmentVariable("LEDGERLINE_SYSTEM_TESTS_KEEP") == "1";

    /// <summary>The collection fixture of <see cref="ComposeTests"/>: <see cref="StackShape.Default"/>.</summary>
    public ComposeStack() : this(StackShape.Default)
    {
    }

    internal ComposeStack(StackShape shape) => Shape = shape;

    public StackShape Shape { get; }

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
            StartedStacks[Shape.ProjectName] = this;
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

    /// <summary>
    /// Raises Risk's velocity rule to <see cref="RiskRules.RaisedVelocityMaxCount"/> (same window) once per test process,
    /// so workloads of many payments from the two seeded customers are not Rejected, then restarts Risk because it caches
    /// its rules for 10 s. Later and concurrent calls wait for the first and do nothing; a failed attempt is retried by the
    /// next call. The rule is restored in <see cref="DisposeAsync"/>, before <c>down</c>. Call after <see cref="UpAsync"/>.
    /// </summary>
    public async Task EnsureVelocityRaisedAsync(CancellationToken ct = default)
    {
        await velocityGate.WaitAsync(ct);
        try
        {
            if (velocityRaised) return;
            using var client = LedgerlineClient.CreateDefault();
            var current = await RiskRules.GetVelocityRuleAsync(client, ct);
            // Registered before the PUT, so a PUT that committed but whose answer was lost is still restored. Kept from the
            // first attempt: a retry would read the raised value back as its "original".
            velocityRestore ??= RiskRules.RestoreTarget(current);
            await RiskRules.UpdateAsync(client, current with { MaxCount = RiskRules.RaisedVelocityMaxCount, Enabled = true }, ct);
            await StopAsync("risk", ct);
            await StartAsync("risk", ct);
            velocityRaised = true;
        }
        finally
        {
            velocityGate.Release();
        }
    }

    /// <summary><c>docker compose stop service</c>: graceful stop (SIGTERM, then SIGKILL after the grace period).</summary>
    public Task StopAsync(string service, CancellationToken ct = default) => RunCheckedAsync(["stop", service], ct);

    /// <summary><c>docker compose start --wait service</c>: starts a stopped or killed service and waits until it is healthy.</summary>
    public Task StartAsync(string service, CancellationToken ct = default) =>
        RunCheckedAsync(["start", "--wait", "--wait-timeout", "300", service], ct);

    /// <summary>
    /// <c>docker compose kill -s KILL service</c>: the process dies without any shutdown code. Returns only once Docker no
    /// longer lists a running container of the service (up to 30 s, else throws), so a following <see cref="StartAsync"/>
    /// does not see a container that is still marked running and skip it.
    /// </summary>
    public async Task KillAsync(string service, CancellationToken ct = default)
    {
        await RunCheckedAsync(["kill", "-s", "KILL", service], ct);
        await Eventually.ShouldAsync(
            async () => (await ComposeAsync(["ps", "--status", "running", "-q", service], CommandTimeout, ct)).StandardOutput.Trim().Length == 0,
            TimeSpan.FromSeconds(30),
            $"A container of service {service} was still running after docker compose kill.");
    }

    /// <summary>
    /// <c>docker compose exec -T service command</c>: runs a command inside a running service container and returns its
    /// standard output (standard error is kept out of it). Throws when the command exits non-zero.
    /// </summary>
    public async Task<string> ExecAsync(string service, IReadOnlyList<string> command, CancellationToken ct = default)
    {
        var result = await ComposeAsync(["exec", "-T", service, .. command], CommandTimeout, ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"docker compose exec {service} failed with exit code {result.ExitCode}.{Environment.NewLine}{Tail(result.Output)}");
        return result.StandardOutput;
    }

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
        // Also with LEDGERLINE_SYSTEM_TESTS_KEEP=1: a kept stack must not keep the raised limit.
        if (velocityRestore is { } restore)
        {
            try
            {
                using var client = LedgerlineClient.CreateDefault();
                await RiskRules.UpdateAsync(client, restore, CancellationToken.None);
            }
            catch (Exception error) // Teardown must not fail the session; the next run restores the seeded rule instead.
            {
                TestContext.Current.SendDiagnosticMessage($"Restoring the Risk velocity rule failed: {error.Message}");
            }
        }
        if (attempted && !KeepRunning) _ = await DownAsync();
        gate.Dispose();
        velocityGate.Dispose();
    }

    /// <summary><c>down</c> without <c>-v</c>; volumes are kept. A failure leaves a trace and returns false instead of throwing.</summary>
    private async Task<bool> DownAsync()
    {
        var down = await ComposeAsync(["down"], CommandTimeout, CancellationToken.None);
        if (down.ExitCode != 0) // Teardown must not fail the session; leave a trace instead.
        {
            TestContext.Current.SendDiagnosticMessage($"docker compose -p {Shape.ProjectName} down failed with exit code {down.ExitCode}: {Tail(down.Output)}");
            return false;
        }
        StartedStacks.TryRemove(new KeyValuePair<string, ComposeStack>(Shape.ProjectName, this));
        return true;
    }

    /// <summary>
    /// Brings down a stack of another shape that this process started and kept (LEDGERLINE_SYSTEM_TESTS_KEEP=1), so its
    /// host ports are free; its collection has ended. Its logs are written first, since a kept stack exists to keep them.
    /// </summary>
    private async Task<bool> MakeWayAsync()
    {
        var path = await CollectLogsAsync(Shape.ProjectName + "-kept");
        TestContext.Current.SendDiagnosticMessage($"Bringing down kept project {Shape.ProjectName} before the next stack; its logs are in {path}");
        return await DownAsync();
    }

    private async Task RunCheckedAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var result = await ComposeAsync(arguments, CommandTimeout + TimeSpan.FromMinutes(3), ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"docker compose {string.Join(' ', arguments)} failed with exit code {result.ExitCode}.{Environment.NewLine}{Tail(result.Output)}");
    }

    private Task<CommandResult> ComposeAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct) =>
        RunAsync("docker", [.. Shape.ComposeArguments, .. arguments], timeout, ct);

    private async Task EnsureNoOtherLedgerlineProjectAsync(CancellationToken ct)
    {
        var result = await RunAsync("docker", ["ps", "--format", "{{.Label \"com.docker.compose.project\"}}"], CommandTimeout, ct);
        if (result.ExitCode != 0) throw new InvalidOperationException($"docker ps failed: {Tail(result.Output)}");
        var running = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(project => project.StartsWith("ledgerline", StringComparison.OrdinalIgnoreCase) && project != Shape.ProjectName)
            .Distinct(StringComparer.Ordinal).ToArray();
        // Only stacks this process started are brought down; any other Ledgerline project stops the run as before.
        var others = new List<string>();
        foreach (var project in running)
            if (!StartedStacks.TryGetValue(project, out var kept) || !await kept.MakeWayAsync()) others.Add(project);
        if (others.Count != 0)
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
        var standardOutput = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) lock (output) { output.AppendLine(e.Data); standardOutput.AppendLine(e.Data); }
        };
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
        lock (output) return new CommandResult(process.ExitCode, output.ToString(), standardOutput.ToString());
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

    private sealed record CommandResult(int ExitCode, string Output, string StandardOutput);
}
