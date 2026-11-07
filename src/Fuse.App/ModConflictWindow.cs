using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fuse.Core.ModConflict;

namespace Fuse.App;

/// <summary>模组冲突修复预览:逐条勾选,确认后交给 Fixer 执行。</summary>
internal sealed class ModConflictWindow : Window
{
    private readonly List<(ModFixAction Action, CheckBox Check)> _rows = new();
    public List<ModFixAction> Selected { get; } = new();
    public bool SwitchGameVersion { get; private set; }

    public ModConflictWindow(ModConflictReport report, string? loader = null)
    {
        DialogKit.ApplyWindow(this, 460);
        var stack = new StackPanel();
        var head = DialogKit.Title("发现 " + report.Actions.Count + " 个模组问题");
        stack.Children.Add(head);
        stack.Children.Add(DialogKit.Hint(
            "勾选要修复的项目。修复前会自动备份 mods 目录，只禁用不删除；带「仅建议」的项不会自动改。"));
        if (!string.IsNullOrEmpty(report.SuggestedGameVersion))
            stack.Children.Add(DialogKit.Hint(
                "这些模组需要 Minecraft " + report.SuggestedGameVersion +
                "：可以切换到该版本，或按建议禁用不兼容的模组。"));

        if (!string.IsNullOrEmpty(report.SuggestedGameVersion) &&
            string.Equals(loader, "fabric", StringComparison.OrdinalIgnoreCase))
        {
            var switchBtn = DialogKit.Secondary(
                "切换到 " + report.SuggestedGameVersion + "（安装并迁移）", () =>
                {
                    SwitchGameVersion = true;
                    DialogResult = true;
                });
            switchBtn.HorizontalAlignment = HorizontalAlignment.Left;
            switchBtn.Margin = new Thickness(0, 8, 0, 12);
            System.Windows.Automation.AutomationProperties.SetName(switchBtn, "切换游戏版本");
            stack.Children.Add(switchBtn);
        }

        var list = new StackPanel();
        foreach (var action in report.Actions)
        {
            var row = new Border
            {
                Background = DialogKit.Res("RowBgBrush"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 6),
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel();
            info.Children.Add(new TextBlock
            {
                Text = ActionTitle(action),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = DialogKit.Res("TextPrimaryBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
            info.Children.Add(new TextBlock
            {
                Text = action.Reason,
                FontSize = 10.5,
                Foreground = DialogKit.Res("TextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            });
            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            var check = new CheckBox
            {
                Style = (Style)Application.Current.Resources["GlassToggle"],
                IsChecked = action.CanAutoFix,
                IsEnabled = action.CanAutoFix,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            System.Windows.Automation.AutomationProperties.SetName(check, "修复 " + action.Reason);
            Grid.SetColumn(check, 1);
            grid.Children.Add(check);
            _rows.Add((action, check));

            row.Child = grid;
            list.Children.Add(row);
        }

        stack.Children.Add(new ScrollViewer
        {
            Style = (Style)Application.Current.Resources["ModernScrollViewer"],
            MaxHeight = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = list,
        });

        var cancel = DialogKit.Secondary("取消", () => DialogResult = false);
        var apply = DialogKit.Primary("修复所选", () =>
        {
            Selected.Clear();
            foreach (var (action, check) in _rows)
                if (check.IsChecked == true && action.CanAutoFix) Selected.Add(action);
            if (Selected.Count == 0)
            {
                DialogResult = false;
                return;
            }
            DialogResult = true;
        });
        System.Windows.Automation.AutomationProperties.SetName(apply, "修复所选模组");
        stack.Children.Add(DialogKit.Buttons(cancel, apply));
        Content = DialogKit.Card(stack);
        DialogKit.WireDrag(this, head);
        DialogKit.WireKeys(this, () => DialogResult = false);
    }

    private static string ActionTitle(ModFixAction action) => action.Kind switch
    {
        ModFixKind.InstallDependency => "安装缺少的前置：" + action.DependencyName,
        ModFixKind.DisableDuplicate => "禁用重复模组：" + action.FileName,
        ModFixKind.ReplaceVersion => string.IsNullOrEmpty(action.Constraint)
            ? "升级模组：" + action.DependencyName + "（找支持 " + action.RequiredGameVersion + " 的版本）"
            : "替换版本：" + action.DependencyName + " " +
              (string.IsNullOrEmpty(action.InstalledVersion) ? "" : action.InstalledVersion + " → ") +
              action.Constraint,
        ModFixKind.DisableIncompatible => "当前游戏版本不支持：" + action.ModName + "（仅建议）",
        _ => action.ModName,
    };
}
