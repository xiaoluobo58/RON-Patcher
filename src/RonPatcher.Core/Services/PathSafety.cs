using System.Runtime.InteropServices;

namespace RonPatcher.Core;

internal static class PathSafety
{
    public static string FullRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("A game root is required.", nameof(root));

        var full = Path.GetFullPath(root.Trim());
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
    }

    public static string NormalizeRelative(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new UnsafeArchiveException("The archive contains an empty path.");
        if (value.Contains('\0', StringComparison.Ordinal))
            throw new UnsafeArchiveException("The archive contains a NUL character in a path.");

        var normalized = value.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.StartsWith("//", StringComparison.Ordinal) ||
            Path.IsPathRooted(normalized) || normalized.Contains(':', StringComparison.Ordinal))
            throw new UnsafeArchiveException($"Absolute or alternate-stream path is not allowed: {value}");

        var parts = normalized.Split('/', StringSplitOptions.None);
        if (parts.Length == 0)
            throw new UnsafeArchiveException($"Invalid archive path: {value}");

        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or "..")
                throw new UnsafeArchiveException($"Traversal or empty path component is not allowed: {value}");
            if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new UnsafeArchiveException($"Invalid filename component: {value}");
        }

        return string.Join('/', parts);
    }

    public static string ResolveUnderRoot(string rootWithSeparator, string relativePath)
    {
        var relative = NormalizeRelative(relativePath);
        var native = relative.Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(rootWithSeparator, native));
        if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new UnsafeArchiveException($"Path escapes the game directory: {relativePath}");
        return full;
    }

    public static void EnsureNotReparsePoint(string path)
    {
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new PatcherException($"Refusing to modify a reparse-point file: {path}");
    }

    public static void EnsureParentDirectoriesAreSafe(string rootWithSeparator, string target)
    {
        var directory = Path.GetDirectoryName(target);
        while (!string.IsNullOrEmpty(directory) &&
               directory.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(directory, rootWithSeparator.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(directory) && File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                throw new PatcherException($"Refusing to modify through a reparse-point directory: {directory}");
            directory = Path.GetDirectoryName(directory);
        }
    }

    public static string SafeId(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return new string(chars);
    }
}
