using System.Collections.Concurrent;
using Agentic.Orchestrator;

namespace Agentic.Orchestrator.Tests;

public class OrchestratorTests
{
    [Fact]
    public async Task OC01_CyclicGraph_IsRejected()
    {
        var definition = Definition(
            Node("a", "b"),
            Node("b", "a"));
        var engine = CreateEngine(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), _ => StageResult.Ok("x"));
        await Assert.ThrowsAsync<CyclicGraphException>(() => engine.RunAsync(definition));
    }

    [Fact]
    public async Task OC02_UnmetEntryGate_IsBlocked()
    {
        var calls = 0;
        var definition = Definition(new StageNode
        {
            Id = "gated",
            Name = "gated",
            Agent = "gated",
            EntryGate = "context:Approved=true",
            Autonomy = "Auto"
        });
        var engine = CreateEngine(TempRoot(), _ =>
        {
            calls++;
            return StageResult.Ok("nope");
        });
        var state = await engine.RunAsync(definition);
        Assert.Equal(StageStatus.Blocked, state.Stages["gated"].Status);
        Assert.Contains("Entry gate", state.Stages["gated"].FailureReason);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task OC03_FailTwiceThenSucceed_RecordsTwoRetriesAndMttr()
    {
        var definition = Definition(Node("work"));
        definition.Stages[0].MaxRetries = 2;
        var root = TempRoot();
        var engine = CreateEngine(root, ctx =>
            ctx.Attempt < 3 ? StageResult.Fail("not yet") : StageResult.Ok("recovered"));
        var state = await engine.RunAsync(definition);
        Assert.Equal(StageStatus.Succeeded, state.Stages["work"].Status);
        Assert.Equal(2, state.Stages["work"].RetryCount);
        var metrics = RunMetricsCalculator.Calculate(state);
        Assert.NotNull(metrics.MttrMs);
        Assert.True(metrics.MttrMs >= 0);
        Assert.True(File.Exists(Path.Combine(root, "runs", state.RunId, "metrics.json")));
    }

    [Fact]
    public async Task OC04_RetryExhaustion_RollsBackAndRestoresFile()
    {
        var root = TempRoot();
        var runId = "oc04";
        var note = Path.Combine(root, "runs", runId, "workspace", "src", "note.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(note)!);
        await File.WriteAllTextAsync(note, "original");
        var stage = Node("impl");
        stage.MaxRetries = 1;
        stage.Rollback = true;
        stage.AllowedWritePaths = ["workspace/src"];
        var engine = CreateEngineWithId(root, runId, ctx =>
        {
            ctx.Workspace.Write("workspace/src/note.txt", "mutated");
            return StageResult.Fail("still broken");
        });
        var state = await engine.RunAsync(Definition(stage));
        Assert.Equal(StageStatus.RolledBack, state.Stages["impl"].Status);
        Assert.True(state.Stages["impl"].RetryCount >= 1);
        Assert.Equal("original", await File.ReadAllTextAsync(note));
    }

    [Fact]
    public async Task OC05_HumanRejectsDesign_DownstreamDoesNotRun()
    {
        var calls = new ConcurrentDictionary<string, int>();
        var design = Node("design");
        design.Autonomy = "HumanRequired";
        design.ApprovalTiming = "before";
        var implement = Node("implement", "design");
        var definition = Definition(design, implement);
        var options = Options(TempRoot());
        options.AutoApprove = false;
        var engine = new WorkflowEngine(
            Registry(ctx =>
            {
                calls.AddOrUpdate(ctx.Stage.Id, 1, (_, count) => count + 1);
                return StageResult.Ok(ctx.Stage.Id);
            }, "design", "implement"),
            new RuleBasedLlmClient(),
            new RejectStageApproval("design"),
            options);
        var state = await engine.RunAsync(definition);
        Assert.Equal(StageStatus.Rejected, state.Stages["design"].Status);
        Assert.Equal(WorkflowStatus.Rejected, state.Status);
        Assert.False(calls.ContainsKey("implement"));
        Assert.False(calls.ContainsKey("design"));
    }

    [Fact]
    public async Task OC06_WriteOutsideAllowedPath_IsBlockedByPolicy()
    {
        var stage = Node("impl");
        stage.AllowedWritePaths = ["workspace/src"];
        var root = TempRoot();
        var engine = CreateEngine(root, ctx =>
        {
            ctx.Workspace.Write("secrets/stolen.txt", "nope");
            return StageResult.Ok("should not land");
        });
        var state = await engine.RunAsync(Definition(stage));
        Assert.Equal(StageStatus.Blocked, state.Stages["impl"].Status);
        Assert.Contains("Policy denied", state.Stages["impl"].FailureReason);
        Assert.False(File.Exists(Path.Combine(root, "runs", state.RunId, "secrets", "stolen.txt")));
    }

    [Fact]
    public async Task OC07_DangerousGeneratedCode_FailsSecurityGate()
    {
        var stage = Node("impl");
        stage.AllowedWritePaths = ["workspace/src"];
        stage.MaxRetries = 2;
        var calls = 0;
        var engine = CreateEngine(TempRoot(), ctx =>
        {
            calls++;
            ctx.Workspace.Write("workspace/src/Bad.cs", "class Bad { void M() { System.Diagnostics.Process.Start(\"calc.exe\"); } }");
            return StageResult.Ok("wrote bad code");
        });
        var state = await engine.RunAsync(Definition(stage));
        Assert.Equal(StageStatus.Failed, state.Stages["impl"].Status);
        Assert.Contains("Security gate", state.Stages["impl"].FailureReason);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OC08_RequirementChange_RerunsOnlyDownstream()
    {
        var counts = new ConcurrentDictionary<string, int>();
        StageResult Run(StageContext ctx)
        {
            counts.AddOrUpdate(ctx.Stage.Id, 1, (_, count) => count + 1);
            return StageResult.Ok(ctx.Stage.Id + "-v1");
        }

        var definition = Definition(
            Node("A"),
            Node("B", "A"),
            Node("C", "B"),
            Node("D", "A"),
            Node("E"));
        var engine = CreateEngine(TempRoot(), Run, "A", "B", "C", "D", "E");
        var state = await engine.RunAsync(definition);
        var again = await engine.ReplanFromOutputChangeAsync(state, "A", "A-v2", "requirement changed");
        Assert.Equal(1, counts["A"]);
        Assert.Equal(1, counts["E"]);
        Assert.Equal(2, counts["B"]);
        Assert.Equal(2, counts["C"]);
        Assert.Equal(2, counts["D"]);
        Assert.Equal(StageStatus.Succeeded, again.Stages["E"].Status);
        Assert.Contains("B", again.InvalidatedStages);
        Assert.DoesNotContain("E", again.InvalidatedStages);
    }

    [Fact]
    public async Task OC09_StopFile_SafeStopsAndResumes()
    {
        var root = TempRoot();
        var runs = Path.Combine(root, "runs");
        Directory.CreateDirectory(runs);
        await File.WriteAllTextAsync(Path.Combine(runs, "STOP"), "stop");
        var counts = new ConcurrentDictionary<string, int>();
        var engine = CreateEngine(root, ctx =>
        {
            counts.AddOrUpdate(ctx.Stage.Id, 1, (_, count) => count + 1);
            return StageResult.Ok(ctx.Stage.Id);
        }, "A", "B");
        var stopped = await engine.RunAsync(Definition(Node("A"), Node("B", "A")));
        Assert.Equal(WorkflowStatus.SafeStopped, stopped.Status);
        Assert.False(counts.ContainsKey("A"));
        Assert.True(File.Exists(Path.Combine(runs, stopped.RunId, "state.json")));
        var resumed = await engine.ResumeAsync(stopped.RunId);
        Assert.Equal(WorkflowStatus.Completed, resumed.Status);
        Assert.Equal(1, counts["A"]);
        Assert.Equal(1, counts["B"]);
    }

    [Fact]
    public async Task OC10_TamperedAuditLog_FailsVerification()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "audit.jsonl");
        var log = AuditLog.Open(path);
        await log.AppendAsync("run", "WorkflowStarted", null, "orchestrator", "one");
        await log.AppendAsync("run", "NodeStarted", "build", "agent", "two");
        await log.AppendAsync("run", "NodeSucceeded", "build", "agent", "three");
        Assert.True(AuditLog.Verify(path).Valid);

        var lines = (await File.ReadAllLinesAsync(path)).ToList();
        lines[1] = lines[1].Replace("two", "tampered", StringComparison.Ordinal);
        await File.WriteAllLinesAsync(path, lines);
        var result = AuditLog.Verify(path);
        Assert.False(result.Valid);
        Assert.Contains("tampered", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OC11_LlmUnavailable_FallbackCompletes()
    {
        var root = TempRoot();
        var options = Options(root);
        var llm = new FallbackLlmClient(new ThrowingLlmClient(), new RuleBasedLlmClient());
        var engine = new WorkflowEngine(
            new StageExecutorRegistry([
                new ScriptExecutor("requirements", async (ctx, ct) =>
                {
                    var response = await ctx.Llm.CompleteAsync(new LlmRequest { Role = "RequirementsAgent", UserPrompt = "Build a URL shortener" }, ct);
                    return StageResult.Ok(response.Content + (response.UsedFallback ? " FALLBACK" : ""), response.UsedFallback);
                })
            ]),
            llm,
            new AutoApprovalService(),
            options);
        var state = await engine.RunAsync(Definition(Node("requirements")));
        Assert.Equal(WorkflowStatus.Completed, state.Status);
        Assert.Contains("FALLBACK", state.Stages["requirements"].Output);
    }

    [Fact]
    public async Task OC12_ParallelStagesOverlap_AndJoinWaits()
    {
        var times = new ConcurrentDictionary<string, (DateTime Start, DateTime End)>();
        IStageExecutor Delay(string name) => new ScriptExecutor(name, async (ctx, ct) =>
        {
            var start = DateTime.UtcNow;
            await Task.Delay(400, ct);
            times[ctx.Stage.Id] = (start, DateTime.UtcNow);
            return StageResult.Ok(ctx.Stage.Id);
        });
        var root = TempRoot();
        var engine = new WorkflowEngine(
            new StageExecutorRegistry([Delay("P1"), Delay("P2"), Delay("J")]),
            new RuleBasedLlmClient(),
            new AutoApprovalService(),
            Options(root));
        var state = await engine.RunAsync(Definition(Node("P1"), Node("P2"), Node("J", "P1", "P2")));
        Assert.Equal(WorkflowStatus.Completed, state.Status);
        Assert.True(times["P1"].Start < times["P2"].End);
        Assert.True(times["P2"].Start < times["P1"].End);
        Assert.True(times["J"].Start >= times["P1"].End);
        Assert.True(times["J"].Start >= times["P2"].End);
    }

    private static WorkflowEngine CreateEngine(string root, Func<StageContext, StageResult> execute, params string[] names) =>
        CreateEngineWithId(root, null, execute, names);

    private static WorkflowEngine CreateEngineWithId(string root, string? runId, Func<StageContext, StageResult> execute, params string[] names)
    {
        if (names.Length == 0)
            names = ["work", "gated", "impl", "requirements"];
        return new WorkflowEngine(Registry(execute, names), new RuleBasedLlmClient(), new AutoApprovalService(), Options(root, runId));
    }

    private static StageExecutorRegistry Registry(Func<StageContext, StageResult> execute, params string[] names) =>
        new(names.Distinct().Select(name => new ScriptExecutor(name, (ctx, _) => Task.FromResult(execute(ctx)))));

    private static OrchestratorOptions Options(string root, string? runId = null) => new()
    {
        RunsRoot = Path.Combine(root, "runs"),
        RepoRoot = root,
        AutoApprove = true,
        RetryBaseDelay = TimeSpan.Zero,
        RunId = runId
    };

    private static WorkflowDefinition Definition(params StageNode[] stages) => new()
    {
        Id = "test",
        Name = "test",
        Requirement = "test requirement",
        ScenarioType = "test",
        Stages = stages.ToList()
    };

    private static StageNode Node(string id, params string[] dependencies) => new()
    {
        Id = id,
        Name = id,
        Agent = id,
        DependsOn = dependencies.ToList(),
        Autonomy = "Auto",
        MaxRetries = 2,
        EntryGate = "always",
        AllowedWritePaths = ["workspace"]
    };

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), "agentic-tests", Guid.NewGuid().ToString("N"));

    private sealed class ScriptExecutor : IStageExecutor
    {
        private readonly Func<StageContext, CancellationToken, Task<StageResult>> _execute;
        public ScriptExecutor(string name, Func<StageContext, CancellationToken, Task<StageResult>> execute)
        {
            Name = name;
            _execute = execute;
        }

        public string Name { get; }
        public Task<StageResult> ExecuteAsync(StageContext context, CancellationToken cancellationToken) => _execute(context, cancellationToken);
    }

    private sealed class RejectStageApproval : IApprovalService
    {
        private readonly string _stageId;
        public RejectStageApproval(string stageId) => _stageId = stageId;
        public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ApprovalDecision(
                !request.StageId.Equals(_stageId, StringComparison.OrdinalIgnoreCase),
                "human",
                "rejected by test",
                TimeSpan.Zero));
    }

    private sealed class ThrowingLlmClient : ILlmClient
    {
        public string Name => "throwing";
        public bool IsConfigured => true;
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken) =>
            throw new LlmUnavailableException("LLM unavailable");
    }
}
