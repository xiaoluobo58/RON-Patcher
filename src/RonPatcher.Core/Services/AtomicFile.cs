namespace RonPatcher.Core;

internal static class AtomicFile
{
    public static async Task CopyAsync(
        string source,
        string target,
        string gameRootWithSeparator,
        string? expectedHash,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(source))
            throw new FileNotFoundException("Source file was not found.", source);

        var directory = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(directory))
            throw new PatcherException($"Invalid target path: {target}");
        Directory.CreateDirectory(directory);
        PathSafety.EnsureParentDirectoriesAreSafe(gameRootWithSeparator, target);
        PathSafety.EnsureNotReparsePoint(target);

        var temp = target + ".ronpatcher-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 128 * 1024, options: FileOptions.SequentialScan | FileOptions.Asynchronous))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 128 * 1024, options: FileOptions.SequentialScan | FileOptions.Asynchronous))
            {
                await input.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            if (!string.IsNullOrEmpty(expectedHash))
            {
                var hash = await Hashing.Sha256Async(temp, cancellationToken).ConfigureAwait(false);
                if (!Hashing.EqualsHash(hash, expectedHash))
                    throw new PatcherException($"Copied file hash verification failed: {target}");
            }

            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    public static async Task MoveToQuarantineAsync(
        string source,
        string quarantinePath,
        string gameRootWithSeparator,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(source))
            return;
        PathSafety.EnsureParentDirectoriesAreSafe(gameRootWithSeparator, source);
        PathSafety.EnsureNotReparsePoint(source);
        var directory = Path.GetDirectoryName(quarantinePath)
            ?? throw new PatcherException("Invalid quarantine path.");
        Directory.CreateDirectory(directory);

        try
        {
            // The quarantine normally lives on the same volume, making this atomic.
            File.Move(source, quarantinePath, overwrite: false);
        }
        catch (IOException)
        {
            // Fall back to a verified copy for a data root on another volume.
            await CopyAsync(source, quarantinePath, gameRootWithSeparator, expectedHash: null, cancellationToken)
                .ConfigureAwait(false);
            File.Delete(source);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Preserve the original exception.
        }
    }
}
