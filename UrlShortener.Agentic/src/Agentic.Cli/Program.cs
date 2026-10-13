using System.Text.Json;
using Agentic.Orchestrator;
using Spectre.Console;

var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory());
if (args.Length == 0)
{
    PrintHelp();
    return 1;
}

var command = args[0].ToLowerInvariant();
var autoApprove = args.Any(arg => arg.Equals("--auto-approve", StringComparison.OrdinalIgnoreCase));
var inject = ReadOption(args, "--inject-failure");
var runsRoot = Path.Combine(repoRoot, "runs");
Directory.CreateDirectory(runsRoot);

try
{
    return command switch
    {
        "run" => await RunAsync(args, repoRoot, runsRoot, autoApprove, inject),
        "resume" => await ResumeAsync(args, repoRoot, runsRoot, autoApprove),
        "audit" => VerifyAudit(args, runsRoot),
        "metrics" => ShowMetrics(args, runsRoot),
        "stop" => Stop(args, runsRoot),
        _ => Unknown()
    };
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine("[red]Error:[/] " + Markup.Escape(ex.Message));
    return 1;
}

static async Task<int> RunAsync(string[] args, string repoRoot, string runsRoot, bool autoApprove, string? inject)
{
    if (args.Length < 2)
    {
        AnsiConsole.MarkupLine("[red]Usage:[/] run <scenario.json> [--auto-approve] [--inject-failure <stage>]");
        return 1;
    }

    var scenarioPath = ResolveScenario(args[1], repoRoot);
    var definition = JsonSerializer.Deserialize<WorkflowDefinition>(await File.ReadAllTextAsync(scenarioPath), OrchestratorJson.Options)
        ?? throw new InvalidOperationException("Scenario file is empty.");
    var llm = LlmClientFactory.CreateFromEnvironment();
    AnsiConsole.MarkupLine(llm.PrimaryConfigured
        ? "[green]LLM:[/] provider configured; deterministic fallback remains available."
        : "[yellow]LLM:[/] no API key; using the deterministic fallback.");
    var engine = BuildEngine(repoRoot, runsRoot, autoApprove, inject, llm);
    AnsiConsole.MarkupLine($"[bold]Run[/] {Markup.Escape(definition.Name)}");
    var state = await engine.RunAsync(definition);
    return Report(state, inject is not null);
}

static async Task<int> ResumeAsync(string[] args, string repoRoot, string runsRoot, bool autoApprove)
{
    if (args.Length < 2)
    {
        AnsiConsole.MarkupLine("[red]Usage:[/] resume <runId> [--auto-approve]");
        return 1;
    }

    var llm = LlmClientFactory.CreateFromEnvironment();
    var engine = BuildEngine(repoRoot, runsRoot, autoApprove, null, llm);
    var state = await engine.ResumeAsync(args[1]);
    return Report(state, demonstration: false);
}

static int VerifyAudit(string[] args, string runsRoot)
{
    if (args.Length < 3 || !args[1].Equals("verify", StringComparison.OrdinalIgnoreCase))
    {
        AnsiConsole.MarkupLine("[red]Usage:[/] audit verify <runId>");
        return 1;
    }

    var path = Path.Combine(runsRoot, args[2], "audit.jsonl");
    var result = AuditLog.Verify(path);
    AnsiConsole.MarkupLine(result.Valid ? "[green]AUDIT OK[/] " + Markup.Escape(result.Message) : "[red]AUDIT FAIL[/] " + Markup.Escape(result.Message));
    return result.Valid ? 0 : 1;
}

static int ShowMetrics(string[] args, string runsRoot)
{
    if (args.Length < 2)
    {
        AnsiConsole.MarkupLine("[red]Usage:[/] metrics <runId>");
        return 1;
    }

    var path = Path.Combine(runsRoot, args[1], "metrics.json");
    if (!File.Exists(path))
    {
        AnsiConsole.MarkupLine("[red]Metrics not found.[/]");
        return 1;
    }

    var metrics = JsonSerializer.Deserialize<RunMetrics>(File.ReadAllText(path), OrchestratorJson.Options)!;
    var table = new Table().Border(TableBorder.Rounded);
    table.AddColumn("Metric");
    table.AddColumn("Value");
    table.AddRow("Status", metrics.Status);
    table.AddRow("Success rate", $"{metrics.SuccessRate:P0}");
    table.AddRow("Retries", metrics.RetryCount.ToString());
    table.AddRow("Retry rate", $"{metrics.RetryRate:P0}");
    table.AddRow("Rollbacks", metrics.RollbackCount.ToString());
    table.AddRow("Rollback frequency", $"{metrics.RollbackFrequency:P0}");
    table.AddRow("MTTR ms", metrics.MttrMs?.ToString("0.###") ?? "n/a");
    table.AddRow("End-to-end ms", metrics.EndToEndLatencyMs.ToString("0"));
    table.AddRow("Approval wait ms", metrics.ApprovalWaitMs.ToString("0"));
    table.AddRow("Replans", metrics.ReplanCount.ToString());
    AnsiConsole.Write(table);
    AnsiConsole.MarkupLine("[grey]MTTR:[/] " + Markup.Escape(metrics.MttrDefinition));
    return 0;
}

static int Stop(string[] args, string runsRoot)
{
    Directory.CreateDirectory(runsRoot);
    var path = args.Length >= 2
        ? Path.Combine(runsRoot, args[1], "STOP")
        : Path.Combine(runsRoot, "STOP");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, "stop");
    AnsiConsole.MarkupLine("[yellow]STOP written[/] " + Markup.Escape(path));
    return 0;
}

static WorkflowEngine BuildEngine(string repoRoot, string runsRoot, bool autoApprove, string? inject, FallbackLlmClient llm)
{
    var options = new OrchestratorOptions
    {
        RunsRoot = runsRoot,
        RepoRoot = repoRoot,
        AutoApprove = autoApprove,
        InjectFailureStage = inject,
        RetryBaseDelay = TimeSpan.FromMilliseconds(100)
    };
    var approvals = new CliApprovalService(autoApprove);
    return new WorkflowEngine(new StageExecutorRegistry(AgentCatalog.Create()), llm, approvals, options, new SpectreObserver());
}

static int Report(RunState state, bool demonstration)
{
    AnsiConsole.MarkupLine($"[bold]Status:[/] {Markup.Escape(state.Status.ToString())}  [grey]{Markup.Escape(state.RunId)}[/]");
    var table = new Table().Border(TableBorder.Simple);
    table.AddColumn("Stage");
    table.AddColumn("Status");
    table.AddColumn("Retries");
    table.AddColumn("Runs");
    foreach (var stage in state.Definition.Stages)
    {
        var execution = state.Stages[stage.Id];
        table.AddRow(stage.Id, execution.Status.ToString(), execution.RetryCount.ToString(), execution.ExecutionCount.ToString());
    }

    AnsiConsole.Write(table);
    if (!string.IsNullOrWhiteSpace(state.StopReason))
        AnsiConsole.MarkupLine("[yellow]" + Markup.Escape(state.StopReason) + "[/]");
    var metricsPath = Path.Combine("runs", state.RunId, "metrics.json");
    AnsiConsole.MarkupLine("Audit: [grey]runs/" + Markup.Escape(state.RunId) + "/audit.jsonl[/]");
    AnsiConsole.MarkupLine("Summary: [grey]runs/" + Markup.Escape(state.RunId) + "/summary.md[/]");
    _ = metricsPath;

    if (state.Status == WorkflowStatus.Completed)
        return 0;
    if (demonstration && state.Status == WorkflowStatus.RolledBack)
    {
        AnsiConsole.MarkupLine("[green]Demonstration complete:[/] retries exhausted and rollback restored the workspace.");
        return 0;
    }

    return state.Status == WorkflowStatus.SafeStopped ? 2 : 1;
}

static string ResolveScenario(string path, string repoRoot)
{
    if (File.Exists(path))
        return Path.GetFullPath(path);
    var fromRepo = Path.Combine(repoRoot, path);
    if (File.Exists(fromRepo))
        return fromRepo;
    throw new FileNotFoundException("Scenario not found.", path);
}

static string? ReadOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }

    return null;
}

static int Unknown()
{
    PrintHelp();
    return 1;
}

static void PrintHelp()
{
    AnsiConsole.MarkupLine("""
        [bold]UrlShortener agentic CLI[/]
        run <scenario.json> [--auto-approve] [--inject-failure <stage>]
        resume <runId> [--auto-approve]
        audit verify <runId>
        metrics <runId>
        stop [runId]
        """);
}

static string FindRepoRoot(string start)
{
    var dir = new DirectoryInfo(start);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "UrlShortener.Agentic.slnx")) ||
            File.Exists(Path.Combine(dir.FullName, "UrlShortener.Agentic.sln")))
            return dir.FullName;
        dir = dir.Parent;
    }

    return start;
}

internal sealed class SpectreObserver : IRunObserver
{
    public void OnUpdate(string stageId, string status, string message)
    {
        var color = status switch
        {
            "Succeeded" or "Approved" => "green",
            "Failed" or "Rejected" or "RolledBack" => "red",
            "Retry" or "WaitingForApproval" or "SafeStopped" => "yellow",
            _ => "grey"
        };
        var text = stageId + " " + status + " " + message.ReplaceLineEndings(" ");
        if (text.Length > 180)
            text = text[..180];
        AnsiConsole.MarkupLine($"[{color}]{Markup.Escape(text)}[/]");
    }
}

internal sealed class CliApprovalService : IApprovalService
{
    private readonly bool _autoApprove;

    public CliApprovalService(bool autoApprove) => _autoApprove = autoApprove;

    public async Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        if (_autoApprove)
            return new ApprovalDecision(true, "human", "Auto-approved by --auto-approve", TimeSpan.Zero);

        AnsiConsole.MarkupLine($"[yellow]Approval required[/] for [bold]{Markup.Escape(request.StageName)}[/] ({Markup.Escape(request.Risk)}).");
        AnsiConsole.MarkupLine(Markup.Escape(request.Summary));
        AnsiConsole.MarkupLine("Type [green]approve[/] or [red]reject[/], or approve from the dashboard.");
        var requestPath = Path.Combine(request.RunDirectory, "approval-request.json");
        var decisionPath = Path.Combine(request.RunDirectory, "approval-decision.json");
        if (File.Exists(decisionPath))
            File.Delete(decisionPath);
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new
        {
            request.RequestId,
            request.RunId,
            request.StageId,
            request.StageName,
            request.Risk,
            request.Summary,
            requestedAtUtc = DateTime.UtcNow.ToString("O")
        }, OrchestratorJson.Options), cancellationToken);

        var started = DateTime.UtcNow;
        var deadline = started + request.Timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(decisionPath))
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(decisionPath, cancellationToken));
                var id = document.RootElement.TryGetProperty("requestId", out var idElement) ? idElement.GetString() : "";
                if (string.Equals(id, request.RequestId, StringComparison.Ordinal))
                {
                    var decision = document.RootElement.TryGetProperty("decision", out var decisionElement) ? decisionElement.GetString() : "";
                    var comment = document.RootElement.TryGetProperty("comment", out var commentElement) ? commentElement.GetString() : "";
                    return new ApprovalDecision(
                        string.Equals(decision, "approve", StringComparison.OrdinalIgnoreCase),
                        "human",
                        comment ?? "dashboard",
                        DateTime.UtcNow - started);
                }
            }

            if (Console.KeyAvailable)
            {
                var line = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (line is "approve" or "reject")
                    return new ApprovalDecision(line == "approve", "human", "console", DateTime.UtcNow - started);
            }

            await Task.Delay(200, cancellationToken);
        }

        throw new ApprovalTimeoutException($"Approval timed out for stage '{request.StageId}'.");
    }
}
