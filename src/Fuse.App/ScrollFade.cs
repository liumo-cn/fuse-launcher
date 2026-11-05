using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Fuse.App;

/// <summary>
/// 滚动条自动隐藏:滚动/悬停时显示,静止 1 秒后淡出;隐藏时不吃鼠标,滚轮仍能滚动内容。
/// 由 App.xaml 的隐式 ScrollBar 样式挂到所有滚动条上。
/// </summary>
public static class ScrollFade
{
    private sealed class State
    {
        public readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromMilliseconds(1000) };
        public bool Hooked;
    }

    private static readonly ConditionalWeakTable<ScrollBar, State> States = new();

    public static readonly DependencyProperty AutoHideProperty = DependencyProperty.RegisterAttached(
        "AutoHide", typeof(bool), typeof(ScrollFade), new PropertyMetadata(false, OnAutoHideChanged));

    public static void SetAutoHide(DependencyObject element, bool value) => element.SetValue(AutoHideProperty, value);
    public static bool GetAutoHide(DependencyObject element) => (bool)element.GetValue(AutoHideProperty);

    private static void OnAutoHideChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollBar bar) return;
        if (e.NewValue is true) Attach(bar);
    }

    private static void Attach(ScrollBar bar)
    {
        var state = States.GetOrCreateValue(bar);
        if (!state.Hooked)
        {
            state.Hooked = true;
            bar.ValueChanged += (_, _) => Show(bar);
            bar.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => Show(bar)));
            bar.MouseEnter += (_, _) => Show(bar);
            state.Timer.Tick += (_, _) =>
            {
                state.Timer.Stop();
                if (bar.IsMouseOver) state.Timer.Start();   // 鼠标还停在条上就先不藏
                else Hide(bar);
            };
        }
        Show(bar);
    }

    private static void Show(ScrollBar bar)
    {
        var state = States.GetOrCreateValue(bar);
        if (!state.Hooked) return;
        bar.IsHitTestVisible = true;
        bar.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        state.Timer.Stop();
        state.Timer.Start();
    }

    private static void Hide(ScrollBar bar)
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) =>
        {
            try { bar.IsHitTestVisible = false; } catch (Exception) { }
        };
        bar.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
