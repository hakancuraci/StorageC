using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StorageC;

return await ProgramEntry.RunAsync(args);

internal static class ProgramEntry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task<int> RunAsync(string[] args)
    {
        LoadTestOptions options = LoadTestOptions.Parse(args);
        Directory.CreateDirectory(options.OutputDirectory);

        var report = new LoadTestReport
        {
            SchemaVersion = 1,
            StartedAtUtc = DateTimeOffset.UtcNow,
            Commit = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "local",
            Ref = Environment.GetEnvironmentVariable("GITHUB_REF") ?? "local",
            EventName = Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME") ?? "local",
            RunnerOs = RuntimeInformation.OSDescription,
            Framework = RuntimeInformation.FrameworkDescription,
            ProcessorCount = Environment.ProcessorCount,
            PayloadBytes = options.PayloadBytes,
            RequestedDurationSeconds = options.DurationSeconds,
            Enforced = options.Enforce
        };

        try
        {
            var runner = new SmbLoadRunner(options);
            report.Scenarios.AddRange(await runner.RunAsync());
        }
        catch (Exception exception)
        {
            report.FatalError = exception.ToString();
        }

        LoadTestReport? baseline = TryReadBaseline(options.BaselinePath);
        PerformanceComparison.Apply(report, baseline);
        report.CompletedAtUtc = DateTimeOffset.UtcNow;

        string jsonPath = Path.Combine(options.OutputDirectory, "result.json");
        string markdownPath = Path.Combine(options.OutputDirectory, "report.md");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, JsonOptions));
        string markdown = MarkdownReport.Create(report);
        await File.WriteAllTextAsync(markdownPath, markdown);
        Console.WriteLine(markdown);

        return options.Enforce && report.HasBlockingIssues ? 2 : 0;
    }

    private static LoadTestReport? TryReadBaseline(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<LoadTestReport>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Baseline could not be read: {exception.Message}");
            return null;
        }
    }
}

internal sealed class SmbLoadRunner(LoadTestOptions options)
{
    private readonly byte[] _payload = CreatePayload(options.PayloadBytes);
    private readonly string _runToken = Sanitize(Environment.GetEnvironmentVariable("GITHUB_RUN_ID")
        ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

    public async Task<IReadOnlyList<ScenarioResult>> RunAsync()
    {
        int[] concurrencyLevels = [1, 4, 8];
        TimeSpan scenarioDuration = TimeSpan.FromSeconds(Math.Max(5, options.DurationSeconds / concurrencyLevels.Length));
        var results = new List<ScenarioResult>(concurrencyLevels.Length);
        foreach (int concurrency in concurrencyLevels)
            results.Add(await RunScenarioAsync(concurrency, scenarioDuration));
        return results;
    }

    private async Task<ScenarioResult> RunScenarioAsync(int concurrency, TimeSpan duration)
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        long workingSetStart = process.WorkingSet64;
        long allocatedStart = GC.GetTotalAllocatedBytes(precise: true);
        int gen0Start = GC.CollectionCount(0);
        int gen1Start = GC.CollectionCount(1);
        int gen2Start = GC.CollectionCount(2);

        await using PooledSmbClient pool = PooledSmbClient.Create(CreateClientOptions(), new SmbClientPoolOptions
        {
            MaxClients = concurrency,
            MaxPendingAcquires = concurrency * 4,
            AcquireTimeout = TimeSpan.FromSeconds(10),
            IdleTimeout = TimeSpan.FromMinutes(5)
        });

        await WarmUpAsync(pool, concurrency);

        using var monitorCancellation = new CancellationTokenSource();
        Task<long> peakWorkingSetTask = MonitorWorkingSetAsync(process, workingSetStart, monitorCancellation.Token);
        var phaseStopwatch = Stopwatch.StartNew();
        WorkerResult[] workers = await Task.WhenAll(Enumerable.Range(0, concurrency)
            .Select(worker => RunWorkerAsync(pool, concurrency, worker, duration)));
        phaseStopwatch.Stop();
        monitorCancellation.Cancel();
        long peakWorkingSet = await peakWorkingSetTask;

        process.Refresh();
        SmbClientPoolStatistics statistics = pool.GetStatistics();
        long totalBytes = workers.Sum(worker => worker.BytesTransferred);
        long successfulOperations = workers.Sum(worker => worker.SuccessfulOperations);
        long errors = workers.Sum(worker => worker.Errors);
        List<double> writeLatencies = workers.SelectMany(worker => worker.WriteLatenciesMs).ToList();
        List<double> readLatencies = workers.SelectMany(worker => worker.ReadLatenciesMs).ToList();

        return new ScenarioResult
        {
            Name = $"smb-1mib-c{concurrency}",
            Concurrency = concurrency,
            DurationSeconds = phaseStopwatch.Elapsed.TotalSeconds,
            BytesTransferred = totalBytes,
            SuccessfulOperations = successfulOperations,
            Errors = errors,
            ThroughputMiBPerSecond = totalBytes / 1024d / 1024d / phaseStopwatch.Elapsed.TotalSeconds,
            OperationsPerSecond = successfulOperations / phaseStopwatch.Elapsed.TotalSeconds,
            WriteP50Milliseconds = Percentile(writeLatencies, 0.50),
            WriteP95Milliseconds = Percentile(writeLatencies, 0.95),
            WriteP99Milliseconds = Percentile(writeLatencies, 0.99),
            ReadP50Milliseconds = Percentile(readLatencies, 0.50),
            ReadP95Milliseconds = Percentile(readLatencies, 0.95),
            ReadP99Milliseconds = Percentile(readLatencies, 0.99),
            WorkingSetStartBytes = workingSetStart,
            WorkingSetEndBytes = process.WorkingSet64,
            PeakWorkingSetBytes = peakWorkingSet,
            AllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedStart,
            Gen0Collections = GC.CollectionCount(0) - gen0Start,
            Gen1Collections = GC.CollectionCount(1) - gen1Start,
            Gen2Collections = GC.CollectionCount(2) - gen2Start,
            PoolStatistics = statistics,
            ErrorSamples = workers.SelectMany(worker => worker.ErrorSamples).Take(10).ToList()
        };
    }

    private async Task WarmUpAsync(PooledSmbClient pool, int concurrency)
    {
        string path = $"storagec-warmup-{_runToken}-c{concurrency}.bin";
        await pool.WriteAsync(path, _payload, new SmbWriteOptions { Overwrite = true });
        SmbFile read = await pool.ReadAsync(path);
        if (!read.Content.AsSpan().SequenceEqual(_payload))
            throw new InvalidDataException("SMB warm-up data verification failed.");
    }

    private async Task<WorkerResult> RunWorkerAsync(PooledSmbClient pool, int concurrency, int worker,
        TimeSpan duration)
    {
        var result = new WorkerResult();
        string path = $"storagec-load-{_runToken}-c{concurrency}-w{worker}.bin";
        var phase = Stopwatch.StartNew();
        var operation = new Stopwatch();

        while (phase.Elapsed < duration)
        {
            try
            {
                operation.Restart();
                await pool.WriteAsync(path, _payload, new SmbWriteOptions { Overwrite = true });
                operation.Stop();
                result.WriteLatenciesMs.Add(operation.Elapsed.TotalMilliseconds);
                result.BytesTransferred += _payload.Length;
                result.SuccessfulOperations++;

                operation.Restart();
                SmbFile read = await pool.ReadAsync(path);
                operation.Stop();
                if (!read.Content.AsSpan().SequenceEqual(_payload))
                    throw new InvalidDataException($"Data verification failed for {path}.");
                result.ReadLatenciesMs.Add(operation.Elapsed.TotalMilliseconds);
                result.BytesTransferred += read.Content.Length;
                result.SuccessfulOperations++;
            }
            catch (Exception exception)
            {
                result.Errors++;
                if (result.ErrorSamples.Count < 3)
                    result.ErrorSamples.Add($"{exception.GetType().Name}: {exception.Message}");
                await Task.Delay(50);
            }
        }

        return result;
    }

    private SmbClientOptions CreateClientOptions() => new()
    {
        Server = options.Server,
        Port = options.Port,
        ShareName = options.Share,
        Username = options.Username,
        Password = options.Password,
        DialTimeout = TimeSpan.FromSeconds(10),
        MaxReadBytes = Math.Max(16L * 1024 * 1024, options.PayloadBytes * 2L),
        ChunkSizeBytes = 1024 * 1024,
        ClearBuffersOnReturn = true
    };

    private static async Task<long> MonitorWorkingSetAsync(Process process, long initial,
        CancellationToken cancellationToken)
    {
        long peak = initial;
        try
        {
            while (true)
            {
                process.Refresh();
                peak = Math.Max(peak, process.WorkingSet64);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            process.Refresh();
            return Math.Max(peak, process.WorkingSet64);
        }
    }

    private static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        int index = Math.Clamp((int)Math.Ceiling(values.Count * percentile) - 1, 0, values.Count - 1);
        return values[index];
    }

    private static byte[] CreatePayload(int size)
    {
        var payload = new byte[size];
        new Random(0x53544F52).NextBytes(payload);
        return payload;
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
            builder.Append(char.IsAsciiLetterOrDigit(character) ? character : '-');
        return builder.ToString();
    }
}

internal static class PerformanceComparison
{
    private const double ThroughputRegressionPercent = -25;
    private const double LatencyRegressionPercent = 50;

    public static void Apply(LoadTestReport report, LoadTestReport? baseline)
    {
        if (!string.IsNullOrWhiteSpace(report.FatalError))
            report.Violations.Add("The load test could not complete.");

        foreach (ScenarioResult scenario in report.Scenarios)
        {
            if (scenario.Errors > 0)
                report.Violations.Add($"{scenario.Name}: {scenario.Errors} SMB operation(s) failed.");
            if (scenario.PoolStatistics.ActiveLeases != 0 || scenario.PoolStatistics.PendingAcquires != 0)
                report.Violations.Add($"{scenario.Name}: the client pool did not return to an idle state.");
        }

        if (baseline is null)
        {
            report.Warnings.Add("No previous main-branch performance artifact was available; regression comparison was skipped.");
            return;
        }

        report.BaselineCommit = baseline.Commit;
        foreach (ScenarioResult current in report.Scenarios)
        {
            ScenarioResult? previous = baseline.Scenarios.FirstOrDefault(item => item.Name == current.Name);
            if (previous is null || previous.ThroughputMiBPerSecond <= 0)
            {
                report.Warnings.Add($"{current.Name}: no matching baseline scenario was found.");
                continue;
            }

            double throughputChange = ChangePercent(current.ThroughputMiBPerSecond, previous.ThroughputMiBPerSecond);
            double writeP95Change = ChangePercent(current.WriteP95Milliseconds, previous.WriteP95Milliseconds);
            double readP95Change = ChangePercent(current.ReadP95Milliseconds, previous.ReadP95Milliseconds);
            report.Comparisons.Add(new ScenarioComparison
            {
                Name = current.Name,
                ThroughputChangePercent = throughputChange,
                WriteP95ChangePercent = writeP95Change,
                ReadP95ChangePercent = readP95Change
            });

            if (throughputChange <= ThroughputRegressionPercent)
                report.Violations.Add($"{current.Name}: throughput regressed by {Math.Abs(throughputChange):F1}% (limit {Math.Abs(ThroughputRegressionPercent):F0}%).");
            if (writeP95Change >= LatencyRegressionPercent)
                report.Violations.Add($"{current.Name}: write p95 latency increased by {writeP95Change:F1}% (limit {LatencyRegressionPercent:F0}%).");
            if (readP95Change >= LatencyRegressionPercent)
                report.Violations.Add($"{current.Name}: read p95 latency increased by {readP95Change:F1}% (limit {LatencyRegressionPercent:F0}%).");
        }
    }

    private static double ChangePercent(double current, double previous) =>
        previous <= 0 ? 0 : (current - previous) / previous * 100;
}

internal static class MarkdownReport
{
    public static string Create(LoadTestReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("# StorageC SMB performance");
        text.AppendLine();
        text.AppendLine($"Commit: `{report.Commit}`  ");
        text.AppendLine($"Mode: `{report.EventName}` · Duration: `{report.RequestedDurationSeconds}s` · Enforced: `{report.Enforced}`  ");
        text.AppendLine($"Baseline: `{report.BaselineCommit ?? "not available"}`");
        text.AppendLine();

        if (report.Scenarios.Count > 0)
        {
            text.AppendLine("| Scenario | MiB/s | ops/s | write p95 | read p95 | errors | peak RSS | throughput Δ |");
            text.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
            foreach (ScenarioResult scenario in report.Scenarios)
            {
                ScenarioComparison? comparison = report.Comparisons.FirstOrDefault(item => item.Name == scenario.Name);
                string change = comparison is null ? "n/a" : $"{comparison.ThroughputChangePercent:+0.0;-0.0;0.0}%";
                text.AppendLine($"| {scenario.Name} | {scenario.ThroughputMiBPerSecond:F1} | {scenario.OperationsPerSecond:F1} | {scenario.WriteP95Milliseconds:F1} ms | {scenario.ReadP95Milliseconds:F1} ms | {scenario.Errors} | {scenario.PeakWorkingSetBytes / 1024d / 1024d:F1} MiB | {change} |");
            }
            text.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(report.FatalError))
        {
            text.AppendLine("## Fatal error");
            text.AppendLine();
            text.AppendLine("```");
            text.AppendLine(report.FatalError);
            text.AppendLine("```");
            text.AppendLine();
        }

        if (report.Violations.Count > 0)
        {
            text.AppendLine(report.Enforced ? "## Blocking findings" : "## Report-only findings");
            text.AppendLine();
            foreach (string violation in report.Violations) text.AppendLine($"- {violation}");
            text.AppendLine();
        }

        if (report.Warnings.Count > 0)
        {
            text.AppendLine("## Warnings");
            text.AppendLine();
            foreach (string warning in report.Warnings) text.AppendLine($"- {warning}");
        }

        return text.ToString();
    }
}

internal sealed class LoadTestOptions
{
    public required string Server { get; init; }
    public required int Port { get; init; }
    public required string Share { get; init; }
    public required string Username { get; init; }
    public required string Password { get; init; }
    public required int DurationSeconds { get; init; }
    public required int PayloadBytes { get; init; }
    public required string OutputDirectory { get; init; }
    public string? BaselinePath { get; init; }
    public required bool Enforce { get; init; }

    public static LoadTestOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                throw new ArgumentException($"Invalid argument: {args[index]}");
            values[args[index][2..]] = args[++index];
        }

        return new LoadTestOptions
        {
            Server = Get(values, "server", "SMB_HOST", "127.0.0.1"),
            Port = ParseInt(Get(values, "port", "SMB_PORT", "445"), "port", 1, 65535),
            Share = Get(values, "share", "SMB_SHARE", "bench"),
            Username = Get(values, "username", "SMB_USER", "bench"),
            Password = Get(values, "password", "SMB_PASSWORD", "benchpass"),
            DurationSeconds = ParseInt(Get(values, "duration-seconds", "STORAGEC_PERF_DURATION_SECONDS", "45"),
                "duration-seconds", 15, 7200),
            PayloadBytes = ParseInt(Get(values, "payload-bytes", "STORAGEC_PERF_PAYLOAD_BYTES", "1048576"),
                "payload-bytes", 4096, 16 * 1024 * 1024),
            OutputDirectory = Get(values, "output", "STORAGEC_PERF_OUTPUT", "artifacts/performance"),
            BaselinePath = GetOptional(values, "baseline", "STORAGEC_PERF_BASELINE"),
            Enforce = bool.TryParse(Get(values, "enforce", "STORAGEC_PERF_ENFORCE", "false"), out bool enforce) && enforce
        };
    }

    private static string Get(IReadOnlyDictionary<string, string> values, string argument, string environment,
        string fallback) => values.TryGetValue(argument, out string? value)
            ? value
            : Environment.GetEnvironmentVariable(environment) ?? fallback;

    private static string? GetOptional(IReadOnlyDictionary<string, string> values, string argument,
        string environment) => values.TryGetValue(argument, out string? value)
            ? value
            : Environment.GetEnvironmentVariable(environment);

    private static int ParseInt(string value, string name, int minimum, int maximum)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int result)
            || result < minimum || result > maximum)
            throw new ArgumentOutOfRangeException(name, $"Expected a value between {minimum} and {maximum}.");
        return result;
    }
}

internal sealed class WorkerResult
{
    public long BytesTransferred { get; set; }
    public long SuccessfulOperations { get; set; }
    public long Errors { get; set; }
    public List<double> WriteLatenciesMs { get; } = [];
    public List<double> ReadLatenciesMs { get; } = [];
    public List<string> ErrorSamples { get; } = [];
}

internal sealed class LoadTestReport
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
    public string Commit { get; set; } = "";
    public string Ref { get; set; } = "";
    public string EventName { get; set; } = "";
    public string RunnerOs { get; set; } = "";
    public string Framework { get; set; } = "";
    public int ProcessorCount { get; set; }
    public int PayloadBytes { get; set; }
    public int RequestedDurationSeconds { get; set; }
    public bool Enforced { get; set; }
    public string? BaselineCommit { get; set; }
    public string? FatalError { get; set; }
    public List<ScenarioResult> Scenarios { get; set; } = [];
    public List<ScenarioComparison> Comparisons { get; set; } = [];
    public List<string> Violations { get; set; } = [];
    public List<string> Warnings { get; set; } = [];

    [JsonIgnore]
    public bool HasBlockingIssues => !string.IsNullOrWhiteSpace(FatalError) || Violations.Count > 0;
}

internal sealed class ScenarioResult
{
    public string Name { get; set; } = "";
    public int Concurrency { get; set; }
    public double DurationSeconds { get; set; }
    public long BytesTransferred { get; set; }
    public long SuccessfulOperations { get; set; }
    public long Errors { get; set; }
    public double ThroughputMiBPerSecond { get; set; }
    public double OperationsPerSecond { get; set; }
    public double WriteP50Milliseconds { get; set; }
    public double WriteP95Milliseconds { get; set; }
    public double WriteP99Milliseconds { get; set; }
    public double ReadP50Milliseconds { get; set; }
    public double ReadP95Milliseconds { get; set; }
    public double ReadP99Milliseconds { get; set; }
    public long WorkingSetStartBytes { get; set; }
    public long WorkingSetEndBytes { get; set; }
    public long PeakWorkingSetBytes { get; set; }
    public long AllocatedBytes { get; set; }
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
    public SmbClientPoolStatistics PoolStatistics { get; set; } = new(0, 0, 0, 0, 0, 0, 0, 0);
    public List<string> ErrorSamples { get; set; } = [];
}

internal sealed class ScenarioComparison
{
    public string Name { get; set; } = "";
    public double ThroughputChangePercent { get; set; }
    public double WriteP95ChangePercent { get; set; }
    public double ReadP95ChangePercent { get; set; }
}
