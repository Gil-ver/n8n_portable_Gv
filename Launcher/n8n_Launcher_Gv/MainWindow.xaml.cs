using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wpf.Ui.Controls;
using Wpf.Ui.Appearance;
using DoubleAnimation = System.Windows.Media.Animation.DoubleAnimation;
using IEasingFunction = System.Windows.Media.Animation.IEasingFunction;
using QuadraticEase = System.Windows.Media.Animation.QuadraticEase;
using EasingMode = System.Windows.Media.Animation.EasingMode;
using RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior;
using Storyboard = System.Windows.Media.Animation.Storyboard;

// 消除 UseWindowsForms 带来的命名空间歧义
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using DoubleCollection = System.Windows.Media.DoubleCollection;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using WpfPath = System.Windows.Shapes.Path;
using WpfPoint = System.Windows.Point;
using WpfTextBlock = System.Windows.Controls.TextBlock;

namespace n8n_launcher_Gv;

/// <summary>
/// 启动器统一状态模型（data/.Launcher/version.json）。
/// 保存窗口缩放、版本缓存以及后续可扩展的用户设置。
/// </summary>
internal class AppConfig
{
    public double WindowScale { get; set; } = 1.0;
    // 默认浅色（2026-08-07 由 "System" 改为 "Light"）：
    // 首次运行 / config 无此字段时生效；"System"（跟随系统）仍是设置页可选项，只是不再作为默认。
    // ★ 改此处必须同步 NormalizeThemeMode 的兜底返回值与 PerformSettingsReset 的重置值，三处保持一致。
    public string ThemeMode { get; set; } = "Light";
    public string BrowserName { get; set; } = "系统默认";
    public string BrowserPath { get; set; } = string.Empty;
    public bool StartWithWindows { get; set; }
    public string StartupMode { get; set; } = "None";
    public bool TakeOverCopilotKey { get; set; }
    public string CopilotKeyMode { get; set; } = string.Empty;
    public bool ProxyEnabled { get; set; }
    public int ProxyPort { get; set; } = 7890;
    public bool LowPerformanceMode { get; set; }
    public int SleepTimerMinutes { get; set; }
    public int SleepTimerLastAppliedMinutes { get; set; }
    // 时区（用于注入 n8n 进程的 GENERIC_TIMEZONE / TZ 环境变量）。
    // "auto" = 跟随系统，或具体 IANA 名（如 "Asia/Shanghai"）。空值等价 "auto"。
    public string Timezone { get; set; } = "auto";
    // 控制台页右上角"字体切换调试按钮"的持久化态。
    //   "Yahei"     = 默认，走 App.xaml 的 LauncherFontFamily（微软雅黑优先）
    //   "SourceHan" = 强制内嵌思源黑体 CN 优先
    // 删除 version.json 后（值缺失/空/非法值）→ EnsureConfigDefaults 兜底回 "Yahei"。
    public string ConsoleFontMode { get; set; } = "Yahei";
    // 关于页教程文本框语言：Chinese / English，默认 Chinese；由右下角浮动切换按钮读写。
    public string AboutTutorialLanguage { get; set; } = "Chinese";
    public VersionCache Versions { get; set; } = new();
    public List<FileAccessPermissionSlot> FileAccessPermissions { get; set; } = new();
}

internal sealed record BrowserChoice(string DisplayName, string BrowserPath);

internal class FileAccessPermissionSlot
{
    public string Path { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
}

/// <summary>
/// 文本 → Visibility 转换器：
///   - 输入 string 为空/纯空白 → Visible（显示占位符）
///   - 输入 string 非空       → Collapsed（隐藏占位符）
/// 用于时区搜索框上叠加的灰色占位 TextBlock 的显隐绑定。
/// </summary>
internal sealed class EmptyStringToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string? text = value as string;
        return string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

internal class VersionCache
{
    public string NodeJs { get; set; } = "未知";
    public string Python { get; set; } = "未知";
    public string Ffmpeg { get; set; } = "未知";
    public string N8nCurrent { get; set; } = "未知";
    public string N8nLatest { get; set; } = "0.0.0";
    public string N8nLatestStatus { get; set; } = "Unknown";
    public string Launcher { get; set; } = "Gv_1.2.2";
    public string LauncherLatest { get; set; } = "0.0.0";
    public string LauncherLatestStatus { get; set; } = "Unknown";
    public string? LauncherLatestUpdatedAt { get; set; }
    public string? LauncherLatestLastCheckAt { get; set; }
    public string? LauncherLatestLastError { get; set; }
    public string? LocalVersionUpdatedAt { get; set; }
    public string? LatestVersionUpdatedAt { get; set; }
    public string? LatestVersionLastCheckAt { get; set; }
    public string? LatestVersionLastError { get; set; }
}

/// <summary>
/// 执行记录页排序列。默认初始态为 Started + 降序（等价于最新在前）。
/// 两态循环：点击当前列 = 反转方向；点击其他列 = 切换到该列并使用该列的默认方向。
/// </summary>
internal enum ExecutionSortField
{
    Workflow,
    Status,
    Started,
    RunTime,
    ExecId
}

public partial class MainWindow : FluentWindow
{
    private bool _isExiting;
    private readonly bool _startHiddenToTray;
    private readonly UserPreferenceChangedEventHandler _themeChangedHandler;

    // 设计基准：一倍 = 2160×1440 物理像素；开发基准 DPI = 150%，对应 WPF 设计画布 1440×960 DIP。
    private const double DESIGN_DPI_SCALE = 1.5;
    private const double BASE_PHYSICAL_WIDTH  = 2160.0;
    private const double BASE_PHYSICAL_HEIGHT = 1440.0;
    private const double DESIGN_DIP_WIDTH  = BASE_PHYSICAL_WIDTH  / DESIGN_DPI_SCALE;
    private const double DESIGN_DIP_HEIGHT = BASE_PHYSICAL_HEIGHT / DESIGN_DPI_SCALE;

    // 当前窗口目标尺寸（DIP 单位）
    private double _targetDipsW = DESIGN_DIP_WIDTH;
    private double _targetDipsH = DESIGN_DIP_HEIGHT;

    // 系统 DPI 缩放比例（构造函数预取，SourceInitialized 时精确更新）
    private double _systemDpi = 1.0;

    // 「启动尺寸已预应用」标记：构造函数中若已按保存比例设好 Width/Height，SourceInitialized 跳过重复二次缩放
    private bool _startupSizePreset;

    // Win32 消息常量
    private const int WM_NCRBUTTONUP   = 0x00A5;
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_SETCURSOR     = 0x0020;
    private const int WM_HOTKEY        = 0x0312;

    private const string StartupModeNone = "None";
    private const string StartupModeStartN8n = "StartN8n";
    private const string StartupModeStartN8nAndOpenWeb = "StartN8nAndOpenWeb";
    private const string StartupTrayArgument = "--startup-tray";
    private const string CopilotKeyModeOff = "Off";
    private const string CopilotKeyModeShowLauncher = "ShowLauncher";
    private const string CopilotKeyModeOpenN8nWeb = "OpenN8nWeb";
    private const string CopilotKeyModeShowLauncherAndOpenN8nWeb = "ShowLauncherAndOpenN8nWeb";

    // Copilot 键常见组合：Win + Shift + F23，仅在启动器进程运行期间接管。
    // 优先使用 RegisterHotKey；若被 Windows/Copilot/OEM 服务占用，则退到 WH_KEYBOARD_LL 低级键盘钩子。
    private const int COPILOT_HOTKEY_ID = 0x4756;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN   = 0x0008;
    private const uint VK_F23    = 0x86;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN    = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP      = 0x0101;
    private const int WM_SYSKEYUP   = 0x0105;
    private const int VK_SHIFT = 0x10;
    private const int VK_LWIN  = 0x5B;
    private const int VK_RWIN  = 0x5C;

    // 系统托盘图标（使用 Windows Forms 原生接口以获取精确的右键事件）
    private System.Windows.Forms.NotifyIcon? _winFormsTray;
    private TrayPopupMenu? _trayPopupMenu;
    private bool _isCopyingConsoleLog;

    // Hit-test 常量（WM_SETCURSOR 的 lParam LOWORD）
    private const int HTLEFT        = 10;
    private const int HTRIGHT       = 11;
    private const int HTTOP         = 12;
    private const int HTTOPLEFT     = 13;
    private const int HTTOPRIGHT    = 14;
    private const int HTBOTTOM      = 15;
    private const int HTBOTTOMLEFT  = 16;
    private const int HTBOTTOMRIGHT = 17;

    // 光标常量
    private const int IDC_ARROW = 32512;
    private const int SW_RESTORE = 9;

    private const string LAUNCHER_VERSION = "Gv_1.2.2";
    private const string N8nLocalRootUrl = "http://localhost:5678";
    private const string N8nLocalWorkflowsUrl = "http://localhost:5678/home/workflows";
    private const string N8nOfficialSiteUrl = "https://n8n.io/";
    private const string N8nOfficialGithubUrl = "https://github.com/n8n-io/n8n";
    private const string AuthorGithubStarUrl = "https://github.com/Gil-ver/n8n_portable_Gv";
    // 远端启动器发布清单：仓库根目录 launcher_release.json，读 latest_version 字段。
    // 发新版流程：改 LAUNCHER_VERSION 常量 → 编译打包 → 修改仓库 launcher_release.json 的 latest_version → push。
    // 12 小时冷却，失败降级 [未知] 并保留旧缓存。
    private const string LauncherReleaseManifestUrl = "https://raw.githubusercontent.com/Gil-ver/n8n_portable_Gv/main/launcher_release.json";
    private static readonly object StateFileLock = new();
    private static readonly JsonSerializerOptions StateJsonOptions = new() { WriteIndented = true };
    private static readonly TimeSpan WorkspaceDataAutoRefreshCooldown = TimeSpan.FromMinutes(10);
    private const int ExecutionRecordsPageSize = 50;
    private const double NodeModulesRingDashTotal = 276.46;
    private const string NodeModulesDownloadUrl = "https://github.com/";
    private const string LauncherStartupRunName = "n8n_launcher_Gv";
    private static readonly Brush NodeModulesOkBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x6B, 0xEB));
    private static readonly Brush NodeModulesWarningBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly Brush NodeModulesIdleBrush = Brushes.Black;
    private static readonly Brush NodeModulesIconWarningBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06));
    private static readonly Brush NodeModulesIconOkBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x6B, 0xEB));
    // 文件夹授权路径框文字色的两个资源键名（浅/深取值见 XAML 资源区注释与 UpdateThemeColors）。
    // ★ 不再用静态 SolidColorBrush 字段：UpdateFileAccessPathInputVisualState 对 Foreground 的赋值
    //   属"本地属性"，优先级高于 DynamicResource，直接塞画刷实例会导致主题切换后不重刷。
    //   改为 SetResourceReference 挂键，Resources[键] 更新时表达式自动重算。
    private const string FileAccessPathInputForegroundKey = "FolderPathTextBoxForegroundBrush";
    private const string FileAccessPathInputLockedForegroundKey = "FolderPathTextBoxLockedForegroundBrush";
    // 设置页「时区选择」下拉列表第 0 项 —— 时区搜索输入框的三个资源键名
    // （浅/深取值与设计判据见 XAML 资源区 TimezoneSearchBox* 注释块与 UpdateThemeColors）。
    // ★ 该搜索框由 AddTimezoneSearchBoxItem() 动态 new，而 PopulateTimezoneSelector()
    //   只在读取设置时调用一次 —— 切主题时不重建。故必须 SetResourceReference 挂键，
    //   不能 new SolidColorBrush 直接赋值，否则换主题后颜色不刷新。
    private const string TimezoneSearchBoxBackgroundKey = "TimezoneSearchBoxBackgroundBrush";
    private const string TimezoneSearchBoxBorderKey = "TimezoneSearchBoxBorderBrush";
    private const string TimezoneSearchBoxForegroundKey = "TimezoneSearchBoxForegroundBrush";
    private static readonly IEasingFunction NodeModulesProgressEase = new QuadraticEase { EasingMode = EasingMode.EaseOut };

    private double _nodeModulesDisplayedPercent;
    private bool _isRepairingNodeModules;
    private bool _isShowingNodeModulesTarMissingPrompt;
    private readonly System.Windows.Threading.DispatcherTimer _nodeModulesDeletingTextTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private int _nodeModulesDeletingTextDotCount;
    private Storyboard? _launchRocketPreviewStoryboard;
    private readonly Random _launchStarRandom = new();
    private readonly System.Windows.Threading.DispatcherTimer _launchStarTimer = new();
    private readonly System.Windows.Threading.DispatcherTimer _launchMeteorTimer = new();
    private Process? _n8nProcess;
    private System.Windows.Controls.Image? _stopLaunchTileImage;
    private System.Windows.Controls.Image? _stopLaunchTileGrayscaleImage;
    private ImageSource? _stopLaunchTileNormalSource;
    private ImageSource? _stopLaunchTileGrayscaleSource;
    private System.Windows.Controls.Image? _restartLaunchTileImage;
    private System.Windows.Controls.Image? _restartLaunchTileGrayscaleImage;
    private ImageSource? _restartLaunchTileNormalSource;
    private ImageSource? _restartLaunchTileGrayscaleSource;
    private static readonly Duration LaunchTileAvailabilityTransitionDuration = new(TimeSpan.FromSeconds(0.5));

    /// <summary>控制台操作键禁用时内容层的不透明度（0.4 时文字约落到 #9A9A9A，浅色/深色底都读得出不可点）。</summary>
    private const double ConsoleTileDisabledContentOpacity = 0.4;
    private int _n8nStartGeneration;
    private bool _isStartingN8n;
    private bool _isStoppingN8n;
    // 用户主动请求停止的持久标志：
    // _isStoppingN8n 用于防重入且很快被清零，但 taskkill 是异步的，
    // 真实的 Process.Exited 回调触发时 _isStoppingN8n 往往已被清零，
    // 会被误判为"启动失败 (Failed)"。此标志由 StopN8nProcessAsync 在
    // 真正对活进程发起 kill 时置 true，由 Process.Exited 回调消费后置 false，
    // 保证"停止是停止，失败是失败"。
    private bool _userStopRequestedExit;
    private bool _isRestartingN8n;
    private readonly StringBuilder _consoleOutputBuilder = new();
    private string _lastN8nCommandLine = string.Empty;
    private bool _isLoadingFileAccessPermissions;
    private bool _isLoadingSettings;
    private bool _isLoadingProxySettings;
    // 时区服务：读取 timezones.json + 调 node.exe 检测 + 决策 effectiveTimezone。
    private readonly TimezoneService _timezoneService = new();
    private string _currentTimezoneTag = TimezoneService.AutoToken;
    private readonly List<string> _timezoneSupportedZones = new();
    private bool _isPopulatingTimezoneSelector;
    private System.Windows.Controls.TextBox? _timezoneSearchTextBox;
    private bool _isCopilotHotkeyRegistered;
    private bool _isCopilotKeyboardHookRegistered;
    private IntPtr _copilotKeyboardHookHandle = IntPtr.Zero;
    private LowLevelKeyboardProc? _copilotKeyboardHookProc;
    // Copilot 键"一次物理按键 = 一次动作"的按下状态标志。
    // Windows 长按一个键会以约 30 次/秒的频率持续投递 KEYDOWN / WM_HOTKEY（自动重复），
    // 若不做配对抑制，长按 1 秒就会执行几十次动作（表现为一瞬间弹出几十个浏览器窗口）。
    // 置位后必须等到物理抬起才复位：
    //   · 键盘钩子路径 —— 由 WM_KEYUP / WM_SYSKEYUP 复位；
    //   · RegisterHotKey 路径 —— 无抬起消息，由 _copilotKeyReleaseWatchTimer 轮询 GetAsyncKeyState 复位。
    private bool _isCopilotKeyPhysicallyDown;
    // RegisterHotKey 路径专用：30ms 轮询 F23 是否已物理抬起，抬起后复位上面的标志并自停。
    private System.Windows.Threading.DispatcherTimer? _copilotKeyReleaseWatchTimer;
    // "打开 n8n 页面"动作的重入保护：n8n 未运行时该流程会等待 HTTP ready，耗时较长，
    // 期间若被再次触发会并发启动多个 n8n 或打开多个页面。
    private bool _isOpenN8nWebFromCopilotKeyRunning;
    private readonly System.Windows.Threading.DispatcherTimer _sleepTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset? _sleepTimerDueAt;
    // 设置重置按钮：常态 ↺ | 一次点击后 ✔（红底）等待 5 秒二次确认；5 秒无操作自动回到常态。
    private readonly System.Windows.Threading.DispatcherTimer _settingsResetConfirmTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _settingsResetConfirmPending;
    private DateTimeOffset? _lastSuccessfulWorkspaceDataRefreshAt;
    private bool _isWorkspaceDataRefreshRunning;
    private List<BrowserChoice> _browserChoices = new();
    private readonly N8nExecutionListService _executionListService = new();
    private readonly List<N8nExecutionListRecord> _executionRecords = new();
    private readonly List<N8nExecutionListRecord> _filteredExecutionRecords = new();
    private string? _selectedExecutionWorkflowName;
    private string? _selectedExecutionStatusName;
    private int _executionRecordsVisibleCount;
    private bool _executionRecordsInitialized;
    private bool _isExecutionRecordsSyncing;

    /// <summary>执行记录页当前排序列。初始态为 Started（等价于按开始时间排序）。</summary>
    private ExecutionSortField _executionSortField = ExecutionSortField.Started;
    /// <summary>执行记录页当前排序方向：true=升序，false=降序。初始为降序（最新在前）。</summary>
    private bool _executionSortAscending = false;

    // 当前用户缩放倍数（默认 1.0），用于保存到配置文件
    private double _currentUserScale = 1.0;

    private static readonly System.Windows.Media.Color ColorDarkSidebar = System.Windows.Media.Color.FromArgb(200, 0x1F, 0x20, 0x21);
    private static readonly System.Windows.Media.Color ColorDarkFooter  = System.Windows.Media.Color.FromArgb(255, 0x1A, 0x1A, 0x1A);
    private static readonly System.Windows.Media.Color ColorLightSidebar = System.Windows.Media.Color.FromArgb(200, 255, 255, 255);
    private static readonly System.Windows.Media.Color ColorLightFooter  = System.Windows.Media.Color.FromArgb(255, 0xD4, 0xE7, 0xFA);

    public MainWindow(bool startHiddenToTray = false)
    {
        _startHiddenToTray = startHiddenToTray;
        InitializeComponent();
        ApplyConfiguredBackdropBeforeHandleCreated();
        // 关于页主卡片右下角版本文字：与底栏 StatusText、配置文件同源，
        // 均来自 LAUNCHER_VERSION 常量。此处一次写入即可，后续升级只改常量一处。
        AboutHeaderVersionText.Text = LAUNCHER_VERSION;
        InitializeStopLaunchTileState();

        // 执行记录页两个筛选 Popup：使用 Custom 定位回调，使 Popup 水平居中于表头文字锚点(Grid)
        // 而不是整个表头列宽（Column 宽度远大于文字视觉宽度，Placement=Bottom 会左对齐列，导致视觉偏离文字）。
        if (ExecutionRecordsWorkflowFilterPopup is not null)
        {
            ExecutionRecordsWorkflowFilterPopup.CustomPopupPlacementCallback = CenterFilterPopupPlacement;
        }
        if (ExecutionRecordsStatusFilterPopup is not null)
        {
            ExecutionRecordsStatusFilterPopup.CustomPopupPlacementCallback = CenterFilterPopupPlacement;
        }

        if (_startHiddenToTray)
        {
            // 开机静默启动时只让首帧透明且不进任务栏，不把窗口置为 Minimized。
            // WPF 自定义窗口从隐藏的最小化状态恢复时，可能只恢复出标题栏外壳。
            ShowActivated = false;
            ShowInTaskbar = false;
            WindowState = WindowState.Normal;
            Opacity = 0;
        }

        // ============================================================
        // 预启动缩放：在窗口创建句柄之前，按保存/自动比例设定 Width/Height
        // 这样 Wpf.Ui 在创建 HWND 和 Acrylic 背景时，就是以正确的尺寸合成，
        // 避免先显示 1x 大底 Acrylic 再缩小到非 1x 内容。
        // ============================================================
        uint rawDpi = GetDpiForSystem();
        double preDpi = rawDpi / 96.0;
        _systemDpi = preDpi; // 预填，SourceInitialized 时再精确更新

        double initScale;
        try
        {
            var preConfig = LoadConfig();
            initScale = preConfig.WindowScale;
        }
        catch
        {
            initScale = ComputeAutoScale();
        }

        _currentUserScale = initScale;
        if (Math.Abs(initScale - 1.0) > 0.001)
        {
            double initLayoutScale = ComputeLayoutScale(initScale, preDpi);
            double initW = DESIGN_DIP_WIDTH  * initLayoutScale;
            double initH = DESIGN_DIP_HEIGHT * initLayoutScale;
            Width  = initW;
            Height = initH;
            _targetDipsW = initW;
            _targetDipsH = initH;
            _startupSizePreset = true;
            Debug.WriteLine($"[PreScale] Constructor preset: userScale={initScale:F4}, " +
                            $"layoutScale={initLayoutScale:F4}, preDpi={preDpi:F2}, window(DIP)={initW:F0}x{initH:F0}");
        }
        else
        {
            _startupSizePreset = false;
        }

        // 托盘菜单显示层：保留 NotifyIcon，只替换旧 ContextMenu 为轻量两层 WPF 弹层菜单。
        _trayPopupMenu = new TrayPopupMenu(this);
        _trayPopupMenu.UpdateScale(_currentUserScale);
        _trayPopupMenu.StartupEnabled = NormalizeStartupMode(LoadConfig().StartupMode) != StartupModeNone && IsStartupEnabled();
        _trayPopupMenu.ScaleRequested += SetWindowScale;
        _trayPopupMenu.StartupToggled += ApplyTrayStartupToggle;
        _trayPopupMenu.ShowRequested += ShowAndActivate;
        _trayPopupMenu.OpenN8nWebRequested += async () => await OpenN8nWebFromTrayAsync();
        _trayPopupMenu.StartN8nRequested += async () => await StartN8nAsync();
        _trayPopupMenu.StopN8nRequested += async () => await StopN8nProcessAsync(showNoProcessMessage: true);
        _trayPopupMenu.RestartN8nRequested += async () => await RestartN8nAsync();
        _trayPopupMenu.ExitRequested += ExitApplication;

        // 保存事件处理器引用以便取消订阅
        _themeChangedHandler = (_, args) =>
        {
            if (args.Category == UserPreferenceCategory.General)
                Dispatcher.Invoke(UpdateThemeColors);
        };
        SystemEvents.UserPreferenceChanged += _themeChangedHandler;
        _sleepTimer.Tick += SleepTimer_Tick;
        _settingsResetConfirmTimer.Tick += SettingsResetConfirmTimer_Tick;
        _nodeModulesDeletingTextTimer.Tick += NodeModulesDeletingTextTimer_Tick;

        // 窗口失焦时 TitleBar 按钮变淡（浅 #9B9B9B / 深 #737373）
        Activated += (_, _) => TitleBar.ClearValue(Wpf.Ui.Controls.TitleBar.ButtonsForegroundProperty);
        Deactivated += (_, _) =>
        {
            var isDark = IsCurrentThemeDark();
            TitleBar.ButtonsForeground = new SolidColorBrush(
                isDark ? Color.FromRgb(0x73, 0x73, 0x73) : Color.FromRgb(0x9B, 0x9B, 0x9B));
        };

        Loaded += (_, _) =>
        {
            // TitleBar 不显示图标，避免非 1x 缩放时与 AppDesignSurface 内 SidebarIcon 产生错位重影。
            LoadSidebarIcon();
            InitTrayIcon();
            UpdateThemeColors();
            ShowSelectedContentPage();
            RenderFileAccessPermissionSlots();
            RenderProxySettings();
            RenderSettingsPage();
            UpdateSettingsResetButtonAppearance();
            // 关于页教程：按记忆的语言初始化按钮 Content 与文本框内容
            if (AboutTutorialLanguageToggleButtonText is not null)
            {
                bool isEnglish = string.Equals(LoadConfig().AboutTutorialLanguage, "English", StringComparison.OrdinalIgnoreCase);
                AboutTutorialLanguageToggleButtonText.Text = isEnglish ? "EN" : "中";
            }
            SetAboutTutorialText(AboutTutorialKind.PackageIntro);
            // 默认激活第一个教程按钮（便携包介绍），与默认显示的教程内容一致
            InitializeTutorialButtonActiveState();
            RefreshNodeModulesHealth(animate: true);
            UpdateStartLaunchTileAvailability();
            UpdateNodeModulesRepairAvailability();
            ApplyConsoleFontFromConfig();
            StartLaunchTile02GradientRotation();
            _ = RefreshWorkspaceDataAsync(WorkspaceDataRefreshReason.Startup);
            if (_startHiddenToTray)
            {
                _ = AutoStartN8nFromStartupModeAsync();
                Dispatcher.BeginInvoke(HideStartupWindowToTray);
            }
        };

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(handle);
            source?.AddHook(WndProcHook);

            // 精确获取系统 DPI（覆盖构造函数的预估值）
            var dpi = VisualTreeHelper.GetDpi(this);
            _systemDpi = dpi.DpiScaleX;
            Debug.WriteLine($"[DPI] SourceInitialized — precise system DPI: {_systemDpi:F2}");

            // 移除 WS_THICKFRAME 和 WS_MAXIMIZEBOX 以禁用拉伸和最大化，同时保留 WS_MINIMIZEBOX。
            // WPF ResizeMode="NoResize" 在部分自定义标题栏场景下可能导致任务栏按钮点击无法触发标准最小化；
            // 显式补回 WS_MINIMIZEBOX 后，任务栏图标可恢复“点一下最小化，再点一下还原”的 Windows 默认行为。
            int style = GetWindowLong(handle, GWL_STYLE);
            style &= ~WS_THICKFRAME;
            style &= ~WS_MAXIMIZEBOX;
            style |= WS_MINIMIZEBOX;
            SetWindowLong(handle, GWL_STYLE, style);

            // 强制窗口框架重绘，使样式变更立即生效
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);

            // 应用启动缩放：
            // - 若构造函数已预应用非 1x 尺寸，这里只设置 AppDesignSurface + LayoutScale，
            //   不再修改 Window Width/Height（避免竞态导致 DWM Acrylic 重合成出错）
            // - 若为 1x，走完整 SetWindowScaleCore 统一入口
            if (_startupSizePreset)
            {
                double dsSysDpi = _systemDpi;
                double layoutScale = ComputeLayoutScale(_currentUserScale, dsSysDpi);
                AppDesignSurface.Width  = DESIGN_DIP_WIDTH;
                AppDesignSurface.Height = DESIGN_DIP_HEIGHT;
                LayoutScale.ScaleX = layoutScale;
                LayoutScale.ScaleY = layoutScale;
                double scaledW = DESIGN_DIP_WIDTH  * layoutScale;
                double scaledH = DESIGN_DIP_HEIGHT * layoutScale;
                Width  = scaledW;
                Height = scaledH;
                _targetDipsW = scaledW;
                _targetDipsH = scaledH;
                UpdateLayout();
                // 用实际物理尺寸调用 SetWindowPos 让 DWM 重合成 Acrylic，并同步居中窗口外壳
                int physW = (int)(scaledW * dsSysDpi);
                int physH = (int)(scaledH * dsSysDpi);
                var centeredPos = GetCenteredWindowPosition(handle, physW, physH);
                SetWindowPos(handle, IntPtr.Zero, centeredPos.X, centeredPos.Y, physW, physH,
                    SWP_NOZORDER | SWP_FRAMECHANGED);
                InvalidateRect(handle, IntPtr.Zero, true);
                UpdateScaleMenuCheck(_currentUserScale);
                Debug.WriteLine($"[Startup] Preset path: userScale={_currentUserScale:F4}, layoutScale={layoutScale:F4}, " +
                                $"window(phys)={physW}x{physH}, " +
                                $"center=({centeredPos.X},{centeredPos.Y})");
            }
            else
            {
                ApplyStartupScale();
            }

            ApplyCopilotKeyMode(LoadConfig().CopilotKeyMode, persist: false, showFailureMessage: false);
        };

        // ESC 全局最小化到托盘
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Hide();
                // 内存优化：隐藏到托盘后启动 1 分钟倒计时，到点挂起画板动画并修剪工作集。
                OnWindowHiddenForMemory();
            }
        };

        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;

        LoadVersionInfo();
        _ = UpdateVersionInfoAsync();
    }

    private void SidebarNavButton_Checked(object sender, RoutedEventArgs e)
    {
        // XAML 初始化期间默认选中的启动按钮可能先触发 Checked，此时内容页字段尚未全部创建。
        if (LaunchPage is null || ConsolePage is null || ExecutionPage is null || SettingPage is null || AboutPage is null)
        {
            return;
        }

        ShowSelectedContentPage();
    }

    private void ShowSelectedContentPage()
    {
        if (LaunchPage is null || ConsolePage is null || ExecutionPage is null || SettingPage is null || AboutPage is null)
        {
            return;
        }

        if (ConsoleNavButton.IsChecked == true)
        {
            ShowContentPage(ConsolePage);
        }
        else if (ExecutionNavButton.IsChecked == true)
        {
            ShowContentPage(ExecutionPage);
        }
        else if (SettingNavButton.IsChecked == true)
        {
            ShowContentPage(SettingPage);
        }
        else if (AboutNavButton.IsChecked == true)
        {
            ShowContentPage(AboutPage);
        }
        else
        {
            ShowContentPage(LaunchPage);
        }
    }

    private void ShowContentPage(UIElement activePage)
    {
        LaunchPage.Visibility = activePage == LaunchPage ? Visibility.Visible : Visibility.Collapsed;
        ConsolePage.Visibility = activePage == ConsolePage ? Visibility.Visible : Visibility.Collapsed;
        ExecutionPage.Visibility = activePage == ExecutionPage ? Visibility.Visible : Visibility.Collapsed;
        SettingPage.Visibility = activePage == SettingPage ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = activePage == AboutPage ? Visibility.Visible : Visibility.Collapsed;

        if (activePage == ExecutionPage)
        {
            _ = InitializeExecutionRecordsAsync(syncInBackground: true);
        }
    }

    // ============================================================
    // Win32 消息挂钩：禁用顶栏右键 + 禁止鼠标拉伸边框
    // ============================================================
    private const int GWL_STYLE     = -16;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const int WS_MAXIMIZEBOX = 0x00010000;

    private const int SWP_NOMOVE       = 0x0002;
    private const int SWP_NOSIZE       = 0x0001;
    private const int SWP_NOZORDER     = 0x0004;
    private const int SWP_FRAMECHANGED = 0x0020;

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    // ============================================================
    // 原生 Acrylic 毛玻璃 (SetWindowCompositionAttribute)
    // 相比 DWMWA_SYSTEMBACKDROP_TYPE 提供更重的高斯模糊和完全可控的色调叠加
    //
    // ⚠ 当前状态：未启用（SetAcrylicBlur 无调用点）。
    //   窗口背景现由 WPF-UI 的 WindowBackdropType 接管。
    //   此段（WCA_ACCENT_POLICY / AccentPolicy / SetWindowCompositionAttribute /
    //   SetAcrylicBlur）作为兜底方案刻意保留：若某些 Windows 版本上 WPF-UI 的
    //   Backdrop 失效，可直接在 OnSourceInitialized 中调用 SetAcrylicBlur 回退。
    //   请勿因"未被调用"而删除。
    // ============================================================
    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int  AccentState;      // 4 = ACCENT_ENABLE_ACRYLICBLURBEHIND
        public int  AccentFlags;      // 保留，设为 0
        public uint GradientColor;    // ABGR 格式 (0xAABBGGRR)，alpha 控制色调不透明度
        public int  AnimationId;      // 保留，设为 0
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int    Attribute;   // 19 = WCA_ACCENT_POLICY
        public IntPtr Data;        // 指向 AccentPolicy 的指针
        public int    DataSize;    // sizeof(AccentPolicy)
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(
        IntPtr hwnd, ref WindowCompositionAttributeData data);

    /// <summary>
    /// 启用原生 Acrylic 毛玻璃，使用可配置的色调叠加。
    /// alpha 建议: 40~80 偏透, 80~128 适中介于半透半模糊, 128~200 偏浓雾
    /// </summary>
    /// <remarks>
    /// 当前未启用，保留为 WPF-UI WindowBackdropType 失效时的兜底方案。详见上方分区注释。
    /// </remarks>
    private void SetAcrylicBlur(uint gradientColor)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        var accent = new AccentPolicy
        {
            AccentState  = ACCENT_ENABLE_ACRYLICBLURBEHIND,
            AccentFlags  = 0,
            GradientColor = gradientColor,
            AnimationId  = 0
        };

        var data = new WindowCompositionAttributeData
        {
            Attribute = WCA_ACCENT_POLICY,
            Data     = Marshal.AllocHGlobal(Marshal.SizeOf(accent)),
            DataSize = Marshal.SizeOf(accent)
        };

        try
        {
            Marshal.StructureToPtr(accent, data.Data, false);
            SetWindowCompositionAttribute(handle, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(data.Data);
        }

        Debug.WriteLine($"[Acrylic] Set gradientColor=0x{gradientColor:X8}");
    }

    private void ApplyConfiguredBackdropBeforeHandleCreated()
    {
        try
        {
            bool lowPerformanceMode = LoadConfig().LowPerformanceMode;
            WindowBackdropType = lowPerformanceMode
                ? Wpf.Ui.Controls.WindowBackdropType.None
                : Wpf.Ui.Controls.WindowBackdropType.Acrylic;

            Debug.WriteLine($"[PerformanceMode] Startup backdrop={WindowBackdropType}, lowPerformanceMode={lowPerformanceMode}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PerformanceMode] Apply startup backdrop failed: {ex.Message}");
        }
    }

    private bool IsLowPerformanceModeEnabled()
    {
        try
        {
            return LoadConfig().LowPerformanceMode;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PerformanceMode] Load failed: {ex.Message}");
            return false;
        }
    }

    private void ApplyPerformanceMode()
    {
        // 低性能模式只影响 LaunchTile02 的动画状态：
        // 不运行时切换 FluentWindow.WindowBackdropType，避免 Wpf.Ui 在 SetWindowChrome 内触发 Freezable 上下文异常。
        ForceRefreshLaunchTileVisualState();
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            // 禁止右键弹出系统菜单（标题栏区域）
            case WM_NCRBUTTONUP:
                handled = true;
                return IntPtr.Zero;

            // 锁定窗口尺寸（DIP 转物理像素）
            case WM_GETMINMAXINFO:
            {
                int physW = (int)(_targetDipsW * _systemDpi);
                int physH = (int)(_targetDipsH * _systemDpi);
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                mmi.ptMinTrackSize = new POINT { x = physW, y = physH };
                mmi.ptMaxTrackSize = new POINT { x = physW, y = physH };
                Marshal.StructureToPtr(mmi, lParam, true);
                handled = true;
                return IntPtr.Zero;
            }

            // Copilot 键接管：注册成功时，Win + Shift + F23 按当前设置执行动作。
            // 不直接调 HandleCopilotKeyAction —— WM_HOTKEY 在长按时会被系统自动重复投递，
            // 必须先过一遍 HandleCopilotHotkeyMessage 的"一次物理按键 = 一次动作"判定。
            case WM_HOTKEY:
            {
                if (wParam.ToInt32() == COPILOT_HOTKEY_ID)
                {
                    HandleCopilotHotkeyMessage();
                    handled = true;
                    return IntPtr.Zero;
                }
                break;
            }

            // 参照 WinUI 3 原版 WM_SETCURSOR 策略：边框区域强制设为普通箭头
            case WM_SETCURSOR:
            {
                int hitTest = lParam.ToInt32() & 0xFFFF;
                if (hitTest == HTLEFT || hitTest == HTRIGHT || hitTest == HTTOP ||
                    hitTest == HTBOTTOM || hitTest == HTTOPLEFT || hitTest == HTTOPRIGHT ||
                    hitTest == HTBOTTOMLEFT || hitTest == HTBOTTOMRIGHT)
                {
                    SetCursor(LoadCursor(IntPtr.Zero, IDC_ARROW));
                    handled = true;
                    return (IntPtr)1; // TRUE = 已处理
                }
                break;
            }
        }
        return IntPtr.Zero;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetOpenClipboardWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    // 获取物理屏幕分辨率（不受 DPI 缩放影响）
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
    private const int SM_CYSCREEN = 1; // 主屏幕物理高度（像素）

    // 获取系统 DPI（窗口句柄创建前即可使用）
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    // 强制窗口客户区立即重绘（触发 WPF 重新布局 + DWM 重新合成 Acrylic 背景）
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    // ============================================================
    // 版本信息与启动器状态（data/.Launcher/version.json）
    // ============================================================
    private void LoadVersionInfo()
    {
        var config = LoadConfig();
        RenderVersionInfo(config.Versions);
    }

    // 异步刷新版本信息：C# 内置实现，不再依赖 data/.Launcher/check_version.py
    private async Task UpdateVersionInfoAsync()
    {
        try
        {
            string stateDir = GetConfigDirOrCreate();
            string? portableRoot = GetPortableRootFromStateDir(stateDir);
            if (portableRoot == null)
            {
                Debug.WriteLine($"[VersionInfo] Cannot resolve portable root from {stateDir}");
                return;
            }

            var currentConfig = LoadConfig();
            var versions = currentConfig.Versions;
            versions.Launcher = LAUNCHER_VERSION;

            await RefreshLocalVersionsAsync(portableRoot, versions);
            SaveVersions(versions);
            RenderVersionInfo(versions);

            if (ShouldRefreshLatestVersion(versions))
            {
                string? latest = await QueryLatestN8nVersionAsync(portableRoot);
                var latestConfig = LoadConfig();
                latestConfig.Versions.LatestVersionLastCheckAt = DateTimeOffset.Now.ToString("O");
                latestConfig.Versions.Launcher = LAUNCHER_VERSION;

                if (!string.IsNullOrWhiteSpace(latest))
                {
                    latestConfig.Versions.N8nLatest = latest.Trim();
                    latestConfig.Versions.N8nLatestStatus = "Latest";
                    latestConfig.Versions.LatestVersionUpdatedAt = DateTimeOffset.Now.ToString("O");
                    latestConfig.Versions.LatestVersionLastError = null;
                    Debug.WriteLine($"[VersionInfo] Latest n8n version refreshed: {latest}");
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(latestConfig.Versions.N8nLatest) ||
                        latestConfig.Versions.N8nLatest == "未知")
                    {
                        latestConfig.Versions.N8nLatest = "0.0.0";
                    }

                    latestConfig.Versions.N8nLatestStatus = "Unknown";
                    latestConfig.Versions.LatestVersionLastError = "npm view n8n version failed or timed out";
                    Debug.WriteLine("[VersionInfo] Latest n8n version refresh failed; keeping cache as unknown");
                }

                SaveConfig(latestConfig);
                RenderVersionInfo(latestConfig.Versions);
            }

            // Launcher 远端版本检测：套路与上面 n8n 检测完全一致，
            // 数据源换成仓库根目录 launcher_release.json 的 latest_version 字段。
            var launcherConfigCheck = LoadConfig();
            if (ShouldRefreshLauncherLatestVersion(launcherConfigCheck.Versions))
            {
                string? latestLauncher = await QueryLatestLauncherVersionAsync();
                var launcherConfig = LoadConfig();
                launcherConfig.Versions.LauncherLatestLastCheckAt = DateTimeOffset.Now.ToString("O");
                launcherConfig.Versions.Launcher = LAUNCHER_VERSION;

                if (!string.IsNullOrWhiteSpace(latestLauncher))
                {
                    launcherConfig.Versions.LauncherLatest = latestLauncher.Trim();
                    launcherConfig.Versions.LauncherLatestStatus = "Latest";
                    launcherConfig.Versions.LauncherLatestUpdatedAt = DateTimeOffset.Now.ToString("O");
                    launcherConfig.Versions.LauncherLatestLastError = null;
                    Debug.WriteLine($"[VersionInfo] Latest Launcher version refreshed: {latestLauncher}");
                }
                else
                {
                    // 失败：保留旧缓存版本号，仅把 status 降级为 Unknown。
                    if (string.IsNullOrWhiteSpace(launcherConfig.Versions.LauncherLatest) ||
                        launcherConfig.Versions.LauncherLatest == "未知")
                    {
                        launcherConfig.Versions.LauncherLatest = "0.0.0";
                    }

                    launcherConfig.Versions.LauncherLatestStatus = "Unknown";
                    launcherConfig.Versions.LauncherLatestLastError = "launcher_release.json fetch failed or timed out";
                    Debug.WriteLine("[VersionInfo] Latest Launcher version refresh failed; keeping cache as unknown");
                }

                SaveConfig(launcherConfig);
                RenderVersionInfo(launcherConfig.Versions);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VersionInfo] Refresh error: {ex.Message}");
        }
    }

    private enum WorkspaceDataRefreshReason
    {
        Startup,
        TrayOpen,
        Manual
    }

    private async Task RefreshWorkspaceDataAsync(WorkspaceDataRefreshReason reason)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        if (reason != WorkspaceDataRefreshReason.Manual &&
            _lastSuccessfulWorkspaceDataRefreshAt is not null &&
            now - _lastSuccessfulWorkspaceDataRefreshAt.Value < WorkspaceDataAutoRefreshCooldown)
        {
            Debug.WriteLine($"[WorkspaceDataRefresh] Skip auto refresh within cooldown; reason={reason}; lastSuccess={_lastSuccessfulWorkspaceDataRefreshAt:O}");
            return;
        }

        if (_isWorkspaceDataRefreshRunning)
        {
            Debug.WriteLine($"[WorkspaceDataRefresh] Skip overlapping refresh; reason={reason}");
            return;
        }

        _isWorkspaceDataRefreshRunning = true;
        try
        {
            bool statsRefreshed = await RefreshExecutionStatsAsync(forceRefresh: reason == WorkspaceDataRefreshReason.Manual);
            bool recordsRefreshed = await RefreshExecutionRecordsForWorkspaceDataRefreshAsync(reason);

            if (statsRefreshed)
            {
                _lastSuccessfulWorkspaceDataRefreshAt = DateTimeOffset.Now;
            }

            Debug.WriteLine($"[WorkspaceDataRefresh] Completed; reason={reason}; stats={statsRefreshed}; records={recordsRefreshed}");
        }
        finally
        {
            _isWorkspaceDataRefreshRunning = false;
        }
    }

    private async Task<bool> RefreshExecutionRecordsForWorkspaceDataRefreshAsync(WorkspaceDataRefreshReason reason)
    {
        Debug.WriteLine($"[WorkspaceDataRefresh] Refresh execution records; reason={reason}");
        await InitializeExecutionRecordsAsync(syncInBackground: false);
        return await SyncExecutionRecordsAsync(manualRefresh: reason == WorkspaceDataRefreshReason.Manual);
    }

    private async Task<bool> RefreshExecutionStatsAsync(bool forceRefresh = false)
    {
        RenderExecutionStats(N8nExecutionStats.Empty());

        bool hasRenderedCache = false;

        try
        {
            string stateDir = GetConfigDirOrCreate();
            string? portableRoot = GetPortableRootFromStateDir(stateDir);
            if (portableRoot == null)
            {
                Debug.WriteLine($"[ExecutionStats] Cannot resolve portable root from {stateDir}");
                RenderExecutionStats(N8nExecutionStats.Empty("Cannot resolve portable root"));
                return false;
            }

            var cacheService = new N8nExecutionStatsCacheService();
            N8nExecutionStatsCacheEntry? cacheEntry = await cacheService.ReadAsync(stateDir);
            if (cacheEntry?.Stats is not null)
            {
                RenderExecutionStats(cacheEntry.Stats);
                hasRenderedCache = true;
                Debug.WriteLine($"[ExecutionStats] Rendered cached stats from {N8nExecutionStatsCacheService.GetCachePath(stateDir)}; forceRefresh={forceRefresh}");
            }

            var service = new N8nExecutionStatsService();
            DateTimeOffset? databaseLastWriteTimeUtc = N8nExecutionStatsService.GetDatabaseLastWriteTimeUtc(portableRoot);
            var stats = await service.ReadLast7DaysAsync(portableRoot);
            if (string.IsNullOrWhiteSpace(stats.ErrorMessage))
            {
                await cacheService.WriteAsync(stateDir, stats, databaseLastWriteTimeUtc);
                RenderExecutionStats(stats);
                return true;
            }

            Debug.WriteLine($"[ExecutionStats] Fresh SQLite stats unavailable: {stats.ErrorMessage}");
            if (!hasRenderedCache)
            {
                RenderExecutionStats(stats);
            }

            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ExecutionStats] UI refresh error: {ex.Message}");
            if (!hasRenderedCache)
            {
                RenderExecutionStats(N8nExecutionStats.Empty(ex.Message));
            }

            return false;
        }
    }

    private void RenderExecutionStats(N8nExecutionStats stats)
    {
        Dispatcher.Invoke(() =>
        {
            if (ExecutionTotalValueText is null ||
                ExecutionFailedValueText is null ||
                ExecutionFailureRateValueText is null ||
                ExecutionAverageRuntimeValueText is null ||
                ExecutionTotalSparkline is null ||
                ExecutionFailedSparkline is null ||
                ExecutionFailureRateSparkline is null ||
                ExecutionAverageRuntimeSparkline is null ||
                LaunchTile01 is null)
            {
                return;
            }

            ExecutionTotalValueText.Text = stats.TotalCount.ToString();
            ExecutionFailedValueText.Text = stats.FailedCount.ToString();
            // 与 n8n 官方对齐：0~1 小数 ×100 → 1 位小数（与官方前端 toFixed(1) 一致）
            ExecutionFailureRateValueText.Text = $"{stats.FailureRatePercent * 100:0.0}%";
            ExecutionAverageRuntimeValueText.Text = FormatRuntimeSeconds(stats.AverageRuntimeSeconds);

            IReadOnlyList<N8nExecutionStatsDailyPoint> dailyPoints = stats.DailyPoints ?? Array.Empty<N8nExecutionStatsDailyPoint>();
            RenderSparkline(ExecutionTotalSparkline, dailyPoints.Select(point => (double)point.TotalCount));
            RenderSparkline(ExecutionFailedSparkline, dailyPoints.Select(point => (double)point.FailedCount));
            RenderSparkline(ExecutionFailureRateSparkline, dailyPoints.Select(point => point.FailureRatePercent));
            RenderSparkline(ExecutionAverageRuntimeSparkline, dailyPoints.Select(point => point.AverageRuntimeSeconds ?? 0));

            LaunchTile01.ToolTip = null;
        });
    }

    private static void RenderSparkline(WpfPath path, IEnumerable<double> sourceValues)
    {
        const double left = 8;
        const double top = 8;
        const double width = 72;
        const double height = 40;

        var values = sourceValues
            .Select(value => double.IsNaN(value) || double.IsInfinity(value) ? 0 : Math.Max(0, value))
            .ToList();

        path.Data = null;
        if (values.Count == 0)
        {
            return;
        }

        double min = values.Min();
        double max = values.Max();
        double range = max - min;
        double xStep = values.Count > 1 ? width / (values.Count - 1) : 0;

        var points = new List<WpfPoint>(values.Count);
        for (int i = 0; i < values.Count; i++)
        {
            double x = left + (values.Count > 1 ? i * xStep : width / 2);
            // range=0：所有点值相同。此时若值为 0，让折线贴在底轴上（normalized=0）以真实反映"零数据"；
            // 若值为同一个非零值，则画一条居中横线（normalized=0.5）表示"有数据但无变化"。
            double normalized = range > 0
                ? (values[i] - min) / range
                : (values[i] > 0 ? 0.5 : 0.0);
            double y = top + height - normalized * height;
            points.Add(new WpfPoint(x, y));
        }

        if (points.Count == 1)
        {
            points.Insert(0, new WpfPoint(left, points[0].Y));
            points.Add(new WpfPoint(left + width, points[0].Y));
        }

        var figure = new PathFigure
        {
            StartPoint = points[0],
            IsClosed = false,
            IsFilled = false
        };

        for (int i = 0; i < points.Count - 1; i++)
        {
            WpfPoint current = points[i];
            WpfPoint next = points[i + 1];
            double controlOffset = (next.X - current.X) * 0.45;

            figure.Segments.Add(new BezierSegment(
                new WpfPoint(current.X + controlOffset, current.Y),
                new WpfPoint(next.X - controlOffset, next.Y),
                next,
                isStroked: true));
        }

        path.Data = new PathGeometry(new[] { figure });
    }

    private async Task InitializeExecutionRecordsAsync(bool syncInBackground)
    {
        if (_executionRecordsInitialized)
        {
            if (syncInBackground && _executionRecords.Count == 0)
            {
                _ = SyncExecutionRecordsAsync(manualRefresh: false);
            }
            return;
        }

        _executionRecordsInitialized = true;
        await LoadExecutionRecordsCacheAsync();

        if (syncInBackground)
        {
            _ = SyncExecutionRecordsAsync(manualRefresh: false);
        }
    }

    private async Task LoadExecutionRecordsCacheAsync()
    {
        try
        {
            SetExecutionRecordsStatus("正在读取缓存...");
            string stateDir = GetConfigDirOrCreate();
            N8nExecutionListCacheEntry? cacheEntry = await _executionListService.ReadCacheAsync(stateDir);
            IReadOnlyList<N8nExecutionListRecord> records = cacheEntry?.Records ?? new List<N8nExecutionListRecord>();
            RenderExecutionRecords(records, cacheEntry?.CachedAt, null);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ExecutionList] Cache UI load failed: {ex.Message}");
            RenderExecutionRecords(Array.Empty<N8nExecutionListRecord>(), null, ex.Message);
        }
    }

    private async Task<bool> SyncExecutionRecordsAsync(bool manualRefresh)
    {
        if (_isExecutionRecordsSyncing)
        {
            return false;
        }

        _isExecutionRecordsSyncing = true;
        Dispatcher.Invoke(() =>
        {
            SetExecutionRecordsStatus(manualRefresh ? "正在手动刷新..." : "正在后台同步...");
            if (ExecutionRecordsRefreshTile is not null)
            {
                ExecutionRecordsRefreshTile.IsEnabled = false;
                ExecutionRecordsRefreshTile.Opacity = 0.62;
            }
        });

        try
        {
            string stateDir = GetConfigDirOrCreate();
            string? portableRoot = GetPortableRootFromStateDir(stateDir);
            if (portableRoot == null)
            {
                RenderExecutionRecords(_executionRecords, null, "Cannot resolve portable root");
                return false;
            }

            N8nExecutionListSyncResult result = await _executionListService.SyncLast7DaysAsync(stateDir, portableRoot);
            RenderExecutionRecords(result.Records, DateTimeOffset.Now, result.ErrorMessage);
            return string.IsNullOrWhiteSpace(result.ErrorMessage);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ExecutionList] UI sync failed: {ex.Message}");
            RenderExecutionRecords(_executionRecords, null, ex.Message);
            return false;
        }
        finally
        {
            _isExecutionRecordsSyncing = false;
            Dispatcher.Invoke(() =>
            {
                if (ExecutionRecordsRefreshTile is not null)
                {
                    ExecutionRecordsRefreshTile.IsEnabled = true;
                    ExecutionRecordsRefreshTile.Opacity = 1.0;
                }
            });
        }
    }

    private void RenderExecutionRecords(
        IReadOnlyList<N8nExecutionListRecord> records,
        DateTimeOffset? cachedAt,
        string? errorMessage)
    {
        Dispatcher.Invoke(() =>
        {
            if (ExecutionRecordsListPanel is null || ExecutionRecordsStatusText is null)
            {
                return;
            }

            _executionRecords.Clear();
            _executionRecords.AddRange(records.OrderByDescending(static record => record.StartedAt ?? DateTimeOffset.MinValue));
            _filteredExecutionRecords.Clear();
            _filteredExecutionRecords.AddRange(_executionRecords);
            ApplyExecutionRecordsSort(_filteredExecutionRecords);
            RenderExecutionWorkflowFilterOptions();
            RenderExecutionStatusFilterOptions();
            _executionRecordsVisibleCount = 0;
            ExecutionRecordsListPanel.Children.Clear();
            AppendExecutionRecordsPage();
            UpdateExecutionRecordsStatus(cachedAt, errorMessage);
            UpdateExecutionSortIndicators();
        });
    }

    private void RenderExecutionWorkflowFilterOptions()
    {
        if (ExecutionRecordsWorkflowFilterPopup?.Child is not Border popupBorder ||
            popupBorder.Child is not StackPanel itemsPanel)
        {
            return;
        }

        itemsPanel.Children.Clear();
        itemsPanel.Children.Add(CreateExecutionWorkflowFilterOptionText("全部", null, Brushes.Black));

        IEnumerable<string> workflowNames = _executionRecords
            .Select(static record => string.IsNullOrWhiteSpace(record.WorkflowName) ? "Unknown workflow" : record.WorkflowName.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static workflowName => workflowName, StringComparer.CurrentCultureIgnoreCase);

        foreach (string workflowName in workflowNames)
        {
            itemsPanel.Children.Add(CreateExecutionWorkflowFilterOptionText(workflowName, workflowName, Brushes.Black));
        }
    }

    private bool _isWorkflowFilterPopupHovered;
    private bool _isStatusFilterPopupHovered;

    private void ExecutionRecordsWorkflowFilterHoverArea_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isWorkflowFilterPopupHovered = true;
        if (ExecutionRecordsWorkflowFilterPopup is not null)
        {
            ExecutionRecordsWorkflowFilterPopup.IsOpen = true;
        }
    }

    private void ExecutionRecordsWorkflowFilterHoverArea_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isWorkflowFilterPopupHovered = false;
        _ = DelayedClosePopupAsync(ExecutionRecordsWorkflowFilterPopup, () => _isWorkflowFilterPopupHovered);
    }

    private void ExecutionRecordsStatusFilterHoverArea_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isStatusFilterPopupHovered = true;
        if (ExecutionRecordsStatusFilterPopup is not null)
        {
            ExecutionRecordsStatusFilterPopup.IsOpen = true;
        }
    }

    private void ExecutionRecordsStatusFilterHoverArea_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isStatusFilterPopupHovered = false;
        _ = DelayedClosePopupAsync(ExecutionRecordsStatusFilterPopup, () => _isStatusFilterPopupHovered);
    }

    /// <summary>
    /// Popup Placement=Custom 回调：使 Popup 水平居中于表头文字锚点（Grid，即包裹 "Workflow"/"Status" 文字与 ∧∨ 指示器的容器）。
    /// targetSize 是锚点渲染尺寸（DIP），popupSize 是 Popup 内容尺寸；返回的 Point 相对锚点左上角。
    /// Y 使用锚点底部 - 9 DIP，叠加 XAML VerticalOffset=-2 后，Popup 顶到表头文字底沿距离 = 7 DIP ≈ 10.5 PS px。
    /// （表头 Border 48 DIP、文字 FontSize=16 VerticalAlignment=Center，文字底沿 ≈ 30 DIP；48-9-2-30 = 7 DIP。）
    /// 若未来表头行高不再是 48 DIP 或 anchor Grid 布局变化，需重新校准这个常量。
    /// </summary>
    private static CustomPopupPlacement[] CenterFilterPopupPlacement(System.Windows.Size popupSize, System.Windows.Size targetSize, System.Windows.Point offset)
    {
        double x = (targetSize.Width - popupSize.Width) / 2.0;
        double y = targetSize.Height - 9;
        return new[]
        {
            new CustomPopupPlacement(new System.Windows.Point(x, y), PopupPrimaryAxis.Horizontal)
        };
    }

    private static async Task DelayedClosePopupAsync(Popup? popup, Func<bool> isHovered)
    {
        if (popup is null)
        {
            return;
        }

        await Task.Delay(500);
        if (isHovered())
        {
            return;
        }

        popup.IsOpen = false;
    }

    // 执行记录页筛选下拉行：斑马纹 + 悬停下划线，尺寸/字号统一在这里控制。
    // 由 RenderExecutionWorkflowFilterOptions / RenderExecutionStatusFilterOptions 添加到 StackPanel.Children，
    // 返回类型仍是 UIElement（Border），Children.Add 兼容。
    // 筛选下拉行的斑马 / 悬停 / 文字色已全部改为资源键（见 XAML 中 ExecutionFilter* 六个键），
    // 由 CreateExecutionFilterOptionRow 通过 SetResourceReference 动态挂载，
    // 因此原先的 static readonly Brush 字段（White / #F5F5F5）已删除。
    // ★ 不要恢复这两个字段：直接塞画刷实例会让行脱离 DynamicResource，切主题后不重刷。

    private Border CreateExecutionWorkflowFilterOptionText(string text, string? workflowName, Brush foreground)
    {
        return CreateExecutionFilterOptionRow(text, foreground, () => ApplyExecutionWorkflowFilter(workflowName));
    }

    private void ApplyExecutionWorkflowFilter(string? workflowName)
    {
        _selectedExecutionWorkflowName = workflowName;
        RebuildFilteredExecutionRecords();
        if (ExecutionRecordsWorkflowFilterPopup is not null)
        {
            ExecutionRecordsWorkflowFilterPopup.IsOpen = false;
        }
    }

    private void ApplyExecutionStatusFilter(string? statusName)
    {
        _selectedExecutionStatusName = statusName;
        RebuildFilteredExecutionRecords();
        if (ExecutionRecordsStatusFilterPopup is not null)
        {
            ExecutionRecordsStatusFilterPopup.IsOpen = false;
        }
    }

    private void RebuildFilteredExecutionRecords()
    {
        _filteredExecutionRecords.Clear();

        IEnumerable<N8nExecutionListRecord> filtered = _executionRecords;

        if (!string.IsNullOrWhiteSpace(_selectedExecutionWorkflowName))
        {
            string workflowName = _selectedExecutionWorkflowName;
            filtered = filtered.Where(record =>
                string.Equals(
                    string.IsNullOrWhiteSpace(record.WorkflowName) ? "Unknown workflow" : record.WorkflowName.Trim(),
                    workflowName,
                    StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(_selectedExecutionStatusName))
        {
            string statusName = _selectedExecutionStatusName;
            filtered = filtered.Where(record =>
                string.Equals(record.Status, statusName, StringComparison.OrdinalIgnoreCase));
        }

        _filteredExecutionRecords.AddRange(filtered);
        ApplyExecutionRecordsSort(_filteredExecutionRecords);

        if (ExecutionRecordsListPanel is not null)
        {
            ExecutionRecordsListPanel.Children.Clear();
        }

        _executionRecordsVisibleCount = 0;

        if (_filteredExecutionRecords.Count == 0 && ExecutionRecordsListPanel is not null)
        {
            ExecutionRecordsListPanel.Children.Add(CreateExecutionRecordsEmptyState(BuildExecutionRecordsEmptyMessage()));
        }
        else
        {
            AppendExecutionRecordsPage();
        }
    }

    private string BuildExecutionRecordsEmptyMessage()
    {
        if (string.IsNullOrWhiteSpace(_selectedExecutionWorkflowName) && string.IsNullOrWhiteSpace(_selectedExecutionStatusName))
        {
            return "暂无执行记录";
        }

        System.Collections.Generic.List<string> parts = new();
        if (!string.IsNullOrWhiteSpace(_selectedExecutionWorkflowName))
        {
            parts.Add($"工作流 \"{_selectedExecutionWorkflowName}\"");
        }
        if (!string.IsNullOrWhiteSpace(_selectedExecutionStatusName))
        {
            parts.Add($"状态 \"{FormatExecutionStatus(_selectedExecutionStatusName)}\"");
        }

        return $"未找到{string.Join(" & ", parts)} 的执行记录";
    }

    private void RenderExecutionStatusFilterOptions()
    {
        if (ExecutionRecordsStatusFilterPopup?.Child is not Border popupBorder ||
            popupBorder.Child is not StackPanel itemsPanel)
        {
            return;
        }

        itemsPanel.Children.Clear();
        itemsPanel.Children.Add(CreateExecutionStatusFilterOptionText("全部", null, Brushes.Black));

        IEnumerable<string> statusNames = _executionRecords
            .Select(record => record.Status)
            .Where(status => !string.IsNullOrWhiteSpace(status))
            .Select(status => status!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static status => status.ToLowerInvariant() switch
            {
                "success" => 0,
                "running" => 1,
                "new" => 2,
                "waiting" => 3,
                "failed" => 4,
                "error" => 5,
                "crashed" => 6,
                "canceled" => 7,
                _ => 8
            })
            .ThenBy(static status => status, StringComparer.CurrentCultureIgnoreCase);

        foreach (string statusName in statusNames)
        {
            itemsPanel.Children.Add(CreateExecutionStatusFilterOptionText(FormatExecutionStatus(statusName), statusName, Brushes.Black));
        }
    }

    private Border CreateExecutionStatusFilterOptionText(string text, string? statusName, Brush foreground)
    {
        return CreateExecutionFilterOptionRow(text, foreground, () => ApplyExecutionStatusFilter(statusName));
    }

    /// <summary>
    /// 执行记录页筛选下拉行统一工厂：
    ///  - 字号 13 / 行高 26
    ///  - 斑马纹：偶数行 ExecutionFilterRowEvenBrush（浅 White / 深 #2B2B2B）
    ///            奇数行 ExecutionFilterRowOddBrush（浅 #F5F5F5 / 深 #242424）
    ///  - 悬停：ExecutionFilterRowHoverBrush（浅 #E6EEF8 / 深 #1F2021）+ 文本下划线
    ///  - 文字：ExecutionFilterTextBrush（浅 Black / 深 White）
    /// ★ 全部取色一律走 SetResourceReference，不允许直接塞画刷实例：
    ///   这些行是 C# 动态创建的，直接赋值等于写"本地属性"，优先级高于 DynamicResource，
    ///   切换深/浅色时已渲染（尤其是 hover 过的）行不会重刷，会卡在旧主题色。
    /// ★ foreground 形参保留是为了不改动 4 个调用点；方法内会用资源键覆盖它。
    ///   浅色下 ExecutionFilterTextBrush = Black，与调用点传入的 Brushes.Black 渲染结果逐位相同。
    /// </summary>
    private static Border CreateExecutionFilterOptionRow(string text, Brush foreground, Action onClick)
    {
        // rowIndex 由调用方 StackPanel 当前 Children.Count 决定；这里保留 static 方法通过后续 Loaded 事件再刷斑马色。
        var textBlock = new WpfTextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 0, 8, 0),
            FontSize = 13,
            Foreground = foreground,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = text,
            IsHitTestVisible = false
        };
        // 覆盖上面的 foreground 本地值，改挂主题资源键（浅色渲染结果不变）。
        textBlock.SetResourceReference(WpfTextBlock.ForegroundProperty, "ExecutionFilterTextBrush");

        var row = new Border
        {
            Height = 26,
            CornerRadius = new CornerRadius(2),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = textBlock
        };
        // 初始按"偶数行"挂载；真实索引在下方 Loaded 里确定后再重挂。
        row.SetResourceReference(Border.BackgroundProperty, "ExecutionFilterRowEvenBrush");

        // 当前行在父 StackPanel 中的索引。Loaded 里确定，MouseLeave 还原时复用，
        // 避免"缓存画刷实例"导致离开 hover 后脱离 DynamicResource。
        int rowIndex = 0;

        // 加入到 StackPanel 后，再由父容器 Loaded 事件按索引应用斑马纹。
        row.Loaded += (_, _) =>
        {
            if (row.Parent is System.Windows.Controls.Panel parent)
            {
                int index = parent.Children.IndexOf(row);
                if (index >= 0)
                {
                    rowIndex = index;
                    row.SetResourceReference(
                        Border.BackgroundProperty,
                        index % 2 == 0 ? "ExecutionFilterRowEvenBrush" : "ExecutionFilterRowOddBrush");
                }
            }
        };

        row.MouseEnter += (_, _) =>
        {
            row.SetResourceReference(Border.BackgroundProperty, "ExecutionFilterRowHoverBrush");
            textBlock.TextDecorations = TextDecorations.Underline;
        };
        row.MouseLeave += (_, _) =>
        {
            // 按索引重新挂回斑马键，而不是还原旧画刷实例 —— 保持 DynamicResource 活性。
            row.SetResourceReference(
                Border.BackgroundProperty,
                rowIndex % 2 == 0 ? "ExecutionFilterRowEvenBrush" : "ExecutionFilterRowOddBrush");
            textBlock.TextDecorations = null;
        };
        row.MouseLeftButtonUp += (_, _) => onClick();

        return row;
    }

    private static WpfTextBlock CreateExecutionRecordsEmptyState(string message)
    {
        return new WpfTextBlock
        {
            Text = message,
            Height = 80,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
    }

    private void AppendExecutionRecordsPage()
    {
        if (ExecutionRecordsListPanel is null)
        {
            return;
        }

        int nextCount = Math.Min(_filteredExecutionRecords.Count, _executionRecordsVisibleCount + ExecutionRecordsPageSize);
        for (int i = _executionRecordsVisibleCount; i < nextCount; i++)
        {
            ExecutionRecordsListPanel.Children.Add(CreateExecutionRecordRow(_filteredExecutionRecords[i], i));
        }

        _executionRecordsVisibleCount = nextCount;
        UpdateExecutionRecordsStatus(null, null);
    }

    private Grid CreateExecutionRecordRow(N8nExecutionListRecord record, int index)
    {
        var row = new Grid
        {
            Height = 34,
            // 偶数行永远 Transparent：透出的是 ExecutionRecordsPanel 底板
            //   （浅色 White / 深色 #2B2B2B，见 ExecutionTablePanelBrush），
            //   因此"表格整体底色"只需改底板一处，偶数行自动跟随。
            Background = Brushes.Transparent,
            // 禁用 Row 的键盘焦点框：切换桌面/窗口回前台时，WPF 会把焦点还给上一次收到焦点的行，
            // 默认 FocusVisualStyle 会把整行画成黑色虚线矩形，视觉上就像"整条执行记录被黑框套住"。
            Focusable = false,
            FocusVisualStyle = null
        };

        // 奇数行斑马纹：必须用 SetResourceReference 而不是 new SolidColorBrush(...)。
        // 这些行是 C# 动态创建的，若直接塞画刷实例，切换深/浅色时已渲染的行不会重刷。
        // 浅色 #EEF4F9（与改造前硬编码逐位相同）/ 深色 #1F2021。
        if (index % 2 != 0)
        {
            // 全限定 System.Windows.Controls.Panel：本文件 using 了 UseWindowsForms，
            // 裸写 Panel 会与 System.Windows.Forms.Panel 冲突（CS0104）。
            row.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "ExecutionRowAltBrush");
        }

        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(356) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });

        row.Children.Add(CreateExecutionWorkflowCell(record));
        // Status 列保持状态色（绿/红/蓝），不接主题画刷。
        row.Children.Add(CreateExecutionRecordCell(FormatExecutionStatus(record.Status), 1, GetExecutionStatusBrush(record.Status)));
        // Started / Run time / Exec. ID 三列走主题文字色：浅色 Black（原值）/ 深色 White。
        row.Children.Add(CreateExecutionThemedTextCell(FormatExecutionStarted(record.StartedAt), 2));
        row.Children.Add(CreateExecutionThemedTextCell(FormatExecutionRuntimeSeconds(record.RunTimeSeconds), 3));
        row.Children.Add(CreateExecutionThemedTextCell(record.ExecutionId, 4));

        return row;
    }

    /// <summary>
    /// 创建"文字色随主题切换"的数据单元格（Started / Run time / Exec. ID 三列专用）。
    /// 先按原逻辑造出 Brushes.Black 的单元格，再挂 DynamicResource 覆盖 Foreground —
    /// 这样 CreateExecutionRecordCell 的签名与内部逻辑完全不动，浅色渲染结果不变。
    /// </summary>
    private static WpfTextBlock CreateExecutionThemedTextCell(string text, int column)
    {
        WpfTextBlock cell = CreateExecutionRecordCell(text, column, Brushes.Black);
        cell.SetResourceReference(WpfTextBlock.ForegroundProperty, "ExecutionCellTextBrush");
        return cell;
    }

    private WpfTextBlock CreateExecutionWorkflowCell(N8nExecutionListRecord record)
    {
        WpfTextBlock cell = CreateExecutionRecordCell(record.WorkflowName, 0, Brushes.Black);
        if (string.IsNullOrWhiteSpace(record.WorkflowId) || string.IsNullOrWhiteSpace(record.ExecutionId))
        {
            return cell;
        }

        string url = BuildExecutionDetailUrl(record);
        cell.Foreground = new SolidColorBrush(Color.FromRgb(0x25, 0x6B, 0xEB));
        cell.Cursor = System.Windows.Input.Cursors.Hand;
        // 去掉默认下划线：仅在鼠标悬浮行时（见 CreateExecutionRecordRow 中 MouseEnter/Leave 事件）显示。
        // 不再挂 ToolTip：避免悬停单元格弹出 n8n URL 提示。
        cell.MouseLeftButtonUp += (_, _) => OpenUrl(url);
        return cell;
    }

    // 各数据列 MaxWidth：直接等于列宽，不做任何缩减。
    //   Workflow(534) / Status(240) / Started(300) / Run time(240) / Exec. ID(240)
    private static readonly double[] ExecutionRecordCellMaxWidths = new double[] { 356, 160, 200, 160, 160 };

    private static WpfTextBlock CreateExecutionRecordCell(string text, int column, Brush foreground)
    {
        var cell = new WpfTextBlock
        {
            Text = text,
            // 单元格居中 + 上下左右统一 8 DIP 边距，避免右侧空白也响应点击。
            Margin = new Thickness(8, 0, 8, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            FontSize = 13,
            Foreground = foreground,
            TextTrimming = TextTrimming.CharacterEllipsis
            // 不再挂 ToolTip：避免悬停单元格弹出与单元格文本内容重复的提示气泡。
        };

        if (column >= 0 && column < ExecutionRecordCellMaxWidths.Length)
        {
            cell.MaxWidth = ExecutionRecordCellMaxWidths[column];
        }

        Grid.SetColumn(cell, column);
        return cell;
    }

    private void UpdateExecutionRecordsStatus(DateTimeOffset? cachedAt, string? errorMessage)
    {
        if (ExecutionRecordsStatusText is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            ExecutionRecordsStatusText.Text = $"读取失败：{errorMessage}";
            return;
        }

        ExecutionRecordsStatusText.Text = string.Empty;
    }

    private void SetExecutionRecordsStatus(string text)
    {
        if (ExecutionRecordsStatusText is not null)
        {
            ExecutionRecordsStatusText.Text = text;
        }
    }

    private void ExecutionRecordsRefreshTile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _ = RefreshWorkspaceDataAsync(WorkspaceDataRefreshReason.Manual);
    }

    // ============================================================
    // ⚙ 调试用：控制台页右上角字体切换磁贴
    //  - 状态已持久化：version.json 的 ConsoleFontMode 字段。
    //    "Yahei"     = 默认，走 App.xaml 的 LauncherFontFamily（微软雅黑优先）
    //    "SourceHan" = 强制内嵌思源黑体 CN
    //  - 点击切换时立即写入 version.json → 重启后保留。
    //  - 删除 version.json → 所有字段被重置 → ConsoleFontMode 回 "Yahei"。
    //  - 直接对 this.FontFamily 赋值即可覆盖 XAML 从资源继承来的 FontFamily，
    //    所有子控件通过 inherited property 立即刷新，无需重启窗口。
    // ============================================================
    private bool _consoleFontUseSourceHan = false;

    /// <summary>从 version.json 加载字体状态并应用。</summary>
    private void ApplyConsoleFontFromConfig()
    {
        var config = LoadConfig();
        bool isSourceHan = string.Equals(config.ConsoleFontMode, "SourceHan", StringComparison.OrdinalIgnoreCase);
        _consoleFontUseSourceHan = isSourceHan;
        ApplyConsoleFont(isSourceHan);
        if (ConsoleFontSwitchTileGlyph is not null)
        {
            ConsoleFontSwitchTileGlyph.Text = isSourceHan ? "S" : "Y";
        }
        if (ConsoleFontSwitchTile is not null)
        {
            ConsoleFontSwitchTile.ToolTip = isSourceHan
                ? "当前：思源黑体（点击切回默认）"
                : "当前：默认（点击切到思源）";
        }
    }

    /// <summary>按 _consoleFontUseSourceHan 状态设置窗口级 FontFamily。
    /// 注意：字体链末尾必须是 Segoe UI Emoji，不能提前——WPF 会把
    /// Segoe UI Emoji 当作"能渲染任意字符"的通用字体接管中文渲染，
    /// 导致汉字被 fallback 到内嵌思源，出现"整体字体变成思源"的现象。</summary>
    private void ApplyConsoleFont(bool useSourceHan)
    {
        string ff = useSourceHan
            ? "Segoe UI, pack://application:,,,/n8n_launcher_Gv;component/Assets/Fonts/#Source Han Sans CN, Segoe UI Emoji"
            : "Segoe UI, Microsoft YaHei UI, 微软雅黑, pack://application:,,,/n8n_launcher_Gv;component/Assets/Fonts/#Source Han Sans CN, Segoe UI Emoji";
        this.FontFamily = new System.Windows.Media.FontFamily(ff);
    }

    private void ConsoleFontSwitchTile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _consoleFontUseSourceHan = !_consoleFontUseSourceHan;
        ApplyConsoleFont(_consoleFontUseSourceHan);
        if (ConsoleFontSwitchTileGlyph is not null)
        {
            ConsoleFontSwitchTileGlyph.Text = _consoleFontUseSourceHan ? "S" : "Y";
        }
        if (ConsoleFontSwitchTile is not null)
        {
            ConsoleFontSwitchTile.ToolTip = _consoleFontUseSourceHan
                ? "当前：思源黑体（点击切回默认）"
                : "当前：默认（点击切到思源）";
        }
        // 持久化到 version.json
        var config = LoadConfig();
        config.ConsoleFontMode = _consoleFontUseSourceHan ? "SourceHan" : "Yahei";
        SaveConfig(config);
    }

    // ============================================================
    // 执行记录页表头点击 → 五列排序（两态循环）
    //  - 初始态：Started 降序（最新在前），等价于旧版「无自定义排序」的自然顺序
    //  - 点击当前列：反转升/降方向
    //  - 点击其他列：切换到该列，并使用该列的默认方向
    //      · Workflow / Status → 首次默认升序（字母表 A→Z 更自然）
    //      · Started / RunTime / ExecId → 首次默认降序（最新/最大在前）
    //  - 各列采用稳定二级键（StartedAt DESC）避免相邻同值行乱跳
    // ============================================================
    private void ExecutionSortHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string tagText)
        {
            return;
        }

        if (!Enum.TryParse<ExecutionSortField>(tagText, ignoreCase: true, out ExecutionSortField clickedField))
        {
            return;
        }

        if (_executionSortField == clickedField)
        {
            // 点击的是当前列：反转方向
            _executionSortAscending = !_executionSortAscending;
        }
        else
        {
            // 点击的是新列：切换列，并使用该列的默认方向
            _executionSortField = clickedField;
            _executionSortAscending = GetDefaultAscendingFor(clickedField);
        }

        ApplyExecutionRecordsSort(_filteredExecutionRecords);
        if (ExecutionRecordsListPanel is not null)
        {
            ExecutionRecordsListPanel.Children.Clear();
        }
        _executionRecordsVisibleCount = 0;

        if (_filteredExecutionRecords.Count == 0 && ExecutionRecordsListPanel is not null)
        {
            ExecutionRecordsListPanel.Children.Add(CreateExecutionRecordsEmptyState(BuildExecutionRecordsEmptyMessage()));
        }
        else
        {
            AppendExecutionRecordsPage();
        }

        UpdateExecutionSortIndicators();
        e.Handled = true;
    }

    /// <summary>
    /// 各列首次点击（切换到该列时）的默认排序方向：
    ///  - Workflow / Status → 升序（字母表 A→Z 更自然）
    ///  - Started / RunTime / ExecId → 降序（最新/最大在前）
    /// </summary>
    private static bool GetDefaultAscendingFor(ExecutionSortField field)
    {
        return field switch
        {
            ExecutionSortField.Workflow => true,
            ExecutionSortField.Status => true,
            _ => false
        };
    }

    /// <summary>
    /// 按 _executionSortField / _executionSortAscending 就地重新排序目标列表。
    /// 二级稳定键统一使用 StartedAt DESC，避免相邻同值行位置抖动。
    /// </summary>
    private void ApplyExecutionRecordsSort(List<N8nExecutionListRecord> records)
    {
        if (records is null || records.Count <= 1)
        {
            return;
        }

        static DateTimeOffset StartedKey(N8nExecutionListRecord record) =>
            record.StartedAt ?? DateTimeOffset.MinValue;

        static string WorkflowKey(N8nExecutionListRecord record) =>
            string.IsNullOrWhiteSpace(record.WorkflowName) ? "Unknown workflow" : record.WorkflowName.Trim();

        static string StatusKey(N8nExecutionListRecord record) =>
            string.IsNullOrWhiteSpace(record.Status) ? string.Empty : record.Status.Trim();

        static double RuntimeKey(N8nExecutionListRecord record) =>
            record.RunTimeSeconds ?? double.MinValue;

        static long ExecIdKey(N8nExecutionListRecord record)
        {
            // 优先按数值比较；ID 非纯数字时退化为按字符串排序（返回 long.MinValue 让字符串键起作用）。
            if (long.TryParse(record.ExecutionId, NumberStyles.Integer, CultureInfo.InvariantCulture, out long numeric))
            {
                return numeric;
            }
            return long.MinValue;
        }

        IEnumerable<N8nExecutionListRecord> ordered;
        bool asc = _executionSortAscending;

        switch (_executionSortField)
        {
            case ExecutionSortField.Workflow:
                ordered = asc
                    ? records.OrderBy(WorkflowKey, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(StartedKey)
                    : records.OrderByDescending(WorkflowKey, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(StartedKey);
                break;

            case ExecutionSortField.Status:
                ordered = asc
                    ? records.OrderBy(StatusKey, StringComparer.OrdinalIgnoreCase).ThenByDescending(StartedKey)
                    : records.OrderByDescending(StatusKey, StringComparer.OrdinalIgnoreCase).ThenByDescending(StartedKey);
                break;

            case ExecutionSortField.Started:
                ordered = asc
                    ? records.OrderBy(StartedKey)
                    : records.OrderByDescending(StartedKey);
                break;

            case ExecutionSortField.RunTime:
                ordered = asc
                    ? records.OrderBy(RuntimeKey).ThenByDescending(StartedKey)
                    : records.OrderByDescending(RuntimeKey).ThenByDescending(StartedKey);
                break;

            case ExecutionSortField.ExecId:
                ordered = asc
                    ? records.OrderBy(ExecIdKey).ThenBy(static r => r.ExecutionId, StringComparer.OrdinalIgnoreCase).ThenByDescending(StartedKey)
                    : records.OrderByDescending(ExecIdKey).ThenByDescending(static r => r.ExecutionId, StringComparer.OrdinalIgnoreCase).ThenByDescending(StartedKey);
                break;

            default:
                // 兜底（正常两态循环下不会走到这里）：按开始时间倒序
                ordered = records.OrderByDescending(StartedKey);
                break;
        }

        var sorted = ordered.ToList();
        records.Clear();
        records.AddRange(sorted);
    }

    /// <summary>
    /// 更新执行记录页 5 列表头的 V 形排序指示器（Polyline）：
    ///  - 激活列升序：显示 _Up（尖朝上 ∧，位于文字上方）
    ///  - 激活列降序：显示 _Down（尖朝下 ∨，位于文字下方）
    ///  - 未激活列：上下 Polyline 均 Hidden（保留占位，避免文字上下抖动）
    /// </summary>
    private void UpdateExecutionSortIndicators()
    {
        (System.Windows.Shapes.Polyline? Up, System.Windows.Shapes.Polyline? Down, ExecutionSortField Field)[] indicators =
        {
            (ExecutionRecordsSortIndicator_Workflow_Up, ExecutionRecordsSortIndicator_Workflow_Down, ExecutionSortField.Workflow),
            (ExecutionRecordsSortIndicator_Status_Up,   ExecutionRecordsSortIndicator_Status_Down,   ExecutionSortField.Status),
            (ExecutionRecordsSortIndicator_Started_Up,  ExecutionRecordsSortIndicator_Started_Down,  ExecutionSortField.Started),
            (ExecutionRecordsSortIndicator_RunTime_Up,  ExecutionRecordsSortIndicator_RunTime_Down,  ExecutionSortField.RunTime),
            (ExecutionRecordsSortIndicator_ExecId_Up,   ExecutionRecordsSortIndicator_ExecId_Down,   ExecutionSortField.ExecId),
        };

        foreach (var pair in indicators)
        {
            bool isActive = _executionSortField == pair.Field;
            bool showUp = isActive && _executionSortAscending;
            bool showDown = isActive && !_executionSortAscending;

            if (pair.Up is not null)
            {
                pair.Up.Visibility = showUp ? Visibility.Visible : Visibility.Collapsed;
            }
            if (pair.Down is not null)
            {
                pair.Down.Visibility = showDown ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private void ExecutionRecordsScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_executionRecordsVisibleCount >= _filteredExecutionRecords.Count)
        {
            return;
        }

        if (sender is ScrollViewer scrollViewer && scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 24)
        {
            AppendExecutionRecordsPage();
        }
    }

    private static string FormatExecutionStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return "Unknown";
        }

        string normalized = status.Trim();
        return normalized.Length == 1
            ? normalized.ToUpperInvariant()
            : char.ToUpperInvariant(normalized[0]) + normalized[1..].ToLowerInvariant();
    }

    private static Brush GetExecutionStatusBrush(string? status)
    {
        if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
        {
            return new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
        }

        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "error", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "crashed", StringComparison.OrdinalIgnoreCase))
        {
            return new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
        }

        if (string.Equals(status, "running", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "new", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "waiting", StringComparison.OrdinalIgnoreCase))
        {
            return new SolidColorBrush(Color.FromRgb(0x25, 0x6B, 0xEB));
        }

        if (string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase))
        {
            return new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
        }

        return Brushes.Black;
    }

    private static string FormatExecutionStarted(DateTimeOffset? startedAt)
    {
        return startedAt is null
            ? "--"
            : startedAt.Value.ToLocalTime().ToString("yyyy.MM.dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static string BuildExecutionDetailUrl(N8nExecutionListRecord record)
    {
        string workflowId = Uri.EscapeDataString(record.WorkflowId ?? string.Empty);
        string executionId = Uri.EscapeDataString(record.ExecutionId);
        return $"{N8nLocalRootUrl}/workflow/{workflowId}/executions/{executionId}";
    }

    private static string FormatExecutionRuntimeSeconds(double? seconds)
    {
        if (seconds is null || double.IsNaN(seconds.Value) || double.IsInfinity(seconds.Value))
        {
            return "--";
        }

        if (seconds.Value < 60)
        {
            return $"{seconds.Value:0.###}s";
        }

        if (seconds.Value < 3600)
        {
            return $"{seconds.Value / 60.0:0.##}m";
        }

        return $"{seconds.Value / 3600.0:0.##}h";
    }

    private static string FormatRuntimeSeconds(double? seconds)
    {
        if (seconds is null || double.IsNaN(seconds.Value) || double.IsInfinity(seconds.Value))
            return "--";

        // 与 n8n 官方 Run time (avg.) 对齐：始终保留 2 位小数的秒（如 71.27s）
        return $"{seconds.Value:0.00}s";
    }

    private void SetWindowScale(double userScale)
    {
        // 更新系统 DPI 参考值
        var dpi = VisualTreeHelper.GetDpi(this);
        _systemDpi = dpi.DpiScaleX;

        _currentUserScale = userScale;
        SetWindowScaleCore(userScale);

        // 持久化到配置文件
        SaveConfig(userScale);
    }

    // ============================================================
    // 用户状态持久化（JSON，data/.Launcher/version.json）
    // ============================================================

    /// <summary>向上搜索 data/.Launcher/ 目录，返回其完整路径；找不到返回 null。</summary>
    private static string? GetConfigDir()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        DirectoryInfo? dir = new(baseDir);
        while (dir != null)
        {
            string test = Path.Combine(dir.FullName, "data", ".Launcher");
            if (Directory.Exists(test))
                return test;
            dir = dir.Parent;
        }
        return null;
    }

    private static string GetConfigDirOrCreate()
    {
        string configDir = GetConfigDir()
                           ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", ".Launcher");
        Directory.CreateDirectory(configDir);
        return configDir;
    }

    private static string GetConfigPath()
    {
        return Path.Combine(GetConfigDirOrCreate(), "version.json");
    }

    private static AppConfig LoadConfig()
    {
        string path = GetConfigPath();
        lock (StateFileLock)
        {
            try
            {
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var config = JsonSerializer.Deserialize<AppConfig>(json);
                    if (config != null)
                    {
                        EnsureConfigDefaults(config);
                        Debug.WriteLine($"[Config] Loaded WindowScale={config.WindowScale} from {path}");
                        return config;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Config] Load error: {ex.Message}");
            }

            var newConfig = CreateDefaultConfigWithLegacyMigration();
            SaveConfigCore(newConfig);
            Debug.WriteLine($"[Config] Created {path} with WindowScale={newConfig.WindowScale}");
            return newConfig;
        }
    }

    private static void SaveConfig(double scale)
    {
        try
        {
            var config = LoadConfig();
            config.WindowScale = scale;
            SaveConfig(config);
            Debug.WriteLine($"[Config] Saved WindowScale={scale}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Config] Save scale error: {ex.Message}");
        }
    }

    private static void SaveVersions(VersionCache versions)
    {
        try
        {
            var config = LoadConfig();
            config.Versions = versions;
            EnsureConfigDefaults(config);
            SaveConfig(config);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VersionInfo] Save versions error: {ex.Message}");
        }
    }

    private static void SaveConfig(AppConfig config)
    {
        lock (StateFileLock)
        {
            EnsureConfigDefaults(config);
            SaveConfigCore(config);
        }
    }

    private static void SaveConfigCore(AppConfig config)
    {
        string json = JsonSerializer.Serialize(config, StateJsonOptions);
        File.WriteAllText(GetConfigPath(), json);
    }

    private static AppConfig CreateDefaultConfigWithLegacyMigration()
    {
        var config = new AppConfig
        {
            WindowScale = ReadLegacyWindowScale() ?? ComputeAutoScale(),
            Versions = new VersionCache { Launcher = LAUNCHER_VERSION }
        };

        var legacyVersions = ReadLegacyVersionTxt();
        if (legacyVersions != null)
            config.Versions = legacyVersions;

        EnsureConfigDefaults(config);
        return config;
    }

    private static double? ReadLegacyWindowScale()
    {
        try
        {
            string legacyPath = Path.Combine(GetConfigDirOrCreate(), "config.json");
            if (!File.Exists(legacyPath))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(legacyPath));
            if (doc.RootElement.TryGetProperty(nameof(AppConfig.WindowScale), out var scaleElement) &&
                scaleElement.TryGetDouble(out double scale) && scale > 0)
            {
                Debug.WriteLine($"[Config] Migrated WindowScale={scale} from {legacyPath}");
                return scale;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Config] Legacy config migration error: {ex.Message}");
        }

        return null;
    }

    private static VersionCache? ReadLegacyVersionTxt()
    {
        try
        {
            string legacyPath = Path.Combine(GetConfigDirOrCreate(), "version.txt");
            if (!File.Exists(legacyPath))
                return null;

            string text = File.ReadAllText(legacyPath).Trim();
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var versions = new VersionCache { Launcher = LAUNCHER_VERSION };
            versions.NodeJs = MatchValue(text, @"Node\.js:\s*v?([^\s]+)") ?? versions.NodeJs;
            versions.Python = MatchValue(text, @"Python:\s*([^\s]+)") ?? versions.Python;
            versions.Ffmpeg = MatchValue(text, @"ffmpeg:\s*([^\s]+)") ?? versions.Ffmpeg;
            versions.N8nCurrent = MatchValue(text, @"n8n:\s*([^\s]+)\s*\[当前\]") ?? versions.N8nCurrent;
            versions.N8nLatest = MatchValue(text, @"\|\s*([^\s]+)\s*\[最新\]") ?? versions.N8nLatest;
            versions.N8nLatestStatus = "Unknown";
            versions.LatestVersionUpdatedAt = null;
            versions.LatestVersionLastError = "Migrated from legacy version.txt; not verified online";
            versions.Launcher = MatchValue(text, @"启动器版本:\s*([^\s]+)") ?? LAUNCHER_VERSION;
            Debug.WriteLine($"[VersionInfo] Migrated legacy version.txt from {legacyPath}");
            return versions;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VersionInfo] Legacy version.txt migration error: {ex.Message}");
            return null;
        }
    }

    private static string? MatchValue(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static void EnsureConfigDefaults(AppConfig config)
    {
        if (config.WindowScale <= 0)
            config.WindowScale = ComputeAutoScale();

        // 历史档位迁移（2026-09-26）：2/3 → 0.6、1/3 → 0.4；中间版本 0.625 → 0.6、0.375 → 0.4。
        // 老配置里存的是 0.6666… / 0.3333…，而托盘与设置页的档位匹配容差都是 0.001，
        // 不迁移会出现「比例菜单 5 项全不打勾 + 设置页下拉框错显 100%」。
        if (Math.Abs(config.WindowScale - 2.0 / 3.0) < 0.01 || Math.Abs(config.WindowScale - 0.625) < 0.01)
            config.WindowScale = 0.6;
        else if (Math.Abs(config.WindowScale - 1.0 / 3.0) < 0.01 || Math.Abs(config.WindowScale - 0.375) < 0.01)
            config.WindowScale = 0.4;

        config.ThemeMode = NormalizeThemeMode(config.ThemeMode);
        config.StartupMode = NormalizeStartupMode(config.StartupMode);
        if (config.StartupMode == StartupModeNone && config.StartWithWindows)
            config.StartupMode = StartupModeStartN8n;
        config.StartWithWindows = config.StartupMode != StartupModeNone;
        config.BrowserName = string.IsNullOrWhiteSpace(config.BrowserName) ? "系统默认" : config.BrowserName.Trim();
        config.BrowserPath = NormalizeBrowserPath(config.BrowserPath);
        if (string.IsNullOrWhiteSpace(config.BrowserPath))
            config.BrowserName = "系统默认";
        config.CopilotKeyMode = NormalizeCopilotKeyMode(config.CopilotKeyMode, config.TakeOverCopilotKey);
        config.TakeOverCopilotKey = config.CopilotKeyMode != CopilotKeyModeOff;
        config.ProxyPort = NormalizeProxyPort(config.ProxyPort);
        if (config.SleepTimerMinutes < 0)
            config.SleepTimerMinutes = 0;
        if (config.SleepTimerLastAppliedMinutes < 0)
            config.SleepTimerLastAppliedMinutes = 0;
        if (config.SleepTimerLastAppliedMinutes == 0 && config.SleepTimerMinutes > 0)
            config.SleepTimerLastAppliedMinutes = config.SleepTimerMinutes;

        config.Timezone = TimezoneService.NormalizeUserChoice(config.Timezone);

        // 控制台调试字体切换按钮的持久化态：只允许 "Yahei" / "SourceHan"，其它一律回落到 "Yahei"。
        // 保证删除 version.json 或配置文件缺失该字段时都会以雅黑默认态启动。
        if (!string.Equals(config.ConsoleFontMode, "Yahei", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(config.ConsoleFontMode, "SourceHan", StringComparison.OrdinalIgnoreCase))
        {
            config.ConsoleFontMode = "Yahei";
        }
        else
        {
            // 归一化大小写以稳定 JSON 表现。
            config.ConsoleFontMode = string.Equals(config.ConsoleFontMode, "SourceHan", StringComparison.OrdinalIgnoreCase)
                ? "SourceHan"
                : "Yahei";
        }

        config.Versions ??= new VersionCache();
        config.FileAccessPermissions ??= new List<FileAccessPermissionSlot>();
        while (config.FileAccessPermissions.Count < 3)
            config.FileAccessPermissions.Add(new FileAccessPermissionSlot());
        if (config.FileAccessPermissions.Count > 3)
            config.FileAccessPermissions = config.FileAccessPermissions.Take(3).ToList();

        foreach (var slot in config.FileAccessPermissions)
        {
            slot.Path = NormalizeFileAccessPath(slot.Path);
            if (string.IsNullOrWhiteSpace(slot.Path) || IsDriveRootPath(slot.Path))
            {
                slot.Path = string.Empty;
                slot.IsEnabled = false;
            }
        }

        config.Versions.NodeJs = NormalizeUnknown(config.Versions.NodeJs);
        config.Versions.Python = NormalizeUnknown(config.Versions.Python);
        config.Versions.Ffmpeg = NormalizeUnknown(config.Versions.Ffmpeg);
        config.Versions.N8nCurrent = NormalizeUnknown(config.Versions.N8nCurrent);
        config.Versions.N8nLatest = NormalizeLatestVersion(config.Versions.N8nLatest);
        config.Versions.N8nLatestStatus = NormalizeLatestStatus(config.Versions.N8nLatestStatus, config.Versions.LatestVersionUpdatedAt);
        config.Versions.Launcher = string.IsNullOrWhiteSpace(config.Versions.Launcher)
            ? LAUNCHER_VERSION
            : config.Versions.Launcher.Trim();
        // 启动器远端最新版本缓存字段与 n8n 那组字段套路完全一致：
        // "未知"/空 → "0.0.0"；status 归一为 Latest/Unknown（无 UpdatedAt 视为 Unknown）。
        config.Versions.LauncherLatest = NormalizeLatestVersion(config.Versions.LauncherLatest);
        config.Versions.LauncherLatestStatus = NormalizeLatestStatus(config.Versions.LauncherLatestStatus, config.Versions.LauncherLatestUpdatedAt);
    }

    /// <summary>
    /// 归一化主题模式取值。合法值：Light / Dark / System（跟随系统）。
    /// ★ 兜底返回 "Light"（2026-08-07 由 "System" 改为 "Light"）：
    ///   配置为空 / 非法值时落到浅色，与 AppConfig.ThemeMode 字段默认值、
    ///   PerformSettingsReset 的重置值三处保持一致。
    ///   注意 "System" 仍是合法值，用户在设置页显式选择时会被原样保留。
    /// </summary>
    private static string NormalizeThemeMode(string? value)
    {
        if (string.Equals(value, "Light", StringComparison.OrdinalIgnoreCase))
            return "Light";
        if (string.Equals(value, "Dark", StringComparison.OrdinalIgnoreCase))
            return "Dark";
        if (string.Equals(value, "System", StringComparison.OrdinalIgnoreCase))
            return "System";
        return "Light";
    }

    private static string NormalizeBrowserPath(string? path)
    {
        string trimmed = path?.Trim().Trim('"') ?? string.Empty;
        return File.Exists(trimmed) ? trimmed : string.Empty;
    }

    private static string NormalizeStartupMode(string? value)
    {
        if (string.Equals(value, StartupModeStartN8n, StringComparison.OrdinalIgnoreCase))
            return StartupModeStartN8n;
        if (string.Equals(value, StartupModeStartN8nAndOpenWeb, StringComparison.OrdinalIgnoreCase))
            return StartupModeStartN8nAndOpenWeb;
        return StartupModeNone;
    }

    private static string NormalizeCopilotKeyMode(string? value, bool legacyTakeOverCopilotKey = false)
    {
        if (string.Equals(value, CopilotKeyModeShowLauncher, StringComparison.OrdinalIgnoreCase))
            return CopilotKeyModeShowLauncher;
        if (string.Equals(value, CopilotKeyModeOpenN8nWeb, StringComparison.OrdinalIgnoreCase))
            return CopilotKeyModeOpenN8nWeb;
        if (string.Equals(value, CopilotKeyModeShowLauncherAndOpenN8nWeb, StringComparison.OrdinalIgnoreCase))
            return CopilotKeyModeShowLauncherAndOpenN8nWeb;
        if (legacyTakeOverCopilotKey)
            return CopilotKeyModeShowLauncher;
        return CopilotKeyModeOff;
    }

    private static int NormalizeSleepTimerMinutes(int minutes)
    {
        return Math.Clamp(minutes, 0, 5256000);
    }

    private static int NormalizeProxyPort(int port)
    {
        return port is > 0 and <= 65535 ? port : 7890;
    }

    private static string NormalizeUnknown(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "未知" : value.Trim();
    }

    private static string NormalizeLatestVersion(string? value)
    {
        return string.IsNullOrWhiteSpace(value) || value.Trim() == "未知" ? "0.0.0" : value.Trim();
    }

    private static string NormalizeLatestStatus(string? status, string? latestVersionUpdatedAt)
    {
        if (string.Equals(status, "Latest", StringComparison.OrdinalIgnoreCase))
            return "Latest";

        if (string.Equals(status, "Unknown", StringComparison.OrdinalIgnoreCase))
            return "Unknown";

        return DateTimeOffset.TryParse(latestVersionUpdatedAt, out _) ? "Latest" : "Unknown";
    }

    private static string? GetPortableRootFromStateDir(string stateDir)
    {
        DirectoryInfo? launcherDir = new(stateDir);
        return launcherDir.Parent?.Parent?.FullName;
    }

    private enum AboutTutorialKind
    {
        Empty,
        PackageIntro,
        LanAccess,
        Folders,
        Privacy,
        Proxy,
        Ai,
        Upgrade,
        Legal,
        Feedback,
        Faq
    }

    private sealed record NodeModulesHealthState(bool NodeModulesOk, bool NodeModulesTarOk);

    private async void LaunchTile02_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!CanUseStartLaunchTile())
            return;

        await FadeLaunchTile02ActionTextOutAsync();
        await StartN8nAsync();
    }

    private async void LaunchTile03_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!CanUseStopLaunchTile())
            return;

        await StopN8nProcessAsync(showNoProcessMessage: true);
    }

    private async void LaunchTile04_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!CanUseRestartLaunchTile())
            return;

        await RestartN8nAsync();
    }

    private async void ConsoleActionTile01_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => await CopyConsoleLogToClipboardAsync();

    private void ConsoleActionTile02_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => ExportConsoleLogToTextFile();

    // 命名坑：本 handler 挂在 XAML 的 ConsoleActionTile04（停止运行）上，编号与 x:Name 交叉错位。
    // 守卫与启动面板「停止运行」磁贴同源（LaunchTile03），保证控制台与面板同一套可用性关系。
    private async void ConsoleActionTile03_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!CanUseStopLaunchTile())
            return;

        await StopN8nProcessAsync(showNoProcessMessage: true);
    }

    // 命名坑：本 handler 挂在 XAML 的 ConsoleActionTile03（重新启动）上，编号与 x:Name 交叉错位。
    // 守卫与启动面板「重新启动」磁贴同源（LaunchTile04）。
    private async void ConsoleActionTile04_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!CanUseRestartLaunchTile())
            return;

        await RestartN8nAsync();
    }

    private async void ConsoleActionTile05_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => await StartN8nAsync();

    private void BrowsePathButton03_Click(object sender, RoutedEventArgs e) => BrowseFileAccessPermissionFolder(0);

    private void BrowsePathButton04_Click(object sender, RoutedEventArgs e) => BrowseFileAccessPermissionFolder(1);

    private void BrowsePathButton05_Click(object sender, RoutedEventArgs e) => BrowseFileAccessPermissionFolder(2);

    // 关于页当前显示的教程种类（4 个按钮之一）。切换语言时用它重新渲染当前段落文本。
    private AboutTutorialKind _currentAboutTutorialKind = AboutTutorialKind.PackageIntro;

        /// <summary>
        /// 当前处于激活态的教程按钮。切换深/浅色主题后需要用它把激活底板重刷成新主题取色，
        /// 否则 SetActiveTutorialButton 写入的是"本地属性"，不会随 DynamicResource 自动更新。
        /// </summary>
        private FrameworkElement? _activeTutorialButton;

        /// <summary>
        /// 教程按钮互斥激活态：被点击的按钮用"激活底板"，其它 9 个恢复"未激活底板"。
        /// ★ 全部 10 个块必须都在这里登记，否则"点了不亮"：
        ///   PackageIntro 单独一段处理 + buttonArray 里 9 个 = 10。
        ///   （曾漏登记 AboutTutorialSlot04 即"代理"块，导致点它时其它块正确熄灭、
        ///     但它自己拿不到激活底板，深浅两种模式下都表现为"点了没反应"。）
        ///   日后新增 / 改名教程块，务必同步更新下面的 buttonArray。
        /// ★ 两个底板取色从 Resources 读取（AboutTutorialButtonActiveBackgroundBrush /
        ///   AboutTutorialButtonBackgroundBrush），由 UpdateThemeColors() 按主题切换：
        ///     未激活：浅 White（原硬编码值）/ 深 Black
        ///     激活：  浅 #F8FBFE（原硬编码值）/ 深 #1C1C1C（N+15 轮由 #171B1F 去蓝改为中性灰）
        ///   这里必须走 Resources 而不能继续硬编码，否则深色下一点击就被浅色值写回。
        /// ★ 本方法给的是 Border.Background 本地属性（优先级高于 DynamicResource），
        ///   因此主题切换时必须由 UpdateThemeColors() 回调本方法重刷，见 _activeTutorialButton。
        /// </summary>
        private void SetActiveTutorialButton(FrameworkElement? activeButton)
        {
            _activeTutorialButton = activeButton;

            var activeBrush = (Brush)FindResource("AboutTutorialButtonActiveBackgroundBrush");
            var inactiveBrush = (Brush)FindResource("AboutTutorialButtonBackgroundBrush");

            // AboutTutorialLanButton 是 Border（占位白块），其余 9 个是 Button
            if (activeButton is not null && ReferenceEquals(activeButton, AboutTutorialPackageIntroButton))
                AboutTutorialPackageIntroButton.Background = activeBrush;
            else
                AboutTutorialPackageIntroButton.Background = inactiveBrush;

            // 9 个 Border（含"代理"块 AboutTutorialSlot04）+ 上面单独处理的 PackageIntro = 10 个块全覆盖。
            var buttonArray = new System.Windows.Controls.Border[]
            {
                AboutTutorialLanButton, AboutTutorialFoldersButton,
                AboutTutorialPrivacyButton, AboutTutorialFaqButton, AboutTutorialAiButton,
                AboutTutorialFeedbackButton, AboutTutorialLegalButton, AboutTutorialUpgradeButton,
                AboutTutorialSlot04
            };
            foreach (var b in buttonArray)
            {
                b.Background = ReferenceEquals(b, activeButton) ? activeBrush : inactiveBrush;
            }
        }

        /// <summary>
        /// 窗口 Loaded 后默认激活第一个教程按钮（局域网访问），与默认显示的教程内容一致。
        /// </summary>
        private void InitializeTutorialButtonActiveState()
        {
            SetActiveTutorialButton(AboutTutorialPackageIntroButton);
        }

        private void AboutTutorialPackageIntroButton_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _currentAboutTutorialKind = AboutTutorialKind.PackageIntro;
            SetAboutTutorialText(_currentAboutTutorialKind);
            SetActiveTutorialButton(AboutTutorialPackageIntroButton);
        }

        private void AboutTutorialAiButton_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _currentAboutTutorialKind = AboutTutorialKind.Ai;
            SetAboutTutorialText(_currentAboutTutorialKind);
            SetActiveTutorialButton(AboutTutorialAiButton);
        }

        private void AboutTutorialUpgradeButton_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _currentAboutTutorialKind = AboutTutorialKind.Upgrade;
            SetAboutTutorialText(_currentAboutTutorialKind);
            SetActiveTutorialButton(AboutTutorialUpgradeButton);
        }

        private void AboutTutorialLegalButton_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _currentAboutTutorialKind = AboutTutorialKind.Legal;
            SetAboutTutorialText(_currentAboutTutorialKind);
            SetActiveTutorialButton(AboutTutorialLegalButton);
        }

        private void AboutTutorialFeedbackButton_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _currentAboutTutorialKind = AboutTutorialKind.Feedback;
            SetAboutTutorialText(_currentAboutTutorialKind);
            SetActiveTutorialButton(AboutTutorialFeedbackButton);
        }

        private void AboutTutorialFaqButton_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _currentAboutTutorialKind = AboutTutorialKind.Faq;
            SetAboutTutorialText(_currentAboutTutorialKind);
            SetActiveTutorialButton(AboutTutorialFaqButton);
        }

        private void AboutTutorialLanButton_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _currentAboutTutorialKind = AboutTutorialKind.LanAccess;
            SetAboutTutorialText(_currentAboutTutorialKind);
            SetActiveTutorialButton(AboutTutorialLanButton);
        }

        private void AboutTutorialFoldersButton_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _currentAboutTutorialKind = AboutTutorialKind.Folders;
            SetAboutTutorialText(_currentAboutTutorialKind);
            SetActiveTutorialButton(AboutTutorialFoldersButton);
        }

        private void AboutTutorialPrivacyButton_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _currentAboutTutorialKind = AboutTutorialKind.Privacy;
            SetAboutTutorialText(_currentAboutTutorialKind);
            SetActiveTutorialButton(AboutTutorialPrivacyButton);
        }

        private void AboutTutorialSlot04_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _currentAboutTutorialKind = AboutTutorialKind.Proxy;
            SetAboutTutorialText(_currentAboutTutorialKind);
            SetActiveTutorialButton(AboutTutorialSlot04);
        }

    // 关于页教程文本框右下角浮动按钮：中英切换 + 复制。
    // 语义 A：按钮显示"当前语言"。中态 = "中"，英态 = "EN"。
    // 记忆持久化到 version.json 的 AboutTutorialLanguage，默认 "Chinese"。
    private void AboutTutorialLanguageToggleButton_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (AboutTutorialLanguageToggleButtonText is null)
            return;

        // 切换到另一态：以 config 当前值为准，避免依赖按钮当前字符。
        var config = LoadConfig();
        bool nowEnglish = string.Equals(config.AboutTutorialLanguage, "English", StringComparison.OrdinalIgnoreCase);
        string next = nowEnglish ? "Chinese" : "English";
        config.AboutTutorialLanguage = next;
        SaveConfig(config);

        AboutTutorialLanguageToggleButtonText.Text = next == "English" ? "EN" : "中";
        SetAboutTutorialText(_currentAboutTutorialKind);
    }

    private void AboutTutorialCopyButton_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (AboutTutorialTextBox is null)
            return;
        try { System.Windows.Clipboard.SetText(AboutTutorialTextBox.Text ?? string.Empty); }
        catch { /* 忽略剪贴板占用异常 */ }
    }

    private void SetAboutTutorialText(AboutTutorialKind kind)
    {
        if (AboutTutorialTextBox is null)
            return;

        // 记录当前段落 kind，供语言切换按钮重新渲染同段落用。
        _currentAboutTutorialKind = kind;

        bool english = string.Equals(LoadConfig().AboutTutorialLanguage, "English", StringComparison.OrdinalIgnoreCase);
        if (english)
        {
            SetAboutTutorialTextEnglish(kind);
            return;
        }

        AboutTutorialTextBox.Text = kind switch
        {
            AboutTutorialKind.Empty => string.Empty,
            AboutTutorialKind.PackageIntro => "n8n_portable_Gv是一个为Windows设计的n8n便携运行环境，让您无需安装任何东西就能在本地完整运行n8n。\r\n\r\n" +
                "1. 完整便携：内置Node.js、Python、FFmpeg和n8n应用本身，所有运行时都放在runtime/目录下。无需安装、无需管理员权限，复制整个文件夹到任意位置（包括U盘）即可使用。\r\n\r\n" +
                "2. 数据隔离：所有n8n配置、工作流、执行记录、启动器设置都保存在data/目录下，不写入注册表、不污染用户目录、不影响系统中其他n8n或Node.js安装。\r\n\r\n" +
                "3. 图形启动器：双击n8n_launcher_Gv.exe即可启动/停止/重启n8n，无需打开命令行。集成执行统计、版本检测、文件夹访问授权、代理切换等日常功能。\r\n\r\n" +
                "4. 长任务支持：内置Task Runner超时延长，支持Code节点运行长达7天的超长任务。\r\n\r\n" +
                "5. 日常备份只需备份data/目录：所有重要数据（工作流、凭证、执行记录、启动器设置）都在data/文件夹内。其他目录（runtime/、app/、Launcher/）丢失后可重新下载，data/丢了就找不回。强烈建议定期把data/单独备份一份。\r\n\r\n" +
                "6. 迁移到新电脑/新路径前，先删除app/node_modules/目录：node_modules里的依赖文件名非常长，Windows资源管理器在复制/移动时经常会因为路径过长报错。正确流程：先删app/node_modules/→把整个便携包文件夹移动到新位置→在新位置打开启动器，启动器会检测到缺失并提示从node_modules.tar一键修复还原。\r\n\r\n" +
                "适合场景：想在Windows上快速试用n8n、需要在多台电脑间移动工作流、不希望n8n在系统里留下痕迹的用户。",
            AboutTutorialKind.LanAccess => "当你在局域网内的多台电脑上运行n8n，且需要互相访问时。\r\n\r\n" +
                "1. 在电脑上查看局域网IP，例如192.168.1.10。\r\n" +
                "2. 在另一台同一局域网电脑的浏览器里访问：http://本机IP:5678，例如http://192.168.1.10:5678。\r\n" +
                "3. 如果无法访问，优先检查Windows防火墙、杀毒软件网络拦截、路由器隔离，以及n8n是否允许监听局域网地址。\r\n\r\n" +
                "提示：局域网访问适合自己家里或可信网络使用，不建议把5678端口直接暴露到公网。\r\n\r\n\r\n" +
                "用局域网IP打开n8n后，文本能复制但节点复制不了。原因是浏览器把局域网IP当作\"非安全来源\"，禁用了剪贴板API。以下方法针对Chrome / Edge，一次配置永久有效：\r\n\r\n" +
                "1. 在浏览器地址栏粘贴以下地址并回车：\r\n" +
                "   Chrome: chrome://flags/#unsafely-treat-insecure-origin-as-secure\r\n" +
                "   Edge: edge://flags/#unsafely-treat-insecure-origin-as-secure\r\n\r\n" +
                "2. 页面会自动定位到 \"Insecure origins treated as secure\"，把右侧下拉框改为 Enabled。\r\n\r\n" +
                "3. 在下方文本框里填入你要放行的地址，例如http://192.168.1.100:5678。多个地址用英文逗号分隔。\r\n\r\n" +
                "4. 点击浏览器右下角的 Relaunch 按钮重启浏览器。\r\n\r\n" +
                "重启后，用该IP打开n8n就可以自由复制/粘贴节点了。\r\n\r\n" +
                "如果重启后仍然弹出\"你正在使用不受支持的命令行标志\"警告，说明浏览器是从系统固定的快捷方式启动的。用下面这个办法一次性消除警告：\r\n\r\n" +
                "1. 完全退出所有浏览器窗口。\r\n" +
                "2. 打开这个路径（把用户名换成你自己的）：\r\n" +
                "   C:\\Users\\<你的用户名>\\AppData\\Roaming\\Microsoft\\Internet Explorer\\Quick Launch\\User Pinned\\TaskBar\r\n" +
                "3. 右键 Microsoft Edge 图标 → 属性 → \"目标\"一栏改为：\r\n" +
                "   \"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe\" --profile-directory=Default --test-type\r\n" +
                "4. 保存后重新固定到任务栏，之后从任务栏启动就不会再弹警告。\r\n\r\n" +
                "注意：只对你自己的局域网n8n地址开启这个白名单，不要给陌生网站添加剪贴板权限。",
            AboutTutorialKind.Folders => "便携包在runtime目录下内置了完整且独立的Node.js、Python和FFmpeg环境。你无需在Windows系统中安装任何环境，就可以在n8n工作流中直接调用。\r\n\r\n\r\n" +
                "1. Python运行环境：\r\n\r\n" +
                "解释器路径：.\\runtime\\python\\python\\python.exe\r\n" +
                "预装库：requests、yt-dlp、browser-cookie3\r\n\r\n" +
                "在n8n的Execute Command节点调用时，Windows下走cmd.exe，建议加上UTF-8编码设置：\r\n" +
                "set PYTHONIOENCODING=utf-8 && .\\runtime\\python\\python\\python.exe 你的脚本.py\r\n\r\n" +
                "如果需要安装额外的第三方库，在便携包根目录打开cmd执行（以清华镜像源为例）：\r\n" +
                ".\\runtime\\python\\python\\python.exe -m pip install <库名> -i https://pypi.tuna.tsinghua.edu.cn/simple\r\n\r\n" +
                "补充：Python也可以通过n8n的Code节点（JavaScript模式）用child_process调用，路径写runtime\\python\\python\\python.exe就能用上便携包内置的解释器。反过来n8n的Python节点（通常经pyodide或系统Python触发）只能调用系统安装的Python，无法调用便携包内置的这个，注意区分。\r\n\r\n\r\n" +
                "2. FFmpeg音视频处理工具：\r\n\r\n" +
                "程序路径：.\\runtime\\ffmpeg\\bin\\ffmpeg.exe\r\n\r\n" +
                "可直接在Execute Command节点中调用，用于视频切片、格式转换、音频提取，或结合yt-dlp下载视频。\r\n" +
                "示例：.\\runtime\\ffmpeg\\bin\\ffmpeg.exe -i input.mp4 -vn -acodec libmp3lame output.mp3\r\n\r\n\r\n" +
                "3. Node.js运行环境：\r\n\r\n" +
                "便携包内的Node.js主要用于驱动n8n主程序运行。工作流中在n8n的Code节点选择JavaScript模式即可原生执行，无需额外配置。",
            AboutTutorialKind.Privacy => "本便携包不会上传你的任何个人数据、工作流或账号凭证。\r\n\r\n" +
                "启动器只在以下两个场景发起联网请求：\r\n" +
                "1. n8n版本检测：访问n8n官方接口，获取最新版本号，用于在界面底部提示是否有新版可用。\r\n" +
                "2. 启动器更新检测：访问GitHub官方仓库，检测n8n_launcher_Gv本身是否有版本更新。\r\n" +
                "除此之外，启动器没有任何其他的联网分析、数据上报或后台统计逻辑。\r\n\r\n" +
                "开源与官方纯净运行时：\r\n" +
                "1. 便携包内的n8n应用、以及runtime目录下的Node.js、Python、FFmpeg等组件，均直接来自各项目的官方开源发布，未经过任何二次篡改或注入（因为不会）。\r\n" +
                "2. 你的所有工作流、凭据、运行日志均保存在本地data目录下，不写入注册表，不上传云端，断网环境下也能正常运行。",
            AboutTutorialKind.Proxy => "使用步骤：\r\n\r\n" +
                "1. 在启动器设置页的\"代理端口\"输入框里，填入本地代理软件监听的HTTP端口。保持这里的端口和代理软件里的端口一致即可。\r\n" +
                "2. 勾选\"启用\"复选框。\r\n" +
                "3. 启动或重启n8n。\r\n\r\n\r\n" +
                "注意事项：\r\n\r\n" +
                "1. 改完代理设置后要重启n8n才生效。代理只在n8n启动瞬间读取一次，运行中改设置不会立即生效。\r\n" +
                "2. 启用期间代理软件必须开着。代理软件关了、端口填错了，n8n会连不上任何网络（包括本地）。\r\n" +
                "3. 不用外网API时建议关掉代理，n8n直连更快更稳。\r\n" +
                "4. 代理软件选\"直连 / 不翻墙\"模式也没关系，只要软件开着占着端口，n8n就能正常联网。",
            AboutTutorialKind.Ai => "搭建工作流耗时太长门槛太高？（我也觉得）\r\n" +
                "便携包内置了专门为AI助手（如ChatGPT、Claude、Cursor、Opencode等）量身定制的AGENTS.md操作规范。只需把根目录下的AGENTS.md提供给AI，即可让AI通过REST API帮你创建、修改和调试工作流，无需手动编写复杂的JSON配置。\r\n\r\n" +
                "使用步骤：\r\n" +
                "1. 打开n8n界面，点击左下角头像→Settings→n8n API→Create an API key，复制生成的Key。\r\n" +
                "2. 将便携包根目录下的AGENTS.md文件直接拖入AI聊天框，作为AI的操作指南。\r\n" +
                "3. 把你的n8n地址和API Key提供给AI，用自然语言提出需求即可。\r\n" +
                "示例：我的n8n地址：http://localhost:5678，API Key：你的Key。请帮我读取工作流ID为1的节点，把里面的Python脚本加上请求头并用清华源安装requests库。\r\n\r\n\r\n" +
                "内置安全红线（放心交给AI）：\r\n" +
                "1. 数据绝对安全：严格禁止AI读写或修改data目录，保护本地数据库与敏感凭证不被破坏。\r\n" +
                "2. 运行不中断：禁止AI执行关机或重启指令，确保代理及启动器注入的环境变量持续生效。\r\n" +
                "3. 操作需授权：AI仅处理你指定的工作流，且任何修改前必须先提供方案并获得你的明确许可。\r\n" +
                "4. Windows编码优化：内置cmd规范与UTF-8编码传输约束，避免节点名变成乱码或连线断开。",
            AboutTutorialKind.Upgrade => "无损升级与迁移步骤：\r\n\r\n" +
                "1. 下载新版便携包：\r\n" +
                "前往GitHub仓库下载最新的n8n_portable_Gv整合包。\r\n\r\n" +
                "2. 迁移核心数据（data）：\r\n" +
                "将旧便携包中的data文件夹整体复制，粘贴并覆盖到新便携包的根目录下。\r\n\r\n" +
                "3. 跨路径 / 跨电脑移动避坑（解决路径过长报错）：\r\n" +
                "Windows资源管理器在直接复制或移动app\\node_modules时，非常容易因为文件路径超过260字符而中断报错。\r\n\r\n" +
                "正确流程：\r\n" +
                "(1) 先手动删除app\\node_modules文件夹。\r\n" +
                "(2) 将整个便携包文件夹移动或复制到新位置。\r\n" +
                "(3) 在新位置双击运行启动器，启动器会自动检测到依赖缺失，并提示你一键从app\\node_modules.tar还原。\r\n\r\n\r\n" +
                "提示：日常备份只需备份data目录。其他目录（runtime、app、Launcher）丢失后可以重新下载便携包补回来，data丢了就找不回。强烈建议定期把data单独备份一份。",
            AboutTutorialKind.Faq => "1. n8n页面打不开 / 提示端口5678被占用\r\n" +
                "启动器点\"开始运行\"后，如果控制台出现类似EADDRINUSE或5678被占用的日志，通常是这三种情况：\r\n" +
                "(1) 之前有n8n进程没干净退出（启动器崩溃 / 强制关闭 / 系统蓝屏后遗留）。启动器新版每次启动n8n前会自动清理便携包路径下的孤儿node.exe，重启一次启动器即可恢复。\r\n" +
                "(2) 你在系统里另外装过n8n（例如npm全局安装），并且它自动跑起来了。任务管理器里搜node.exe，杀掉非便携包路径下的那个即可。\r\n" +
                "(3) 5678被其他软件占用（Docker容器、代理软件、旧版n8n服务）。可以在cmd里用netstat -ano | findstr :5678定位占用进程。\r\n\r\n\r\n" +
                "2. 页面能打开，节点报错\"连不上xxx.com\"\r\n" +
                "n8n内的HTTP Request / OpenAI / Telegram等节点默认走系统直连，国内网络无法直接访问Google / OpenAI / Telegram等服务。解决办法：\r\n" +
                "(1) 打开代理软件（Clash / v2rayN等），记下监听端口（Clash默认7890，v2rayN默认10809）。\r\n" +
                "(2) 在\"启动\"页\"代理端口\"卡片填对应端口，勾选\"启用\"。\r\n" +
                "(3) 点\"重新启动 (Restart)\"让n8n重新读取代理环境变量。启用状态下代理软件必须保持运行，否则国内地址也会连不上。\r\n\r\n\r\n" +
                "3. 工作流Timeout（超时）\r\n" +
                "便携包已经把Task Runner心跳（N8N_RUNNERS_HEARTBEAT_INTERVAL）设为10分钟、任务超时（N8N_RUNNERS_TASK_TIMEOUT）设为7天，Code节点最长可以跑7天。如果还是超时，多半不是超时本身：\r\n" +
                "(1) 外部API主动断开长连接（例如30秒无响应就断），换成分批请求或加轮询。\r\n" +
                "(2) 内存爆掉。Task Runner进程OOM会表现为\"无缘无故超时\"，看控制台是否有JavaScript heap out of memory。\r\n" +
                "(3) 单节点内做超大循环没让出线程，导致心跳发不出去。可以拆分节点或用Split In Batches。",
            AboutTutorialKind.Feedback => "如果在使用n8n_portable_Gv过程中遇到任何问题、Bug，或有功能建议、改进想法，欢迎通过以下渠道反馈。\r\n\r\n\r\n" +
                "反馈渠道（推荐顺序）：\r\n\r\n" +
                "1. GitHub Issues（首选，公开可追溯）：\r\n" +
                "https://github.com/Gil-ver/n8n_portable_Gv/issues\r\n" +
                "点击右上角New issue → 描述问题 → 提交即可。无需精通英文，中文提交完全可以。\r\n\r\n" +
                "2. Bilibili私信（适合非技术反馈、使用建议）：\r\n" +
                "https://space.bilibili.com/108908966\r\n" +
                "关注后点击\"发消息\"，简单说明即可。\r\n\r\n\r\n" +
                "建议随反馈附上的信息（帮助定位问题）：\r\n\r\n" +
                "1. 便携包版本：启动器底栏左下角显示的Launcher版本号（例如Gv_1.0.0），以及n8n版本号。\r\n" +
                "2. 操作系统：Windows版本（Win10 / Win11）和位数（一般都是64位）。\r\n" +
                "3. 复现步骤：从\"启动到问题出现\"的每一步操作。步骤越具体越好定位。\r\n" +
                "4. 期望行为 vs 实际行为：你原本以为会怎样，实际出现了什么。\r\n" +
                "5. 相关日志：启动器\"控制台\"页面右上角有\"复制日志\"和\"导出日志\"按钮，可以把当时的日志一起附上。截图或日志文件都可以。\r\n" +
                "6. 是否稳定复现：偶发一次 / 每次都会 / 特定条件下才会（例如\"启用代理后必现\"）。\r\n\r\n\r\n" +
                "隐私提示：\r\n\r\n" +
                "反馈日志前请先粗略过一眼，避免把工作流里含API Key、账号密码等敏感信息的输出行一起发出。启动器本身不会主动上传任何数据，反馈渠道也完全由你手动控制。\r\n\r\n" +
                "即使你不擅长描述技术细节，只写\"我遇到了xxx问题\"也没关系，我会主动追问需要的信息。感谢你花时间反馈——每一条反馈都是便携包变得更好用的动力。",
            AboutTutorialKind.Legal => "致谢：\r\n\r\n" +
                "n8n_portable_Gv的诞生离不开以下优秀开源项目及其社区的无私奉献，在此表达最诚挚的感谢：\r\n\r\n" +
                "1. n8n (https://n8n.io/)：强大的工作流自动化平台，本便携包的核心。\r\n" +
                "2. Node.js (https://nodejs.org/)：高效安全的JavaScript运行环境。\r\n" +
                "3. WinPython (https://winpython.github.io/)：灵活强悍的脚本与AI扩展运行环境。\r\n" +
                "4. FFmpeg (https://ffmpeg.org/)：全能的多媒体处理工具库。\r\n\r\n\r\n" +
                "开源许可证说明：\r\n\r\n" +
                "1. 本项目 / 启动器：n8n_launcher_Gv及便携包封装脚本基于MIT License开源。\r\n" +
                "2. 上游第三方软件：便携包内包含的第三方运行时与组件（位于app与runtime目录中）均保留其原始开源许可与版权声明：\r\n" +
                "(1) n8n遵循其官方Sustainable Use License / Fair-code协议（个人与内部使用免费，商业转售或对外提供SaaS服务需遵循其官方授权）。\r\n" +
                "(2) Node.js遵循MIT License。\r\n" +
                "(3) WinPython遵循MIT License（内含CPython，遵循PSF License）。\r\n" +
                "(4) FFmpeg遵循LGPL / GPL License。\r\n\r\n\r\n" +
                "免责声明：\r\n\r\n" +
                "1. 按\"原样\"提供：本软件（含启动器及整合包）按\"原样\"（AS IS）提供，不提供任何形式的明示或暗示担保，包括但不限于适销性、特定用途适用性及不侵权担保。\r\n" +
                "2. 数据安全与备份：使用者需自行承担使用本软件的所有风险。开发者不对因使用本软件造成的任何数据丢失（包括但不限于data目录损坏）、系统故障、收益损失或直接 / 间接损害承担法律责任。请务必养成定期手动备份data文件夹的良好习惯。\r\n" +
                "3. 网络与使用安全：用户在通过本工具接入API、公网穿透或配置自动化工作流时，请务必注意保护敏感凭证。因用户自身配置不当、密码泄露或违规使用导致的损失与法律责任，由用户自行承担。",
            _ => string.Empty
        };

        AboutTutorialTextBox.CaretIndex = 0;
        AboutTutorialTextBox.ScrollToHome();
    }

    // 关于页教程文本框：英文文案，与中文 SetAboutTutorialText 一一对应。
    private void SetAboutTutorialTextEnglish(AboutTutorialKind kind)
    {
        if (AboutTutorialTextBox is null)
            return;

        AboutTutorialTextBox.Text = kind switch
        {
            AboutTutorialKind.Empty => string.Empty,
            AboutTutorialKind.PackageIntro => "n8n_portable_Gv is a portable n8n runtime for Windows, letting you run a complete local n8n instance without installing anything.\r\n\r\n" +
                "1. Fully portable: Node.js, Python, FFmpeg, and the n8n app itself are bundled under the runtime/ directory. No installation or admin rights required. Just copy the entire folder anywhere (including USB drives) and run.\r\n\r\n" +
                "2. Data isolation: All n8n configs, workflows, execution records, and launcher settings live inside the data/ directory. Nothing is written to the registry, the user profile, or other locations — it won't conflict with existing n8n or Node.js installs on your system.\r\n\r\n" +
                "3. Graphical launcher: Double-click n8n_launcher_Gv.exe to start/stop/restart n8n — no command line needed. Built-in execution stats, version checks, file access permissions, and proxy toggle for daily use.\r\n\r\n" +
                "4. Long task support: Extended Task Runner timeout allows Code nodes to run for up to 7 days.\r\n\r\n" +
                "5. Daily backup only needs the data/ folder: Everything important (workflows, credentials, execution records, launcher settings) lives inside data/. The other directories (runtime/, app/, Launcher/) can be re-downloaded if lost — but data/ cannot be recovered. Strongly recommend backing up the data/ folder separately on a regular basis.\r\n\r\n" +
                "6. Before moving to a new PC or path, delete app/node_modules/ first: The dependency files inside node_modules have very long names, and Windows Explorer often fails with \"path too long\" errors when copying or moving them. The correct flow is: delete app/node_modules/ → move the whole portable folder to the new location → open the launcher there, and it will detect the missing folder and offer to restore it from node_modules.tar in one click.\r\n\r\n" +
                "Best for: Quickly trying n8n on Windows, moving workflows between machines, and keeping n8n completely self-contained without leaving traces on the system.",
            AboutTutorialKind.LanAccess => "When you run n8n on multiple PCs on the same LAN and need them to reach each other:\r\n\r\n" +
                "1. On the PC running n8n, check its LAN IP, e.g. 192.168.1.10.\r\n" +
                "2. On another PC in the same LAN, open in a browser: http://<your IP>:5678, e.g. http://192.168.1.10:5678.\r\n" +
                "3. If it doesn't work, first check Windows Firewall, antivirus network blocking, router isolation, and whether n8n is allowed to listen on the LAN address.\r\n\r\n" +
                "Tip: LAN access is meant for your home or a trusted network. It's not recommended to expose port 5678 directly to the public internet.\r\n\r\n\r\n" +
                "After opening n8n via a LAN IP, text can be copied but nodes cannot. The reason is that browsers treat a LAN IP as a \"non-secure origin\" and disable the Clipboard API. The following one-time setup works permanently for Chrome / Edge:\r\n\r\n" +
                "1. Paste the following URL into the browser address bar and press Enter:\r\n" +
                "   Chrome: chrome://flags/#unsafely-treat-insecure-origin-as-secure\r\n" +
                "   Edge: edge://flags/#unsafely-treat-insecure-origin-as-secure\r\n\r\n" +
                "2. The page will jump to \"Insecure origins treated as secure\". Set the dropdown on the right to Enabled.\r\n\r\n" +
                "3. In the text box below, enter the addresses you want to allow, e.g. http://192.168.1.100:5678. Separate multiple addresses with commas.\r\n\r\n" +
                "4. Click the Relaunch button at the bottom-right of the browser.\r\n\r\n" +
                "After restart, opening n8n via that IP will allow copy/paste of nodes freely.\r\n\r\n" +
                "If the warning \"You are using an unsupported command-line flag\" still appears every time the browser starts, it means the browser is being launched from a pinned system shortcut. Fix it once and for all with the steps below:\r\n\r\n" +
                "1. Fully quit all browser windows.\r\n" +
                "2. Open the following path (replace the user name with your own):\r\n" +
                "   C:\\Users\\<your user>\\AppData\\Roaming\\Microsoft\\Internet Explorer\\Quick Launch\\User Pinned\\TaskBar\r\n" +
                "3. Right-click the Microsoft Edge icon → Properties → change the \"Target\" field to:\r\n" +
                "   \"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe\" --profile-directory=Default --test-type\r\n" +
                "4. Save, re-pin to the taskbar, and the warning will no longer appear when launched from the taskbar.\r\n\r\n" +
                "Note: Only whitelist your own LAN n8n address. Never grant clipboard permission to unknown websites.",
            AboutTutorialKind.Folders => "Built-in runtime notes:\r\n\r\n" +
                "Under the runtime directory, this portable package bundles complete and self-contained Node.js, Python, and FFmpeg environments. You don't need to install anything on Windows — n8n workflows can call these directly.\r\n\r\n" +
                "1. Python runtime:\r\n\r\n" +
                "Interpreter path: .\\runtime\\python\\python\\python.exe\r\n" +
                "Pre-installed libraries: requests, yt-dlp, browser-cookie3\r\n\r\n" +
                "When calling from an Execute Command node, Windows runs it through cmd.exe, so it's recommended to set UTF-8 encoding first:\r\n" +
                "set PYTHONIOENCODING=utf-8 && .\\runtime\\python\\python\\python.exe your_script.py\r\n\r\n" +
                "To install extra third-party libraries, open cmd in the portable package root and run (Tsinghua mirror as an example):\r\n" +
                ".\\runtime\\python\\python\\python.exe -m pip install <package_name> -i https://pypi.tuna.tsinghua.edu.cn/simple\r\n\r\n" +
                "Note: You can also invoke Python from the n8n Code node (JavaScript mode) via child_process — point it at runtime\\python\\python\\python.exe to use the bundled interpreter. However, the Python node itself in n8n (usually backed by pyodide or the system Python) can only call the system-installed Python, not the bundled one. Keep this distinction in mind.\r\n\r\n" +
                "2. FFmpeg audio/video tool:\r\n\r\n" +
                "Executable path: .\\runtime\\ffmpeg\\bin\\ffmpeg.exe\r\n\r\n" +
                "Can be called directly from an Execute Command node for video slicing, format conversion, audio extraction, or combined with yt-dlp to download videos. Example:\r\n" +
                ".\\runtime\\ffmpeg\\bin\\ffmpeg.exe -i input.mp4 -vn -acodec libmp3lame output.mp3\r\n\r\n" +
                "3. Node.js runtime:\r\n\r\n" +
                "The bundled Node.js is mainly used to run n8n itself. In workflows, just pick JavaScript mode inside the n8n Code node — it runs natively with no extra configuration.",
            AboutTutorialKind.Privacy => "This portable package will never upload any of your personal data, workflows, or account credentials.\r\n\r\n" +
                "The launcher only makes network requests in the following two scenarios:\r\n" +
                "1. n8n version check: contacts the official n8n endpoint to fetch the latest version number, used to indicate at the bottom of the UI whether a newer version is available.\r\n" +
                "2. Launcher update check: contacts the official GitHub repository to check whether n8n_launcher_Gv itself has a new version.\r\n" +
                "Apart from these, the launcher performs no other network analytics, telemetry, or background statistics.\r\n\r\n" +
                "Open source and clean official runtimes:\r\n" +
                "1. The n8n application bundled in this portable package, as well as the Node.js, Python, FFmpeg and other components under the runtime directory, all come directly from the official open-source releases of each project, with no secondary tampering or injection (because I wouldn't).\r\n" +
                "2. All your workflows, credentials, and run logs are stored locally under the data directory. They are never written to the registry and never uploaded to the cloud, and everything works normally even when offline.",
            AboutTutorialKind.Ai => "Building workflows is too slow and the learning curve too steep? (I feel the same.)\r\n" +
                "The portable package ships with an AGENTS.md tailored for AI assistants (ChatGPT, Claude, Cursor, Opencode, etc.). Just hand the AGENTS.md in the root directory to your AI, and it can create, modify, and debug workflows for you through the n8n REST API — no need to hand-craft complex JSON.\r\n\r\n" +
                "Steps:\r\n" +
                "1. Open the n8n UI, click the avatar in the bottom-left corner → Settings → n8n API → Create an API key, then copy the generated key.\r\n" +
                "2. Drag the AGENTS.md from the portable package root directly into the AI chat as its operating guide.\r\n" +
                "3. Give the AI your n8n address and API key, then describe what you want in plain language.\r\n" +
                "Example: \"My n8n address: http://localhost:5678, API Key: <your key>. Please read the nodes in workflow ID 1, add request headers to the Python script inside, and install the requests library from the Tsinghua mirror.\"\r\n\r\n\r\n" +
                "Built-in safety rails (safe to hand over to AI):\r\n" +
                "1. Data is off-limits: the AI is strictly forbidden from reading or modifying the data directory, protecting the local database and sensitive credentials.\r\n" +
                "2. Never interrupt runtime: the AI is forbidden from shutting down or restarting anything, so proxy and launcher-injected environment variables stay effective.\r\n" +
                "3. Explicit authorization required: the AI only touches the workflows you specify, and must present a plan and get your approval before any change.\r\n" +
                "4. Windows encoding optimization: bundled cmd conventions and UTF-8 transport constraints prevent node names from turning into mojibake or connections from breaking.",
            AboutTutorialKind.Upgrade => "Lossless upgrade and migration steps:\r\n\r\n" +
                "1. Download the new portable package:\r\n" +
                "Go to the GitHub repository and download the latest n8n_portable_Gv bundle.\r\n\r\n" +
                "2. Migrate the core data (data):\r\n" +
                "Copy the entire data folder from the old portable package, then paste and overwrite it into the root directory of the new portable package.\r\n\r\n" +
                "3. Cross-path / cross-PC move — how to avoid the \"path too long\" error:\r\n" +
                "When Windows Explorer copies or moves app\\node_modules directly, it very often fails because a file path exceeds 260 characters.\r\n\r\n" +
                "The correct flow is:\r\n" +
                "(1) Manually delete the app\\node_modules folder first.\r\n" +
                "(2) Move or copy the whole portable package folder to its new location.\r\n" +
                "(3) In the new location, double-click the launcher. It will detect the missing dependency and offer a one-click restore from app\\node_modules.tar.\r\n\r\n\r\n" +
                "Tip: For daily backups, you only need to back up the data folder. The other directories (runtime, app, Launcher) can be re-downloaded from the portable package if lost, but data cannot be recovered once gone. Strongly recommend backing up the data folder separately on a regular basis.",
            AboutTutorialKind.Faq => "1. The n8n page won't open / port 5678 is already in use\r\n" +
                "After clicking \"Start\" in the launcher, if the console logs something like EADDRINUSE or \"port 5678 in use\", it's usually one of these three:\r\n" +
                "(1) A previous n8n process didn't exit cleanly (launcher crashed / force-closed / left over after a system crash). The latest launcher automatically kills orphan node.exe processes from the portable package before starting n8n, so simply restarting the launcher usually fixes it.\r\n" +
                "(2) You also installed n8n elsewhere on the system (e.g. a global npm install) and it's running in the background. Search node.exe in Task Manager and kill the one whose path is not inside this portable package.\r\n" +
                "(3) Port 5678 is used by another program (Docker container, proxy software, old n8n service). In cmd, run: netstat -ano | findstr :5678 to identify the process.\r\n\r\n\r\n" +
                "2. The page loads but a node reports \"cannot reach xxx.com\"\r\n" +
                "n8n's HTTP Request / OpenAI / Telegram nodes go direct by default. In some regions, direct access to Google / OpenAI / Telegram is unreliable. Fix:\r\n" +
                "(1) Start your proxy software (Clash / v2rayN / etc.) and note its listening port (Clash default 7890, v2rayN default 10809).\r\n" +
                "(2) On the Launch page, fill in the \"Proxy port\" tile and tick \"Enabled\".\r\n" +
                "(3) Click Restart so that n8n reloads the proxy environment variables. Your proxy software must stay running; otherwise even domestic addresses will fail.\r\n\r\n\r\n" +
                "3. Workflow Timeout\r\n" +
                "This portable package already sets N8N_RUNNERS_HEARTBEAT_INTERVAL to 10 minutes and N8N_RUNNERS_TASK_TIMEOUT to 7 days, so Code nodes can run for up to 7 days. If you still see timeouts, it's usually not the timeout itself:\r\n" +
                "(1) The external API drops the long-lived connection (e.g. closes after 30 seconds of inactivity). Switch to batched requests or polling.\r\n" +
                "(2) Out of memory. When the Task Runner process runs out of RAM it looks like \"random timeouts\" — check the console for \"JavaScript heap out of memory\".\r\n" +
                "(3) A single node runs a huge synchronous loop without yielding, so the heartbeat can't be sent. Split the node or use Split In Batches.",
            AboutTutorialKind.Feedback => "If you run into any problems or bugs while using n8n_portable_Gv, or have any feature requests or suggestions, feel free to reach out through the channels below.\r\n\r\n\r\n" +
                "Feedback channels (recommended order):\r\n\r\n" +
                "1. GitHub Issues (preferred, public and trackable):\r\n" +
                "https://github.com/Gil-ver/n8n_portable_Gv/issues\r\n" +
                "Click \"New issue\" in the top-right corner, describe the problem, and submit. English is not required — Chinese or any language works.\r\n\r\n" +
                "2. Bilibili DM (for non-technical feedback and usage suggestions):\r\n" +
                "https://space.bilibili.com/108908966\r\n" +
                "Follow the account and click \"Send message\".\r\n\r\n" +
                "Info that helps a lot when included:\r\n\r\n" +
                "1. Package version: The Launcher version shown at the bottom-left of the launcher (e.g. Gv_1.0.0), and the n8n version.\r\n" +
                "2. OS: Windows version (Win10 / Win11) and architecture (typically 64-bit).\r\n" +
                "3. Steps to reproduce: Every action from launch to the moment the problem appears. The more concrete, the easier to trace.\r\n" +
                "4. Expected vs actual behavior: What you thought would happen versus what actually happened.\r\n" +
                "5. Logs: On the \"Console\" page there are \"Copy log\" and \"Export log\" tiles in the top-right. Attaching the log around the failure time helps a lot. Screenshots or log files both work.\r\n" +
                "6. Reproducibility: One-off / always / only under specific conditions (e.g. \"only when the proxy is enabled\").\r\n\r\n" +
                "Privacy note:\r\n\r\n" +
                "Please glance over the log before sending it, in case any workflow output contains sensitive info such as API keys or account credentials. The launcher itself never uploads anything on its own; every feedback channel is fully under your control.\r\n\r\n" +
                "Even if you can't articulate the technical details, \"something went wrong with xxx\" is enough — I will follow up and ask for what's needed. Thank you for taking the time to send feedback; every report is what keeps this portable package improving.",
            AboutTutorialKind.Legal => "Credits:\r\n\r\n" +
                "n8n_portable_Gv would not exist without the following excellent open-source projects and their communities. Sincere thanks:\r\n\r\n" +
                "1. n8n (https://n8n.io/): a powerful workflow automation platform, the core of this portable package.\r\n" +
                "2. Node.js (https://nodejs.org/): an efficient and secure JavaScript runtime.\r\n" +
                "3. WinPython (https://winpython.github.io/): a flexible and capable runtime for scripts and AI extensions.\r\n" +
                "4. FFmpeg (https://ffmpeg.org/): an all-round multimedia processing toolkit.\r\n\r\n\r\n" +
                "Open-source license notice:\r\n\r\n" +
                "1. This project / launcher: n8n_launcher_Gv and the portable packaging scripts are open-sourced under the MIT License.\r\n" +
                "2. Upstream third-party software: the third-party runtimes and components bundled inside the portable package (under the app and runtime directories) retain their original open-source licenses and copyright notices:\r\n" +
                "(1) n8n follows its official Sustainable Use License / Fair-code agreement (free for personal and internal use; commercial resale or offering as a SaaS service to third parties requires official authorization).\r\n" +
                "(2) Node.js follows the MIT License.\r\n" +
                "(3) WinPython follows the MIT License (bundles CPython under the PSF License).\r\n" +
                "(4) FFmpeg follows the LGPL / GPL License.\r\n\r\n\r\n" +
                "Disclaimer:\r\n\r\n" +
                "1. Provided \"as is\": This software (including the launcher and the portable bundle) is provided \"AS IS\", without warranty of any kind, express or implied, including but not limited to warranties of merchantability, fitness for a particular purpose, and non-infringement.\r\n" +
                "2. Data safety and backup: Users assume all risks of using this software. The developer is not legally liable for any data loss (including but not limited to corruption of the data directory), system failure, loss of revenue, or any direct / indirect damages caused by the use of this software. Please make it a habit to regularly back up the data folder manually.\r\n" +
                "3. Network and usage safety: When using this tool to access APIs, public-network tunneling, or configuring automation workflows, be sure to protect sensitive credentials. Losses and legal liability caused by improper user configuration, password leaks, or non-compliant use are borne by the user.",
            AboutTutorialKind.Proxy => "Steps:\r\n\r\n" +
                "1. In the launcher settings page, enter the HTTP port your local proxy software listens on into the \"Proxy port\" input. Keep this port the same as the port configured in your proxy software.\r\n" +
                "2. Tick the \"Enabled\" checkbox.\r\n" +
                "3. Start or restart n8n.\r\n\r\n\r\n" +
                "Notes:\r\n\r\n" +
                "1. After changing proxy settings, restart n8n to apply. The proxy is only read once when n8n starts — changes during runtime won't take effect until restart.\r\n" +
                "2. Your proxy software must stay running while enabled. If it's closed or the port is wrong, n8n will fail to reach any network (including local).\r\n" +
                "3. When you don't need external APIs, turn the proxy off — direct connection is faster and more stable.\r\n" +
                "4. Even if your proxy software is set to \"direct / no VPN\" mode, n8n can still go online through it as long as the software is running and holding the port.",
            _ => string.Empty
        };

        AboutTutorialTextBox.CaretIndex = 0;
        AboutTutorialTextBox.ScrollToHome();
    }

    private void RenderSettingsPage()
    {
        _isLoadingSettings = true;
        try
        {
            var config = LoadConfig();
            _browserChoices = DetectInstalledBrowsers(config);

            SelectScaleComboBoxItem(config.WindowScale);
            SelectComboBoxItemByTag(SettingsThemeComboBox, NormalizeThemeMode(config.ThemeMode));
            SelectComboBoxItemByTag(SettingsLowPerformanceModeComboBox, config.LowPerformanceMode ? "True" : "False");

            SettingsBrowserComboBox.ItemsSource = _browserChoices;
            SettingsBrowserComboBox.SelectedItem = _browserChoices.FirstOrDefault(choice =>
                string.Equals(choice.BrowserPath, config.BrowserPath, StringComparison.OrdinalIgnoreCase))
                ?? _browserChoices.First();

            string startupMode = NormalizeStartupMode(config.StartupMode);
            if (startupMode == StartupModeNone && IsStartupEnabled())
                startupMode = StartupModeStartN8n;
            SelectComboBoxItemByTag(SettingsStartupModeComboBox, startupMode);
            if (_trayPopupMenu != null)
                _trayPopupMenu.StartupEnabled = startupMode != StartupModeNone;
            SelectComboBoxItemByTag(SettingsCopilotKeyModeComboBox, NormalizeCopilotKeyMode(config.CopilotKeyMode, config.TakeOverCopilotKey));
            UpdateCopilotHotkeyStatusText();
            PopulateTimezoneSelector(config.Timezone);
            // 关机计时器：输入框回填上一次成功启动过的时长（记忆），
            // 但倒计时激活状态每次启动强制清零，避免自动开始倒计时/自动关机。
            int rememberedMinutes = NormalizeSleepTimerMinutes(config.SleepTimerLastAppliedMinutes);
            SetSleepTimerInputsFromMinutes(rememberedMinutes);
            ApplySleepTimer(0, persist: true, rememberLastApplied: false);
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    private void RenderProxySettings()
    {
        _isLoadingProxySettings = true;
        try
        {
            var config = LoadConfig();
            int proxyPort = NormalizeProxyPort(config.ProxyPort);
            ProxyEnabledCheckBox.IsChecked = config.ProxyEnabled;
            ProxyPortInput.Text = proxyPort.ToString(CultureInfo.InvariantCulture);
            ProxyPortInput.IsEnabled = true;
            ProxyPortInput.Opacity = 1.0;
        }
        finally
        {
            _isLoadingProxySettings = false;
        }
    }

    private void ProxyEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingProxySettings)
            return;

        bool enabled = ProxyEnabledCheckBox.IsChecked == true;
        var config = LoadConfig();
        config.ProxyEnabled = enabled;
        config.ProxyPort = ReadProxyPortFromInputOrDefault(updateInput: true);
        SaveConfig(config);
    }

    private void ProxyPortInput_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !IsAsciiDigits(e.Text);
    }

    private void ProxyPortInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingProxySettings)
            return;

        if (!int.TryParse(ProxyPortInput.Text?.Trim(), out int port) || port is <= 0 or > 65535)
            return;

        var config = LoadConfig();
        config.ProxyPort = port;
        SaveConfig(config);
    }

    private void SettingsScaleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings || SettingsScaleComboBox.SelectedItem is not ComboBoxItem item)
            return;

        if (double.TryParse(item.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double scale) && scale > 0)
        {
            SetWindowScale(scale);
        }
    }

    private void SettingsThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings || SettingsThemeComboBox.SelectedItem is not ComboBoxItem item)
            return;

        var config = LoadConfig();
        config.ThemeMode = NormalizeThemeMode(item.Tag?.ToString());
        SaveConfig(config);
        UpdateThemeColors();
    }

    private async void SettingsLowPerformanceModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings || SettingsLowPerformanceModeComboBox.SelectedItem is not ComboBoxItem item)
            return;

        bool enabled = string.Equals(item.Tag?.ToString(), "True", StringComparison.OrdinalIgnoreCase);
        var config = LoadConfig();
        if (config.LowPerformanceMode == enabled)
            return;

        config.LowPerformanceMode = enabled;
        SaveConfig(config);
        ApplyPerformanceMode();
        await RestartLauncherForLowPerformanceModeChangeAsync();
    }

    private void SettingsBrowserComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings || SettingsBrowserComboBox.SelectedItem is not BrowserChoice choice)
            return;

        var config = LoadConfig();
        config.BrowserName = choice.DisplayName;
        config.BrowserPath = choice.BrowserPath;
        SaveConfig(config);
    }

    private void SettingsStartupModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings || SettingsStartupModeComboBox.SelectedItem is not ComboBoxItem item)
            return;

        string startupMode = NormalizeStartupMode(item.Tag?.ToString());
        bool startupEnabled = startupMode != StartupModeNone;
        SetStartupEnabled(startupEnabled);
        var config = LoadConfig();
        config.StartupMode = startupMode;
        config.StartWithWindows = startupEnabled;
        SaveConfig(config);
        if (_trayPopupMenu != null)
            _trayPopupMenu.StartupEnabled = startupEnabled;
    }

    private void ApplyTrayStartupToggle(bool enabled)
    {
        string startupMode = enabled ? StartupModeStartN8n : StartupModeNone;
        SetStartupEnabled(enabled);

        var config = LoadConfig();
        config.StartupMode = startupMode;
        config.StartWithWindows = enabled;
        SaveConfig(config);

        if (_trayPopupMenu != null)
            _trayPopupMenu.StartupEnabled = enabled;

        _isLoadingSettings = true;
        try
        {
            SelectComboBoxItemByTag(SettingsStartupModeComboBox, startupMode);
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    private void SettingsCopilotKeyModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings || SettingsCopilotKeyModeComboBox.SelectedItem is not ComboBoxItem item)
            return;

        string mode = NormalizeCopilotKeyMode(item.Tag?.ToString());
        ApplyCopilotKeyMode(mode, persist: true, showFailureMessage: true);
    }

    // ============================================================
    // 时区选择：标准 ComboBox + 下拉 Popup 顶部搜索框。
    //   - 首项固定为 "自动 (Auto)"，对应 AppConfig.Timezone = "auto"。
    //   - 后续项为便携包 app/node_modules/n8n/dist/timezones.json 内 IANA 时区。
    //   - 若保存值不在清单里，临时补一条同名条目，避免被静默改成 Auto。
    //   - 顶部搜索框实时过滤 ComboBox 条目。
    // ============================================================

    private void PopulateTimezoneSelector(string savedChoice)
    {
        if (SettingsTimezoneComboBox is null)
            return;

        string normalizedChoice = TimezoneService.NormalizeUserChoice(savedChoice);
        _currentTimezoneTag = normalizedChoice;

        var previousLoading = _isLoadingSettings;
        _isLoadingSettings = true;
        _isPopulatingTimezoneSelector = true;
        try
        {
            SettingsTimezoneComboBox.Items.Clear();

            AddTimezoneSearchBoxItem();

            string? portableRoot = ResolvePortableRoot();
            string? detected = !string.IsNullOrWhiteSpace(portableRoot)
                ? _timezoneService.DetectSystemTimezone(portableRoot)
                : null;
                string autoLastCity = (detected ?? "UTC").Split('/').Last();
            AddTimezoneItem(TimezoneService.AutoToken, $"Auto ({autoLastCity})");

            IReadOnlyList<string> zones = string.IsNullOrWhiteSpace(portableRoot)
                ? Array.Empty<string>()
                : _timezoneService.LoadSupportedTimezones(portableRoot);

            _timezoneSupportedZones.Clear();
            _timezoneSupportedZones.AddRange(zones);

            foreach (string zone in zones)
                AddTimezoneItem(zone, zone);

            bool isAuto = string.Equals(normalizedChoice, TimezoneService.AutoToken, StringComparison.OrdinalIgnoreCase);
            if (!isAuto && !ContainsTimezone(zones, normalizedChoice))
                AddTimezoneItem(normalizedChoice, normalizedChoice);

            SelectTimezoneComboBoxItemByTag(normalizedChoice);
        }
        finally
        {
            _isPopulatingTimezoneSelector = false;
            _isLoadingSettings = previousLoading;
        }
    }

    private void AddTimezoneItem(string tag, string display)
    {
        SettingsTimezoneComboBox?.Items.Add(new ComboBoxItem { Content = display, Tag = tag });
    }

    private void AddTimezoneSearchBoxItem()
    {
        if (SettingsTimezoneComboBox is null)
            return;

        var textBox = new System.Windows.Controls.TextBox
        {
            Width = 194.6666666666667,
            Height = 33.3333333333333,
            // Background 刻意保持 Transparent：底色由外层 border 提供，
            // 此处若也换成主题色会变成双层底色叠加。
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 0, 8, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 13,
            Tag = "__search_prompt__"
        };
        // 文字与光标共用同一个键：深底上若只换文字色会留下几乎不可见的黑色光标。
        textBox.SetResourceReference(
            System.Windows.Controls.Control.ForegroundProperty, TimezoneSearchBoxForegroundKey);
        textBox.SetResourceReference(
            System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty, TimezoneSearchBoxForegroundKey);
        textBox.TextChanged += TimezoneSearchBox_TextChanged;
        _timezoneSearchTextBox = textBox;

        // wpfui 默认 TextBox 模板在 TextBox 左下角挂了一条 1 DIP 高的 AccentUnderline / FocusUnderline 起笔元素，
        // 静态色约 #909DAC。外层 Border 圆角 3.3 DIP 会把这条从左下起笔的短线剪成一个小圆点，视觉就是
        //   "左下角一个小黑点，其它三个角干净"。
        // BorderThickness/BorderBrush 无法遮掉它——它不是控件边框，而是模板独立元素。
        // 处理办法：给这颗 TextBox 单独换一个极简 ControlTemplate，只保留 PART_ContentHost（承载文字与 caret），
        //   模板内不含任何 Underline / Accent 元素。外框仍由我们自己的 border 负责，视觉一致性反而更好。
        var searchBoxTemplate = new ControlTemplate(typeof(System.Windows.Controls.TextBox));
        var contentHostFactory = new FrameworkElementFactory(typeof(ScrollViewer));
        contentHostFactory.Name = "PART_ContentHost";
        contentHostFactory.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        contentHostFactory.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        contentHostFactory.SetValue(ScrollViewer.PaddingProperty, new Thickness(8, 0, 8, 0));
        contentHostFactory.SetValue(ScrollViewer.BackgroundProperty, Brushes.Transparent);
        contentHostFactory.SetValue(ScrollViewer.FocusableProperty, false);
        searchBoxTemplate.VisualTree = contentHostFactory;
        textBox.Template = searchBoxTemplate;

        // 阻止点击搜索框时事件冒泡到 ComboBoxItem 触发"选中并关闭下拉"。
        // 只在未聚焦时 Handled=true：首次点击接管焦点并抑制关闭；已聚焦时放行以保留光标定位/选词等默认行为。
        textBox.PreviewMouseLeftButtonDown += (_, args) =>
        {
            if (!textBox.IsKeyboardFocusWithin)
            {
                textBox.Focus();
                args.Handled = true;
            }
        };

        var placeholderText = new WpfTextBlock
        {
            Text = "输入时区 (Enter timezone)",
            Foreground = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(8, 0, 0, 0),
            IsHitTestVisible = false
        };

        // 占位符显隐改用 WPF Binding + 转换器：
        //   - 直接绑定 textBox.Text，任何清空路径（Ctrl+A + Delete / Backspace / Cut）都会立即刷新 Visibility；
        //   - Populate 重建 TextBox / placeholderText 实例时绑定关系随对象一起重建，不会出现闭包引用错位。
        System.Windows.Data.BindingOperations.SetBinding(
            placeholderText,
            UIElement.VisibilityProperty,
            new System.Windows.Data.Binding("Text")
            {
                Source = textBox,
                Mode = System.Windows.Data.BindingMode.OneWay,
                UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged,
                Converter = new EmptyStringToVisibilityConverter()
            });

        var contentGrid = new Grid();
        contentGrid.Children.Add(textBox);
        contentGrid.Children.Add(placeholderText);

        var border = new Border
        {
            Width = 194.6666666666667,
            Height = 33.3333333333333,
            CornerRadius = new CornerRadius(3.33333333333333),
            BorderThickness = new Thickness(1.33),
            Margin = new Thickness(0, 0, 0, 0),
            Child = contentGrid
        };
        border.SetResourceReference(Border.BackgroundProperty, TimezoneSearchBoxBackgroundKey);
        border.SetResourceReference(Border.BorderBrushProperty, TimezoneSearchBoxBorderKey);

        var item = new ComboBoxItem
        {
            Content = border,
            Tag = "__search_prompt__",
            Padding = new Thickness(0),
            FocusVisualStyle = null
        };

        SettingsTimezoneComboBox.Items.Add(item);
    }

    private void TimezoneSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SettingsTimezoneComboBox is null)
            return;

        string keyword = (_timezoneSearchTextBox?.Text ?? string.Empty).Trim();

        _isPopulatingTimezoneSelector = true;
        try
        {
            // 索引 0 = 搜索框，始终可见。
            // 索引 1 = Auto，索引 2+ = IANA 时区。
            // 只切换 Visibility，不删除/重建 Items，不改变 SelectedItem。
            for (int i = 1; i < SettingsTimezoneComboBox.Items.Count; i++)
            {
                if (SettingsTimezoneComboBox.Items[i] is not ComboBoxItem item)
                    continue;

                string tag = item.Tag?.ToString() ?? string.Empty;
                bool isAuto = string.Equals(tag, TimezoneService.AutoToken, StringComparison.OrdinalIgnoreCase);

                if (keyword.Length == 0)
                {
                    // 关键词为空：全部可见。
                    item.Visibility = Visibility.Visible;
                }
                else if (isAuto)
                {
                    // 有关键词：隐藏 Auto。
                    item.Visibility = Visibility.Collapsed;
                }
                else
                {
                    // IANA 时区：按匹配显示/隐藏。
                    item.Visibility = tag.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
            }
            // 不调 SelectTimezoneComboBoxItemByTag，不改变 SelectedItem，
            // 搜索框自然保有焦点，ComboBox 关闭态选中文字不变。
        }
        finally
        {
            _isPopulatingTimezoneSelector = false;
        }
    }

    private static bool ContainsTimezone(IReadOnlyList<string> zones, string value)
    {
        foreach (string zone in zones)
        {
            if (string.Equals(zone, value, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void SelectTimezoneComboBoxItemByTag(string tag)
    {
        if (SettingsTimezoneComboBox is null)
            return;

        foreach (var obj in SettingsTimezoneComboBox.Items)
        {
            if (obj is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString() ?? string.Empty, tag, StringComparison.OrdinalIgnoreCase))
            {
                SettingsTimezoneComboBox.SelectedItem = item;
                return;
            }
        }
    }

    private void SettingsTimezoneComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings || _isPopulatingTimezoneSelector || SettingsTimezoneComboBox?.SelectedItem is not ComboBoxItem item)
            return;

        string tag = item.Tag?.ToString() ?? TimezoneService.AutoToken;

        // 搜索提示框仅作视觉占位，不保存也不作为有效时区。
        if (string.Equals(tag, "__search_prompt__", StringComparison.OrdinalIgnoreCase))
        {
            SelectTimezoneComboBoxItemByTag(_currentTimezoneTag);
            return;
        }

        string normalized = TimezoneService.NormalizeUserChoice(tag);

        if (string.Equals(_currentTimezoneTag, normalized, StringComparison.OrdinalIgnoreCase))
            return;

        _currentTimezoneTag = normalized;

        var cfg = LoadConfig();
        if (!string.Equals(cfg.Timezone, normalized, StringComparison.OrdinalIgnoreCase))
        {
            cfg.Timezone = normalized;
            SaveConfig(cfg);
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
                return typed;

            var found = FindVisualChild<T>(child);
            if (found != null)
                return found;
        }

        return null;
    }

    private void SettingsTimezoneComboBox_DropDownOpened(object sender, EventArgs e)
    {
        if (sender is System.Windows.Controls.ComboBox comboBox)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var popup = (Popup)comboBox.Template.FindName("PART_Popup", comboBox);
                if (popup?.Child != null)
                {
                    var scrollViewer = FindVisualChild<ScrollViewer>(popup.Child);
                    if (scrollViewer != null)
                    {
                        scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                    }
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (comboBox.Items.Count > 0 &&
                    comboBox.ItemContainerGenerator.ContainerFromIndex(0) is ComboBoxItem firstItem)
                {
                    firstItem.BringIntoView();
                }
            }), System.Windows.Threading.DispatcherPriority.Render);

            // 弹出下拉时自动聚焦到搜索框。
            // 关键点：
            //   1. 不用 Keyboard.ClearFocus()——会导致 caret 先消失再出现，视觉是"闪一下"。
            //   2. 优先级用 ApplicationIdle：比 ComboBox 内部所有默认抢焦点动作都靠后，避免被覆盖。
            //   3. 追加一次性 LostKeyboardFocus 兜底：如果打开瞬间焦点还是被 ComboBox 抢走，
            //      再拉回来一次；用完立即解绑，不干扰用户之后离开 TextBox 的正常行为。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var tb = _timezoneSearchTextBox;
                if (tb is null)
                    return;

                tb.Focus();
                Keyboard.Focus(tb);
                tb.SelectAll();

                KeyboardFocusChangedEventHandler? once = null;
                once = (_, _) =>
                {
                    tb.LostKeyboardFocus -= once!;
                    if (comboBox.IsDropDownOpen && !tb.IsKeyboardFocusWithin)
                    {
                        tb.Focus();
                        Keyboard.Focus(tb);
                    }
                };
                tb.LostKeyboardFocus += once;
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    private void SettingsSleepTimerActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
            return;

        // 倒计时进行中：点击 ■ 停止倒计时（不改动上次记忆值，仅清零当前会话的倒计时状态）。
        if (_sleepTimerDueAt is not null)
        {
            ApplySleepTimer(0, persist: true, rememberLastApplied: false);
            return;
        }

        // 未在倒计时：读取三个输入框，规范化后启动倒计时并记为"上次成功启动过的时长"。
        int minutes = ReadSleepTimerMinutesFromInputs();
        SetSleepTimerInputsFromMinutes(minutes);
        ApplySleepTimer(minutes, persist: true, rememberLastApplied: minutes > 0);
    }

    private void SleepTimerTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !IsAsciiDigits(e.Text);
    }

    private void SleepTimerTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox textBox || textBox.IsKeyboardFocusWithin)
            return;

        e.Handled = true;
        textBox.Focus();
        MoveSleepTimerTextBoxCaretToEnd(textBox);
    }

    private void SleepTimerTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox textBox)
            MoveSleepTimerTextBoxCaretToEnd(textBox);
    }

    private static void MoveSleepTimerTextBoxCaretToEnd(System.Windows.Controls.TextBox textBox)
    {
        textBox.CaretIndex = textBox.Text?.Length ?? 0;
    }

    private void SleepTimerTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(System.Windows.DataFormats.Text))
        {
            e.CancelCommand();
            return;
        }

        string text = e.DataObject.GetData(System.Windows.DataFormats.Text)?.ToString() ?? string.Empty;
        if (!IsAsciiDigits(text))
            e.CancelCommand();
    }

    private void SleepTimerTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingSettings || sender is not System.Windows.Controls.TextBox textBox)
            return;

        string filtered = new(textBox.Text.Where(char.IsAsciiDigit).ToArray());
        if (textBox.Text == filtered)
            return;

        int caretIndex = Math.Min(textBox.CaretIndex, filtered.Length);
        textBox.Text = filtered;
        textBox.CaretIndex = caretIndex;
    }

    /// <summary>
    /// 睡眠计时器输入框失焦时，如果内容为空则自动填 "0"，避免留下空框。
    /// </summary>
    private void SleepTimerTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings || sender is not System.Windows.Controls.TextBox textBox)
            return;

        if (string.IsNullOrWhiteSpace(textBox.Text))
        {
            textBox.Text = "0";
            textBox.CaretIndex = textBox.Text.Length;
        }
    }

    /// <summary>
    /// 重置按钮双击确认：
    ///   - 常态：↺ 灰色。
    ///   - 第一次点击：切换到 ✔ 红底白字，并启动 5 秒计时器；期间再次点击 → 真正执行重置。
    ///   - 5 秒内未再次点击 → 自动回到常态，不做任何改动。
    /// 移除了旧版的 MessageBox 弹窗确认。
    /// </summary>
    private void SettingsResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_settingsResetConfirmPending)
        {
            _settingsResetConfirmPending = true;
            UpdateSettingsResetButtonAppearance();
            _settingsResetConfirmTimer.Stop();
            _settingsResetConfirmTimer.Start();
            return;
        }

        // 二次点击 → 真正执行重置
        _settingsResetConfirmTimer.Stop();
        _settingsResetConfirmPending = false;
        PerformSettingsReset();
        UpdateSettingsResetButtonAppearance();
    }

    /// <summary>
    /// 5 秒确认窗口到期回调：无二次点击 → 撤销待确认态，视觉回到常态 ↺。
    /// </summary>
    private void SettingsResetConfirmTimer_Tick(object? sender, EventArgs e)
    {
        _settingsResetConfirmTimer.Stop();
        if (!_settingsResetConfirmPending)
            return;

        _settingsResetConfirmPending = false;
        UpdateSettingsResetButtonAppearance();
    }

    /// <summary>
    /// 按当前 _settingsResetConfirmPending 刷新按钮外观：
    ///   - 常态：↺ 灰字白底（默认 Button 皮肤）
    ///   - 待确认：✔ 白字红底（#DC2626）
    /// emoji 基线微调复用与关机计时器 ✔ 相同的偏移量（右 1 PS、上 6 PS ≈ 右 0.67 DIP、上 4 DIP），
    /// ↺ 常态几何居中，不做偏移。
    /// </summary>
    private void UpdateSettingsResetButtonAppearance()
    {
        if (SettingsResetButton is null)
            return;

        bool pending = _settingsResetConfirmPending;
        var contentText = new System.Windows.Controls.TextBlock
        {
            Text = pending ? "✔" : "↺",
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            // ✔ 态：右 0.67 / 上 4 PS 微调，与关机计时器 ✔ 按钮字符落位一致。
            // ↺ 常态：右 2 / 上 4 PS 微调，视觉上让 ↺ 字符居中略偏右上，与 ✔ 平衡。
            Margin = pending
                ? new System.Windows.Thickness(0.67, -4.00, 0, 0)
                : new System.Windows.Thickness(2, -4, 0, 0),
            IsHitTestVisible = false
        };
        // 常态不设 Foreground：继承 Button 的 {DynamicResource ButtonForeground}，
        // 与关机计时器按钮同机制，深/浅色模式自动跟随；仅 pending 红底态强制白字。
        if (pending)
            contentText.Foreground = Brushes.White;
        // ✔ 态：显式 FontSize=15，与关机计时器 ✔ 按钮完全一致（覆盖 XAML 的 17）；
        // ↺ 常态：显式 FontSize=18，比 XAML 默认的 17 稍大，视觉体量接近 ✔。
        contentText.FontSize = pending ? 15 : 18;
        SettingsResetButton.Content = contentText;

        if (pending)
        {
            SettingsResetButton.Background = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
            SettingsResetButton.BorderBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
        }
        else
        {
            SettingsResetButton.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
            SettingsResetButton.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
        }
    }

    /// <summary>
    /// 真正执行"恢复默认设置"：保留所有 n8n 数据，仅重置启动器自身设置项。
    /// 从旧版 SettingsResetButton_Click 中抽出的原逻辑，保持行为一致。
    /// </summary>
    private void PerformSettingsReset()
    {
        var config = LoadConfig();
        config.WindowScale = ComputeAutoScale();
        // 恢复默认设置 → 浅色（2026-08-07 由 "System" 改为 "Light"，与配置字段默认值一致）。
        config.ThemeMode = "Light";
        config.LowPerformanceMode = false;
        config.BrowserName = "系统默认";
        config.BrowserPath = string.Empty;
        config.StartWithWindows = false;
        config.StartupMode = StartupModeNone;
        config.TakeOverCopilotKey = false;
        config.CopilotKeyMode = CopilotKeyModeOff;
        config.SleepTimerMinutes = 0;
        config.SleepTimerLastAppliedMinutes = 0;
        config.Timezone = TimezoneService.AutoToken;
        SetStartupEnabled(false);
        UnregisterCopilotHotkey();
        SaveConfig(config);
        SetWindowScale(config.WindowScale);
        RenderSettingsPage();
        UpdateThemeColors();
    }

    private void SelectScaleComboBoxItem(double scale)
    {
        foreach (var item in SettingsScaleComboBox.Items.OfType<ComboBoxItem>())
        {
            if (double.TryParse(item.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && Math.Abs(value - scale) < 0.001)
            {
                SettingsScaleComboBox.SelectedItem = item;
                return;
            }
        }

        SettingsScaleComboBox.SelectedIndex = 0;
    }

    private static void SelectComboBoxItemByTag(System.Windows.Controls.ComboBox comboBox, string tag)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private static List<BrowserChoice> DetectInstalledBrowsers(AppConfig config)
    {
        var choices = new List<BrowserChoice> { new("系统默认", string.Empty) };
        AddBrowserChoice(choices, "Microsoft Edge", GetBrowserAppPath("msedge.exe"));
        AddBrowserChoice(choices, "Google Chrome", GetBrowserAppPath("chrome.exe"));
        AddBrowserChoice(choices, "Mozilla Firefox", GetBrowserAppPath("firefox.exe"));
        AddBrowserChoice(choices, "Brave", GetBrowserAppPath("brave.exe"));
        AddBrowserChoice(choices, "Opera", GetBrowserAppPath("opera.exe"));

        if (!string.IsNullOrWhiteSpace(config.BrowserPath) && File.Exists(config.BrowserPath) &&
            choices.All(choice => !string.Equals(choice.BrowserPath, config.BrowserPath, StringComparison.OrdinalIgnoreCase)))
        {
            choices.Add(new(string.IsNullOrWhiteSpace(config.BrowserName) ? Path.GetFileNameWithoutExtension(config.BrowserPath) : config.BrowserName, config.BrowserPath));
        }

        return choices;
    }

    private static void AddBrowserChoice(List<BrowserChoice> choices, string name, string? path)
    {
        path = NormalizeBrowserPath(path);
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (choices.Any(choice => string.Equals(choice.BrowserPath, path, StringComparison.OrdinalIgnoreCase)))
            return;

        choices.Add(new(name, path));
    }

    private static string? GetBrowserAppPath(string exeName)
    {
        string[] appPathKeys =
        {
            $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}",
            $@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\{exeName}"
        };

        foreach (string keyPath in appPathKeys)
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath) ?? Registry.CurrentUser.OpenSubKey(keyPath);
            string? registryPath = key?.GetValue(null)?.ToString();
            if (!string.IsNullOrWhiteSpace(registryPath) && File.Exists(registryPath.Trim('"')))
                return registryPath.Trim('"');
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        string[] candidates = exeName.ToLowerInvariant() switch
        {
            "msedge.exe" => new[]
            {
                Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", exeName),
                Path.Combine(programFiles, "Microsoft", "Edge", "Application", exeName)
            },
            "chrome.exe" => new[]
            {
                Path.Combine(programFiles, "Google", "Chrome", "Application", exeName),
                Path.Combine(programFilesX86, "Google", "Chrome", "Application", exeName),
                Path.Combine(localAppData, "Google", "Chrome", "Application", exeName)
            },
            "firefox.exe" => new[]
            {
                Path.Combine(programFiles, "Mozilla Firefox", exeName),
                Path.Combine(programFilesX86, "Mozilla Firefox", exeName)
            },
            "brave.exe" => new[]
            {
                Path.Combine(programFiles, "BraveSoftware", "Brave-Browser", "Application", exeName),
                Path.Combine(programFilesX86, "BraveSoftware", "Brave-Browser", "Application", exeName),
                Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "Application", exeName)
            },
            "opera.exe" => new[]
            {
                Path.Combine(localAppData, "Programs", "Opera", exeName),
                Path.Combine(programFiles, "Opera", exeName),
                Path.Combine(appData, "Opera Software", "Opera Stable", exeName)
            },
            _ => Array.Empty<string>()
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string GetLauncherExecutablePath()
    {
        return Environment.ProcessPath
               ?? Process.GetCurrentProcess().MainModule?.FileName
               ?? Path.ChangeExtension(Environment.GetCommandLineArgs().FirstOrDefault() ?? string.Empty, ".exe");
    }

    private static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            string? value = key?.GetValue(LauncherStartupRunName)?.ToString();
            string? registeredExePath = ExtractExecutablePathFromRunCommand(value);
            string exePath = GetLauncherExecutablePath();
            return !string.IsNullOrWhiteSpace(registeredExePath) && string.Equals(registeredExePath, exePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Read startup error: {ex.Message}");
            return false;
        }
    }

    private static void SetStartupEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (enabled)
            {
                string exePath = GetLauncherExecutablePath();
                if (File.Exists(exePath))
                    key?.SetValue(LauncherStartupRunName, $"\"{exePath}\" {StartupTrayArgument}");
            }
            else
            {
                key?.DeleteValue(LauncherStartupRunName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Save startup error: {ex.Message}");
        }
    }

    private static string? ExtractExecutablePathFromRunCommand(string? command)
    {
        string trimmed = command?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;

        if (trimmed.StartsWith('"'))
        {
            int closingQuoteIndex = trimmed.IndexOf('"', 1);
            return closingQuoteIndex > 1 ? trimmed[1..closingQuoteIndex] : trimmed.Trim('"');
        }

        int firstSpaceIndex = trimmed.IndexOf(' ');
        return firstSpaceIndex > 0 ? trimmed[..firstSpaceIndex] : trimmed;
    }

    private void ApplyCopilotKeyMode(string mode, bool persist, bool showFailureMessage)
    {
        mode = NormalizeCopilotKeyMode(mode);
        bool enabled = mode != CopilotKeyModeOff;
        bool applied;

        if (enabled)
        {
            applied = RegisterCopilotHotkey();
            if (!applied)
                applied = RegisterCopilotKeyboardHook();
        }
        else
        {
            applied = UnregisterCopilotHotkey();
        }

        string effectiveMode = enabled && applied ? mode : CopilotKeyModeOff;
        bool effectiveEnabled = effectiveMode != CopilotKeyModeOff;

        if (persist || (enabled && !applied))
        {
            var config = LoadConfig();
            config.CopilotKeyMode = effectiveMode;
            config.TakeOverCopilotKey = effectiveEnabled;
            SaveConfig(config);
        }

        _isLoadingSettings = true;
        try
        {
            SelectComboBoxItemByTag(SettingsCopilotKeyModeComboBox, effectiveMode);
        }
        finally
        {
            _isLoadingSettings = false;
        }

        UpdateCopilotHotkeyStatusText();

        if (enabled && !applied && showFailureMessage)
        {
            System.Windows.MessageBox.Show(
                this,
                "无法接管 Copilot 键。标准热键注册和强制键盘钩子都启用失败，设置已自动关闭。",
                "Copilot 键接管失败",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }
    }

    private bool RegisterCopilotHotkey()
    {
        if (_isCopilotHotkeyRegistered)
            return true;

        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return false;

        bool success = RegisterHotKey(handle, COPILOT_HOTKEY_ID, MOD_WIN | MOD_SHIFT, VK_F23);
        _isCopilotHotkeyRegistered = success;
        if (!success)
            Debug.WriteLine($"[Hotkey] Register Copilot key failed: {Marshal.GetLastWin32Error()}");
        return success;
    }

    private bool RegisterCopilotKeyboardHook()
    {
        if (_isCopilotKeyboardHookRegistered)
            return true;

        _copilotKeyboardHookProc ??= CopilotKeyboardHookCallback;
        IntPtr moduleHandle = GetModuleHandle(Process.GetCurrentProcess().MainModule?.ModuleName);
        _copilotKeyboardHookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _copilotKeyboardHookProc, moduleHandle, 0);
        _isCopilotKeyboardHookRegistered = _copilotKeyboardHookHandle != IntPtr.Zero;

        if (!_isCopilotKeyboardHookRegistered)
            Debug.WriteLine($"[Hotkey] Register Copilot keyboard hook failed: {Marshal.GetLastWin32Error()}");

        return _isCopilotKeyboardHookRegistered;
    }

    private bool UnregisterCopilotHotkey()
    {
        if (_isCopilotHotkeyRegistered)
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero && !UnregisterHotKey(handle, COPILOT_HOTKEY_ID))
                Debug.WriteLine($"[Hotkey] Unregister Copilot key failed: {Marshal.GetLastWin32Error()}");

            _isCopilotHotkeyRegistered = false;
        }

        if (_isCopilotKeyboardHookRegistered)
        {
            if (_copilotKeyboardHookHandle != IntPtr.Zero && !UnhookWindowsHookEx(_copilotKeyboardHookHandle))
                Debug.WriteLine($"[Hotkey] Unregister Copilot keyboard hook failed: {Marshal.GetLastWin32Error()}");

            _copilotKeyboardHookHandle = IntPtr.Zero;
            _isCopilotKeyboardHookRegistered = false;
        }

        // 取消接管后清掉按键配对状态，避免"上次按下未抬起"的残留标志把下次接管后的首击吞掉。
        _copilotKeyReleaseWatchTimer?.Stop();
        _isCopilotKeyPhysicallyDown = false;

        return true;
    }

    /// <summary>
    /// WM_HOTKEY（RegisterHotKey 路径）的 Copilot 键处理入口。
    /// 该路径只有"按下"消息、没有"抬起"消息，且长按时系统会自动重复投递，
    /// 因此用 <see cref="_isCopilotKeyPhysicallyDown"/> + 30ms 轮询 GetAsyncKeyState 实现
    /// "一次物理按键 = 一次动作，不论按多久"；松开后再按算新的一次。
    /// </summary>
    private void HandleCopilotHotkeyMessage()
    {
        if (_isCopilotKeyPhysicallyDown)
            return;

        _isCopilotKeyPhysicallyDown = true;
        StartCopilotKeyReleaseWatch();
        HandleCopilotKeyAction();
    }

    /// <summary>
    /// 启动 F23 物理抬起轮询（仅 WM_HOTKEY 路径需要）。
    /// 30ms 检测一次 GetAsyncKeyState(VK_F23)：仍按着则继续等，已抬起则复位
    /// <see cref="_isCopilotKeyPhysicallyDown"/> 并自停，下次按下即可再触发一次动作。
    /// </summary>
    private void StartCopilotKeyReleaseWatch()
    {
        if (_copilotKeyReleaseWatchTimer is null)
        {
            _copilotKeyReleaseWatchTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(30)
            };
            _copilotKeyReleaseWatchTimer.Tick += (_, _) =>
            {
                if (IsKeyPressed((int)VK_F23))
                    return;

                _copilotKeyReleaseWatchTimer!.Stop();
                _isCopilotKeyPhysicallyDown = false;
            };
        }

        _copilotKeyReleaseWatchTimer.Stop();
        _copilotKeyReleaseWatchTimer.Start();
    }

    /// <summary>
    /// WH_KEYBOARD_LL 低级键盘钩子回调（RegisterHotKey 被占用时的兜底路径）。
    /// 用 KEYDOWN / KEYUP 配对实现"一次物理按键 = 一次动作，不论按多久"：
    ///   · KEYDOWN 且标志已置位 → 判定为 Windows 自动重复（长按约 30 次/秒），吞掉不派发动作；
    ///   · KEYDOWN 且标志未置位 → 置位并派发一次动作；
    ///   · KEYUP → 复位标志（同样吞掉，避免 F23 漏给系统触发原生 Copilot）。
    /// </summary>
    private IntPtr CopilotKeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            bool isKeyDown = wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN;
            bool isKeyUp = wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP;

            if (isKeyDown || isKeyUp)
            {
                var hookInfo = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (hookInfo.vkCode == VK_F23)
                {
                    if (isKeyUp)
                    {
                        // 抬起：结束本次按键，下一次按下才算新的一次动作。
                        _isCopilotKeyPhysicallyDown = false;
                        return (IntPtr)1;
                    }

                    if (!IsCopilotModifierPressed())
                        return CallNextHookEx(_copilotKeyboardHookHandle, nCode, wParam, lParam);

                    if (_isCopilotKeyPhysicallyDown)
                    {
                        // 自动重复：吞掉，不派发动作。
                        return (IntPtr)1;
                    }

                    _isCopilotKeyPhysicallyDown = true;
                    Dispatcher.BeginInvoke(HandleCopilotKeyAction);
                    return (IntPtr)1;
                }
            }
        }

        return CallNextHookEx(_copilotKeyboardHookHandle, nCode, wParam, lParam);
    }

    private static bool IsCopilotModifierPressed()
    {
        bool shiftPressed = IsKeyPressed(VK_SHIFT);
        bool winPressed = IsKeyPressed(VK_LWIN) || IsKeyPressed(VK_RWIN);
        return shiftPressed && winPressed;
    }

    private static bool IsKeyPressed(int virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & unchecked((short)0x8000)) != 0;
    }

    private void UpdateCopilotHotkeyStatusText()
    {
        // Copilot 键拦截行的副标题文案由 XAML 静态定义，
        // 不论 Copilot 键模式如何变化，都不覆盖 SettingsCopilotHotkeyStatusText.Text。
    }

    private void HandleCopilotKeyAction()
    {
        string mode = NormalizeCopilotKeyMode(LoadConfig().CopilotKeyMode, LoadConfig().TakeOverCopilotKey);
        if (mode == CopilotKeyModeShowLauncher || mode == CopilotKeyModeShowLauncherAndOpenN8nWeb)
            ShowAndActivate();

        if (mode == CopilotKeyModeOpenN8nWeb || mode == CopilotKeyModeShowLauncherAndOpenN8nWeb)
            _ = OpenN8nWebFromCopilotKeyAsync();
    }

    /// <summary>
    /// Copilot 键"打开 n8n 页面"动作。带重入保护：n8n 未运行时该流程需等待 HTTP ready，
    /// 耗时可达数十秒，期间若被再次触发会并发调用 StartN8nAsync 或重复打开页面。
    /// </summary>
    private async Task OpenN8nWebFromCopilotKeyAsync()
    {
        if (_isOpenN8nWebFromCopilotKeyRunning)
            return;

        _isOpenN8nWebFromCopilotKeyRunning = true;
        try
        {
            if (IsN8nManagedProcessRunning())
            {
                OpenUrl(N8nLocalWorkflowsUrl);
                return;
            }

            await StartN8nAsync(openWebWhenReady: true);
        }
        finally
        {
            _isOpenN8nWebFromCopilotKeyRunning = false;
        }
    }

    private void ApplySleepTimer(int minutes, bool persist, bool rememberLastApplied = false)
    {
        minutes = NormalizeSleepTimerMinutes(minutes);
        _sleepTimer.Stop();
        _sleepTimerDueAt = null;

        if (minutes > 0)
        {
            _sleepTimerDueAt = DateTimeOffset.Now.AddMinutes(minutes);
            _sleepTimer.Start();
        }

        if (persist)
        {
            var config = LoadConfig();
            config.SleepTimerMinutes = minutes;
            if (rememberLastApplied)
                config.SleepTimerLastAppliedMinutes = minutes;
            SaveConfig(config);
        }

        UpdateSleepTimerInputsState();
        UpdateSleepTimerStatus();
    }

    private static bool IsAsciiDigits(string value)
    {
        return value.All(char.IsAsciiDigit);
    }

    private int ReadSleepTimerMinutesFromInputs()
    {
        int days = ReadNonNegativeInt(SettingsSleepTimerDaysTextBox.Text);
        int hours = Math.Min(ReadNonNegativeInt(SettingsSleepTimerHoursTextBox.Text), 23);
        int minutes = Math.Min(ReadNonNegativeInt(SettingsSleepTimerMinutesTextBox.Text), 59);

        long totalMinutes = ((long)days * 24 * 60) + (hours * 60L) + minutes;
        return NormalizeSleepTimerMinutes(totalMinutes > int.MaxValue ? int.MaxValue : (int)totalMinutes);
    }

    private static int ReadNonNegativeInt(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) && result > 0
            ? result
            : 0;
    }

    private void SetSleepTimerInputsFromMinutes(int totalMinutes)
    {
        totalMinutes = NormalizeSleepTimerMinutes(totalMinutes);
        int days = totalMinutes / 1440;
        int remainder = totalMinutes % 1440;
        int hours = remainder / 60;
        int minutes = remainder % 60;

        SettingsSleepTimerDaysTextBox.Text = days.ToString(CultureInfo.InvariantCulture);
        SettingsSleepTimerHoursTextBox.Text = hours.ToString(CultureInfo.InvariantCulture);
        SettingsSleepTimerMinutesTextBox.Text = minutes.ToString(CultureInfo.InvariantCulture);
    }

    private void UpdateSleepTimerInputsState()
    {
        bool isTimerActive = _sleepTimerDueAt is not null;
        SettingsSleepTimerDaysTextBox.IsEnabled = !isTimerActive;
        SettingsSleepTimerHoursTextBox.IsEnabled = !isTimerActive;
        SettingsSleepTimerMinutesTextBox.IsEnabled = !isTimerActive;
        // emoji 视觉基线校正（PS ÷ 1.5 → DIP）：
        //   打勾 ✔  右移 1 PS ≈ 0.67 DIP，上移 6 PS = 4.00 DIP
        //   矩形 ■  上移 2 PS ≈ 1.33 DIP
        SettingsSleepTimerActionButton.Content = new System.Windows.Controls.TextBlock
        {
            Text = isTimerActive ? "■" : "✔",
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Margin = isTimerActive
                ? new System.Windows.Thickness(0, -1.33, 0, 0)
                : new System.Windows.Thickness(0.67, -4.00, 0, 0),
            IsHitTestVisible = false
        };
    }

    private static string FormatSleepTimerCountdown(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero)
            remaining = TimeSpan.Zero;

        int days = remaining.Days;
        int hours = remaining.Hours;
        int minutes = remaining.Minutes;
        int seconds = remaining.Seconds;

        if (days > 0)
            return string.Format(CultureInfo.InvariantCulture, "{0}d + {1:D2}:{2:D2}:{3:D2}", days, hours, minutes, seconds);

        return string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}:{2:D2}", hours, minutes, seconds);
    }

    private void UpdateSleepTimerStatus()
    {
        if (SettingsSleepTimerStatusText is null)
            return;

        // 两态文案：
        //  - 未启动倒计时：显示关机提示（默认文案与 XAML 静态默认值保持一致）
        //  - 倒计时进行中 / 到点定格：均显示 "倒计时 (Countdown)  HH:MM:SS"
        //    到点定格由 SleepTimer_Tick 显式写入 00:00:00 后立即执行关机，
        //    这里不再返回"正在退出..."之类的中间态文案。
        if (_sleepTimerDueAt is null)
        {
            SettingsSleepTimerStatusText.Text = "计时结束后将关闭计算机。(When the timer ends, the PC will shut down.)";
            return;
        }

        var remaining = _sleepTimerDueAt.Value - DateTimeOffset.Now;
        if (remaining < TimeSpan.Zero)
            remaining = TimeSpan.Zero;

        SettingsSleepTimerStatusText.Text = "倒计时 (Countdown) : " + FormatSleepTimerCountdown(remaining);
    }

    private void SleepTimer_Tick(object? sender, EventArgs e)
    {
        if (_sleepTimerDueAt is null)
            return;

        if (DateTimeOffset.Now < _sleepTimerDueAt.Value)
        {
            UpdateSleepTimerStatus();
            return;
        }

        // 到点：定格 00:00:00 → 执行系统关机（方案 A：shutdown /s /t 0 /f）。
        // 不主动停 n8n、不等工作流：交给 Windows 关机流程统一处理。
        // 不修改 SleepTimerLastApplied：每次启动会清零，无需在这里维护"上次倒计时"记忆。
        _sleepTimer.Stop();
        _sleepTimerDueAt = null;
        if (SettingsSleepTimerStatusText is not null)
            SettingsSleepTimerStatusText.Text = "倒计时 (Countdown) : " + FormatSleepTimerCountdown(TimeSpan.Zero);
        UpdateSleepTimerInputsState();

        var config = LoadConfig();
        config.SleepTimerMinutes = 0;
        SaveConfig(config);

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = "/s /t 0 /f",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SleepTimer] shutdown /s /t 0 /f failed: {ex.Message}");
            System.Windows.MessageBox.Show(
                this,
                $"到点关机失败：{ex.Message}",
                "关机失败 (Shutdown Failed)",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    private void FileAccessPermissionToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingFileAccessPermissions)
            return;

        SaveFileAccessPermissionSlotsFromUi();
        UpdateAllFileAccessPathInputVisualStates();
    }

    private void FileAccessPathInput_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox textBox)
            return;

        int slotIndex = GetFileAccessSlotIndex(textBox);
        if (GetFileAccessToggle(slotIndex).IsChecked == true)
        {
            textBox.Select(0, 0);
            e.Handled = true;
            return;
        }

        textBox.SelectAll();
        e.Handled = true;
    }

    private void ClearFileAccessPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button)
            return;

        int slotIndex = GetFileAccessClearButtonSlotIndex(button);
        GetFileAccessPathInput(slotIndex).Clear();
        GetFileAccessToggle(slotIndex).IsChecked = false;
        SaveFileAccessPermissionSlotsFromUi();
        UpdateFileAccessPathInputVisualState(slotIndex);
        e.Handled = true;
    }

    private void BrowseFileAccessPermissionFolder(int slotIndex)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择要授权给 n8n 读写的文件夹。不能选择盘符根目录。",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        string currentPath = GetFileAccessPathInput(slotIndex).Text?.Trim() ?? string.Empty;
        if (Directory.Exists(currentPath))
            dialog.SelectedPath = currentPath;

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return;

        string selectedPath = NormalizeFileAccessPath(dialog.SelectedPath);
        if (IsDriveRootPath(selectedPath))
        {
            _ = ShowFileAccessRootDeniedOverlayAsync();
            return;
        }

        GetFileAccessPathInput(slotIndex).Text = selectedPath;
        GetFileAccessToggle(slotIndex).IsChecked = true;
        SaveFileAccessPermissionSlotsFromUi();
        UpdateFileAccessPathInputVisualState(slotIndex);
    }

    private System.Windows.Controls.CheckBox GetFileAccessToggle(int slotIndex)
    {
        return slotIndex switch
        {
            0 => PathToggle03,
            1 => PathToggle04,
            2 => PathToggle05,
            _ => throw new ArgumentOutOfRangeException(nameof(slotIndex))
        };
    }

    private System.Windows.Controls.TextBox GetFileAccessPathInput(int slotIndex)
    {
        return slotIndex switch
        {
            0 => PathInput03,
            1 => PathInput04,
            2 => PathInput05,
            _ => throw new ArgumentOutOfRangeException(nameof(slotIndex))
        };
    }

    private System.Windows.Controls.Button GetFileAccessClearButton(int slotIndex)
    {
        return slotIndex switch
        {
            0 => ClearPathButton03,
            1 => ClearPathButton04,
            2 => ClearPathButton05,
            _ => throw new ArgumentOutOfRangeException(nameof(slotIndex))
        };
    }

    private int GetFileAccessSlotIndex(System.Windows.Controls.TextBox textBox)
    {
        if (ReferenceEquals(textBox, PathInput03))
            return 0;
        if (ReferenceEquals(textBox, PathInput04))
            return 1;
        if (ReferenceEquals(textBox, PathInput05))
            return 2;

        throw new ArgumentException("未知的文件夹授权路径输入框。", nameof(textBox));
    }

    private int GetFileAccessClearButtonSlotIndex(System.Windows.Controls.Button button)
    {
        if (ReferenceEquals(button, ClearPathButton03))
            return 0;
        if (ReferenceEquals(button, ClearPathButton04))
            return 1;
        if (ReferenceEquals(button, ClearPathButton05))
            return 2;

        throw new ArgumentException("未知的文件夹授权路径清空按钮。", nameof(button));
    }

    private void UpdateAllFileAccessPathInputVisualStates()
    {
        for (int i = 0; i < 3; i++)
            UpdateFileAccessPathInputVisualState(i);
    }

    private void UpdateFileAccessPathInputVisualState(int slotIndex)
    {
        var input = GetFileAccessPathInput(slotIndex);
        var clearButton = GetFileAccessClearButton(slotIndex);
        bool hasPath = !string.IsNullOrWhiteSpace(input.Text);
        bool isLocked = GetFileAccessToggle(slotIndex).IsChecked == true && hasPath;

        input.SetResourceReference(
            System.Windows.Controls.Control.ForegroundProperty,
            isLocked ? FileAccessPathInputLockedForegroundKey : FileAccessPathInputForegroundKey);
        input.Focusable = !isLocked;
        input.Cursor = isLocked ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.IBeam;
        clearButton.Visibility = hasPath ? Visibility.Visible : Visibility.Collapsed;

        if (isLocked)
        {
            input.Select(0, 0);
            if (input.IsKeyboardFocusWithin)
                Keyboard.ClearFocus();
        }
    }

    private void RenderFileAccessPermissionSlots()
    {
        _isLoadingFileAccessPermissions = true;
        try
        {
            var config = LoadConfig();
            EnsureConfigDefaults(config);
            for (int i = 0; i < 3; i++)
            {
                var slot = config.FileAccessPermissions[i];
                GetFileAccessPathInput(i).Text = slot.Path;
                GetFileAccessToggle(i).IsChecked = slot.IsEnabled && !string.IsNullOrWhiteSpace(slot.Path);
                UpdateFileAccessPathInputVisualState(i);
            }
        }
        finally
        {
            _isLoadingFileAccessPermissions = false;
        }
    }

    private void SaveFileAccessPermissionSlotsFromUi()
    {
        var config = LoadConfig();
        EnsureConfigDefaults(config);

        for (int i = 0; i < 3; i++)
        {
            string path = NormalizeFileAccessPath(GetFileAccessPathInput(i).Text);
            bool enabled = GetFileAccessToggle(i).IsChecked == true && !string.IsNullOrWhiteSpace(path) && !IsDriveRootPath(path);
            config.FileAccessPermissions[i].Path = path;
            config.FileAccessPermissions[i].IsEnabled = enabled;
        }

        SaveConfig(config);
    }

    private List<string> GetEnabledFileAccessPermissionPaths()
    {
        var config = LoadConfig();
        EnsureConfigDefaults(config);
        return config.FileAccessPermissions
            .Where(static slot => slot.IsEnabled)
            .Select(static slot => NormalizeFileAccessPath(slot.Path))
            .Where(static path => !string.IsNullOrWhiteSpace(path) && !IsDriveRootPath(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task ShowFileAccessRootDeniedOverlayAsync()
    {
        FileAccessRootDeniedOverlay.Visibility = Visibility.Visible;
        await Task.Delay(3600);
        FileAccessRootDeniedOverlay.Visibility = Visibility.Collapsed;
    }

    private static string NormalizeFileAccessPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return path.Trim();
        }
    }

    private static bool IsDriveRootPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            string fullPath = Path.GetFullPath(path.Trim());
            string root = Path.GetPathRoot(fullPath) ?? string.Empty;
            return !string.IsNullOrWhiteSpace(root)
                && string.Equals(
                    fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task AutoStartN8nFromStartupModeAsync()
    {
        string startupMode = NormalizeStartupMode(LoadConfig().StartupMode);
        if (startupMode == StartupModeNone)
            return;

        bool openWebWhenReady = startupMode == StartupModeStartN8nAndOpenWeb;
        AppendConsoleOutput(openWebWhenReady
            ? "[INFO] 开机启动模式：正在自动启动 n8n，启动完成后将打开页面。"
            : "[INFO] 开机启动模式：正在自动启动 n8n，启动完成后不自动打开页面。");
        await StartN8nAsync(openWebWhenReady: openWebWhenReady);
    }

    private async Task RestartN8nAsync()
    {
        if (_isRestartingN8n || _isStoppingN8n)
        {
            AppendConsoleOutput("[INFO] n8n 正在重启/中止中，请稍候；当前重启请求已忽略。");
            return;
        }

        if (_isStartingN8n)
        {
            AppendConsoleOutput("[INFO] n8n 正在启动中，请稍候；当前重启请求已忽略。");
            return;
        }

        _isRestartingN8n = true;
        SetLaunchTile02ActionTextOpenPageStable();
        try
        {
            AppendConsoleOutput("[INFO] 正在重启 n8n：先异步中止当前 n8n 进程树，然后重新启动。界面不会在等待进程退出时卡住。");
            SetN8nRunningStatus("重启中 (Restarting)", "正在重启", "Restarting n8n...");
            await StopN8nProcessAsync(showNoProcessMessage: false);
            await Task.Delay(500);
            await StartN8nAsync();
        }
        finally
        {
            _isRestartingN8n = false;
            UpdateNodeModulesRepairAvailability();
        }
    }

    private async Task StartN8nAsync(bool openWebWhenReady = true)
    {
        if (!CanUseStartLaunchTile())
        {
            AppendConsoleOutput("[INFO] 正在处理运行依赖，请稍候；当前启动请求已忽略。");
            return;
        }

        if (_isStartingN8n)
        {
            AppendConsoleOutput("[INFO] n8n 正在启动中，请稍候...");
            return;
        }

        if (_n8nProcess is { HasExited: false })
        {
            SetN8nRunningStatus("运行中 (Running)", "运行中", "n8n is already running");
            AppendConsoleOutput("[INFO] n8n 已经在运行，已忽略重复启动。对应脚本逻辑：start /b 后台进程仍在运行。");
            if (openWebWhenReady)
                OpenUrl(N8nLocalWorkflowsUrl);
            FadeLaunchTile02ActionTextIn(LaunchTile02OpenWorkbenchText);
            return;
        }

        // 孤儿 n8n 进程清理（防御式）：
        // 启动器不知道有活的 _n8nProcess，但机器上可能残留上次未干净退出的 node.exe（本便携包路径下）。
        // 典型场景：启动器进程崩溃 / 用户强杀启动器 / 蓝屏 / IDE 直接关调试。
        // 若不清理，新启动的 n8n 会因 5678 端口被占而秒退。
        // 精确匹配 MainModule.FileName == 本便携包 runtime\node\node.exe，绝不误杀系统其他 Node 进程。
        try
        {
            string? portableRootForOrphanCheck = ResolvePortableRoot();
            if (!string.IsNullOrWhiteSpace(portableRootForOrphanCheck))
                KillOrphanN8nProcesses(Path.GetFullPath(portableRootForOrphanCheck));
        }
        catch (Exception ex)
        {
            // 孤儿清理失败不阻塞启动流程：即便清理不彻底，后续 node.exe start 也可能因端口占用报错，
            // 用户能通过日志看到具体原因。
            Debug.WriteLine($"[OrphanKill] Unexpected error: {ex.Message}");
        }

        _isStartingN8n = true;
        if (!_isRestartingN8n)
            StartLaunchRocketPreviewAnimation();
        SetN8nRunningStatus("启动中 (Starting)", "正在启动", "Starting n8n...");
        ClearConsoleOutput();

        try
        {
            string? portableRoot = ResolvePortableRoot();
            if (string.IsNullOrWhiteSpace(portableRoot))
                throw new InvalidOperationException("无法定位 n8n 便携包根目录。脚本中的 %~dp0 无法映射到启动器根目录。");

            portableRoot = Path.GetFullPath(portableRoot);
            if (!await EnsureNodeModulesReadyForLaunchAsync(portableRoot))
                return;

            string rootPathForEnvironment = EnsureTrailingDirectorySeparator(portableRoot);
            string dataDir = Path.Combine(portableRoot, "data");
            string nodeExePath = Path.Combine(portableRoot, "runtime", "node", "node.exe");
            string n8nBinPath = Path.Combine(portableRoot, "app", "node_modules", "n8n", "bin", "n8n");
            string nodePath = Path.Combine(portableRoot, "runtime", "node", "node_modules");
            string pythonPath = Path.Combine(portableRoot, "runtime", "python", "python");
            string ffmpegPath = Path.Combine(portableRoot, "runtime", "ffmpeg", "bin");

            Directory.CreateDirectory(dataDir);

            if (!File.Exists(nodeExePath))
                throw new FileNotFoundException("找不到启动脚本指定的 node.exe。", nodeExePath);

            if (!File.Exists(n8nBinPath))
                throw new FileNotFoundException("找不到启动脚本指定的 n8n 入口文件，请先修复 node_modules。", n8nBinPath);

            var proxySettings = GetProxyLaunchSettings();
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(userProfile))
                userProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? string.Empty;

            string arguments = $"\"{n8nBinPath}\" start --userDir=\"{dataDir}\"";
            _lastN8nCommandLine = $"\"{nodeExePath}\" {arguments}";

            var startInfo = new ProcessStartInfo
            {
                FileName = nodeExePath,
                Arguments = arguments,
                WorkingDirectory = portableRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            List<string> enabledFileAccessPaths = GetEnabledFileAccessPermissionPaths();
            // 时区注入：LoadConfig() 每次都做归一化，取当前用户设置（"auto" 或具体 IANA），
            // 交给 TimezoneService 决策出真正注入 n8n 进程的 effectiveTimezone。
            string effectiveTimezone = _timezoneService.ResolveEffectiveTimezone(portableRoot, LoadConfig().Timezone);
            AppendConsoleOutput($"[INFO] 时区: {effectiveTimezone}");
            ApplyRunBatEnvironment(startInfo, portableRoot, rootPathForEnvironment, dataDir, nodePath, pythonPath, ffmpegPath, proxySettings.Enabled, proxySettings.Port, proxySettings.Url, userProfile, enabledFileAccessPaths, effectiveTimezone);

            AppendConsoleOutput("======================================================");
            AppendConsoleOutput("                     n8n portable");
            AppendConsoleOutput("======================================================");
            AppendConsoleOutput($"[INFO] 工作目录: {portableRoot}");
            AppendConsoleOutput(proxySettings.Enabled ? $"[INFO] 代理地址: {proxySettings.Url}" : "[INFO] 代理：未启用");
            AppendConsoleOutput($"[INFO] 正在将数据强制锁定在: {dataDir}");
            AppendConsoleOutput("[INFO] 🚀");
            AppendConsoleOutput($"[COMMAND] {_lastN8nCommandLine}");

            int autoOpenRequested = 0;
            int startGeneration = System.Threading.Interlocked.Increment(ref _n8nStartGeneration);
            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            void AppendProcessOutputAndOpenWhenReady(string line)
            {
                AppendConsoleOutput(line);
                if (!IsN8nReadyOutputLine(line))
                    return;

                if (!openWebWhenReady)
                    return;

                if (System.Threading.Interlocked.Exchange(ref autoOpenRequested, 1) != 0)
                    return;

                _ = OpenN8nWhenHttpReadyAsync(process, startGeneration);
            }

            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                    AppendProcessOutputAndOpenWhenReady(args.Data);
            };

            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                    AppendProcessOutputAndOpenWhenReady(args.Data);
            };

            process.Exited += (_, _) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    int exitCode;
                    try
                    {
                        exitCode = process.ExitCode;
                    }
                    catch
                    {
                        exitCode = -1;
                    }

                    // 关键：Exited 触发时 _isStoppingN8n 常已被 StopN8nProcessAsync 清零（taskkill 异步），
                    // 因此额外读取 _userStopRequestedExit 持久标志，保证用户主动停止不会被误判为"启动失败"。
                    bool wasStopping = _isStoppingN8n || _userStopRequestedExit;
                    AppendConsoleOutput($"[INFO] n8n 进程已退出，ExitCode={exitCode}");

                    if (ReferenceEquals(_n8nProcess, process))
                    {
                        _n8nProcess.Dispose();
                        _n8nProcess = null;
                    }

                    _isStartingN8n = false;

                    if (wasStopping || _isRestartingN8n)
                    {
                        // 用户主动停止 / 重启内部中止：视觉态已由 StopN8nProcessAsync 处理为"已停止"，此处只消费标志。
                        _userStopRequestedExit = false;
                        _isStoppingN8n = false;
                        return;
                    }

                    // wasStopping = false && _isRestartingN8n = false 才走 exitCode 判定：这才是真正的自然退出/崩溃。
                    SetN8nRunningStatus(exitCode == 0 ? "已停止 (Stopped)" : "启动失败 (Failed)", "启动 n8n", exitCode == 0 ? "Stopped" : "Failed");
                    StopLaunchRocketPreviewAnimation();
                    _isStoppingN8n = false;
                    _userStopRequestedExit = false;
                });
            };

            if (!process.Start())
                throw new InvalidOperationException("Process.Start 返回 false，n8n 进程未启动。相当于 start /b 未成功创建后台进程。");

            _n8nProcess = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            _isStartingN8n = false;
            SetN8nRunningStatus("运行中 (Running)", "运行中", "Running — n8n launched");
        }
        catch (Exception ex)
        {
            _isStartingN8n = false;
            AppendConsoleOutput($"[ERROR] n8n 启动失败：{ex.Message}");
            SetN8nRunningStatus("启动失败 (Failed)", "启动失败", "Failed");
            System.Windows.MessageBox.Show(this, $"n8n 启动失败：{ex.Message}", "启动失败", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        finally
        {
            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// 清理便携包目录下的孤儿 n8n 进程（node.exe 是本便携包 runtime\node\node.exe）。
    /// 只匹配"MainModule.FileName == 本便携包 runtime\node\node.exe"的进程，绝不误杀：
    ///   · 用户机器上的系统 Node（不同路径）
    ///   · VS Code / Electron 应用内的 Node.js runtime（不同路径）
    ///   · 用户其它 Node 项目
    /// 只有真正来自本便携包的孤儿 node.exe 会被清理。
    /// </summary>
    private void KillOrphanN8nProcesses(string portableRoot)
    {
        string nodeExePath = Path.GetFullPath(Path.Combine(portableRoot, "runtime", "node", "node.exe"));
        int killedCount = 0;

        foreach (var process in Process.GetProcessesByName("node"))
        {
            try
            {
                // 读取进程主模块路径。跨会话/权限不足读不到（32 位 vs 64 位、权限拒绝、进程已退出）→ 跳过。
                string? modulePath = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(modulePath))
                    continue;

                if (!string.Equals(
                        Path.GetFullPath(modulePath),
                        nodeExePath,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                AppendConsoleOutput($"[INFO] 检测到孤儿 n8n 进程 PID={process.Id}，正在清理...");
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(3000))
                    AppendConsoleOutput($"[WARN] 孤儿 n8n 进程 PID={process.Id} 3 秒内未完全退出，Windows 仍在清理子进程。");
                killedCount++;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OrphanKill] Skip PID={process.Id}: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        if (killedCount > 0)
            AppendConsoleOutput($"[INFO] 已清理 {killedCount} 个孤儿 n8n 进程。");
    }

    private async Task<bool> EnsureNodeModulesReadyForLaunchAsync(string portableRoot)
    {
        NodeModulesHealthState state = ReadNodeModulesHealth(portableRoot);
        RenderNodeModulesHealth(state, animate: true);

        if (state.NodeModulesOk && state.NodeModulesTarOk)
            return true;

        if (!state.NodeModulesTarOk)
        {
            AppendConsoleOutput("[ERROR] 启动前检查失败：缺少可用的 app\\node_modules.tar，无法自动修复 node_modules，也不会继续启动 n8n。");
            SetN8nRunningStatus("启动失败 (Failed)", "启动失败", "Missing node_modules.tar");
            await ShowNodeModulesTarDownloadPromptAsync();
            _isStartingN8n = false;
            return false;
        }

        AppendConsoleOutput("[INFO] 启动前检查：node_modules 不满足运行条件，但 node_modules.tar 可用。启动器将自动执行“修复”流程，解压完成后继续启动 n8n。");
        SetN8nRunningStatus("修复中 (Repairing)", "正在修复", "Preparing node_modules...");
        await RepairNodeModulesFromTarAsync(portableRoot);

        state = ReadNodeModulesHealth(portableRoot);
        RenderNodeModulesHealth(state, animate: true);

        if (state.NodeModulesOk && state.NodeModulesTarOk)
        {
            AppendConsoleOutput("[INFO] node_modules 自动修复完成，继续启动 n8n。");
            SetN8nRunningStatus("启动中 (Starting)", "正在启动", "Starting n8n...");
            return true;
        }

        AppendConsoleOutput("[ERROR] node_modules 自动修复后仍不满足运行条件，已阻止 n8n 启动。");
        SetN8nRunningStatus("启动失败 (Failed)", "启动失败", "node_modules repair failed");
        System.Windows.MessageBox.Show(this, "node_modules 自动修复后仍不满足运行条件，已停止启动 n8n。", "启动失败", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        _isStartingN8n = false;
        return false;
    }

    private static void ApplyRunBatEnvironment(
        ProcessStartInfo startInfo,
        string portableRoot,
        string rootPathForEnvironment,
        string dataDir,
        string nodePath,
        string pythonPath,
        string ffmpegPath,
        bool proxyEnabled,
        int proxyPort,
        string proxyUrl,
        string userProfile,
        IReadOnlyList<string> enabledFileAccessPaths,
        string effectiveTimezone)
    {
        string existingPath = startInfo.Environment.TryGetValue("PATH", out string? currentPath) ? currentPath ?? string.Empty : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        startInfo.Environment["PATH"] = string.Join(';', new[] { Path.Combine(portableRoot, "runtime", "node"), pythonPath, ffmpegPath, existingPath }.Where(static item => !string.IsNullOrWhiteSpace(item)));
        startInfo.Environment["ROOT_PATH"] = rootPathForEnvironment;
        startInfo.Environment["NODE_PATH"] = nodePath;
        startInfo.Environment["N8N_COMMUNITY_PACKAGES_ENABLED"] = "true";
        startInfo.Environment["n8n_BLOCK_SVC_JS_NODE_BUILT_IN_MODULES"] = "false";
        startInfo.Environment["n8n_USER_FOLDER"] = dataDir;
        startInfo.Environment["n8n_ENCRYPTION_KEY"] = "n8n_portable_2026";
        startInfo.Environment["n8n_DEFAULT_LOCALE"] = "zh";
        startInfo.Environment["NODE_TLS_REJECT_UNAUTHORIZED"] = "0";
        startInfo.Environment["NODES_EXCLUDE"] = "[]";
        var restrictedFileAccessPaths = new List<string> { userProfile, rootPathForEnvironment };
        restrictedFileAccessPaths.AddRange(enabledFileAccessPaths.Where(static path => !string.IsNullOrWhiteSpace(path)));
        startInfo.Environment["n8n_RESTRICT_FILE_ACCESS_TO"] = string.Join(';', restrictedFileAccessPaths.Distinct(StringComparer.OrdinalIgnoreCase));
        startInfo.Environment["NODE_FUNCTION_ALLOW_BUILTIN"] = "*";
        startInfo.Environment["NODE_FUNCTION_ALLOW_EXTERNAL"] = "*";
        startInfo.Environment["N8N_SECURE_COOKIE"] = "false";
        startInfo.Environment["N8N_BLOCK_EXTERNAL_CODE_EXECUTION"] = "false";
        startInfo.Environment["N8N_RUNNERS_HEARTBEAT_INTERVAL"] = "600000";
        startInfo.Environment["N8N_RUNNERS_TASK_TIMEOUT"] = "604800";
        startInfo.Environment["N8N_EXECUTION_PROCESS"] = "main";
        startInfo.Environment["N8N_EXECUTION_TIMEOUT"] = "600000";
        startInfo.Environment["EXECUTIONS_DATA_PRUNE"] = "true";
        startInfo.Environment["EXECUTIONS_DATA_MAX_AGE"] = "72";
        // 时区注入：GENERIC_TIMEZONE 是 n8n 官方业务变量（影响 Schedule Trigger / Cron 触发时刻与执行记录时间戳），
        // TZ 是 Node/JS 通用变量，一并注入以保证 new Date() 等原生行为一致。
        if (!string.IsNullOrWhiteSpace(effectiveTimezone))
        {
            startInfo.Environment["GENERIC_TIMEZONE"] = effectiveTimezone;
            startInfo.Environment["TZ"] = effectiveTimezone;
        }
        if (proxyEnabled)
        {
            startInfo.Environment["proxy_port"] = proxyPort.ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["HTTP_PROXY"] = proxyUrl;
            startInfo.Environment["HTTPS_PROXY"] = proxyUrl;
        }
        else
        {
            startInfo.Environment.Remove("proxy_port");
            startInfo.Environment.Remove("HTTP_PROXY");
            startInfo.Environment.Remove("HTTPS_PROXY");
        }
    }

    private (bool Enabled, int Port, string Url) GetProxyLaunchSettings()
    {
        var config = LoadConfig();
        bool enabled = config.ProxyEnabled;
        int port = enabled ? GetProxyPortOrDefault() : NormalizeProxyPort(config.ProxyPort);
        string proxyUrl = $"http://127.0.0.1:{port}";
        return (enabled, port, proxyUrl);
    }

    private int GetProxyPortOrDefault()
    {
        int port = ReadProxyPortFromInputOrDefault(updateInput: true);
        var config = LoadConfig();
        if (config.ProxyPort != port)
        {
            config.ProxyPort = port;
            SaveConfig(config);
        }

        return port;
    }

    private int ReadProxyPortFromInputOrDefault(bool updateInput)
    {
        string? text = ProxyPortInput.Text?.Trim();
        if (int.TryParse(text, out int port) && port is > 0 and <= 65535)
            return port;

        if (updateInput)
            ProxyPortInput.Text = "7890";
        return 7890;
    }

    private static string EnsureTrailingDirectorySeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// 控制台日志保留上限（行）。超过后按批裁掉最旧日志，防止 n8n 长时间运行时日志无界增长。
    /// </summary>
    private const int ConsoleOutputMaxLines = 5000;

    /// <summary>
    /// 单次裁剪掉的行数。批量裁剪（而非每超一行裁一行）把 O(n) 的重建摊销到 1000 行一次，
    /// 避免逼近上限后每来一行都触发全量字符串重建。
    /// </summary>
    private const int ConsoleOutputTrimBatchLines = 1000;

    /// <summary>当前 <see cref="_consoleOutputBuilder"/> 中的日志行数，用于裁剪判定，避免反复扫描字符串。</summary>
    private int _consoleOutputLineCount;

    private void ClearConsoleOutput()
    {
        _consoleOutputBuilder.Clear();
        _consoleOutputLineCount = 0;
        ConsoleOutputTextBox.Clear();
    }

    /// <summary>
    /// 追加一行控制台日志。
    ///
    /// 内存优化（2026-08-07）：原实现每追加一行都执行
    /// <c>ConsoleOutputTextBox.Text = _consoleOutputBuilder.ToString()</c>，
    /// 即把整份日志重建成一个新字符串，累计分配量是 O(n²)；且日志超过 85KB 后每次重建的字符串
    /// 都落入大对象堆（LOH），LOH 默认不压缩会持续碎片化 —— 这是启动器内存缓慢爬升到 400MB
    /// 再被 GC 拉回的主因。
    ///
    /// 现改为 <see cref="System.Windows.Controls.TextBox.AppendText"/> 增量追加（O(1) 摊销），
    /// 并给 <see cref="_consoleOutputBuilder"/> 加上 <see cref="ConsoleOutputMaxLines"/> 行上限。
    /// 「复制日志 / 导出日志」读取的仍是 <c>ConsoleOutputTextBox.Text</c>，行为不变。
    /// </summary>
    private void AppendConsoleOutput(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AppendConsoleOutput(line));
            return;
        }

        _consoleOutputBuilder.AppendLine(line);
        _consoleOutputLineCount++;

        // 增量追加：不再整体 ToString() 赋值，避免 O(n²) 分配与 LOH 碎片。
        ConsoleOutputTextBox.AppendText(line + Environment.NewLine);

        if (_consoleOutputLineCount > ConsoleOutputMaxLines)
            TrimConsoleOutputToLimit();

        // CaretIndex 用 builder 长度推导：builder 用 AppendLine（\r\n）、TextBox 用 Environment.NewLine（\r\n），
        // 两者字符数一致。这样避免读取 TextBox.Text（其 getter 每次都会重建完整字符串，同样是 O(n)）。
        ConsoleOutputTextBox.CaretIndex = _consoleOutputBuilder.Length;
        ConsoleOutputTextBox.ScrollToEnd();
    }

    /// <summary>
    /// 把控制台日志裁剪到上限以内：一次性丢弃最旧的若干行，使剩余行数回落到
    /// <c>ConsoleOutputMaxLines - ConsoleOutputTrimBatchLines</c>。
    /// 仅在超限时调用（约每 1000 行一次），因此这里的全量重建开销可以接受。
    /// </summary>
    private void TrimConsoleOutputToLimit()
    {
        int keepLines = ConsoleOutputMaxLines - ConsoleOutputTrimBatchLines;
        int removeLines = _consoleOutputLineCount - keepLines;
        if (removeLines <= 0)
            return;

        string text = _consoleOutputBuilder.ToString();
        int cutIndex = 0;
        for (int i = 0; i < removeLines; i++)
        {
            int next = text.IndexOf('\n', cutIndex);
            if (next < 0)
            {
                cutIndex = text.Length;
                break;
            }

            cutIndex = next + 1;
        }

        string trimmed = text[cutIndex..];
        _consoleOutputBuilder.Clear();
        _consoleOutputBuilder.Append(trimmed);
        _consoleOutputLineCount = keepLines;
        ConsoleOutputTextBox.Text = trimmed;
    }

    private const string LaunchTile02StartText = "开始运行 (Start)";
    private const string LaunchTile02OpenWorkbenchText = "打开操作页 (Open Page)";

    private async Task FadeLaunchTile02ActionTextOutAsync()
    {
        LaunchTile02ActionText.BeginAnimation(OpacityProperty, null);
        var animation = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromSeconds(1))
        };
        LaunchTile02ActionText.BeginAnimation(OpacityProperty, animation);
        await Task.Delay(TimeSpan.FromSeconds(1));
    }

    private void FadeLaunchTile02ActionTextIn(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => FadeLaunchTile02ActionTextIn(text));
            return;
        }

        LaunchTile02ActionText.BeginAnimation(OpacityProperty, null);
        LaunchTile02ActionText.Text = text;
        LaunchTile02ActionText.Opacity = 0;
        var animation = new DoubleAnimation
        {
            To = 1,
            Duration = new Duration(TimeSpan.FromSeconds(1))
        };
        LaunchTile02ActionText.BeginAnimation(OpacityProperty, animation);
    }

    private void ResetLaunchTile02ActionTextToStart()
    {
        LaunchTile02ActionText.BeginAnimation(OpacityProperty, null);
        LaunchTile02ActionText.Text = LaunchTile02StartText;
        LaunchTile02ActionText.Opacity = 1;
    }

    private void SetLaunchTile02ActionTextOpenPageStable()
    {
        LaunchTile02ActionText.BeginAnimation(OpacityProperty, null);
        LaunchTile02ActionText.Text = LaunchTile02OpenWorkbenchText;
        LaunchTile02ActionText.Opacity = 1;
    }

    private void SetN8nRunningStatus(string consoleStatus, string tileTitle, string tileStatus)
    {
        ConsoleStatusText.Text = consoleStatus;
        LaunchTile02TitleText.Text = tileTitle;
        LaunchTile02StatusText.Text = tileStatus;
        if (!_isRestartingN8n)
        {
            if (consoleStatus.Contains("未运行") || consoleStatus.Contains("已停止") || consoleStatus.Contains("启动失败"))
                ResetLaunchTile02ActionTextToStart();
            else if (consoleStatus.Contains("运行中"))
                SetLaunchTile02ActionTextOpenPageStable();
        }
        UpdateTrayTooltip();

        // 状态文案集中映射到画板按钮视觉态（双稳态 + 两段过渡）。
        // 重启进行中的内部中止态切换由 ApplyLaunchTileVisualState 内部抑制，视觉统一为 Starting。
        LaunchTileVisualState visualState = MapConsoleStatusToVisualState(consoleStatus);
        UpdateStopLaunchTileAvailability(visualState);
        UpdateRestartLaunchTileAvailability(visualState);
        UpdateConsoleActionTilesAvailability(visualState);
        ApplyLaunchTileVisualState(visualState);
        UpdateNodeModulesRepairAvailability();

        // 内存优化：窗口隐藏且动画已挂起时，n8n 状态变化需要短暂解除挂起让新状态动画走完。
        // 窗口可见时该调用只是空转。
        OnN8nStatusChangedForMemory();
    }

    private void InitializeStopLaunchTileState()
    {
        _stopLaunchTileImage = FindName("StopLaunchTileImage") as System.Windows.Controls.Image;
        _stopLaunchTileGrayscaleImage = FindName("StopLaunchTileGrayscaleImage") as System.Windows.Controls.Image;
        _stopLaunchTileNormalSource = _stopLaunchTileImage?.Source;
        _stopLaunchTileGrayscaleSource = CreateGrayscaleImageSource(_stopLaunchTileNormalSource);
        if (_stopLaunchTileGrayscaleImage is not null)
        {
            _stopLaunchTileGrayscaleImage.Source = _stopLaunchTileGrayscaleSource ?? _stopLaunchTileNormalSource;
        }

        _restartLaunchTileImage = FindName("RestartLaunchTileImage") as System.Windows.Controls.Image;
        _restartLaunchTileGrayscaleImage = FindName("RestartLaunchTileGrayscaleImage") as System.Windows.Controls.Image;
        _restartLaunchTileNormalSource = _restartLaunchTileImage?.Source;
        _restartLaunchTileGrayscaleSource = CreateGrayscaleImageSource(_restartLaunchTileNormalSource);
        if (_restartLaunchTileGrayscaleImage is not null)
        {
            _restartLaunchTileGrayscaleImage.Source = _restartLaunchTileGrayscaleSource ?? _restartLaunchTileNormalSource;
        }

        UpdateStopLaunchTileAvailability(LaunchTileVisualState.NotRunning, animate: false);
        UpdateRestartLaunchTileAvailability(LaunchTileVisualState.NotRunning, animate: false);
        UpdateConsoleActionTilesAvailability(LaunchTileVisualState.NotRunning, animate: false);
    }

    private bool CanUseStartLaunchTile()
    {
        return !_isRepairingNodeModules && !IsNodeModulesOverlayVisible();
    }

    private void UpdateStartLaunchTileAvailability()
    {
        if (LaunchTile02 is null)
            return;

        bool canStart = CanUseStartLaunchTile();
        LaunchTile02.IsEnabled = canStart;
        LaunchTile02.IsHitTestVisible = canStart;
        LaunchTile02.Cursor = canStart ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;
        LaunchTile02.ToolTip = canStart ? null : "正在处理运行依赖，请稍候";

        // node_modules 修复 / 遮罩状态会改变开始运行的可用性，控制台同名键同步刷新（与面板同源）。
        UpdateConsoleActionTilesAvailability(_launchTileVisualState, animate: false);
    }

    private bool CanUseStopLaunchTile()
    {
        return LaunchTile03.IsEnabled && LaunchTile03.IsHitTestVisible;
    }

    private void UpdateStopLaunchTileAvailability(LaunchTileVisualState visualState, bool animate = true)
    {
        bool canStop = visualState is LaunchTileVisualState.Starting or LaunchTileVisualState.Running;

        LaunchTile03.IsEnabled = canStop;
        LaunchTile03.IsHitTestVisible = canStop;
        LaunchTile03.Cursor = canStop ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;
        LaunchTile03.ToolTip = null;

        if (_stopLaunchTileImage is null)
            return;

        if (_stopLaunchTileGrayscaleImage is null)
        {
            _stopLaunchTileImage.Source = canStop
                ? _stopLaunchTileNormalSource ?? _stopLaunchTileImage.Source
                : _stopLaunchTileGrayscaleSource ?? _stopLaunchTileNormalSource ?? _stopLaunchTileImage.Source;
            SetLaunchTileAvailabilityOpacity(_stopLaunchTileImage, canStop ? 1.0 : 0.7, animate);
            return;
        }

        _stopLaunchTileImage.Source = _stopLaunchTileNormalSource ?? _stopLaunchTileImage.Source;
        _stopLaunchTileGrayscaleImage.Source = _stopLaunchTileGrayscaleSource ?? _stopLaunchTileNormalSource ?? _stopLaunchTileImage.Source;
        SetLaunchTileAvailabilityOpacity(_stopLaunchTileImage, canStop ? 1.0 : 0.0, animate);
        SetLaunchTileAvailabilityOpacity(_stopLaunchTileGrayscaleImage, canStop ? 0.0 : 0.7, animate);
    }

    private static void SetLaunchTileAvailabilityOpacity(UIElement element, double opacity, bool animate)
    {
        if (!animate)
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = opacity;
            return;
        }

        element.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation
            {
                To = opacity,
                Duration = LaunchTileAvailabilityTransitionDuration
            });
    }

    private bool CanUseRestartLaunchTile()
    {
        return LaunchTile04.IsEnabled && LaunchTile04.IsHitTestVisible;
    }

    private void UpdateRestartLaunchTileAvailability(LaunchTileVisualState visualState, bool animate = true)
    {
        bool canRestart = visualState is LaunchTileVisualState.Starting or LaunchTileVisualState.Running;

        LaunchTile04.IsEnabled = canRestart;
        LaunchTile04.IsHitTestVisible = canRestart;
        LaunchTile04.Cursor = canRestart ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;
        LaunchTile04.ToolTip = null;

        if (_restartLaunchTileImage is null)
            return;

        if (_restartLaunchTileGrayscaleImage is null)
        {
            _restartLaunchTileImage.Source = canRestart
                ? _restartLaunchTileNormalSource ?? _restartLaunchTileImage.Source
                : _restartLaunchTileGrayscaleSource ?? _restartLaunchTileNormalSource ?? _restartLaunchTileImage.Source;
            SetLaunchTileAvailabilityOpacity(_restartLaunchTileImage, canRestart ? 1.0 : 0.7, animate);
            return;
        }

        _restartLaunchTileImage.Source = _restartLaunchTileNormalSource ?? _restartLaunchTileImage.Source;
        _restartLaunchTileGrayscaleImage.Source = _restartLaunchTileGrayscaleSource ?? _restartLaunchTileNormalSource ?? _restartLaunchTileImage.Source;
        SetLaunchTileAvailabilityOpacity(_restartLaunchTileImage, canRestart ? 1.0 : 0.0, animate);
        SetLaunchTileAvailabilityOpacity(_restartLaunchTileGrayscaleImage, canRestart ? 0.0 : 0.7, animate);
    }

    /// <summary>
    /// 控制台页右下角三个 n8n 控制键的启用态，与启动面板磁贴 / 托盘菜单保持同一套关系：
    /// 同一时刻只有一组动作可点 —— 未运行稳态只有「开始运行」亮；
    /// Starting / Running 只有「停止运行 / 重新启动」亮；中止中 / 修复期三键全灰。
    /// 命名坑：本组按钮的 x:Name 与 handler 编号交叉错位 ——
    ///   ConsoleActionTile03 = 重新启动（handler 却叫 Tile04）、
    ///   ConsoleActionTile04 = 停止运行（handler 却叫 Tile03）、
    ///   ConsoleActionTile05 = 开始运行（内外一致）。
    ///   本方法一律按 x:Name 认按钮，改这里时不要被 handler 编号带偏。
    /// </summary>
    private void UpdateConsoleActionTilesAvailability(LaunchTileVisualState visualState, bool animate = true)
    {
        if (ConsoleActionTile03 is null || ConsoleActionTile04 is null || ConsoleActionTile05 is null)
            return;

        bool canStopOrRestart = visualState is LaunchTileVisualState.Starting or LaunchTileVisualState.Running;
        bool canStart = visualState is LaunchTileVisualState.NotRunning && CanUseStartLaunchTile();

        ApplyConsoleTileAvailability(ConsoleActionTile03, canStopOrRestart, animate); // 重新启动
        ApplyConsoleTileAvailability(ConsoleActionTile04, canStopOrRestart, animate); // 停止运行
        ApplyConsoleTileAvailability(ConsoleActionTile05, canStart, animate);          // 开始运行
    }

    /// <summary>
    /// 单块控制台操作键切到启用/禁用态：只淡出内容层（外层 Grid），
    /// 底板与文字配色完全不动，因此 UpdateThemeColors() 无需同步改动。
    /// </summary>
    private void ApplyConsoleTileAvailability(Border tile, bool enabled, bool animate)
    {
        tile.IsEnabled = enabled;
        tile.IsHitTestVisible = enabled;   // 禁用后 IsMouseOver 恒为 false，悬浮内描边自动不再出现
        tile.Cursor = enabled ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;

        if (tile.Child is UIElement content)
            SetLaunchTileAvailabilityOpacity(content, enabled ? 1.0 : ConsoleTileDisabledContentOpacity, animate);
    }

    private static ImageSource? CreateGrayscaleImageSource(ImageSource? source)
    {
        if (source is not BitmapSource bitmapSource)
            return source;

        // 不使用 Gray8：Gray8 会丢失 PNG 透明通道，透明/半透明边缘会被当成不透明黑色像素显示，产生黑点噪点。
        // 这里转成 BGRA 后只灰度化 RGB，保留 Alpha，禁用态边缘会保持原图抗锯齿透明度。
        var bgraSource = new FormatConvertedBitmap(bitmapSource, PixelFormats.Bgra32, null, 0);
        int width = bgraSource.PixelWidth;
        int height = bgraSource.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        bgraSource.CopyPixels(pixels, stride, 0);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte blue = pixels[i];
            byte green = pixels[i + 1];
            byte red = pixels[i + 2];
            byte gray = (byte)Math.Clamp((red * 0.299) + (green * 0.587) + (blue * 0.114), 0, 255);

            pixels[i] = gray;
            pixels[i + 1] = gray;
            pixels[i + 2] = gray;
        }

        var grayscaleSource = BitmapSource.Create(
            width,
            height,
            bgraSource.DpiX,
            bgraSource.DpiY,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        if (grayscaleSource.CanFreeze)
            grayscaleSource.Freeze();
        return grayscaleSource;
    }

    /// <summary>
    /// 将 <paramref name="consoleStatus"/> 文案映射为画板按钮视觉态。
    /// 启动中/重启中/修复中 → Starting；运行中 → Running；中止中 → Stopping；
    /// 已停止/未运行/启动失败 → NotRunning。
    /// </summary>
    private static LaunchTileVisualState MapConsoleStatusToVisualState(string consoleStatus)
    {
        if (consoleStatus.Contains("启动中") || consoleStatus.Contains("重启中") || consoleStatus.Contains("修复中"))
            return LaunchTileVisualState.Starting;

        if (consoleStatus.Contains("运行中"))
            return LaunchTileVisualState.Running;

        if (consoleStatus.Contains("中止中"))
            return LaunchTileVisualState.Stopping;

        // 已停止 / 未运行 / 启动失败 等归入未启动稳态。
        return LaunchTileVisualState.NotRunning;
    }

    private bool IsN8nManagedProcessRunning()
    {
        return _n8nProcess is { HasExited: false };
    }

    private void UpdateTrayTooltip()
    {
        if (_winFormsTray == null)
            return;

        _winFormsTray.Text = IsN8nManagedProcessRunning() ? "n8n运行中" : "n8n_launcher_Gv";
    }

    private async Task OpenN8nWebFromTrayAsync()
    {
        if (IsN8nManagedProcessRunning())
        {
            OpenUrl(N8nLocalWorkflowsUrl);
            return;
        }

        await StartN8nAsync();
    }

    private string GetConsoleLogText()
    {
        string logText = _consoleOutputBuilder.ToString();
        if (string.IsNullOrWhiteSpace(logText))
            logText = ConsoleOutputTextBox.Text ?? string.Empty;
        return logText;
    }

    private async Task CopyConsoleLogToClipboardAsync()
    {
        if (_isCopyingConsoleLog)
        {
            AppendConsoleOutput("[INFO] 控制台日志正在复制中，请稍候...");
            return;
        }

        string logText = GetConsoleLogText();
        if (string.IsNullOrWhiteSpace(logText))
        {
            AppendConsoleOutput("[INFO] 当前控制台日志为空，没有可复制的内容。");
            return;
        }

        _isCopyingConsoleLog = true;

        try
        {
            try
            {
                // 优先模拟用户手动操作：聚焦控制台文本框、全选、执行控件内置复制。
                // 成功后再追加提示，避免“正在复制...”这类新日志被一起复制进去。
                ConsoleOutputTextBox.Focus();
                ConsoleOutputTextBox.SelectAll();
                ConsoleOutputTextBox.Copy();
                AppendConsoleOutput("[INFO] 控制台日志已复制到剪贴板。");
                return;
            }
            catch (Exception controlCopyException)
            {
                Debug.WriteLine($"[Clipboard] TextBox.Copy fallback: {controlCopyException.Message}");
                AppendConsoleOutput("[INFO] 控件复制未成功，正在使用兼容复制方式...");
            }

            await SetClipboardTextWithRetryAsync(logText, new WindowInteropHelper(this).Handle);
            AppendConsoleOutput("[INFO] 控制台日志已复制到剪贴板。");
        }
        catch (Exception ex)
        {
            AppendConsoleOutput($"[ERROR] 复制控制台日志失败：{ex.Message}");
            System.Windows.MessageBox.Show(this, $"复制控制台日志失败：{ex.Message}", "复制失败", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        finally
        {
            _isCopyingConsoleLog = false;
        }
    }

    private static Task SetClipboardTextWithRetryAsync(string text, IntPtr ownerHandle)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                SetClipboardTextWithRetry(text, ownerHandle);
                completion.SetResult(null);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });

        thread.IsBackground = true;
        thread.Name = "n8n Launcher Clipboard Copy";
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();

        return completion.Task;
    }

    private static void SetClipboardTextWithRetry(string text, IntPtr ownerHandle)
    {
        const int maxAttempts = 20;
        Exception? lastException = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                // 优先使用 WPF 剪贴板路径；如果旧系统/旧机器上 OLE 剪贴板仍报 OpenClipboard，下一步走 Win32 fallback。
                System.Windows.Clipboard.SetText(text, System.Windows.TextDataFormat.UnicodeText);
                return;
            }
            catch (Exception ex)
            {
                lastException = ex;
            }

            try
            {
                SetClipboardTextNative(text, ownerHandle);
                return;
            }
            catch (Exception ex)
            {
                lastException = ex;
            }

            System.Threading.Thread.Sleep(Math.Min(1000, 80 + attempt * 70));
        }

        string ownerText = GetClipboardOwnerDescription();
        string message = lastException?.Message ?? "写入剪贴板失败。";
        throw new InvalidOperationException($"{message} {ownerText}", lastException);
    }

    private static void SetClipboardTextNative(string text, IntPtr ownerHandle)
    {
        if (!OpenClipboard(ownerHandle))
            throw CreateWin32ClipboardException("OpenClipboard 失败");

        IntPtr globalMemory = IntPtr.Zero;
        bool setClipboardDataSucceeded = false;

        try
        {
            if (!EmptyClipboard())
                throw CreateWin32ClipboardException("EmptyClipboard 失败");

            byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
            globalMemory = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
            if (globalMemory == IntPtr.Zero)
                throw CreateWin32ClipboardException("GlobalAlloc 失败");

            IntPtr lockedMemory = GlobalLock(globalMemory);
            if (lockedMemory == IntPtr.Zero)
                throw CreateWin32ClipboardException("GlobalLock 失败");

            try
            {
                Marshal.Copy(bytes, 0, lockedMemory, bytes.Length);
            }
            finally
            {
                GlobalUnlock(globalMemory);
            }

            if (SetClipboardData(CF_UNICODETEXT, globalMemory) == IntPtr.Zero)
                throw CreateWin32ClipboardException("SetClipboardData 失败");

            setClipboardDataSucceeded = true;
        }
        finally
        {
            CloseClipboard();
            if (!setClipboardDataSucceeded && globalMemory != IntPtr.Zero)
                GlobalFree(globalMemory);
        }
    }

    private static Exception CreateWin32ClipboardException(string operation)
    {
        int errorCode = Marshal.GetLastWin32Error();
        return new System.ComponentModel.Win32Exception(errorCode, $"{operation} ({errorCode})");
    }

    private static string GetClipboardOwnerDescription()
    {
        IntPtr ownerWindow = GetOpenClipboardWindow();
        if (ownerWindow == IntPtr.Zero)
            return "当前没有检测到保持打开剪贴板的窗口；可能是剪贴板服务、剪贴板历史/同步、远程桌面或安全软件短暂拦截。";

        GetWindowThreadProcessId(ownerWindow, out uint processId);
        if (processId == 0)
            return $"当前占用剪贴板的窗口句柄：0x{ownerWindow.ToInt64():X}。";

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return $"当前占用剪贴板的进程：{process.ProcessName} (PID {processId})。";
        }
        catch
        {
            return $"当前占用剪贴板的进程 PID：{processId}。";
        }
    }

    private void ExportConsoleLogToTextFile()
    {
        string logText = GetConsoleLogText();
        if (string.IsNullOrWhiteSpace(logText))
        {
            AppendConsoleOutput("[INFO] 当前控制台日志为空，没有可导出的内容。");
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出控制台日志",
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            DefaultExt = ".txt",
            AddExtension = true,
            FileName = $"n8n-console-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
        };

        bool? result = dialog.ShowDialog(this);
        if (result != true || string.IsNullOrWhiteSpace(dialog.FileName))
            return;

        try
        {
            File.WriteAllText(dialog.FileName, logText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            AppendConsoleOutput($"[INFO] 控制台日志已导出：{dialog.FileName}");
        }
        catch (Exception ex)
        {
            AppendConsoleOutput($"[ERROR] 导出控制台日志失败：{ex.Message}");
            System.Windows.MessageBox.Show(this, $"导出控制台日志失败：{ex.Message}", "导出失败", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private async Task StopN8nProcessAsync(bool showNoProcessMessage, bool showErrors = true)
    {
        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(() => StopN8nProcessAsync(showNoProcessMessage, showErrors)).Task.Unwrap();
            return;
        }

        if (_isStoppingN8n)
        {
            if (showNoProcessMessage)
                AppendConsoleOutput("[INFO] n8n 正在中止中，请稍候；当前中止请求已忽略。");
            return;
        }

        _isStartingN8n = false;
        System.Threading.Interlocked.Increment(ref _n8nStartGeneration);
        Process? process = _n8nProcess;

        if (process is null)
        {
            StopLaunchRocketPreviewAnimation();
            if (!_isExiting)
                SetN8nRunningStatus("未运行 (Not Running)", "启动 n8n", "Launch n8n");
            if (showNoProcessMessage)
                AppendConsoleOutput("[INFO] 当前没有由启动器托管的 n8n 进程。关闭窗口按钮只会隐藏到托盘；托盘“退出”会执行中止流程。");
            return;
        }

        try
        {
            if (process.HasExited)
            {
                process.Dispose();
                if (ReferenceEquals(_n8nProcess, process))
                    _n8nProcess = null;
                _isStoppingN8n = false;
                StopLaunchRocketPreviewAnimation();
                if (!_isExiting)
                    SetN8nRunningStatus("已停止 (Stopped)", "启动 n8n", "Stopped");
                if (showNoProcessMessage)
                    AppendConsoleOutput("[INFO] n8n 进程已经退出。已清理启动器内的进程引用。");
                return;
            }

            _isStoppingN8n = true;
            // 关键：真正要 kill 一个仍在运行的进程时，把持久停止标志置 true，
            // 让稍后 taskkill 触发的 Process.Exited 回调知道这是用户主动停止（而不是崩溃/自然失败）。
            // 该标志会在 Exited 回调中消费后置回 false，保证下一次启动不受污染。
            _userStopRequestedExit = true;
            _n8nProcess = null;
            if (!_isExiting)
                SetN8nRunningStatus("中止中 (Stopping)", "正在中止", "Stopping n8n...");
            AppendConsoleOutput("[INFO] 正在异步中止 n8n 进程树...界面会保持可操作，不会等待进程退出而卡住。");

            bool exited = await Task.Run(() =>
            {
                process.Kill(entireProcessTree: true);
                return process.WaitForExit(5000);
            });

            if (!exited)
                AppendConsoleOutput("[WARN] 已发送中止请求，但进程树未在 5 秒内完全退出。Windows 可能仍在清理子进程。启动器不会因此阻塞。");

            process.Dispose();
            _isStoppingN8n = false;
            StopLaunchRocketPreviewAnimation();
            if (!_isExiting)
                SetN8nRunningStatus("已停止 (Stopped)", "启动 n8n", "Stopped");
            AppendConsoleOutput("[INFO] n8n 已中止。所有由启动器托管的 n8n 子进程树已请求结束。");
        }
        catch (Exception ex)
        {
            _isStoppingN8n = false;
            if (ReferenceEquals(_n8nProcess, process))
                _n8nProcess = null;
            AppendConsoleOutput($"[ERROR] 中止 n8n 失败：{ex.Message}");
            if (showErrors)
                System.Windows.MessageBox.Show(this, $"中止 n8n 失败：{ex.Message}", "中止失败", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private void StopLaunchRocketPreviewAnimation()
    {
        _launchRocketPreviewStoryboard?.Stop();
        _launchRocketPreviewStoryboard = null;
    }

    /// <summary>
    /// 启动 LaunchTile02 的 360 度旋转渐变描边动画（6 秒/圈，无限循环）。
    /// 由窗口 Loaded 事件调用，驱动 ConicGradientBrush.Angle 属性旋转。
    /// </summary>
    private void StartLaunchTile02GradientRotation()
    {
        if (FindName("LaunchTile02GradientRotation") is not RotateTransform rotateTransform)
            return;

        // 低性能模式（设置页-个性化-精简效果=启用）：
        // 不启动旋转动画，把 Angle 复位到 0°，让三个环的 PNG 停在原始设计朝向。
        // Tile03/04 的 RotateTransform.Angle 通过 Binding 绑定到本 Transform，会跟着变成 0°，三环静止一致。
        if (IsLowPerformanceModeEnabled())
        {
            rotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);
            rotateTransform.Angle = 0;
            return;
        }

        // 正常模式：6 秒/圈无限旋转。
        var rotateAnimation = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromSeconds(6)),
            RepeatBehavior = RepeatBehavior.Forever
        };

        rotateTransform.BeginAnimation(RotateTransform.AngleProperty, rotateAnimation);
    }

    /// <summary>
    /// 启动页"正在启动"态的文案切换。
    /// </summary>
    /// <remarks>
    /// 历史沿革：原本此处用 Storyboard 逐帧驱动火箭动画，后已整体迁移到
    /// RocketArt.xaml / FireArt.xaml 等资源字典中由 XAML 声明式播放，
    /// 故本方法只保留文案切换职责，方法名沿用旧称未改以免影响调用点。
    /// </remarks>
    private void StartLaunchRocketPreviewAnimation()
    {
        _launchRocketPreviewStoryboard?.Stop();
        _launchRocketPreviewStoryboard = null;

        LaunchTile02StatusText.Text = "Starting preview...";
        LaunchTile02TitleText.Text = "正在启动";
    }

    // 星星装饰规则：
    // - 同屏维持 5~10 颗星星
    // - 仅出现在左右各三分之一区域，且距离边缘 50px 以上（优先级最高）
    // - 任意两颗星星中心间距不少于 70px
    // - 不旋转；淡入/淡出各 1 秒，停留 3~10 秒随机
    // 设计稿为 PS px（1x=2160×1440），WPF 设计面为 DIP（1440×960），换算系数 1.5。
    // 所有以 PS px 描述的尺寸都需乘以该系数转换为 DIP。
    private const double DesignPxToDip = 1.0 / 1.5;

    private const int LaunchStarMinCount = 5;
    private const int LaunchStarMaxCount = 10;
    private const double LaunchStarEdgeMargin = 30 * DesignPxToDip;   // PS 30px（边缘排除）
    private const double LaunchStarMinSpacing = 70 * DesignPxToDip;   // PS 70px
    private const double LaunchStarPlanetExclusionRadius = 12 * DesignPxToDip;  // PS 12px（星球额外排除范围）

    // 三个星球固定位置和半径（DIP 单位，从 MainWindow.xaml 读取）
    private static readonly (double X, double Y, double Radius)[] LaunchPlanetPositions = new[]
    {
        (389.33, 77.34, 60.0),    // Planet1（带星环）：中心 (389.33, 77.34)，半径 60
        (85.34, 110.67, 26.67),   // Planet2：中心 (85.34, 110.67)，半径 26.67
        (170.67, 44.0, 16.67)     // Planet3：中心 (170.67, 44)，半径 16.67
    };

    private void StartLaunchStarDecoration()
    {
        _launchStarTimer.Stop();
        _launchStarTimer.Tick -= LaunchStarTimer_Tick;
        _launchStarTimer.Tick += LaunchStarTimer_Tick;
        EnsureLaunchStarPopulation();
        ScheduleNextLaunchStar();
        _launchStarTimer.Start();
    }

    private void StopLaunchStarDecoration(bool clearExistingStars)
    {
        _launchStarTimer.Stop();
        _launchStarTimer.Tick -= LaunchStarTimer_Tick;

        if (clearExistingStars && FindName("LaunchStarLayer") is Canvas launchStarLayer)
        {
            launchStarLayer.Children.Clear();
        }
    }

    private void LaunchStarTimer_Tick(object? sender, EventArgs e)
    {
        EnsureLaunchStarPopulation();
        ScheduleNextLaunchStar();
    }

    private void ScheduleNextLaunchStar()
    {
        _launchStarTimer.Interval = TimeSpan.FromMilliseconds(RandomDouble(500, 2000));  // 0.5~2 秒随机
    }

    // 维持同屏星星数量在 5~10 颗之间：每次定时器触发只生成 1 颗，避免一次性出现多颗
    private void EnsureLaunchStarPopulation()
    {
        if (FindName("LaunchStarLayer") is not Canvas launchStarLayer)
        {
            return;
        }

        int currentCount = launchStarLayer.Children.Count;
        if (currentCount >= LaunchStarMaxCount)
        {
            return;
        }

        // 每次只生成 1 颗，让星星逐个出现
        SpawnLaunchStar(launchStarLayer);
    }

    /// <summary>
    /// 当前是否深色外观（配置 ThemeMode：Dark=是 / Light=否 / System=跟随系统）。
    /// 与 <see cref="UpdateThemeColors"/> 共用同一判定，避免两处逻辑走偏。
    /// </summary>
    private bool IsCurrentThemeDark()
    {
        // 局部初值仅在 LoadConfig 抛异常时生效；与配置默认值保持一致取 "Light"。
        string themeMode = "Light";
        try
        {
            themeMode = NormalizeThemeMode(LoadConfig().ThemeMode);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Theme] Load theme mode failed: {ex.Message}");
        }

        return themeMode switch
        {
            "Dark" => true,
            "Light" => false,
            _ => IsSystemDarkMode()
        };
    }

    /// <summary>
    /// 启动页画板「星星 / 流星」装饰色：浅色 #00A2E9（原硬编码值，逐位一致）/ 深色 #FFF100。
    /// ★ 必须在 Spawn 时刻取色，不能用 DynamicResource 主题键：
    ///   星星 / 流星是运行时 new 出来、几秒后销毁的短命元素，Fill 为本地赋值的画刷实例，
    ///   不引用任何资源键，切主题时改资源 Color 对它们无效。在生成点取色即可让切主题后
    ///   「新生成」的元素自动使用新色；切换瞬间已在屏上的旧元素保持旧色自然淡出
    ///   （星星 4~12s / 流星 1.1s 内全部轮换完），刻意不做遍历重刷，避免与正在运行的
    ///   Opacity 动画叠加出突变闪色。
    /// ★ 只换 RGB，不动任何不透明度：流星渐变 3 个 GradientStop 的 α(0x00/0x66/0xD9)
    ///   与 offset(0.0/0.5/1.0)、星星的淡入淡出 Opacity 动画一律保持原值。
    /// </summary>
    private Color GetLaunchDecorationColor()
        => IsCurrentThemeDark()
            ? Color.FromRgb(0xFF, 0xF1, 0x00)   // 深色：亮黄
            : Color.FromRgb(0x00, 0xA2, 0xE9);  // 浅色：原统一蓝（零回归）

    private void SpawnLaunchStar(Canvas launchStarLayer)
    {
        double layerWidth = launchStarLayer.ActualWidth > 1 ? launchStarLayer.ActualWidth : LaunchTile02.ActualWidth;
        double layerHeight = launchStarLayer.ActualHeight > 1 ? launchStarLayer.ActualHeight : LaunchTile02.ActualHeight;

        if (layerWidth <= 1 || layerHeight <= 1)
        {
            return;
        }

        if (!TryPickLaunchStarPosition(launchStarLayer, layerWidth, layerHeight, out double centerX, out double centerY))
        {
            return;
        }

        double size = RandomDouble(12 * DesignPxToDip, 30 * DesignPxToDip);   // PS 12~30px（宽度）
        double height = size * 1.25;  // 高度按 SVG 比例 1:1.25（viewBox 8.24×10.3）
        double left = centerX - size / 2;
        double top = centerY - height / 2;
        Color starColor = GetLaunchDecorationColor();  // 浅色统一蓝 / 深色亮黄

        var star = new WpfPath
        {
            Width = size,
            Height = height,
            Data = Geometry.Parse("M8.24 5.15c-2.04,-0.03 -3.74,-2.23 -4.12,-5.15 -0.38,2.92 -2.08,5.12 -4.12,5.15 2.04,0.03 3.74,2.23 4.12,5.15 0.38,-2.92 2.08,-5.12 4.12,-5.15z"),
            Fill = new SolidColorBrush(starColor),
            Stretch = Stretch.Fill,
            Opacity = 0,
            IsHitTestVisible = false,
            // 用 Tag 记录星星中心点，供 70px 间距判定使用
            Tag = new WpfPoint(centerX, centerY)
        };

        Canvas.SetLeft(star, left);
        Canvas.SetTop(star, top);
        launchStarLayer.Children.Add(star);

        double visibleSeconds = RandomDouble(3, 10);
        var fadeAnimation = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames();
        fadeAnimation.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fadeAnimation.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1))));
        fadeAnimation.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1 + visibleSeconds))));
        fadeAnimation.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2 + visibleSeconds))));

        var storyboard = new Storyboard();
        Storyboard.SetTarget(fadeAnimation, star);
        Storyboard.SetTargetProperty(fadeAnimation, new PropertyPath(OpacityProperty));
        storyboard.Children.Add(fadeAnimation);
        storyboard.Completed += (_, _) => launchStarLayer.Children.Remove(star);
        storyboard.Begin();
    }

    // 在左/右各三分之一区域内、距边缘 50px 以上、且与现有星星中心间距≥70px 处随机选点
    private bool TryPickLaunchStarPosition(Canvas launchStarLayer, double layerWidth, double layerHeight, out double centerX, out double centerY)
    {
        centerX = 0;
        centerY = 0;

        // 横向全范围，纵向上 75%，边缘 30px 排除
        double xMin = LaunchStarEdgeMargin;
        double xMax = layerWidth - LaunchStarEdgeMargin;
        double yMin = LaunchStarEdgeMargin;
        double yMax = layerHeight * 0.75 - LaunchStarEdgeMargin;

        if (xMax <= xMin || yMax <= yMin)
        {
            return false;
        }

        const int maxAttempts = 30;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            double x = RandomDouble(xMin, xMax);
            double y = RandomDouble(yMin, yMax);

            // 排除三个星球周围 12px 范围
            if (IsNearPlanet(x, y))
                continue;

            if (IsLaunchStarSpacingOk(launchStarLayer, x, y))
            {
                centerX = x;
                centerY = y;
                return true;
            }
        }

        return false;
    }

    // 判定星星中心是否在任意星球周围（星球半径 + 12px 排除范围）
    private static bool IsNearPlanet(double x, double y)
    {
        foreach (var (px, py, radius) in LaunchPlanetPositions)
        {
            double dx = x - px;
            double dy = y - py;
            double distanceSquared = dx * dx + dy * dy;
            double exclusionDistance = radius + LaunchStarPlanetExclusionRadius;
            if (distanceSquared < exclusionDistance * exclusionDistance)
                return true;
        }
        return false;
    }

    private static bool IsLaunchStarSpacingOk(Canvas launchStarLayer, double x, double y)
    {
        double minSpacingSquared = LaunchStarMinSpacing * LaunchStarMinSpacing;
        foreach (var child in launchStarLayer.Children)
        {
            if (child is WpfPath existing && existing.Tag is WpfPoint center)
            {
                double dx = center.X - x;
                double dy = center.Y - y;
                if (dx * dx + dy * dy < minSpacingSquared)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private double RandomDouble(double minInclusive, double maxExclusive)
    {
        return minInclusive + _launchStarRandom.NextDouble() * (maxExclusive - minInclusive);
    }

    // 流星装饰规则：
    // - 矢量：青蓝(#00A2E9)胶囊（圆角矩形），横向渐变亮头→尾部 α0.85 → 0.4 → 0，亮头在运动前方。
    //   矢量 +X 端为亮头（offset 1.0），与运动方向（旋转后 +X 朝前）对齐。
    // - 起点：在「顶边右 5/6」(A) 与「右边上 3/4」(B) 连成的线段上随机取点。
    // - 运动：右上 → 左下，滑行角 15~36° 随机；旋转角与滑行方向一致（亮头朝前）。
    //   WPF Y 轴向下，右上→左下 = X 减小、Y 增大，方向向量 = (-cosθ, +sinθ)。
    // - 节奏：单条 0.8s 划过；平均每 2~4s 最多发 1 条；同屏最多 3 条；划过后回收。
    // 设计稿为 PS px（1x=2160×1440），换算系数 1.5（DesignPxToDip = 1/1.5）。
    private const double LaunchMeteorLength = 500.0 / 6 * DesignPxToDip;        // PS 500px 的 1/6 → 约 55.5 DIP（缩小 50%）
    private const double LaunchMeteorThickness = 500.0 / 6 / 17.5 * DesignPxToDip; // 17.5:1 长宽比 → 约 3.15 DIP（缩小 50%）
    private const double LaunchMeteorMinAngleDeg = 15;
    private const double LaunchMeteorMaxAngleDeg = 36;
    private const double LaunchMeteorTravelDistance = 800;              // 终点沿运动方向延伸的距离（DIP），确保完全划出后回收
    private const int LaunchMeteorDurationMs = 1143;                   // 单条划过画板用时（800 ÷ 0.7 ≈ 1143ms，速度降为 70%）
    private const int LaunchMeteorMaxCount = 3;                        // 同屏上限

    private void StartLaunchMeteorDecoration()
    {
        _launchMeteorTimer.Stop();
        _launchMeteorTimer.Tick -= LaunchMeteorTimer_Tick;
        _launchMeteorTimer.Tick += LaunchMeteorTimer_Tick;
        ScheduleNextLaunchMeteor();
        _launchMeteorTimer.Start();
    }

    private void StopLaunchMeteorDecoration(bool clearExistingMeteors)
    {
        _launchMeteorTimer.Stop();
        _launchMeteorTimer.Tick -= LaunchMeteorTimer_Tick;

        if (clearExistingMeteors && FindName("LaunchMeteorLayer") is Canvas launchMeteorLayer)
        {
            launchMeteorLayer.Children.Clear();
        }
    }

    private void LaunchMeteorTimer_Tick(object? sender, EventArgs e)
    {
        TrySpawnLaunchMeteor();
        ScheduleNextLaunchMeteor();
    }

    private void ScheduleNextLaunchMeteor()
    {
        // 平均每 2~4 秒最多发射 1 条
        _launchMeteorTimer.Interval = TimeSpan.FromMilliseconds(RandomDouble(2000, 4000));
    }

    private void TrySpawnLaunchMeteor()
    {
        if (FindName("LaunchMeteorLayer") is not Canvas launchMeteorLayer)
        {
            return;
        }

        if (launchMeteorLayer.Children.Count >= LaunchMeteorMaxCount)
        {
            return;
        }

        SpawnLaunchMeteor(launchMeteorLayer);
    }

    private void SpawnLaunchMeteor(Canvas launchMeteorLayer)
    {
        double layerWidth = launchMeteorLayer.ActualWidth > 1 ? launchMeteorLayer.ActualWidth : LaunchTile02.ActualWidth;
        double layerHeight = launchMeteorLayer.ActualHeight > 1 ? launchMeteorLayer.ActualHeight : LaunchTile02.ActualHeight;

        if (layerWidth <= 1 || layerHeight <= 1)
        {
            return;
        }

        // 起点：A=(W*5/6, 0)（顶边右 5/6），B=(W, H*3/4)（右边上 3/4），在 AB 线段上随机取点。
        double ax = layerWidth * 5.0 / 6.0;
        double ay = 0;
        double bx = layerWidth;
        double by = layerHeight * 3.0 / 4.0;
        double t = _launchStarRandom.NextDouble();
        double startX = ax + t * (bx - ax);
        double startY = ay + t * (by - ay);

        // 滑行角 30~60° 随机；方向向量（右上→左下）= (-cosθ, +sinθ)。
        double angleDeg = RandomDouble(LaunchMeteorMinAngleDeg, LaunchMeteorMaxAngleDeg);
        double angleRad = angleDeg * Math.PI / 180.0;
        double dirX = -Math.Cos(angleRad);
        double dirY = Math.Sin(angleRad);

        // 终点位移：沿运动方向延伸足够距离，确保完全划出画板后回收。
        double deltaX = dirX * LaunchMeteorTravelDistance;
        double deltaY = dirY * LaunchMeteorTravelDistance;

        // 旋转角：矢量默认亮头朝右(+X，0°)，运动方向角 = atan2(dirY, dirX)，使亮头朝运动前方。
        double rotateDeg = Math.Atan2(dirY, dirX) * 180.0 / Math.PI;

        // 胶囊矢量 + 横向渐变（尾部 α0 → 中段 α0.4 → +X 端亮头 α0.85）。
        // 旋转后矢量 +X 端朝运动方向前方，故亮头放在 offset 1.0，确保亮头在前。
        // 颜色随主题（浅色统一蓝 / 深色亮黄），3 个 α 值与 offset 不动。
        var meteorColor = GetLaunchDecorationColor();
        var gradient = new System.Windows.Media.LinearGradientBrush
        {
            StartPoint = new WpfPoint(0, 0.5),
            EndPoint = new WpfPoint(1, 0.5)
        };
        gradient.GradientStops.Add(new System.Windows.Media.GradientStop(Color.FromArgb(0x00, meteorColor.R, meteorColor.G, meteorColor.B), 0.0));   // 尾部 α=0
        gradient.GradientStops.Add(new System.Windows.Media.GradientStop(Color.FromArgb(0x66, meteorColor.R, meteorColor.G, meteorColor.B), 0.5));   // 中段 α≈0.4
        gradient.GradientStops.Add(new System.Windows.Media.GradientStop(Color.FromArgb(0xD9, meteorColor.R, meteorColor.G, meteorColor.B), 1.0));   // 亮头 α≈0.85

        var meteor = new System.Windows.Shapes.Rectangle
        {
            Width = LaunchMeteorLength,
            Height = LaunchMeteorThickness,
            RadiusX = LaunchMeteorThickness / 2,
            RadiusY = LaunchMeteorThickness / 2,
            Fill = gradient,
            IsHitTestVisible = false
        };

        // 以流星中心为旋转/定位基准：先用 RotateTransform 让长轴对齐运动方向，再用 TranslateTransform 做位移动画。
        var rotateTransform = new RotateTransform(rotateDeg, LaunchMeteorLength / 2, LaunchMeteorThickness / 2);
        var translateTransform = new TranslateTransform(0, 0);
        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(rotateTransform);
        transformGroup.Children.Add(translateTransform);
        meteor.RenderTransform = transformGroup;

        // 起点：让流星中心落在 (startX, startY)。Canvas.Left/Top 定位左上角，再由旋转绕中心生效。
        Canvas.SetLeft(meteor, startX - LaunchMeteorLength / 2);
        Canvas.SetTop(meteor, startY - LaunchMeteorThickness / 2);
        launchMeteorLayer.Children.Add(meteor);

        var duration = TimeSpan.FromMilliseconds(LaunchMeteorDurationMs);
        var moveX = new DoubleAnimation
        {
            From = 0,
            To = deltaX,
            Duration = duration
        };
        var moveY = new DoubleAnimation
        {
            From = 0,
            To = deltaY,
            Duration = duration
        };

        var storyboard = new Storyboard();
        Storyboard.SetTarget(moveX, meteor);
        Storyboard.SetTargetProperty(moveX, new PropertyPath("RenderTransform.Children[1].X"));
        Storyboard.SetTarget(moveY, meteor);
        Storyboard.SetTargetProperty(moveY, new PropertyPath("RenderTransform.Children[1].Y"));
        storyboard.Children.Add(moveX);
        storyboard.Children.Add(moveY);
        storyboard.Completed += (_, _) => launchMeteorLayer.Children.Remove(meteor);
        storyboard.Begin();
    }
private void LaunchTile07_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => OpenUrl("https://n8n.io/workflows/");

    /// <summary>
    /// 关于页 5 个网站快捷方式按钮的统一点击入口。
    /// 从 sender.Tag 读取目标 URL，通过 OpenUrl 使用"用户配置浏览器"打开
    /// （行为与 LaunchTile07 / 概览面板 URL 完全一致）。
    /// </summary>
    private void AboutSiteButton_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string url && !string.IsNullOrWhiteSpace(url))
        {
            OpenUrl(url);
        }
    }


    private void LaunchTile10_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => OpenPortableFolder(".");

    private void LaunchTile11_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => OpenPortableFolder("runtime");

    private void LaunchTile12_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => OpenPortableFolder("app");

    private void LaunchTile13_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => OpenPortableFolder("data");

    private void NodeModulesRing_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!CanUseNodeModulesRepairTile())
        {
            ShowNodeModulesRingNormalIcon();
            return;
        }

        NodeModulesHealthStatusIcon.Visibility = Visibility.Collapsed;
        NodeModulesRepairIcon.Visibility = Visibility.Visible;
    }

    private void NodeModulesRing_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        ShowNodeModulesRingNormalIcon();
    }

    private bool IsNodeModulesOverlayVisible()
    {
        return NodeModulesRepairConfirmOverlay.Visibility == Visibility.Visible
               || NodeModulesTarMissingOverlay.Visibility == Visibility.Visible;
    }

    private bool CanUseNodeModulesRepairTile()
    {
        return !_isRepairingNodeModules
               && !IsNodeModulesOverlayVisible()
               && !_isStartingN8n
               && !_isStoppingN8n
               && !_isRestartingN8n
               && !IsN8nManagedProcessRunning();
    }

    private void UpdateNodeModulesRepairAvailability()
    {
        if (FindName("NodeModulesRingGrid") is not FrameworkElement nodeModulesRingGrid)
            return;

        bool canRepair = CanUseNodeModulesRepairTile();
        nodeModulesRingGrid.IsEnabled = canRepair;
        nodeModulesRingGrid.IsHitTestVisible = canRepair;
        nodeModulesRingGrid.Cursor = canRepair ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;
        nodeModulesRingGrid.ToolTip = canRepair ? null : "n8n 启动、运行或重启期间不能修复运行依赖";

        if (!canRepair)
            ShowNodeModulesRingNormalIcon();
    }

    private void ShowNodeModulesRingNormalIcon()
    {
        NodeModulesHealthStatusIcon.Visibility = Visibility.Visible;
        NodeModulesRepairIcon.Visibility = Visibility.Collapsed;
    }

    private async void NodeModulesRing_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!CanUseNodeModulesRepairTile())
            return;

        string? portableRoot = ResolvePortableRoot();
        if (string.IsNullOrWhiteSpace(portableRoot))
        {
            System.Windows.MessageBox.Show(this, "无法定位 n8n 便携包目录。", "修复失败", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        NodeModulesHealthState state = ReadNodeModulesHealth(portableRoot);
        if (!state.NodeModulesTarOk)
        {
            await ShowNodeModulesTarDownloadPromptAsync();
            return;
        }

        if (state.NodeModulesOk)
        {
            ShowNodeModulesRepairConfirmOverlay();
            return;
        }

        await RepairNodeModulesFromTarAsync(portableRoot);
    }

    private async Task ShowNodeModulesTarDownloadPromptAsync()
    {
        if (_isShowingNodeModulesTarMissingPrompt)
            return;

        _isShowingNodeModulesTarMissingPrompt = true;
        NodeModulesTarMissingOverlay.Visibility = Visibility.Visible;
        ShowNodeModulesRingNormalIcon();
        UpdateStartLaunchTileAvailability();
        UpdateNodeModulesRepairAvailability();

        try
        {
            await Task.Delay(3600);
            NodeModulesTarMissingOverlay.Visibility = Visibility.Collapsed;
            OpenUrl(NodeModulesDownloadUrl);
        }
        finally
        {
            _isShowingNodeModulesTarMissingPrompt = false;
            UpdateStartLaunchTileAvailability();
            UpdateNodeModulesRepairAvailability();
        }
    }

    private async void ConfirmRepairNodeModulesButton_Click(object sender, RoutedEventArgs e)
    {
        string? portableRoot = ResolvePortableRoot();
        if (string.IsNullOrWhiteSpace(portableRoot))
        {
            HideNodeModulesRepairConfirmOverlay();
            System.Windows.MessageBox.Show(this, "无法定位 n8n 便携包根目录。", "修复失败", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        await RepairNodeModulesFromTarAsync(portableRoot, showDeletingOverlay: true);
    }

    private void CancelRepairNodeModulesButton_Click(object sender, RoutedEventArgs e) => HideNodeModulesRepairConfirmOverlay();

    private void ShowNodeModulesRepairConfirmOverlay()
    {
        ResetNodeModulesRepairOverlayContent();
        NodeModulesRepairConfirmOverlay.Visibility = Visibility.Visible;
        ShowNodeModulesRingNormalIcon();
        UpdateStartLaunchTileAvailability();
        UpdateNodeModulesRepairAvailability();
    }

    private void ShowNodeModulesRepairDeletingOverlay()
    {
        NodeModulesRepairConfirmOverlayText.FontSize = 24;
        NodeModulesRepairConfirmOverlayText.Width = 150;
        NodeModulesRepairConfirmOverlayText.Margin = new Thickness(0);
        NodeModulesRepairConfirmOverlayText.TextAlignment = TextAlignment.Left;
        NodeModulesRepairConfirmOverlayText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRowSpan(NodeModulesRepairConfirmOverlayText, 2);
        NodeModulesRepairConfirmButtons.Visibility = Visibility.Collapsed;
        NodeModulesRepairConfirmOverlay.Visibility = Visibility.Visible;
        ShowNodeModulesRingNormalIcon();
        UpdateStartLaunchTileAvailability();
        UpdateNodeModulesRepairAvailability();
        StartNodeModulesDeletingTextAnimation();
    }

    private void HideNodeModulesRepairConfirmOverlay()
    {
        NodeModulesRepairConfirmOverlay.Visibility = Visibility.Collapsed;
        ResetNodeModulesRepairOverlayContent();
        UpdateStartLaunchTileAvailability();
        UpdateNodeModulesRepairAvailability();
    }

    private void ResetNodeModulesRepairOverlayContent()
    {
        StopNodeModulesDeletingTextAnimation();
        NodeModulesRepairConfirmOverlayMessageRun.Text = " 运行依赖无异常，是否坚持修复？";
        NodeModulesRepairConfirmOverlayText.FontSize = 17;
        NodeModulesRepairConfirmOverlayText.Width = double.NaN;
        NodeModulesRepairConfirmOverlayText.Margin = new Thickness(0, 28, 0, 0);
        NodeModulesRepairConfirmOverlayText.TextAlignment = TextAlignment.Center;
        NodeModulesRepairConfirmOverlayText.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetRowSpan(NodeModulesRepairConfirmOverlayText, 1);
        NodeModulesRepairConfirmButtons.Visibility = Visibility.Visible;
    }

    private void StartNodeModulesDeletingTextAnimation()
    {
        _nodeModulesDeletingTextDotCount = 0;
        UpdateNodeModulesDeletingText();
        _nodeModulesDeletingTextTimer.Stop();
        _nodeModulesDeletingTextTimer.Start();
    }

    private void StopNodeModulesDeletingTextAnimation()
    {
        _nodeModulesDeletingTextTimer.Stop();
        _nodeModulesDeletingTextDotCount = 0;
    }

    private void NodeModulesDeletingTextTimer_Tick(object? sender, EventArgs e)
    {
        _nodeModulesDeletingTextDotCount = (_nodeModulesDeletingTextDotCount + 1) % 4;
        UpdateNodeModulesDeletingText();
    }

    private void UpdateNodeModulesDeletingText()
    {
        NodeModulesRepairConfirmOverlayMessageRun.Text = " 删除中" + new string('.', _nodeModulesDeletingTextDotCount);
    }

    private async Task RepairNodeModulesFromTarAsync(string portableRoot, bool showDeletingOverlay = false)
    {
        if (_isRepairingNodeModules)
            return;

        string appDir = Path.Combine(portableRoot, "app");
        string nodeModulesPath = Path.Combine(appDir, "node_modules");
        string tarPath = Path.Combine(appDir, "node_modules.tar");
        if (!File.Exists(tarPath) || new FileInfo(tarPath).Length <= 0)
        {
            RefreshNodeModulesHealth(animate: true);
            HideNodeModulesRepairConfirmOverlay();
            await ShowNodeModulesTarDownloadPromptAsync();
            return;
        }

        _isRepairingNodeModules = true;
        SetNodeModulesPercentMode(NodeModulesWarningBrush);
        ShowNodeModulesRingNormalIcon();
        UpdateStartLaunchTileAvailability();
        UpdateNodeModulesRepairAvailability();
        SetNodeModulesProgress(0, 0);

        Task progressTask = Task.CompletedTask;

        try
        {
            if (Directory.Exists(nodeModulesPath))
            {
                if (showDeletingOverlay)
                {
                    ShowNodeModulesRepairDeletingOverlay();
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
                }

                await DeleteDirectoryWithRetryAsync(nodeModulesPath);

                if (Directory.Exists(nodeModulesPath))
                    throw new IOException("node_modules 目录仍未删除完成，已取消解压。请稍后重试。");

                if (showDeletingOverlay)
                {
                    HideNodeModulesRepairConfirmOverlay();
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
                }
            }
            else if (showDeletingOverlay)
            {
                HideNodeModulesRepairConfirmOverlay();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
            }

            if (Directory.Exists(nodeModulesPath))
                throw new IOException("node_modules 目录仍未删除完成，已取消解压。请稍后重试。");

            progressTask = SimulateNodeModulesRepairProgressAsync();

            int exitCode = await RunNodeModulesTarExtractionAsync(portableRoot);
            _isRepairingNodeModules = false;
            await progressTask;

            if (exitCode == 0)
            {
                RefreshNodeModulesHealth(animate: false);
            }
            else
            {
                RefreshNodeModulesHealth(animate: false);
                System.Windows.MessageBox.Show(this, $"node_modules.tar 解压失败，错误代码：{exitCode}", "修复失败", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            _isRepairingNodeModules = false;
            await progressTask;
            RefreshNodeModulesHealth(animate: false);
            Debug.WriteLine($"[NodeModules] Repair failed: {ex.Message}");
            System.Windows.MessageBox.Show(this, $"修复 node_modules 失败：{ex.Message}", "修复失败", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        finally
        {
            _isRepairingNodeModules = false;
            HideNodeModulesRepairConfirmOverlay();
            UpdateStartLaunchTileAvailability();
            UpdateNodeModulesRepairAvailability();
        }
    }

    private static async Task DeleteDirectoryWithRetryAsync(string path)
    {
        if (!Directory.Exists(path))
            return;

        Exception? lastException = null;

        for (int attempt = 1; attempt <= 12; attempt++)
        {
            try
            {
                TryClearReadOnlyAttributes(path);

                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);

                if (await WaitForDirectoryDeletedAsync(path))
                {
                    await Task.Delay(300);
                    return;
                }

                lastException = new IOException("node_modules 目录删除后仍未完全消失。");
            }
            catch (IOException ex)
            {
                lastException = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                lastException = ex;
            }

            int delayMs = Math.Min(1800, 250 + (attempt * 180));
            await Task.Delay(delayMs);
        }

        throw new IOException(
            "node_modules 正在被其他程序占用，暂时无法删除。请关闭可能正在访问该目录的程序后重试，例如 n8n、命令行、编辑器、资源管理器或杀毒软件扫描。",
            lastException);
    }

    private static async Task<bool> WaitForDirectoryDeletedAsync(string path)
    {
        for (int i = 0; i < 20; i++)
        {
            if (!Directory.Exists(path))
                return true;

            await Task.Delay(150);
        }

        return !Directory.Exists(path);
    }

    private static void TryClearReadOnlyAttributes(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return;

            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    FileAttributes attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
                catch
                {
                    // 忽略单个文件属性清理失败，交给删除重试处理。
                }
            }

            foreach (string directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    FileAttributes attributes = File.GetAttributes(directory);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(directory, attributes & ~FileAttributes.ReadOnly);
                }
                catch
                {
                    // 忽略单个目录属性清理失败，交给删除重试处理。
                }
            }

            try
            {
                FileAttributes rootAttributes = File.GetAttributes(path);
                if ((rootAttributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(path, rootAttributes & ~FileAttributes.ReadOnly);
            }
            catch
            {
                // 忽略根目录属性清理失败，交给删除重试处理。
            }
        }
        catch
        {
            // 枚举过程中如果遇到临时占用，交给删除重试处理。
        }
    }

    private void RefreshNodeModulesHealth(bool animate)
    {
        if (_isRepairingNodeModules)
            return;

        try
        {
            string? portableRoot = ResolvePortableRoot();
            NodeModulesHealthState state = string.IsNullOrWhiteSpace(portableRoot)
                ? new NodeModulesHealthState(false, false)
                : ReadNodeModulesHealth(portableRoot);

            RenderNodeModulesHealth(state, animate);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NodeModules] Health check failed: {ex.Message}");
            RenderNodeModulesHealth(new NodeModulesHealthState(false, false), animate);
        }
    }

    private string? ResolvePortableRoot()
    {
        string stateDir = GetConfigDirOrCreate();
        return GetPortableRootFromStateDir(stateDir);
    }

    private static NodeModulesHealthState ReadNodeModulesHealth(string portableRoot)
    {
        string appDir = Path.Combine(portableRoot, "app");
        string nodeModulesPath = Path.Combine(appDir, "node_modules");
        string tarPath = Path.Combine(appDir, "node_modules.tar");

        bool nodeModulesOk = Directory.Exists(nodeModulesPath)
                             && Directory.EnumerateFileSystemEntries(nodeModulesPath).Any()
                             && Directory.Exists(Path.Combine(nodeModulesPath, ".bin"))
                             && Directory.Exists(Path.Combine(nodeModulesPath, "n8n"))
                             && Directory.Exists(Path.Combine(nodeModulesPath, "@n8n"));

        bool tarOk = File.Exists(tarPath) && new FileInfo(tarPath).Length > 0;

        return new NodeModulesHealthState(nodeModulesOk, tarOk);
    }

    private void RenderNodeModulesHealth(NodeModulesHealthState state, bool animate)
    {
        if (state.NodeModulesOk)
        {
            SetNodeModulesCheckMode();
            if (animate)
                AnimateNodeModulesProgress(100, 100, TimeSpan.FromMilliseconds(520));
            else
                SetNodeModulesProgress(100, 100);
            return;
        }

        SetNodeModulesPercentMode(NodeModulesWarningBrush);

        if (animate)
            AnimateNodeModulesProgress(0, 0, TimeSpan.FromMilliseconds(520));
        else
            SetNodeModulesProgress(0, 0);
    }

    private System.Windows.Threading.DispatcherTimer? _nodeModulesAnimTimer;

    private void AnimateNodeModulesProgress(double targetPercent, double targetRingPercent, TimeSpan duration)
    {
        StopNodeModulesAnimation();

        double startPercent = _nodeModulesDisplayedPercent;
        DateTime startedAt = DateTime.UtcNow;

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };

        timer.Tick += (_, _) =>
        {
            double elapsed = (DateTime.UtcNow - startedAt).TotalMilliseconds;
            double progress = duration.TotalMilliseconds <= 0 ? 1 : Math.Clamp(elapsed / duration.TotalMilliseconds, 0, 1);
            double eased = NodeModulesProgressEase.Ease(progress);
            double currentPercent = startPercent + ((targetPercent - startPercent) * eased);
            double currentRingPercent = startPercent + ((targetRingPercent - startPercent) * eased);

            SetNodeModulesProgress(currentPercent, currentRingPercent);

            if (progress >= 1)
            {
                timer.Stop();
                if (ReferenceEquals(_nodeModulesAnimTimer, timer))
                    _nodeModulesAnimTimer = null;
            }
        };

        _nodeModulesAnimTimer = timer;
        timer.Start();
    }

    private void StopNodeModulesAnimation()
    {
        if (_nodeModulesAnimTimer is not null)
        {
            _nodeModulesAnimTimer.Stop();
            _nodeModulesAnimTimer = null;
        }
    }

    private void SetNodeModulesProgress(double percent, double ringPercent)
    {
        percent = Math.Clamp(percent, 0, 100);
        ringPercent = Math.Clamp(ringPercent, 0, 100);
        _nodeModulesDisplayedPercent = percent;

        // WPF 的 StrokeDashArray 数值单位不是 DIP，而是 StrokeThickness 的倍数。
        // NodeModulesRingDashTotal 是按实际圆周 DIP 估算的长度，因此必须除以线宽；
        // 否则 StrokeThickness=8 时，视觉进度会约快 8 倍，导致 42 秒计划实际约 6 秒跑满。
        double strokeThickness = NodeModulesHealthProgressRing.StrokeThickness;
        double dashTotal = strokeThickness > 0 ? NodeModulesRingDashTotal / strokeThickness : NodeModulesRingDashTotal;

        if (ringPercent <= 0)
        {
            NodeModulesHealthProgressRing.StrokeDashArray = new DoubleCollection { 0, dashTotal };
            return;
        }

        if (ringPercent >= 100)
        {
            NodeModulesHealthProgressRing.StrokeDashArray = new DoubleCollection { dashTotal, 0 };
            return;
        }

        double dashLength = dashTotal * ringPercent / 100.0;
        NodeModulesHealthProgressRing.StrokeDashArray = new DoubleCollection
        {
            dashLength,
            dashTotal - dashLength
        };
    }

    private void SetNodeModulesPercentMode(Brush brush)
    {
        NodeModulesHealthProgressRing.Stroke = brush;
        NodeModulesHealthStatusIcon.Source = FindResource("NodeModulesErrorIconImage") as ImageSource;
        NodeModulesHealthStatusIcon.Width = 10;
        NodeModulesHealthStatusIcon.Height = 48;
        NodeModulesHealthStatusIcon.Margin = new Thickness(0);
    }

    private void SetNodeModulesCheckMode()
    {
        NodeModulesHealthProgressRing.Stroke = NodeModulesOkBrush;
        NodeModulesHealthStatusIcon.Source = FindResource("NodeModulesRightIconImage") as ImageSource;
        NodeModulesHealthStatusIcon.Width = 40;
        NodeModulesHealthStatusIcon.Height = 30;
        NodeModulesHealthStatusIcon.Margin = new Thickness(0, 2, 0, 0);
    }

    private async Task SimulateNodeModulesRepairProgressAsync()
    {
        StopNodeModulesAnimation();

        DateTime startedAt = DateTime.UtcNow;
        double startPercent = Math.Clamp(_nodeModulesDisplayedPercent, 0, 80);
        const double targetPercent = 80;
        TimeSpan duration = TimeSpan.FromSeconds(42);

        while (_isRepairingNodeModules)
        {
            double progress = Math.Clamp((DateTime.UtcNow - startedAt).TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
            double simulatedPercent = startPercent + ((targetPercent - startPercent) * progress);
            SetNodeModulesPercentMode(NodeModulesWarningBrush);
            ShowNodeModulesRingNormalIcon();
            SetNodeModulesProgress(simulatedPercent, simulatedPercent);
            await Task.Delay(200);
        }
    }

    private static async Task<int> RunNodeModulesTarExtractionAsync(string portableRoot)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "tar",
                Arguments = "-xf app\\node_modules.tar -C app",
                WorkingDirectory = portableRoot,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private void OpenPortableFolder(string relativePath)
    {
        try
        {
            string stateDir = GetConfigDirOrCreate();
            string? portableRoot = GetPortableRootFromStateDir(stateDir);
            if (string.IsNullOrWhiteSpace(portableRoot))
            {
                Debug.WriteLine($"[FolderOpen] Cannot resolve portable root from state dir: {stateDir}");
                System.Windows.MessageBox.Show(this, "无法定位 n8n 便携包根目录。", "打开文件夹失败", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }

            string targetDir = string.IsNullOrWhiteSpace(relativePath) || relativePath == "."
                ? portableRoot
                : Path.Combine(portableRoot, relativePath);

            Directory.CreateDirectory(targetDir);

            Process.Start(new ProcessStartInfo
            {
                FileName = targetDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FolderOpen] Open failed: {relativePath} — {ex.Message}");
            System.Windows.MessageBox.Show(this, $"无法打开文件夹：{ex.Message}", "打开文件夹失败", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private static bool IsN8nReadyOutputLine(string line)
    {
        return line.Contains("n8n ready on", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Editor is now accessible via:", StringComparison.OrdinalIgnoreCase);
    }

    private async Task OpenN8nWhenHttpReadyAsync(Process expectedProcess, int startGeneration)
    {
        await Task.Delay(TimeSpan.FromSeconds(2));

        if (!IsCurrentN8nProcess(expectedProcess, startGeneration))
            return;

        bool isReady = await WaitForN8nHttpReadyAsync(
            TimeSpan.FromSeconds(90),
            TimeSpan.FromMilliseconds(900),
            () => IsCurrentN8nProcess(expectedProcess, startGeneration));

        await Dispatcher.InvokeAsync(() =>
        {
            if (!IsCurrentN8nProcess(expectedProcess, startGeneration))
            {
                AppendConsoleOutput("[WARN] 等待自动打开页面期间，n8n 进程已经退出或已被新的启动替换，已取消打开页面。");
                return;
            }

            if (!isReady)
            {
                AppendConsoleOutput("[WARN] 已检测到 n8n ready 日志，但 90 秒内本地接口仍未稳定可访问，已取消自动打开页面。可稍后手动打开本地页面。");
                return;
            }

            AppendConsoleOutput($"[INFO] 已确认 n8n 本地接口可访问，正在自动打开页面：{N8nLocalRootUrl}");
            OpenUrl(N8nLocalRootUrl);
            FadeLaunchTile02ActionTextIn(LaunchTile02OpenWorkbenchText);
        });
    }

    private bool IsCurrentN8nProcess(Process expectedProcess, int startGeneration)
    {
        return System.Threading.Volatile.Read(ref _n8nStartGeneration) == startGeneration
            && ReferenceEquals(_n8nProcess, expectedProcess)
            && !expectedProcess.HasExited;
    }

    private static async Task<bool> WaitForN8nHttpReadyAsync(TimeSpan timeout, TimeSpan retryInterval, Func<bool> shouldContinue)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        using var httpClient = new System.Net.Http.HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3)
        };

        int consecutiveReadyCount = 0;
        while (DateTimeOffset.UtcNow < deadline && shouldContinue())
        {
            try
            {
                if (await IsN8nRootPageReadyAsync(httpClient))
                {
                    consecutiveReadyCount++;
                    if (consecutiveReadyCount >= 2)
                        return true;
                }
                else
                {
                    consecutiveReadyCount = 0;
                }
            }
            catch
            {
                consecutiveReadyCount = 0;
            }

            await Task.Delay(retryInterval);
        }

        return false;
    }

    private static async Task<bool> IsN8nRootPageReadyAsync(System.Net.Http.HttpClient httpClient)
    {
        using var response = await httpClient.GetAsync(N8nLocalRootUrl);
        if (!response.IsSuccessStatusCode)
            return false;

        string content = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(content))
            return false;

        if (content.Contains("Cannot GET /", StringComparison.OrdinalIgnoreCase))
            return false;

        return content.Contains("</html>", StringComparison.OrdinalIgnoreCase)
            || content.Contains("n8n", StringComparison.OrdinalIgnoreCase)
            || content.Contains("app", StringComparison.OrdinalIgnoreCase);
    }

    private void OpenUrl(string url)
    {
        try
        {
            var config = LoadConfig();
            string browserPath = NormalizeBrowserPath(config.BrowserPath);
            if (!string.IsNullOrWhiteSpace(browserPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = browserPath,
                    Arguments = BuildBrowserUrlArguments(browserPath, url, addChromiumTestType: true),
                    UseShellExecute = false
                });
                return;
            }

            string defaultBrowserPath = ResolveDefaultBrowserPath();
            if (!string.IsNullOrWhiteSpace(defaultBrowserPath) && IsChromiumBrowser(defaultBrowserPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = defaultBrowserPath,
                    Arguments = BuildBrowserUrlArguments(defaultBrowserPath, url, addChromiumTestType: true),
                    UseShellExecute = false
                });
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UrlOpen] Open failed: {url} — {ex.Message}");
            System.Windows.MessageBox.Show(this, $"无法打开链接：{ex.Message}", "打开链接失败", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private static string BuildBrowserUrlArguments(string browserPath, string url, bool addChromiumTestType)
    {
        string escapedUrl = $"\"{url}\"";
        return addChromiumTestType && IsChromiumBrowser(browserPath)
            ? $"--test-type {escapedUrl}"
            : escapedUrl;
    }

    private static string ResolveDefaultBrowserPath()
    {
        string? progId = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice", "ProgId", null) as string;
        string? command = null;
        if (!string.IsNullOrWhiteSpace(progId))
            command = Registry.GetValue($@"HKEY_CLASSES_ROOT\{progId}\shell\open\command", string.Empty, null) as string;

        command ??= Registry.GetValue(@"HKEY_CLASSES_ROOT\http\shell\open\command", string.Empty, null) as string;
        return ExtractExecutablePathFromRunCommand(command) ?? string.Empty;
    }

    private static bool IsChromiumBrowser(string browserPath)
    {
        string fileName = Path.GetFileName(browserPath);
        return string.Equals(fileName, "msedge.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "chrome.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "chromium.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "brave.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "opera.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "opera_gx.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildStatusText(VersionCache versions)
    {
        string n8nLatestLabel = string.Equals(versions.N8nLatestStatus, "Latest", StringComparison.OrdinalIgnoreCase)
            ? "最新"
            : "未知";
        bool launcherLatestOk = string.Equals(versions.LauncherLatestStatus, "Latest", StringComparison.OrdinalIgnoreCase);
        string launcherLatestLabel = launcherLatestOk ? "最新" : "未知";
        // Launcher 最新版号在"未知"降级态下沿用当前本地版号占位，避免出现 "0.0.0 [未知]" 造成阅读困惑。
        string launcherLatestDisplay = launcherLatestOk
            ? NormalizeLatestVersion(versions.LauncherLatest)
            : versions.Launcher;

        return $"n8n: {versions.N8nCurrent} [当前] / {NormalizeLatestVersion(versions.N8nLatest)} [{n8nLatestLabel}]   |   " +
               $"Node.js: {FormatNodeVersion(versions.NodeJs)}   " +
               $"Python: {versions.Python}   " +
               $"FFmpeg: {versions.Ffmpeg}   |   " +
               $"Launcher: {versions.Launcher} [当前] / {launcherLatestDisplay} [{launcherLatestLabel}]\u00A0\u00A0\u00A0";
    }

    private static string FormatNodeVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || version == "未知")
            return "未知";
        return version.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? version : $"v{version}";
    }

    private void RenderVersionInfo(VersionCache versions)
    {
        EnsureConfigDefaults(new AppConfig { Versions = versions, WindowScale = _currentUserScale });
        string text = BuildStatusText(versions);
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = text;
            Debug.WriteLine($"[VersionInfo] UI Updated: {text}");
        });
    }

    private static async Task RefreshLocalVersionsAsync(string portableRoot, VersionCache versions)
    {
        versions.Launcher = LAUNCHER_VERSION;

        string nodeExe = Path.Combine(portableRoot, "runtime", "node", "node.exe");
        string pythonExe = Path.Combine(portableRoot, "runtime", "python", "python", "python.exe");
        string ffmpegExe = Path.Combine(portableRoot, "runtime", "ffmpeg", "bin", "ffmpeg.exe");
        string n8nPackageJson = Path.Combine(portableRoot, "app", "node_modules", "n8n", "package.json");

        string? nodeOut = await RunProcessAsync(nodeExe, "-v", portableRoot, TimeSpan.FromSeconds(5));
        versions.NodeJs = MatchValue(nodeOut ?? "", @"v?([\d\.]+)") ?? versions.NodeJs;

        string? pythonOut = await RunProcessAsync(pythonExe, "--version", portableRoot, TimeSpan.FromSeconds(5));
        versions.Python = MatchValue(pythonOut ?? "", @"Python\s+([\d\.]+)") ?? versions.Python;

        string? ffmpegOut = await RunProcessAsync(ffmpegExe, "-version", portableRoot, TimeSpan.FromSeconds(5));
        string? ffmpegVersion = MatchValue(ffmpegOut ?? "", @"version\s+([^\s]+)");
        if (!string.IsNullOrWhiteSpace(ffmpegVersion))
            versions.Ffmpeg = ffmpegVersion.Length > 10 ? ffmpegVersion[..10] : ffmpegVersion;

        string? n8nCurrent = ReadPackageVersion(n8nPackageJson);
        if (!string.IsNullOrWhiteSpace(n8nCurrent))
            versions.N8nCurrent = n8nCurrent;

        versions.LocalVersionUpdatedAt = DateTimeOffset.Now.ToString("O");
        EnsureConfigDefaults(new AppConfig { Versions = versions, WindowScale = 1.0 });
    }

    private static string? ReadPackageVersion(string packageJsonPath)
    {
        try
        {
            if (!File.Exists(packageJsonPath))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (doc.RootElement.TryGetProperty("version", out var versionElement))
                return versionElement.GetString();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VersionInfo] Read n8n package.json error: {ex.Message}");
        }

        return null;
    }

    private static bool ShouldRefreshLatestVersion(VersionCache versions)
    {
        if (!string.Equals(versions.N8nLatestStatus, "Latest", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.IsNullOrWhiteSpace(versions.N8nLatest) || versions.N8nLatest == "未知" || versions.N8nLatest == "0.0.0")
            return true;

        if (!DateTimeOffset.TryParse(versions.LatestVersionUpdatedAt, out var lastUpdate))
            return true;

        return DateTimeOffset.Now - lastUpdate > TimeSpan.FromHours(12);
    }

    /// <summary>
    /// 判断是否需要刷新启动器远端最新版本缓存。判据与 <see cref="ShouldRefreshLatestVersion"/> 一致：
    /// 上次拉取失败（Status != Latest）、缓存值无效、缓存值缺时间戳、或距离上次成功刷新已超过 12 小时。
    /// </summary>
    private static bool ShouldRefreshLauncherLatestVersion(VersionCache versions)
    {
        if (!string.Equals(versions.LauncherLatestStatus, "Latest", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.IsNullOrWhiteSpace(versions.LauncherLatest) || versions.LauncherLatest == "未知" || versions.LauncherLatest == "0.0.0")
            return true;

        if (!DateTimeOffset.TryParse(versions.LauncherLatestUpdatedAt, out var lastUpdate))
            return true;

        return DateTimeOffset.Now - lastUpdate > TimeSpan.FromHours(12);
    }

    /// <summary>
    /// 拉取仓库 <c>launcher_release.json</c> 并解析 <c>latest_version</c> 字段。
    /// 8 秒超时；失败返回 null（调用方降级为 [未知] 并保留旧缓存）。
    /// </summary>
    private static async Task<string?> QueryLatestLauncherVersionAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var httpClient = new System.Net.Http.HttpClient
            {
                Timeout = TimeSpan.FromSeconds(8)
            };

            // raw.githubusercontent.com 建议携带 User-Agent 头，避免部分 CDN 节点直接 403。
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"n8n_launcher_Gv/{LAUNCHER_VERSION}");
            httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");

            using var response = await httpClient.GetAsync(LauncherReleaseManifestUrl, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"[VersionInfo] launcher_release.json returned HTTP {(int)response.StatusCode}");
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(cts.Token);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("latest_version", out var versionElement))
            {
                Debug.WriteLine("[VersionInfo] launcher_release.json missing latest_version field");
                return null;
            }

            string? version = versionElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(version))
            {
                Debug.WriteLine("[VersionInfo] launcher_release.json latest_version is empty");
                return null;
            }

            Debug.WriteLine($"[VersionInfo] Latest Launcher version from manifest: {version}");
            return version;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VersionInfo] launcher_release.json query failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<string?> QueryLatestN8nVersionAsync(string portableRoot)
    {
        const string registryLatestUrl = "https://registry.npmjs.org/n8n/latest";

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var httpClient = new System.Net.Http.HttpClient
            {
                Timeout = TimeSpan.FromSeconds(8)
            };

            using var response = await httpClient.GetAsync(registryLatestUrl, cts.Token);
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync(cts.Token);
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("version", out var versionElement))
                {
                    string? version = versionElement.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(version) && Regex.IsMatch(version, @"^[0-9]+(?:\.[0-9]+)+"))
                    {
                        Debug.WriteLine($"[VersionInfo] Latest n8n version from npm registry: {version}");
                        return version;
                    }
                }
            }

            Debug.WriteLine($"[VersionInfo] npm registry returned HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VersionInfo] npm registry query failed: {ex.Message}");
        }

        string npmExe = Path.Combine(portableRoot, "runtime", "node", "npm.exe");
        if (!File.Exists(npmExe))
            return null;

        string? output = await RunProcessAsync(npmExe, "view n8n version --silent", portableRoot, TimeSpan.FromSeconds(12));
        string? fallbackVersion = MatchValue(output ?? "", @"([0-9]+(?:\.[0-9]+)+)");
        return fallbackVersion;
    }

    private static async Task<string?> RunProcessAsync(string fileName, string arguments, string workingDirectory, TimeSpan timeout)
    {
        Process? process = null;

        try
        {
            if (!File.Exists(fileName) && !string.Equals(fileName, "cmd.exe", StringComparison.OrdinalIgnoreCase))
                return null;

            using var cts = new CancellationTokenSource(timeout);
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : AppDomain.CurrentDomain.BaseDirectory,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            process = Process.Start(startInfo);
            if (process == null)
                return null;

            string stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
            string stderr = await process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);
            string output = $"{stdout}\n{stderr}".Trim();
            Debug.WriteLine($"[VersionInfo] Process {Path.GetFileName(fileName)} exited {process.ExitCode}: {output}");
            return output;
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (process != null && !process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception killEx)
            {
                Debug.WriteLine($"[VersionInfo] Process kill after timeout failed: {killEx.Message}");
            }

            Debug.WriteLine($"[VersionInfo] Process timeout: {fileName} {arguments}");
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VersionInfo] Process error: {fileName} {arguments} — {ex.Message}");
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>
    /// 根据主屏幕物理高度自动选择初始缩放倍数（不受 DPI 缩放影响）。
    /// 档位固定整十百分比阶梯（100% / 75% / 60% / 50% / 40%）：窗口尺寸 = 2160×1440 × 倍数，
    /// 阈值按「目标屏工作区高度放得下且不浪费」标定：1080P / 1024 高 5:4 → 0.6（占工作区 84% / 89%）、768P/720P → 0.4（占 80% / 86%）。
    ///   1.0 → 2160×1440、0.75 → 1620×1080、0.6 → 1296×864、0.5 → 1080×720、0.4 → 864×576。
    /// 返回值必须与托盘「比例切换」和设置页下拉框的档位一一对应。
    /// </summary>
    private static double ComputeAutoScale()
    {
        int height = GetSystemMetrics(SM_CYSCREEN);
        Debug.WriteLine($"[Config] PrimaryScreenHeight (physical) = {height}");

        if (height > 1800) return 1.0;
        if (height > 1280) return 0.75;
        if (height > 980)  return 0.6;    // 1080P / 1024 高 5:4 → 1296×864
        if (height > 840)  return 0.5;    // 900P / 960P → 1080×720
        if (height > 640)  return 0.4;    // 768P/720P → 864×576

        return 0.4;
    }

    /// <summary>
    /// 把用户选择的托盘倍数转换为 WPF LayoutTransform 倍数。
    /// userScale 是唯一业务缩放；DESIGN_DPI_SCALE / systemDpi 只做统一 DPI 补偿，保证物理尺寸不被系统 DPI 改变。
    /// </summary>
    private static double ComputeLayoutScale(double userScale, double systemDpi)
    {
        double safeDpi = systemDpi > 0 ? systemDpi : 1.0;
        return userScale * DESIGN_DPI_SCALE / safeDpi;
    }

    /// <summary>启动时应用缩放：优先读取配置记忆，首次启动自动计算并保存</summary>
    private void ApplyStartupScale()
    {
        var config = LoadConfig();
        _currentUserScale = config.WindowScale;
        SetWindowScaleCore(_currentUserScale);
    }

    /// <summary>
    /// 根据当前窗口所在显示器的工作区，计算指定物理尺寸窗口的居中左上角坐标。
    /// 注意：这里移动的是窗口外壳，不改变 AppDesignSurface 左上角缩放锚点。
    /// </summary>
    private static (int X, int Y) GetCenteredWindowPosition(IntPtr handle, int physW, int physH)
    {
        var screen = handle != IntPtr.Zero
            ? System.Windows.Forms.Screen.FromHandle(handle)
            : System.Windows.Forms.Screen.PrimaryScreen;

        var workArea = screen?.WorkingArea ?? System.Drawing.Rectangle.Empty;
        if (workArea == System.Drawing.Rectangle.Empty)
            return (0, 0);

        int x = workArea.Left + Math.Max(0, (workArea.Width  - physW) / 2);
        int y = workArea.Top  + Math.Max(0, (workArea.Height - physH) / 2);
        return (x, y);
    }

    /// <summary>
    /// 核心缩放逻辑。
    /// 架构说明：
    ///   一倍物理基准固定为 2160×1440，用户倍数只按托盘档位统一缩放。
    ///   AppDesignSurface 固定为 1440×960 DIP（150% 设计基准），不改变内部文字/图标/间距比例。
    ///   LayoutScale = userScale × 1.5 / systemDpi，用统一 DPI 补偿保证视觉物理尺寸不受系统 DPI 改变。
    ///   窗口 DIP 尺寸 = 目标物理尺寸 / 当前系统 DPI。
    /// </summary>
    private void SetWindowScaleCore(double userScale)
    {
        double systemDpi = _systemDpi;

        // 1. 设定应用设计画布的固定 DIP 尺寸（150% 设计基准，不随系统 DPI 变化）
        AppDesignSurface.Width  = DESIGN_DIP_WIDTH;
        AppDesignSurface.Height = DESIGN_DIP_HEIGHT;

        // 2. 以左上角为锚点统一缩放 AppDesignSurface；DPI 补偿也只作为统一倍数参与，不单独改任何控件尺寸。
        double layoutScale = ComputeLayoutScale(userScale, systemDpi);
        LayoutScale.ScaleX = layoutScale;
        LayoutScale.ScaleY = layoutScale;

        // 3. 窗口 DIP 尺寸 = 目标物理尺寸 / 当前系统 DPI。
        double scaledW = BASE_PHYSICAL_WIDTH  * userScale / systemDpi;
        double scaledH = BASE_PHYSICAL_HEIGHT * userScale / systemDpi;
        Width  = scaledW;
        Height = scaledH;

        _targetDipsW = scaledW;
        _targetDipsH = scaledH;

        // 4. 强制布局，并用实际物理尺寸显式调整窗口外壳，触发 DWM 立即重新合成 Acrylic 背景
        UpdateLayout();

        var handle = new WindowInteropHelper(this).Handle;
        int physW = (int)(scaledW * systemDpi);
        int physH = (int)(scaledH * systemDpi);
        var centeredPos = GetCenteredWindowPosition(handle, physW, physH);
        // 注意：不传 SWP_NOSIZE，也不传 SWP_NOMOVE，显式传入新的物理坐标和尺寸。
        // 内容仍然以 AppDesignSurface 左上角缩放；这里只把窗口外壳重新放到屏幕工作区中心。
        SetWindowPos(handle, IntPtr.Zero, centeredPos.X, centeredPos.Y, physW, physH,
            SWP_NOZORDER | SWP_FRAMECHANGED);
        // 强制立即重绘整个客户区（让 WPF 重走 layout + DWM 重新合成 Acrylic）
        InvalidateRect(handle, IntPtr.Zero, true);

        Debug.WriteLine($"[Scale] userScale={userScale:F4}, layoutScale={layoutScale:F4}, systemDpi={systemDpi:F2}, " +
                        $"DesignSurface(DIP)={DESIGN_DIP_WIDTH:F0}x{DESIGN_DIP_HEIGHT:F0}, " +
                        $"scaled(DIP)={scaledW:F0}x{scaledH:F0}, " +
                        $"Window(Physical)={physW}x{physH}, " +
                        $"center=({centeredPos.X},{centeredPos.Y})");

        // 5. 更新托盘缩放子菜单的选中状态
        UpdateScaleMenuCheck(userScale);
    }

    /// <summary>同步新托盘弹层菜单的当前缩放状态</summary>
    private void UpdateScaleMenuCheck(double userScale)
    {
        _trayPopupMenu?.UpdateScale(userScale);
        Debug.WriteLine($"[Scale] Tray popup state updated — userScale={userScale:F4}");
    }

    private void HideStartupWindowToTray()
    {
        // 只隐藏到托盘，不保留最小化窗口状态；避免之后托盘唤起时只显示标题栏外壳。
        WindowState = WindowState.Normal;
        Hide();
        Opacity = 1;
        // 内存优化：开机静默启动直接进托盘，同样进入待挂起倒计时。
        OnWindowHiddenForMemory();
    }

    private void ShowAndActivate()
    {
        Dispatcher.Invoke(() =>
        {
            Opacity = 1;
            WindowState = WindowState.Normal;
            ShowInTaskbar = true;
            Show();
            UpdateLayout();

            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
                ShowWindow(handle, SW_RESTORE);
            }

            Activate();
            Focus();

            // 内存优化：取消待挂起倒计时；若动画已挂起则立即按当前真实状态恢复。
            OnWindowShownForMemory();
        });

        _ = RefreshWorkspaceDataAsync(WorkspaceDataRefreshReason.TrayOpen);
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExiting)
        {
            e.Cancel = true;
            Hide();
            // 内存优化：关闭按钮只隐藏到托盘，进入待挂起倒计时。
            OnWindowHiddenForMemory();
        }
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _sleepTimer.Stop();
        CancelAnimationSuspendTimer();
        UnregisterCopilotHotkey();
        await StopN8nProcessAsync(showNoProcessMessage: false, showErrors: false);
        SystemEvents.UserPreferenceChanged -= _themeChangedHandler;
        _trayPopupMenu?.Dispose();
        _winFormsTray?.Dispose();
    }

    private async Task RestartLauncherForLowPerformanceModeChangeAsync()
    {
        bool previousIsExiting = _isExiting;
        try
        {
            string exePath = GetLauncherExecutablePath();
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                throw new FileNotFoundException("无法定位启动器可执行文件。", exePath);

            _isExiting = true;
            _sleepTimer.Stop();
            UnregisterCopilotHotkey();
            await StopN8nProcessAsync(showNoProcessMessage: false, showErrors: false);

            SystemEvents.UserPreferenceChanged -= _themeChangedHandler;
            _trayPopupMenu?.Dispose();
            _trayPopupMenu = null;
            _winFormsTray?.Dispose();
            _winFormsTray = null;

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppDomain.CurrentDomain.BaseDirectory,
                UseShellExecute = true
            });

            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _isExiting = previousIsExiting;
            Debug.WriteLine($"[PerformanceMode] Restart launcher failed: {ex.Message}");
            System.Windows.MessageBox.Show(this, $"重启启动器失败：{ex.Message}", "重启失败", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private async void ExitApplication()
    {
        _isExiting = true;
        _sleepTimer.Stop();
        UnregisterCopilotHotkey();
        await StopN8nProcessAsync(showNoProcessMessage: false, showErrors: false);
        SystemEvents.UserPreferenceChanged -= _themeChangedHandler;
        _trayPopupMenu?.Dispose();
        _winFormsTray?.Dispose();
        Application.Current.Shutdown();
    }

    // ============================================================
    // 图标加载
    // ============================================================
    private void LoadSidebarIcon()
    {
        try
        {
            // ★ 为何要显式走 BeginInit/DecodePixelWidth，而不是一行 new BitmapImage(uri)：
            //   源图 n8n_launcher.png 为 512×512，而 SidebarIcon 在 XAML 中固定 17.33 DIP。
            //   若直接整图解码，WPF 会在渲染期把 512px 用默认双线性算法缩到约 26px（150% DPI）
            //   —— 缩小近 20 倍的极端降采样，细节会被均匀糊成灰边（改造前"图标发糊"的根因之二，
            //   另一处是 XAML 里 Window.Icon 曾指向 PNG，已改为多帧 .ico）。
            //
            // ★ DecodePixelWidth/Height = 64 的换算依据：
            //   17.33 DIP × 200% DPI（当前支持的最高档）= 34.66 物理像素，
            //   64 是大于它的最近 2 的幂 —— 既保证任何 DPI 下都不会因像素不足而模糊，
            //   缩放比例又足够温和。降采样由 WIC 解码器在解码阶段高质量完成。
            //   ※ 若日后把 SidebarIcon 的 Width/Height 调到 32 DIP 以上，此处 64 需同步上调，
            //     否则会因解码尺寸不足重新变糊（本项目易忘点，改布局时务必回看这里）。
            //
            // ★ 顺带的内存收益：解码后位图从 512×512×4 ≈ 1MB 降到 64×64×4 = 16KB，
            //   与 csproj 中那套"托盘常驻期压低驻留内存"的优化同向。
            var icon = new BitmapImage();
            icon.BeginInit();
            icon.UriSource = new Uri("pack://application:,,,/icon/n8n_launcher.png", UriKind.Absolute);
            icon.DecodePixelWidth = 64;
            icon.DecodePixelHeight = 64;
            icon.EndInit();
            // Freeze：位图冻结后跨线程只读共享、免去 WPF 的变更通知簿记，
            // 且不会因为反复解码而在托盘常驻期占用可变对象内存。
            if (icon.CanFreeze)
                icon.Freeze();

            // 64 → 约 26 物理像素这最后一步缩放交给渲染层，
            // 显式指定 HighQuality（Fant 算法）而非默认双线性，进一步保住边缘锐度。
            RenderOptions.SetBitmapScalingMode(SidebarIcon, BitmapScalingMode.HighQuality);
            SidebarIcon.Source = icon;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Icon] Sidebar load error: {ex.Message}");
        }
    }

    private static System.Drawing.Icon? LoadEmbeddedTrayIcon()
    {
        string[] resourceUris =
        {
            "pack://application:,,,/icon/n8n_launcher.ico"
        };

        foreach (string resourceUri in resourceUris)
        {
            try
            {
                var resourceInfo = Application.GetResourceStream(new Uri(resourceUri, UriKind.Absolute));
                if (resourceInfo?.Stream == null)
                    continue;

                using Stream stream = resourceInfo.Stream;
                using var icon = new System.Drawing.Icon(stream);
                return (System.Drawing.Icon)icon.Clone();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Tray] Embedded icon load error ({resourceUri}): {ex.Message}");
            }
        }

        return null;
    }

    private void InitTrayIcon()
    {
        try
        {
            System.Drawing.Icon? trayIcon = LoadEmbeddedTrayIcon();
            if (trayIcon == null)
            {
                Debug.WriteLine("[Tray] No embedded icon found");
                return;
            }

            // 使用 Windows Forms NotifyIcon 以获得原生右键/左键事件支持
            _winFormsTray = new System.Windows.Forms.NotifyIcon
            {
                Icon = trayIcon,
                Text = IsN8nManagedProcessRunning() ? "n8n运行中" : "n8n_launcher_Gv",
                Visible = true
            };

            _winFormsTray.MouseUp += (_, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Right)
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (_trayPopupMenu == null) return;

                        var cursor = System.Windows.Forms.Cursor.Position;
                        if (_trayPopupMenu.IsOpen)
                        {
                            _trayPopupMenu.CloseAll();
                        }
                        else
                        {
                            _trayPopupMenu.IsN8nRunning = IsN8nManagedProcessRunning();
                            _trayPopupMenu.ShowAt(cursor);
                        }
                    });
                }
                else if (e.Button == System.Windows.Forms.MouseButtons.Left)
                {
                    Dispatcher.Invoke(() =>
                    {
                        _trayPopupMenu?.CloseAll();
                        ShowAndActivate();
                    });
                }
            };

            UpdateTrayTooltip();
            Debug.WriteLine("[Tray] Initialized with Windows Forms NotifyIcon");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Tray] Init error: {ex.Message}");
        }
    }

    // ============================================================
    // 主题切换逻辑（深色/浅色模式）
    // ============================================================
    private bool IsSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value != null && (int)value == 0;
        }
        catch
        {
            return false;
        }
    }

    private void UpdateThemeColors()
    {
        bool lowPerformanceMode = false;
        try
        {
            lowPerformanceMode = LoadConfig().LowPerformanceMode;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Theme] Load performance mode failed: {ex.Message}");
        }

        // 深浅判定统一走 IsCurrentThemeDark()（星星 / 流星生成点取色也用它，避免两处逻辑走偏）。
        bool isDark = IsCurrentThemeDark();

        var fg = isDark ? Colors.White : Colors.Black;
        var footerBg = isDark ? ColorDarkFooter : ColorLightFooter;
        var rightBg = isDark ? Color.FromArgb(255, 0x1F, 0x20, 0x21) : Color.FromRgb(0xEE, 0xF4, 0xF9);

        // 标准模式：左侧为 Acrylic 上的半透明遮罩；低性能模式：左侧改为不透明固定背景。
        var sidebarBg = lowPerformanceMode
            ? (isDark ? Color.FromArgb(255, 0x2A, 0x2F, 0x35) : Color.FromRgb(0xF7, 0xF4, 0xF0))
            : (isDark ? ColorDarkSidebar : ColorLightSidebar);
        SidebarBorder.Background = new SolidColorBrush(sidebarBg);
        // 右侧不透明浅蓝灰/深灰底板
        RightPanelBg.Background = new SolidColorBrush(rightBg);
        // 内容区透明（在 RightPanelBg 之上）
        ContentBorder.Background = Brushes.Transparent;
        // 底部版本号
        FooterBorder.Background = new SolidColorBrush(footerBg);
        StatusText.Foreground = new SolidColorBrush(fg);
        SidebarTitleText.Foreground = new SolidColorBrush(fg);
        // Freeze：主题切换会反复走到这里重新构造 BitmapImage，冻结后可省去变更通知簿记。
        var brandLogo = new BitmapImage(new Uri(isDark ? "pack://application:,,,/icon/logo_dark.png" : "pack://application:,,,/icon/logo_light.png"));
        if (brandLogo.CanFreeze)
            brandLogo.Freeze();
        SidebarBrandLogo.Source = brandLogo;

        // 导航按钮文字/图标颜色：深色模式白色，浅色模式 #1F1F1F
        var navFg = isDark ? Colors.White : Color.FromRgb(0x1F, 0x1F, 0x1F);
        var navBrush = new SolidColorBrush(navFg);
        LaunchNavButton.Foreground = navBrush;
        ConsoleNavButton.Foreground = navBrush;
        ExecutionNavButton.Foreground = navBrush;
        SettingNavButton.Foreground = navBrush;
        AboutNavButton.Foreground = navBrush;

        // 导航按钮悬浮/选中/按下的状态叠加层：
        // 不透明度沿用 XAML 原值（悬浮/选中 0x2B、按下 0x33），仅切换叠加色。
        // 浅色模式黑色叠加（背景变暗）；深色模式中灰 #959595 叠加（背景变亮），
        // 因为黑色叠加在深色 Acrylic 背景上几乎不可见；基色由纯白 → #CCCCCC → #959595
        // 逐步收敛，进一步降低悬浮/选中时的明度跳变，避免闪白感。
        byte navOverlay = isDark ? (byte)0x95 : (byte)0x00;
        Resources["NavHoverBrush"] = new SolidColorBrush(Color.FromArgb(0x2B, navOverlay, navOverlay, navOverlay));
        Resources["NavPressedBrush"] = new SolidColorBrush(Color.FromArgb(0x33, navOverlay, navOverlay, navOverlay));

        // 设置页标题文字色（页标题 / 4 个分类名 / 9 个卡片主标题，共 14 处 TextBlock）：
        // 深色模式白色；浅色模式写回 XAML 原硬编码 #1F1F1F（保证深→浅切换可复原，
        // 浅色渲染结果与改动前逐位相同）。
        // 卡片副标题(#80000000)、d/h/m 单位标签、重置按钮文字不在此画刷范围内，保持原硬编码。
        Resources["SettingsHeadingBrush"] = new SolidColorBrush(
            isDark ? Colors.White : Color.FromRgb(0x1F, 0x1F, 0x1F));

        // 设置页卡片底板：浅色纯白 / 深色 #2B2B2B（纯黑过深，与窗口底融为一体），描边已在 XAML 中彻底移除。
        Resources["SettingsCardBrush"] = new SolidColorBrush(
            isDark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Colors.White);

        // 卡片副标题：浅色半透明黑(#80000000，50%) / 深色半透明白(#52FFFFFF，32%)。
        Resources["SettingsSubTextBrush"] = new SolidColorBrush(
            isDark ? Color.FromArgb(0x52, 0xFF, 0xFF, 0xFF)
                   : Color.FromArgb(0x80, 0x00, 0x00, 0x00));

        // 设置页标题下分割线：浅 #14000000（8% 透明黑）/ 深 #3F3F3F（不透明灰）。
        // ★ 本轮统一取值方案：与 ExecutionHeaderDividerBrush、InsightsCardDividerBrush、
        //   LaunchSectionDividerBrush 完全一致，全项目 4 个分割线键彻底同源（键名仍独立）。
        // ★ 改造前浅 #E6E6E6 / 深 #2B2B2B：深色旧值压在 #1F2021 页底板上明度差仅约 12，
        //   几乎不可见；浅色改半透明黑后叠白底≈#EBEBEB，与旧值肉眼不可分辨。
        // ★ 浅色分支必须 Color.FromArgb 保留 alpha=0x14，误用 FromRgb 会退化成纯黑重线
        //   （本项目历史踩坑点，已第 4 次遇到）。
        Resources["SettingsDividerBrush"] = new SolidColorBrush(
            isDark ? Color.FromRgb(0x3F, 0x3F, 0x3F)
                   : Color.FromArgb(0x14, 0x00, 0x00, 0x00));

        // 控制台页左上角状态标题（ConsoleStatusText，如「未运行 (Not Running)」）：
        // 浅色写回 XAML 原硬编码 #1F1F1F（保证深→浅切换逐位复原）；深色改纯白，
        // 否则近黑文字压在深色 Acrylic 页底上几乎不可辨识。
        // 与 SettingsHeadingBrush 当前色值相同但键名独立，便于后续对运行状态做差异化配色。
        Resources["ConsoleHeadingBrush"] = new SolidColorBrush(
            isDark ? Colors.White : Color.FromRgb(0x1F, 0x1F, 0x1F));

        // 控制台页底部 5 个操作按钮（ConsoleActionTile01~05）：
        // · 底板：浅色纯白（写回 XAML 原硬编码值）/ 深色 #2B2B2B，与 SettingsCardBrush 深色值同源，
        //   纯黑会与 #1F2021 页底板融为一体。
        // · 前景（文字 + 📋📤🔁⏹️ emoji + Tile05 顶层矢量三角）：浅色 #1F1F1F / 深色纯白。
        //   WPF 不渲染 COLR 彩色字形，这些 emoji 按单色字形绘制，故受 Foreground 控制。
        // · Tile05 三层叠加图标的中层遮挡层同样引用 ConsoleTileBrush，与按钮底色永远一致。
        // · 悬浮内描边：浅色写回 XAML 原硬编码 #CCD0D3；深色收敛为 #3F3F3F
        //   （#CCD0D3 压在 #2B2B2B 底板上明度差过大，悬浮时会形成一圈刺眼亮环；
        //    #3F3F3F 与底板差约 20 级，能看出边界但不抢眼）。
        Resources["ConsoleTileBrush"] = new SolidColorBrush(
            isDark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Colors.White);
        Resources["ConsoleTileTextBrush"] = new SolidColorBrush(
            isDark ? Colors.White : Color.FromRgb(0x1F, 0x1F, 0x1F));
        Resources["ConsoleTileHoverBorderBrush"] = new SolidColorBrush(
            isDark ? Color.FromRgb(0x3F, 0x3F, 0x3F) : Color.FromRgb(0xCC, 0xD0, 0xD3));

        // 控制台运行情况大块：底板（ConsoleOutputPanel）+ 运行情况文字（ConsoleOutputTextBox）。
        // 浅色写回 XAML 原硬编码值（White / #1F1F1F），保证深→浅切换逐位复原；
        // 深色底板 #2B2B2B（与 SettingsCardBrush / ConsoleTileBrush 深色值同源），
        // 文字 #CCCCCC（纯白在深底上过亮刺眼，降到浅灰既不刺眼又能清晰阅读长篇日志）。
        Resources["ConsoleOutputPanelBrush"] = new SolidColorBrush(
            isDark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Colors.White);
        Resources["ConsoleOutputTextBrush"] = new SolidColorBrush(
            isDark ? Color.FromRgb(0xCC, 0xCC, 0xCC) : Color.FromRgb(0x1F, 0x1F, 0x1F));

        // 关于页 5 个链接按钮的超链接图标（HyperlinkIconImage）：
        // DrawingImage 属于 Freezable 且不在可视树内，无法响应 DynamicResource，
        // 因此 XAML 用 StaticResource 绑定同一 SolidColorBrush 实例，此处直接改其 Color。
        // 浅色写回 XAML 原硬编码 #333333，深色取 #E0E0E0（与深色主文字同亮度层级，不刺眼）。
        if (TryFindResource("AboutLinkIconBrush") is SolidColorBrush aboutLinkIconBrush
            && !aboutLinkIconBrush.IsFrozen)
        {
            aboutLinkIconBrush.Color = isDark
                ? Color.FromRgb(0xE0, 0xE0, 0xE0)
                : Color.FromRgb(0x33, 0x33, 0x33);
        }

        // 关于页主卡片中文副文案（AboutHeaderSubtitleText，两行中文说明）：
        // 浅色写回 XAML 改动前原硬编码 Black（#000000，注意不是 #1F1F1F，
        // 故不复用 SettingsHeadingBrush，避免浅色模式渲染结果发生偏移）；深色改纯白。
        // 主卡片底色（AboutMainPanel）已复用 SettingsCardBrush（浅 White / 深 #2B2B2B），无需单独处理。
        // 英文行(#ABABAB)与版本号(White)本轮不纳入，保持原硬编码。
        Resources["AboutHeaderSubtitleBrush"] = new SolidColorBrush(
            isDark ? Colors.White : Colors.Black);

        // 关于页主卡片英文副文案（AboutHeaderSubtitleEnglishText，两行英文说明）：
        // 浅色永久锁死 XAML 原硬编码 #ABABAB（★禁止改成 #80000000 去复用 SettingsSubTextBrush，
        // 那样会让浅色模式这行变深，破坏"深→浅切换后逐位复原"铁律）；
        // 深色取 #52FFFFFF —— 与 SettingsSubTextBrush 深色值同源，使深色下与
        // 设置页功能描述行、关于页 5 按钮副文字三者视觉统一。
        Resources["AboutHeaderSubtitleEnglishBrush"] = new SolidColorBrush(
            isDark
                ? Color.FromArgb(0x52, 0xFF, 0xFF, 0xFF)
                : Color.FromRgb(0xAB, 0xAB, 0xAB));

        // 关于页教程区右下角浮动工具栏 2 个 24×24 按钮（中英切换 AboutTutorialLanguageToggleButton /
        // 复制 AboutTutorialCopyButton）的描边色。
        // 浅色写回 XAML 替换前原硬编码 #E8EBED（逐位复原，零回归）；
        // 深色取 #3F3F3F —— 与 ConsoleTileHoverBorderBrush 深色值同源，即关于页 5 个链接按钮
        // "悬浮描边"的深色值（用户明确要求"描边用那 5 个按钮的悬浮描边"）。
        // 注意：这两个小按钮的描边是常显（BorderThickness=1.33），不是 hover 才出现，
        // 只借用颜色值，显隐逻辑不变。
        // 底板未单独开键：XAML 已直接引用 SettingsCardBrush（浅 White / 深 #2B2B2B），
        // 与 5 个链接按钮底色永远一致（用户要求"底色用那 5 个链接按钮的底色"）。
        Resources["AboutTutorialToolButtonBorderBrush"] = new SolidColorBrush(
            isDark
                ? Color.FromRgb(0x3F, 0x3F, 0x3F)
                : Color.FromRgb(0xE8, 0xEB, 0xED));

        // 上述中英切换按钮内 "中"/"EN" 文字色（AboutTutorialLanguageToggleButtonText）。
        // 浅色 Black —— 该 TextBlock 改动前根本没写 Foreground，走 WPF 隐式默认黑 #FF000000，
        // 显式给 Black 后渲染结果逐位相同，零回归；深色改纯白。
        // 复制按钮的 📋 emoji 不纳入：WPF 按彩色 COLR 字形渲染，Foreground 不生效，无需切换。
        Resources["AboutTutorialToolButtonTextBrush"] = new SolidColorBrush(
            isDark ? Colors.White : Colors.Black);

        // 关于页教程区正文文本框 AboutTutorialTextBox 的 3 个画刷。
        // ★ 本轮只新增深色取值；浅色三项严格等于替换前 XAML 原硬编码值，浅色渲染逐位不变。
        //   底板：浅 #F8FBFE（原值）/ 深 #1C1C1C（用户指定的中性灰）
        //         ★ 原深色为 #171B1F（B 通道最高）→ 大面积色块被明显感知为"泛蓝"，用户否决，禁止回改。
        //           深色底板一律用三通道相等的中性灰。
        //   正文：浅 #1F1F1F（原值）/ 深 #CCCCCC（用户指定，与 ConsoleOutputTextBrush 深色同源，
        //         长篇文本在深底上用纯白过亮刺眼，#CCCCCC 已验证为可长时间阅读的档位）
        //   描边：浅 #18000000（原值，9% 黑极淡线）/ 深 #3F3F3F
        //         深色值刻意与 AboutTutorialToolButtonBorderBrush 深色同值（用户要求"描边和上面两个按钮一样"）。
        //         ★ 不直接复用那个键：它浅色是 #E8EBED，复用会把浅色淡线改成浅灰实色 → 浅色回归，禁止。
        //         两键深色同值属人工对齐，不会自动跟随；日后调按钮描边深色需人工决定是否同步本键。
        // TextBox.Template 内已是 {TemplateBinding Background/BorderBrush}，自动跟随，无需额外处理。
        Resources["AboutTutorialTextBoxBackgroundBrush"] = new SolidColorBrush(
            isDark
                ? Color.FromRgb(0x1C, 0x1C, 0x1C)
                : Color.FromRgb(0xF8, 0xFB, 0xFE));

        Resources["AboutTutorialTextBoxForegroundBrush"] = new SolidColorBrush(
            isDark
                ? Color.FromRgb(0xCC, 0xCC, 0xCC)
                : Color.FromRgb(0x1F, 0x1F, 0x1F));

        Resources["AboutTutorialTextBoxBorderBrush"] = new SolidColorBrush(
            isDark
                ? Color.FromRgb(0x3F, 0x3F, 0x3F)
                : Color.FromArgb(0x18, 0x00, 0x00, 0x00));

        // 关于页教程区左侧 10 个 Border 按钮的两个底板画刷（未激活 / 激活）。
        // ★ 浅色两项严格等于替换前硬编码值（White / #F8FBFE），浅色渲染逐位不变。
        //   未激活：浅 White（原 XAML 值）/ 深 #121212（用户指定；原纯黑 Black 过深已废弃）
        //   激活：  浅 #F8FBFE（原 C# 硬编码值）/ 深 #1C1C1C
        //           （与 AboutTutorialTextBoxBackgroundBrush 深色同值，选中块与右侧正文框底色统一；
        //             原 #171B1F 泛蓝已废弃，两处必须一起改，否则选中块与正文框出现色差）
        // 描边与中文主文字未单独开键：XAML 已分别复用
        //   AboutTutorialToolButtonBorderBrush（浅 #E8EBED 原值 / 深 #3F3F3F）
        //   SettingsHeadingBrush（浅 #1F1F1F 原值 / 深 White）
        Resources["AboutTutorialButtonBackgroundBrush"] = new SolidColorBrush(
            isDark ? Color.FromRgb(0x12, 0x12, 0x12) : Colors.White);

        Resources["AboutTutorialButtonActiveBackgroundBrush"] = new SolidColorBrush(
            isDark
                ? Color.FromRgb(0x1C, 0x1C, 0x1C)
                : Color.FromRgb(0xF8, 0xFB, 0xFE));

        // 关于页底部 Slogan 文字（AboutSlogenText，
        // "- Without imagination, we'd be like all those other poor dullards. -"）。
        // 浅 #1F1F1F —— 与替换前 XAML 硬编码逐位相同，浅色零回归；
        // 深 #CCCCCC —— 用户指定，刻意低于 SettingsHeadingBrush 深色的纯 White，
        //   使 Slogan 保持"落款 / 点睛"的次要层级（与 AboutTutorialTextBoxForegroundBrush 深色同值）。
        // ★ 该 TextBlock 未被任何 C# 代码引用（无 Foreground 本地属性赋值），
        //   因此只需在此更新资源键即可，无需像教程按钮那样额外重刷激活态。
        Resources["AboutSlogenBrush"] = new SolidColorBrush(
            isDark
                ? Color.FromRgb(0xCC, 0xCC, 0xCC)
                : Color.FromRgb(0x1F, 0x1F, 0x1F));

        // 启动页 · 概览 (Insights) 顶部统计磁贴 LaunchTile01 的三个键。
        // 作用对象：磁贴底色 + 4 个统计单元的 3 行同级文字（共 12 个 TextBlock）+ 3 条竖向分割线。
        // 浅色三项全部等于替换前 XAML 原硬编码（White / Black / #14000000），浅色渲染逐位不变。
        // ★ LaunchTile01 与 4 个数值 TextBlock 在 .cs 中仅被赋 .Text / .ToolTip，
        //   没有任何 Background/Foreground 本地属性赋值，故只需在此更新资源键，无需额外重刷。
        Resources["InsightsCardBackgroundBrush"] = new SolidColorBrush(
            isDark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Colors.White);
        Resources["InsightsCardTextBrush"] = new SolidColorBrush(
            isDark ? Colors.White : Colors.Black);
        // ★ 浅色 #14000000 是 8% 透明黑，必须用 FromArgb 保留 alpha；
        //   若误用 FromRgb 会退化成纯黑重线（本项目历史踩坑点，同 ExecutionHeaderDividerBrush）。
        Resources["InsightsCardDividerBrush"] = new SolidColorBrush(
            isDark
                ? Color.FromRgb(0x3F, 0x3F, 0x3F)
                : Color.FromArgb(0x14, 0x00, 0x00, 0x00));

        // LaunchTile01 内 4 组迷你折线图的坐标轴（X 轴 + Y 轴，共 8 条 Line）。
        // 浅色 #33000000 等于替换前 XAML 原硬编码（20% 黑），浅色渲染逐位不变。
        // ★ 深色为何用 33% 白而非 33% 灰（#335C5C5C，已废弃）：深色底为 #2B2B2B 系，
        //   半透明灰与底色明度差过小，叠出来的轴线几乎不可见；改为半透明白以"提亮"方式呈现，
        //   符合深色 UI 分割线/坐标轴的通行做法。
        // ★ 不透明度按用户要求完全不动：两个分支的 alpha 均固定为 0x33，只替换 RGB 部分
        //   （浅 000000 → 深 FFFFFF）。因此两侧都必须用 Color.FromArgb 四参重载，
        //   任一分支误写 FromRgb 都会丢掉 alpha，把淡灰轴线变成实色重线
        //   （本项目历史踩坑点，这是第 4 个半透明主题键）。
        // ★ 只作用于坐标轴，与 4 条折线本体（Execution*Sparkline 的品牌色 Stroke）无关：
        //   RenderSparkline() 仅写 path.Data，从不改 Stroke，折线颜色永不随主题变化。
        Resources["InsightsChartAxisBrush"] = new SolidColorBrush(
            isDark
                ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x33, 0x00, 0x00, 0x00));

        // 启动页「资源」区右侧 4 个目录按钮（LaunchTile10 根目录 / LaunchTile11 运行环境 /
        // LaunchTile12 应用目录 / LaunchTile13 数据目录），每块 3 处颜色共 12 处引用。
        // 三个键的浅色分支全部等于替换前 XAML 原硬编码值，浅色渲染逐位不变（本轮只新增深色取值）。
        //   底色   浅 White      → 深 #2B2B2B
        //   主文字 浅 Black      → 深 White
        //   副文字 浅 #B3000000  → 深 #52FFFFFF
        // ★ 副文字为何不复用 SettingsSubTextBrush：那个键浅色是 #80000000（50% 黑），
        //   本处原硬编码是 #B3000000（70% 黑），复用会让浅色变浅 —— 属浅色回归，禁止。
        //   两者深色刻意同值 #52FFFFFF（32% 白），属人工对齐，不会自动联动。
        // ★ 副文字两个分支都是半透明色，必须用 Color.FromArgb 四参重载：
        //   任一分支误写 FromRgb 都会丢 alpha，把半透明小字变成实色重字
        //   （本项目历史踩坑点，这是第 5 个半透明主题键）。
        // ★ 底色 / 主文字深浅值与 InsightsCardBackgroundBrush / InsightsCardTextBrush 恰好同值，
        //   但键名独立：日后单独调 LaunchTile01 统计卡配色时不应连带改动这 4 个目录按钮。
        // ★ LaunchTile10~13 在 .cs 中仅挂 MouseLeftButtonUp（OpenPortableFolder），
        //   无任何 Background/Foreground 本地属性赋值 => 纯 DynamicResource 即可，无需额外重刷。
        // ★ 4 个 Viewbox 内的文件夹矢量图标（Path Fill 红/绿/蓝/黄）按用户要求完全不纳入主题，
        //   一行未改；深浅两态都用原品牌色。
        Resources["LaunchFolderTileBackgroundBrush"] = new SolidColorBrush(
            isDark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Colors.White);
        Resources["LaunchFolderTileTextBrush"] = new SolidColorBrush(
            isDark ? Colors.White : Colors.Black);
        Resources["LaunchFolderTileSubTextBrush"] = new SolidColorBrush(
            isDark
                ? Color.FromArgb(0x52, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0xB3, 0x00, 0x00, 0x00));

        // 启动页「自动化模板」磁贴 LaunchTile07（底色 / 主文字 / 副文字，共 3 处引用）。
        // 浅色三项全部等于替换前 XAML 原硬编码（White / Black / #797979），浅色渲染零回归。
        // ★ 为何不复用上方 LaunchFolderTile* 三键：底色与主文字虽深浅同值，
        //   但副文字浅色不同（本处 #797979 不透明灰 vs 目录按钮 #B3000000 半透明黑），
        //   复用即浅色回归；且 4 个目录入口与这张模板卡属不同功能块，键名独立便于各自调整。
        // ★ 副文字仅深色侧带 alpha（#52FFFFFF，32% 白）：深色分支必须用 Color.FromArgb 四参重载；
        //   浅色 #797979 为不透明色，用 Color.FromRgb 即可（本仓库第 6 个含半透明取值的主题键）。
        // ★ LaunchTile07 在 .cs 中仅有 MouseLeftButtonUp（OpenUrl 到 n8n workflows 页），
        //   无任何 Background/Foreground 本地属性赋值 => 纯 DynamicResource 即可，无需额外重刷。
        // ★ Viewbox 内 n8n 立方体矢量图标（RadialGradientBrush #3E192C→#E02B75 + #EA4B71）
        //   按用户要求完全不纳入主题，一行未改，深浅两态均用原品牌色。
        // ★ 中文行 emoji 🤖 与文字共用同一个 Foreground，但 WPF 对 emoji 走彩色字形渲染，
        //   Foreground 对 🤖 不生效（与 📋 同结论）；深色下 🤖 保持原彩色、文字变白，属正常现象。
        Resources["LaunchTemplateTileBackgroundBrush"] = new SolidColorBrush(
            isDark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Colors.White);
        Resources["LaunchTemplateTileTextBrush"] = new SolidColorBrush(
            isDark ? Colors.White : Colors.Black);
        Resources["LaunchTemplateTileSubTextBrush"] = new SolidColorBrush(
            isDark
                ? Color.FromArgb(0x52, 0xFF, 0xFF, 0xFF)
                : Color.FromRgb(0x79, 0x79, 0x79));

        // EmojiCheckBoxStyle 勾选记号（🔲 未勾 / ☑️ 已勾）三键。
        // 受影响控件：LaunchTile09「文件夹访问授权」的 PathToggle03/04/05
        //   路径为空 => IsEnabled=False 不可用态；填了路径 => 可用态。
        // ★ 浅色三项 = 改造前实际渲染值，逐位复原、零回归：
        //   改造前模板 EmojiGlyph 未写 Foreground、PathToggle 祖先链也无 Foreground，
        //   走 WPF 隐式默认黑 #FF000000，故浅色显式 Colors.Black 与原渲染完全相同；
        //   不可用态原本只有 Opacity=0.35，故浅色 Opacity 仍锁 0.35。
        // ★ 为何 Opacity 也要做成资源键：深色不可用态要求精确呈现 #5C5C5C，
        //   沿用 0.35 会把它压到几乎不可见；直接删触发器则浅色不可用态由灰变纯黑（浅色回归）。
        //   两侧分开取值（浅 0.35 / 深 1.0）是同时满足两者的唯一做法。
        // ★ 深色不可用态取值修正记录：初版 #3F3F3F（25% 灰）实测压在深底上偏暗、
        //   与可用态白记号落差过大，已按用户指示上调为 #5C5C5C（36% 灰）。
        //   注意与 InsightsChartAxisBrush 的「深底禁用半透明黑/灰」教训区分：
        //   本键是不透明实色字形，靠灰阶抬亮即可，不需要换成半透明白。
        // ★ 三键均为不透明色，全部用 Color.FromRgb 即可，无 alpha 陷阱。
        // ★ 同用该样式的 ProxyEnabledCheckBox（LaunchTile06「启用」）不受影响：
        //   它写了本地 Foreground（LaunchTemplateTileTextBrush），本地属性优先级高于 Style Setter；
        //   且本文件从不设它的 IsEnabled，永不进入不可用态分支。
        // ★ PathToggle03/04/05 在本文件中仅有索引映射（GetFileAccessPermissionToggle），
        //   无 Foreground/Opacity 本地属性赋值 => 纯 DynamicResource 即可，无需额外重刷。
        Resources["EmojiCheckBoxGlyphBrush"] = new SolidColorBrush(
            isDark ? Colors.White : Colors.Black);
        Resources["EmojiCheckBoxDisabledGlyphBrush"] = new SolidColorBrush(
            isDark ? Color.FromRgb(0x5C, 0x5C, 0x5C) : Colors.Black);
        Resources["EmojiCheckBoxDisabledGlyphOpacity"] = isDark ? 1.0 : 0.35;

        // FolderPathTextBoxStyle 路径框两键（LaunchTile09「文件夹访问授权」的
        // PathInput03/04/05 三个只读路径框共用同一 Style，一改全覆盖）。
        // ★ 浅色两项 = 改造前 Style Setter 原字面值（White / #33000000），逐位复原、零回归。
        // ★ 描边浅色必须用 Color.FromArgb(0x33, 0, 0, 0) 保留 alpha：
        //   #33000000 是 20% 透明黑（极淡灰线），若误写成 FromRgb(0x33,0x33,0x33)
        //   会变成实心深灰重线 => 浅色可见回归。（本仓库第 7 个含半透明取值的主题键）
        // ★ 深色底 #2B2B2B / 描边 #3F3F3F 与设置页卡片、控制台磁贴深色值人工对齐，
        //   但键名独立，日后单独调整不会互相牵连。
        // ★ 模板内 Border 用 TemplateBinding 转发 Background/BorderBrush，模板无需改动。
        // ★ 三个用点在本文件中无 Background/BorderBrush 本地属性赋值
        //   => 纯 DynamicResource 即可，无需额外重刷。
        Resources["FolderPathTextBoxBackgroundBrush"] = new SolidColorBrush(
            isDark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Colors.White);
        Resources["FolderPathTextBoxBorderBrush"] = new SolidColorBrush(
            isDark ? Color.FromRgb(0x3F, 0x3F, 0x3F) : Color.FromArgb(0x33, 0x00, 0x00, 0x00));

        // 路径文字两键（同为 PathInput03/04/05 服务，按「未锁定 / 锁定」两态分别取色；
        // 锁定态 = 对应 PathToggle 勾选且路径非空，见 UpdateFileAccessPathInputVisualState）。
        // · 未锁定 FolderPathTextBoxForegroundBrush       浅 Black      / 深 White
        // · 锁定   FolderPathTextBoxLockedForegroundBrush 浅 #80000000  / 深 #5C5C5C（用户指定）
        // ★ 浅色两项 = 改造前原字面值（XAML 本地 Foreground="Black" 与旧字段
        //   FileAccessPathInputLockedBrush = #80000000），逐位复原、零回归。
        // ★ 锁定态浅色必须用 Color.FromArgb(0x80, 0, 0, 0) 保留 alpha：
        //   #80000000 是 50% 透明黑（透出白底后呈中灰），误写 FromRgb(0x80,0x80,0x80)
        //   会变成实心灰 => 浅色可见回归。（本仓库第 8 个含半透明取值的主题键）
        // ★ 深色锁定值 #5C5C5C 与 EmojiCheckBoxDisabledGlyphBrush 深色同值属人工对齐，
        //   键名独立，日后单独调勾选框不可用态不会连带影响路径文字。
        // ★ 这两个键无需在此额外回刷视觉态：UpdateFileAccessPathInputVisualState 用的是
        //   SetResourceReference（资源引用表达式），Resources 更新后 WPF 自动重算。
        Resources["FolderPathTextBoxForegroundBrush"] = new SolidColorBrush(
            isDark ? Colors.White : Colors.Black);
        Resources["FolderPathTextBoxLockedForegroundBrush"] = new SolidColorBrush(
            isDark ? Color.FromRgb(0x5C, 0x5C, 0x5C) : Color.FromArgb(0x80, 0x00, 0x00, 0x00));

        // FolderBrowseIconButtonStyle 浏览按钮「圆角矩形填充」单键
        // （LaunchTile09「文件夹访问授权」的 BrowsePathButton03/04/05 三个图标按钮共用同一 Style）。
        // · 浅 #FFFFF3D6（= 改造前 Style Setter 原字面值，逐位复原、零回归）
        // · 深 #FF80796B（用户指定）
        // ★ 两侧均为不透明色，用 Color.FromRgb 即可，无 alpha 陷阱。
        // ★ 本轮只改填充：描边 #FFF9BC0F、按下态 #FFFFE8A8、图标 FileYellowIconImage(#F9BC0F)
        //   仍是 XAML 里的硬编码浅色，不纳入主题（用户明确要求「其他的不变」）。
        // ★ 禁止对 #FFF3D6 做全局替换：该字面值另有 3 处属别的控件
        //   （ConsoleActionTileBorderStyle 悬浮色、NodeModulesOverlayButtonStyle 悬浮/按下色）。
        // ★ 三个用点在 XAML/本文件中均无 Background 本地属性赋值
        //   => 纯 DynamicResource 即可，无需额外重刷。
        Resources["FolderBrowseButtonBackgroundBrush"] = new SolidColorBrush(
            isDark ? Color.FromRgb(0x80, 0x79, 0x6B) : Color.FromRgb(0xFF, 0xF3, 0xD6));

        // LauncherPlainTextBoxStyle 简洁数字输入框三键（共 4 个用点，一改全覆盖）：
        //   · ProxyPortInput                    启动页 LaunchTile06「代理端口」
        //   · SettingsSleepTimerDaysTextBox     设置页 睡眠定时 d
        //   · SettingsSleepTimerHoursTextBox    设置页 睡眠定时 h
        //   · SettingsSleepTimerMinutesTextBox  设置页 睡眠定时 min
        // · 底色   浅 White      / 深 #2B2B2B（用户指定）
        // · 描边   浅 #33000000  / 深 #3F3F3F（用户指定）
        // · 文字   浅 Black      / 深 White  （用户指定）
        // ★ 浅色三项 = 改造前 Style Setter 原字面值，逐位复原、零回归。
        // ★ 描边浅色必须用 Color.FromArgb(0x33, 0, 0, 0) 保留 alpha：
        //   #33000000 是 20% 透明黑（极淡灰线），若误写成 FromRgb(0x33,0x33,0x33)
        //   会变成实心深灰重线 => 浅色可见回归。（本仓库第 9 个含半透明取值的主题键）
        // ★ 深色底 #2B2B2B / 描边 #3F3F3F 与 FolderPathTextBox* 深色同值属人工对齐，
        //   键名独立：日后单独调 LaunchTile09 路径框配色不应连带改动这 4 个数字输入框。
        // ★ 4 个用点在 XAML 与本文件中均无 Background/BorderBrush/Foreground 本地属性赋值
        //   （本文件只动 .Text / .IsEnabled / .Opacity）=> 纯 DynamicResource 即可，无需额外重刷。
        // ★ 该 Style 没有 IsEnabled=False 触发器、自定义 ControlTemplate 也屏蔽了 WPF 默认禁用灰底，
        //   故睡眠定时 3 框在计时激活时（UpdateSleepTimer* 里置 IsEnabled=false）禁用态与常态外观相同，
        //   属改造前既有行为，本轮不引入禁用态配色。
        Resources["LauncherPlainTextBoxBackgroundBrush"] = new SolidColorBrush(
            isDark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Colors.White);
        Resources["LauncherPlainTextBoxBorderBrush"] = new SolidColorBrush(
            isDark ? Color.FromRgb(0x3F, 0x3F, 0x3F) : Color.FromArgb(0x33, 0x00, 0x00, 0x00));
        Resources["LauncherPlainTextBoxForegroundBrush"] = new SolidColorBrush(
            isDark ? Colors.White : Colors.Black);

        // 设置页「时区选择 (Timezone Selection)」下拉列表第 0 项 —— 时区搜索输入框三键。
        // 用点（全部在 AddTimezoneSearchBoxItem 里动态创建）：
        //   · TimezoneSearchBoxBackgroundBrush 外层 border.Background   浅 White    / 深 #2B2B2B
        //   · TimezoneSearchBoxBorderBrush     外层 border.BorderBrush  浅 #F0F0F0  / 深 #3F3F3F
        //   · TimezoneSearchBoxForegroundBrush textBox.Foreground
        //                                      + textBox.CaretBrush     浅 #1F1F1F  / 深 White
        // ★ 浅色三项 = 改造前 C# 里的原字面值，逐位复原、零回归。
        // ★ 文字与光标共用同一个键：深底上若只换文字色会留下几乎不可见的黑色光标。
        // ★ 三键两侧全是不透明 6 位 RGB，用 Color.FromRgb / Colors 即可，无 alpha 陷阱。
        // ★ 搜索框由 AddTimezoneSearchBoxItem 用纯 C# new 出来，且 PopulateTimezoneSelector()
        //   只在读取设置时调一次 —— 切主题不会重建它。因此那边必须用 SetResourceReference 挂键，
        //   Resources[键] 在此更新后表达式自动重算，无需额外回刷或重建下拉项。
        // ★ 为何不复用 LauncherPlainTextBox* 三键：那组浅色描边 #33000000（20% 透明黑）、
        //   浅色文字纯 Black，与本组 #F0F0F0 / #1F1F1F 不同，复用会造成浅色可见回归。
        //   深色 #2B2B2B / #3F3F3F 与之同值属人工对齐，键名独立、可各自单独调整。
        // ★ 刻意不纳入主题（用户明确要求保持原样）：
        //   占位提示文字 "输入时区 (Enter timezone)" 的 #9E9E9E（深浅两色下都可读）；
        //   textBox.Background = Brushes.Transparent（底色由外层 border 提供，改则双层叠色）。
        Resources["TimezoneSearchBoxBackgroundBrush"] = new SolidColorBrush(
            isDark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Colors.White);
        Resources["TimezoneSearchBoxBorderBrush"] = new SolidColorBrush(
            isDark ? Color.FromRgb(0x3F, 0x3F, 0x3F) : Color.FromRgb(0xF0, 0xF0, 0xF0));
        Resources["TimezoneSearchBoxForegroundBrush"] = new SolidColorBrush(
            isDark ? Colors.White : Color.FromRgb(0x1F, 0x1F, 0x1F));

        // 启动页「运行 n8n」LaunchTile02、「停止运行 (Stop)」LaunchTile03
        // 与「重新启动 (Restart)」LaunchTile04 三块动作磁贴。
        // （Tile03/04 先接入，Tile02 于后续一轮补齐；两键取值自始未变。）
        // · 底色 LaunchActionTileBackgroundBrush 浅 White（原硬编码） / 深 #2B2B2B（用户指定）
        // · 文字 LaunchActionTileTextBrush       浅 Black（原硬编码） / 深 White（用户指定）
        // ★ 浅色两项 = 改造前 Border.Background="White" / TextBlock.Foreground="Black"，逐位复原、零回归。
        // ★ 两块磁贴共用同一对键（同一功能组，视觉必须永远一致），不与其它控件复用。
        // ★ 底色可见的前提：Stop.png / restart.png 为半透明 PNG（已确认），
        //   满幅 Stretch=Fill 覆在 Border 上但四周透明，故 Border 底色能透出来。
        // ★ 两侧均为不透明 6 位 RGB，用 Color.FromRgb / Colors 即可，无 alpha 陷阱。
        // ★ LaunchTile03/04 在本文件中仅被赋 IsEnabled / IsHitTestVisible / Cursor / ToolTip，
        //   无任何画刷本地属性赋值 => 纯 DynamicResource 即可，无需额外重刷。
        // ★ 灰度禁用图、渐变环、悬浮/按下态逻辑本轮一律不动。
        Resources["LaunchActionTileBackgroundBrush"] = new SolidColorBrush(
            isDark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Colors.White);
        Resources["LaunchActionTileTextBrush"] = new SolidColorBrush(
            isDark ? Colors.White : Colors.Black);

        // ★ 上面两个键换新画刷后必须重刷一次激活态：
        //   SetActiveTutorialButton 给 Border.Background 赋的是"本地属性"，
        //   优先级高于 DynamicResource，不会随 Resources 更新自动变色。
        //   窗口 Loaded 之前（XAML 控件尚未建好）跳过，由 InitializeTutorialButtonActiveState 负责首刷。
        if (AboutTutorialPackageIntroButton is not null)
        {
            SetActiveTutorialButton(_activeTutorialButton);
        }

        // 执行记录页右上角刷新磁贴（ExecutionRecordsRefreshTile 的内层可见底板）。
        // 浅色 White —— 与替换前 XAML 原硬编码逐位相同，浅色零回归；
        // 深色 #2B2B2B —— 用户指定值（与 SettingsCardBrush / ConsoleTileBrush 深色恰好同值，
        //   但键名独立，日后单独调控制台 5 个操作磁贴的深色底不会连带影响本按钮）。
        // ★ 按钮内 🔄 emoji 的 Foreground="#0080FF" 按用户明确要求不纳入主题切换，保持原硬编码。
        // ★ 外层 Border（Background="Transparent" + MouseLeftButtonUp）不动：
        //   它只负责命中区，同步时 IsEnabled=false / Opacity=0.62 的逻辑不受本改动影响。
        // ★ 同页左上角标题 ExecutionRecordsTitleText 不在此处理：
        //   XAML 已改为复用 SettingsHeadingBrush（浅 #1F1F1F 原值 / 深 White），无需新增分支。
        Resources["ExecutionRefreshTileBrush"] = new SolidColorBrush(
            isDark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Colors.White);

        // 执行记录页表格（4 个键）。★ 浅色四项严格等于改造前硬编码值，浅色渲染逐位不变。
        // · 底板 ExecutionTablePanelBrush（ExecutionRecordsPanel）：
        //     浅 White（原值）/ 深 #2B2B2B（用户指定"整体底色"）。
        //     偶数行 Background 恒为 Transparent，透出的正是本底板 —— 所以"整体底色"只改这一处。
        // · 斑马 ExecutionRowAltBrush（奇数行）：浅 #EEF4F9（原值，实色无 alpha）/ 深 #1F2021（用户指定）。
        //     行是 C# 动态 new 的 Grid，CreateExecutionRecordRow 里用 SetResourceReference 挂本键，
        //     切主题时已渲染的行会自动重刷，无需重建列表。
        // · 表头文字 ExecutionHeaderTextBrush（Workflow / Status / Started / Run time / Exec. ID 共 5 个 TextBlock）：
        //     浅 #1F1F1F（原值）/ 深 White。
        //     ★ 不复用 SettingsHeadingBrush（两者当前深浅值恰好相同）：键名独立，
        //       日后调设置页标题色不会连带改动表头。
        // · 单元格文字 ExecutionCellTextBrush（Started / Run time / Exec. ID 三列）：
        //     浅 Black（原值，即 Brushes.Black）/ 深 White。
        //     这三列由 CreateExecutionThemedTextCell 挂动态引用；
        //     Workflow 列链接蓝 #256BEB 与 Status 列状态色（绿/红/蓝）不纳入，保持原硬编码。
        // ★ 表头下分割线 #14000000、排序 V 形指示器 Stroke=#9E9E9E、状态栏 #80000000、
        //   两个筛选 Popup 的白底与 #F5F5F5 斑马本轮不动。
        Resources["ExecutionTablePanelBrush"] = new SolidColorBrush(
            isDark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Colors.White);

        Resources["ExecutionRowAltBrush"] = new SolidColorBrush(
            isDark ? Color.FromRgb(0x1F, 0x20, 0x21) : Color.FromRgb(0xEE, 0xF4, 0xF9));

        Resources["ExecutionHeaderTextBrush"] = new SolidColorBrush(
            isDark ? Colors.White : Color.FromRgb(0x1F, 0x1F, 0x1F));

        Resources["ExecutionCellTextBrush"] = new SolidColorBrush(
            isDark ? Colors.White : Colors.Black);

        // · 表头下分割线 ExecutionHeaderDividerBrush（列标题与数据区之间的 1 DIP 细线）：
        //     浅 #14000000（原值，8% 透明黑，叠在白底板上≈#EBEBEB）/
        //     深 #3F3F3F（不透明；8% 黑压在 #2B2B2B 底板上几乎不可见，故深色改用不透明灰）。
        //   ★ 浅色分支必须用 Color.FromArgb 保留 alpha=0x14，若误用 FromRgb 会退化成纯黑实线（浅色回归）。
        Resources["ExecutionHeaderDividerBrush"] = new SolidColorBrush(
            isDark ? Color.FromRgb(0x3F, 0x3F, 0x3F)
                   : Color.FromArgb(0x14, 0x00, 0x00, 0x00));

        // · 启动页「控制 / 资源」两区右侧竖分割线 LaunchSectionDividerBrush
        //   （ControlsDivider01 / ControlsDivider02 两根线共用一个键）：
        //     浅 #14000000（8% 透明黑，叠白底≈#EBEBEB）/ 深 #3F3F3F（不透明灰）。
        //   取值方案与紧邻上方的 ExecutionHeaderDividerBrush 完全一致（人工对齐，键名独立）。
        //   ★ 改造前两根线是 Background="#0080ff" + Opacity="0.3"（蓝调线）且此处零赋值，
        //     深色模式下从未适配；本轮统一为灰调分割线，XAML 里的 Opacity="0.3" 已同步删除
        //     （8% alpha 再乘 0.3 只剩 2.4%，两种主题下分割线都会消失）。
        //   ★ 浅色分支必须用 Color.FromArgb 保留 alpha=0x14，若误用 FromRgb 会退化成纯黑实线。
        Resources["LaunchSectionDividerBrush"] = new SolidColorBrush(
            isDark ? Color.FromRgb(0x3F, 0x3F, 0x3F)
                   : Color.FromArgb(0x14, 0x00, 0x00, 0x00));

        // 执行记录页 Workflow / Status 表头筛选下拉框（两个 Popup 共用同一套 6 个键）。
        // ★ 浅色六项严格等于改造前硬编码值，浅色渲染逐位不变。
        // · ExecutionFilterPopupBrush（两个 Popup 外框 Border.Background）：
        //     浅 White（原值）/ 深 #2B2B2B。
        // · ExecutionFilterPopupBorderBrush（两个 Popup 外框 Border.BorderBrush）：
        //     浅 #22000000（原值，13% 透明黑）/ 深 #3F3F3F（不透明）。
        //   ★ 浅色分支必须用 Color.FromArgb 保留 alpha=0x22 —— 与上一轮表头分割线同一个坑，
        //     误用 FromRgb 会退化成纯黑实线，属浅色回归。
        // · ExecutionFilterRowEvenBrush（偶数行）：浅 White（原值）/ 深 #2B2B2B。
        //     深色下与外框底同值，视觉上表现为"隔行透空"，与浅色 White + #F5F5F5 的弱分隔观感等价。
        // · ExecutionFilterRowOddBrush（奇数行斑马）：浅 #F5F5F5（原值）/ 深 #242424。
        // · ExecutionFilterTextBrush（条目文字，含"全部"项）：浅 Black（原值 Brushes.Black）/ 深 White。
        // · ExecutionFilterRowHoverBrush（悬停底色）：浅 #E6EEF8（原值）/ 深 #1F2021（用户指定）。
        // ★ 这些行由 CreateExecutionFilterOptionRow 动态创建，全部走 SetResourceReference，
        //   切主题时（包括 hover 过又离开的行）会自动重刷，无需重建下拉内容。
        Resources["ExecutionFilterPopupBrush"] = new SolidColorBrush(
            isDark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Colors.White);

        Resources["ExecutionFilterPopupBorderBrush"] = new SolidColorBrush(
            isDark ? Color.FromRgb(0x3F, 0x3F, 0x3F)
                   : Color.FromArgb(0x22, 0x00, 0x00, 0x00));

        Resources["ExecutionFilterRowEvenBrush"] = new SolidColorBrush(
            isDark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Colors.White);

        Resources["ExecutionFilterRowOddBrush"] = new SolidColorBrush(
            isDark ? Color.FromRgb(0x24, 0x24, 0x24) : Color.FromRgb(0xF5, 0xF5, 0xF5));

        Resources["ExecutionFilterTextBrush"] = new SolidColorBrush(
            isDark ? Colors.White : Colors.Black);

        Resources["ExecutionFilterRowHoverBrush"] = new SolidColorBrush(
            isDark ? Color.FromRgb(0x1F, 0x20, 0x21) : Color.FromRgb(0xE6, 0xEE, 0xF8));

        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
            isDark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light);

        // 切主题时 Wpf.Ui 重建 ResourceDictionary，DWM 合成被震掉。
        // 先切到 None 再切回 Acrylic，强制 FluentWindow 内部重新调用 DWMWA。
        // （直接设 Acrylic 时 DependencyProperty 值未变，setter 不会触发 DWM 调用。）
        // ContextIdle 是 WPF 最低优先级，确保 Wpf.Ui 内部同步操作完成后再设。
        if (!lowPerformanceMode)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;
                WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.Acrylic;
            }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        // 三层云颜色：双套 DrawingImage（Light/Dark 各 3 个，定义在 CloudArt.xaml），
        // 切换 Image.Source 即可，无需改 SolidColorBrush.Color（StaticResource 已冻结，永远改不动）。
        // ★ 浅色硬编码色值（#B3D5F8 / #DBECFC / #F0F7FC），深色 Y-0.3 压暗（#86A0BB / #B3C1CE / #CACFD4）。
        // ★ 不碰 Rise/Sway TranslateTransform、圆角 Clip、火箭 z 序与三条超长 Geometry。
        LaunchCloudBack.Source = (DrawingImage)TryFindResource(isDark ? "LaunchCloudBackDark" : "LaunchCloudBackLight");
        LaunchCloudMid.Source = (DrawingImage)TryFindResource(isDark ? "LaunchCloudMidDark" : "LaunchCloudMidLight");
        LaunchCloudFront.Source = (DrawingImage)TryFindResource(isDark ? "LaunchCloudFrontDark" : "LaunchCloudFrontLight");

        // 刷新 TitleBar 按钮前景色：XAML 已绑定 DynamicResource，更新资源键即可。
        Resources["TitleBarButtonsForegroundBrush"] = new SolidColorBrush(
            isDark ? Colors.White : Color.FromRgb(0x1F, 0x1F, 0x1F));

        Debug.WriteLine($"[Theme] Updated: isDark={isDark}, lowPerformanceMode={lowPerformanceMode}");
    }
}
