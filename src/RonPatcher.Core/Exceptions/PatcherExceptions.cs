namespace RonPatcher.Core;

public class PatcherException : Exception
{
    public PatcherException(string message) : base(message) { }
    public PatcherException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class UnsafeArchiveException : PatcherException
{
    public UnsafeArchiveException(string message) : base(message) { }
}

public sealed class BaselineRequiredException : PatcherException
{
    public BaselineRequiredException(string message) : base(message) { }
}

public sealed class BaselineMismatchException : PatcherException
{
    public BaselineMismatchException(string message) : base(message) { }
}

public sealed class ConflictException : PatcherException
{
    public IReadOnlyList<string> Paths { get; }

    public ConflictException(string message, IReadOnlyList<string>? paths = null) : base(message)
    {
        Paths = paths ?? Array.Empty<string>();
    }
}

public sealed class GameRunningException : PatcherException
{
    public IReadOnlyList<RunningProcessInfo> Processes { get; }

    public GameRunningException(string message, IReadOnlyList<RunningProcessInfo>? processes = null) : base(message)
    {
        Processes = processes ?? Array.Empty<RunningProcessInfo>();
    }
}

public sealed class StateCorruptException : PatcherException
{
    public StateCorruptException(string message, Exception? innerException = null)
        : base(message, innerException ?? new InvalidDataException(message)) { }
}
