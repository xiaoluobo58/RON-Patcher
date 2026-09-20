using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RonPatcher.Diagnostics;
using RonPatcher.ViewModels;
using RonPatcher.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace RonPatcher;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();
    private readonly UpdateService _updateService = new();

    public MainWindow()
    {
        StartupLog.Write("MainWindow constructor entered.");
        InitializeComponent();
        RootGrid.DataContext = ViewModel;

        Title = "RON Patcher";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new DesktopAcrylicBackdrop();

        ConfigureWindow();
        RootGrid.Loaded += RootGrid_Loaded;
        StartupLog.Write("MainWindow constructor completed.");
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= RootGrid_Loaded;
        await ViewModel.InitializeAsync();
        _ = CheckForUpdatesQuietlyAsync();
        if (string.IsNullOrWhiteSpace(ViewModel.GamePath) || !ViewModel.HasOriginalFilesBackup)
        {
            await ShowFirstRunDialogAsync();
        }
    }

    private async Task CheckForUpdatesQuietlyAsync()
    {
        try
        {
            var update = await _updateService.CheckAsync();
            if (update is not null)
                DispatcherQueue.TryEnqueue(() => ShowUpdateBar(update));
        }
        catch (Exception exception)
        {
            StartupLog.Write("Update check failed.", exception);
        }
    }

    private void ShowUpdateBar(UpdateInfo update)
    {
        ViewModel.SetUpdate(update.Version, update.ReleaseUrl);
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
        => await CheckUpdatesAsync();

    private async void CheckUpdates_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        => await CheckUpdatesAsync();

    private async Task CheckUpdatesAsync()
    {
        try
        {
            var update = await _updateService.CheckAsync();
            if (update is null)
            {
                await ShowNoticeAsync("已是最新版本", "当前版本已经是 GitHub 上的最新 Release。");
                return;
            }
            ViewModel.SetUpdate(update.Version, update.ReleaseUrl);
        }
        catch (Exception exception)
        {
            StartupLog.Write("Manual update check failed.", exception);
            await ShowNoticeAsync("检查更新失败", "无法连接 GitHub，请稍后重试。");
        }
    }

    private void OpenUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ViewModel.UpdateUrl))
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri(ViewModel.UpdateUrl));
    }

    private void ConfigureWindow()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            appWindow.Resize(new Windows.Graphics.SizeInt32(1180, 820));

            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(iconPath))
                appWindow.SetIcon(iconPath);

            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
                appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            }
        }
        catch (Exception exception)
        {
            StartupLog.Write("Window customization was skipped.", exception);
        }
    }

    private async void ChooseGamePath_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            ViewModel.SetGamePath(folder.Path);
            await ViewModel.BackupOriginalFilesAsync();
        }
    }

    private async void ChoosePatch_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".zip");
        picker.FileTypeFilter.Add(".rar");
        picker.FileTypeFilter.Add(".7z");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            if (string.IsNullOrWhiteSpace(ViewModel.GamePath))
            {
                await ShowNoticeAsync("请先选择正版目录", "首次设置必须先选择未修改的正版游戏目录，然后才能备份原版游戏文件。");
                return;
            }
            ViewModel.SetPatchArchivePath(file.Path);
            await ViewModel.ScanAsync();
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
        => await ViewModel.ScanAsync();

    private async void SwitchToLan_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmAsync(
            "切换到局域网模式",
            "程序会先保存当前文件，再写入补丁压缩包中的文件。首次使用前，请确保已经备份原版游戏文件。",
            "继续切换"))
        {
            await ViewModel.SwitchToLanAsync();
        }
    }

    private async void SwitchToOfficial_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmAsync(
            "切回 Steam 正版",
            "程序会恢复已备份的原版游戏文件，并把补丁新增文件移入可回滚的隔离目录。",
            "恢复正版"))
        {
            await ViewModel.SwitchToOfficialAsync();
        }
    }

    private async void LaunchLan_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmAsync(
            "启动局域网游戏",
            "这会运行补丁包提供的 ColdClientLoader 和第三方 DLL。请确认补丁来源可信。",
            "启动"))
        {
            await ViewModel.LaunchLanAsync();
        }
    }

    private async void LaunchOfficial_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmAsync(
            "通过 Steam 启动正版",
            "程序会打开 Steam 的 Ready or Not 启动链接。",
            "打开 Steam"))
        {
            await ViewModel.LaunchOfficialAsync();
        }
    }

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmAsync(
            "修复当前模式",
            "程序会按当前目标模式重新写入文件，并创建新的回滚快照。",
            "修复"))
        {
            await ViewModel.RepairAsync();
        }
    }

    private async void Rollback_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmAsync(
            "回滚上一次操作",
            "程序会恢复最近一次切换前的文件状态。",
            "回滚"))
        {
            await ViewModel.RollbackAsync();
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message, string primaryText)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520
            },
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowFirstRunDialogAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "首次设置",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "第一步必须选择未修改的 Steam 正版游戏目录。",
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = "随后选择 ZIP、RAR 或 7Z 补丁包。RON Patcher 会立即备份补丁将覆盖的原版游戏文件，以后可一键切回正版。",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.72
                    }
                }
            },
            PrimaryButtonText = "选择正版目录",
            CloseButtonText = "稍后设置",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            ChooseGamePath_Click(this, new RoutedEventArgs());
    }

    private async Task ShowNoticeAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 },
            CloseButtonText = "知道了"
        };
        await dialog.ShowAsync();
    }
}
