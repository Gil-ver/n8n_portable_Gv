using System;
using System.Windows;

namespace n8n_launcher_Gv;

/// <summary>
/// 画板按钮（LaunchTile02）的视觉状态机控制器。
///
/// 设计模型：双稳态 + 两段过渡（与 plans/launch-tile-animation-handoff.md 一致）。
/// <list type="bullet">
///   <item><description><see cref="LaunchTileVisualState.NotRunning"/>：未启动稳态（默认）。</description></item>
///   <item><description><see cref="LaunchTileVisualState.Running"/>：运行中稳态。</description></item>
///   <item><description><see cref="LaunchTileVisualState.Starting"/>：启动过渡（点启动、或重启的启动阶段）。Forever 循环，由 n8n 就绪事件打断切到 Running。</description></item>
///   <item><description><see cref="LaunchTileVisualState.Stopping"/>：中止过渡。Forever 循环，由进程退出事件打断切到 NotRunning。</description></item>
/// </list>
///
/// 约定：
/// <list type="number">
///   <item><description>过渡动画采用 Forever 循环（方案 A），不与真实启动/退出耗时绑定，老旧电脑多循环几圈即可，绝不脱节。</description></item>
///   <item><description>重启全程视觉统一为 <see cref="LaunchTileVisualState.Starting"/>：重启进行中（<c>_isRestartingN8n</c>）时抑制内部中止触发的 Stopping/NotRunning 态切换。</description></item>
///   <item><description>四个动画钩子当前为空实现，供后续填充具体动画；调整两个稳态动画时，过渡动画就在同一控制器内，便于同步。</description></item>
/// </list>
/// </summary>
public partial class MainWindow
{
    /// <summary>火焰稳定燃烧循环 Storyboard（Forever），点火完成后启动；熄灭/切非启动态时停止。</summary>
    private System.Windows.Media.Animation.Storyboard? _launchFireBurnStoryboard;

    /// <summary>火箭运动 Storyboard 占位（有限动画下火箭仅一次性上移，不再循环；保留字段以统一停止入口）。</summary>
    private System.Windows.Media.Animation.Storyboard? _launchRocketMotionStoryboard;

    /// <summary>烟雾 3 层各自的随机透明度链式动画是否仍在运行；停止时置 false，拦截下一段递归。</summary>
    private bool _launchSmokeFlickerActive;

    /// <summary>启动过渡时"烟雾延迟点火"的一次性计时器；用于避免烟雾早于火焰完全点燃就出现的穿帮。</summary>
    private System.Windows.Threading.DispatcherTimer? _launchSmokeIgniteDelayTimer;

    // 火焰动画参数。
    private const int LaunchFireIgniteMs = 640;          // 点火：缩放/淡入时长（火焰在 t=640ms 长满）
    private const int LaunchFireBurnPeriodMs = 150;      // 稳定燃烧：单程抖动时长（配合 AutoReverse）
    private const int LaunchFireExtinguishMs = 300;      // 熄灭：缩回/淡出时长（火焰在 t=600ms 完全消失 = Delay 300 + Ms 300）
    private const int LaunchFireExtinguishDelayMs = 300; // 关闭时火焰延后 300ms 才开始熄灭（与烟雾散完前有 200ms 重叠）

    // 烟雾动画参数。
    private const int LaunchSmokeFadeInMs = 1500;        // 点火后烟雾容器缓缓出现时长（配合 500ms 延迟 → 烟雾在 t=2000ms 完全飘出）
    private const int LaunchSmokeFadeOutMs = 500;        // 熄灭时烟雾淡出时长（烟雾在 t=500ms 完全消失）
    private const int LaunchSmokeIgniteDelayMs = 500;    // 启动时烟雾"延后 500ms 才开始淡入"，避免火焰刚点燃烟就冒
    private const double LaunchSmokeOpacityMin = 0.08;   // 单层随机透明度下限
    private const double LaunchSmokeOpacityMax = 0.28;   // 单层随机透明度上限
    private const int LaunchSmokeFlickerMinMs = 500;     // 单层透明度随机变动单段时长下限（随机速度）
    private const int LaunchSmokeFlickerMaxMs = 1300;    // 单层透明度随机变动单段时长上限（随机速度）

    // 星球动画参数。
    private const double LaunchPlanet1EntryOffsetXDip = 180.0;      // planet_1 未运行时位于右上方画板外：沿 30° 方向的 X 偏移
    private const double LaunchPlanet1EntryOffsetYDip = -103.92;    // Y 偏移 = -X*tan(30°)，负值表示向上
    private const int LaunchPlanet1EnterMs = 900;                   // 启动时从右上方外侧进入当前位置
    private const int LaunchPlanet1ExitMs = 450;                    // 停止时复位回右上方外侧
    private const double LaunchPlanet2EntryOffsetXDip = -120.0;     // planet_2 未运行时位于左侧画板外：沿 24° 方向的 X 偏移
    private const double LaunchPlanet2EntryOffsetYDip = -53.42;     // Y 偏移 = X*tan(24°) 的向上分量，负值表示向上
    private const int LaunchPlanet2EnterMs = 850;                   // 启动时从左侧外部 24° 进入当前位置
    private const int LaunchPlanet2ExitMs = 420;                    // 停止时复位回左侧外部
    private const double LaunchPlanet3EntryOffsetXDip = -16.82;     // planet_3 未运行时位于左上方画板外：沿 84° 方向的 X 偏移
    private const double LaunchPlanet3EntryOffsetYDip = -160.0;     // 接近垂直向下进入，负值表示从上方来
    private const int LaunchPlanet3EnterMs = 820;                   // 启动时从左侧 84° 接近垂直进入当前位置
    private const int LaunchPlanet3ExitMs = 420;                    // 停止时复位回左上方外部

    // 云朵动画参数（三层视差：底层最慢/幅度最大，顶层最快/幅度最小）。
    private const double LaunchCloudHideOffsetDip = 412.85;   // 初始藏到画板下方的下移量 = 云高（与 XAML Rise 初值一致）
    private const int LaunchCloudRiseBackMs = 1700;           // 底层上移时长（最慢）
    private const int LaunchCloudRiseMidMs = 1300;            // 中层上移时长
    private const int LaunchCloudRiseFrontMs = 950;           // 顶层上移时长（最快）
    private const double LaunchCloudSwayBaseDip = 7.0;        // 横向晃动基准幅度（顶层=1.0 倍基准）
    private const double LaunchCloudSwayFactorBack = 1.4;     // 底层晃动幅度系数
    private const double LaunchCloudSwayFactorMid = 1.2;      // 中层晃动幅度系数
    private const double LaunchCloudSwayFactorFront = 1.0;    // 顶层晃动幅度系数
    private const int LaunchCloudSwayPeriodBackMs = 4200;     // 底层晃动单程周期（最慢，棉花糖延迟感）
    private const int LaunchCloudSwayPeriodMidMs = 3400;      // 中层晃动单程周期
    private const int LaunchCloudSwayPeriodFrontMs = 2600;    // 顶层晃动单程周期（最快）
    private const int LaunchCloudSettleFrontMs = 950;         // 关闭时顶层云下沉时长（对称倒放：与 RiseFrontMs 相同 → 顶层最先沉完）
    private const int LaunchCloudSettleMidMs = 1300;          // 关闭时中层云下沉时长（对称倒放：与 RiseMidMs 相同）
    private const int LaunchCloudSettleBackMs = 1700;         // 关闭时底层云下沉时长（对称倒放：与 RiseBackMs 相同 → 底层最后沉完）

    /// <summary>云朵 3 层横向晃动循环 Storyboard（Forever），上移到位后启动；关闭/复位时停止。</summary>
    private System.Windows.Media.Animation.Storyboard? _launchCloudSwayStoryboard;

    // 火箭位移已静态写入 XAML：启动页初始整体上移 50 PS（33.33 DIP），运行时不再移动。

    /// <summary>画板按钮的视觉状态。</summary>
    internal enum LaunchTileVisualState
    {
        /// <summary>未启动稳态（默认）。</summary>
        NotRunning,

        /// <summary>启动过渡（含重启的启动阶段），Forever 循环。</summary>
        Starting,

        /// <summary>运行中稳态。</summary>
        Running,

        /// <summary>中止过渡，Forever 循环。</summary>
        Stopping
    }

    /// <summary>当前已应用的视觉态，用于同态防抖。</summary>
    private LaunchTileVisualState _launchTileVisualState = LaunchTileVisualState.NotRunning;

    /// <summary>
    /// 视觉状态机唯一入口：将画板按钮切换到目标视觉态。
    ///
    /// 调用规则：在现有 <see cref="SetN8nRunningStatus"/> 调用点之后，按语义追加一行本方法调用。
    /// 重启进行中（<c>_isRestartingN8n</c>）时，内部中止产生的 Stopping/NotRunning 请求会被抑制，
    /// 以保证重启全程视觉停留在 Starting，不闪 Stopping。
    /// </summary>
    /// <param name="target">目标视觉态。</param>
    private void ApplyLaunchTileVisualState(LaunchTileVisualState target)
    {
        // 重启进行中：不重启动画。内部中止/重新启动产生的 Starting/Stopping/NotRunning 都不切换视觉态，
        // 保持点击重启前的启动按钮动画状态，避免火箭/火焰/云层重新开始。
        if (_isRestartingN8n && target is LaunchTileVisualState.Starting or LaunchTileVisualState.Stopping or LaunchTileVisualState.NotRunning)
            return;

        // 同态防抖：目标与当前一致则不重复触发动画。
        if (target == _launchTileVisualState)
            return;

        _launchTileVisualState = target;

        // 切换前统一停止/清理上一段动画，避免叠加。
        StopAllLaunchTileAnimations();

        if (IsLowPerformanceModeEnabled())
        {
            ApplyLaunchTileLowPerformanceStaticState(target);
            return;
        }

        switch (target)
        {
            case LaunchTileVisualState.NotRunning:
                PlayNotRunningStateAnimation();
                break;
            case LaunchTileVisualState.Starting:
                PlayStartingTransitionLoop();
                break;
            case LaunchTileVisualState.Running:
                PlayRunningStateAnimation();
                break;
            case LaunchTileVisualState.Stopping:
                PlayStoppingTransitionLoop();
                break;
        }

        // 状态机切换后同步刷新三个渐变环的显隐（详见 UpdateGradientRingVisibility 注释）。
        UpdateGradientRingVisibility();
    }

    /// <summary>强制按当前视觉态重新渲染 LaunchTile02；用于切换低性能模式后立即应用。</summary>
    private void ForceRefreshLaunchTileVisualState()
    {
        var target = _launchTileVisualState;
        _launchTileVisualState = (LaunchTileVisualState)(-1);
        ApplyLaunchTileVisualState(target);
    }

    /// <summary>低性能模式：不播放动画，只把画板停在对应静态画面。</summary>
    private void ApplyLaunchTileLowPerformanceStaticState(LaunchTileVisualState target)
    {
        bool opened = target is LaunchTileVisualState.Starting or LaunchTileVisualState.Running;

        if (opened)
        {
            StopLaunchStarDecoration(clearExistingStars: false);
            EnsureLaunchStarPopulation();
            StopLaunchStarDecoration(clearExistingStars: false);
            foreach (var child in LaunchStarLayer.Children.OfType<UIElement>())
            {
                child.BeginAnimation(UIElement.OpacityProperty, null);
                child.Opacity = 1.0;
            }
        }
        else
        {
            StopLaunchStarDecoration(clearExistingStars: true);
        }

        StopLaunchMeteorDecoration(clearExistingMeteors: true);

        LaunchFireScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        LaunchFireScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        LaunchFire.BeginAnimation(UIElement.OpacityProperty, null);
        LaunchFireScale.ScaleX = opened ? 1.0 : 0.0;
        LaunchFireScale.ScaleY = opened ? 1.0 : 0.0;
        LaunchFire.Opacity = opened ? 1.0 : 0.0;

        LaunchSmoke.BeginAnimation(UIElement.OpacityProperty, null);
        LaunchSmoke.Opacity = opened ? 0.45 : 0.0;
        LaunchSmokeLayer1.BeginAnimation(UIElement.OpacityProperty, null);
        LaunchSmokeLayer2.BeginAnimation(UIElement.OpacityProperty, null);
        LaunchSmokeLayer3.BeginAnimation(UIElement.OpacityProperty, null);
        LaunchSmokeLayer1.Opacity = opened ? 0.18 : 0.0;
        LaunchSmokeLayer2.Opacity = opened ? 0.18 : 0.0;
        LaunchSmokeLayer3.Opacity = opened ? 0.18 : 0.0;

        LaunchRocketFloatTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        LaunchRocketShakeTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        LaunchRocketShakeTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        LaunchRocketFloatTransform.Y = -33.33;
        LaunchRocketShakeTransform.X = 0.0;
        LaunchRocketShakeTransform.Y = 0.0;

        void SetCloud(System.Windows.Media.TranslateTransform rise, System.Windows.Media.TranslateTransform sway)
        {
            rise.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
            sway.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            rise.Y = opened ? 0.0 : LaunchCloudHideOffsetDip;
            sway.X = 0.0;
        }

        SetCloud(LaunchCloudBackRise, LaunchCloudBackSway);
        SetCloud(LaunchCloudMidRise, LaunchCloudMidSway);
        SetCloud(LaunchCloudFrontRise, LaunchCloudFrontSway);

        void SetPlanet(System.Windows.Media.TranslateTransform transform, double hiddenX, double hiddenY)
        {
            transform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            transform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
            transform.X = opened ? 0.0 : hiddenX;
            transform.Y = opened ? 0.0 : hiddenY;
        }

        SetPlanet(LaunchPlanet1EntryTransform, LaunchPlanet1EntryOffsetXDip, LaunchPlanet1EntryOffsetYDip);
        SetPlanet(LaunchPlanet2EntryTransform, LaunchPlanet2EntryOffsetXDip, LaunchPlanet2EntryOffsetYDip);
        SetPlanet(LaunchPlanet3EntryTransform, LaunchPlanet3EntryOffsetXDip, LaunchPlanet3EntryOffsetYDip);
    }

    /// <summary>统一停止/清理画板按钮的全部视觉动画（稳态 + 过渡）。</summary>
    private void StopAllLaunchTileAnimations()
    {
        // 收编现有火箭预览动画的停止逻辑，保持现有行为不变。
        _launchRocketPreviewStoryboard?.Stop();
        _launchRocketPreviewStoryboard = null;

        // 停止火焰的稳定燃烧循环（点火/熄灭的瞬时动画无需在此停止，会被新的状态动画覆盖）。
        _launchFireBurnStoryboard?.Stop(this);
        _launchFireBurnStoryboard = null;

        // 停止火箭运动（复位动画由各状态钩子按需触发）。
        _launchRocketMotionStoryboard?.Stop(this);
        _launchRocketMotionStoryboard = null;

        // 关闭烟雾 3 层随机透明度链式动画（淡出/复位由各状态钩子按需触发）。
        _launchSmokeFlickerActive = false;

        // 取消可能还在等待触发的"烟雾延迟点火"计时器，
        // 防止用户在 500ms 内又切成 Stopping/NotRunning 时，未来还会冒出一撮烟。
        _launchSmokeIgniteDelayTimer?.Stop();
        _launchSmokeIgniteDelayTimer = null;

        // 停止云朵横向晃动循环（上移/下沉复位由各状态钩子按需触发）。
        _launchCloudSwayStoryboard?.Stop(this);
        _launchCloudSwayStoryboard = null;
    }

    // ───────────────────────── 稳态动画钩子 ─────────────────────────

    /// <summary>未启动稳态：熄灭火焰、烟雾淡出、火箭复位静止。</summary>
    private void PlayNotRunningStateAnimation()
    {
        StopLaunchStarDecoration(clearExistingStars: true);
        StopLaunchMeteorDecoration(clearExistingMeteors: true);
        ExtinguishLaunchFire();
        ExtinguishLaunchSmoke();
        SettleLaunchRocketMotion();
        SettleLaunchClouds();
        HideLaunchPlanet1();
        HideLaunchPlanet2();
        HideLaunchPlanet3();
    }

    /// <summary>运行中稳态：保持火焰稳定燃烧 + 烟雾缓现闪烁 + 火箭浮动颤抖（点火已在 Starting 完成，这里直接续燃续动）。</summary>
    private void PlayRunningStateAnimation()
    {
        // 若从 Starting 平滑进入 Running，火焰已点燃并在燃烧；为兼容直接置 Running 的情形，
        // 确保火焰处于点亮状态并启动燃烧循环 + 烟雾 + 火箭运动循环 + 云朵上移晃动。
        // 烟雾走"延迟点火"路径：若 Starting 期间的延迟已触发 → _launchSmokeFlickerActive=true → 内部直接返回不重播；
        // 若 Starting 太短（<500ms）延迟 timer 还没到 → 内部检测到 timer 仍在等待，也不重复调度，
        // 平滑衔接、无跳变。
        StartLaunchStarDecoration();
        StartLaunchMeteorDecoration();
        IgniteLaunchFire(startBurnLoopAfterIgnite: true);
        ScheduleDelayedIgniteLaunchSmoke();
        StartLaunchRocketMotionLoop();
        RaiseLaunchClouds();
        EnterLaunchPlanet1();
        EnterLaunchPlanet2();
        EnterLaunchPlanet3();
    }

    // ───────────────────────── 过渡动画钩子 ─────────────────────────

    /// <summary>启动过渡：点火 → 稳定燃烧循环 + 烟雾缓现闪烁 + 火箭浮动颤抖（Forever，进入 Running 时继续）。</summary>
    private void PlayStartingTransitionLoop()
    {
        StartLaunchStarDecoration();
        StartLaunchMeteorDecoration();
        IgniteLaunchFire(startBurnLoopAfterIgnite: true);
        // 烟雾走"延迟点火"路径：t=0 火焰开始点燃 → t=500 烟雾才开始淡入，
        // 避免"火焰还没燃起、烟就先冒出来"的物理直觉穿帮。
        ScheduleDelayedIgniteLaunchSmoke();
        StartLaunchRocketMotionLoop();
        RaiseLaunchClouds();
        EnterLaunchPlanet1();
        EnterLaunchPlanet2();
        EnterLaunchPlanet3();
    }

    /// <summary>中止过渡：熄灭火焰、烟雾淡出、火箭复位静止、云朵下沉藏起。</summary>
    private void PlayStoppingTransitionLoop()
    {
        StopLaunchStarDecoration(clearExistingStars: true);
        StopLaunchMeteorDecoration(clearExistingMeteors: true);
        ExtinguishLaunchFire();
        ExtinguishLaunchSmoke();
        SettleLaunchRocketMotion();
        SettleLaunchClouds();
        HideLaunchPlanet1();
        HideLaunchPlanet2();
        HideLaunchPlanet3();
    }

    // ───────────────────────── 火焰动画实现 ─────────────────────────

    /// <summary>
    /// 点火：火焰从顶部中心 ScaleX/ScaleY 0→1（带轻微过冲回落）+ Opacity 0→1，约 250ms。
    /// 点火完成后按需启动稳定燃烧循环。
    /// </summary>
    private void IgniteLaunchFire(bool startBurnLoopAfterIgnite)
    {
        // 先停掉燃烧循环，避免与点火动画争用同一属性。
        _launchFireBurnStoryboard?.Stop(this);
        _launchFireBurnStoryboard = null;

        var igniteDuration = TimeSpan.FromMilliseconds(LaunchFireIgniteMs);
        var overshootEase = new System.Windows.Media.Animation.BackEase
        {
            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
            Amplitude = 0.4
        };

        var scaleXAnim = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = 1.0,
            Duration = igniteDuration,
            EasingFunction = overshootEase,
            FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
        };
        var scaleYAnim = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = 1.0,
            Duration = igniteDuration,
            EasingFunction = overshootEase,
            FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
        };
        var opacityAnim = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(LaunchFireIgniteMs),
            FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
        };

        if (startBurnLoopAfterIgnite)
            scaleYAnim.Completed += (_, _) => StartLaunchFireBurnLoop();

        // 直接对元素 BeginAnimation，避免与 Storyboard 的属性锁定冲突。
        LaunchFireScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scaleXAnim);
        LaunchFireScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scaleYAnim);
        LaunchFire.BeginAnimation(System.Windows.UIElement.OpacityProperty, opacityAnim);
    }

    /// <summary>
    /// 稳定燃烧循环：ScaleY 在 1.0~1.1 跳动 + ScaleX 在 1.0~1.05 轻微抖动，
    /// AutoReverse + Forever，周期约 150ms，营造跳动火苗。
    /// 不再做 Opacity 闪烁（保持火焰不透明度恒定）。
    /// </summary>
    private void StartLaunchFireBurnLoop()
    {
        _launchFireBurnStoryboard?.Stop(this);

        var period = TimeSpan.FromMilliseconds(LaunchFireBurnPeriodMs);
        var storyboard = new System.Windows.Media.Animation.Storyboard();

        void AddBurnAnimation(DependencyProperty property, string targetName, double from, double to)
        {
            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = from,
                To = to,
                Duration = period,
                AutoReverse = true,
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                EasingFunction = new System.Windows.Media.Animation.SineEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut
                }
            };
            System.Windows.Media.Animation.Storyboard.SetTargetName(anim, targetName);
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(
                anim, new PropertyPath(property));
            storyboard.Children.Add(anim);
        }

        AddBurnAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, "LaunchFireScale", 1.0, 1.1);
        AddBurnAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, "LaunchFireScale", 1.0, 1.05);

        _launchFireBurnStoryboard = storyboard;
        storyboard.Begin(this, true);
    }

    /// <summary>熄灭：停止燃烧循环并把 Scale/Opacity 缩回 0。
    /// 关闭时延后 <see cref="LaunchFireExtinguishDelayMs"/> 才开始熄灭，让烟雾先散一部分再收火焰（"烟先散、火后灭"更符合物理直觉）。
    /// 缓动使用 CubicEase EaseIn：前 60% 温和收缩，后 40% 加速消失，无"欲缩先鼓"，
    /// 视觉上从起始时刻就能明显看到火焰在变小，避免用户误以为"火焰在等烟雾散完才开始消失"。</summary>
    private void ExtinguishLaunchFire()
    {
        _launchFireBurnStoryboard?.Stop(this);
        _launchFireBurnStoryboard = null;

        var duration = TimeSpan.FromMilliseconds(LaunchFireExtinguishMs);
        var beginTime = TimeSpan.FromMilliseconds(LaunchFireExtinguishDelayMs);
        var extinguishEase = new System.Windows.Media.Animation.CubicEase
        {
            EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn
        };
        var scaleXAnim = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = 0.0,
            Duration = duration,
            BeginTime = beginTime,
            EasingFunction = extinguishEase,
            FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
        };
        var scaleYAnim = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = 0.0,
            Duration = duration,
            BeginTime = beginTime,
            EasingFunction = extinguishEase,
            FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
        };
        var opacityAnim = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = 0.0,
            Duration = duration,
            BeginTime = beginTime,
            EasingFunction = extinguishEase,
            FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
        };

        LaunchFireScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scaleXAnim);
        LaunchFireScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scaleYAnim);
        LaunchFire.BeginAnimation(System.Windows.UIElement.OpacityProperty, opacityAnim);
    }

    // ───────────────────────── 火箭运动实现 ─────────────────────────

    /// <summary>火箭位移已固定在 XAML 初始位置；启动后不再移动。</summary>
    private void StartLaunchRocketMotionLoop()
    {
        _launchRocketMotionStoryboard?.Stop(this);
        _launchRocketMotionStoryboard = null;
        LaunchRocketFloatTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        LaunchRocketShakeTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        LaunchRocketShakeTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
    }

    /// <summary>火箭停止时不复位位置，保持 XAML 中的静态上移位置。</summary>
    private void SettleLaunchRocketMotion()
    {
        _launchRocketMotionStoryboard?.Stop(this);
        _launchRocketMotionStoryboard = null;
        LaunchRocketFloatTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        LaunchRocketShakeTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        LaunchRocketShakeTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
    }

    // ───────────────────────── 烟雾动画实现 ─────────────────────────

    /// <summary>
    /// 调度"延迟点火"版本的烟雾入场：t=0 立即返回，t=+LaunchSmokeIgniteDelayMs 才真正调用 <see cref="IgniteLaunchSmoke"/>。
    /// 用于避免"火焰刚点燃、烟就先冒出来"的物理直觉穿帮。
    ///
    /// 防抖/幂等：
    /// - 若已有延迟计时器正在等待，直接返回（不重置周期），避免同一次 Starting 触发多次 → Running 触发时才会看到跳变。
    /// - 若 3 层链式动画已在运行（_launchSmokeFlickerActive=true），说明延迟点火已触发过，也直接返回。
    /// - 任何状态切换（含快速切 Stopping/NotRunning）都会通过 StopAllLaunchTileAnimations() 里的 timer.Stop() 兜底清理。
    /// </summary>
    private void ScheduleDelayedIgniteLaunchSmoke()
    {
        // 已在飘：不重复调度。
        if (_launchSmokeFlickerActive)
            return;

        // 延迟计时器还在等待：不重启周期，直接返回。
        if (_launchSmokeIgniteDelayTimer != null && _launchSmokeIgniteDelayTimer.IsEnabled)
            return;

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(LaunchSmokeIgniteDelayMs)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // 若外部已把 timer 引用清掉（例如 StopAllLaunchTileAnimations 被调用过），
            // 说明状态已切走，不再点火，避免关闭后又冒一撮烟。
            if (!ReferenceEquals(_launchSmokeIgniteDelayTimer, timer))
                return;
            _launchSmokeIgniteDelayTimer = null;
            IgniteLaunchSmoke();
        };
        _launchSmokeIgniteDelayTimer = timer;
        timer.Start();
    }

    /// <summary>
    /// 点火后烟雾缓缓出现：容器 Opacity 0→1 缓现；同时 3 层各自启动独立的随机透明度链式变动。
    /// 烟雾位于 LaunchRocketAssembly 内，随火箭浮动颤抖一同运动。
    /// </summary>
    private void IgniteLaunchSmoke()
    {
        var fadeIn = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(LaunchSmokeFadeInMs),
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut
            },
            FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
        };
        LaunchSmoke.BeginAnimation(System.Windows.UIElement.OpacityProperty, fadeIn);

        // 启动 3 层各自的随机透明度链式动画。
        _launchSmokeFlickerActive = true;
        BeginNextRandomSmokeOpacitySegment(LaunchSmokeLayer1);
        BeginNextRandomSmokeOpacitySegment(LaunchSmokeLayer2);
        BeginNextRandomSmokeOpacitySegment(LaunchSmokeLayer3);
    }

    /// <summary>
    /// 单层烟雾的随机透明度链式段：在 [min,max] 内随机一个目标透明度、随机一个时长（随机速度），
    /// 到点后递归续接，制造每层各自不规律的浓淡呼吸。
    /// </summary>
    private void BeginNextRandomSmokeOpacitySegment(System.Windows.Shapes.Path layer)
    {
        if (!_launchSmokeFlickerActive)
            return;

        double target = RandomDouble(LaunchSmokeOpacityMin, LaunchSmokeOpacityMax);
        int durationMs = (int)RandomDouble(LaunchSmokeFlickerMinMs, LaunchSmokeFlickerMaxMs);

        var anim = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new System.Windows.Media.Animation.SineEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut
            },
            FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
        };

        anim.Completed += (_, _) => BeginNextRandomSmokeOpacitySegment(layer);
        layer.BeginAnimation(System.Windows.UIElement.OpacityProperty, anim);
    }

    /// <summary>熄灭：停止 3 层随机透明度变动，容器整体淡出到 0。
    /// 对称倒放：时长 = FadeInMs (1500ms)、缓动 = QuadraticEase EaseInOut（与 IgniteLaunchSmoke 一致）。</summary>
    private void ExtinguishLaunchSmoke()
    {
        // 关闭闪烁开关，拦截各层 Completed 回调里的下一段递归。
        _launchSmokeFlickerActive = false;

        var fadeOut = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(LaunchSmokeFadeOutMs),
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut
            },
            FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
        };
        LaunchSmoke.BeginAnimation(System.Windows.UIElement.OpacityProperty, fadeOut);
    }

    // ───────────────────────── 星球动画实现 ─────────────────────────

    /// <summary>planet_1 从右上方画板外沿 30° 斜线进入最终位置。</summary>
    private void EnterLaunchPlanet1()
    {
        AnimateLaunchPlanet1Entry(toX: 0.0, toY: 0.0, durationMs: LaunchPlanet1EnterMs, easeOut: true);
    }

    /// <summary>planet_1 复位到右上方画板外，作为未运行状态。</summary>
    private void HideLaunchPlanet1()
    {
        AnimateLaunchPlanet1Entry(
            toX: LaunchPlanet1EntryOffsetXDip,
            toY: LaunchPlanet1EntryOffsetYDip,
            durationMs: LaunchPlanet1ExitMs,
            easeOut: false);
    }

    private void AnimateLaunchPlanet1Entry(double toX, double toY, int durationMs, bool easeOut)
    {
        var duration = TimeSpan.FromMilliseconds(durationMs);
        var easing = new System.Windows.Media.Animation.QuadraticEase
        {
            EasingMode = easeOut
                ? System.Windows.Media.Animation.EasingMode.EaseOut
                : System.Windows.Media.Animation.EasingMode.EaseIn
        };

        var xAnim = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = toX,
            Duration = duration,
            EasingFunction = easing,
            FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
        };
        var yAnim = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = toY,
            Duration = duration,
            EasingFunction = easing,
            FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
        };

        if (LaunchPlanet1.RenderTransform is not System.Windows.Media.TransformGroup group ||
            group.Children.Count == 0 ||
            group.Children[0] is not System.Windows.Media.TranslateTransform entryTransform)
            return;

        entryTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, xAnim);
        entryTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, yAnim);
    }

    /// <summary>planet_2 从左侧画板外沿 24° 斜线进入最终位置。</summary>
    private void EnterLaunchPlanet2()
    {
        AnimateLaunchPlanet2Entry(toX: 0.0, toY: 0.0, durationMs: LaunchPlanet2EnterMs, easeOut: true);
    }

    /// <summary>planet_2 复位到左侧画板外，作为未运行状态。</summary>
    private void HideLaunchPlanet2()
    {
        AnimateLaunchPlanet2Entry(
            toX: LaunchPlanet2EntryOffsetXDip,
            toY: LaunchPlanet2EntryOffsetYDip,
            durationMs: LaunchPlanet2ExitMs,
            easeOut: false);
    }

    private void AnimateLaunchPlanet2Entry(double toX, double toY, int durationMs, bool easeOut)
    {
        var duration = TimeSpan.FromMilliseconds(durationMs);
        var easing = new System.Windows.Media.Animation.QuadraticEase
        {
            EasingMode = easeOut
                ? System.Windows.Media.Animation.EasingMode.EaseOut
                : System.Windows.Media.Animation.EasingMode.EaseIn
        };

        var xAnim = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = toX,
            Duration = duration,
            EasingFunction = easing,
            FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
        };
        var yAnim = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = toY,
            Duration = duration,
            EasingFunction = easing,
            FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
        };

        if (LaunchPlanet2.RenderTransform is not System.Windows.Media.TranslateTransform entryTransform)
            return;

        entryTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, xAnim);
        entryTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, yAnim);
    }

    /// <summary>planet_3 从左侧 84°、接近垂直向下进入最终位置。</summary>
    private void EnterLaunchPlanet3()
    {
        AnimateLaunchPlanet3Entry(toX: 0.0, toY: 0.0, durationMs: LaunchPlanet3EnterMs, easeOut: true);
    }

    /// <summary>planet_3 复位到左上方画板外，作为未运行状态。</summary>
    private void HideLaunchPlanet3()
    {
        AnimateLaunchPlanet3Entry(
            toX: LaunchPlanet3EntryOffsetXDip,
            toY: LaunchPlanet3EntryOffsetYDip,
            durationMs: LaunchPlanet3ExitMs,
            easeOut: false);
    }

    private void AnimateLaunchPlanet3Entry(double toX, double toY, int durationMs, bool easeOut)
    {
        var duration = TimeSpan.FromMilliseconds(durationMs);
        var easing = new System.Windows.Media.Animation.QuadraticEase
        {
            EasingMode = easeOut
                ? System.Windows.Media.Animation.EasingMode.EaseOut
                : System.Windows.Media.Animation.EasingMode.EaseIn
        };

        var xAnim = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = toX,
            Duration = duration,
            EasingFunction = easing,
            FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
        };
        var yAnim = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = toY,
            Duration = duration,
            EasingFunction = easing,
            FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
        };

        if (LaunchPlanet3.RenderTransform is not System.Windows.Media.TranslateTransform entryTransform)
            return;

        entryTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, xAnim);
        entryTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, yAnim);
    }

    // ───────────────────────── 云朵动画实现 ─────────────────────────

    /// <summary>
    /// 云朵入场（开启）：三层从画板下方（RiseY=LaunchCloudHideOffsetDip）平滑上移到最终位置（RiseY=0），
    /// 移动速度底层最慢、顶层最快；到位后启动三层各自的棉花糖横向晃动循环（幅度/周期按层递增，营造延迟摇曳）。
    /// </summary>
    private void RaiseLaunchClouds()
    {
        // 先停掉可能在跑的晃动循环并清掉 Sway 残留，避免与新动画叠加。
        _launchCloudSwayStoryboard?.Stop(this);
        _launchCloudSwayStoryboard = null;
        LaunchCloudBackSway.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        LaunchCloudMidSway.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        LaunchCloudFrontSway.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);

        void RiseTo0(System.Windows.Media.TranslateTransform rise, int durationMs)
        {
            // 清掉 Rise 残留持有值，确保新动画能从当前藏底位置接管。
            rise.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = LaunchCloudHideOffsetDip,
                To = 0.0,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                },
                FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
            };
            rise.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, anim);
        }

        RiseTo0(LaunchCloudBackRise, LaunchCloudRiseBackMs);
        RiseTo0(LaunchCloudMidRise, LaunchCloudRiseMidMs);
        RiseTo0(LaunchCloudFrontRise, LaunchCloudRiseFrontMs);

        // 待最慢的底层也上移到位后，启动三层棉花糖晃动循环（用一次性计时器驱动，不再二次动画占用 Rise 属性）。
        var swayStartTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(LaunchCloudRiseBackMs)
        };
        swayStartTimer.Tick += (_, _) =>
        {
            swayStartTimer.Stop();
            StartLaunchCloudSwayLoop();
        };
        swayStartTimer.Start();
    }

    /// <summary>
    /// 棉花糖横向晃动循环：三层各自 Sway.X 从当前 0 平滑进入到 +幅度，之后在 ±幅度 间正弦往复（AutoReverse + Forever）。
    /// 幅度系数 底层1.4 / 中层1.2 / 顶层1.0；周期 底层最慢 / 顶层最快，三层错位营造柔软延迟摇曳。
    /// 注意：不要设置 From=-amplitude，否则从入场结束的 0 瞬间跳到 -amplitude，会出现用户看到的“到位后跳一下”。
    /// </summary>
    private void StartLaunchCloudSwayLoop()
    {
        _launchCloudSwayStoryboard?.Stop(this);

        var storyboard = new System.Windows.Media.Animation.Storyboard();

        void AddSway(string targetName, double amplitude, int periodMs)
        {
            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                To = amplitude,
                Duration = TimeSpan.FromMilliseconds(periodMs),
                AutoReverse = true,
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                EasingFunction = new System.Windows.Media.Animation.SineEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut
                }
            };
            System.Windows.Media.Animation.Storyboard.SetTargetName(anim, targetName);
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(
                anim, new PropertyPath(System.Windows.Media.TranslateTransform.XProperty));
            storyboard.Children.Add(anim);
        }

        AddSway("LaunchCloudBackSway", LaunchCloudSwayBaseDip * LaunchCloudSwayFactorBack, LaunchCloudSwayPeriodBackMs);
        AddSway("LaunchCloudMidSway", LaunchCloudSwayBaseDip * LaunchCloudSwayFactorMid, LaunchCloudSwayPeriodMidMs);
        AddSway("LaunchCloudFrontSway", LaunchCloudSwayBaseDip * LaunchCloudSwayFactorFront, LaunchCloudSwayPeriodFrontMs);

        _launchCloudSwayStoryboard = storyboard;
        storyboard.Begin(this, true);
    }

    /// <summary>
    /// 云朵复位（关闭）：停止晃动循环，Sway.X 归零，三层各自平滑下沉回画板下方（RiseY=LaunchCloudHideOffsetDip）藏起。
    /// 对称倒放：每层下沉时长 = 对应 RiseMs（front=950 / mid=1300 / back=1700），
    /// 缓动 = QuadraticEase EaseOut（与 RaiseLaunchClouds 一致）。
    ///
    /// 【Bug 修复】原实现里 <c>rise.BeginAnimation(Y, null)</c> 会先清空动画 → Y 瞬间掉回 XAML 基值 412.85（藏底位置），
    /// 造成"云在关闭瞬间就已消失"的穿帮；随后新动画 To=412.85 又变成 412.85→412.85 的空动画。
    /// 修复：不再清空动画，而是显式设置 <c>From = 当前 Y 值</c>，让下沉从当前视觉位置 0 起步。
    /// </summary>
    private void SettleLaunchClouds()
    {
        _launchCloudSwayStoryboard?.Stop(this);
        _launchCloudSwayStoryboard = null;

        // Sway.X 归零跟着顶层 950ms（视觉上最先"稳下来"）。
        var swayDuration = TimeSpan.FromMilliseconds(LaunchCloudSettleFrontMs);
        void SwayTo0(System.Windows.Media.TranslateTransform sway)
        {
            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                To = 0.0,
                Duration = swayDuration,
                FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
            };
            sway.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, anim);
        }
        SwayTo0(LaunchCloudBackSway);
        SwayTo0(LaunchCloudMidSway);
        SwayTo0(LaunchCloudFrontSway);

        void HideDown(System.Windows.Media.TranslateTransform rise, int durationMs)
        {
            // 修 Bug：读当前 Y（动画托管值 = 0），显式 From 起步，避免清空动画导致的瞬间跳变。
            double currentY = (double)rise.GetValue(System.Windows.Media.TranslateTransform.YProperty);
            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = currentY,
                To = LaunchCloudHideOffsetDip,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                },
                FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
            };
            rise.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, anim);
        }
        HideDown(LaunchCloudFrontRise, LaunchCloudSettleFrontMs);
        HideDown(LaunchCloudMidRise, LaunchCloudSettleMidMs);
        HideDown(LaunchCloudBackRise, LaunchCloudSettleBackMs);
    }
}


