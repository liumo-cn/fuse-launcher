using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Fuse.App;

/// <summary>
/// 解压式整合包导入:博主自制的 zip 没有安装清单,需要用户指定它对应的游戏版本。
/// 整合包会解压到独立实例目录,并生成一个 inheritsFrom 该版本的版本 json。
/// </summary>
internal sealed class ImportPlainPackWindow : Window
{
    private readonly TextBox _name;
    private readonly TextBlock _error;
    private readonly List<Button> _rows = new();
    private string? _selectedId;

    /// <summary>导入成功后生成的版本 id。</summary>
    public string? ImportedVersionId { get; private set; }

    private readonly string _zipPath;

    public ImportPlainPackWindow(string zipPath, string defaultName, List<(string Id, string Display)> versions)
    {
        _zipPath = zipPath;
        DialogKit.ApplyWindow(this, 430);

        var stack = new StackPanel();
        var title = DialogKit.Title("导入整合包（解压式）");
        stack.Children.Add(title);
        stack.Children.Add(DialogKit.Hint(
            "这个压缩包没有安装清单。请指定它对应的游戏版本（含加载器），整合包会解压到独立实例，不会动现有存档。"));

        stack.Children.Add(Label("整合包名称"));
        _name = DialogKit.Input(defaultName);
        _name.Margin = new Thickness(0, 6, 0, 0);
        System.Windows.Automation.AutomationProperties.SetName(_name, "整合包名称");
        stack.Children.Add(_name);

        stack.Children.Add(Label("基础版本"));
        stack.Children.Add(DialogKit.Hint(
            "选择整合包对应的 Minecraft 版本。如果里面是 Forge/Fabric 模组，请选已装好对应加载器的版本（没有就先去下载页装）。"));

        var list = new StackPanel();
        foreach (var (id, display) in versions)
        {
            var captured = id;
            var rowContent = new StackPanel { Margin = new Thickness(10, 6, 10, 6) };
            rowContent.Children.Add(new TextBlock
            {
                Text = display,
                FontSize = 12.5,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            });
            rowContent.Children.Add(new TextBlock
            {
                Text = id,
                FontSize = 10.5,
                FontFamily = new FontFamily("Consolas"),
                Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
                Margin = new Thickness(0, 1, 0, 0),
            });
            var row = new Button
            {
                Style = (Style)Application.Current.Resources["FolderRowButton"],
                Content = rowContent,
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = Brushes.Transparent,
                Tag = captured,
            };
            System.Windows.Automation.AutomationProperties.SetName(row, "基础版本 " + id);
            row.Click += (_, _) => SelectVersion(captured);
            _rows.Add(row);
            list.Children.Add(row);
        }
        var scroll = new ScrollViewer
        {
            Style = (Style)Application.Current.Resources["ModernScrollViewer"],
            MaxHeight = 168,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = list,
        };
        var listHost = new Border
        {
            Background = (Brush)Application.Current.Resources["RowBgBrush"],
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(4),
            Margin = new Thickness(0, 8, 0, 0),
            Child = scroll,
        };
        stack.Children.Add(listHost);

        _error = DialogKit.Hint("", DialogKit.Res("DangerBrush"));
        _error.Visibility = Visibility.Collapsed;
        stack.Children.Add(_error);

        var cancel = DialogKit.Secondary("取消", () => DialogResult = false);
        var import = DialogKit.Primary("导入", TryImport);
        System.Windows.Automation.AutomationProperties.SetName(import, "导入");
        stack.Children.Add(DialogKit.Buttons(cancel, import));
        Content = DialogKit.Card(stack);

        DialogKit.WireDrag(this, title);
        DialogKit.WireKeys(this, TryImport);
    }

    private static TextBlock Label(string text) => new TextBlock
    {
        Text = text,
        FontSize = 11.5,
        Foreground = DialogKit.Res("TextSecondaryBrush"),
        Margin = new Thickness(0, 12, 0, 0),
    };

    private void SelectVersion(string id)
    {
        _selectedId = id;
        for (var i = 0; i < _rows.Count; i++)
        {
            var rowId = _rows[i].Tag as string;
            _rows[i].Background = string.Equals(rowId, id, StringComparison.OrdinalIgnoreCase)
                ? (Brush)Application.Current.Resources["AccentChipSoftBrush"]
                : Brushes.Transparent;
        }
        _error.Visibility = Visibility.Collapsed;
    }

    private void TryImport()
    {
        if (string.IsNullOrEmpty(_selectedId))
        {
            _error.Text = "请先选择一个基础版本";
            _error.Visibility = Visibility.Visible;
            return;
        }
        var id = AppServices.ImportPlainPack(_zipPath, _name.Text, _selectedId!, out var error);
        if (id == null)
        {
            _error.Text = error;
            _error.Visibility = Visibility.Visible;
            return;
        }
        ImportedVersionId = id;
        DialogResult = true;
    }
}
