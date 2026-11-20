using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace FuseSetup;

/// <summary>
/// Fuse Launcher 安装向导。
/// 支持命令行(供自动化/静默部署):
///   /INSTALL=&lt;应用安装目录&gt;  /GAMES=&lt;游戏下载目录&gt;  /SILENT  /RUN
///   /UNINSTALL=&lt;应用安装目录&gt;
/// </summary>
public partial class MainWindow : Window
{
    private const string AppName = "Fuse Launcher";
    /// <summary>安装器与启动器同版本发布,注册表/快捷方式描述直接取程序集版本。</summary>
    private static string Version =>
        (Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0)).ToString(3);
    private const string AppExe = "Fuse Launcher.exe";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Fuse Launcher";
    private static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FuseLauncher");

    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "fuse-setup.log");

    private static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + msg + Environment.NewLine); }
        catch { }
    }

    private int _step;
    private string _installDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Fuse Launcher");
    private string _gamesDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FuseLauncher");
    private bool _silent, _autoRun, _portable;

    private TextBox _installBox, _gamesBox;
    private ProgressBar _bar;
    private TextBlock _progressText;
    private CheckBox _runCheck;

    public MainWindow()
    {
        Log("ctor: begin");
        InitializeComponent();
        Log("ctor: InitializeComponent done");
        ParseArgs();
        Log("ctor: ParseArgs done");
        // /PORTABLE 且没显式给 /GAMES:游戏目录默认放安装目录下的 games
        if (_portable && _gamesDir.Equals(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FuseLauncher"),
                StringComparison.OrdinalIgnoreCase))
            _gamesDir = Path.Combine(_installDir, "games");
        if (_silent)
        {
            // 静默模式:窗口从未显示,Close() 不会结束消息循环,必须显式 Shutdown。
            // /RUN 的启动由 Install() 内部处理(这里再启一次会开出两个启动器)。
            // 退出码交给接力脚本:0=成功,非 0=失败(它会把旧启动器重新拉起来)
            var ok = Install(_installDir, _gamesDir);
            Application.Current.Shutdown(ok ? 0 : 1);
            return;
        }
        ShowWelcome();
    }

    private void ParseArgs()
    {
        Log("args: " + string.Join(" | ", Environment.GetCommandLineArgs().Skip(1)));
        foreach (var raw in Environment.GetCommandLineArgs().Skip(1))
        {
            var a = raw.TrimStart('/');
            if (a.StartsWith("INSTALL=", StringComparison.OrdinalIgnoreCase))
                _installDir = a.Substring(8).Trim('"');
            else if (a.StartsWith("GAMES=", StringComparison.OrdinalIgnoreCase))
                _gamesDir = a.Substring(6).Trim('"');
            else if (a.StartsWith("UNINSTALL=", StringComparison.OrdinalIgnoreCase))
            {
                RunUninstall(a.Substring(10).Trim('"'));
                Environment.Exit(0);
            }
            else if (a.Equals("SILENT", StringComparison.OrdinalIgnoreCase)) _silent = true;
            else if (a.Equals("PORTABLE", StringComparison.OrdinalIgnoreCase)) _portable = true;
            else if (a.Equals("RUN", StringComparison.OrdinalIgnoreCase)) _autoRun = true;
        }

        // 自更新接力:路径用环境变量传(值不是命令行,盘根/引号/中文都不会被截断)
        var envInstall = Environment.GetEnvironmentVariable("FUSE_INSTALL_DIR");
        if (!string.IsNullOrWhiteSpace(envInstall)) _installDir = envInstall!;
        var envGames = Environment.GetEnvironmentVariable("FUSE_GAMES_DIR");
        if (!string.IsNullOrWhiteSpace(envGames)) _gamesDir = envGames!;
        // 便携模式标记跟随安装目录:自更新不传 /PORTABLE 也不会把配置写到 AppData
        if (File.Exists(Path.Combine(_installDir, "portable"))) _portable = true;
        Log("env: install='" + _installDir + "' games='" + _gamesDir + "'");
    }

    // ---------- 向导页面 ----------

    private void ShowWelcome()
    {
        _step = 0;
        BackBtn.Visibility = Visibility.Collapsed;
        NextBtn.Content = "下一步";
        Page.Content = new StackPanel
        {
            Children =
            {
                Head("欢迎使用 Fuse Launcher"),
                Para("Fuse Launcher 是一款 Minecraft Java 版下载器与启动器(作者 liumo)。"),
                Para("安装向导将帮你:"),
                Bullet("把启动器安装到你选择的目录"),
                Bullet("预置游戏下载目录(之后可在设置里修改)"),
                Bullet("创建桌面 / 开始菜单快捷方式,并注册卸载入口"),
                Para("内置的启动器会自动补齐 Java 运行时、资源文件与依赖库,无需手动配置。"),
            },
        };
    }

    private void ShowPaths()
    {
        _step = 1;
        BackBtn.Visibility = Visibility.Visible;
        NextBtn.Content = "安装";

        _installBox = new TextBox { Text = _installDir };
        _gamesBox = new TextBox { Text = _gamesDir };
        var stack = new StackPanel();
        stack.Children.Add(Head("选择安装位置"));
        stack.Children.Add(Label("启动器安装目录:"));
        stack.Children.Add(Row(_installBox, () => _installDir = Pick(_installBox, _installDir)));
        stack.Children.Add(Label("游戏下载目录(版本 / 资源 / 存档都在这里):"));
        stack.Children.Add(Row(_gamesBox, () => _gamesDir = Pick(_gamesBox, _gamesDir)));
        var portableCheck = new CheckBox
        {
            Content = "便携模式（配置/账号放安装目录，游戏目录默认在安装目录\\games）",
            FontSize = 12.5,
            Foreground = FindStatic("TextBrush"),
            Margin = new Thickness(0, 14, 0, 0),
            IsChecked = _portable,
        };
        portableCheck.Checked += (_, _) =>
        {
            _portable = true;
            if (_gamesBox != null)
            {
                _gamesDir = Path.Combine(_installDir, "games");
                _gamesBox.Text = _gamesDir;
            }
        };
        portableCheck.Unchecked += (_, _) => _portable = false;
        stack.Children.Add(portableCheck);
        stack.Children.Add(Label("已有 V1.0.0 之前版本的账号数据会自动迁移,无需手动处理。"));
        Page.Content = stack;
    }

    private void ShowProgress()
    {
        _step = 2;
        BackBtn.Visibility = Visibility.Collapsed;
        NextBtn.IsEnabled = false;
        NextBtn.Content = "安装中…";
        _bar = new ProgressBar { Height = 10, Minimum = 0, Maximum = 100, Foreground = FindResource("AccentBrush") as System.Windows.Media.Brush };
        _progressText = new TextBlock
        {
            Text = "准备中…", Foreground = FindResource("TextDimBrush") as System.Windows.Media.Brush,
            Margin = new Thickness(0, 10, 0, 0),
        };
        Page.Content = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { Head("正在安装"), new Border { Height = 20 }, _bar, _progressText },
        };
        DispatchInstall();
    }

    private void ShowFinish()
    {
        _step = 3;
        NextBtn.IsEnabled = true;
        NextBtn.Content = "完成";
        _runCheck = new CheckBox
        {
            Content = "立即运行 " + AppName,
            Foreground = FindResource("TextBrush") as System.Windows.Media.Brush,
            IsChecked = true,
            Margin = new Thickness(0, 16, 0, 0),
        };
        Page.Content = new StackPanel
        {
            Children =
            {
                Head("安装完成"),
                Para(AppName + " 已安装到:\n" + _installDir),
                Para("游戏下载目录:\n" + _gamesDir),
                _runCheck,
            },
        };
    }

    private static TextBlock Head(string text) => new()
    {
        Text = text, FontSize = 20, FontWeight = FontWeights.Bold,
        Foreground = FindStatic("TextBrush"), Margin = new Thickness(0, 4, 0, 14),
    };

    private static TextBlock Para(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap,
        Foreground = FindStatic("TextDimBrush"), Margin = new Thickness(0, 0, 0, 10),
    };

    private static TextBlock Label(string text) => new()
    {
        Text = text, FontSize = 12.5, Foreground = FindStatic("TextDimBrush"),
        Margin = new Thickness(0, 12, 0, 4),
    };

    private static FrameworkElement Bullet(string text) => new TextBlock
    {
        Text = "· " + text, Foreground = FindStatic("TextDimBrush"), Margin = new Thickness(12, 0, 0, 4),
    };

    private static Grid Row(TextBox box, Action browse)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(box, 0);
        grid.Children.Add(box);
        var btn = new Button { Content = "浏览…", Style = (Style)Application.Current.Resources["Btn"], Margin = new Thickness(8, 0, 0, 0) };
        btn.Click += (_, _) => browse();
        Grid.SetColumn(btn, 1);
        grid.Children.Add(btn);
        return grid;
    }

    private static System.Windows.Media.Brush FindStatic(string key)
        => (System.Windows.Media.Brush)Application.Current.Resources[key];

    private string Pick(TextBox box, string current)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            SelectedPath = Directory.Exists(current) ? current : "",
            ShowNewFolderButton = true,
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            box.Text = dlg.SelectedPath;
            return dlg.SelectedPath;
        }
        return box.Text;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 1) ShowWelcome();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case 0: ShowPaths(); break;
            case 1:
                _installDir = _installBox.Text.Trim();
                _gamesDir = _gamesBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(_installDir) || string.IsNullOrWhiteSpace(_gamesDir))
                {
                    MessageBox.Show("请填写安装目录和游戏下载目录。", AppName);
                    return;
                }
                ShowProgress();
                break;
            case 3:
                if (_runCheck?.IsChecked == true) Launch();
                Close();
                break;
        }
    }

    private void DispatchInstall()
    {
        // UI 线程先画一帧进度条,再进后台执行复制
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) => { timer.Stop(); Install(_installDir, _gamesDir); };
        timer.Start();
    }

    // ---------- 安装 / 卸载 ----------

    private bool Install(string installDir, string gamesDir)
    {
        Log($"install begin: '{installDir}' games='{gamesDir}' silent={_silent}");
        try
        {
            Report(4, "关闭正在运行的程序…");
            KillRunningProcesses();

            Report(5, "写入程序文件…");
            Directory.CreateDirectory(installDir);
            var self = Assembly.GetExecutingAssembly().Location;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("FuseSetup.payload.zip"))
            {
                if (stream == null) throw new FileNotFoundException("安装包内缺少 payload.zip");
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
                var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
                var skipped = new List<string>();
                for (var i = 0; i < entries.Count; i++)
                {
                    var target = Path.Combine(installDir, entries[i].FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
                    try
                    {
                        entries[i].ExtractToFile(target, overwrite: true);
                    }
                    catch (IOException ex)
                    {
                        // 被占用的文件(PresentMon 探针等遗留进程锁住)跳过而不是中止整个安装:
                        // 中途抛出会让前面已写入的文件停在一半(2026-11-14 用户实测 PresentMon 锁 tools 文件)
                        Log("skip locked: " + target + " · " + ex.Message);
                        skipped.Add(entries[i].FullName);
                    }
                    Report(5 + 80 * (i + 1) / entries.Count, "写入 " + entries[i].Name);
                }
                if (skipped.Count > 0)
                    Log("install done with " + skipped.Count + " locked-file skip(s): " + string.Join(", ", skipped));
            }

            Report(88, "预置配置…");
            if (_portable)
            {
                // 便携模式:exe 同目录写 portable 标记,配置放 <安装目录>\data
                File.WriteAllText(Path.Combine(installDir, "portable"), "");
                var portableData = Path.Combine(installDir, "data");
                Directory.CreateDirectory(portableData);
                var portableConfig = Path.Combine(portableData, "config.json");
                if (!File.Exists(portableConfig))
                    WriteInitialConfig(portableConfig, gamesDir);
            }
            else
            {
                Directory.CreateDirectory(AppDataDir);
                var configPath = Path.Combine(AppDataDir, "config.json");
                if (!File.Exists(configPath))
                {
                    // 只在全新安装时预置游戏目录;升级用户保留原有配置(启动器自身会做旧数据迁移)
                    WriteInitialConfig(configPath, gamesDir);
                }
            }
            // 游戏目录只影响"首次安装的默认下载目录":升级时路径失效(移动硬盘拔了/
            // 盘符变了/手填了非法路径)不该让整个更新失败
            try { Directory.CreateDirectory(gamesDir); }
            catch (Exception ex) { Log("games dir create skipped: " + ex.Message); }

            Report(92, "创建快捷方式…");
            // 快捷方式只是便利:桌面/开始菜单被重定向到不存在的目录(OneDrive 卸载、
            // 组策略)时不能把整个更新判失败
            TryShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"),
                Path.Combine(installDir, AppExe), installDir);
            var startMenu = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
            try { Directory.CreateDirectory(startMenu); } catch (Exception ex) { Log("start menu skipped: " + ex.Message); }
            TryShortcut(Path.Combine(startMenu, AppName + ".lnk"), Path.Combine(installDir, AppExe), installDir);
            TryShortcut(Path.Combine(startMenu, "卸载 " + AppName + ".lnk"),
                Path.Combine(installDir, "Uninstall " + AppName + ".exe"), installDir);

            Report(96, "注册卸载信息…");
            // 卸载助手是独立的小程序(payload 内),外观/体积都和安装器不同;
            // 老安装包没有它时退回"复制自己"(兼容旧 payload)
            var uninstallExe = Path.Combine(installDir, "Uninstall " + AppName + ".exe");
            if (!File.Exists(uninstallExe))
            {
                try
                {
                    File.Copy(self, uninstallExe, overwrite: true);
                    Log("uninstaller missing in payload, copied self");
                }
                catch (Exception ex)
                {
                    Log("uninstaller copy skipped: " + ex.Message);
                }
            }
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", AppName);
                key.SetValue("DisplayVersion", Version);
                key.SetValue("Publisher", "liumo");
                key.SetValue("InstallLocation", installDir);
                key.SetValue("DisplayIcon", Path.Combine(installDir, AppExe));
                // 必须带 /UNINSTALL=<目录>:只写 exe 路径的话,双击会打开安装向导(旧版本的 bug)
                key.SetValue("UninstallString",
                    "\"" + uninstallExe + "\" /UNINSTALL=\"" + installDir + "\"");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }

            Report(100, "完成");
            Log("install ok");
            if (_silent) { if (_autoRun) Launch(); return true; }
            ShowFinish();
            return true;
        }
        catch (Exception ex)
        {
            Log("install FAILED: " + ex);
            // 静默模式(自动更新)不能弹框:没人点,接力脚本只能看到进程卡住/失败
            if (!_silent)
            {
                MessageBox.Show("安装失败：" + ex.Message, AppName, MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
            return false;
        }
    }

    private static void WriteInitialConfig(string path, string gamesDir)
    {
        var escaped = gamesDir.Replace("\\", "\\\\").Replace("\"", "\\\"");
        // 与 AppConfig.Save 一致:带 BOM 的 UTF-8,外部工具能正确识别中文路径
        File.WriteAllText(path, "{\"InstallDirectory\": \"" + escaped + "\"}", new UTF8Encoding(true));
    }

    /// <summary>
    /// 升级/重装前先结束占用文件的进程(启动器本体、旧版卸载助手)。
    /// 不做这一步时,覆盖 Fuse Launcher.exe / Uninstall ... 会被系统拒绝("文件正在使用")。
    /// </summary>
    private static void KillRunningProcesses()
    {
        var me = Process.GetCurrentProcess();
        foreach (var name in new[] { "Fuse Launcher", "Uninstall Fuse Launcher" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                if (p.Id == me.Id) continue;
                try
                {
                    p.Kill();
                    p.WaitForExit(3000);
                    Log("killed " + name + " pid=" + p.Id);
                }
                catch (Exception ex)
                {
                    Log("kill " + name + ": " + ex.Message);
                }
            }
        }
        System.Threading.Thread.Sleep(400);
    }

    private void Report(int pct, string text)
    {
        if (_bar == null) return;
        Dispatcher.Invoke(() =>
        {
            _bar.Value = pct;
            _progressText.Text = text;
        });
    }

    private void RunUninstall(string installDir)
    {
        var title = "卸载 " + AppName;
        if (MessageBoxResult.Yes != MessageBox.Show(
                "确定要卸载 " + AppName + " 吗？\n\n游戏下载目录(版本 / 存档)不会被删除。",
                title, MessageBoxButton.YesNo, MessageBoxImage.Question))
            return;

        try
        {
            foreach (var p in Process.GetProcessesByName("Fuse Launcher"))
            {
                try { p.Kill(); } catch { }
            }

            DeleteShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"));
            var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
            DeleteShortcut(Path.Combine(startMenu, AppName + ".lnk"));
            DeleteShortcut(Path.Combine(startMenu, "卸载 " + AppName + ".lnk"));
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);

            // 游戏下载目录默认在文档下,不在安装目录里,不会被波及
            var self = Process.GetCurrentProcess().MainModule?.FileName;
            var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 2 127.0.0.1 > nul & rmdir /s /q \"" + installDir + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            Process.Start(psi);
            if (!string.IsNullOrEmpty(self) && !self.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(self); } catch { }
            MessageBox.Show("卸载完成。", title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("卸载失败：" + ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void DeleteShortcut(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryShortcut(string lnkPath, string targetPath, string workDir)
    {
        try { CreateShortcut(lnkPath, targetPath, workDir); }
        catch (Exception ex) { Log("shortcut skipped: " + lnkPath + " · " + ex.Message); }
    }

    private static void CreateShortcut(string lnkPath, string targetPath, string workDir)
    {
        // WScript.Shell 的 CLSID;用 late binding 免去 COM 引用
        var shellType = Type.GetTypeFromCLSID(new Guid("72C24DD5-D70A-438B-8A42-98424B88AFB8"));
        dynamic shell = Activator.CreateInstance(shellType);
        try
        {
            dynamic link = shell.CreateShortcut(lnkPath);
            link.TargetPath = targetPath;
            link.WorkingDirectory = workDir;
            link.IconLocation = targetPath + ",0";
            link.Description = AppName + " V" + Version + " —— Minecraft 第三方启动器";
            link.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    private void Launch()
    {
        var exe = Path.Combine(_installDir, AppExe);
        if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }
}
