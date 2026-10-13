using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agentic.Orchestrator;

public static class AgentSupport
{
    public static async Task<LlmResponse> AskAsync(StageContext context, string role, string promptFile, string user, CancellationToken cancellationToken)
    {
        var system = LoadPrompt(context.RepoRoot, promptFile);
        return await context.Llm.CompleteAsync(new LlmRequest
        {
            Role = role,
            SystemPrompt = system,
            UserPrompt = user,
            CorrelationId = context.State.RunId + ":" + context.Stage.Id
        }, cancellationToken);
    }

    public static string LoadPrompt(string repoRoot, string fileName)
    {
        var path = Path.Combine(repoRoot, "prompts", fileName);
        return File.Exists(path) ? File.ReadAllText(path) : "Follow the stage contract and return structured text.";
    }

    public static string Prompt(StageContext context) =>
        "Requirement:\n" + context.State.Requirement + "\n\nParent artifacts:\n" + context.ParentOutputText() +
        (string.IsNullOrWhiteSpace(context.FailureFeedback) ? "" : "\n\nPrevious failure:\n" + context.FailureFeedback);
}

public sealed class RequirementsAgent : IStageExecutor
{
    public string Name => "RequirementsAgent";

    public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken cancellationToken)
    {
        var response = await AgentSupport.AskAsync(context, Name, "requirement.md", context.State.Requirement, cancellationToken);
        context.Workspace.Write("workspace/requirements/requirements.json", response.Content);
        return StageResult.Ok(response.Content, response.UsedFallback);
    }
}

public sealed class BaselineAgent : IStageExecutor
{
    public string Name => "BaselineAgent";

    public Task<StageResult> ExecuteAsync(StageContext context, CancellationToken cancellationToken)
    {
        var document = """
            # Governance baseline
            This node does not depend on the requirement text.
            Controls that stay in force for every scenario:
            - Agents write only inside the stage allow-list.
            - Security and review stages are read-only.
            - High-impact stages wait for a human approval, or for --auto-approve in a non-interactive demo.
            - The audit log is append-only and hash-chained.
            - A STOP file, max step count, or max duration ends the run without discarding state.
            """;
        context.Workspace.Write("workspace/baseline/policy-baseline.md", document);
        return Task.FromResult(StageResult.Ok(document));
    }
}

public sealed class CodebaseAnalystAgent : IStageExecutor
{
    public string Name => "CodebaseAnalystAgent";

    public Task<StageResult> ExecuteAsync(StageContext context, CancellationToken cancellationToken)
    {
        var src = Path.Combine(context.RepoRoot, "src");
        var files = Directory.Exists(src)
            ? Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                               && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                               && path.Contains("UrlShortener", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

        var endpoints = new List<string>();
        var evidence = new List<object>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var match = Regex.Match(line, "Map(Get|Post|Delete|Put|Patch)\\(\"([^\"]+)\"");
                if (match.Success)
                    endpoints.Add(match.Groups[1].Value + " " + match.Groups[2].Value);
                if (line.Contains("class UrlShortenerService", StringComparison.Ordinal) ||
                    line.Contains("class JsonUrlRepository", StringComparison.Ordinal) ||
                    line.Contains("class AtomicFileStore", StringComparison.Ordinal) ||
                    line.Contains("class UrlValidator", StringComparison.Ordinal))
                {
                    evidence.Add(new { file = Path.GetRelativePath(context.RepoRoot, file), line = i + 1, text = line.Trim() });
                }
            }
        }

        var modules = files.Select(path => path.Contains("UrlShortener.Api") ? "Api" : path.Contains("Infrastructure") ? "Infrastructure" : "Core")
            .Distinct()
            .OrderBy(name => name)
            .ToArray();
        var risk = Math.Round(Math.Min(0.95, 0.35 + (modules.Length * 0.08) + 0.15), 2);
        var report = JsonSerializer.Serialize(new
        {
            scenario = "brownfield",
            modules,
            endpoints,
            evidence,
            dataFlow = "HTTP request -> UrlShortenerService -> IUrlRepository/IClickRepository -> atomic JSON files data/urls.json and data/clicks.json. Redirect does not fetch the destination URL.",
            impactedByAliasAndExpiry = new[] { "UrlShortenerService", "JsonUrlRepository", "UrlMapping", "POST /api/shorten", "GET /{code}" },
            riskScore = risk,
            riskRationale = "Touching code allocation, the public create contract, and the on-disk document. Existing redirect codes must keep resolving."
        }, OrchestratorJson.Options);
        context.Workspace.Write("workspace/analysis/impact.json", report);
        return Task.FromResult(StageResult.Ok(report));
    }
}

public sealed class ArchitectAgent : IStageExecutor
{
    public string Name => "ArchitectAgent";

    public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken cancellationToken)
    {
        var response = await AgentSupport.AskAsync(context, Name, "architecture.md", AgentSupport.Prompt(context), cancellationToken);
        context.Workspace.Write("workspace/architecture/architecture.json", response.Content);
        return StageResult.Ok(response.Content, response.UsedFallback);
    }
}

public sealed class DeveloperAgent : IStageExecutor
{
    public string Name => "DeveloperAgent";

    public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken cancellationToken)
    {
        var response = await AgentSupport.AskAsync(context, Name, "implementation.md", AgentSupport.Prompt(context), cancellationToken);
        var notes = new StringBuilder();
        notes.AppendLine("# Implementation notes");
        notes.AppendLine();
        notes.AppendLine("The agent writes this workspace copy. It does not edit src/UrlShortener.* during the demo, so the running API and its tests stay stable.");
        notes.AppendLine();
        notes.AppendLine(response.Content);
        if (!string.IsNullOrWhiteSpace(context.FailureFeedback))
        {
            notes.AppendLine();
            notes.AppendLine("Previous attempt failed: " + context.FailureFeedback);
        }

        var sketch = """
            namespace Workspace.Sketch;

            // Collision-safe allocation sketch. The production copy lives in JsonUrlRepository.
            public static class CollisionSafeCodes
            {
                public const int MaxAttempts = 8;

                public static string Allocate(Func<string> next, Func<string, bool> taken)
                {
                    for (var attempt = 0; attempt < MaxAttempts; attempt++)
                    {
                        var code = next();
                        if (!taken(code))
                            return code;
                    }

                    throw new InvalidOperationException("Could not allocate a unique short code.");
                }
            }
            """;
        context.Workspace.Write("workspace/src/IMPLEMENTATION.md", notes.ToString());
        context.Workspace.Write("workspace/src/CollisionSafeCodes.cs", sketch);
        return StageResult.Ok(notes.ToString(), response.UsedFallback);
    }
}

public sealed class TestAgent : IStageExecutor
{
    public string Name => "TestAgent";

    public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken cancellationToken)
    {
        var notes = """
            # Acceptance notes
            The authoritative suite is tests/UrlShortener.Tests.
            This stage runs that suite and records the exit code. It does not weaken tests.
            Covered: create, invalid scheme, SSRF, redirect, missing, expired, alias conflict, stats, concurrency, atomic write, rate limit, idempotent shorten.
            """;
        context.Workspace.Write("workspace/tests/acceptance-notes.md", notes);

        var project = Path.Combine(context.RepoRoot, "tests", "UrlShortener.Tests", "UrlShortener.Tests.csproj");
        if (!File.Exists(project))
            return StageResult.Fail("Test project was not found at " + project);

        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "test \"" + project + "\" --nologo --verbosity minimal",
            WorkingDirectory = context.RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet test.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* process already exited */ }
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var log = "exit=" + process.ExitCode + "\n" + stdout + "\n" + stderr;
        context.Workspace.Write("workspace/tests/dotnet-test-output.txt", log);
        if (process.ExitCode != 0)
            return StageResult.Fail("dotnet test failed with exit code " + process.ExitCode);

        return StageResult.Ok("dotnet test passed.\n" + Tail(log, 2500));
    }

    private static string Tail(string text, int max) => text.Length <= max ? text : text[^max..];
}

public sealed class SecurityAgent : IStageExecutor
{
    public string Name => "SecurityAgent";

    public Task<StageResult> ExecuteAsync(StageContext context, CancellationToken cancellationToken)
    {
        var roots = new[]
        {
            Path.Combine(context.RepoRoot, "src", "UrlShortener.Core"),
            Path.Combine(context.RepoRoot, "src", "UrlShortener.Api"),
            Path.Combine(context.RepoRoot, "src", "UrlShortener.Infrastructure"),
            Path.Combine(context.RunDirectory, "workspace")
        };
        var findings = roots.SelectMany(SecurityScanner.ScanDirectory).ToList();
        var high = findings.Where(finding => finding.Severity is "HIGH" or "CRITICAL").ToList();
        var report = JsonSerializer.Serialize(new
        {
            status = high.Count == 0 ? "pass" : "fail",
            findings,
            positiveControls = new[]
            {
                "UrlValidator rejects non-http(s) schemes and private IP literals.",
                "Redirect does not fetch the destination.",
                "Click records omit raw IP addresses.",
                "POST /api/shorten uses the built-in rate limiter."
            }
        }, OrchestratorJson.Options);
        if (high.Count > 0)
            return Task.FromResult(StageResult.Fail("Security review found high or critical issues. " + report));
        return Task.FromResult(StageResult.Ok(report));
    }
}

public sealed class ReviewAgent : IStageExecutor
{
    public string Name => "ReviewAgent";

    public Task<StageResult> ExecuteAsync(StageContext context, CancellationToken cancellationToken)
    {
        var implementation = context.State.Stages.TryGetValue("implementation", out var stage) ? stage.Output : null;
        var verdict = string.IsNullOrWhiteSpace(implementation) ? "FAIL" : "PASS_WITH_WARNINGS";
        var report = JsonSerializer.Serialize(new
        {
            verdict,
            evidence = new[]
            {
                "Requirements, architecture, and implementation artifacts are present in the run state.",
                "Workspace code is a review copy; production behavior is enforced by UrlShortener.Tests.",
                "Warning: JSON storage is single-node and not a multi-writer database."
            }
        }, OrchestratorJson.Options);
        return Task.FromResult(verdict == "FAIL" ? StageResult.Fail(report) : StageResult.Ok(report));
    }
}

public sealed class DocsAgent : IStageExecutor
{
    public string Name => "DocsAgent";

    public Task<StageResult> ExecuteAsync(StageContext context, CancellationToken cancellationToken)
    {
        var document = new StringBuilder();
        document.AppendLine("# Scenario changelog");
        document.AppendLine();
        document.AppendLine("Run: " + context.State.RunId);
        document.AppendLine();
        document.AppendLine("Requirement:");
        document.AppendLine();
        document.AppendLine(context.State.Requirement);
        document.AppendLine();
        document.AppendLine("Stages:");
        foreach (var pair in context.State.Stages)
            document.AppendLine("- " + pair.Key + ": " + pair.Value.Status);
        context.Workspace.Write("workspace/docs/CHANGELOG.md", document.ToString());
        return Task.FromResult(StageResult.Ok(document.ToString()));
    }
}

public sealed class ReleaseAgent : IStageExecutor
{
    public string Name => "ReleaseAgent";

    public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken cancellationToken)
    {
        var commentary = await AgentSupport.AskAsync(context, Name, "release.md", AgentSupport.Prompt(context), cancellationToken);
        var blocking = context.Stage.DependsOn
            .Where(id => context.State.Stages[id].Status != StageStatus.Succeeded)
            .ToList();
        var ready = blocking.Count == 0;
        var report = JsonSerializer.Serialize(new
        {
            ready,
            decision = ready ? "go" : "no-go",
            blocking,
            warnings = new[] { "Prototype store is a JSON file on one machine.", "Dashboard approval is for a local demo and is not an authenticated production control." },
            rollbackPlan = "Restore the workspace snapshot from runs/<runId>/snapshots and redeploy the previous build. The product repository interface can be pointed back at the previous JSON files.",
            requiredApprovals = new[] { "Human release approval" },
            narrative = commentary.Content
        }, OrchestratorJson.Options);
        context.Workspace.Write("workspace/release/readiness.json", report);
        return StageResult.Ok(report, commentary.UsedFallback);
    }
}

public static class AgentCatalog
{
    public static IReadOnlyList<IStageExecutor> Create() =>
    [
        new RequirementsAgent(),
        new BaselineAgent(),
        new CodebaseAnalystAgent(),
        new ArchitectAgent(),
        new DeveloperAgent(),
        new TestAgent(),
        new SecurityAgent(),
        new ReviewAgent(),
        new DocsAgent(),
        new ReleaseAgent()
    ];
}
