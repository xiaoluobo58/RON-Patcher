using System.Diagnostics;
using System.Globalization;

namespace RonPatcher.Core;

/// <summary>
/// Coordinates scanning, backup, switching and launching for one game directory.
/// All writes are local and are guarded by a transaction snapshot.
/// </summary>
public sealed class RonPatcherEngine : IDisposable
{
    public static IReadOnlyList<string> KnownManagedPaths { get; } =
    [
        "ColdClientLoader.ini",
        "Engine/Binaries/ThirdParty/Steamworks/Steamv153/Win64/steam_api64.dll",
        "ReadyOrNot/Binaries/Win64/Custom.dll",
        "ReadyOrNot/Binaries/Win64/dlllist.txt",
        "ReadyOrNot/Binaries/Win64/OnlineFix.ini",
        "ReadyOrNot/Binaries/Win64/OnlineFix.url",
        "ReadyOrNot/Binaries/Win64/OnlineFix64.dll",
        "ReadyOrNot/Binaries/Win64/RedpointEOS/EOSSDK-Win64-Shipping.dll",
        "ReadyOrNot/Binaries/Win64/RedpointEOS/EOSSDK-Win64-Shipping.of",
        "ReadyOrNot/Binaries/Win64/winmm.dll",
        "steam_api64.dll",
        "steam_settings/disable_lan_only.txt",
        "steam_settings/DLC.txt",
        "steamclient.dll",
        "steamclient64.dll",
        "启动游戏.exe",
        "说明.txt"
    ];

    private static readonly string[] RequiredPatchFiles =
    [
        "ColdClientLoader.ini",
        "启动游戏.exe",
        "steamclient.dll",
        "steamclient64.dll",
        "ReadyOrNot/Binaries/Win64/OnlineFix64.dll"
    ];

    private static readonly string[] PatchMarkerFiles =
    [
        "ColdClientLoader.ini",
        "启动游戏.exe",
        "steam_settings/disable_lan_only.txt",
        "ReadyOrNot/Binaries/Win64/OnlineFix64.dll",
        "ReadyOrNot/Binaries/Win64/Custom.dll",
        "ReadyOrNot/Binaries/Win64/dlllist.txt",
        "ReadyOrNot/Binaries/Win64/OnlineFix.ini",
        "ReadyOrNot/Binaries/Win64/OnlineFix.url"
    ];

    private readonly RonPatcherOptions _options;
    private readonly string _gameRoot;
    private readonly string _gameRootWithoutSeparator;
    private readonly string _dataRoot;
    private readonly StateStore _stateStore;
    private readonly PatchArchiveReader _archiveReader;
    private readonly GameProcessDetector _processDetector;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppState _state = new();
    private PatchManifest? _manifest;
    private bool _loaded;

    public RonPatcherEngine(RonPatcherOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _gameRoot = PathSafety.FullRoot(options.GameRoot);
        _gameRootWithoutSeparator = _gameRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _dataRoot = Path.GetFullPath(options.DataRoot ?? Path.Combine(_gameRootWithoutSeparator, ".ron-patcher"));
        if (!_dataRoot.StartsWith(_gameRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The data root must be inside the game directory.", nameof(options));

        _stateStore = new StateStore(_dataRoot);
        _archiveReader = new PatchArchiveReader(options with { GameRoot = _gameRootWithoutSeparator });
        _processDetector = new GameProcessDetector(_gameRoot);
    }

    public AppState State => _state;

    public void Dispose()
    {
        _gate.Dispose();
    }

    public async Task<PatchManifest> ReadPatchManifestAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            return await LoadManifestAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            return await ScanCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>选择未修改的正版目录后，立即备份已知受管路径并计算原版哈希。</summary>
    public async Task<OperationResult> BackupOriginalFilesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            _processDetector.ThrowIfRunning();
            ValidateGameRoot();

            if (_state.Baseline is not null)
                return new OperationResult(true, "备份原版游戏文件", RonMode.Official, "原版游戏文件已经备份。", null);

            var knownPatchResidue = PatchMarkerFiles.Any(relative =>
                File.Exists(PathSafety.ResolveUnderRoot(_gameRoot, relative)));
            if (knownPatchResidue)
                throw new BaselineRequiredException("所选目录存在联机补丁文件。首次设置必须选择未修改的正版游戏目录，请先通过 Steam 验证游戏文件。");

            var placeholderFiles = KnownManagedPaths
                .Select(relative => new PatchFileEntry(relative, 0, string.Empty))
                .ToArray();
            var placeholderManifest = new PatchManifest(string.Empty, string.Empty, DateTimeOffset.UtcNow, placeholderFiles);
            await CreateBaselineAsync(placeholderManifest, cancellationToken).ConfigureAwait(false);
            _state.LastOperation = new LastOperationInfo("备份原版游戏文件", true, RonMode.Official,
                DateTimeOffset.UtcNow, "已备份受管的原版游戏文件并计算哈希。");
            await SaveStateAsync(cancellationToken).ConfigureAwait(false);
            return new OperationResult(true, "备份原版游戏文件", RonMode.Official,
                "原版游戏文件已备份。现在请选择补丁压缩包。", null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>首次设置时确认目录未被修改，并立即备份补丁将覆盖的原版游戏文件。</summary>
    public async Task<OperationResult> InitializeOriginalFilesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var manifest = await LoadManifestAsync(cancellationToken).ConfigureAwait(false);
            var scan = await ScanCoreAsync(cancellationToken).ConfigureAwait(false);
            _processDetector.ThrowIfRunning();

            if (_state.Baseline is not null)
                return new OperationResult(true, "备份原版游戏文件", scan.Mode, "原版游戏文件已经备份。", scan);
            if (scan.Mode != RonMode.Official || scan.HasPatchResidue)
                throw new BaselineRequiredException("所选目录包含补丁文件或修改痕迹。首次设置必须选择未修改的正版游戏目录，请先通过 Steam 验证游戏文件。");

            await CreateBaselineAsync(manifest, cancellationToken).ConfigureAwait(false);
            var post = await ScanCoreAsync(cancellationToken).ConfigureAwait(false);
            _state.LastOperation = new LastOperationInfo("备份原版游戏文件", true, RonMode.Official,
                DateTimeOffset.UtcNow, "已备份补丁会覆盖的原版游戏文件。");
            await SaveStateAsync(cancellationToken).ConfigureAwait(false);
            return new OperationResult(true, "备份原版游戏文件", RonMode.Official,
                "原版游戏文件已备份，现在可以安全切换到局域网模式。", post);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult> SwitchToLanAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var manifest = await LoadManifestAsync(cancellationToken).ConfigureAwait(false);
            var scan = await ScanCoreAsync(cancellationToken).ConfigureAwait(false);
            _processDetector.ThrowIfRunning();

            if (scan.Mode == RonMode.Lan && scan.Integrity == IntegrityStatus.Intact)
                return new OperationResult(true, "切换到局域网", RonMode.Lan, "当前已经是完整的局域网模式。", scan);

            if (_state.Baseline is null)
            {
                if (scan.Mode == RonMode.Mixed || scan.HasPatchResidue || scan.Mode == RonMode.Lan)
                    throw new BaselineRequiredException("检测到补丁残留，但还没有原版游戏文件备份。请先在 Steam 中验证游戏文件，再重新完成首次设置。未写入任何文件。");

                await CreateBaselineAsync(manifest, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                EnsureBaselineMatches(manifest);
            }

            var transaction = await CreateTransactionAsync(manifest, "切换到局域网", cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var file in manifest.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var target = PathSafety.ResolveUnderRoot(_gameRoot, file.RelativePath);
                    MakeWritableIfNeeded(target);
                    await _archiveReader.CopyEntryToAsync(manifest, file, target, _gameRoot, cancellationToken).ConfigureAwait(false);
                }

                var post = await ScanCoreAsync(cancellationToken).ConfigureAwait(false);
                _state.LastOperation = new LastOperationInfo("切换到局域网", true, RonMode.Lan, DateTimeOffset.UtcNow,
                    "补丁文件已写入。", transaction.Id);
                await SaveStateAsync(cancellationToken).ConfigureAwait(false);
                return new OperationResult(true, "切换到局域网", RonMode.Lan, "已切换到局域网模式。", post, transaction.Id);
            }
            catch
            {
                await RestoreSnapshotAsync(transaction, "切换失败回滚", cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult> SwitchToOfficialAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var manifest = await LoadManifestAsync(cancellationToken).ConfigureAwait(false);
            EnsureBaselineMatches(manifest);
            var scan = await ScanCoreAsync(cancellationToken).ConfigureAwait(false);
            _processDetector.ThrowIfRunning();

            if (scan.Mode == RonMode.Official && scan.Integrity == IntegrityStatus.Intact)
                return new OperationResult(true, "切回正版", RonMode.Official, "当前已经是完整的正版模式。", scan);

            var transaction = await CreateTransactionAsync(manifest, "切回正版", cancellationToken).ConfigureAwait(false);
            try
            {
                await RestoreBaselineAsync(manifest, transaction.Id, cancellationToken).ConfigureAwait(false);
                var post = await ScanCoreAsync(cancellationToken).ConfigureAwait(false);
                _state.LastOperation = new LastOperationInfo("切回正版", true, RonMode.Official, DateTimeOffset.UtcNow,
                    "已从备份恢复原版游戏文件。", transaction.Id);
                await SaveStateAsync(cancellationToken).ConfigureAwait(false);
                return new OperationResult(true, "切回正版", RonMode.Official, "已恢复正版文件。", post, transaction.Id);
            }
            catch
            {
                await RestoreSnapshotAsync(transaction, "恢复失败回滚", cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult> RepairAsync(RonMode targetMode = RonMode.Unknown, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var manifest = await LoadManifestAsync(cancellationToken).ConfigureAwait(false);
            var scan = await ScanCoreAsync(cancellationToken).ConfigureAwait(false);
            targetMode = targetMode is RonMode.Lan or RonMode.Official
                ? targetMode
                : _state.LastOperation?.Mode ?? RonMode.Unknown;

            if (targetMode == RonMode.Unknown || targetMode == RonMode.Mixed)
                throw new ConflictException("无法从混合状态推断修复目标，请先点击“切换到局域网”或“切回正版”。");
            _processDetector.ThrowIfRunning();
            EnsureBaselineMatches(manifest);

            var transaction = await CreateTransactionAsync(manifest, "修复", cancellationToken).ConfigureAwait(false);
            try
            {
                if (targetMode == RonMode.Lan)
                {
                    foreach (var file in manifest.Files)
                    {
                        var target = PathSafety.ResolveUnderRoot(_gameRoot, file.RelativePath);
                        MakeWritableIfNeeded(target);
                        await _archiveReader.CopyEntryToAsync(manifest, file, target, _gameRoot, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    await RestoreBaselineAsync(manifest, transaction.Id, cancellationToken).ConfigureAwait(false);
                }

                var post = await ScanCoreAsync(cancellationToken).ConfigureAwait(false);
                _state.LastOperation = new LastOperationInfo("修复", true, targetMode, DateTimeOffset.UtcNow,
                    "文件已按目标模式重新写入。", transaction.Id);
                await SaveStateAsync(cancellationToken).ConfigureAwait(false);
                return new OperationResult(true, "修复", targetMode, "已修复当前模式。", post, transaction.Id);
            }
            catch
            {
                await RestoreSnapshotAsync(transaction, "修复失败回滚", cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult> RollbackAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            _processDetector.ThrowIfRunning();
            var transaction = _state.LastTransaction
                ?? throw new PatcherException("没有可回滚的事务快照。");

            await RestoreSnapshotAsync(transaction, "手动回滚", cancellationToken).ConfigureAwait(false);
            var post = await ScanCoreAsync(cancellationToken).ConfigureAwait(false);
            _state.LastTransaction = null;
            _state.LastOperation = new LastOperationInfo("回滚", true, post.Mode, DateTimeOffset.UtcNow,
                "已恢复上一次操作之前的文件状态。");
            await SaveStateAsync(cancellationToken).ConfigureAwait(false);
            return new OperationResult(true, "回滚", post.Mode, "已回滚上一次操作。", post);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LaunchResult> LaunchLanAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var scan = await ScanCoreAsync(cancellationToken).ConfigureAwait(false);
            if (scan.Mode != RonMode.Lan || scan.Integrity != IntegrityStatus.Intact)
                throw new ConflictException("只有完整的局域网模式才能启动。");

            var loader = PathSafety.ResolveUnderRoot(_gameRoot, "启动游戏.exe");
            if (!File.Exists(loader))
                throw new PatcherException("找不到补丁提供的启动游戏.exe。");
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = loader,
                WorkingDirectory = _gameRootWithoutSeparator,
                UseShellExecute = false,
                CreateNoWindow = false
            });
            if (process is null)
                return new LaunchResult(false, loader, null, "无法创建 ColdClientLoader 进程。");
            return new LaunchResult(true, loader, process.Id, "已通过游戏根目录的 ColdClientLoader 启动局域网模式。");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LaunchResult> LaunchOfficialAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var scan = await ScanCoreAsync(cancellationToken).ConfigureAwait(false);
            if (scan.Mode != RonMode.Official || (scan.Integrity != IntegrityStatus.Intact && scan.Integrity != IntegrityStatus.Unknown))
                throw new ConflictException("只有正版模式才能通过 Steam 启动。");

            var uri = $"steam://run/{_options.AppId}";
            var process = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return process is null
                ? new LaunchResult(false, uri, null, "无法打开 Steam 启动链接。")
                : new LaunchResult(true, uri, process.Id, "已请求 Steam 启动 Ready or Not。");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
            return;

        _state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        _state.GameRoot = _gameRootWithoutSeparator;
        _state.PatchArchivePath = string.IsNullOrWhiteSpace(_options.PatchArchivePath)
            ? string.Empty
            : Path.GetFullPath(_options.PatchArchivePath);
        _loaded = true;
    }

    private async Task<PatchManifest> LoadManifestAsync(CancellationToken cancellationToken)
    {
        var manifest = await _archiveReader.ReadManifestAsync(cancellationToken).ConfigureAwait(false);
        ValidateManifest(manifest);
        _manifest = manifest;
        return manifest;
    }

    private static void ValidateManifest(PatchManifest manifest)
    {
        var paths = manifest.Files.Select(file => file.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = RequiredPatchFiles.Where(required => !paths.Contains(required)).ToArray();
        if (missing.Length > 0)
            throw new PatcherException("补丁压缩包缺少必要文件：" + string.Join(", ", missing));

        foreach (var file in manifest.Files)
        {
            if (file.RelativePath.Equals(".ron-patcher", StringComparison.OrdinalIgnoreCase) ||
                file.RelativePath.StartsWith(".ron-patcher/", StringComparison.OrdinalIgnoreCase))
                throw new UnsafeArchiveException("补丁压缩包不能写入 .ron-patcher 数据目录。");
        }
    }

    private async Task<ScanResult> ScanCoreAsync(CancellationToken cancellationToken)
    {
        ValidateGameRoot();

        if (string.IsNullOrWhiteSpace(_options.PatchArchivePath))
            throw new PatcherException("尚未选择补丁压缩包。");

        var manifest = _manifest ?? await LoadManifestAsync(cancellationToken).ConfigureAwait(false);
        var baseline = _state.Baseline;
        var baselineByPath = baseline?.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, BaselineFile>(StringComparer.OrdinalIgnoreCase);
        var statuses = new List<ManagedFileStatus>(manifest.Files.Count);
        var allPatch = manifest.Files.Count > 0;
        var allOriginal = baseline is not null && manifest.Files.Count > 0;

        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = PathSafety.ResolveUnderRoot(_gameRoot, file.RelativePath);
            var original = baselineByPath.TryGetValue(file.RelativePath, out var originalRecord) ? originalRecord : null;
            if (!File.Exists(target))
            {
                var matchesOriginal = original is not null && !original.Existed;
                allPatch = false;
                allOriginal &= matchesOriginal;
                var state = matchesOriginal ? ManagedFileState.Original : ManagedFileState.Missing;
                statuses.Add(new ManagedFileStatus(file.RelativePath, state, false, null, null, file.Sha256,
                    original?.Existed == true ? original.Sha256 : null));
                continue;
            }

            PathSafety.EnsureNotReparsePoint(target);
            var actual = await Hashing.Sha256Async(target, cancellationToken).ConfigureAwait(false);
            var isPatch = Hashing.EqualsHash(actual, file.Sha256);
            var isOriginal = original?.Existed == true && Hashing.EqualsHash(actual, original.Sha256);
            allPatch &= isPatch;
            allOriginal &= isOriginal;
            ManagedFileState status;
            if (isOriginal && !isPatch)
            {
                status = ManagedFileState.Original;
            }
            else if (isPatch && !isOriginal)
            {
                status = ManagedFileState.Patch;
            }
            else if (isPatch && isOriginal)
            {
                status = _state.LastOperation?.Mode == RonMode.Lan
                    ? ManagedFileState.Patch
                    : ManagedFileState.Original;
            }
            else
            {
                status = original is null ? ManagedFileState.Unmanaged : ManagedFileState.Modified;
            }

            statuses.Add(new ManagedFileStatus(file.RelativePath, status, true, new FileInfo(target).Length, actual,
                file.Sha256, original?.Existed == true ? original.Sha256 : null));
        }

        var markerResidue = await HasPatchMarkerResidueAsync(manifest, cancellationToken).ConfigureAwait(false);
        var baselineMismatch = baseline is not null && !HasSameManagedPaths(baseline, manifest);
        var mode = baselineMismatch
            ? RonMode.Mixed
            : allPatch
            ? RonMode.Lan
            : allOriginal
                ? RonMode.Official
                : markerResidue
                    ? RonMode.Mixed
                    : RonMode.Official;

        var conflictCount = statuses.Count(file => file.State is ManagedFileState.Modified or ManagedFileState.Conflict or ManagedFileState.Unmanaged);
        var missingCount = statuses.Count(file => file.State == ManagedFileState.Missing);
        var integrity = baselineMismatch
            ? IntegrityStatus.Conflict
            : baseline is null && mode == RonMode.Official
                ? IntegrityStatus.Unknown
            : mode == RonMode.Lan && allPatch || mode == RonMode.Official && allOriginal
            ? IntegrityStatus.Intact
            : missingCount > 0
                ? IntegrityStatus.Missing
                : mode == RonMode.Mixed || conflictCount > 0
                    ? IntegrityStatus.Conflict
                    : IntegrityStatus.Modified;

        var message = baselineMismatch
            ? "补丁压缩包与原版游戏文件备份不匹配；为避免误恢复，当前切换已锁定。"
            : mode switch
        {
            RonMode.Lan => "所有受管文件与补丁压缩包一致。",
            RonMode.Official when baseline is null => "未发现补丁残留；尚未备份原版游戏文件。",
            RonMode.Official => "所有受管文件与原版游戏文件备份一致。",
            RonMode.Mixed => baseline is null
                ? "检测到补丁文件或混合状态，但没有原版游戏文件备份；请先用 Steam 验证文件。"
                : "检测到补丁与原版文件混合，请选择目标模式或执行修复。",
            _ => "无法判断当前文件状态。"
        };

        _state.LastScan = new ScanSummary(mode, integrity, markerResidue, DateTimeOffset.UtcNow, message);
        await SaveStateAsync(cancellationToken).ConfigureAwait(false);
        return new ScanResult(mode, integrity, statuses.AsReadOnly(), markerResidue, baseline is not null,
            DateTimeOffset.UtcNow, message);
    }

    private void ValidateGameRoot()
    {
        if (!Directory.Exists(_gameRootWithoutSeparator))
            throw new PatcherException("游戏目录不存在。");
        if (!File.Exists(Path.Combine(_gameRootWithoutSeparator, "ReadyOrNot.exe")))
            throw new PatcherException("所选目录不是 Ready Or Not 根目录（找不到 ReadyOrNot.exe）。");
    }

    private async Task<bool> HasPatchMarkerResidueAsync(PatchManifest manifest, CancellationToken cancellationToken)
    {
        foreach (var relative in PatchMarkerFiles)
        {
            var target = PathSafety.ResolveUnderRoot(_gameRoot, relative);
            if (!File.Exists(target))
                continue;

            cancellationToken.ThrowIfCancellationRequested();
            var patch = manifest.Files.FirstOrDefault(file => file.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase));
            if (patch is null)
                return true;

            var actual = await Hashing.Sha256Async(target, cancellationToken).ConfigureAwait(false);
            if (Hashing.EqualsHash(actual, patch.Sha256))
                return true;
        }

        return false;
    }

    private async Task CreateBaselineAsync(PatchManifest manifest, CancellationToken cancellationToken)
    {
        var id = CreateId("baseline");
        var directory = Path.Combine(_dataRoot, "baseline", id);
        var filesDirectory = Path.Combine(directory, "files");
        Directory.CreateDirectory(filesDirectory);
        var records = new List<BaselineFile>(manifest.Files.Count);
        try
        {
            foreach (var file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = PathSafety.ResolveUnderRoot(_gameRoot, file.RelativePath);
                var backupRelative = Path.Combine("baseline", id, "files", file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(source))
                {
                    records.Add(new BaselineFile(file.RelativePath, false, 0, string.Empty, string.Empty));
                    continue;
                }

                PathSafety.EnsureNotReparsePoint(source);
                var backupPath = Path.Combine(_dataRoot, "baseline", id, "files",
                    file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                await CopySnapshotFileAsync(source, backupPath, cancellationToken).ConfigureAwait(false);
                var info = new FileInfo(source);
                var hash = await Hashing.Sha256Async(source, cancellationToken).ConfigureAwait(false);
                records.Add(new BaselineFile(file.RelativePath, true, info.Length, hash, backupRelative));
            }

            var baseline = new BaselineInfo(id, manifest.ArchiveSha256, DateTimeOffset.UtcNow, records.AsReadOnly());
            _state.Baseline = baseline;
            await SaveStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteDirectory(directory);
            throw;
        }
    }

    private async Task<TransactionSnapshotInfo> CreateTransactionAsync(PatchManifest manifest, string operation, CancellationToken cancellationToken)
    {
        var id = CreateId("transaction");
        var directory = Path.Combine(_dataRoot, "transactions", id, "files");
        Directory.CreateDirectory(directory);
        var records = new List<BaselineFile>(manifest.Files.Count);
        try
        {
            foreach (var file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = PathSafety.ResolveUnderRoot(_gameRoot, file.RelativePath);
                var backupRelative = Path.Combine("transactions", id, "files", file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(source))
                {
                    records.Add(new BaselineFile(file.RelativePath, false, 0, string.Empty, string.Empty));
                    continue;
                }

                PathSafety.EnsureNotReparsePoint(source);
                var backupPath = Path.Combine(_dataRoot, "transactions", id, "files",
                    file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                await CopySnapshotFileAsync(source, backupPath, cancellationToken).ConfigureAwait(false);
                var info = new FileInfo(source);
                var hash = await Hashing.Sha256Async(source, cancellationToken).ConfigureAwait(false);
                records.Add(new BaselineFile(file.RelativePath, true, info.Length, hash, backupRelative));
            }

            var transaction = new TransactionSnapshotInfo(id, operation, DateTimeOffset.UtcNow, records.AsReadOnly());
            _state.LastTransaction = transaction;
            await SaveStateAsync(cancellationToken).ConfigureAwait(false);
            return transaction;
        }
        catch
        {
            TryDeleteDirectory(Path.Combine(_dataRoot, "transactions", id));
            throw;
        }
    }

    private async Task RestoreBaselineAsync(PatchManifest manifest, string transactionId, CancellationToken cancellationToken)
    {
        var baseline = _state.Baseline ?? throw new BaselineRequiredException("没有可用的原版游戏文件备份。");
        var baselineByPath = baseline.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        var quarantineRoot = Path.Combine(_dataRoot, "quarantine", transactionId);

        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = PathSafety.ResolveUnderRoot(_gameRoot, file.RelativePath);
            if (baselineByPath.TryGetValue(file.RelativePath, out var original) && original.Existed)
            {
                var source = ResolveDataRelative(original.BackupPath);
                MakeWritableIfNeeded(target);
                await AtomicFile.CopyAsync(source, target, _gameRoot, expectedHash: null, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else if (File.Exists(target))
            {
                MakeWritableIfNeeded(target);
                var quarantine = Path.Combine(quarantineRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                await AtomicFile.MoveToQuarantineAsync(target, quarantine, _gameRoot, cancellationToken).ConfigureAwait(false);
                _state.QuarantinedFiles.Add(Path.GetRelativePath(_gameRootWithoutSeparator, quarantine));
            }
        }
    }

    private async Task RestoreSnapshotAsync(TransactionSnapshotInfo transaction, string reason, CancellationToken cancellationToken)
    {
        var quarantineRoot = Path.Combine(_dataRoot, "quarantine", "rollback_" + transaction.Id);
        foreach (var record in transaction.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = PathSafety.ResolveUnderRoot(_gameRoot, record.RelativePath);
            if (record.Existed)
            {
                var source = ResolveDataRelative(record.BackupPath);
                MakeWritableIfNeeded(target);
                await AtomicFile.CopyAsync(source, target, _gameRoot, expectedHash: null, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else if (File.Exists(target))
            {
                MakeWritableIfNeeded(target);
                var quarantine = Path.Combine(quarantineRoot, record.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                await AtomicFile.MoveToQuarantineAsync(target, quarantine, _gameRoot, cancellationToken).ConfigureAwait(false);
            }
        }

        _state.LastOperation = new LastOperationInfo(reason, true, RonMode.Unknown, DateTimeOffset.UtcNow, reason, transaction.Id);
        await SaveStateAsync(cancellationToken).ConfigureAwait(false);
    }

    private void EnsureBaselineMatches(PatchManifest manifest)
    {
        if (_state.Baseline is null)
            throw new BaselineRequiredException("尚未备份原版游戏文件。请先完成首次设置。");
        if (!HasSameManagedPaths(_state.Baseline, manifest))
            throw new BaselineMismatchException("新补丁覆盖的文件路径发生了变化。为避免原版文件缺少备份，请先恢复正版，再重新完成首次设置。");
    }

    private static bool HasSameManagedPaths(BaselineInfo originalFiles, PatchManifest manifest)
    {
        var backedUpPaths = originalFiles.Files
            .Select(file => file.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var patchPaths = manifest.Files
            .Select(file => file.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return backedUpPaths.SetEquals(patchPaths);
    }

    private string ResolveDataRelative(string relative)
    {
        var normalized = relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(_dataRoot, normalized));
        var root = _dataRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new StateCorruptException("状态文件包含越界的备份路径。");
        return full;
    }

    private static async Task CopySnapshotFileAsync(string source, string target, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(target) ?? throw new PatcherException("无效的备份路径。");
        Directory.CreateDirectory(directory);
        var temp = target + ".tmp_" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(true);
            }
            File.Move(temp, target, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private async Task SaveStateAsync(CancellationToken cancellationToken)
        => await _stateStore.SaveAsync(_state, cancellationToken).ConfigureAwait(false);

    private static string CreateId(string prefix)
        => prefix + "_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..8];

    private static void MakeWritableIfNeeded(string path)
    {
        if (!File.Exists(path))
            return;
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Preserve the original operation error.
        }
    }
}
