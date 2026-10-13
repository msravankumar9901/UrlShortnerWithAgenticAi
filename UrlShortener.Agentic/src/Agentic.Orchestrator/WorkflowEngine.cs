using System.Text;
using System.Text.Json;

namespace Agentic.Orchestrator;

public sealed class WorkflowEngine
{
    private readonly StageExecutorRegistry _executors;
    private readonly ILlmClient _llm;
    private readonly IApprovalService _approvals;
    private readonly OrchestratorOptions _options;
    private readonly IRunObserver? _observer;
    private readonly object _gate = new();
    private int _halt;

    private RunState _state = new();
    private string _runDirectory = "";
    private AuditLog? _audit;
    private IReadOnlyList<StageNode> _order = [];

    public WorkflowEngine(
        StageExecutorRegistry executors,
        ILlmClient llm,
        IApprovalService approvals,
        OrchestratorOptions options,
        IRunObserver? observer = null)
    {
        _executors = executors;
        _llm = llm;
        _approvals = approvals;
        _options = options;
        _observer = observer;
    }

    public async Task<RunState> RunAsync(WorkflowDefinition definition, CancellationToken cancellationToken = default)
    {
        Normalize(definition);
        _order = GraphValidator.TopologicalSort(definition);
        var runId = string.IsNullOrWhiteSpace(_options.RunId)
            ? DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]
            : _options.RunId!;
        _runDirectory = Path.Combine(_options.RunsRoot, runId);
        Directory.CreateDirectory(Path.Combine(_runDirectory, "artifacts"));
        _state = new RunState
        {
            RunId = runId,
            WorkflowId = definition.Id,
            WorkflowName = definition.Name,
            Requirement = definition.Requirement,
            ScenarioType = definition.ScenarioType,
            Status = WorkflowStatus.Running,
            StartedAtUtc = DateTime.UtcNow,
            Definition = definition,
            Context = new Dictionary<string, string>(definition.InitialContext ?? new Dictionary<string, string>())
        };
        foreach (var stage in _order)
        {
            _state.Stages[stage.Id] = new StageExecutionState { StageId = stage.Id, Status = StageStatus.Pending };
        }

        _audit = AuditLog.Open(Path.Combine(_runDirectory, "audit.jsonl"));
        await AuditAsync("WorkflowStarted", null, "orchestrator", $"Started {definition.Name}");
        Save();
        return await ExecuteLoopAsync(cancellationToken);
    }

    public async Task<RunState> ResumeAsync(string runId, CancellationToken cancellationToken = default)
    {
        _runDirectory = Path.Combine(_options.RunsRoot, runId);
        var statePath = Path.Combine(_runDirectory, "state.json");
        if (!File.Exists(statePath))
            throw new InvalidOperationException($"Run '{runId}' was not found.");

        _state = JsonSerializer.Deserialize<RunState>(await File.ReadAllTextAsync(statePath, cancellationToken), OrchestratorJson.Options)
            ?? throw new InvalidOperationException("Run state is empty.");
        Normalize(_state.Definition);
        _order = GraphValidator.TopologicalSort(_state.Definition);
        ClearStopFiles();
        _audit = AuditLog.Open(Path.Combine(_runDirectory, "audit.jsonl"));
        if (_state.Status == WorkflowStatus.Completed)
            return _state;

        _state.Status = WorkflowStatus.Running;
        _state.StopReason = null;
        _state.CompletedAtUtc = null;
        foreach (var stage in _state.Stages.Values)
        {
            if (stage.Status is StageStatus.Running or StageStatus.Ready or StageStatus.WaitingForApproval or StageStatus.SafeStopped)
                stage.Status = StageStatus.Pending;
        }

        _halt = 0;
        await AuditAsync("WorkflowResumed", null, "orchestrator", "Resumed run.");
        Save();
        return await ExecuteLoopAsync(cancellationToken);
    }

    public async Task<RunState> ReplanFromOutputChangeAsync(RunState state, string stageId, string newOutput, string reason, CancellationToken cancellationToken = default)
    {
        _state = state;
        _runDirectory = Path.Combine(_options.RunsRoot, state.RunId);
        Normalize(_state.Definition);
        _order = GraphValidator.TopologicalSort(_state.Definition);
        _audit = AuditLog.Open(Path.Combine(_runDirectory, "audit.jsonl"));
        await ApplyOutputChangeAsync(stageId, newOutput, reason);
        _state.Status = WorkflowStatus.Running;
        _state.CompletedAtUtc = null;
        _halt = 0;
        Save();
        return await ExecuteLoopAsync(cancellationToken);
    }

    private async Task<RunState> ExecuteLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_halt == 1 || _state.Status == WorkflowStatus.SafeStopped)
            {
                await FinishSafeStopAsync(_state.StopReason ?? "Safe stop.");
                return _state;
            }

            if (StopRequested())
            {
                await FinishSafeStopAsync("STOP file present.");
                return _state;
            }

            if (_state.StepCount >= _options.MaxSteps)
            {
                await FinishSafeStopAsync("Max steps exceeded.");
                return _state;
            }

            if (DateTime.UtcNow - _state.StartedAtUtc > _options.MaxDuration)
            {
                await FinishSafeStopAsync("Max duration exceeded.");
                return _state;
            }

            var ready = await CollectReadyAsync();
            if (ready.Count == 0)
            {
                if (_state.Stages.Values.Any(stage => stage.Status is StageStatus.Pending or StageStatus.Stale))
                {
                    _state.Status = WorkflowStatus.Failed;
                    _state.StopReason = "Workflow cannot proceed; pending stages are not ready.";
                }

                FinalizeStatus();
                await WriteReportsAsync();
                return _state;
            }

            foreach (var node in ready)
                _state.Stages[node.Id].Status = StageStatus.Ready;

            await Task.WhenAll(ready.Select(node => ExecuteNodeAsync(node, cancellationToken)));
            Save();

            if (_halt == 1 || _state.Status == WorkflowStatus.SafeStopped)
            {
                await FinishSafeStopAsync(_state.StopReason ?? "Safe stop.");
                return _state;
            }

            if (await TryApplyScenarioReplanAsync())
            {
                Save();
            }
        }
    }

    private async Task<List<StageNode>> CollectReadyAsync()
    {
        var ready = new List<StageNode>();
        foreach (var node in _order)
        {
            var execution = _state.Stages[node.Id];
            if (execution.Status is not (StageStatus.Pending or StageStatus.Stale))
                continue;

            if (node.DependsOn.Count > 0)
            {
                var dependencies = node.DependsOn.Select(id => _state.Stages[id]).ToList();
                if (dependencies.Any(dep => dep.Status == StageStatus.Rejected))
                {
                    await MarkAsync(execution, StageStatus.Blocked, "Upstream stage was rejected.", "NodeBlocked");
                    continue;
                }

                if (dependencies.Any(dep => dep.Status == StageStatus.Blocked))
                {
                    await MarkAsync(execution, StageStatus.Blocked, "Upstream stage was blocked.", "NodeBlocked");
                    continue;
                }

                if (dependencies.Any(dep => dep.Status is StageStatus.Failed or StageStatus.RolledBack))
                {
                    await MarkAsync(execution, StageStatus.RolledBack, "Upstream stage failed or was rolled back.", "NodeRolledBack");
                    continue;
                }

                if (dependencies.Any(dep => dep.Status != StageStatus.Succeeded))
                    continue;
            }

            if (!EntryGateMet(node, out var reason))
            {
                await MarkAsync(execution, StageStatus.Blocked, reason, "NodeBlocked");
                continue;
            }

            ready.Add(node);
        }

        return ready;
    }

    private async Task ExecuteNodeAsync(StageNode stage, CancellationToken cancellationToken)
    {
        var execution = _state.Stages[stage.Id];
        execution.ExecutionCount++;
        execution.StartedAtUtc ??= DateTime.UtcNow;
        execution.InputHash = ComputeInputHash(stage);
        var workspace = new Workspace(_runDirectory, stage.AllowedWritePaths, stage.LargeDiffLineThreshold);
        var failureFeedback = "";
        long? firstFailureStamp = execution.RecoveryMs is null && execution.FirstFailureAtUtc is not null
            ? null
            : null;
        var retriesThisRun = 0;
        string? snapshot = null;

        try
        {
            if (NeedsApproval(stage) && !IsAfter(stage))
            {
                if (!await ApproveAsync(stage, $"Approve '{stage.Name}' before it runs. Risk: {stage.Risk}.", cancellationToken))
                {
                    await MarkAsync(execution, StageStatus.Rejected, "Human rejected the stage.", "NodeRejected");
                    execution.CompletedAtUtc = DateTime.UtcNow;
                    return;
                }
            }

            if (stage.Rollback)
            {
                snapshot = Path.Combine(_runDirectory, "snapshots", stage.Id);
                DirectorySnapshot.Capture(Path.Combine(_runDirectory, "workspace"), snapshot);
            }

            for (var attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    _state.StepCount++;
                    _state.TotalAttempts++;
                }

                execution.Attempt = attempt;
                execution.Status = StageStatus.Running;
                Notify(stage.Id, "Running", $"Attempt {attempt}");
                await AuditAsync("NodeStarted", stage.Id, "orchestrator", $"Attempt {attempt}");

                var context = new StageContext
                {
                    Definition = _state.Definition,
                    Stage = stage,
                    State = _state,
                    Workspace = workspace,
                    Llm = _llm,
                    RepoRoot = _options.RepoRoot,
                    RunDirectory = _runDirectory,
                    Attempt = attempt,
                    FailureFeedback = failureFeedback
                };

                try
                {
                    var executor = _executors.Resolve(stage);
                    var result = await executor.ExecuteAsync(context, cancellationToken);
                    if (!result.Success)
                        throw new StageFailedException(result.FailureReason ?? "Stage failed.");

                    var inject = !string.IsNullOrWhiteSpace(_options.InjectFailureStage) &&
                                 stage.Id.Equals(_options.InjectFailureStage, StringComparison.OrdinalIgnoreCase);
                    if (inject)
                    {
                        var folder = stage.AllowedWritePaths.FirstOrDefault() ?? "workspace";
                        workspace.Write(folder.TrimEnd('/') + "/injected-failure.txt", "attempt " + attempt);
                        throw new InjectedFailureException($"Injected failure at '{stage.Id}' attempt {attempt}.");
                    }

                    if (!stage.ReadOnly)
                    {
                        var blocking = SecurityScanner.ScanDirectory(Path.Combine(_runDirectory, "workspace"))
                            .Where(finding => finding.Severity is "HIGH" or "CRITICAL")
                            .ToList();
                        if (blocking.Count > 0)
                        {
                            var detail = string.Join("; ", blocking.Select(finding => finding.Rule + " in " + Path.GetFileName(finding.File)));
                            throw new SecurityGateException("Security gate failed: " + detail);
                        }
                    }

                    await CompleteSuccessAsync(stage, execution, result, retriesThisRun, firstFailureStamp);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (PolicyViolationException ex)
                {
                    execution.RetryCount = retriesThisRun;
                    execution.CompletedAtUtc = DateTime.UtcNow;
                    execution.LatencyMs = Elapsed(execution);
                    await MarkAsync(execution, StageStatus.Blocked, ex.Message, "PolicyDenied");
                    AddDecision(stage.Id, "policy", "Blocked write", ex.Message, execution.InputHash);
                    return;
                }
                catch (SecurityGateException ex)
                {
                    RecordCategory(FailureCategory.Security);
                    execution.RetryCount = retriesThisRun;
                    execution.FailureReason = ex.Message;
                    execution.CompletedAtUtc = DateTime.UtcNow;
                    execution.LatencyMs = Elapsed(execution);
                    if (stage.Rollback && snapshot is not null)
                        await RollbackAsync(stage, snapshot);
                    execution.Status = stage.Rollback ? StageStatus.RolledBack : StageStatus.Failed;
                    await AuditAsync("SecurityGateFailed", stage.Id, "policy", ex.Message);
                    Notify(stage.Id, execution.Status.ToString(), ex.Message);
                    return;
                }
                catch (ApprovalTimeoutException ex)
                {
                    execution.Status = StageStatus.WaitingForApproval;
                    _state.StopReason = ex.Message;
                    _state.Status = WorkflowStatus.SafeStopped;
                    Interlocked.Exchange(ref _halt, 1);
                    await AuditAsync("SafeStop", stage.Id, "orchestrator", ex.Message);
                    return;
                }
                catch (Exception ex)
                {
                    var category = FailureClassifier.Classify(ex);
                    RecordCategory(category);
                    execution.FirstFailureAtUtc ??= DateTime.UtcNow;
                    firstFailureStamp ??= System.Diagnostics.Stopwatch.GetTimestamp();
                    failureFeedback = ex.Message;
                    execution.FailureReason = ex.Message;

                    if (category is FailureCategory.Security or FailureCategory.Governance)
                    {
                        execution.Status = StageStatus.Blocked;
                        execution.CompletedAtUtc = DateTime.UtcNow;
                        await AuditAsync("NodeBlocked", stage.Id, "policy", ex.Message);
                        return;
                    }

                    if (category == FailureCategory.Unknown || attempt > stage.MaxRetries)
                    {
                        execution.RetryCount = retriesThisRun;
                        execution.CompletedAtUtc = DateTime.UtcNow;
                        execution.LatencyMs = Elapsed(execution);
                        if (category == FailureCategory.Unknown)
                        {
                            execution.Status = StageStatus.Failed;
                            _state.StopReason = "Unclassified failure; safe stop. " + ex.Message;
                            _state.Status = WorkflowStatus.SafeStopped;
                            Interlocked.Exchange(ref _halt, 1);
                            await AuditAsync("SafeStop", stage.Id, "orchestrator", ex.Message);
                            return;
                        }

                        if (stage.Rollback && snapshot is not null)
                        {
                            await RollbackAsync(stage, snapshot);
                            execution.Status = StageStatus.RolledBack;
                        }
                        else
                        {
                            execution.Status = StageStatus.Failed;
                            await AuditAsync("NodeFailed", stage.Id, "orchestrator", ex.Message);
                        }

                        Notify(stage.Id, execution.Status.ToString(), ex.Message);
                        return;
                    }

                    retriesThisRun++;
                    execution.RetryCount = retriesThisRun;
                    lock (_gate) { _state.TotalRetries++; }
                    await AuditAsync("RetryScheduled", stage.Id, "orchestrator", $"Retry {retriesThisRun}: {ex.Message}");
                    Notify(stage.Id, "Retry", ex.Message);
                    var delayMs = Math.Min(2000, _options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                    if (delayMs > 0)
                        await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            execution.Status = StageStatus.Pending;
            throw;
        }
    }

    private async Task CompleteSuccessAsync(StageNode stage, StageExecutionState execution, StageResult result, int retriesThisRun, long? firstFailureStamp)
    {
        if (NeedsApproval(stage) && IsAfter(stage))
        {
            var preview = result.Output.Length > 500 ? result.Output[..500] : result.Output;
            if (!await ApproveAsync(stage, $"Approve '{stage.Name}' after review. Risk: {stage.Risk}.\n{preview}", CancellationToken.None))
            {
                execution.Output = ForState(result.Output);
                execution.OutputHash = Hashing.Sha256(result.Output);
                await MarkAsync(execution, StageStatus.Rejected, "Human rejected the stage.", "NodeRejected");
                execution.CompletedAtUtc = DateTime.UtcNow;
                execution.LatencyMs = Elapsed(execution);
                return;
            }
        }

        execution.RetryCount = retriesThisRun;
        execution.Output = ForState(result.Output);
        execution.OutputHash = Hashing.Sha256(result.Output);
        execution.Status = StageStatus.Succeeded;
        execution.FailureReason = null;
        execution.CompletedAtUtc = DateTime.UtcNow;
        execution.LatencyMs = Elapsed(execution);
        if (firstFailureStamp is long stamp)
        {
            execution.RecoveryMs = (System.Diagnostics.Stopwatch.GetTimestamp() - stamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        if (result.Context is not null)
        {
            lock (_gate)
            {
                foreach (var pair in result.Context)
                    _state.Context[pair.Key] = pair.Value;
            }
        }

        WriteArtifact(stage, execution, result.Output);
        AddDecision(stage.Id, "agent", "Accepted stage output", Truncate(result.Output, 240), execution.InputHash);
        await AuditAsync("NodeSucceeded", stage.Id, "agent", $"{stage.Name} succeeded.");
        await AuditAsync("ArtifactCreated", stage.Id, "orchestrator", execution.OutputHash ?? "");
        if (result.UsedFallback)
            await AuditAsync("LlmFallback", stage.Id, "llm", "Primary LLM was unavailable; deterministic fallback completed the stage.");
        Notify(stage.Id, "Succeeded", stage.Name);
    }

    private async Task<bool> ApproveAsync(StageNode stage, string summary, CancellationToken cancellationToken)
    {
        var execution = _state.Stages[stage.Id];
        execution.Status = StageStatus.WaitingForApproval;
        Notify(stage.Id, "WaitingForApproval", summary);
        await AuditAsync("ApprovalRequested", stage.Id, "orchestrator", summary);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        ApprovalDecision decision;
        if (_options.AutoApprove)
        {
            decision = new ApprovalDecision(true, "human", "Auto-approved by --auto-approve", TimeSpan.Zero);
        }
        else
        {
            decision = await _approvals.RequestAsync(new ApprovalRequest
            {
                RunId = _state.RunId,
                RunDirectory = _runDirectory,
                StageId = stage.Id,
                StageName = stage.Name,
                Risk = stage.Risk,
                Summary = summary
            }, cancellationToken);
        }

        watch.Stop();
        execution.ApprovalWaitMs += watch.Elapsed.TotalMilliseconds;
        AddDecision(stage.Id, "human", decision.Approved ? "Approved stage" : "Rejected stage", decision.Comment, execution.InputHash);
        await AuditAsync(decision.Approved ? "ApprovalGranted" : "ApprovalRejected", stage.Id, decision.Actor, decision.Comment);
        Notify(stage.Id, decision.Approved ? "Approved" : "Rejected", decision.Comment);
        return decision.Approved;
    }

    private async Task RollbackAsync(StageNode stage, string snapshot)
    {
        await AuditAsync("RollbackStarted", stage.Id, "orchestrator", "Restoring workspace snapshot.");
        DirectorySnapshot.Restore(snapshot, Path.Combine(_runDirectory, "workspace"));
        lock (_gate) { _state.RollbackCount++; }
        await AuditAsync("RollbackCompleted", stage.Id, "orchestrator", "Workspace restored.");
        AddDecision(stage.Id, "orchestrator", "Rolled back stage", "Retries exhausted.", _state.Stages[stage.Id].InputHash);
    }

    private async Task<bool> TryApplyScenarioReplanAsync()
    {
        var replan = _state.Definition.Replan;
        if (_state.ReplanApplied || replan is null || string.IsNullOrWhiteSpace(replan.AfterStage))
            return false;
        if (!_state.Stages.TryGetValue(replan.AfterStage, out var after) || after.Status != StageStatus.Succeeded)
            return false;
        if (string.IsNullOrWhiteSpace(replan.ChangedStage))
            return false;

        await ApplyOutputChangeAsync(replan.ChangedStage, replan.NewOutput, replan.Reason);
        _state.ReplanApplied = true;
        return true;
    }

    private async Task ApplyOutputChangeAsync(string stageId, string newOutput, string reason)
    {
        if (!_state.Stages.TryGetValue(stageId, out var changed))
            throw new InvalidOperationException($"Cannot replan unknown stage '{stageId}'.");

        var previous = changed.OutputHash;
        var previousOutput = changed.Output;
        if (!string.IsNullOrEmpty(previousOutput))
        {
            Directory.CreateDirectory(Path.Combine(_runDirectory, "artifacts"));
            File.WriteAllText(Path.Combine(_runDirectory, "artifacts", stageId + ".superseded.txt"), previousOutput);
        }

        changed.Output = ForState(newOutput);
        changed.OutputHash = Hashing.Sha256(newOutput);
        if (stageId.Equals("requirements", StringComparison.OrdinalIgnoreCase))
            _state.Requirement = newOutput;

        _state.ReplanCount++;
        await AuditAsync("ReplanStarted", stageId, "human", reason);
        await AuditAsync("ArtifactSuperseded", stageId, "orchestrator", $"Previous hash {previous}");
        AddDecision(stageId, "human", "Changed upstream artifact", reason, previous ?? "");

        foreach (var downstream in GraphValidator.DownstreamOf(_state.Definition, stageId))
        {
            var execution = _state.Stages[downstream];
            execution.Status = StageStatus.Stale;
            execution.CompletedAtUtc = null;
            execution.FailureReason = "Invalidated because an upstream artifact changed.";
            _state.InvalidatedStages.Add(downstream);
            await AuditAsync("NodeInvalidated", downstream, "orchestrator", $"Upstream {stageId} changed.");
        }

        await AuditAsync("ReplanCompleted", stageId, "orchestrator", $"{_state.InvalidatedStages.Count} downstream stages invalidated.");
        WriteArtifact(new StageNode { Id = stageId, Name = stageId }, changed, newOutput);
    }

    private void FinalizeStatus()
    {
        var statuses = _state.Stages.Values.Select(stage => stage.Status).ToList();
        if (statuses.Count > 0 && statuses.All(status => status == StageStatus.Succeeded))
            _state.Status = WorkflowStatus.Completed;
        else if (statuses.Any(status => status == StageStatus.Rejected))
            _state.Status = WorkflowStatus.Rejected;
        else if (_state.RollbackCount > 0 || statuses.Any(status => status == StageStatus.RolledBack))
            _state.Status = WorkflowStatus.RolledBack;
        else
            _state.Status = WorkflowStatus.Failed;

        _state.CompletedAtUtc ??= DateTime.UtcNow;
    }

    private async Task FinishSafeStopAsync(string reason)
    {
        _state.Status = WorkflowStatus.SafeStopped;
        _state.StopReason = reason;
        _state.CompletedAtUtc = DateTime.UtcNow;
        foreach (var stage in _state.Stages.Values)
        {
            if (stage.Status is StageStatus.Running or StageStatus.Ready)
                stage.Status = StageStatus.Pending;
        }

        await AuditAsync("SafeStop", null, "orchestrator", reason);
        await WriteReportsAsync();
        Notify("-", "SafeStopped", reason);
    }

    private async Task WriteReportsAsync()
    {
        var metrics = RunMetricsCalculator.Calculate(_state);
        var metricsPath = Path.Combine(_runDirectory, "metrics.json");
        await WriteAtomicAsync(metricsPath, JsonSerializer.Serialize(metrics, OrchestratorJson.Options));
        var summary = new StringBuilder();
        summary.AppendLine($"# {_state.WorkflowName}");
        summary.AppendLine();
        summary.AppendLine($"- Run: `{_state.RunId}`");
        summary.AppendLine($"- Status: {_state.Status}");
        summary.AppendLine($"- Requirement: {_state.Requirement}");
        summary.AppendLine();
        foreach (var stage in _order)
        {
            var execution = _state.Stages[stage.Id];
            summary.AppendLine($"- {stage.Id}: {execution.Status} (retries {execution.RetryCount}, executions {execution.ExecutionCount})");
        }

        await WriteAtomicAsync(Path.Combine(_runDirectory, "summary.md"), summary.ToString());
        Save();
        var terminal = _state.Status.ToString();
        await AuditAsync("Workflow" + terminal, null, "orchestrator", terminal);
    }

    private async Task MarkAsync(StageExecutionState execution, StageStatus status, string reason, string eventType)
    {
        execution.Status = status;
        execution.FailureReason = reason;
        await AuditAsync(eventType, execution.StageId, "orchestrator", reason);
        Notify(execution.StageId, status.ToString(), reason);
    }

    private bool EntryGateMet(StageNode stage, out string reason)
    {
        var gate = string.IsNullOrWhiteSpace(stage.EntryGate) ? "always" : stage.EntryGate.Trim();
        if (gate.Equals("always", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Entry gate passed.";
            return true;
        }

        if (gate.StartsWith("context:", StringComparison.OrdinalIgnoreCase))
        {
            var expression = gate["context:".Length..];
            var split = expression.Split('=', 2);
            if (split.Length == 2 &&
                _state.Context.TryGetValue(split[0], out var value) &&
                string.Equals(value, split[1], StringComparison.Ordinal))
            {
                reason = "Entry gate passed.";
                return true;
            }

            reason = $"Entry gate unmet: {gate}";
            return false;
        }

        reason = $"Entry gate unmet: unknown gate '{gate}'.";
        return false;
    }

    private static bool NeedsApproval(StageNode stage) =>
        stage.Autonomy.Equals("HumanRequired", StringComparison.OrdinalIgnoreCase) ||
        stage.Autonomy.Equals("AutoWithReview", StringComparison.OrdinalIgnoreCase);

    private static bool IsAfter(StageNode stage) =>
        stage.ApprovalTiming.Equals("after", StringComparison.OrdinalIgnoreCase);

    private string ComputeInputHash(StageNode stage)
    {
        var builder = new StringBuilder();
        builder.Append(_state.Requirement);
        foreach (var dependency in stage.DependsOn.OrderBy(id => id, StringComparer.Ordinal))
        {
            builder.Append('|').Append(dependency).Append('=').Append(_state.Stages[dependency].OutputHash);
        }

        return Hashing.Sha256(builder.ToString());
    }

    private void RecordCategory(FailureCategory category)
    {
        lock (_gate)
        {
            var key = category.ToString();
            _state.FailureCategories.TryGetValue(key, out var count);
            _state.FailureCategories[key] = count + 1;
        }
    }

    private void AddDecision(string stage, string actor, string what, string why, string? inputsHash)
    {
        lock (_gate)
        {
            var parent = _state.Decisions.Count == 0 ? null : _state.Decisions[^1].Id;
            _state.Decisions.Add(new DecisionRecord
            {
                Id = "dec-" + Guid.NewGuid().ToString("N")[..12],
                Stage = stage,
                Actor = actor,
                What = what,
                Why = Truncate(why, 500),
                InputsHash = inputsHash ?? "",
                ParentDecisionId = parent,
                TimestampUtc = DateTime.UtcNow
            });
        }
    }

    private void WriteArtifact(StageNode stage, StageExecutionState execution, string output)
    {
        var directory = Path.Combine(_runDirectory, "artifacts");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, stage.Id + ".v" + Math.Max(1, execution.ExecutionCount) + ".txt"), output);
        File.WriteAllText(Path.Combine(directory, stage.Id + ".txt"), output);
    }

    private async Task AuditAsync(string eventType, string? stage, string actor, string message)
    {
        if (_audit is null)
            return;
        await _audit.AppendAsync(_state.RunId, eventType, stage, actor, message);
    }

    private void Notify(string stageId, string status, string message)
    {
        lock (_gate)
        {
            _observer?.OnUpdate(stageId, status, message);
        }
    }

    private bool StopRequested()
    {
        var global = _options.StopFilePath ?? Path.Combine(_options.RunsRoot, "STOP");
        return File.Exists(global) || File.Exists(Path.Combine(_runDirectory, "STOP"));
    }

    private void ClearStopFiles()
    {
        foreach (var path in new[] { _options.StopFilePath ?? Path.Combine(_options.RunsRoot, "STOP"), Path.Combine(_runDirectory, "STOP") })
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(_runDirectory);
        var json = JsonSerializer.Serialize(_state, OrchestratorJson.Options);
        WriteAtomicAsync(Path.Combine(_runDirectory, "state.json"), json).GetAwaiter().GetResult();
    }

    private static async Task WriteAtomicAsync(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        await File.WriteAllTextAsync(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private static void Normalize(WorkflowDefinition definition)
    {
        definition.Stages ??= [];
        definition.InitialContext ??= new Dictionary<string, string>();
        foreach (var stage in definition.Stages)
        {
            stage.DependsOn ??= [];
            stage.AllowedWritePaths ??= [];
            if (string.IsNullOrWhiteSpace(stage.Autonomy))
                stage.Autonomy = "Auto";
            if (string.IsNullOrWhiteSpace(stage.EntryGate))
                stage.EntryGate = "always";
            if (string.IsNullOrWhiteSpace(stage.ApprovalTiming))
                stage.ApprovalTiming = "before";
        }
    }

    private static double Elapsed(StageExecutionState execution) =>
        execution.StartedAtUtc is null || execution.CompletedAtUtc is null
            ? 0
            : Math.Max(0, (execution.CompletedAtUtc.Value - execution.StartedAtUtc.Value).TotalMilliseconds);

    private static string ForState(string output) => output.Length <= 100_000 ? output : output[^8000..];

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        return value.Length <= max ? value : value[..max];
    }
}
