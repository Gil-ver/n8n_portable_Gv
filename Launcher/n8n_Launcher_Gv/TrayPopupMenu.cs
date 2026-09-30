using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfColors = System.Windows.Media.Colors;
using WpfCursors = System.Windows.Input.Cursors;
using WpfKey = System.Windows.Input.Key;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfPanel = System.Windows.Controls.Panel;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace n8n_launcher_Gv;

/// <summary>
/// 简洁两层 WPF 托盘菜单：
/// - 保留 Windows Forms NotifyIcon，只替换菜单显示层。
/// - 只支持一级菜单 + 窗口缩放二级菜单，不做无限级递归。
/// - 二级比例菜单横向位置：以一级菜单面板边缘为基准（右/左各留 4 DIP 间隙），右侧放不下就翻到左侧，绝不压住一级菜单。
/// - 菜单独立于 AppDesignSurface，不参与主窗口 userScale 缩放。
/// </summary>
internal sealed class TrayPopupMenu : IDisposable
{
    // 150% DPI 下：一级菜单 110.5 DIP ≈ 166px（文字↔✓ 空档与二级「100% ✓」一致的 17.9 DIP）；二级菜单约 92 DIP ≈ 138px。
    private const double MainMenuWidth = 110.5;
    private const double ScaleMenuWidth = 92.0;
    // 二级比例菜单与一级菜单面板之间的水平间隙（单位 DIP，换算物理像素要乘 DPI）。
    // 150% DPI 下 4 DIP = 6px：能看出两块菜单是分开的，又不至于离得太远。
    private const double ScaleMenuGap = 4.0;
    // Windows Defender 风格：150% DPI 下 32 DIP ≈ 48 物理像素响应高度。
    private const double ItemHeight = 32.0;
    private const double MenuPadding = 4.0;
    private const double CornerRadius = 10.0;
    private const double ShadowDepth = 8.0;

    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;

    private readonly FrameworkElement _owner;
    private readonly Popup _mainPopup;
    private readonly Popup _scalePopup;
    private readonly DispatcherTimer _outsideClickTimer;

    private Border? _mainRoot;
    private Border? _scaleRoot;
    private bool _wasMouseDown;
    private bool _disposed;

    public event Action<double>? ScaleRequested;
    public event Action<bool>? StartupToggled;
    public event Action? ShowRequested;
    public event Action? OpenN8nWebRequested;
    public event Action? StartN8nRequested;
    public event Action? StopN8nRequested;
    public event Action? RestartN8nRequested;
    public event Action? ExitRequested;

    public double CurrentScale { get; private set; } = 1.0;

    public bool StartupEnabled { get; set; }

    public bool IsN8nRunning { get; set; }

    public bool IsOpen => _mainPopup.IsOpen || _scalePopup.IsOpen;

    public TrayPopupMenu(FrameworkElement owner)
    {
        _owner = owner;

        _mainPopup = new Popup
        {
            Placement = PlacementMode.AbsolutePoint,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.None,
            StaysOpen = true
        };

        _scalePopup = new Popup
        {
            Placement = PlacementMode.AbsolutePoint,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.None,
            StaysOpen = true
        };

        _outsideClickTimer = new DispatcherTimer(DispatcherPriority.Input, _owner.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        _outsideClickTimer.Tick += OutsideClickTimer_Tick;

        _owner.PreviewKeyDown += Owner_PreviewKeyDown;
    }

    public void UpdateScale(double scale)
    {
        CurrentScale = scale;

        if (IsOpen)
        {
            RebuildMenus();
        }
    }

    public void ShowAt(System.Drawing.Point physicalPoint)
    {
        if (_disposed) return;

        CloseAll();
        RebuildMenus();

        if (_mainRoot == null) return;

        _mainRoot.Measure(new WpfSize(double.PositiveInfinity, double.PositiveInfinity));
        var menuSize = _mainRoot.DesiredSize;
        var dipPoint = GetSafePopupDipPoint(physicalPoint, menuSize.Width, menuSize.Height);

        _mainPopup.HorizontalOffset = dipPoint.X;
        _mainPopup.VerticalOffset = dipPoint.Y;
        _mainPopup.IsOpen = true;

        _mainRoot.Focus();
        _outsideClickTimer.Start();

        Debug.WriteLine($"[TrayMenu] Open main at physical=({physicalPoint.X},{physicalPoint.Y}), dip=({dipPoint.X:F0},{dipPoint.Y:F0})");
    }

    public void CloseAll()
    {
        _scalePopup.IsOpen = false;
        _mainPopup.IsOpen = false;
        _outsideClickTimer.Stop();
        _wasMouseDown = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CloseAll();
        _owner.PreviewKeyDown -= Owner_PreviewKeyDown;
        _outsideClickTimer.Tick -= OutsideClickTimer_Tick;
    }

    private void RebuildMenus()
    {
        _mainRoot = BuildMainMenu();
        _scaleRoot = BuildScaleMenu();

        _mainPopup.Child = _mainRoot;
        _scalePopup.Child = _scaleRoot;
    }

    private Border BuildMainMenu()
    {
        bool dark = IsSystemDarkMode();
        var panel = CreateMenuPanel();

        // 「显示界面」提到最上方（比例切换之上）：托盘最常用的动作，单手即可点到。
        AddActionItem(panel, "显示界面", dark, () => ShowRequested?.Invoke());

        var scaleItem = CreateMenuItem("比例切换", "›", dark, null);
        scaleItem.MouseEnter += (_, _) => OpenScaleMenuFrom(scaleItem);
        panel.Children.Add(scaleItem);

        AddStartupItem(panel, dark);
        panel.Children.Add(CreateSeparator(dark));
        AddActionItem(panel, "重启n8n", dark, () => RestartN8nRequested?.Invoke(), enabled: IsN8nRunning);
        AddActionItem(panel, "中止n8n", dark, () => StopN8nRequested?.Invoke(), enabled: IsN8nRunning);
        AddActionItem(panel, "启动n8n", dark, () => StartN8nRequested?.Invoke(), enabled: !IsN8nRunning);
        AddActionItem(panel, "n8n网页", dark, () => OpenN8nWebRequested?.Invoke());
        panel.Children.Add(CreateSeparator(dark));
        AddActionItem(panel, "彻底退出", dark, () => ExitRequested?.Invoke());

        return CreateMenuRoot(panel, MainMenuWidth, dark);
    }

    private void AddActionItem(WpfPanel panel, string text, bool dark, Action action, bool enabled = true)
    {
        if (!enabled)
        {
            var disabled = CreateDisabledMenuItem(text, dark);
            disabled.MouseEnter += (_, _) => _scalePopup.IsOpen = false;
            panel.Children.Add(disabled);
            return;
        }

        var item = CreateMenuItem(text, null, dark, () =>
        {
            CloseAll();
            action();
        });
        item.MouseEnter += (_, _) => _scalePopup.IsOpen = false;
        panel.Children.Add(item);
    }

    private Border CreateDisabledMenuItem(string text, bool dark)
    {
        var disabledForeground = new SolidColorBrush(dark ? WpfColor.FromRgb(0x60, 0x63, 0x68) : WpfColor.FromRgb(0xB5, 0xB5, 0xB5));

        var grid = new Grid
        {
            Height = ItemHeight,
            Margin = new Thickness(0)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = text,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0),
            Foreground = disabledForeground
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        return new Border
        {
            MinHeight = ItemHeight,
            CornerRadius = new CornerRadius(6),
            Background = WpfBrushes.Transparent,
            Cursor = WpfCursors.Arrow,
            Child = grid
        };
    }

    private void AddStartupItem(WpfPanel panel, bool dark)
    {
        var item = CreateMenuItem("开机启动", StartupEnabled ? "✓" : null, dark, () =>
        {
            bool targetEnabled = !StartupEnabled;
            CloseAll();
            StartupToggled?.Invoke(targetEnabled);
        }, selected: StartupEnabled);
        item.MouseEnter += (_, _) => _scalePopup.IsOpen = false;
        panel.Children.Add(item);
    }

    private Border BuildScaleMenu()
    {
        bool dark = IsSystemDarkMode();
        var panel = CreateMenuPanel();

        // 档位固定在整十百分比阶梯上（100% / 75% / 60% / 50% / 40%）：
        // 窗口尺寸 = 2160×1440 × 倍数，阈值按「目标屏工作区高度放得下且不浪费」标定：
        //   1.0 → 2160×1440、0.75 → 1620×1080、0.6 → 1296×864、0.5 → 1080×720、0.4 → 864×576。
        // 二级菜单宽 92 DIP，✓ 列边距压到 4/8 后选中行可用 40 DIP；最宽标签「100%」在系统默认字体（Microsoft YaHei UI 13 DIP）下实测 34.4 DIP，余量 5.6 DIP 不裁字。
        AddScaleItem(panel, "100%", 1.0, dark);
        AddScaleItem(panel, "75%", 0.75, dark);
        AddScaleItem(panel, "60%", 0.6, dark);
        AddScaleItem(panel, "50%", 0.5, dark);
        AddScaleItem(panel, "40%", 0.4, dark);

        return CreateMenuRoot(panel, ScaleMenuWidth, dark);
    }

    private void AddScaleItem(WpfPanel panel, string header, double scale, bool dark)
    {
        bool selected = IsSameScale(CurrentScale, scale);
        var item = CreateScaleMenuItem(header, selected, dark, () =>
        {
            CloseAll();
            ScaleRequested?.Invoke(scale);
        });

        panel.Children.Add(item);
    }

    private Border CreateMenuRoot(UIElement child, double width, bool dark)
    {
        var background = dark ? WpfColor.FromRgb(0x2B, 0x2D, 0x30) : WpfColors.White;
        var border = dark ? WpfColor.FromRgb(0x48, 0x4A, 0x4D) : WpfColor.FromRgb(0xD8, 0xD8, 0xD8);

        return new Border
        {
            Width = width,
            Padding = new Thickness(MenuPadding),
            Background = new SolidColorBrush(background),
            BorderBrush = new SolidColorBrush(border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(CornerRadius),
            SnapsToDevicePixels = true,
            Focusable = true,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = ShadowDepth,
                Direction = 270,
                Opacity = dark ? 0.45 : 0.22,
                Color = WpfColors.Black
            },
            Child = child
        };
    }

    private static StackPanel CreateMenuPanel()
    {
        return new StackPanel
        {
            Orientation = WpfOrientation.Vertical,
            Background = WpfBrushes.Transparent
        };
    }

    private Border CreateScaleMenuItem(string text, bool selected, bool dark, Action clickAction)
    {
        var normalBg = WpfBrushes.Transparent;
        var hoverBg = new SolidColorBrush(dark ? WpfColor.FromRgb(0x3A, 0x3D, 0x42) : WpfColor.FromRgb(0xF0, 0xF4, 0xFA));
        var foreground = new SolidColorBrush(dark ? WpfColors.White : WpfColor.FromRgb(0x1F, 0x1F, 0x1F));
        var checkForeground = new SolidColorBrush(WpfColor.FromRgb(0x4C, 0x9D, 0xFF));

        var grid = new Grid
        {
            Height = ItemHeight,
            Margin = new Thickness(0)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = text,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 8, 0),
            Foreground = foreground
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var check = new TextBlock
        {
            Text = selected ? "✓" : string.Empty,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(4, 0, 8, 1),
            Foreground = checkForeground
        };
        Grid.SetColumn(check, 1);
        grid.Children.Add(check);

        var item = new Border
        {
            MinHeight = ItemHeight,
            CornerRadius = new CornerRadius(6),
            Background = normalBg,
            Cursor = WpfCursors.Hand,
            Child = grid,
            Tag = normalBg
        };

        item.MouseEnter += (_, _) => item.Background = hoverBg;
        item.MouseLeave += (_, _) => item.Background = item.Tag as WpfBrush ?? normalBg;
        item.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            clickAction();
        };

        return item;
    }

    // 标记列（✓ / ›）边缘参数刻意与二级比例菜单 CreateScaleMenuItem 对齐：文字↔标记间隙 = 8(label 右) + 4(标记左) = 12 DIP，
    // 标记右侧留白 8 DIP；主菜单外宽由 MainMenuWidth 固定 110.5 DIP，文字↔标记的视觉空档由该宽度决定（调上面两处边缘参数不会改变它）。
    private Border CreateMenuItem(string text, string? trailingText, bool dark, Action? clickAction, bool selected = false)
    {
        var normalBg = WpfBrushes.Transparent;
        var hoverBg = new SolidColorBrush(dark ? WpfColor.FromRgb(0x3A, 0x3D, 0x42) : WpfColor.FromRgb(0xF0, 0xF4, 0xFA));
        var foreground = new SolidColorBrush(dark ? WpfColors.White : WpfColor.FromRgb(0x1F, 0x1F, 0x1F));
        var mutedForeground = new SolidColorBrush(dark ? WpfColor.FromRgb(0xC9, 0xD1, 0xD9) : WpfColor.FromRgb(0x5F, 0x63, 0x68));

        var grid = new Grid
        {
            Height = ItemHeight,
            Margin = new Thickness(0)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = text,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 8, 0),
            Foreground = foreground
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        if (!string.IsNullOrWhiteSpace(trailingText))
        {
            var trailing = new TextBlock
            {
                Text = trailingText,
                FontSize = trailingText == "✓" ? 14 : 17,
                FontWeight = trailingText == "✓" ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 8, 1),
                Foreground = selected ? new SolidColorBrush(WpfColor.FromRgb(0x4C, 0x9D, 0xFF)) : mutedForeground
            };
            Grid.SetColumn(trailing, 1);
            grid.Children.Add(trailing);
        }

        var item = new Border
        {
            MinHeight = ItemHeight,
            CornerRadius = new CornerRadius(6),
            Background = normalBg,
            Cursor = WpfCursors.Hand,
            Child = grid,
            Tag = normalBg
        };

        item.MouseEnter += (_, _) => item.Background = hoverBg;
        item.MouseLeave += (_, _) => item.Background = item.Tag as WpfBrush ?? normalBg;

        if (clickAction != null)
        {
            item.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                clickAction();
            };
        }

        return item;
    }

    private static Border CreateSeparator(bool dark)
    {
        return new Border
        {
            Height = 1,
            Margin = new Thickness(8, 5, 8, 5),
            Background = new SolidColorBrush(dark ? WpfColor.FromRgb(0x45, 0x48, 0x4D) : WpfColor.FromRgb(0xE6, 0xE6, 0xE6))
        };
    }

    private void OpenScaleMenuFrom(FrameworkElement parentItem)
    {
        if (_scaleRoot == null) return;

        _scaleRoot.Measure(new WpfSize(double.PositiveInfinity, double.PositiveInfinity));

        var dpi = GetDpiScale();
        var itemTopLeftPhysical = parentItem.PointToScreen(new WpfPoint(0, 0));

        // 二级菜单的位置一律以「一级菜单面板自身」的边缘为基准计算，不用菜单项边缘——
        // 菜单项在面板内还缩进了 1(边框)+4(内边距) DIP，用菜单项算必然压住一级菜单边缘。
        var mainTopLeftPhysical = _mainRoot != null
            ? _mainRoot.PointToScreen(new WpfPoint(0, 0))
            : itemTopLeftPhysical;
        double mainWidthDip = _mainRoot != null && _mainRoot.ActualWidth > 0
            ? _mainRoot.ActualWidth
            : MainMenuWidth;

        var screen = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)itemTopLeftPhysical.X, (int)itemTopLeftPhysical.Y));
        var workArea = screen.WorkingArea;

        // 全部换算成物理像素：DIP × DPI。间隙也必须乘 DPI，否则 150% 下只剩 2/3。
        double scaleWidthPhysical = _scaleRoot.DesiredSize.Width * dpi.X;
        double mainWidthPhysical = mainWidthDip * dpi.X;
        double gapPhysical = ScaleMenuGap * dpi.X;

        // 优先放一级菜单右侧（面板右边缘再往右留 gap）；
        // 右侧放不下（托盘贴屏幕右缘时必然放不下）就翻到左侧（面板左边缘再往左留 gap）。
        double xPhysical = mainTopLeftPhysical.X + mainWidthPhysical + gapPhysical;
        if (xPhysical + scaleWidthPhysical > workArea.Right)
        {
            xPhysical = mainTopLeftPhysical.X - gapPhysical - scaleWidthPhysical;
        }

        // 垂直方向仍与「比例切换」菜单项顶边对齐。
        double yPhysical = itemTopLeftPhysical.Y;

        var dipPoint = PhysicalToDip(new WpfPoint(xPhysical, yPhysical));
        _scalePopup.HorizontalOffset = dipPoint.X;
        _scalePopup.VerticalOffset = dipPoint.Y;
        _scalePopup.IsOpen = true;

        Debug.WriteLine($"[TrayMenu] Open scale at physical=({xPhysical:F0},{yPhysical:F0}), mainLeft={mainTopLeftPhysical.X:F0}, mainWidth={mainWidthPhysical:F0}, gap={gapPhysical:F0}");
    }

    private WpfPoint GetSafePopupDipPoint(System.Drawing.Point physicalPoint, double menuWidthDip, double menuHeightDip)
    {
        var screen = System.Windows.Forms.Screen.FromPoint(physicalPoint);
        var workArea = screen.WorkingArea;
        var dpi = GetDpiScale();

        double menuWidthPhysical = menuWidthDip * dpi.X;
        double menuHeightPhysical = menuHeightDip * dpi.Y;

        double x = physicalPoint.X;
        double y = physicalPoint.Y;

        if (x + menuWidthPhysical > workArea.Right)
            x = workArea.Right - menuWidthPhysical;
        if (y + menuHeightPhysical > workArea.Bottom)
            y = workArea.Bottom - menuHeightPhysical;

        x = Math.Max(workArea.Left, x);
        y = Math.Max(workArea.Top, y);

        return PhysicalToDip(new WpfPoint(x, y));
    }

    private WpfPoint PhysicalToDip(WpfPoint physicalPoint)
    {
        var dpi = GetDpiScale();
        return new WpfPoint(physicalPoint.X / dpi.X, physicalPoint.Y / dpi.Y);
    }

    private (double X, double Y) GetDpiScale()
    {
        var dpi = VisualTreeHelper.GetDpi(_owner);
        return (dpi.DpiScaleX, dpi.DpiScaleY);
    }

    private void OutsideClickTimer_Tick(object? sender, EventArgs e)
    {
        if (!IsOpen)
        {
            _outsideClickTimer.Stop();
            return;
        }

        bool mouseDown = IsMouseButtonDown();
        if (mouseDown && !_wasMouseDown)
        {
            var cursor = System.Windows.Forms.Cursor.Position;
            if (!IsPointInsideOpenPopups(cursor))
            {
                CloseAll();
                return;
            }
        }

        _wasMouseDown = mouseDown;
    }

    private bool IsPointInsideOpenPopups(System.Drawing.Point physicalPoint)
    {
        return IsPointInsidePopup(_mainRoot, physicalPoint) || IsPointInsidePopup(_scaleRoot, physicalPoint);
    }

    private bool IsPointInsidePopup(FrameworkElement? root, System.Drawing.Point physicalPoint)
    {
        if (root == null || !root.IsVisible || root.ActualWidth <= 0 || root.ActualHeight <= 0)
            return false;

        var topLeft = root.PointToScreen(new WpfPoint(0, 0));
        var dpi = GetDpiScale();
        double right = topLeft.X + root.ActualWidth * dpi.X;
        double bottom = topLeft.Y + root.ActualHeight * dpi.Y;

        return physicalPoint.X >= topLeft.X && physicalPoint.X <= right &&
               physicalPoint.Y >= topLeft.Y && physicalPoint.Y <= bottom;
    }

    private void Owner_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == WpfKey.Escape && IsOpen)
        {
            CloseAll();
            e.Handled = true;
        }
    }

    private static bool IsSameScale(double left, double right)
    {
        return Math.Abs(left - right) < 0.001;
    }

    private static bool IsMouseButtonDown()
    {
        return (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 ||
               (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;
    }

    private static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value != null && (int)value == 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
