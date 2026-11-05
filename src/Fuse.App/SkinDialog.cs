using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Fuse.App;

/// <summary>
/// 本地皮肤/披风管理(离线、正版账号):选一张 png 存到
/// 文档\FuseLauncher\skins\&lt;玩家名&gt;\skin.png 或 cape.png。
/// 与皮肤站账号无关 —— 不上传、不联网。
/// </summary>
public static class SkinDialog
{
    public static void Show(string kind, string playerName, Action? onChanged)
    {
        var isCape = kind == "cape";
        var title = isCape ? "本地披风" : "本地皮肤";
        // 所有离线/正版账号共用同一个本地皮肤目录(用户要求)
        var dir = Path.Combine(AppServices.InstallDirectory, "skins", "local");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, isCape ? "cape.png" : "skin.png");

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
            Padding = new Thickness(14, 20, 14, 16),
            Width = 300,
            BorderBrush = new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)),
        };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBgBrush");

        var stack = new StackPanel();
        var head = new TextBlock
        {
            Text = title, FontSize = 15, FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
        };
        head.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        stack.Children.Add(head);

        // 皮肤页用 MIT 的 SkinView(默认站立正面、单层);披风页仍用自带视图(能显示披风)
        var skinHost = new Border
        {
            Width = 250,
            Height = 300,
            Margin = new Thickness(0, 8, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = isCape ? Visibility.Collapsed : Visibility.Visible,
        };
        stack.Children.Add(skinHost);

        var preview = new SkinPreview3D
        {
            Width = 250,
            Height = 300,
            Margin = new Thickness(0, 8, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = isCape ? Visibility.Visible : Visibility.Collapsed,
        };
        stack.Children.Add(preview);

        BitmapSource? LoadPng(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var bmp = new BitmapImage();
                using var fs = File.OpenRead(path);
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = fs;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception)
            {
                return null;
            }
        }

        void LoadPreview()
        {
            var skin = LoadPng(Path.Combine(dir, "skin.png"));
            var cape = LoadPng(Path.Combine(dir, "cape.png"));

            // 1) 初始化皮肤工具:全新实例,默认就是站立正面、单层模型
            // 2) 再填数据:只赋 SkinSource 触发一次建模,不残留上一次的姿态/角度
            skinHost.Child = new SkinView
            {
                Width = 250,
                Height = 300,
                SkinSource = skin!,
            };

            preview.SetSkin(skin, cape);
        }
        LoadPreview();

        var buttons = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };

        var pick = new Button
        {
            Content = "选择图片", Height = 40,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Style = (Style)Application.Current.Resources["GreenBigButton"],
            Margin = new Thickness(0, 0, 0, 8),
        };
        Grid.SetRow(pick, 0);
        Grid.SetColumnSpan(pick, 3);
        pick.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog
            {
                Filter = "PNG 图片 (*.png)|*.png",
                DefaultExt = ".png",
                InitialDirectory = dir,   // 默认落在启动器自己的皮肤目录
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                File.Copy(dlg.FileName, target, overwrite: true);
                LoadPreview();
                onChanged?.Invoke();
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存失败：" + ex.Message, AppServices.ProductName);
            }
        };
        buttons.Children.Add(pick);

        var row = new System.Windows.Controls.Grid();
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var clear = new Button
        {
            Content = "移除", Height = 38,
            Padding = new Thickness(18, 0, 18, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Style = (Style)Application.Current.Resources["GlassSecondaryButton"],
        };
        clear.Click += (_, _) =>
        {
            try { if (File.Exists(target)) File.Delete(target); } catch (Exception) { }
            LoadPreview();
            onChanged?.Invoke();
        };
        Grid.SetColumn(clear, 0);
        Grid.SetRow(clear, 1);
        row.Children.Add(clear);

        var close = new Button
        {
            Content = "关闭", Height = 38,
            Padding = new Thickness(18, 0, 18, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Style = (Style)Application.Current.Resources["GlassSecondaryButton"],
        };
        close.Click += (_, _) => window.Close();
        Grid.SetColumn(close, 2);
        Grid.SetRow(close, 1);
        row.Children.Add(close);
        buttons.Children.Add(row);

        stack.Children.Add(buttons);
        border.Child = stack;
        window.Content = border;

        // 手动拖动(分层窗口用 DragMove 不可靠)
        var dragging = false;
        var origin = new Point();
        border.Cursor = System.Windows.Input.Cursors.SizeAll;
        border.MouseLeftButtonDown += (_, e) =>
        {
            dragging = true;
            origin = e.GetPosition(window);
            border.CaptureMouse();
            e.Handled = true;
        };
        border.MouseMove += (_, e) =>
        {
            if (!dragging) return;
            var p2 = e.GetPosition(window);
            window.Left += p2.X - origin.X;
            window.Top += p2.Y - origin.Y;
        };
        border.MouseLeftButtonUp += (_, _) =>
        {
            dragging = false;
            border.ReleaseMouseCapture();
        };

        window.ShowDialog();
    }
}
