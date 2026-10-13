using System.Text;

namespace UrlShortener.Infrastructure;

public interface ICommitGuard
{
    void OnBeforeCommit(string tempPath, string destinationPath);
}

public sealed class AllowCommitGuard : ICommitGuard
{
    public void OnBeforeCommit(string tempPath, string destinationPath)
    {
    }
}

/// <summary>
/// Writes a temp file in the destination directory, then replaces the destination.
/// A crash or an injected failure before the replace leaves the previous file intact.
/// </summary>
public sealed class AtomicFileStore
{
    private readonly ICommitGuard _guard;

    public AtomicFileStore(ICommitGuard? guard = null)
    {
        _guard = guard ?? new AllowCommitGuard();
    }

    public async Task WriteAsync(string destinationPath, string content, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrEmpty(directory))
            throw new ArgumentException("Destination path must include a directory.", nameof(destinationPath));

        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, "." + Path.GetFileName(destinationPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.None))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            _guard.OnBeforeCommit(temp, destinationPath);
            CommitWithRetry(temp, destinationPath);
        }
        catch
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* best-effort cleanup of the uncommitted temp file */ }
            }

            throw;
        }
    }

    private static void CommitWithRetry(string temp, string destinationPath)
    {
        const int attempts = 8;
        for (var i = 1; i <= attempts; i++)
        {
            try
            {
                if (File.Exists(destinationPath))
                    File.Replace(temp, destinationPath, null, ignoreMetadataErrors: true);
                else
                    File.Move(temp, destinationPath);
                return;
            }
            catch (Exception ex) when (i < attempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(25 * i);
            }
        }
    }
}
