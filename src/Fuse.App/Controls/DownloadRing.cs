using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Fuse.Core.Download;

namespace Fuse.App.Controls;

/// <summary>
/// 下载进度圆环(所有可下载页面左下角通用)。
///
/// 表现:
/// ① 有任务时出现;底圈固定 50% 不透明,进度弧从正上方顺时针生长,弧自身的不透明度由 50% 渐升到 100%;
/// ② 多个任务合并成一个圆环,进度取所有未完成任务的均值;
/// ③ 全部完成后保留满环 1 秒再隐藏;
/// ④ 每完成一个任务在圆环右侧跳一行小字(主色 90%),默认 1 秒;若期间又有任务完成,
///    当前提示缩短到 0.5 秒后立刻切下一条,保证批量完成时不会排队拖很久。
/// </summary>
public class DownloadRing : UserControl
{
    private const double RingSize = 32;
    private const double RingThickness = 4;
    private static readonly double Circumference = Math.PI * (RingSize - RingThickness);
    private static readonly double DashUnits = Circumference / RingThickness;

    private readonly Ellipse _track;
    private readonly Ellipse _arc;
    private readonly Grid _ring;
    private readonly TextBlock _hint;
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _hintTimer;

    private readonly Queue<string> _pendingHints = new();
    private readonly Dictionary<string, DownloadTaskStatus> _lastStatus = new(StringComparer.Ordinal);
    private bool _showingHint;
    private DateTime? _hideRingAt;
    private bool _wasActive;
    private bool _seeded;
    private DateTime _shownAt = DateTime.Now;

    private readonly string _panelKind;

    public DownloadRing(string panelKind)
    {
        _panelKind = panelKind;
        _track = new Ellipse
        {
            Width = RingSize,
            Height = RingSize,
            StrokeThickness = RingThickness,
            Opacity = 0.5,
            SnapsToDevicePixels = true,
        };
        _track.SetResourceReference(Shape.StrokeProperty, "GreenBrush");

        _arc = new Ellipse
        {
            Width = RingSize,
            Height = RingSize,
            StrokeThickness = RingThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Opacity = 0.5,
            RenderTransform = new RotateTransform(-90, RingSize / 2, RingSize / 2),
            SnapsToDevicePixels = true,
        };
        _arc.SetResourceReference(Shape.StrokeProperty, "GreenBrush");
        _arc.StrokeDashArray = new DoubleCollection { 0, DashUnits };

        _ring = new Grid { Width = RingSize, Height = RingSize, Visibility = Visibility.Collapsed };
        _ring.Children.Add(_track);
        _ring.Children.Add(_arc);

        _hint = new TextBlock
        {
            FontSize = 12,
            TextAlignment = TextAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Opacity = 0.9,
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 260,
        };
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "GreenBrush");

        var host = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        host.Children.Add(_ring);
        host.Children.Add(_hint);

        Content = host;
        // 用户指定:页面外面(面板左侧留白)→ 靠下 → 靠左
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(0);

        _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _poll.Tick += (_, _) => Refresh();
        _hintTimer = new DispatcherTimer();
        _hintTimer.Tick += (_, _) => ShowNextHint();

        Loaded += (_, _) => { if (!_poll.IsEnabled) _poll.Start(); Refresh(); };
        Unloaded += (_, _) => _poll.Stop();
    }

    /// <summary>轮询队列:更新圆环进度、检测"刚完成"的任务。</summary>
    private void Refresh()
    {
        var tasks = AppServices.Queue?.GetTasks();
        if (tasks == null) return;

        // 1. 完成检测。第一次 tick 只记录基线(避免把上次遗留的完成任务再报一遍);
        //    之后"第一次看到就已经完成"的任务也要提示 —— 任务可能在 200ms 轮询间隔内跑完。
        if (!_seeded)
        {
            foreach (var task in tasks)
                if (!string.IsNullOrEmpty(task.TaskId)) _lastStatus[task.TaskId] = task.Status;
            _seeded = true;
        }
        else
        {
            foreach (var task in tasks)
            {
                if (string.IsNullOrEmpty(task.TaskId)) continue;
                var known = _lastStatus.TryGetValue(task.TaskId, out var prev);
                if (task.Status == DownloadTaskStatus.Completed && (!known || prev != DownloadTaskStatus.Completed))
                    EnqueueHint(task.Name);
                _lastStatus[task.TaskId] = task.Status;
            }
        }

        var active = tasks
            .Where(t => t.Status == DownloadTaskStatus.Queued || t.Status == DownloadTaskStatus.Downloading)
            .ToList();
        DownloadFocus.Sync(active.Select(t => t.TaskId));

        // 进度只显示在"第一个发起下载的页面"上,其它页面即使有任务也不显示圆环
        if (DownloadFocus.Current != null && DownloadFocus.Current != _panelKind)
        {
            _ring.Visibility = Visibility.Collapsed;
            return;
        }

        if (active.Count > 0)
        {
            if (!_wasActive) _shownAt = DateTime.Now;   // 一轮下载的开始时间
            _wasActive = true;
            _hideRingAt = null;
            _ring.Visibility = Visibility.Visible;
            UpdateArc(active.Average(t => Math.Max(0, Math.Min(1, t.Progress))));
            return;
        }

        // 任务太快(几百毫秒就结束)时,保证圆环至少露脸 1.2 秒,否则用户只看到"闪一下"
        if (_wasActive && (DateTime.Now - _shownAt).TotalMilliseconds < 1200)
        {
            _hideRingAt = _shownAt.AddMilliseconds(1200 + 1000);
            UpdateArc(1.0);
            _ring.Visibility = Visibility.Visible;
            return;
        }

        // 2. 刚跑完:保留满环 1 秒
        if (_wasActive)
        {
            _wasActive = false;
            UpdateArc(1.0);
            _ring.Visibility = Visibility.Visible;
            _hideRingAt = DateTime.Now.AddSeconds(1);
            return;
        }
        if (_hideRingAt.HasValue && DateTime.Now >= _hideRingAt.Value)
        {
            _hideRingAt = null;
            _ring.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>弧长 = 进度,弧的不透明度随进度从 50% 升到 100%。</summary>
    private void UpdateArc(double progress)
    {
        var ratio = Math.Max(0, Math.Min(1, progress));
        _arc.StrokeDashArray = new DoubleCollection
        {
            ratio * DashUnits,
            Math.Max(0.0001, (1 - ratio) * DashUnits),
        };
        _arc.Opacity = 0.5 + 0.5 * ratio;
        _ring.Visibility = Visibility.Visible;
    }

    // ---------- 完成提示 ----------

    private void EnqueueHint(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _pendingHints.Enqueue(text);

        if (_showingHint)
        {
            // 正在显示时又完成一个:当前这条最多再停 0.5 秒就切下一条(批量完成时不会拖很久)
            _hintTimer.Stop();
            _hintTimer.Interval = TimeSpan.FromMilliseconds(500);
            _hintTimer.Start();
            return;
        }
        ShowNextHint();
    }

    private void ShowNextHint()
    {
        if (_pendingHints.Count == 0)
        {
            _showingHint = false;
            _hint.Visibility = Visibility.Collapsed;
            _hintTimer.Stop();
            return;
        }

        _showingHint = true;
        var text = _pendingHints.Dequeue();
        _hint.Text = text.EndsWith("完成", StringComparison.Ordinal) ? text : text + " 完成";
        _hint.Visibility = Visibility.Visible;

        // 后面还有排队的就只停 0.5 秒,否则完整 1 秒
        var more = _pendingHints.Count > 0;
        _hintTimer.Stop();
        _hintTimer.Interval = TimeSpan.FromMilliseconds(more ? 500 : 1000);
        _hintTimer.Start();
    }
}
