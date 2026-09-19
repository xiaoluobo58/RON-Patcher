using SharpCompress.Archives;
using SharpCompress.Readers;

namespace RonPatcher.Core;

internal sealed class PatchArchiveReader
{
    private static readonly string[] SupportedExtensions = [".zip", ".rar", ".7z"];
    private readonly RonPatcherOptions _options;

    public PatchArchiveReader(RonPatcherOptions options)
    {
        _options = options;
    }

    public async Task<PatchManifest> ReadManifestAsync(CancellationToken cancellationToken)
    {
        var archivePath = Path.GetFullPath(_options.PatchArchivePath);
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("找不到补丁压缩包。", archivePath);
        if (!SupportedExtensions.Contains(Path.GetExtension(archivePath), StringComparer.OrdinalIgnoreCase))
            throw new UnsafeArchiveException("补丁格式必须是 ZIP、RAR 或 7Z。");

        var archiveHash = await Hashing.Sha256Async(archivePath, cancellationToken).ConfigureAwait(false);
        var files = new List<PatchFileEntry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;

        try
        {
            using var archive = OpenArchive(archivePath);
            if (archive.IsEncrypted)
                throw new UnsafeArchiveException("不支持带密码的补丁压缩包。");

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.IsDirectory)
                    continue;
                if (entry.IsEncrypted)
                    throw new UnsafeArchiveException($"补丁条目已加密：{entry.Key}");
                if (!string.IsNullOrWhiteSpace(entry.LinkTarget))
                    throw new UnsafeArchiveException($"补丁中不能包含符号链接：{entry.Key}");
                if (files.Count >= _options.MaxArchiveEntries)
                    throw new UnsafeArchiveException($"补丁文件数量超过 {_options.MaxArchiveEntries} 个。");
                if (entry.Size < 0 || entry.Size > _options.MaxEntryBytes)
                    throw new UnsafeArchiveException($"补丁条目过大：{entry.Key}");

                var entryKey = entry.Key ?? throw new UnsafeArchiveException("补丁包含无名称条目。");
                var relativePath = NormalizeArchivePath(entryKey);
                if (!names.Add(relativePath))
                    throw new UnsafeArchiveException($"补丁包含重复路径：{relativePath}");

                totalBytes = checked(totalBytes + entry.Size);
                if (totalBytes > _options.MaxArchiveBytes)
                    throw new UnsafeArchiveException("补丁解压后的总大小超出安全限制。");

                await using var entryStream = entry.OpenEntryStream();
                var hash = await Hashing.Sha256Async(entryStream, cancellationToken).ConfigureAwait(false);
                files.Add(new PatchFileEntry(relativePath, entry.Size, hash));
            }
        }
        catch (PatcherException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            throw new UnsafeArchiveException($"无法读取补丁压缩包：{ex.Message}");
        }

        return new PatchManifest(archivePath, archiveHash, DateTimeOffset.UtcNow, files.AsReadOnly());
    }

    public async Task CopyEntryToAsync(
        PatchManifest manifest,
        PatchFileEntry expected,
        string targetPath,
        string gameRootWithSeparator,
        CancellationToken cancellationToken)
    {
        var currentArchiveHash = await Hashing.Sha256Async(manifest.ArchivePath, cancellationToken).ConfigureAwait(false);
        if (!Hashing.EqualsHash(currentArchiveHash, manifest.ArchiveSha256))
            throw new PatcherException("补丁压缩包在扫描后发生了变化，请重新扫描。");

        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrEmpty(targetDirectory))
            throw new PatcherException($"目标路径无效：{targetPath}");
        Directory.CreateDirectory(targetDirectory);
        PathSafety.EnsureParentDirectoriesAreSafe(gameRootWithSeparator, targetPath);
        PathSafety.EnsureNotReparsePoint(targetPath);

        var temp = targetPath + ".ronpatcher-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var archive = OpenArchive(manifest.ArchivePath);
            var entry = archive.Entries.FirstOrDefault(e =>
                    !e.IsDirectory && e.Key is not null && string.Equals(NormalizeArchivePath(e.Key), expected.RelativePath,
                        StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                    throw new PatcherException($"补丁条目已消失：{expected.RelativePath}");
                if (entry.Size != expected.Length)
                    throw new PatcherException($"补丁条目已变化：{expected.RelativePath}");

            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 128 * 1024, options: FileOptions.SequentialScan | FileOptions.Asynchronous))
            await using (var input = entry.OpenEntryStream())
            {
                await input.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            var hash = await Hashing.Sha256Async(temp, cancellationToken).ConfigureAwait(false);
            if (!Hashing.EqualsHash(hash, expected.Sha256))
                throw new PatcherException($"补丁文件哈希校验失败：{expected.RelativePath}");

            File.Move(temp, targetPath, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static IArchive OpenArchive(string path)
        => ArchiveFactory.OpenArchive(path, ReaderOptions.ForFilePath);

    private static string NormalizeArchivePath(string value)
    {
        // This archive was produced with a legacy GBK filename flag. .NET's
        // ZipArchive exposes its two Chinese root names as replacement chars;
        // map only those known names, then apply the normal traversal checks.
        var normalized = value switch
        {
            "������Ϸ.exe" => "启动游戏.exe",
            "˵��.txt" => "说明.txt",
            _ => value
        };
        return PathSafety.NormalizeRelative(normalized);
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
            // A failed cleanup must not hide the original operation error.
        }
    }
}
