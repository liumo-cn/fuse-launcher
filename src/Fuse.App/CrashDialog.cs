using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Fuse.App;

/// <summary>
/// 出错提示框:大字"坏菜了!" + 小字提示 + 确定/导出。
/// 风格与面板一致(圆角深色底),配色全部 DynamicResource:
/// 背景跟 PanelBgBrush,按钮跟主题强调色(GreenBrush / HoverSolidBrush)。
/// </summary>
public static class CrashDialog
{
    public static void Show(string title, Action export)
    {
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            Title = AppServices.ProductName,
        };
        System.Windows.Automation.AutomationProperties.SetName(window, "错误提示");

        var border = new Border
        {
            CornerRadius = new CornerRadius(20),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(26, 22, 26, 18),
            Width = 320,
        };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBgBrush");
        border.BorderBrush = new SolidColorBrush(Color.FromArgb(56, 255, 255, 255));

        // 撑满内宽,所有子元素在其内部居中 → 左右留白恒对称
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };

        var big = new TextBlock
        {
            Text = "坏菜了！",
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextAlignment = TextAlignment.Center,
        };
        big.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        stack.Children.Add(big);

        var small = new TextBlock
        {
            // 两行按字数配平(11 / 12 字),居中后左右留白对称;原来 15/8 字看起来偏左
            Text = "游戏发生崩溃，有问题请" + Environment.NewLine + "导出日志而不是拍这个页面",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 18),
        };
        small.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        stack.Children.Add(small);

        var buttons = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var ok = new Button
        {
            Content = "确定",
            Height = 40,
            Style = (Style)Application.Current.Resources["GlassSecondaryButton"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        System.Windows.Automation.AutomationProperties.SetName(ok, "确定");
        ok.Click += (_, _) => window.Close();
        Grid.SetColumn(ok, 0);
        buttons.Children.Add(ok);

        var exportButton = new Button
        {
            Content = "导出",
            Height = 40,
            Style = (Style)Application.Current.Resources["GreenBigButton"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        System.Windows.Automation.AutomationProperties.SetName(exportButton, "导出");
        exportButton.Click += (_, _) =>
        {
            window.Close();
            try { export(); } catch (Exception) { }
        };
        Grid.SetColumn(exportButton, 2);
        buttons.Children.Add(exportButton);

        stack.Children.Add(buttons);
        border.Child = stack;
        window.Content = border;
        window.ShowDialog();
    }
}

/// <summary>同风格的信息提示框(用于"还差一步"这类引导)。</summary>
public static class NoticeDialog
{
    public static void Show(string title, string message, string? actionLabel, Action? action)
    {
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
        };
        System.Windows.Automation.AutomationProperties.SetName(window, title);

        var border = new Border
        {
            CornerRadius = new CornerRadius(20),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(26, 22, 26, 18),
            Width = 340,
            BorderBrush = new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)),
        };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBgBrush");

        var stack = new StackPanel();
        var head = new TextBlock
        {
            Text = title, FontSize = 16, FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
        };
        head.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        stack.Children.Add(head);

        var body = new TextBlock
        {
            Text = message, FontSize = 12, Margin = new Thickness(0, 10, 0, 16),
            TextWrapping = TextWrapping.Wrap,
        };
        body.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        stack.Children.Add(body);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var ok = new Button
        {
            Content = "确定", Height = 40, Width = 120,
            Style = (Style)Application.Current.Resources["GlassSecondaryButton"],
            Margin = new Thickness(0, 0, 8, 0),
        };
        ok.Click += (_, _) => window.Close();
        buttons.Children.Add(ok);

        if (actionLabel != null && action != null)
        {
            var act = new Button
            {
                Content = actionLabel, Height = 40, Width = 160,
                Style = (Style)Application.Current.Resources["GreenBigButton"],
            };
            act.Click += (_, _) => { window.Close(); try { action(); } catch (Exception) { } };
            buttons.Children.Add(act);
        }

        stack.Children.Add(buttons);
        border.Child = stack;
        window.Content = border;
        window.ShowDialog();
    }
}
