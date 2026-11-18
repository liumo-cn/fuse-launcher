using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Fuse.Core.Accounts;
using Fuse.Core.Launch;
using Fuse.Core.Pipeline;
using Fuse.Core.Versioning;
using Fuse.App.Panels;

namespace Fuse.App;

/// <summary>
/// 桌面悬浮组件（FR-12）：中心启动大圆 + 五花瓣(正五边形布局)。
/// 悬停展开花瓣;单击大圆启动上次版本;拖动移动位置并记忆;花瓣弹出玻璃面板。
/// </summary>
public partial class WidgetWindow : Window
{
    private bool _petalsOpen;
    private bool _dragging;
    private Point _dragStart;
    private readonly System.Windows.Threading.DispatcherTimer _hoverTimer;

    // ---- 悬停判定 ----
    // 展开靠"压在圆盘上",收回靠"离开以圆心为中心的正五边形"。
    // 圆心在 SafeArea 里是 (138,138)(大圆 80 摆在 98,98),不是容器正中,
    // 所以一切距离都按圆心算,和视觉对齐。
    private const double OrbCenterInSafe = OrbInSafe + OrbSide / 2;   // 138
    // 五边形边心距(圆心到每条边的距离)。花瓣的命中区最近处约距圆心 43px,
    // 这个值不能低于 43,否则"从圆滑到花瓣"会落在缝隙里;剩下的缝隙由下面的
    // 1.5 秒宽限期兜住(离开判定区不立刻收,来得及移到花瓣上)。
    private const double KeepOpenApothem = 50;

    // 离开正方形后的宽限期:这段时间内摸到/点击花瓣就不收回
    private const double CloseGraceMs = 1500;
    private DateTime? _closeDueAt;

    // ---- 无交互暂停 ----
    // 星光/漂浮是每帧重绘的常驻开销(实测 ~26% 单核),光标不在旁边时没有观感价值。
    // 离开窗口周围超过 IdlePauseAfterMs 就定格全部装饰动画;悬停计时器继续 20Hz 探测,
    // 光标一回到附近立即续播(50ms 内,肉眼无感)。
    private const double IdlePauseAfterMs = 4000;
    private bool _decorPaused;
    private DateTime _cursorLastNearAt = DateTime.Now;

    // ---- 启动特效(呼吸 → 成功脉冲 / 失败收缩) ----
    // 技术来源:orb-explosion.html 的积分法变频前摇 + 能量环 + 抖动,
    //          OrbExplosion.cs 的 Canvas 粒子/闪光 + DispatcherTimer 驱动。
    // 硬约束:大圆任何时刻不得接触花瓣——花瓣内缘距圆心 63px,
    //         呼吸上限 1.06(半径 42.4)、脉冲上限 1.20(半径 48)都在预算内。
    private enum LaunchFxState { Idle, Breathing, Success, Fail }

    private LaunchFxState _fxState = LaunchFxState.Idle;
    private readonly System.Windows.Threading.DispatcherTimer _fxTimer;
    private DateTime _fxPhaseStart;
    private double _wobblePhase;      // 积分相位:频率渐变时曲线不跳变
    private double _fxProgress = 0.05;
    private double _currentScale = 1; // 供悬停命中判定用
    private double _pulseFromScale = 1;
    private ScaleTransform? _orbScale;
    private TranslateTransform? _orbShift;
    private ScaleTransform? _orbBodyScale;
    private TranslateTransform? _orbBodyShift;
    /// <summary>当前显示的光晕档位(索引,避免重复改 Source)。</summary>
    private int _glowFrame = -1;
    private DateTime _lastGlowAt = DateTime.MinValue;
    /// <summary>星光动画的作用目标(大圆盘 + 各花瓣光晕盘),不可见暂停时按此定格。</summary>
    private readonly List<System.Windows.UIElement> _twinkleTargets = new();
    /// <summary>只属于花瓣的星光/漂浮:花瓣收起时停掉,大圆的星光保留。</summary>
    private readonly List<System.Windows.UIElement> _petalTwinkleTargets = new();
    private Ellipse? _fxFlash;

    // ---- 圆弧动画参数(FR-12) ----
    // 五花瓣全部叠于大圆中心,展开时各自沿"自己的顶点方向"直线飞出(不再扫过圆弧):
    // 扫弧会让相邻花瓣在半路交叉重叠,改成固定朝向 + 平移距离动画后互不影响。
    private const double ArcRadius = 100;     // 花瓣中心距大圆心(用户要求"离大圆近一点点")
    private const double PetalCenter = 37;    // 花瓣 74px 的一半:旋转/缩放的基准点
    // 槽位角(屏幕坐标,Y 向下;顶点朝上的正五边形,相邻 72°):
    // 更多=正上 90° / 登录=左上 162° / 下载=右上 18° / 设置=左下 234° / 版本=右下 306°
    private static readonly double[] TargetAngles = { 162, 18, 234, 306, 90 };
    private const double SweepDeg = 72;        // 扫过的弧长 = 相邻顶点角距(五条弧段互不重叠)
    private const double OvershootDeg = 5;     // 越过目标角后折返的幅度(用户设定)

    public WidgetWindow()
    {
        InitializeComponent();
        Icon = AppIcon.Image;
        ApplySavedPosition();
        RefreshOrbGlyph();

        // LittleSkin 仪表盘的「外置登录」卡片可直接拖进来:识别 URL → 打开登录页预填
        AllowDrop = true;
        PreviewDragOver += (_, e) =>
        {
            if (!AuthServerDrop.TryGetUrl(e.Data))
            {
                e.Effects = DragDropEffects.None;
                DropHint.Visibility = Visibility.Collapsed;
                e.Handled = true;
                return;
            }
            e.Effects = DragDropEffects.Copy;
            DropHint.Visibility = Visibility.Visible;
            e.Handled = true;
        };
        PreviewDragLeave += (_, _) => DropHint.Visibility = Visibility.Collapsed;
        Drop += async (_, e) =>
        {
            DropHint.Visibility = Visibility.Collapsed;
            if (!AuthServerDrop.TryGetUrl(e.Data, out var url)) return;
            e.Handled = true;
            await AuthServerDrop.AddAsync(url, this);
        };
        // 花瓣初始隐藏（逐实例变换,便于动画）
        for (var i = 0; i < Petals().Length; i++)
        {
            Petals()[i].RenderTransform = BuildPetalTransform(i, collapsed: true);
            Petals()[i].Opacity = 0;
        }
        // 悬停判定(替代旧的 SafeArea.MouseEnter / Window.MouseLeave):
        //   展开 = 鼠标真的压在"圆盘"上(半径 40 的圆,不是 240 的安全区方框);
        //   收回 = 离开"以大圆圆心为中心、边心距 50px 的正五边形",且不在任何花瓣上。
        // 旧实现按 240 方框判定,而圆心在方框内偏了 18px → 左/上感应范围比右/下大。
        _hoverTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _hoverTimer.Tick += (_, _) => UpdateHover();

        // 启动特效:大圆挂 [Scale, Translate](顺序不能反:先缩放后平移,平移才不会被缩放),
        // 16ms 计时器统一驱动呼吸/脉冲/火花(OrbExplosion.cs 的技术)。
        _orbScale = new ScaleTransform(1, 1) { CenterX = OrbSide / 2, CenterY = OrbSide / 2 };
        _orbShift = new TranslateTransform();
        var orbFx = new TransformGroup();
        orbFx.Children.Add(_orbScale);
        orbFx.Children.Add(_orbShift);
        Orb.RenderTransform = orbFx;

        // 预渲染光晕在大圆 Grid 之外(SafeArea 里),呼吸/脉冲的缩放平移要同步镜像过去
        _orbBodyScale = new ScaleTransform(1, 1, 250, 250);
        _orbBodyShift = new TranslateTransform();
        var bodyFx = new TransformGroup();
        bodyFx.Children.Add(_orbBodyScale);
        bodyFx.Children.Add(_orbBodyShift);
        OrbGlowWrap.RenderTransform = bodyFx;
        _fxTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _fxTimer.Tick += (_, _) => FxTick();

        // 不可见(收进托盘/启动后隐藏)时暂停星光与漂浮:窗口隐藏不渲染,
        // 但 WPF 动画仍每帧空转;可见恢复时原样续播(帧率不变,只是不白烧)。
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                // 装饰动画由 Resume 续播;无交互门复位,重新给一段宽限再判定
                _decorPaused = false;
                _cursorLastNearAt = DateTime.Now;
                ResumeIdleAnimations();
            }
            else PauseIdleAnimations();
        };

        // 模板实例化发生在首次渲染,故延迟到 Background 优先级再启动星光
        Loaded += (_, _) => Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (!_twinkleStarted) StartTwinkle();
                if (!_iconStarted) { _iconStarted = true; SetupPetalIcons(); }
                if (!_floatStarted) { _floatStarted = true; SetupPetalFloat(); }
                _hoverTimer.Start();
                LogPerformance();
            }),
            System.Windows.Threading.DispatcherPriority.Background);

    }

    /// <summary>
    /// 每 50ms 按"圆心距离"判定一次:压在圆盘上就展开,离开正方形且不在花瓣上就收回。
    /// 用坐标判定而不是 MouseEnter/Leave —— 分层窗口里花瓣和大圆互相覆盖,
    /// 进出事件会在缝隙处抖动(旧实现"半距离收回"就是这么来的)。
    /// </summary>
    private void UpdateHover()
    {
        if (!IsVisible) return;
        UpdateDecorGate();
        if (_decorPaused) return;   // 光标在远处:悬浮逻辑不必走,此时鼠标位置也是过期的
        var p = Mouse.GetPosition(SafeArea);
        if (double.IsNaN(p.X) || double.IsNaN(p.Y)) return;

        var dx = p.X - OrbCenterInSafe;
        var dy = p.Y - OrbCenterInSafe;

        if (!_petalsOpen)
        {
            _closeDueAt = null;
            // 只认圆盘本身:方框四角(距圆心 56px)不算,和"接触到圆"一致。
            // 半径随特效缩放:呼吸 1.06、脉冲 1.20 时圆盘变大,命中范围跟着变大。
            var r = OrbSide / 2 * _currentScale;
            if (dx * dx + dy * dy <= r * r) OpenPetals();
            return;
        }

        // 还在五边形里,或已经摸到某个花瓣(花瓣在五边形之外)→ 取消待收回
        if (InsideKeepArea(dx, dy) || Petals().Any(b => b.IsMouseOver))
        {
            _closeDueAt = null;
            return;
        }

        // 离开判定区:先给 1.5s 宽限,期间移到花瓣上还来得及
        if (_closeDueAt == null)
        {
            _closeDueAt = DateTime.Now.AddMilliseconds(CloseGraceMs);
            return;
        }
        if (DateTime.Now < _closeDueAt.Value) return;

        _closeDueAt = null;
        ClosePetals();
    }

    /// <summary>
    /// 收回判定区:以圆心为中心的正五边形(顶点朝上,与五个花瓣槽位同向)。
    /// 判断方式 = 点到五条边的外法线距离都不超过边心距(凸多边形半平面判定)。
    /// </summary>
    private static bool InsideKeepArea(double dx, double dy)
    {
        for (var k = 0; k < 5; k++)
        {
            // 边中点方向(外法线):顶点朝上时依次为 126/198/270/342/54 度
            var angle = (126 + k * 72) * Math.PI / 180.0;
            var nx = Math.Cos(angle);
            var ny = -Math.Sin(angle);      // 屏幕 Y 向下
            if (dx * nx + dy * ny > KeepOpenApothem) return false;
        }
        return true;
    }

    // ---------- 无交互暂停 ----------

    /// <summary>
    /// 由 20Hz 悬停计时器驱动:光标回到窗口周围立即续播装饰动画;
    /// 离开超过 IdlePauseAfterMs 就定格(星光/漂浮每帧重绘是常驻开销,没人看时是白烧)。
    /// </summary>
    private void UpdateDecorGate()
    {
        var near = IsCursorNearWidget();
        if (near)
        {
            _cursorLastNearAt = DateTime.Now;
            if (_decorPaused)
            {
                _decorPaused = false;
                ResumeIdleAnimations();
                Boot("[fx] decor resumed (cursor near)");
            }
            return;
        }
        if (!_decorPaused && (DateTime.Now - _cursorLastNearAt).TotalMilliseconds >= IdlePauseAfterMs)
        {
            _decorPaused = true;
            PauseIdleAnimations(stopHoverTimer: false);   // 计时器留着当唤醒探测器
            Boot("[fx] decor paused (cursor away)");
        }
    }

    /// <summary>光标是否落在窗口屏幕矩形外扩 48px 内。查询失败按"近"处理:宁可多烧也不冻结交互。</summary>
    private bool IsCursorNearWidget()
    {
        try
        {
            if (!NativeMethods.GetCursorPos(out var pt)) return true;
            const int margin = 48;
            var tl = SafeArea.PointToScreen(new Point(-margin, -margin));
            var br = SafeArea.PointToScreen(new Point(SafeArea.ActualWidth + margin, SafeArea.ActualHeight + margin));
            return pt.X >= tl.X && pt.X <= br.X && pt.Y >= tl.Y && pt.Y <= br.Y;
        }
        catch
        {
            return true;
        }
    }

    // ---------- 启动特效:呼吸 → 成功脉冲 / 失败收缩 ----------

    /// <summary>启动开始:进入呼吸态(幅度/频率随进度渐增)。</summary>
    public void NotifyLaunchStarted()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _fxProgress = 0.05;
            _wobblePhase = 0;
            _fxState = LaunchFxState.Breathing;
            _fxPhaseStart = DateTime.Now;
            if (!_fxTimer.IsEnabled) _fxTimer.Start();
            Boot("[fx] breathing");
        }));
    }

    /// <summary>启动进度推进(0..1,封顶 0.95,留到确认主菜单后才算成功)。</summary>
    public void NotifyLaunchProgress(double progress)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_fxState != LaunchFxState.Breathing) return;
            _fxProgress = Math.Max(_fxProgress, Math.Min(0.95, progress));
        }));
    }

    /// <summary>游戏进入主菜单:猛地放大到 1.20 再回弹,伴白闪 + 光源粒子(用户拍板 1.20 够猛)。</summary>
    public void NotifyLaunchSucceeded()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_fxState != LaunchFxState.Breathing) return;
            _pulseFromScale = _currentScale;
            _fxState = LaunchFxState.Success;
            _fxPhaseStart = DateTime.Now;
            SpawnSuccessFx();
            Boot("[fx] success pulse");
        }));
    }

    /// <summary>启动失败:收缩变暗一下再复原(用户拍板的失败反馈)。</summary>
    public void NotifyLaunchFailed()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_fxState != LaunchFxState.Breathing) return;
            _pulseFromScale = _currentScale;
            _fxState = LaunchFxState.Fail;
            _fxPhaseStart = DateTime.Now;
            Boot("[fx] fail pulse");
        }));
    }

    /// <summary>
    /// 切换预渲染光晕档位(最多 30fps):呼吸/脉冲只改位图选择,不再每帧重跑 120px 模糊。
    /// 低特效模式下固定静息档,只保留缩放和亮度变化。
    /// </summary>
    private void ApplyGlowFrame(double blurRadius)
    {
        if (Performance.LowEffects) blurRadius = GlowCache.FrameRadii[0];
        if ((DateTime.Now - _lastGlowAt).TotalMilliseconds < 33) return;
        var index = GlowCache.IndexFor(blurRadius);
        if (index == _glowFrame) return;
        var frame = GlowCache.FrameAt(index);
        if (frame == null) return;   // 后台烘焙还没好,保持当前档位
        _glowFrame = index;
        OrbBody.Source = frame;
        _lastGlowAt = DateTime.Now;
    }

    /// <summary>特效每帧:一个计时器驱动所有状态(OrbExplosion.cs 的技术)。</summary>
    private void FxTick()
    {
        var t = (DateTime.Now - _fxPhaseStart).TotalMilliseconds;
        switch (_fxState)
        {
            case LaunchFxState.Breathing:
            {
                var p = Math.Max(0.05, _fxProgress);
                // 积分法变频(orb-explosion.html):相位累加,频率渐增时曲线不跳变
                _wobblePhase += 2 * Math.PI * (0.8 + 1.7 * p) * 0.016;
                var amp = 0.015 + 0.045 * p * p;              // 1.5% → 6%,p² 缓增
                var wob = Math.Sin(_wobblePhase);
                ApplyOrb(1 + amp * wob, jitter: p > 0.75 ? (p - 0.75) / 0.25 : 0);

                // 辉光随波动增强:换成对应模糊档位(30fps 上限),不再逐帧改 BlurRadius
                var boost = 2.0 * p * p + 0.5 * Math.Max(0, wob) * p;
                ApplyGlowFrame(120 * (1 + 0.12 * boost));

                break;
            }

            case LaunchFxState.Success:
            {
                const int UpMs = 120, DownMs = 380;
                if (t < UpMs)
                {
                    var k = t / UpMs;
                    k = 1 - (1 - k) * (1 - k);   // easeOutQuad:猛地拉起
                    ApplyOrb(_pulseFromScale + (1.20 - _pulseFromScale) * k, 0);
                }
                else
                {
                    var x = Math.Min(1, (t - UpMs) / DownMs);
                    ApplyOrb(1.20 - 0.20 * EaseOutBack(x), 0);   // 回弹略过冲
                }

                // 亮度爆闪:60ms 猛拉满 → 保持一瞬 → 380ms 回落(大圆图标整体提亮)。
                // 关闭「启动后保持显示」时,过峰值后不再回落:让爆闪亮到收进托盘那一刻,
                // 免得先看到一颗常态大圆再消失。
                var hideOnLaunch = !AppServices.Config.KeepVisibleOnLaunch;
                var level = FlashCurve(t) / FlashPeak;
                if (hideOnLaunch && t >= 140) level = 1;
                OrbFlash.Opacity = FlashPeak * level;

                // 白色径向闪光:0.3s 内淡出(用户要求保留的扩散感)
                if (_fxFlash != null)
                {
                    var fk = Math.Min(1, t / 300.0);
                    _fxFlash.Opacity = 0.46 * (1 - fk);
                    var grow = 1 + 1.4 * fk;
                    var half = 80 * grow;
                    _fxFlash.Width = _fxFlash.Height = half * 2;
                    Canvas.SetLeft(_fxFlash, OrbCenterInWindow.X - half);
                    Canvas.SetTop(_fxFlash, OrbCenterInWindow.Y - half);
                }

                // 光晕同步 flare:同一把攻击曲线,略慢回落
                ApplyGlowFrame(120 + 90 * level);

                // 爆闪播完(白闪已淡出、亮度仍在线)后收进托盘,所有面板一起关掉
                if (hideOnLaunch && t >= HideAfterLaunchMs)
                {
                    HideAfterLaunchFx();
                    break;
                }

                if (t > FxTotalMs) FinishFx();
                break;
            }

            case LaunchFxState.Fail:
            {
                const int ShrinkMs = 160, HoldMs = 240, BackMs = 360;
                if (t < ShrinkMs)
                {
                    var k = 1 - (1 - t / ShrinkMs) * (1 - t / ShrinkMs);
                    ApplyOrb(_pulseFromScale + (0.88 - _pulseFromScale) * k, 0);
                    Orb.Opacity = 1;
                    OrbGlowWrap.Opacity = 1;
                }
                else if (t < ShrinkMs + HoldMs)
                {
                    ApplyOrb(0.88, 0);
                    Orb.Opacity = 1 - 0.45 * ((t - ShrinkMs) / HoldMs);   // 变暗
                    OrbGlowWrap.Opacity = Orb.Opacity;
                }
                else
                {
                    var x = Math.Min(1, (t - ShrinkMs - HoldMs) / BackMs);
                    var e = x * x * (3 - 2 * x);
                    ApplyOrb(0.88 + 0.12 * e, 0);
                    Orb.Opacity = 0.55 + 0.45 * e;
                    OrbGlowWrap.Opacity = Orb.Opacity;
                    if (x >= 1) { FinishFx(); break; }
                }
                if (t > 1200) FinishFx();   // 兜底
                break;
            }
        }
    }

    private void ApplyOrb(double scale, double jitter)
    {
        _currentScale = scale;
        if (_orbScale != null)
        {
            _orbScale.ScaleX = scale;
            _orbScale.ScaleY = scale;
        }
        if (_orbBodyScale != null)
        {
            _orbBodyScale.ScaleX = scale;
            _orbBodyScale.ScaleY = scale;
        }
        if (_orbShift != null)
        {
            if (jitter <= 0 || _dragging)
            {
                _orbShift.X = 0;
                _orbShift.Y = 0;
                if (_orbBodyShift != null) { _orbBodyShift.X = 0; _orbBodyShift.Y = 0; }
            }
            else
            {
                var j = 2.5 * jitter;
                _orbShift.X = (_floatRnd.NextDouble() * 2 - 1) * j;
                _orbShift.Y = (_floatRnd.NextDouble() * 2 - 1) * j;
                if (_orbBodyShift != null)
                {
                    _orbBodyShift.X = _orbShift.X;
                    _orbBodyShift.Y = _orbShift.Y;
                }
            }
        }
    }

    private void SpawnSuccessFx()
    {
        // 白色径向闪光(径向渐变到透明,不会出现方形边)
        _fxFlash = new Ellipse { Width = 160, Height = 160, Opacity = 0.5 };
        _fxFlash.Fill = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0),
                new GradientStop(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF), 0.45),
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1),
            },
        };
        Canvas.SetLeft(_fxFlash, OrbCenterInWindow.X - 80);
        Canvas.SetTop(_fxFlash, OrbCenterInWindow.Y - 80);
        FxLayer.Children.Add(_fxFlash);
    }

    /// <summary>爆闪播完后收起:关掉所有面板再隐藏组件。不写 ShowWidget(下次启动仍正常显示)。</summary>
    private void HideAfterLaunchFx()
    {
        try { PanelWindow.CloseAll(); } catch (Exception) { }
        FinishFx();
        Hide();
        Boot("[fx] hidden after launch");
    }

    private void FinishFx()
    {
        _fxTimer.Stop();
        _fxState = LaunchFxState.Idle;
        ApplyOrb(1, 0);
        Orb.Opacity = 1;
        OrbGlowWrap.Opacity = 1;
        OrbFlash.Opacity = 0;
        // 回到静息档光晕
        _glowFrame = 0;
        OrbBody.Source = GlowCache.FrameAt(0);
        _lastGlowAt = DateTime.MinValue;
        FxLayer.Children.Clear();
        _fxFlash = null;
        Boot("[fx] idle");
    }

    private const double FlashPeak = 0.82;    // 爆闪峰值亮度(1 = 纯白;用户要求别太白了)
    private const double FxTotalMs = 620;     // 成功特效总时长(爆闪完全落回)
    private const double HideAfterLaunchMs = 360;   // 收起模式:白闪(300ms)淡出后再亮 60ms,然后收进托盘

    /// <summary>爆闪曲线:60ms 猛拉满、保持到 140ms、520ms 落回 0。</summary>
    private static double FlashCurve(double ms)
    {
        const double Attack = 60, Hold = 140, End = 520;
        if (ms <= 0) return 0;
        if (ms < Attack)
        {
            var k = ms / Attack;
            return FlashPeak * (1 - (1 - k) * (1 - k));   // easeOutQuad:一瞬间拉到最亮
        }
        if (ms < Hold) return FlashPeak;
        if (ms < End)
        {
            var k = 1 - (ms - Hold) / (End - Hold);
            return FlashPeak * k * k * k;                 // 三次方回落:前快后缓
        }
        return 0;
    }

    private static double EaseOutBack(double x)
    {
        const double c1 = 1.70158, c3 = c1 + 1;
        return 1 + c3 * Math.Pow(x - 1, 3) + c1 * Math.Pow(x - 1, 2);
    }

    private Button[] Petals() =>
        new[] { PetalLogin, PetalDownload, PetalSettings, PetalVersions, PetalMore };

    // WidgetWindow.xaml 布局常量:窗口 460×380,SafeArea 240×240 居中,大圆 80×80 位于 SafeArea 内 (98,98)。
    // 开屏动画要在大圆的真实圆心结晶,必须用这组常量推导,不能假设圆心就是窗口中心。
    private const double WindowW = 460, WindowH = 380, SafeAreaSide = 240, OrbSide = 80, OrbInSafe = 98;

    /// <summary>大圆圆心相对组件窗口左上角的偏移(DIP)。改 XAML 布局时必须同步这里。</summary>
    public static Point OrbCenterInWindow => new(
        (WindowW - SafeAreaSide) / 2 + OrbInSafe + OrbSide / 2,
        (WindowH - SafeAreaSide) / 2 + OrbInSafe + OrbSide / 2);

    /// <summary>大圆的真实屏幕圆心(DIP)。须在窗口 Show 并完成布局后调用。</summary>
    public Point OrbCenterOnScreen =>
        Orb.PointToScreen(new Point(Orb.ActualWidth / 2, Orb.ActualHeight / 2));

    /// <summary>按当前主色重绘苦力怕浮雕(换主题时由 ApplyTheme 调用)。</summary>
    public void RefreshOrbGlyph()
    {
        try
        {
            OrbCarved.Source = OrbGlyph.RenderWhiteFace();
        }
        catch (Exception)
        {
            // 渲染失败时保持素圆面,不影响启动功能
        }

        // 主题换色会重烘光晕:换回新的静息档,并重挂星光
        _glowFrame = 0;
        OrbBody.Source = GlowCache.FrameAt(0);
        _lastGlowAt = DateTime.MinValue;

        // 星光动画挂在预渲染图上,换帧后重新挂一次即可
        if (IsLoaded) StartTwinkle();
    }

    private bool _twinkleStarted;

    // ---------- 花瓣漂浮 / 悬浮放大 ----------

    private const double FloatRange = 7;      // 漂浮半径(px):五花瓣中心间距 117px、净空 43px,不会互撞
    private const double HoverScale = 1.25;   // 悬浮放大:半径 28 → 35(放大 1/4)

    private readonly TranslateTransform?[] _floatOffsets = new TranslateTransform?[5];
    private readonly Random _floatRnd = new Random(20261101);
    private bool _floatStarted;
    private bool _iconStarted;

    /// <summary>把花瓣上的文字换成纯白线条图标(书/齿轮/下载/人形)。</summary>
    private void SetupPetalIcons()
    {
        try
        {
            foreach (var petal in Petals())
            {
                var name = petal.Tag as string;  // login / download / settings / versions
                if (string.IsNullOrEmpty(name)) continue;
                // 换成图标后 Button 没有了文字,补回无障碍名称(自动化与屏幕阅读器都依赖它)
                var label = name switch
                {
                    "login" => "登录",
                    "download" => "下载",
                    "settings" => "设置",
                    "more" => "更多",
                    _ => "版本",
                };
                System.Windows.Automation.AutomationProperties.SetName(petal, label);
                var geometry = PetalIcons.Get(name!);
                if (geometry == null) continue;    // 取不到就保留原标题
                var icon = new Path
                {
                    Data = geometry,
                    Stroke = Brushes.White,
                    StrokeThickness = 1.2,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    Fill = null,
                    Width = 40,
                    Height = 40,
                    Stretch = Stretch.Uniform,
                    IsHitTestVisible = false,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                petal.Content = icon;
            }
            Boot("petal icons ready");
        }
        catch (Exception ex)
        {
            Boot("petal icons failed: " + ex.GetType().Name + " " + ex.Message);
        }
    }

    /// <summary>给每个花瓣挂上随机漂浮并接管悬浮/点击交互。</summary>
    private void SetupPetalFloat()
    {
        try
        {
            var petals = Petals();
            for (var i = 0; i < petals.Length; i++)
            {
                var petal = petals[i];
                if (petal.RenderTransform is not TransformGroup group) continue;

                var offset = new TranslateTransform(0, 0);
                group.Children.Add(offset);
                _floatOffsets[i] = offset;
                // 收起状态下花瓣缩放到 0,不需要漂浮;低特效也不跑
                if (_petalsOpen && !Performance.LowEffects) StartPetalFloat(i, 0, 0);

                petal.MouseEnter += (_, _) =>
                {
                    _closeDueAt = null;   // 摸到花瓣:取消"离开正方形 1.5s 后收回"
                    OnPetalHover(petal, true);
                };
                petal.MouseLeave += (_, _) => OnPetalHover(petal, false);
                // 点击后立刻回到常态(面板随即盖住花瓣)
                petal.Click += (_, _) =>
                {
                    _closeDueAt = null;   // 点中花瓣同样不收回
                    OnPetalHover(petal, false);
                };
            }
            Boot($"petal float ready ({petals.Length})");
        }
        catch (Exception ex)
        {
            Boot("petal float failed: " + ex.GetType().Name + " " + ex.Message);
        }
    }

    /// <summary>
    /// 从当前位置开始新一轮随机漂浮。必须用 BeginAnimation 直接作用在 TranslateTransform 上:
    /// RenderTransform 里的 Freezable 不在可视树中,Storyboard.Begin(element) 驱动不了它。
    /// </summary>
    private void StartPetalFloat(int index, double fromX, double fromY)
    {
        var offset = _floatOffsets[index];
        if (offset == null) return;
        var duration = TimeSpan.FromSeconds(3.2 + _floatRnd.NextDouble() * 2.4);
        offset.BeginAnimation(TranslateTransform.XProperty, FloatKeys(fromX, duration));
        offset.BeginAnimation(TranslateTransform.YProperty, FloatKeys(fromY, duration));
    }

    private DoubleAnimationUsingKeyFrames FloatKeys(double from, TimeSpan duration)
    {
        var anim = new DoubleAnimationUsingKeyFrames
        {
            Duration = duration,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Timeline.SetDesiredFrameRate(anim, 30);   // 3~6 秒的慢漂浮,30fps 看不出差别
        var seconds = duration.TotalSeconds;
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(from, TimeSpan.Zero));
        for (var k = 1; k <= 3; k++)
        {
            var value = (_floatRnd.NextDouble() * 2 - 1) * FloatRange;
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(value,
                TimeSpan.FromSeconds(seconds * k / 4.0),
                new SineEase { EasingMode = EasingMode.EaseInOut }));
        }
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(from, TimeSpan.FromSeconds(seconds)));
        return anim;
    }

    /// <summary>把漂浮停在当前偏移上(直接移除动画会瞬间弹回 0)。</summary>
    private void FreezePetalFloat(int index)
    {
        var offset = _floatOffsets[index];
        if (offset == null) return;
        var x = offset.X;
        var y = offset.Y;
        var hold = TimeSpan.FromMilliseconds(1);
        offset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, x, hold));
        offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, y, hold));
    }

    /// <summary>悬浮:停住漂浮并把圆放大 1/4;离开或点击:恢复漂浮与原始大小。</summary>
    private void OnPetalHover(Button petal, bool entering)
    {
        try
        {
            var index = Array.IndexOf(Petals(), petal);
            if (index < 0) return;

            if (entering)
            {
                FreezePetalFloat(index);
            }
            else if (!Performance.LowEffects && _petalsOpen)
            {
                var offset = _floatOffsets[index];
                StartPetalFloat(index, offset?.X ?? 0, offset?.Y ?? 0);
            }

            var scale = petal.Template?.FindName("petalHoverScale", petal) as ScaleTransform;
            if (scale == null) return;

            var anim = new DoubleAnimation(entering ? HoverScale : 1.0,
                TimeSpan.FromMilliseconds(entering ? 140 : 180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
        catch (Exception)
        {
            // 纯装饰:失败也不能影响点击花瓣打开面板
        }
    }

    /// <summary>
    /// 星光:圆面亮度做正弦往复,五个圆各自错开相位,像星星各自明灭。
    ///
    /// 注意:星光动画作用在预渲染的光晕图上,不要再给图挂 Effect——那会把模糊重新拉回每帧路径。
    /// </summary>
    private void StartTwinkle()
    {
        try
        {
            _twinkleTargets.Clear();
            _petalTwinkleTargets.Clear();

            if (Performance.LowEffects)
            {
                OrbBody.BeginAnimation(OpacityProperty, null);
                OrbBody.Opacity = 0.9;
                foreach (var petal in Petals())
                    if (TemplateChild(petal, "glowPulse") is System.Windows.UIElement glow) glow.Opacity = 1.0;
                _twinkleStarted = true;
                Boot("twinkle skipped (low effects)");
                return;
            }

            OrbBody.BeginAnimation(OpacityProperty, Twinkle(0.80, 1.0, TimeSpan.Zero));
            _twinkleTargets.Add(OrbBody);

            for (var i = 0; i < Petals().Length; i++)
            {
                var glow = TemplateChild(Petals()[i], "glowPulse");
                if (glow == null) continue;
                _petalTwinkleTargets.Add(glow);
            }
            Boot($"twinkle started (petals {_petalTwinkleTargets.Count}/{Petals().Length})");
            _twinkleStarted = true;

            // 花瓣收起时它们不可见,星光留到展开再跑
            if (_petalsOpen) StartPetalTwinkle();
        }
        catch (Exception ex)
        {
            // 星光只是装饰:失败也绝不能影响启动器的正常使用
            Boot("twinkle failed: " + ex.GetType().Name + " " + ex.Message);
        }
    }

    /// <summary>花瓣的星光只在展开时跑(收起后缩放到 0,跑了也看不见)。</summary>
    private void StartPetalTwinkle()
    {
        if (Performance.LowEffects || !_petalsOpen) return;
        for (var i = 0; i < _petalTwinkleTargets.Count; i++)
            _petalTwinkleTargets[i].BeginAnimation(
                OpacityProperty, Twinkle(0.28, 1.0, TimeSpan.FromMilliseconds(i * 470)));
    }

    private void StopPetalTwinkle()
    {
        foreach (var glow in _petalTwinkleTargets)
        {
            var held = glow.Opacity;
            glow.BeginAnimation(OpacityProperty, null);
            glow.Opacity = held > 0 ? held : 1.0;
        }
    }

    private static System.Windows.UIElement? TemplateChild(Button button, string name)
    {
        button.ApplyTemplate();
        return button.Template?.FindName(name, button) as System.Windows.UIElement;
    }

    /// <summary>设置页切换低特效后立即生效:停掉/恢复星光与漂浮,光晕回到静息档。</summary>
    public void ApplyPerformanceMode()
    {
        try
        {
            if (Performance.LowEffects) PauseIdleAnimations(stopHoverTimer: false);
            else
            {
                _decorPaused = false;
                _cursorLastNearAt = DateTime.Now;
                ResumeIdleAnimations();
            }

            if (Performance.LowEffects)
            {
                OrbBody.BeginAnimation(OpacityProperty, null);
                OrbBody.Opacity = 0.9;
                _glowFrame = 0;
                OrbBody.Source = GlowCache.FrameAt(0);
                foreach (var glow in _petalTwinkleTargets)
                {
                    glow.BeginAnimation(OpacityProperty, null);
                    glow.Opacity = 1.0;
                }
            }
            Boot("[perf] low effects=" + Performance.LowEffects);
        }
        catch (Exception)
        {
            // 装饰开关失败不影响使用
        }
    }

    /// <summary>
    /// 记录渲染档位/DPI/远程会话与 2 秒空闲帧率。支持远程排查:
    /// tier&lt;2 或远程会话就是"所有 WPF 动画一起卡"的典型环境。
    /// </summary>
    private void LogPerformance()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            Boot($"[perf] tier={Performance.Tier} dpi={dpi.DpiScaleX:0.##} "
                 + $"remote={Performance.RemoteSession} low={Performance.LowEffects}");

            var frames = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            EventHandler onRendering = (_, _) => frames++;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
            CompositionTarget.Rendering += onRendering;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                CompositionTarget.Rendering -= onRendering;
                var fps = frames / Math.Max(0.1, sw.Elapsed.TotalSeconds);
                Boot($"[perf] frames={frames} in {sw.Elapsed.TotalSeconds:0.0}s ({fps:0.#} fps)");
            };
            timer.Start();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>暂停装饰动画:漂浮定格在当前偏移、星光定格在当前亮度。stopHoverTimer=false 时悬停计时器继续走(无交互暂停靠它唤醒)。</summary>
    private void PauseIdleAnimations(bool stopHoverTimer = true)
    {
        try
        {
            if (_floatStarted && _petalsOpen)
                for (var i = 0; i < _floatOffsets.Length; i++)
                    FreezePetalFloat(i);
            foreach (var el in _twinkleTargets.Concat(_petalTwinkleTargets))
            {
                var held = el.Opacity;                       // 动画驱动的当前值
                el.BeginAnimation(OpacityProperty, null);    // 摘掉动画(会弹回基准值)
                el.Opacity = held;                           // 立刻定格,肉眼无跳变
            }
            if (stopHoverTimer) _hoverTimer.Stop();
        }
        catch (Exception)
        {
            // 纯装饰:暂停失败不影响任何功能
        }
    }

    /// <summary>恢复可见:星光/漂浮按原有节奏续播(帧率不变)。</summary>
    private void ResumeIdleAnimations()
    {
        try
        {
            if (_twinkleStarted) StartTwinkle();
            if (_floatStarted && _petalsOpen && !Performance.LowEffects)
                for (var i = 0; i < _floatOffsets.Length; i++)
                {
                    var off = _floatOffsets[i];
                    if (off != null) StartPetalFloat(i, off.X, off.Y);
                }
            if (IsLoaded && !_hoverTimer.IsEnabled) _hoverTimer.Start();
        }
        catch (Exception)
        {
            // 纯装饰:恢复失败不影响任何功能
        }
    }

    private static DoubleAnimation Twinkle(double from, double to, TimeSpan begin)
    {
        var anim = new DoubleAnimation(from, to, TimeSpan.FromSeconds(1.9))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = begin,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        // 慢速正弦 30fps 足够,分层窗口每帧的呈现成本直接减半
        Timeline.SetDesiredFrameRate(anim, 30);
        return anim;
    }

    private void ApplySavedPosition()
    {
        var c = AppServices.Config;
        if (c.WidgetLeft < 0 || c.WidgetTop < 0)
        {
            // 默认桌面右下
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 40;
            Top = area.Bottom - Height - 40;
        }
        else
        {
            Left = c.WidgetLeft;
            Top = c.WidgetTop;
        }
    }

    // ---------- 花瓣展开/收起 ----------

    private void OpenPetals()
    {
        if (_petalsOpen) return;
        _petalsOpen = true;
        Boot("OpenPetals");
        AnimatePetals(open: true);
        StartPetalTwinkle();
        if (_floatStarted && !Performance.LowEffects)
            for (var i = 0; i < _floatOffsets.Length; i++)
                if (_floatOffsets[i] != null) StartPetalFloat(i, _floatOffsets[i]!.X, _floatOffsets[i]!.Y);
    }

    private void ClosePetals()
    {
        if (!_petalsOpen) return;
        _closeDueAt = null;
        // 面板打开时保持展开,避免遮住正在使用的面板(先判再记日志,免得每 1.5s 刷一条)
        if (PanelWindow.AnyPanelOpen) return;
        Boot("ClosePetals");
        _petalsOpen = false;
        // 收起后花瓣不可见:星光/漂浮停掉,不再每帧空转
        StopPetalTwinkle();
        if (_floatStarted)
            for (var i = 0; i < _floatOffsets.Length; i++)
                FreezePetalFloat(i);
        AnimatePetals(open: false);
    }

    /// <summary>
    /// 花瓣变换组:[Scale, Rotate(θ), Translate(R,0), Rotate(-θ)]。
    /// 平移恒为 R、θ 从 (顶点+72°) 扫到顶点 → 花瓣沿圆弧旋转出来;
    /// 72° 恰好等于相邻顶点的角距,五条弧段首尾相接、互不重叠。
    /// 收起时 scale=0:零缩放没有命中面积,不会出现"看不见的花瓣挡住鼠标"。
    /// 注意:两个 Rotate 的中心都是 (37,37)(花瓣中心),缺一即偏离画面。
    /// </summary>
    private static TransformGroup BuildPetalTransform(int index, bool collapsed)
    {
        var angle = TargetAngles[index];
        var start = angle + SweepDeg;
        var group = new TransformGroup();
        group.Children.Add(new ScaleTransform(collapsed ? 0 : 1, collapsed ? 0 : 1, PetalCenter, PetalCenter));
        group.Children.Add(new RotateTransform(collapsed ? start : angle, PetalCenter, PetalCenter));
        group.Children.Add(new TranslateTransform(ArcRadius, 0));
        group.Children.Add(new RotateTransform(collapsed ? -start : -angle, PetalCenter, PetalCenter));
        return group;
    }

    private void AnimatePetals(bool open)
    {
        const int DurationMs = 460;
        for (var i = 0; i < Petals().Length; i++)
        {
            var petal = Petals()[i];
            var angle = TargetAngles[i];
            var start = angle + SweepDeg;

            var group = petal.RenderTransform as TransformGroup ?? BuildPetalTransform(i, collapsed: true);
            petal.RenderTransform = group;
            var scale = (ScaleTransform)group.Children[0];
            var rot = (RotateTransform)group.Children[1];
            var rotBack = (RotateTransform)group.Children[3];

            if (open)
            {
                // 从 (顶点+72°) 扫到顶点:越过目标 5° 后折返,精确停在顶点
                // (原来的 BackEase 过冲十几度,正好压到隔壁花瓣,用户要求收到 5°)
                var frames = new DoubleAnimationUsingKeyFrames
                {
                    Duration = TimeSpan.FromMilliseconds(DurationMs),
                    BeginTime = Stagger(i),
                };
                frames.KeyFrames.Add(new LinearDoubleKeyFrame(
                    start, KeyTime.FromPercent(0)));
                frames.KeyFrames.Add(new EasingDoubleKeyFrame(
                    angle - OvershootDeg, KeyTime.FromPercent(0.78),
                    new CubicEase { EasingMode = EasingMode.EaseOut }));
                frames.KeyFrames.Add(new EasingDoubleKeyFrame(
                    angle, KeyTime.FromPercent(1),
                    new QuadraticEase { EasingMode = EasingMode.EaseInOut }));

                // 反向旋转保持 -θ,抵消朝向变化(两者必须同节奏)
                var backFrames = new DoubleAnimationUsingKeyFrames
                {
                    Duration = TimeSpan.FromMilliseconds(DurationMs),
                    BeginTime = Stagger(i),
                };
                backFrames.KeyFrames.Add(new LinearDoubleKeyFrame(
                    -start, KeyTime.FromPercent(0)));
                backFrames.KeyFrames.Add(new EasingDoubleKeyFrame(
                    -(angle - OvershootDeg), KeyTime.FromPercent(0.78),
                    new CubicEase { EasingMode = EasingMode.EaseOut }));
                backFrames.KeyFrames.Add(new EasingDoubleKeyFrame(
                    -angle, KeyTime.FromPercent(1),
                    new QuadraticEase { EasingMode = EasingMode.EaseInOut }));

                rot.BeginAnimation(RotateTransform.AngleProperty, frames);
                rotBack.BeginAnimation(RotateTransform.AngleProperty, backFrames);

                // 缩放保留弹簧:只放大自身,不会撞到别人
                var animS = new DoubleAnimation(1, TimeSpan.FromMilliseconds(DurationMs))
                {
                    EasingFunction = new BackEase { Amplitude = 1.56, EasingMode = EasingMode.EaseOut },
                    BeginTime = Stagger(i),
                };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, animS);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, animS);
                petal.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160))
                {
                    BeginTime = Stagger(i),
                });
            }
            else
            {
                // 沿圆弧转回 (顶点+72°) 并缩回
                var animA = new DoubleAnimation(start, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
                };
                var animB = new DoubleAnimation(-start, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
                };
                rot.BeginAnimation(RotateTransform.AngleProperty, animA);
                rotBack.BeginAnimation(RotateTransform.AngleProperty, animB);
                var animS = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
                };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, animS);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, animS);
                petal.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
            }
        }
    }

    /// <summary>展开时的错峰:按花瓣顺序逐个弹出(0/60/120/180/240ms)。</summary>
    private static TimeSpan Stagger(int index) => TimeSpan.FromMilliseconds(index * 60);

    // ---------- 花瓣点击 → 面板 ----------

    private void Petal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string kind }) return;
        var petal = Petals().First(p => string.Equals(p.Tag as string, kind, StringComparison.Ordinal));
        var point = petal.PointToScreen(new Point(petal.ActualWidth, petal.ActualHeight / 2));
        var onLeft = kind is "login" or "settings";

        PanelWindow.ShowPanel(kind, point, onLeft, this);
    }

    // ---------- 大圆:拖动 + 单击启动 ----------

    private void Orb_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        _dragStart = e.GetPosition(this);
        Orb.CaptureMouse();
    }

    private void Orb_MouseMove(object sender, MouseEventArgs e)
    {
        if (!Orb.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _dragStart.X) + Math.Abs(pos.Y - _dragStart.Y) > 6) _dragging = true;
        if (_dragging)
        {
            Left += pos.X - _dragStart.X;
            Top += pos.Y - _dragStart.Y;
        }
    }

    private void Orb_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        Orb.ReleaseMouseCapture();
        if (_dragging)
        {
            AppServices.Config.WidgetLeft = Left;
            AppServices.Config.WidgetTop = Top;
            AppServices.SaveConfig();
            _dragging = false;
            return;
        }
        _ = LaunchLastVersionAsync();
    }

    private void Orb_MouseLeave(object sender, MouseEventArgs e)
    {
        if (Orb.IsMouseCaptured && !_dragging) Orb.ReleaseMouseCapture();
    }

    internal static void Boot(string message)
    {
        var dir = System.IO.Path.GetDirectoryName(AppServices.ConfigPath)!;
        System.IO.Directory.CreateDirectory(dir);
        Fuse.Core.Logging.LogSink.Append(System.IO.Path.Combine(dir, "boot.log"),
            $"[{DateTime.Now:HH:mm:ss.fff}] [widget] {message}\n");
    }

    public void SetVisible(bool visible)
    {
        if (visible) { Show(); } else { Hide(); }
    }

    public async Task LaunchLastVersionAsync()
    {
        var installed = InstalledVersionScanner.Scan(
            AppServices.VersionsRoot, AppServices.InstancesRoot, AppServices.LibrariesRoot);
        var versionId = LaunchVersionPicker.Pick(
            AppServices.Config.LastLaunchedVersion,
            installed.Select(v => v.Id).ToList(), out var repaired);
        if (string.IsNullOrEmpty(versionId))
        {
            Boot("launch skipped: no installed version");
            return;
        }
        if (repaired)
        {
            // 上次启动的版本被改名/删除/乱码了:回退到第一个并修掉配置,
            // 否则每次点大圆都会弹"缺少 version json"
            Boot("[launch] last version missing, fallback to " + versionId);
            AppServices.Config.LastLaunchedVersion = versionId;
            AppServices.SaveConfig();
        }

        // 统一走 AppLauncher(带同版本占位):进度回调很密集,只在阶段切换时落日志
        var lastStage = "";
        var lastMessage = "";
        var outcome = await AppLauncher.LaunchAsync(versionId!, msg =>
        {
            lastMessage = msg;
            var stage = msg.Split(' ')[0];
            if (stage == lastStage) return;
            lastStage = stage;
            Boot("launch " + msg);
        });

        if (outcome != LaunchOutcome.AlreadyBusy) return;
        // 同版本已经在跑:把游戏窗口叫到前台并轻提示,避免"点了没反应"
        FocusRunningGame(versionId!, lastMessage);
    }

    private static void FocusRunningGame(string versionId, string message)
    {
        try
        {
            var proc = LaunchState.GetRunning(versionId);
            if (proc != null)
            {
                proc.Refresh();
                var hwnd = proc.MainWindowHandle;
                if (hwnd != IntPtr.Zero)
                {
                    NativeMethods.ShowWindow(hwnd, 9);   // SW_RESTORE
                    NativeMethods.SetForegroundWindow(hwnd);
                }
            }
        }
        catch (Exception)
        {
            // 进程刚退出/句柄失效:忽略,只保留提示
        }
        Toast.ShowNearOrb(string.IsNullOrEmpty(message) ? "该版本已经在运行了" : message);
    }
}

internal partial class NativeMethods
{
    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    internal struct POINT
    {
        public int X;
        public int Y;
    }
}
