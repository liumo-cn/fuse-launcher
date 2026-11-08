using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Fuse.App;

/// <summary>文件夹列表用的小弹窗(暗色圆角,和面板同一套视觉)。</summary>
internal static class DialogKit
{
    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public static void ApplyWindow(Window w, double width)
    {
        w.WindowStyle = WindowStyle.None;
        w.ResizeMode = ResizeMode.NoResize;
        w.ShowInTaskbar = false;
        w.AllowsTransparency = true;
        w.Background = Brushes.Transparent;
        w.SizeToContent = SizeToContent.Height;
        w.Width = width;
        w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        w.Topmost = true;
        // 面板贴着屏幕边时 CenterOwner 可能把弹窗顶出去一截 内容被裁掉
        // Loaded 时 SizeToContent 还没算完 所以 ContentRendered 再来一次
        void Clamp()
        {
            var area = SystemParameters.WorkArea;
            if (w.ActualWidth > 0)
                w.Left = Math.Max(area.Left + 8, Math.Min(area.Right - w.ActualWidth - 8, w.Left));
            if (w.ActualHeight > 0)
                w.Top = Math.Max(area.Top + 8, Math.Min(area.Bottom - w.ActualHeight - 8, w.Top));
        }
        w.Loaded += (_, _) => Clamp();
        w.ContentRendered += (_, _) => Clamp();
    }

    public static Border Card(UIElement content) => new Border
    {
        Background = Res("PanelBgBrush"),
        CornerRadius = new CornerRadius(18),
        Padding = new Thickness(20, 16, 20, 16),
        BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
        BorderThickness = new Thickness(1),
        Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 10, Opacity = 0.5 },
        Child = content,
    };

    public static TextBlock Title(string text)
    {
        var title = new TextBlock
        {
            Text = text,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res("TextPrimaryBrush"),
        };
        return title;
    }

    public static TextBlock Hint(string text, Brush? brush = null) => new TextBlock
    {
        Text = text,
        FontSize = 11.5,
        Foreground = brush ?? Res("TextSecondaryBrush"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 5, 0, 0),
    };

    public static TextBox Input(string text = "") => new TextBox
    {
        Text = text,
        Style = (Style)Application.Current.Resources["DarkInput"],
        Margin = new Thickness(0, 10, 0, 0),
    };

    public static Button Primary(string text, Action onClick, bool danger = false)
    {
        var b = new Button
        {
            Content = text,
            Style = (Style)Application.Current.Resources["PillPrimary"],
            MinWidth = 84,
            Height = 34,
            Padding = new Thickness(18, 0, 18, 0),
            FontSize = 12.5,
            Margin = new Thickness(8, 0, 0, 0),
        };
        if (danger) b.Background = Res("DangerBrush");
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Button Secondary(string text, Action onClick) => new Button
    {
        Content = text,
        Style = (Style)Application.Current.Resources["PillSecondary"],
        MinWidth = 84,
        Height = 34,
        Padding = new Thickness(18, 0, 18, 0),
        FontSize = 12.5,
        Margin = new Thickness(8, 0, 0, 0),
    }.Tap(b => b.Click += (_, _) => onClick());

    /// <summary>标题条拖动(只有标题可拖,输入框照常选中文字)。</summary>
    public static void WireDrag(Window w, UIElement handle, Action? onDragStart = null)
    {
        handle.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            onDragStart?.Invoke();
            try { w.DragMove(); } catch (InvalidOperationException) { }
        };
    }

    public static void WireKeys(Window w, Action? onEnter)
    {
        w.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                w.DialogResult = false;
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && onEnter != null)
            {
                onEnter();
                e.Handled = true;
            }
        };
    }

    public static StackPanel Buttons(params UIElement[] items)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        foreach (var item in items) row.Children.Add(item);
        return row;
    }

    /// <summary>WinForms 文件对话框的 owner 包装:没有它,选择框会跑到弹窗后面。</summary>
    public sealed class OwnerHandle : System.Windows.Forms.IWin32Window
    {
        public IntPtr Handle { get; }
        public OwnerHandle(IntPtr handle) { Handle = handle; }
    }

    public static IntPtr HandleOf(Window w) => new WindowInteropHelper(w).Handle;

    private static T Tap<T>(this T self, Action<T> action) { action(self); return self; }
}

/// <summary>重命名文件夹(只改显示名,不动磁盘)。</summary>
internal sealed class RenameFolderWindow : Window
{
    private readonly TextBox _box;
    private readonly TextBlock _error;
    private readonly Func<string, string?> _validate;

    public string Value => _box.Text.Trim();

    public RenameFolderWindow(string current, Func<string, string?> validate)
    {
        _validate = validate;
        DialogKit.ApplyWindow(this, 340);

        var stack = new StackPanel();
        var title = DialogKit.Title("重命名文件夹");
        stack.Children.Add(title);
        stack.Children.Add(DialogKit.Hint("只改列表里显示的名称,不会改动磁盘上的文件夹。"));
        _box = DialogKit.Input(current);
        System.Windows.Automation.AutomationProperties.SetName(_box, "文件夹名称");
        stack.Children.Add(_box);
        _error = DialogKit.Hint("", DialogKit.Res("DangerBrush"));
        _error.Visibility = Visibility.Collapsed;
        stack.Children.Add(_error);

        var cancel = DialogKit.Secondary("取消", () => DialogResult = false);
        var ok = DialogKit.Primary("保存", TryOk);
        stack.Children.Add(DialogKit.Buttons(cancel, ok));
        Content = DialogKit.Card(stack);

        DialogKit.WireDrag(this, title);
        DialogKit.WireKeys(this, TryOk);
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    private void TryOk()
    {
        var error = _validate(Value);
        if (error != null)
        {
            _error.Text = error;
            _error.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }
}

/// <summary>新建 &lt;位置&gt;\&lt;名称&gt;\.minecraft。</summary>
internal sealed class NewFolderWindow : Window
{
    private readonly TextBox _parent;
    private readonly TextBox _name;
    private readonly TextBlock _preview;
    private readonly TextBlock _error;

    public string? CreatedPath { get; private set; }

    public NewFolderWindow(string defaultParent)
    {
        DialogKit.ApplyWindow(this, 400);

        var stack = new StackPanel();
        var title = DialogKit.Title("新建 .minecraft 文件夹");
        stack.Children.Add(title);
        stack.Children.Add(DialogKit.Hint("会在所选位置下创建 <名称>\\.minecraft,并建好 versions / libraries / assets 等子目录。"));

        stack.Children.Add(new TextBlock
        {
            Text = "位置", FontSize = 11.5, Foreground = DialogKit.Res("TextSecondaryBrush"),
            Margin = new Thickness(0, 12, 0, 0),
        });
        var parentRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        parentRow.ColumnDefinitions.Add(new ColumnDefinition());
        parentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _parent = DialogKit.Input(defaultParent);
        _parent.Margin = new Thickness(0);
        System.Windows.Automation.AutomationProperties.SetName(_parent, "文件夹位置");
        Grid.SetColumn(_parent, 0);
        parentRow.Children.Add(_parent);
        var browse = new Button
        {
            Content = "浏览",
            Style = (Style)Application.Current.Resources["ChipButton"],
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(12, 6, 12, 6),
        };
        System.Windows.Automation.AutomationProperties.SetName(browse, "选择位置");
        browse.Click += (_, _) => BrowseParent();
        Grid.SetColumn(browse, 1);
        parentRow.Children.Add(browse);
        stack.Children.Add(parentRow);

        stack.Children.Add(new TextBlock
        {
            Text = "名称", FontSize = 11.5, Foreground = DialogKit.Res("TextSecondaryBrush"),
            Margin = new Thickness(0, 12, 0, 0),
        });
        _name = DialogKit.Input("");
        _name.Margin = new Thickness(0, 6, 0, 0);
        System.Windows.Automation.AutomationProperties.SetName(_name, "文件夹名称");
        stack.Children.Add(_name);

        _preview = DialogKit.Hint("");
        stack.Children.Add(_preview);
        _error = DialogKit.Hint("", DialogKit.Res("DangerBrush"));
        _error.Visibility = Visibility.Collapsed;
        stack.Children.Add(_error);

        var cancel = DialogKit.Secondary("取消", () => DialogResult = false);
        var create = DialogKit.Primary("创建", TryCreate);
        System.Windows.Automation.AutomationProperties.SetName(create, "创建");
        stack.Children.Add(DialogKit.Buttons(cancel, create));
        Content = DialogKit.Card(stack);

        DialogKit.WireDrag(this, title);
        DialogKit.WireKeys(this, TryCreate);
        _parent.TextChanged += (_, _) => UpdatePreview();
        _name.TextChanged += (_, _) => UpdatePreview();
        UpdatePreview();
        Loaded += (_, _) => _name.Focus();
    }

    private string TargetPath()
    {
        var parent = _parent.Text.Trim();
        var name = _name.Text.Trim();
        // 名称还没填时不预览,否则会显示成"将创建 <位置>\.minecraft",像是会建在父目录里
        if (parent.Length == 0 || name.Length == 0) return "";
        try { return System.IO.Path.Combine(parent, name, ".minecraft"); }
        catch (Exception) { return ""; }
    }

    private void UpdatePreview()
    {
        var target = TargetPath();
        _preview.Text = target.Length == 0 ? "" : "将创建:" + target;
        _preview.Visibility = target.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BrowseParent()
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择新 .minecraft 文件夹要放在哪个目录下",
            ShowNewFolderButton = true,
        };
        if (dlg.ShowDialog(new DialogKit.OwnerHandle(DialogKit.HandleOf(this))) ==
            System.Windows.Forms.DialogResult.OK)
            _parent.Text = dlg.SelectedPath;
    }

    private void TryCreate()
    {
        var created = AppServices.CreateGameFolder(_parent.Text, _name.Text, out var error);
        if (created == null)
        {
            _error.Text = error;
            _error.Visibility = Visibility.Visible;
            return;
        }
        CreatedPath = created;
        DialogResult = true;
    }
}

/// <summary>删除文件夹:必须输入 yes 才能点删除。</summary>
internal sealed class DeleteFolderWindow : Window
{
    private readonly string _path;
    private readonly TextBox _confirm;
    private readonly TextBlock _error;
    private readonly Button _delete;

    public DeleteFolderWindow(string path)
    {
        _path = path;
        DialogKit.ApplyWindow(this, 380);

        var stack = new StackPanel();
        var title = DialogKit.Title("删除文件夹");
        stack.Children.Add(title);
        stack.Children.Add(DialogKit.Hint(
            "将永久删除磁盘上的这个文件夹及其全部内容,无法恢复。", DialogKit.Res("DangerBrush")));
        stack.Children.Add(new TextBlock
        {
            Text = path,
            FontSize = 11,
            FontFamily = new FontFamily("Consolas"),
            Foreground = DialogKit.Res("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });
        stack.Children.Add(DialogKit.Hint("确认请输入 yes:"));
        _confirm = DialogKit.Input("");
        System.Windows.Automation.AutomationProperties.SetName(_confirm, "确认文本");
        stack.Children.Add(_confirm);
        _error = DialogKit.Hint("", DialogKit.Res("DangerBrush"));
        _error.Visibility = Visibility.Collapsed;
        stack.Children.Add(_error);

        var cancel = DialogKit.Secondary("取消", () => DialogResult = false);
        _delete = DialogKit.Primary("删除", DoDelete, danger: true);
        _delete.IsEnabled = false;
        System.Windows.Automation.AutomationProperties.SetName(_delete, "删除");
        stack.Children.Add(DialogKit.Buttons(cancel, _delete));
        Content = DialogKit.Card(stack);

        DialogKit.WireDrag(this, title);
        DialogKit.WireKeys(this, null);
        _confirm.TextChanged += (_, _) =>
            _delete.IsEnabled = string.Equals(_confirm.Text.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
        Loaded += (_, _) => _confirm.Focus();
    }

    private void DoDelete()
    {
        if (!_delete.IsEnabled) return;
        if (!AppServices.DeleteGameDirectory(_path, out var error))
        {
            _error.Text = error;
            _error.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }
}

/// <summary>通用确认弹窗(还原备份/删除备份等)。</summary>
internal sealed class ConfirmWindow : Window
{
    public ConfirmWindow(string title, string message, string confirmText, bool danger = false)
    {
        DialogKit.ApplyWindow(this, 380);
        var stack = new StackPanel();
        var head = DialogKit.Title(title);
        stack.Children.Add(head);
        stack.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 12,
            Foreground = DialogKit.Res("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });
        stack.Children.Add(DialogKit.Buttons(
            DialogKit.Secondary("取消", () => DialogResult = false),
            DialogKit.Primary(confirmText, () => DialogResult = true, danger)));
        Content = DialogKit.Card(stack);
        DialogKit.WireDrag(this, head);
        // 破坏性操作不绑定回车,避免误触
        DialogKit.WireKeys(this, null);
    }
}

/// <summary>添加/编辑服务器(servers.dat)。</summary>
internal sealed class ServerEditWindow : Window
{
    private readonly TextBox _name;
    private readonly TextBox _ip;
    private readonly TextBlock _error;
    private readonly CheckBox _autoJoin;

    public string ServerName => _name.Text.Trim();
    public string ServerIp => _ip.Text.Trim();
    /// <summary>每次启动该版本时自动进入这个服务器。</summary>
    public bool AutoJoin => _autoJoin.IsChecked == true;

    public ServerEditWindow(string? name, string? ip, bool autoJoin = false)
    {
        DialogKit.ApplyWindow(this, 400);
        var stack = new StackPanel();
        var title = DialogKit.Title(string.IsNullOrEmpty(ip) ? "添加服务器" : "编辑服务器");
        stack.Children.Add(title);
        stack.Children.Add(DialogKit.Hint("地址填 example.com 或 example.com:25565（不写端口默认 25565）。"));

        stack.Children.Add(Label("名称"));
        _name = DialogKit.Input(name ?? "");
        System.Windows.Automation.AutomationProperties.SetName(_name, "服务器名称");
        stack.Children.Add(_name);

        stack.Children.Add(Label("地址"));
        _ip = DialogKit.Input(ip ?? "");
        System.Windows.Automation.AutomationProperties.SetName(_ip, "服务器地址");
        stack.Children.Add(_ip);

        _error = DialogKit.Hint("", DialogKit.Res("DangerBrush"));
        _error.Visibility = Visibility.Collapsed;
        stack.Children.Add(_error);

        var cancel = DialogKit.Secondary("取消", () => DialogResult = false);
        var save = DialogKit.Primary("保存", TrySave);
        System.Windows.Automation.AutomationProperties.SetName(save, "保存服务器");

        // 自动加入开关放左下(用户指定:在取消键左侧)
        var autoRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        autoRow.Children.Add(new TextBlock
        {
            Text = "自动加入",
            FontSize = 11.5,
            Foreground = DialogKit.Res("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        _autoJoin = new CheckBox
        {
            Style = (Style)Application.Current.Resources["GlassToggle"],
            IsChecked = autoJoin,
            VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(_autoJoin, "自动加入");
        autoRow.Children.Add(_autoJoin);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);

        var footer = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(autoRow);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);
        stack.Children.Add(footer);
        Content = DialogKit.Card(stack);

        DialogKit.WireDrag(this, title);
        DialogKit.WireKeys(this, TrySave);
        Loaded += (_, _) => _ip.Focus();
    }

    private static TextBlock Label(string text) => new TextBlock
    {
        Text = text,
        FontSize = 11.5,
        Foreground = DialogKit.Res("TextSecondaryBrush"),
        Margin = new Thickness(0, 12, 0, 0),
    };

    private void TrySave()
    {
        var address = ServerIp;
        if (address.Length == 0)
        {
            Fail("请输入服务器地址");
            return;
        }
        if (address.Any(char.IsWhiteSpace))
        {
            Fail("地址不能包含空格");
            return;
        }
        DialogResult = true;
    }

    private void Fail(string message)
    {
        _error.Text = message;
        _error.Visibility = Visibility.Visible;
    }
}

/// <summary>只读日志查看器:启动器内直接看 latest.log / 崩溃报告,不用开资源管理器。</summary>
internal sealed class LogViewerWindow : Window
{
    private readonly TextBox _text;
    private readonly string _path;

    public LogViewerWindow(string path, string title)
    {
        _path = path;
        DialogKit.ApplyWindow(this, 660);

        var stack = new StackPanel();
        var head = DialogKit.Title(title);
        stack.Children.Add(head);
        stack.Children.Add(new TextBlock
        {
            Text = path,
            FontSize = 10.5,
            FontFamily = new FontFamily("Consolas"),
            Foreground = DialogKit.Res("TextSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 6, 0, 0),
        });

        _text = new TextBox
        {
            Style = (Style)Application.Current.Resources["DarkInput"],
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Height = 400,
            Margin = new Thickness(0, 10, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        System.Windows.Automation.AutomationProperties.SetName(_text, "日志内容");
        stack.Children.Add(_text);

        var refresh = DialogKit.Secondary("刷新", Load);
        var openFolder = DialogKit.Secondary("打开所在文件夹", () =>
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception)
            {
            }
        });
        var close = DialogKit.Primary("关闭", Close);
        stack.Children.Add(DialogKit.Buttons(refresh, openFolder, close));
        Content = DialogKit.Card(stack);
        DialogKit.WireDrag(this, head);
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                _text.Text = "文件不存在:" + _path;
                return;
            }
            _text.Text = ReadTail(_path, 400 * 1024);
            _text.ScrollToEnd();
        }
        catch (Exception ex)
        {
            _text.Text = "读取失败:" + ex.Message;
        }
    }

    /// <summary>只读文件尾部(游戏/启动器可能正拿着写句柄)。</summary>
    private static string ReadTail(string path, int maxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var length = fs.Length;
        var take = (int)Math.Min(length, maxBytes);
        fs.Seek(length - take, SeekOrigin.Begin);
        var buffer = new byte[take];
        var read = 0;
        while (read < take)
        {
            var n = fs.Read(buffer, read, take - read);
            if (n <= 0) break;
            read += n;
        }
        return Encoding.UTF8.GetString(buffer, 0, read);
    }
}

/// <summary>一键添加本机目录的结果清单:勾选要加的,已在列表的置灰。</summary>
internal sealed class AutoAddFoldersWindow : Window
{
    private readonly Action<int> _onAdded;
    private readonly List<(Fuse.Core.Launcher.FoundGameFolder Folder, CheckBox Check)> _rows = new();

    public AutoAddFoldersWindow(
        List<Fuse.Core.Launcher.FoundGameFolder> found, Action<int> onAdded)
    {
        _onAdded = onAdded;
        DialogKit.ApplyWindow(this, 500);

        var stack = new StackPanel();
        var head = DialogKit.Title("找到 " + found.Count + " 个 MC 游戏目录");
        stack.Children.Add(head);
        stack.Children.Add(DialogKit.Hint("勾选要加入文件夹列表的目录，加入后版本列表会自动显示它们。"));

        var list = new StackPanel();
        var enableCount = 0;
        foreach (var folder in found)
        {
            var row = new Border
            {
                Background = DialogKit.Res("RowBgBrush"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 6),
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = folder.Name + "（" + folder.Kind + " · " + folder.VersionCount + " 个版本）",
                FontSize = 12.5,
                Foreground = DialogKit.Res("TextPrimaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            info.Children.Add(new TextBlock
            {
                Text = folder.Path,
                FontSize = 10.5,
                FontFamily = new FontFamily("Consolas"),
                Foreground = DialogKit.Res("TextSecondaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            var check = new CheckBox
            {
                Style = (Style)Application.Current.Resources["GlassToggle"],
                IsChecked = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            System.Windows.Automation.AutomationProperties.SetName(check, "添加 " + folder.Path);
            Grid.SetColumn(check, 1);
            grid.Children.Add(check);
            _rows.Add((folder, check));
            enableCount++;

            row.Child = grid;
            list.Children.Add(row);
        }

        var scroll = new ScrollViewer
        {
            Style = (Style)Application.Current.Resources["ModernScrollViewer"],
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = list,
        };
        stack.Children.Add(scroll);

        var cancel = DialogKit.Secondary("取消", () => DialogResult = false);
        var add = DialogKit.Primary("添加所选", () => TryAdd());
        System.Windows.Automation.AutomationProperties.SetName(add, "添加所选目录");
        stack.Children.Add(DialogKit.Buttons(cancel, add));
        Content = DialogKit.Card(stack);
        DialogKit.WireDrag(this, head);
        DialogKit.WireKeys(this, TryAdd);
    }

    private void TryAdd()
    {
        var added = 0;
        foreach (var (folder, check) in _rows)
        {
            if (check.IsChecked != true) continue;
            AppServices.AddGameDirectory(folder.Path);
            added++;
        }
        if (added > 0) _onAdded(added);
        DialogResult = added > 0;
    }
}

/// <summary>一句话提示(错误/结果),只有一个「知道了」。</summary>
internal sealed class InfoWindow : Window
{
    public InfoWindow(string title, string message)
    {
        DialogKit.ApplyWindow(this, 360);
        var stack = new StackPanel();
        var head = DialogKit.Title(title);
        stack.Children.Add(head);
        stack.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 12,
            Foreground = DialogKit.Res("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });
        stack.Children.Add(DialogKit.Buttons(DialogKit.Primary("知道了", () => DialogResult = true)));
        Content = DialogKit.Card(stack);
        DialogKit.WireDrag(this, head);
        DialogKit.WireKeys(this, () => DialogResult = true);
    }
}
