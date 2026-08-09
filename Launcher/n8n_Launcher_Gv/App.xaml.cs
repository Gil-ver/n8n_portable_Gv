using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;

namespace n8n_launcher_Gv;

// 注意：不 using System.Windows.Controls，避免与 System.Windows.Forms.TextBox 冲突。
// 在下方处理器中显式使用 System.Windows.Controls.TextBox / MenuItem / ContextMenu。
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfComboBoxItem = System.Windows.Controls.ComboBoxItem;

public partial class App : System.Windows.Application
{
    private const string StartupTrayArgument = "--startup-tray";
    private static Mutex? _instanceMutex;
    private MainWindow? _mainWindow;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        // 单实例运行
        const string mutexName = "n8n_launcher_Gv_SingleInstance";
        _instanceMutex = new Mutex(true, mutexName, out bool createdNew);

        if (!createdNew)
        {
            ActivateExistingInstance();
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        // 全局右键菜单规则（一次注册，覆盖整个 App）：
        //   · 任何 TextBox：右键前自动挂上 App.xaml 里定义的资源菜单
        //       - IsReadOnly=true  → ReadOnlyTextBoxContextMenu（复制/全选）
        //       - IsReadOnly=false → TextBoxContextMenu（剪切/复制/粘贴/全选）
        //     已显式设置 ContextMenu 的 TextBox（例如控制台）尊重原设置，不覆盖。
        //     若 TextBox 是 ComboBox 内部的 PART_EditableTextBox 且 ComboBox 非可编辑 → 屏蔽右键（相当于 ComboBox 本身不该有菜单）。
        //   · ComboBox 及其内部 ComboBoxItem：右键直接屏蔽，不弹出任何菜单。
        //   新增的 TextBox / ComboBox 未来无需再手动挂资源，自动生效。
        EventManager.RegisterClassHandler(
            typeof(WpfTextBox),
            FrameworkElement.ContextMenuOpeningEvent,
            new ContextMenuEventHandler(OnGlobalTextBoxContextMenuOpening));
        EventManager.RegisterClassHandler(
            typeof(WpfComboBox),
            FrameworkElement.ContextMenuOpeningEvent,
            new ContextMenuEventHandler(OnGlobalComboBoxContextMenuOpening));
        EventManager.RegisterClassHandler(
            typeof(WpfComboBoxItem),
            FrameworkElement.ContextMenuOpeningEvent,
            new ContextMenuEventHandler(OnGlobalComboBoxItemContextMenuOpening));

        bool startHiddenToTray = e.Args.Any(arg => string.Equals(arg, StartupTrayArgument, StringComparison.OrdinalIgnoreCase));
        _mainWindow = new MainWindow(startHiddenToTray);
        _mainWindow.Closed += (s, args) =>
        {
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
            _instanceMutex = null;
        };

        _mainWindow.Show();
    }

    private static void ActivateExistingInstance()
    {
        var current = Process.GetCurrentProcess();
        var processes = Process.GetProcessesByName(current.ProcessName);

        foreach (var process in processes)
        {
            if (process.Id != current.Id)
            {
                var hwnd = process.MainWindowHandle;
                if (hwnd != IntPtr.Zero)
                {
                    ShowWindow(hwnd, 9); // SW_RESTORE
                    SetForegroundWindow(hwnd);
                }
                break;
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    // ─────────────────────────────────────────────────────────────────────────
    // 全局 TextBox 右键菜单事件处理
    // App.xaml 里定义的 TextBoxContextMenu / ReadOnlyTextBoxContextMenu 使用。
    // 通过 sender → MenuItem → 所属 ContextMenu → PlacementTarget 找回目标 TextBox。
    // 这样一套处理器可覆盖启动器中所有引用了本资源的 TextBox。
    // ─────────────────────────────────────────────────────────────────────────
    private static WpfTextBox? ResolveTargetTextBox(object sender)
    {
        if (sender is not WpfMenuItem menuItem) return null;

        // MenuItem 可能被嵌套在 Separator/其他 MenuItem 下，向上找到根 ContextMenu 即可。
        DependencyObject? current = menuItem;
        while (current is not null)
        {
            if (current is WpfContextMenu cm)
            {
                return cm.PlacementTarget as WpfTextBox;
            }
            current = System.Windows.Media.VisualTreeHelper.GetParent(current)
                     ?? System.Windows.LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private void TextBoxContextMenu_Cut_Click(object sender, RoutedEventArgs e)
    {
        var tb = ResolveTargetTextBox(sender);
        if (tb is null) return;
        if (tb.IsReadOnly || tb.SelectionLength == 0) return;
        tb.Cut();
    }

    private void TextBoxContextMenu_Copy_Click(object sender, RoutedEventArgs e)
    {
        var tb = ResolveTargetTextBox(sender);
        if (tb is null) return;
        // 未选中文字时复制整段（更符合"随手右键复制控制台内容"这类场景）。
        if (tb.SelectionLength > 0)
        {
            tb.Copy();
        }
        else if (!string.IsNullOrEmpty(tb.Text))
        {
            try { System.Windows.Clipboard.SetText(tb.Text); } catch { /* 忽略剪贴板异常 */ }
        }
    }

    private void TextBoxContextMenu_Paste_Click(object sender, RoutedEventArgs e)
    {
        var tb = ResolveTargetTextBox(sender);
        if (tb is null) return;
        if (tb.IsReadOnly) return;
        if (!System.Windows.Clipboard.ContainsText()) return;
        tb.Paste();
    }

    private void TextBoxContextMenu_SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var tb = ResolveTargetTextBox(sender);
        if (tb is null) return;
        tb.Focus();
        tb.SelectAll();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 全局 ContextMenuOpening 处理器
    // ─────────────────────────────────────────────────────────────────────────

    // TextBox 类事件：为所有未显式设置 ContextMenu 的 TextBox 自动挂对应资源；
    // ComboBox 内部 PART_EditableTextBox 且 ComboBox 非可编辑 → 屏蔽。
    private void OnGlobalTextBoxContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not WpfTextBox tb) return;

        // ComboBox 内部 EditableTextBox：非可编辑 ComboBox 屏蔽右键
        if (FindAncestor<WpfComboBox>(tb) is WpfComboBox owningCombo && !owningCombo.IsEditable)
        {
            tb.ContextMenu = null;
            e.Handled = true;
            return;
        }

        // 始终按 IsReadOnly 强制挂上我们的资源菜单。
        //   —— 不再保留"已有 ContextMenu 则跳过"分支，因为 wpfui 皮肤 (TextBox default Style) 会
        //      默认挂一个英文右键菜单，若跳过则永远无法覆盖成我们的资源菜单。
        //   —— 控制台 ConsoleOutputTextBox 本来就是只读，会挂上 ReadOnlyTextBoxContextMenu，
        //      与 XAML 里显式引用的结果一致，效果无变化。
        string resourceKey = tb.IsReadOnly ? "ReadOnlyTextBoxContextMenu" : "TextBoxContextMenu";
        if (Current.Resources[resourceKey] is WpfContextMenu menu)
        {
            tb.ContextMenu = menu;
        }
    }

    // ComboBox 本体：直接屏蔽右键
    private void OnGlobalComboBoxContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is WpfComboBox combo)
        {
            combo.ContextMenu = null;
            e.Handled = true;
        }
    }

    // ComboBoxItem（下拉列表项）：屏蔽右键
    private void OnGlobalComboBoxItemContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is WpfComboBoxItem item)
        {
            item.ContextMenu = null;
            e.Handled = true;
        }
    }

    // 向上查找视觉/逻辑树中的指定类型祖先（用于识别 TextBox 是否处于 ComboBox 内部）。
    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        DependencyObject? current = start;
        while (current is not null)
        {
            if (current is T match) return match;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current)
                     ?? LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}
