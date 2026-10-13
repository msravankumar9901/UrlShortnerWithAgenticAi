using System.Text.Json.Serialization;

namespace Agentic.Orchestrator;

public enum StageStatus
{
    Pending,
    Ready,
    Running,
    WaitingForApproval,
    Succeeded,
    Failed,
    RolledBack,
    Blocked,
    Rejected,
    SafeStopped,
    Stale
}

public enum WorkflowStatus
{
    Running,
    Completed,
    Failed,
    Rejected,
    SafeStopped,
    RolledBack
}

public enum FailureCategory
{
    Transient,
    Implementation,
    Dependency,
    Validation,
    Security,
    Governance,
    Unknown
}

public sealed class WorkflowDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Requirement { get; set; } = "";
    public string ScenarioType { get; set; } = "";
    public List<StageNode> Stages { get; set; } = [];
    public ReplanSpec? Replan { get; set; }
    public Dictionary<string, string> InitialContext { get; set; } = new();
}

public sealed class StageNode
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Agent { get; set; } = "";
    public List<string> DependsOn { get; set; } = [];
    public string Autonomy { get; set; } = "Auto";
    public string ApprovalTiming { get; set; } = "before";
    public int MaxRetries { get; set; } = 2;
    public string EntryGate { get; set; } = "always";
    public List<string> AllowedWritePaths { get; set; } = [];
    public bool Rollback { get; set; }
    public string Risk { get; set; } = "low";
    public bool ReadOnly { get; set; }
    public int LargeDiffLineThreshold { get; set; }
}

public sealed class ReplanSpec
{
    public string AfterStage { get; set; } = "";
    public string ChangedStage { get; set; } = "";
    public string NewOutput { get; set; } = "";
    public string Reason { get; set; } = "Upstream artifact changed.";
}

public sealed class RunState
{
    public string RunId { get; set; } = "";
    public string WorkflowId { get; set; } = "";
    public string WorkflowName { get; set; } = "";
    public string Requirement { get; set; } = "";
    public string ScenarioType { get; set; } = "";
    public WorkflowStatus Status { get; set; } = WorkflowStatus.Running;
    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public int StepCount { get; set; }
    public int TotalRetries { get; set; }
    public int TotalAttempts { get; set; }
    public int RollbackCount { get; set; }
    public int ReplanCount { get; set; }
    public string? StopReason { get; set; }
    public bool ReplanApplied { get; set; }
    public WorkflowDefinition Definition { get; set; } = new();
    public Dictionary<string, StageExecutionState> Stages { get; set; } = new();
    public Dictionary<string, string> Context { get; set; } = new();
    public List<DecisionRecord> Decisions { get; set; } = [];
    public List<string> InvalidatedStages { get; set; } = [];
    public Dictionary<string, int> FailureCategories { get; set; } = new();
}

public sealed class StageExecutionState
{
    public string StageId { get; set; } = "";
    public StageStatus Status { get; set; } = StageStatus.Pending;
    public int Attempt { get; set; }
    public int RetryCount { get; set; }
    public int ExecutionCount { get; set; }
    public string? FailureReason { get; set; }
    public string? Output { get; set; }
    public string? OutputHash { get; set; }
    public string? InputHash { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? FirstFailureAtUtc { get; set; }
    public double? RecoveryMs { get; set; }
    public double LatencyMs { get; set; }
    public double ApprovalWaitMs { get; set; }
}

public sealed class DecisionRecord
{
    public string Id { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Actor { get; set; } = "";
    public string What { get; set; } = "";
    public string Why { get; set; } = "";
    public string InputsHash { get; set; } = "";
    public string? ParentDecisionId { get; set; }
    public DateTime TimestampUtc { get; set; }
}

public sealed class RunMetrics
{
    public string RunId { get; set; } = "";
    public string Status { get; set; } = "";
    public int TotalStages { get; set; }
    public int SucceededStages { get; set; }
    public double SuccessRate { get; set; }
    public int RetryCount { get; set; }
    public int AttemptCount { get; set; }
    public double RetryRate { get; set; }
    public int RollbackCount { get; set; }
    public double RollbackFrequency { get; set; }
    public double? MttrMs { get; set; }
    public string MttrDefinition { get; set; } =
        "Mean elapsed time from a stage's first failed attempt to its later success, averaged across recovered stages. Null when no stage recovered.";
    public double EndToEndLatencyMs { get; set; }
    public double ApprovalWaitMs { get; set; }
    public Dictionary<string, double> StageLatencyMs { get; set; } = new();
    public Dictionary<string, int> FailureCategories { get; set; } = new();
    public int ReplanCount { get; set; }
}

public sealed class OrchestratorOptions
{
    public string RunsRoot { get; set; } = "runs";
    public string RepoRoot { get; set; } = ".";
    public bool AutoApprove { get; set; }
    public string? InjectFailureStage { get; set; }
    public string? RunId { get; set; }
    public string? StopFilePath { get; set; }
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(50);
    public int MaxSteps { get; set; } = 500;
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromMinutes(30);
}

public sealed class StageResult
{
    public bool Success { get; init; }
    public string Output { get; init; } = "";
    public string? FailureReason { get; init; }
    public bool UsedFallback { get; init; }
    public Dictionary<string, string>? Context { get; init; }

    public static StageResult Ok(string output, bool usedFallback = false) =>
        new() { Success = true, Output = output, UsedFallback = usedFallback };

    public static StageResult Fail(string reason) =>
        new() { Success = false, FailureReason = reason };
}

public sealed class StageContext
{
    public required WorkflowDefinition Definition { get; init; }
    public required StageNode Stage { get; init; }
    public required RunState State { get; init; }
    public required Workspace Workspace { get; init; }
    public required ILlmClient Llm { get; init; }
    public required string RepoRoot { get; init; }
    public required string RunDirectory { get; init; }
    public int Attempt { get; set; } = 1;
    public string FailureFeedback { get; set; } = "";

    public string ParentOutputText()
    {
        var builder = new System.Text.StringBuilder();
        foreach (var dep in Stage.DependsOn)
        {
            builder.AppendLine("## " + dep);
            builder.AppendLine(State.Stages.TryGetValue(dep, out var parent) ? parent.Output : "");
        }

        return builder.ToString();
    }
}

public interface IStageExecutor
{
    string Name { get; }
    Task<StageResult> ExecuteAsync(StageContext context, CancellationToken cancellationToken);
}

public interface IRunObserver
{
    void OnUpdate(string stageId, string status, string message);
}

public sealed class StageExecutorRegistry
{
    private readonly Dictionary<string, IStageExecutor> _map;

    public StageExecutorRegistry(IEnumerable<IStageExecutor> executors)
    {
        _map = new Dictionary<string, IStageExecutor>(StringComparer.OrdinalIgnoreCase);
        foreach (var executor in executors)
            _map[executor.Name] = executor;
    }

    public IStageExecutor Resolve(StageNode stage)
    {
        if (_map.TryGetValue(stage.Id, out var byId))
            return byId;
        if (!string.IsNullOrWhiteSpace(stage.Agent) && _map.TryGetValue(stage.Agent, out var byAgent))
            return byAgent;
        throw new InvalidOperationException($"No executor registered for stage '{stage.Id}' ({stage.Agent}).");
    }
}

public sealed class ApprovalRequest
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public string RunId { get; set; } = "";
    public string RunDirectory { get; set; } = "";
    public string StageId { get; set; } = "";
    public string StageName { get; set; } = "";
    public string Risk { get; set; } = "";
    public string Summary { get; set; } = "";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(15);
}

public sealed record ApprovalDecision(bool Approved, string Actor, string Comment, TimeSpan Wait);

public interface IApprovalService
{
    Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

public sealed class AutoApprovalService : IApprovalService
{
    public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new ApprovalDecision(true, "human", "approved", TimeSpan.Zero));
}

public class WorkflowValidationException : Exception
{
    public WorkflowValidationException(string message) : base(message) { }
}

public sealed class CyclicGraphException : WorkflowValidationException
{
    public CyclicGraphException(string message) : base(message) { }
}

public sealed class PolicyViolationException : Exception
{
    public PolicyViolationException(string message) : base(message) { }
}

public sealed class SecurityGateException : Exception
{
    public SecurityGateException(string message) : base(message) { }
}

public sealed class InjectedFailureException : Exception
{
    public InjectedFailureException(string message) : base(message) { }
}

public sealed class StageFailedException : Exception
{
    public StageFailedException(string message) : base(message) { }
}

public sealed class UnknownFailureException : Exception
{
    public UnknownFailureException(string message) : base(message) { }
}

public sealed class LlmUnavailableException : Exception
{
    public LlmUnavailableException(string message) : base(message) { }
}

public sealed class ApprovalTimeoutException : Exception
{
    public ApprovalTimeoutException(string message) : base(message) { }
}

public static class RunMetricsCalculator
{
    public static RunMetrics Calculate(RunState state)
    {
        var stages = state.Stages.Values.ToList();
        var succeeded = stages.Count(s => s.Status == StageStatus.Succeeded);
        var recoveries = stages.Where(s => s.RecoveryMs is not null).Select(s => s.RecoveryMs!.Value).ToList();
        var end = state.CompletedAtUtc ?? DateTime.UtcNow;
        var attempts = Math.Max(1, state.TotalAttempts);
        return new RunMetrics
        {
            RunId = state.RunId,
            Status = state.Status.ToString(),
            TotalStages = stages.Count,
            SucceededStages = succeeded,
            SuccessRate = stages.Count == 0 ? 0 : (double)succeeded / stages.Count,
            RetryCount = state.TotalRetries,
            AttemptCount = state.TotalAttempts,
            RetryRate = (double)state.TotalRetries / attempts,
            RollbackCount = state.RollbackCount,
            RollbackFrequency = stages.Count == 0 ? 0 : (double)state.RollbackCount / stages.Count,
            MttrMs = recoveries.Count == 0 ? null : recoveries.Average(),
            EndToEndLatencyMs = Math.Max(0, (end - state.StartedAtUtc).TotalMilliseconds),
            ApprovalWaitMs = stages.Sum(s => s.ApprovalWaitMs),
            StageLatencyMs = stages.ToDictionary(s => s.StageId, s => s.LatencyMs),
            FailureCategories = new Dictionary<string, int>(state.FailureCategories),
            ReplanCount = state.ReplanCount
        };
    }
}
