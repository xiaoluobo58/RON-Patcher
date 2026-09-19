using System.Diagnostics;

namespace RonPatcher.Core;

internal sealed class GameProcessDetector
{
    private readonly string _gameRoot;

    public GameProcessDetector(string gameRootWithSeparator)
    {
        _gameRoot = gameRootWithSeparator.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public IReadOnlyList<RunningProcessInfo> GetRunningProcesses()
    {
        var result = new List<RunningProcessInfo>();
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return result;
        }

        foreach (var process in processes)
        {
            try
            {
                string? path = null;
                try { path = process.MainModule?.FileName; } catch { /* protected process */ }

                var name = process.ProcessName;
                var knownName = name.Equals("ReadyOrNot", StringComparison.OrdinalIgnoreCase) ||
                                name.Equals("ReadyOrNot-Win64-Shipping", StringComparison.OrdinalIgnoreCase) ||
                                name.Equals("ColdClientLoader", StringComparison.OrdinalIgnoreCase) ||
                                name.Equals("启动游戏", StringComparison.OrdinalIgnoreCase);
                var underRoot = path is not null &&
                                path.StartsWith(_gameRoot + Path.DirectorySeparatorChar,
                                    StringComparison.OrdinalIgnoreCase);
                if (knownName || underRoot)
                    result.Add(new RunningProcessInfo(process.Id, name, path));
            }
            catch
            {
                // A process can exit while being inspected.
            }
            finally
            {
                process.Dispose();
            }
        }

        return result;
    }

    public void ThrowIfRunning()
    {
        var processes = GetRunningProcesses();
        if (processes.Count > 0)
            throw new GameRunningException("The game or its loader is still running.", processes);
    }
}
