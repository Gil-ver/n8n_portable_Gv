using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace n8n_launcher_Gv;

/// <summary>
/// 内存优化（2026-08-07）：托盘隐藏期间"暂停"画板动画 + 修剪工作集。
///
/// 背景：LaunchTile02 常驻多组 RepeatBehavior.Forever 动画（火焰抖动 150ms/程、云层三层摇曳、
/// 渐变环 6s/圈），外加星星/流星两条 DispatcherTimer 持续 new/销毁 Shape。窗口 Hide() 到托盘后
/// 这些动画仍在跑：WPF 组合线程按帧求值时间轴、维持脏区，工作集无法回落；星星/流星每秒产生垃圾
/// 把 Gen0 反复推起来。这是"最小化到托盘后内存不降"的主因。
///
/// ★★★ 核心原则：Pause / Resume，绝不 Stop + 重播 ★★★
/// 首版实现用的是 StopAllLaunchTileAnimations() 挂起、ForceRefreshLaunchTileVisualState() 恢复，
/// 结果恢复窗口时重放了整套入场动画 —— 用户看到"云朵又从画板底下升上来"、火焰重新点火。
/// 原因：ForceRefresh → ApplyLaunchTileVisualState(Running) → PlayRunningStateAnimation()
/// → RaiseLaunchClouds()，而 RaiseLaunchClouds 里的 RiseTo0() 写死 From=LaunchCloudHideOffsetDip
/// （画板底下）→ To=0，它本身就是"入场"动画，不是"续播"。
///
/// 现版对三个持有字段的 Forever Storyboard 直接 Pause(this)/Resume(this)（它们都以
/// Begin(this, true) 启动，isControllable=true，因此可控）；对 BeginAnimation 起的渐变环旋转
/// 采用"记角度 → 清动画 → 恢复时 From=记下的角度 继续转"；对星星/流星只停 DispatcherTimer 且
/// clearExisting:false；对烟雾链式动画只关 _launchSmokeFlickerActive 开关，各层 Opacity 停在
/// HoldEnd 当前值不动。
/// </summary>
public partial class MainWindow
{
    // ───────────────────────── 常量 ─────────────────────────

    /// <summary>窗口隐藏到托盘后，延迟多久才挂起动画（避免用户"点开-收起"来回切时反复启停）。</summary>
    private const int AnimationSuspendDelayMs = 60_000;

    /// <summary>挂起期间 n8n 状态发生变化时，临时恢复动画播放的时长（让过渡动画走完再重新挂起）。</summary>
    private const int AnimationTemporaryResumeMs = 30_000;

    /// <summary>渐变环旋转周期（与 StartLaunchTile02GradientRotation 保持一致）。</summary>
    private const int GradientRotationPeriodMs = 6_000;

    // ───────────────────────── 字段 ─────────────────────────

    /// <summary>窗口隐藏后延迟挂起动画的计时器。</summary>
    private DispatcherTimer? _animationSuspendTimer;

    /// <summary>画板动画当前是否处于挂起（暂停）状态。</summary>
    private bool _launchAnimationsSuspended;

    /// <summary>挂起瞬间渐变环的角度，恢复时从该角度继续旋转，避免跳回 0°。</summary>
    private double _suspendedGradientAngle;

    /// <summary>挂起瞬间烟雾闪烁链是否在运行；仅当为 true 时恢复才续接递归。</summary>
    private bool _suspendedSmokeFlickerWasActive;

    // ───────────────────────── Win32 ─────────────────────────

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    /// <summary>
    /// 把进程"已提交但此刻不活跃"的物理页面从工作集移到系统 standby list。
    /// 页面内容不丢失（不写盘），只是不再计入本进程工作集，任务管理器读数会明显下降；
    /// 窗口重新显示时页面从 standby list 换回，绝大多数是纯内存操作。
    /// 仅在"窗口已隐藏 + 动画已挂起"时调用，避免影响前台交互首帧。
    /// </summary>
    private static void TrimWorkingSet()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            EmptyWorkingSet(process.Handle);
        }
        catch
        {
            // 修剪失败不影响功能，静默忽略。
        }
    }

    // ───────────────────────── 对外钩子 ─────────────────────────

    /// <summary>窗口隐藏到托盘：启动延迟挂起计时器。</summary>
    private void OnWindowHiddenForMemory()
    {
        // 已挂起则不重复调度。
        if (_launchAnimationsSuspended)
            return;

        RestartAnimationSuspendTimer(AnimationSuspendDelayMs);
    }

    /// <summary>窗口重新显示：取消待挂起计时器，并把已挂起的动画原地续播。</summary>
    private void OnWindowShownForMemory()
    {
        CancelAnimationSuspendTimer();

        if (_launchAnimationsSuspended)
        {
            ResumeLaunchAnimationsForTray();
        }
    }

    /// <summary>
    /// 挂起期间 n8n 状态变化：先续播时钟，再让 SetN8nRunningStatus 已经触发的
    /// ApplyLaunchTileVisualState 过渡动画正常播放；一段时间后重新挂起。
    /// 窗口可见时本方法为空转。
    /// </summary>
    private void OnN8nStatusChangedForMemory()
    {
        if (!_launchAnimationsSuspended)
            return;

        // 状态真的变了，过渡动画（云朵下沉 / 火焰熄灭等）该播就播 —— 这与"同态恢复不该重播"是两回事。
        ResumeLaunchAnimationsForTray();

        // 窗口仍隐藏 → 给过渡动画留出时间，随后重新挂起。
        if (!IsVisible)
        {
            RestartAnimationSuspendTimer(AnimationTemporaryResumeMs);
        }
    }

    /// <summary>取消延迟挂起计时器（窗口显示、程序退出时调用）。</summary>
    private void CancelAnimationSuspendTimer()
    {
        if (_animationSuspendTimer is null)
            return;

        _animationSuspendTimer.Stop();
        _animationSuspendTimer = null;
    }

    private void RestartAnimationSuspendTimer(int delayMs)
    {
        CancelAnimationSuspendTimer();

        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(delayMs)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _animationSuspendTimer = null;

            // 到点时窗口若已可见（用户提前点开），不挂起。
            if (IsVisible)
                return;

            PauseLaunchAnimationsForTray();
        };
        _animationSuspendTimer = timer;
        timer.Start();
    }

    // ───────────────────────── 挂起 / 续播 ─────────────────────────

    /// <summary>
    /// 挂起：把所有常驻动画"原地冻结"，不改变任何视觉位置/透明度，之后修剪工作集。
    /// </summary>
    private void PauseLaunchAnimationsForTray()
    {
        if (_launchAnimationsSuspended)
            return;

        _launchAnimationsSuspended = true;

        // 1) 三个可控 Forever Storyboard 直接暂停时钟（必须传与 Begin(this, true) 同一个 this）。
        _launchFireBurnStoryboard?.Pause(this);
        _launchCloudSwayStoryboard?.Pause(this);
        _launchRocketMotionStoryboard?.Pause(this);

        // 2) 渐变环旋转由 BeginAnimation 启动、无 Storyboard 句柄：记下当前角度后清动画并写回该角度，
        //    视觉上原地停住（不清动画的话时间轴仍在求值，达不到省电省内存目的）。
        if (FindName("LaunchTile02GradientRotation") is RotateTransform gradientRotation)
        {
            _suspendedGradientAngle = gradientRotation.Angle;
            gradientRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            gradientRotation.Angle = _suspendedGradientAngle;
        }

        // 3) 星星 / 流星：只停生成计时器，clearExistingXxx 必须为 false。
        //    传 true 会 Children.Clear() 清空 Canvas，恢复时屏上空一片再逐颗冒出来，同样是穿帮。
        StopLaunchStarDecoration(clearExistingStars: false);
        StopLaunchMeteorDecoration(clearExistingMeteors: false);

        // 4) 烟雾 3 层随机透明度链式动画：关开关拦截下一段递归，
        //    各层 Opacity 保持 FillBehavior.HoldEnd 的当前值不动（不调 ExtinguishLaunchSmoke，否则会淡出）。
        _suspendedSmokeFlickerWasActive = _launchSmokeFlickerActive;
        _launchSmokeFlickerActive = false;

        // 5) 时钟都停下了，页面已不活跃 → 修剪工作集。
        TrimWorkingSet();
    }

    /// <summary>
    /// 续播：把挂起时冻结的动画从当前视觉位置继续，不重播任何入场动画。
    /// </summary>
    private void ResumeLaunchAnimationsForTray()
    {
        if (!_launchAnimationsSuspended)
            return;

        _launchAnimationsSuspended = false;

        // 低性能模式下本来就没有动画在跑，只复位标记即可。
        if (IsLowPerformanceModeEnabled())
            return;

        // 1) 三个可控 Storyboard 从暂停处继续。
        _launchFireBurnStoryboard?.Resume(this);
        _launchCloudSwayStoryboard?.Resume(this);
        _launchRocketMotionStoryboard?.Resume(this);

        // 2) 渐变环：From 必须显式写挂起时记下的角度。
        //    不能省略 From —— BeginAnimation(null) 之后属性已回落到基值，省略会导致瞬间跳回原点。
        if (FindName("LaunchTile02GradientRotation") is RotateTransform gradientRotation)
        {
            var resumeAnimation = new DoubleAnimation
            {
                From = _suspendedGradientAngle,
                To = _suspendedGradientAngle + 360,
                Duration = new Duration(TimeSpan.FromMilliseconds(GradientRotationPeriodMs)),
                RepeatBehavior = RepeatBehavior.Forever
            };
            gradientRotation.BeginAnimation(RotateTransform.AngleProperty, resumeAnimation);
        }

        // 3) 星星 / 流星：现存元素及其各自动画从未被停过，这里只把生成计时器重新跑起来。
        //    仅在运行相关视觉态下恢复，未运行态本来就没有星星装饰。
        if (_launchTileVisualState is LaunchTileVisualState.Starting or LaunchTileVisualState.Running)
        {
            StartLaunchStarDecoration();
            StartLaunchMeteorDecoration();
        }

        // 4) 烟雾：仅当挂起前确实在飘才续接 3 层递归。
        //    BeginNextRandomSmokeOpacitySegment 用的是 To=随机目标（无 From），天然从当前 Opacity 起步，无跳变。
        if (_suspendedSmokeFlickerWasActive)
        {
            _suspendedSmokeFlickerWasActive = false;
            _launchSmokeFlickerActive = true;
            BeginNextRandomSmokeOpacitySegment(LaunchSmokeLayer1);
            BeginNextRandomSmokeOpacitySegment(LaunchSmokeLayer2);
            BeginNextRandomSmokeOpacitySegment(LaunchSmokeLayer3);
        }
    }
}
