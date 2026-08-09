using System.Windows;

namespace n8n_launcher_Gv;

/// <summary>
/// 三个启动控制按钮（LaunchTile02/03/04）顶层旋转渐变环的显隐控制。
///
/// 规则（两态 + 悬浮联动）：
/// <list type="bullet">
///   <item><description>n8n 未运行（NotRunning / Stopping）：Tile02 环显 / Tile03/04 环隐。</description></item>
///   <item><description>n8n 运行中（Starting / Running）：默认 Tile02 环显；鼠标悬浮 Tile03/04 时切到该按钮显、其它两个隐。</description></item>
/// </list>
///
/// 关键约束：使用 Opacity=0/1（不使用 Visibility=Collapsed），因为三个环的旋转 Angle 通过 Binding
/// 绑定到 LaunchTile02GradientRotation.Angle，同一时钟源；用 Visibility.Collapsed 会解除元素挂载，
/// Binding 短暂失效可能导致重新可见时角度对不上。Opacity 只影响渲染，Binding 全程有效，旋转永不停。
/// </summary>
public partial class MainWindow
{
    /// <summary>鼠标是否悬浮在 LaunchTile03 上。仅在运行态影响环显隐。</summary>
    private bool _isTile03Hovered;

    /// <summary>鼠标是否悬浮在 LaunchTile04 上。仅在运行态影响环显隐。</summary>
    private bool _isTile04Hovered;

    /// <summary>
    /// 鼠标是否悬浮在 LaunchTile03↔LaunchTile04 之间的垂直缝隙桥接层上。
    /// 运行态下命中此区域 → 三个环全部隐藏，避免"从 Tile03 划到 Tile04 途中 Tile02 回弹一下"。
    /// </summary>
    private bool _isTile03Tile04GapHovered;

    /// <summary>
    /// 根据当前视觉态和悬浮状态，一次性设置三个环的 Opacity。
    /// 由 ApplyLaunchTileVisualState / MouseEnter / MouseLeave 三个入口共同调用。
    /// </summary>
    private void UpdateGradientRingVisibility()
    {
        if (LaunchTile02GradientRing is null || LaunchTile03GradientRing is null || LaunchTile04GradientRing is null)
            return;

        bool isRunningState = _launchTileVisualState is LaunchTileVisualState.Starting or LaunchTileVisualState.Running;

        double tile02Opacity;
        double tile03Opacity;
        double tile04Opacity;

        if (!isRunningState)
        {
            // 未运行 / 中止中：只显 Tile02 环。
            tile02Opacity = 1.0;
            tile03Opacity = 0.0;
            tile04Opacity = 0.0;
        }
        else if (_isTile03Hovered)
        {
            // 运行态 + 悬浮 Tile03:仅 Tile03 显。
            tile02Opacity = 0.0;
            tile03Opacity = 1.0;
            tile04Opacity = 0.0;
        }
        else if (_isTile04Hovered)
        {
            // 运行态 + 悬浮 Tile04:仅 Tile04 显。
            tile02Opacity = 0.0;
            tile03Opacity = 0.0;
            tile04Opacity = 1.0;
        }
        else if (_isTile03Tile04GapHovered)
        {
            // 运行态 + 悬浮 Tile03↔Tile04 缝隙:三个环全部隐藏（避免过渡时回弹到 Tile02）。
            tile02Opacity = 0.0;
            tile03Opacity = 0.0;
            tile04Opacity = 0.0;
        }
        else
        {
            // 运行态 + 无悬浮:Tile02 常显。
            tile02Opacity = 1.0;
            tile03Opacity = 0.0;
            tile04Opacity = 0.0;
        }

        LaunchTile02GradientRing.Opacity = tile02Opacity;
        LaunchTile03GradientRing.Opacity = tile03Opacity;
        LaunchTile04GradientRing.Opacity = tile04Opacity;
    }

    private void LaunchTile03_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isTile03Hovered = true;
        UpdateGradientRingVisibility();
    }

    private void LaunchTile03_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isTile03Hovered = false;
        UpdateGradientRingVisibility();
    }

    private void LaunchTile04_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isTile04Hovered = true;
        UpdateGradientRingVisibility();
    }

    private void LaunchTile04_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isTile04Hovered = false;
        UpdateGradientRingVisibility();
    }

    private void LaunchTile03Tile04Gap_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isTile03Tile04GapHovered = true;
        UpdateGradientRingVisibility();
    }

    private void LaunchTile03Tile04Gap_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isTile03Tile04GapHovered = false;
        UpdateGradientRingVisibility();
    }
}
