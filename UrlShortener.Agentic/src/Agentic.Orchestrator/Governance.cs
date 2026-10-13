using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Agentic.Orchestrator;

public static class OrchestratorJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static readonly JsonSerializerOptions Line = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}

public static class Hashing
{
    public static string Sha256(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public static class FailureClassifier
{
    public static FailureCategory Classify(Exception exception) => exception switch
    {
        InjectedFailureException => FailureCategory.Implementation,
        StageFailedException => FailureCategory.Implementation,
        SecurityGateException => FailureCategory.Security,
        PolicyViolationException => FailureCategory.Governance,
        LlmUnavailableException => FailureCategory.Dependency,
        TimeoutException => FailureCategory.Transient,
        IOException => FailureCategory.Transient,
        HttpRequestException => FailureCategory.Transient,
        _ => FailureCategory.Unknown
    };
}

public static class PolicyEngine
{
    public static PolicyDecision EvaluateWrite(string root, string relativePath, string content, IReadOnlyList<string> allowed, int largeDiffLineThreshold = 0)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return PolicyDecision.Deny("Policy denied write: empty path.");
        if (Path.IsPathRooted(relativePath))
            return PolicyDecision.Deny("Policy denied write: absolute paths are not allowed.");

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (normalized.Split('/').Any(segment => segment == ".."))
            return PolicyDecision.Deny("Policy denied write: path traversal is not allowed.");

        var full = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var rootFull = Path.GetFullPath(root);
        if (!IsUnder(full, rootFull))
            return PolicyDecision.Deny("Policy denied write: path escapes the run directory.");

        if (allowed.Count == 0)
            return PolicyDecision.Deny("Policy denied write: stage is read-only.");

        var allowedMatch = allowed.Any(prefix =>
        {
            var clean = prefix.Replace('\\', '/').Trim('/');
            return normalized.Equals(clean, StringComparison.OrdinalIgnoreCase) ||
                   normalized.StartsWith(clean + "/", StringComparison.OrdinalIgnoreCase);
        });
        if (!allowedMatch)
            return PolicyDecision.Deny($"Policy denied write: path '{normalized}' is outside allowed write paths ({string.Join(", ", allowed)}).");

        if (largeDiffLineThreshold > 0)
        {
            var lines = content.Count(ch => ch == '\n') + 1;
            if (lines > largeDiffLineThreshold)
                return PolicyDecision.Deny($"Policy denied write: large diff of {lines} lines requires human approval.");
        }

        return PolicyDecision.Allow();
    }

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var noEmail = Regex.Replace(value, @"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}", "[redacted-email]", RegexOptions.IgnoreCase);
        return Regex.Replace(noEmail, @"\b\d{12,}\b", "[redacted-number]");
    }

    private static bool IsUnder(string full, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.Equals(root, comparison) || full.StartsWith(prefix, comparison);
    }
}

public readonly record struct PolicyDecision(bool Allowed, string Reason)
{
    public static PolicyDecision Allow() => new(true, "allowed");
    public static PolicyDecision Deny(string reason) => new(false, reason);
}

public sealed class SecurityFinding
{
    public string Severity { get; set; } = "LOW";
    public string File { get; set; } = "";
    public string Rule { get; set; } = "";
    public string Message { get; set; } = "";
}

public static class SecurityScanner
{
    private static readonly string[] CodeExtensions = [".cs", ".js", ".ts", ".ps1", ".sh", ".cmd", ".bat"];
    private static readonly Regex SecretPattern = new(
        "(?i)(api[_-]?key|password|secret|connectionstring)\\s*[:=]\\s*\"[^\"]+\"",
        RegexOptions.Compiled);

    public static List<SecurityFinding> ScanText(string content, string fileName)
    {
        var findings = new List<SecurityFinding>();
        var extension = Path.GetExtension(fileName);
        if (!CodeExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return findings;

        if (content.Contains("Process.Start", StringComparison.Ordinal) ||
            content.Contains("System.Diagnostics.Process", StringComparison.Ordinal))
        {
            findings.Add(new SecurityFinding
            {
                Severity = "HIGH",
                File = fileName,
                Rule = "dangerous-process",
                Message = "Generated code invokes a process API."
            });
        }

        if (SecretPattern.IsMatch(content))
        {
            findings.Add(new SecurityFinding
            {
                Severity = "CRITICAL",
                File = fileName,
                Rule = "hardcoded-secret",
                Message = "Generated code appears to contain a hardcoded secret."
            });
        }

        return findings;
    }

    public static List<SecurityFinding> ScanDirectory(string directory)
    {
        var findings = new List<SecurityFinding>();
        if (!Directory.Exists(directory))
            return findings;

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                continue;

            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch
            {
                continue;
            }

            findings.AddRange(ScanText(text, file));
        }

        return findings;
    }
}

public sealed class Workspace
{
    private readonly string _root;
    private readonly IReadOnlyList<string> _allowed;
    private readonly int _largeDiffLineThreshold;

    public Workspace(string runDirectory, IReadOnlyList<string> allowedPaths, int largeDiffLineThreshold = 0)
    {
        _root = runDirectory;
        _allowed = allowedPaths;
        _largeDiffLineThreshold = largeDiffLineThreshold;
    }

    public string Root => _root;

    public void Write(string relativePath, string content)
    {
        var decision = PolicyEngine.EvaluateWrite(_root, relativePath, content, _allowed, _largeDiffLineThreshold);
        if (!decision.Allowed)
            throw new PolicyViolationException(decision.Reason);

        var full = Path.GetFullPath(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}

public static class DirectorySnapshot
{
    public static void Capture(string source, string destination)
    {
        if (Directory.Exists(destination))
            Directory.Delete(destination, recursive: true);
        Directory.CreateDirectory(destination);
        if (!Directory.Exists(source) || !Directory.EnumerateFileSystemEntries(source).Any())
        {
            File.WriteAllText(Path.Combine(destination, ".empty"), "");
            return;
        }

        CopyAll(source, destination);
    }

    public static void Restore(string snapshot, string source)
    {
        if (Directory.Exists(source))
            Directory.Delete(source, recursive: true);
        Directory.CreateDirectory(source);
        if (!Directory.Exists(snapshot) || File.Exists(Path.Combine(snapshot, ".empty")))
            return;
        CopyAll(snapshot, source);
    }

    private static void CopyAll(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}

public sealed class AuditEnvelope
{
    public long Seq { get; set; }
    public string PrevHash { get; set; } = "";
    public string Hash { get; set; } = "";
    public string TimestampUtc { get; set; } = "";
    public string RunId { get; set; } = "";
    public string EventType { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Actor { get; set; } = "";
    public string Message { get; set; } = "";
    public string Details { get; set; } = "";
}

public sealed record AuditVerificationResult(bool Valid, string Message, int Events);

public sealed class AuditLog
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _lastHash;
    private long _seq;

    private AuditLog(string path, string lastHash, long seq)
    {
        _path = path;
        _lastHash = lastHash;
        _seq = seq;
    }

    public static AuditLog Open(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lastHash = "GENESIS";
        long seq = 0;
        if (File.Exists(path))
        {
            foreach (var line in ReadLinesShared(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var envelope = JsonSerializer.Deserialize<AuditEnvelope>(line, OrchestratorJson.Line);
                if (envelope is null)
                    continue;
                lastHash = envelope.Hash;
                seq = envelope.Seq;
            }
        }

        return new AuditLog(path, lastHash, seq);
    }

    public async Task AppendAsync(string runId, string eventType, string? stage, string actor, string message, string? details = null, CancellationToken cancellationToken = default)
    {
        var envelope = new AuditEnvelope
        {
            TimestampUtc = DateTime.UtcNow.ToString("O"),
            RunId = runId,
            EventType = eventType,
            Stage = stage ?? "",
            Actor = actor,
            Message = PolicyEngine.Redact(message),
            Details = PolicyEngine.Redact(details)
        };

        await _gate.WaitAsync(cancellationToken);
        try
        {
            envelope.Seq = ++_seq;
            envelope.PrevHash = _lastHash;
            envelope.Hash = ComputeHash(envelope.PrevHash, envelope);
            _lastHash = envelope.Hash;
            var line = JsonSerializer.Serialize(envelope, OrchestratorJson.Line) + "\n";
            await File.AppendAllTextAsync(_path, line, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static AuditVerificationResult Verify(string path)
    {
        if (!File.Exists(path))
            return new AuditVerificationResult(false, "Audit log is missing.", 0);

        var prev = "GENESIS";
        long expected = 1;
        var count = 0;
        foreach (var line in ReadLinesShared(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            AuditEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<AuditEnvelope>(line, OrchestratorJson.Line);
            }
            catch (Exception ex)
            {
                return new AuditVerificationResult(false, $"Line {expected} is not valid JSON ({ex.Message}).", count);
            }

            if (envelope is null)
                return new AuditVerificationResult(false, $"Line {expected} is empty.", count);
            if (envelope.Seq != expected)
                return new AuditVerificationResult(false, $"Sequence gap at seq {envelope.Seq}; expected {expected}.", count);
            if (!string.Equals(envelope.PrevHash, prev, StringComparison.Ordinal))
                return new AuditVerificationResult(false, $"Previous-hash mismatch at seq {envelope.Seq}.", count);
            var computed = ComputeHash(envelope.PrevHash, envelope);
            if (!string.Equals(computed, envelope.Hash, StringComparison.OrdinalIgnoreCase))
                return new AuditVerificationResult(false, $"Hash mismatch at seq {envelope.Seq}. The log was tampered.", count);
            prev = envelope.Hash;
            expected++;
            count++;
        }

        return new AuditVerificationResult(true, $"Verified {count} audit events.", count);
    }

    public static string ComputeHash(string prevHash, AuditEnvelope envelope)
    {
        var material = string.Join("\n",
            envelope.Seq.ToString(),
            envelope.TimestampUtc,
            envelope.RunId,
            envelope.EventType,
            envelope.Stage,
            envelope.Actor,
            envelope.Message,
            envelope.Details);
        return Hashing.Sha256(prevHash + "\n" + material);
    }

    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
            yield return line;
    }
}

public sealed class FileApprovalService : IApprovalService
{
    public async Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        var requestPath = Path.Combine(request.RunDirectory, "approval-request.json");
        var decisionPath = Path.Combine(request.RunDirectory, "approval-decision.json");
        if (File.Exists(decisionPath))
            File.Delete(decisionPath);

        var requestedAt = DateTime.UtcNow;
        var payload = JsonSerializer.Serialize(new
        {
            request.RequestId,
            request.RunId,
            request.StageId,
            request.StageName,
            request.Risk,
            request.Summary,
            requestedAtUtc = requestedAt.ToString("O")
        }, OrchestratorJson.Options);
        await File.WriteAllTextAsync(requestPath, payload, cancellationToken);

        var deadline = DateTime.UtcNow + request.Timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(decisionPath))
            {
                var json = await File.ReadAllTextAsync(decisionPath, cancellationToken);
                using var document = JsonDocument.Parse(json);
                var id = document.RootElement.TryGetProperty("requestId", out var idElement) ? idElement.GetString() : "";
                if (string.Equals(id, request.RequestId, StringComparison.Ordinal))
                {
                    var decision = document.RootElement.TryGetProperty("decision", out var decisionElement) ? decisionElement.GetString() : "";
                    var comment = document.RootElement.TryGetProperty("comment", out var commentElement) ? commentElement.GetString() : "";
                    var approved = string.Equals(decision, "approve", StringComparison.OrdinalIgnoreCase);
                    return new ApprovalDecision(approved, "human", comment ?? "", DateTime.UtcNow - requestedAt);
                }
            }

            await Task.Delay(200, cancellationToken);
        }

        throw new ApprovalTimeoutException($"Approval timed out for stage '{request.StageId}'.");
    }
}
