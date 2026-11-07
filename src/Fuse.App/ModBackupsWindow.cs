using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fuse.Core.ModConflict;

namespace Fuse.App;

/// <summary>列出某实例的 mods-backup-* 备份,可打开或恢复。</summary>
internal sealed class ModBackupsWindow : Window
{
    private readonly string _modsDir;

    public ModBackupsWindow(string modsDir)
    {
        _modsDir = modsDir;
        DialogKit.ApplyWindow(this, 460);
        var stack = new StackPanel();
        stack.Children.Add(DialogKit.Title("修复备份"));
        stack.Children.Add(DialogKit.Hint(
            "每次一键修复前都会把整个 mods 目录复制到这里；恢复会先把当前 mods 移到 mods-pre-undo-时间。"));

        var parent = Path.GetDirectoryName(modsDir) ?? modsDir;
        var backups = Directory.Exists(parent)
            ? Directory.GetDirectories(parent, "mods-backup-*").OrderByDescending(d => d).Take(10).ToList()
            : new List<string>();

        if (backups.Count == 0)
        {
            stack.Children.Add(DialogKit.Hint("还没有修复备份。"));
        }
        else
        {
            var list = new StackPanel();
            foreach (var dir in backups)
            {
                var info = new DirectoryInfo(dir);
                var size = 0L;
                var count = 0;
                try
                {
                    foreach (var f in info.GetFiles("*", SearchOption.AllDirectories))
                    {
                        size += f.Length;
                        count++;
                    }
                }
                catch (Exception)
                {
                }

                var row = new Border
                {
                    Tag = dir,
                    Background = DialogKit.Res("RowBgBrush"),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 0, 0, 6),
                };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock
                {
                    Text = info.Name,
                    FontSize = 12,
                    Foreground = DialogKit.Res("TextPrimaryBrush"),
                });
                text.Children.Add(new TextBlock
                {
                    Text = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm") + " · " + count + " 个文件 · " + FormatSize(size),
                    FontSize = 10.5,
                    Foreground = DialogKit.Res("TextSecondaryBrush"),
                });
                Grid.SetColumn(text, 0);
                grid.Children.Add(text);

                var buttons = new StackPanel { Orientation = Orientation.Horizontal };
                var open = ActionButton("打开", () =>
                {
                    try { Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); }
                    catch (Exception ex) { new InfoWindow("打开失败", ex.Message) { Owner = this }.ShowDialog(); }
                });
                var restore = ActionButton("恢复", () => Restore(dir));
                var remove = ActionButton("删除", () => Delete(dir, list), danger: true);
                buttons.Children.Add(open);
                buttons.Children.Add(restore);
                buttons.Children.Add(remove);
                Grid.SetColumn(buttons, 1);
                grid.Children.Add(buttons);
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
        }

        stack.Children.Add(DialogKit.Buttons(DialogKit.Secondary("关闭", () => DialogResult = false)));
        Content = DialogKit.Card(stack);
        DialogKit.WireDrag(this, stack.Children[0] as FrameworkElement);
        DialogKit.WireKeys(this, () => DialogResult = false);
    }

    private void Delete(string backupDir, Panel host)
    {
        var confirm = new ConfirmWindow("删除备份",
            "确定删除这个修复备份？" + Environment.NewLine + Environment.NewLine +
            backupDir + Environment.NewLine + Environment.NewLine + "删除后无法恢复。",
            "删除", danger: true)
        {
            Owner = this,
        };
        if (confirm.ShowDialog() != true) return;
        try
        {
            Directory.Delete(backupDir, true);
            var row = host.Children.OfType<Border>()
                .FirstOrDefault(b => (b.Tag as string) == backupDir);
            if (row != null) host.Children.Remove(row);
        }
        catch (Exception ex)
        {
            new InfoWindow("删除失败", ex.Message) { Owner = this }.ShowDialog();
        }
    }

    /// <summary>方形带小圆角的操作按钮(备份行专用)。</summary>
    private static Button ActionButton(string text, Action onClick, bool danger = false)
    {
        var button = new Button
        {
            Content = text,
            Height = 28,
            MinWidth = 52,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(6, 0, 0, 0),
            FontSize = 11.5,
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = new SolidColorBrush(Color.FromArgb(danger ? (byte)0x33 : (byte)0x22,
                danger ? (byte)0xD9 : (byte)0xFF, danger ? (byte)0x53 : (byte)0xFF, danger ? (byte)0x4F : (byte)0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(danger ? (byte)0x66 : (byte)0x40,
                danger ? (byte)0xFF : (byte)0xFF, danger ? (byte)0x8A : (byte)0xFF, danger ? (byte)0x8A : (byte)0xFF)),
            BorderThickness = new Thickness(1),
        };
        button.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");

        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty,
            new SolidColorBrush(Color.FromArgb(danger ? (byte)0x55 : (byte)0x33,
                danger ? (byte)0xD9 : (byte)0xFF, danger ? (byte)0x53 : (byte)0xFF, danger ? (byte)0x4F : (byte)0xFF)), "Bd"));
        var pressed = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty,
            new SolidColorBrush(Color.FromArgb(0x44, 255, 255, 255)), "Bd"));
        template.Triggers.Add(hover);
        template.Triggers.Add(pressed);
        button.Template = template;
        button.Click += (_, _) => onClick();
        return button;
    }

    private void Restore(string backupDir)
    {
        var confirm = new ConfirmWindow("恢复备份",
            "用这个备份覆盖当前 mods？" + Environment.NewLine + Environment.NewLine +
            "当前 mods 会先移到 mods-pre-undo-时间，不会丢失。", "恢复", danger: true)
        {
            Owner = this,
        };
        if (confirm.ShowDialog() != true) return;
        if (ModConflictFixer.RestoreBackup(_modsDir, backupDir, out var error))
        {
            new InfoWindow("已恢复", "mods 已恢复到：" + Environment.NewLine + backupDir) { Owner = this }
                .ShowDialog();
            DialogResult = true;
        }
        else
        {
            new InfoWindow("恢复失败", error) { Owner = this }.ShowDialog();
        }
    }

    private static string FormatSize(long bytes) =>
        bytes < 1024 * 1024 ? (bytes / 1024.0).ToString("0.#") + " KB"
        : (bytes / 1024.0 / 1024.0).ToString("0.#") + " MB";
}
