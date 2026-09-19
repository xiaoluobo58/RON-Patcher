using System.Collections.ObjectModel;

namespace RonPatcher.Core;

/// <summary>Configuration for one game installation and one patch archive.</summary>
public sealed record RonPatcherOptions(
    string GameRoot,
    string PatchArchivePath,
    string? DataRoot = null,
    uint AppId = 1144200)
{
    /// <summary>Maximum number of files accepted from a patch archive.</summary>
    public int MaxArchiveEntries { get; init; } = 4096;

    /// <summary>Maximum uncompressed size of one archive entry.</summary>
    public long MaxEntryBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Maximum total uncompressed size of an archive.</summary>
    public long MaxArchiveBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>When true, launcher paths are allowed to be absent during a scan.</summary>
    public bool AllowMissingLauncher { get; init; }
}

public sealed record PatchFileEntry(
    string RelativePath,
    long Length,
    string Sha256);

public sealed record PatchManifest(
    string ArchivePath,
    string ArchiveSha256,
    DateTimeOffset ReadAt,
    IReadOnlyList<PatchFileEntry> Files)
{
    public int FileCount => Files.Count;
}

public sealed record ManagedFileStatus(
    string RelativePath,
    ManagedFileState State,
    bool Exists,
    long? ActualLength,
    string? ActualSha256,
    string PatchSha256,
    string? OriginalSha256);

public sealed record ScanResult(
    RonMode Mode,
    IntegrityStatus Integrity,
    IReadOnlyList<ManagedFileStatus> Files,
    bool HasPatchResidue,
    bool HasBaseline,
    DateTimeOffset ScannedAt,
    string Message)
{
    public bool IsSafe => Integrity == IntegrityStatus.Intact;

    public int PatchFileCount => Files.Count(static f => f.State == ManagedFileState.Patch);

    public int OriginalFileCount => Files.Count(static f => f.State == ManagedFileState.Original);

    public int ConflictCount => Files.Count(static f => f.State is ManagedFileState.Modified or ManagedFileState.Conflict);
}

/// <summary>One original file captured before a patch operation.</summary>
public sealed record BaselineFile(
    string RelativePath,
    bool Existed,
    long Length,
    string Sha256,
    string BackupPath);

public sealed record BaselineInfo(
    string Id,
    string ArchiveSha256,
    DateTimeOffset CreatedAt,
    IReadOnlyList<BaselineFile> Files)
{
    public bool Contains(string relativePath) =>
        Files.Any(f => string.Equals(f.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Metadata for a pre-operation snapshot retained for rollback.</summary>
public sealed record TransactionSnapshotInfo(
    string Id,
    string Operation,
    DateTimeOffset CreatedAt,
    IReadOnlyList<BaselineFile> Files);

public sealed record LastOperationInfo(
    string Operation,
    bool Succeeded,
    RonMode Mode,
    DateTimeOffset At,
    string Message,
    string? TransactionId = null);

public sealed class AppState
{
    public int SchemaVersion { get; set; } = 1;
    public string GameRoot { get; set; } = string.Empty;
    public string PatchArchivePath { get; set; } = string.Empty;
    public uint AppId { get; set; } = 1144200;
    public BaselineInfo? Baseline { get; set; }
    public TransactionSnapshotInfo? LastTransaction { get; set; }
    public LastOperationInfo? LastOperation { get; set; }
    public ScanSummary? LastScan { get; set; }
    public List<string> QuarantinedFiles { get; set; } = new();
}

public sealed record ScanSummary(
    RonMode Mode,
    IntegrityStatus Integrity,
    bool HasPatchResidue,
    DateTimeOffset ScannedAt,
    string Message);

public sealed record OperationResult(
    bool Success,
    string Operation,
    RonMode Mode,
    string Message,
    ScanResult? Scan = null,
    string? TransactionId = null);

public sealed record LaunchResult(
    bool Success,
    string Target,
    int? ProcessId,
    string Message);

public sealed record RunningProcessInfo(
    int ProcessId,
    string Name,
    string? Path);
