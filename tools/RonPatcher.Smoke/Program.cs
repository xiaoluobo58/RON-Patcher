using RonPatcher.Core;

var archive = args.Length > 0 ? Path.GetFullPath(args[0]) : FindArchive();
if (!File.Exists(archive))
    throw new FileNotFoundException("Patch archive not found.", archive);

var root = Path.Combine(Path.GetTempPath(), "ron-patcher-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
File.WriteAllBytes(Path.Combine(root, "ReadyOrNot.exe"), [0x4D, 0x5A, 0x90, 0x00]);

Console.WriteLine($"fixture={root}");
using var engine = new RonPatcherEngine(new RonPatcherOptions(root, archive));

Console.WriteLine("before scan");
var initial = await engine.ScanAsync();
Console.WriteLine("after scan");
Assert(initial.Mode == RonMode.Official, $"initial mode was {initial.Mode}");
Assert(initial.Integrity == IntegrityStatus.Unknown, $"initial integrity was {initial.Integrity}");

var lan = await engine.SwitchToLanAsync();
Assert(lan.Success && lan.Mode == RonMode.Lan, "LAN switch failed");
Assert(lan.Scan?.Integrity == IntegrityStatus.Intact, "LAN post-scan was not intact");

var official = await engine.SwitchToOfficialAsync();
Assert(official.Success && official.Mode == RonMode.Official, "official switch failed");
Assert(official.Scan?.Integrity == IntegrityStatus.Intact, "official post-scan was not intact");

var rollback = await engine.RollbackAsync();
Assert(rollback.Success && rollback.Mode == RonMode.Lan, "rollback did not restore the LAN snapshot");

Console.WriteLine("PASS: scan -> LAN -> official -> rollback");

static string FindArchive()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    for (var i = 0; i < 7 && directory is not null; i++, directory = directory.Parent)
    {
        var file = directory.GetFiles("*.zip").OrderByDescending(item => item.LastWriteTimeUtc).FirstOrDefault();
        if (file is not null)
            return file.FullName;
    }

    return string.Empty;
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
