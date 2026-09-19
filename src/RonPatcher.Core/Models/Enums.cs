namespace RonPatcher.Core;

/// <summary>The state represented by the files managed by the patch.</summary>
public enum RonMode
{
    Unknown,
    Official,
    Lan,
    Mixed
}

/// <summary>Overall confidence in the result of a scan.</summary>
public enum IntegrityStatus
{
    Unknown,
    Intact,
    Missing,
    Modified,
    Conflict
}

/// <summary>Comparison result for one file in the patch manifest.</summary>
public enum ManagedFileState
{
    Missing,
    Original,
    Patch,
    Modified,
    Conflict,
    Unmanaged
}
