using System;
using System.Collections.Generic;
using System.Linq;
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
    private System.Windows.Media.Effects.DropShadowEffect? _glowClone;
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
        _fxTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _fxTimer.Tick += (_, _) => FxTick();

        // 模板实例化发生在首次渲染,故延迟到 Background 优先级再启动星光
        Loaded += (_, _) => Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (!_twinkleStarted) StartTwinkle();
                if (!_iconStarted) { _iconStarted = true; SetupPetalIcons(); }
                if (!_floatStarted) { _floatStarted = true; SetupPetalFloat(); }
                _hoverTimer.Start();
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
            EnsureGlowClone();
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

    private void EnsureGlowClone()
    {
        if (_glowClone != null) return;
        // Resources 里的 Freezable 是冻结的,不能直接动画;克隆一份挂到圆盘上用完即弃
        if (TryFindResource("OrbGlow") is System.Windows.Media.Effects.DropShadowEffect baseFx)
            _glowClone = baseFx.Clone();
        OrbDisc.Effect = _glowClone;
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

                // 辉光随波动增强(克隆的 Effect 不受冻结限制)
                var boost = 2.0 * p * p + 0.5 * Math.Max(0, wob) * p;
                if (_glowClone != null)
                {
                    _glowClone.Opacity = Math.Min(1, 0.9 + 0.10 * boost);
                    _glowClone.BlurRadius = 120 * (1 + 0.12 * boost);
                }

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
                if (_glowClone != null)
                {
                    _glowClone.Opacity = 0.9 + 0.1 * level;
                    _glowClone.BlurRadius = 120 + 90 * level;
                }

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
                }
                else if (t < ShrinkMs + HoldMs)
                {
                    ApplyOrb(0.88, 0);
                    Orb.Opacity = 1 - 0.45 * ((t - ShrinkMs) / HoldMs);   // 变暗
                }
                else
                {
                    var x = Math.Min(1, (t - ShrinkMs - HoldMs) / BackMs);
                    var e = x * x * (3 - 2 * x);
                    ApplyOrb(0.88 + 0.12 * e, 0);
                    Orb.Opacity = 0.55 + 0.45 * e;
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
        if (_orbShift != null)
        {
            if (jitter <= 0 || _dragging)
            {
                _orbShift.X = 0;
                _orbShift.Y = 0;
            }
            else
            {
                var j = 2.5 * jitter;
                _orbShift.X = (_floatRnd.NextDouble() * 2 - 1) * j;
                _orbShift.Y = (_floatRnd.NextDouble() * 2 - 1) * j;
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
        OrbFlash.Opacity = 0;
        // 恢复主题光晕(克隆件用完即弃)
        OrbDisc.SetResourceReference(System.Windows.UIElement.EffectProperty, "OrbGlow");
        _glowClone = null;
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

        // 主题换色会重建 OrbGlow(新的 Effect 实例),星光动画要重新挂上
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
                var source = IconGlyph.Get(name!);
                if (source == null) continue;    // 取不到就保留原标题
                var icon = new Image
                {
                    Source = source,
                    Width = 40,
                    Height = 40,
                    Stretch = Stretch.Uniform,
                    IsHitTestVisible = false,
                };
                // 256px 位图缩到 30px:用高质量缩放,避免线条边缘出现台阶
                RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
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
                StartPetalFloat(i, 0, 0);

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
            else
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
    /// 注意:动画的必须是**元素自身**的 Opacity,不能动 OrbGlow/PetalGlow——
    /// 它们放在 Application.Resources 里,WPF 会把 Freezable 冻结,对它 BeginAnimation 会抛
    /// "该对象已密封或已冻结"。元素透明度变化时其 Effect 也会跟着变淡,观感一致。
    /// </summary>
    private void StartTwinkle()
    {
        try
        {
            var orbDisc = Orb.Children.OfType<System.Windows.Shapes.Ellipse>().FirstOrDefault();
            orbDisc?.BeginAnimation(OpacityProperty, Twinkle(0.80, 1.0, TimeSpan.Zero));

            var petals = Petals();
            var found = 0;
            for (var i = 0; i < petals.Length; i++)
            {
                // 模板可能尚未实例化,先强制应用再遍历
                petals[i].ApplyTemplate();
                var disc = FindFirstEllipse(petals[i]);
                if (disc == null) continue;
                found++;
                disc.BeginAnimation(OpacityProperty, Twinkle(0.28, 1.0, TimeSpan.FromMilliseconds(i * 470)));
            }
            Boot($"twinkle started (petals {found}/{petals.Length})");
            _twinkleStarted = true;
        }
        catch (Exception ex)
        {
            // 星光只是装饰:失败也绝不能影响启动器的正常使用
            Boot("twinkle failed: " + ex.GetType().Name + " " + ex.Message);
        }
    }

    private static DoubleAnimation Twinkle(double from, double to, TimeSpan begin)
    {
        return new DoubleAnimation(from, to, TimeSpan.FromSeconds(1.9))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = begin,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
    }

    /// <summary>在可视化树里找第一个椭圆(花瓣模板中的光晕盘)。</summary>
    private static System.Windows.Shapes.Ellipse? FindFirstEllipse(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Shapes.Ellipse ellipse) return ellipse;
            var nested = FindFirstEllipse(child);
            if (nested != null) return nested;
        }
        return null;
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
    }

    private void ClosePetals()
    {
        if (!_petalsOpen) return;
        _closeDueAt = null;
        // 面板打开时保持展开,避免遮住正在使用的面板(先判再记日志,免得每 1.5s 刷一条)
        if (PanelWindow.AnyPanelOpen) return;
        Boot("ClosePetals");
        _petalsOpen = false;
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
        var versionId = AppServices.Config.LastLaunchedVersion;
        if (string.IsNullOrEmpty(versionId))
        {
            var installed = InstalledVersionScanner.Scan(
                AppServices.VersionsRoot, AppServices.InstancesRoot, AppServices.LibrariesRoot);
            versionId = installed.FirstOrDefault()?.Id;
        }
        if (string.IsNullOrEmpty(versionId))
        {
            Boot("launch skipped: no installed version");
            return;
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
