using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Controls;
using RonPatcher.Core;

namespace RonPatcher.ViewModels;

public sealed class FileStatusRow
{
    public required string RelativePath { get; init; }
    public required string Status { get; init; }
    public required string Source { get; init; }
    public required string CurrentHash { get; init; }
    public required string OriginalHash { get; init; }
    public required string PatchHash { get; init; }
}

public sealed class OperationLogRow
{
    public required string Summary { get; init; }
    public required string Detail { get; init; }
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class MainViewModel : ObservableObject
{
    private readonly ObservableCollection<FileStatusRow> _fileStatuses = new();
    private readonly ObservableCollection<OperationLogRow> _operationLogs = new();
    private RonPatcherEngine? _engine;
    private ScanResult? _scan;
    private RonMode _mode = RonMode.Unknown;
    private IntegrityStatus _integrity = IntegrityStatus.Unknown;
    private bool _hasBaseline;
    private bool _canRollback;
    private bool _isBusy;
    private bool _initialized;
    private string _gamePath = string.Empty;
    private string _patchArchivePath = string.Empty;
    private string _busyText = string.Empty;
    private string _messageTitle = string.Empty;
    private string _messageText = string.Empty;
    private InfoBarSeverity _messageSeverity = InfoBarSeverity.Informational;
    private string _lastOperation = "暂无记录";
    private string _lastOperationDetail = "完成一次扫描后会显示结果。";
    private string _settingsRoot = string.Empty;
    private string _updateVersion = string.Empty;
    private string _updateUrl = string.Empty;

    public MainViewModel()
    {
        _settingsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RONPatcher");
    }

    public string GamePath
    {
        get => _gamePath;
        private set
        {
            if (SetProperty(ref _gamePath, value))
            {
                ResetInspectionState();
            }
        }
    }

    public string PatchArchivePath
    {
        get => _patchArchivePath;
        private set
        {
            if (SetProperty(ref _patchArchivePath, value))
            {
                ResetInspectionState();
            }
        }
    }

    public ObservableCollection<FileStatusRow> FileStatuses => _fileStatuses;

    public ObservableCollection<OperationLogRow> OperationLogs => _operationLogs;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandProperties();
            }
        }
    }

    public string BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    public string ModeDisplay => _mode switch
    {
        RonMode.Official => "正版模式",
        RonMode.Lan => "局域网模式",
        RonMode.Mixed => "混合状态",
        _ => "未知",
    };

    public string ModeDetail => _scan?.Message ?? "请选择目录和补丁包，然后扫描。";

    public string IntegrityDisplay => _integrity switch
    {
        IntegrityStatus.Intact => "完整",
        IntegrityStatus.Missing => "缺少文件",
        IntegrityStatus.Modified => "需要修复",
        IntegrityStatus.Conflict => "存在冲突",
        _ => "未扫描",
    };

    public string IntegrityDetail
    {
        get
        {
            if (_scan is null)
            {
                return "尚未执行扫描";
            }

            var mismatches = _scan.Files.Count(file => file.State is ManagedFileState.Missing or ManagedFileState.Modified or ManagedFileState.Conflict);
            return $"已检查 {_scan.Files.Count} 个受管文件；{mismatches} 个不一致";
        }
    }

    public string LastOperationDisplay => _lastOperation;
    public string LastOperationDetail => _lastOperationDetail;

    public bool HasMessage => !string.IsNullOrWhiteSpace(MessageText);
    public string MessageTitle
    {
        get => _messageTitle;
        private set
        {
            if (SetProperty(ref _messageTitle, value))
            {
                RaisePropertyChanged(nameof(HasMessage));
            }
        }
    }

    public string MessageText
    {
        get => _messageText;
        private set
        {
            if (SetProperty(ref _messageText, value))
            {
                RaisePropertyChanged(nameof(HasMessage));
            }
        }
    }

    public InfoBarSeverity MessageSeverity
    {
        get => _messageSeverity;
        private set => SetProperty(ref _messageSeverity, value);
    }

    public bool CanScan => !IsBusy && Directory.Exists(GamePath) && File.Exists(PatchArchivePath);

    public bool CanSwitchToLan
        => !IsBusy && _engine is not null && _scan is not null && _hasBaseline &&
           !(_mode == RonMode.Lan && _integrity == IntegrityStatus.Intact);

    public bool CanSwitchToOfficial
        => !IsBusy && _engine is not null && _scan is not null && _hasBaseline && _mode != RonMode.Official;

    public bool CanLaunchLan
        => !IsBusy && _engine is not null && _mode == RonMode.Lan && _integrity == IntegrityStatus.Intact;

    public bool CanLaunchOfficial
        => !IsBusy && _engine is not null && _mode == RonMode.Official &&
           (_integrity == IntegrityStatus.Intact || _integrity == IntegrityStatus.Unknown);

    public bool CanRepair
        => !IsBusy && _engine is not null && _scan is not null && _hasBaseline &&
           (_mode == RonMode.Lan || _mode == RonMode.Official) && _integrity != IntegrityStatus.Intact;

    public bool CanRollback => !IsBusy && _engine is not null && _canRollback;
    public bool HasOriginalFilesBackup => _hasBaseline;
    public string VersionDisplay => $"本地工具 · v{typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}";
    public bool HasUpdate => !string.IsNullOrWhiteSpace(_updateVersion);
    public string UpdateUrl => _updateUrl;
    public string UpdateMessage => HasUpdate ? $"GitHub 已发布 v{_updateVersion}，当前版本为 v{VersionDisplay.Replace("本地工具 · v", string.Empty)}。" : string.Empty;

    public void SetUpdate(string version, string url)
    {
        _updateVersion = version;
        _updateUrl = url;
        RaisePropertyChanged(nameof(HasUpdate));
        RaisePropertyChanged(nameof(UpdateUrl));
        RaisePropertyChanged(nameof(UpdateMessage));
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        LoadSavedPaths();
        if (CanScan)
        {
            await ScanAsync();
        }
        else
        {
            SetMessage(InfoBarSeverity.Informational, "首次设置", "请先选择未修改的正版游戏目录，再选择 ZIP、RAR 或 7Z 补丁包。程序会立即备份补丁将覆盖的原版游戏文件。");
        }
    }

    public void SetGamePath(string path)
    {
        _engine?.Dispose();
        _engine = null;
        GamePath = path.Trim();
        ClearMessage();
        SavePaths();
    }

    public void SetPatchArchivePath(string path)
    {
        _engine?.Dispose();
        _engine = null;
        PatchArchivePath = path.Trim();
        ClearMessage();
        SavePaths();
    }

    public async Task InitializeOriginalFilesAsync()
    {
        if (!CanScan)
        {
            SetMessage(InfoBarSeverity.Warning, "无法备份", "请先选择未修改的正版游戏目录和补丁压缩包。");
            return;
        }

        await RunBusyAsync("正在备份原版游戏文件…", async () =>
        {
            EnsureEngine();
            var result = await _engine!.InitializeOriginalFilesAsync();
            ApplyResult(result);
            SavePaths();
        });
    }

    public async Task BackupOriginalFilesAsync()
    {
        if (!Directory.Exists(GamePath))
        {
            SetMessage(InfoBarSeverity.Warning, "无法备份", "请先选择未修改的正版游戏目录。");
            return;
        }

        await RunBusyAsync("正在备份原版游戏文件并计算哈希…", async () =>
        {
            EnsureEngine(requirePatch: false);
            var result = await _engine!.BackupOriginalFilesAsync();
            ApplyResult(result);
            SavePaths();
        });
    }

    public async Task ScanAsync()
    {
        if (!CanScan)
        {
            SetMessage(InfoBarSeverity.Warning, "无法扫描", "请确认游戏目录和补丁压缩包都存在。");
            return;
        }

        await RunBusyAsync("正在扫描文件…", async () =>
        {
            EnsureEngine();
            _scan = await _engine!.ScanAsync();
            ApplyScan(_scan);
            _lastOperation = "扫描完成";
            _lastOperationDetail = _scan.Message;
            AddLog("扫描", _scan.Message);
            SetMessage(_scan.Mode == RonMode.Mixed ? InfoBarSeverity.Warning : InfoBarSeverity.Success, "扫描完成", _scan.Message);
        });
    }

    public async Task SwitchToLanAsync()
    {
        await RunBusyAsync("正在切换到局域网模式…", async () =>
        {
            EnsureEngine();
            var result = await _engine!.SwitchToLanAsync();
            ApplyResult(result);
        });
    }

    public async Task SwitchToOfficialAsync()
    {
        await RunBusyAsync("正在恢复正版文件…", async () =>
        {
            EnsureEngine();
            var result = await _engine!.SwitchToOfficialAsync();
            ApplyResult(result);
        });
    }

    public async Task RepairAsync()
    {
        await RunBusyAsync("正在修复当前模式…", async () =>
        {
            EnsureEngine();
            var result = await _engine!.RepairAsync(_mode);
            ApplyResult(result);
        });
    }

    public async Task RollbackAsync()
    {
        await RunBusyAsync("正在回滚上一次操作…", async () =>
        {
            EnsureEngine();
            var result = await _engine!.RollbackAsync();
            ApplyResult(result);
            _canRollback = false;
            RaiseCommandProperties();
        });
    }

    public async Task LaunchLanAsync()
    {
        await RunBusyAsync("正在启动局域网游戏…", async () =>
        {
            EnsureEngine();
            var result = await _engine!.LaunchLanAsync();
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Message);
            }

            _lastOperation = "已启动局域网游戏";
            _lastOperationDetail = result.Message;
            AddLog("启动局域网游戏", result.Message);
            SetMessage(InfoBarSeverity.Success, "启动成功", result.Message);
        });
    }

    public async Task LaunchOfficialAsync()
    {
        await RunBusyAsync("正在请求 Steam 启动…", async () =>
        {
            EnsureEngine();
            var result = await _engine!.LaunchOfficialAsync();
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Message);
            }

            _lastOperation = "已请求 Steam 启动";
            _lastOperationDetail = result.Message;
            AddLog("启动正版", result.Message);
            SetMessage(InfoBarSeverity.Success, "已请求 Steam", result.Message);
        });
    }

    private void EnsureEngine(bool requirePatch = true)
    {
        if (!Directory.Exists(GamePath) || (requirePatch && !File.Exists(PatchArchivePath)))
        {
            throw new InvalidOperationException("游戏目录或补丁压缩包不存在。");
        }

        _engine ??= new RonPatcherEngine(new RonPatcherOptions(GamePath, PatchArchivePath));
    }

    private void ApplyResult(OperationResult result)
    {
        if (result.Scan is not null)
        {
            _scan = result.Scan;
            ApplyScan(_scan);
        }

        _canRollback = _engine?.State.LastTransaction is not null;
        _hasBaseline = _engine?.State.Baseline is not null;
        _lastOperation = result.Operation;
        _lastOperationDetail = result.Message;
        AddLog(result.Operation, result.Message);
        SetMessage(result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error, result.Success ? "操作完成" : "操作失败", result.Message);
        RaiseCommandProperties();
        RaisePropertyChanged(nameof(HasOriginalFilesBackup));
    }

    private void ApplyScan(ScanResult scan)
    {
        _mode = scan.Mode;
        _integrity = scan.Integrity;
        _hasBaseline = scan.HasBaseline;
        _canRollback = _engine?.State.LastTransaction is not null;

        _fileStatuses.Clear();
        foreach (var file in scan.Files.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            _fileStatuses.Add(new FileStatusRow
            {
                RelativePath = file.RelativePath,
                Status = file.State switch
                {
                    ManagedFileState.Patch => "补丁一致",
                    ManagedFileState.Original => "原版一致",
                    ManagedFileState.Missing => "缺失",
                    ManagedFileState.Modified => "已修改",
                    ManagedFileState.Conflict => "冲突",
                    _ => "未受管",
                },
                Source = file.State switch
                {
                    ManagedFileState.Patch => "补丁",
                    ManagedFileState.Original => "原版",
                    _ => "需检查",
                },
                CurrentHash = FormatHash(file.ActualSha256, file.Exists ? "计算失败" : "文件不存在"),
                OriginalHash = FormatHash(file.OriginalSha256, "原版中不存在"),
                PatchHash = FormatHash(file.PatchSha256, "未计算"),
            });
        }

        RaisePropertyChanged(nameof(ModeDisplay));
        RaisePropertyChanged(nameof(ModeDetail));
        RaisePropertyChanged(nameof(IntegrityDisplay));
        RaisePropertyChanged(nameof(IntegrityDetail));
        RaisePropertyChanged(nameof(HasOriginalFilesBackup));
        RaiseCommandProperties();
        RaisePropertyChanged(nameof(HasOriginalFilesBackup));
    }

    private static string FormatHash(string? value, string emptyText)
        => string.IsNullOrWhiteSpace(value) ? emptyText : value;

    private async Task RunBusyAsync(string text, Func<Task> operation)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        BusyText = text;
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            _lastOperation = "操作失败";
            _lastOperationDetail = ex.Message;
            AddLog("失败", ex.Message);
            SetMessage(InfoBarSeverity.Error, "操作失败", ex.Message);
        }
        finally
        {
            BusyText = string.Empty;
            IsBusy = false;
            RaiseCommandProperties();
        }
    }

    private void ResetInspectionState()
    {
        _engine?.Dispose();
        _engine = null;
        _scan = null;
        _mode = RonMode.Unknown;
        _integrity = IntegrityStatus.Unknown;
        _hasBaseline = false;
        _canRollback = false;
        _fileStatuses.Clear();
        RaisePropertyChanged(nameof(ModeDisplay));
        RaisePropertyChanged(nameof(ModeDetail));
        RaisePropertyChanged(nameof(IntegrityDisplay));
        RaisePropertyChanged(nameof(IntegrityDetail));
        RaiseCommandProperties();
    }

    private void RaiseCommandProperties()
    {
        RaisePropertyChanged(nameof(CanScan));
        RaisePropertyChanged(nameof(CanSwitchToLan));
        RaisePropertyChanged(nameof(CanSwitchToOfficial));
        RaisePropertyChanged(nameof(CanLaunchLan));
        RaisePropertyChanged(nameof(CanLaunchOfficial));
        RaisePropertyChanged(nameof(CanRepair));
        RaisePropertyChanged(nameof(CanRollback));
    }

    private void SetMessage(InfoBarSeverity severity, string title, string text)
    {
        MessageSeverity = severity;
        MessageTitle = title;
        MessageText = text;
    }

    private void ClearMessage()
    {
        MessageText = string.Empty;
        MessageTitle = string.Empty;
    }

    private void AddLog(string summary, string detail)
    {
        _operationLogs.Insert(0, new OperationLogRow
        {
            Summary = $"{DateTime.Now:HH:mm:ss}  {summary}",
            Detail = detail,
        });

        while (_operationLogs.Count > 20)
        {
            _operationLogs.RemoveAt(_operationLogs.Count - 1);
        }
    }

    private string SettingsPath => Path.Combine(_settingsRoot, "settings.json");

    private void LoadSavedPaths()
    {
        try
        {
            _settingsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RONPatcher");
            if (!File.Exists(SettingsPath))
                return;
            var lines = File.ReadAllLines(SettingsPath);
            var game = lines.FirstOrDefault(line => line.StartsWith("game=", StringComparison.Ordinal))?[5..];
            var patch = lines.FirstOrDefault(line => line.StartsWith("patch=", StringComparison.Ordinal))?[6..];
            if (!string.IsNullOrWhiteSpace(game) && Directory.Exists(game))
                GamePath = game;
            if (!string.IsNullOrWhiteSpace(patch) && File.Exists(patch))
                PatchArchivePath = patch;
        }
        catch
        {
            // 设置损坏时回到首次设置流程。
        }
    }

    private void SavePaths()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_settingsRoot))
                _settingsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RONPatcher");
            Directory.CreateDirectory(_settingsRoot);
            File.WriteAllLines(SettingsPath, [$"game={GamePath}", $"patch={PatchArchivePath}"]);
        }
        catch
        {
            // 路径仍可在本次会话使用。
        }
    }
}
