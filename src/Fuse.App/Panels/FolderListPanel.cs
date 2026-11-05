using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Fuse.Core.Download;
using Newtonsoft.Json.Linq;

namespace Fuse.App.Panels;

/// <summary>
/// 文件夹列表:显示名 + 完整路径,右键重命名/打开/刷新/移出/删除;
/// 底部「添加或导入」:新建 .minecraft / 添加已有文件夹 / 导入整合包。
/// </summary>
public sealed class FolderListPanel : UserControl
{
    /// <summary>文件夹增删改或切换选中后触发(由版本面板刷新版本列表)。</summary>
    public event Action? Changed;

    private readonly StackPanel _root = new();

    public FolderListPanel()
    {
        Content = _root;
        // 拖拽导入:把 .mrpack / .zip / .mcdl.json 拖到文件夹列表上直接走导入流程
        AllowDrop = true;
        DragOver += (_, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        };
        Drop += (_, e) =>
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
            HandleImportFile(files[0]);
            e.Handled = true;
        };
        Reload();
    }

    public void Reload()
    {
        _root.Children.Clear();

        _root.Children.Add(SectionHeader("文件夹列表"));

        var folderCard = new Border
        {
            Background = Res("RowBgBrush"),
            CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(Color.FromArgb(15, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
            Margin = new Thickness(0, 4, 0, 12),
        };
        var rows = new StackPanel();
        var active = AppServices.ActiveGameDirectory;
        foreach (var (path, name) in AppServices.GameFolderEntries())
        {
            var isCurrent = path.Equals(active, StringComparison.OrdinalIgnoreCase);
            rows.Children.Add(BuildFolderRow(path, name, isCurrent));
        }
        folderCard.Child = rows;
        _root.Children.Add(folderCard);

        _root.Children.Add(SectionHeader("添加或导入"));
        var actionCard = new Border
        {
            Background = Res("RowBgBrush"),
            CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(Color.FromArgb(15, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
            // 底部留 14:和下面的版本列表卡片明确分开(用户反馈第一行版本贴着卡片)
            Margin = new Thickness(0, 4, 0, 14),
        };
        var actions = new StackPanel();
        actions.Children.Add(ActionRow(_autoFindBusy ? "搜索本机目录中…" : "一键添加本机目录", OnAutoFindFolders));
        actions.Children.Add(ActionRow("新建 .minecraft 文件夹", OnCreateFolder));
        actions.Children.Add(ActionRow("添加已有文件夹", OnAddExisting));
        actions.Children.Add(ActionRow("导入整合包", OnImportModpack));
        actionCard.Child = actions;
        _root.Children.Add(actionCard);
    }

    // ---------- 列表行 ----------

    private static TextBlock SectionHeader(string text) => new()
    {
        Text = text,
        // 用户要求:字号与面板标题一致(14),但不加粗
        FontSize = 14,
        FontWeight = FontWeights.Normal,
        Foreground = Res("TextSecondaryBrush"),
        Margin = new Thickness(2, 2, 0, 0),
    };

    private Button BuildFolderRow(string path, string name, bool isCurrent)
    {
        var grid = new Grid { Margin = new Thickness(8, 5, 8, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res("TextPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        info.Children.Add(new TextBlock
        {
            Text = path,
            FontSize = 11,
            FontFamily = new FontFamily("Consolas"),
            Foreground = Res("TextSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = path,
            Margin = new Thickness(0, 1, 0, 0),
        });
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        if (isCurrent)
        {
            var chip = new TextBlock
            {
                Text = "当前",
                FontSize = 10.5,
                Foreground = Res("AccentTextBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 2, 0),
            };
            Grid.SetColumn(chip, 1);
            grid.Children.Add(chip);
        }

        var row = new Button
        {
            Style = (Style)Application.Current.Resources["FolderRowButton"],
            Content = grid,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 1, 0, 1),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = isCurrent ? Res("AccentChipSoftBrush") : Brushes.Transparent,
        };
        System.Windows.Automation.AutomationProperties.SetName(row, "文件夹 " + name);
        row.Click += (_, _) =>
        {
            if (isCurrent) return;
            AppServices.SetActiveGameDirectory(path);
            Changed?.Invoke();
        };
        row.ContextMenu = BuildContextMenu(path);
        return row;
    }

    private Button ActionRow(string label, Action onClick)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 7, 8, 7),
        };
        content.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            Foreground = Res("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var row = new Button
        {
            Style = (Style)Application.Current.Resources["FolderRowButton"],
            Content = content,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 1, 0, 1),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        System.Windows.Automation.AutomationProperties.SetName(row, label);
        row.Click += (_, _) => onClick();
        return row;
    }

    // ---------- 右键菜单 ----------

    private ContextMenu BuildContextMenu(string path)
    {
        var menu = new ContextMenu
        {
            Style = (Style)Application.Current.Resources["FolderContextMenu"],
        };
        menu.Items.Add(MenuItem("重命名", () => OnRename(path)));
        menu.Items.Add(MenuItem("打开", () => OpenFolder(path)));
        menu.Items.Add(MenuItem("刷新", () => Changed?.Invoke()));
        var remove = MenuItem("移出列表", () => OnRemove(path));
        var delete = MenuItem("删除", () => OnDelete(path));
        // 主目录是启动器自己的游戏目录:只能改名,不能移出/删除
        var isMain = AppServices.IsMainGameDirectory(path);
        remove.IsEnabled = !isMain;
        delete.IsEnabled = !isMain;
        menu.Items.Add(remove);
        menu.Items.Add(delete);
        return menu;
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem
        {
            Header = header,
            Style = (Style)Application.Current.Resources["FolderMenuItem"],
        };
        item.Click += (_, _) => action();
        return item;
    }

    // ---------- 菜单动作 ----------

    private void OnRename(string path)
    {
        var current = AppServices.GameFolderName(path);
        var dialog = new RenameFolderWindow(current, value =>
        {
            if (value.Length == 0) return "名称不能为空";
            if (value.Length > 40) return "名称最长 40 个字符";
            if (value.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
                return "名称不能包含 \\ / : * ? \" < > |";
            return null;
        });
        if (Show(dialog) == true)
        {
            AppServices.RenameGameFolder(path, dialog.Value);
            Changed?.Invoke();
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                Toast.ShowNearOrb("文件夹不存在:" + path);
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + path + "\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Toast.ShowNearOrb("打开失败:" + ex.Message);
        }
    }

    private void OnRemove(string path)
    {
        if (AppServices.IsMainGameDirectory(path)) return;
        var name = AppServices.GameFolderName(path);
        if (!AppServices.RemoveGameDirectory(path)) return;
        Toast.ShowNearOrb("已移出列表:" + name);
        Changed?.Invoke();
    }

    private void OnDelete(string path)
    {
        if (AppServices.IsMainGameDirectory(path)) return;
        if (Show(new DeleteFolderWindow(path)) != true) return;
        Toast.ShowNearOrb("已删除文件夹:" + path);
        Changed?.Invoke();
    }

    // ---------- 添加或导入 ----------

    private void OnCreateFolder()
    {
        var defaultParent = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var dialog = new NewFolderWindow(defaultParent);
        if (Show(dialog) != true) return;
        Toast.ShowNearOrb("已新建并切换:" + dialog.CreatedPath);
        Changed?.Invoke();
    }

    private void OnAddExisting()
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择 MC 游戏目录(.minecraft 或外部启动器目录,里面应有 versions 文件夹)",
            ShowNewFolderButton = false,
        };
        if (dlg.ShowDialog(new DialogKit.OwnerHandle(DialogKit.HandleOf(Window.GetWindow(this)!))) !=
            System.Windows.Forms.DialogResult.OK) return;
        AppServices.AddGameDirectory(dlg.SelectedPath);
        Changed?.Invoke();
    }

    /// <summary>一键添加本机目录:后台扫描常见位置,弹结果清单让用户勾选。</summary>
    private void OnAutoFindFolders()
    {
        if (_autoFindBusy) return;
        _autoFindBusy = true;
        Reload();
        var known = AppServices.GameDirectories;
        _ = Task.Run(async () =>
        {
            List<Fuse.Core.Launcher.FoundGameFolder> found;
            try
            {
                found = Fuse.Core.Launcher.McFolderFinder.Find(known).ToList();
            }
            catch (Exception ex)
            {
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    _autoFindBusy = false;
                    Reload();
                    Toast.ShowNearOrb("搜索失败:" + ex.Message);
                }));
                return;
            }

            await Dispatcher.InvokeAsync(new Action(() =>
            {
                _autoFindBusy = false;
                Reload();
                ShowAutoFindResult(found);
            }));
        });
    }

    private bool _autoFindBusy;

    private void ShowAutoFindResult(List<Fuse.Core.Launcher.FoundGameFolder> found)
    {
        if (found.Count == 0)
        {
            Toast.ShowNearOrb("没有找到新的 MC 游戏目录");
            return;
        }
        Show(new AutoAddFoldersWindow(found, addedCount =>
        {
            Toast.ShowNearOrb("已添加 " + addedCount + " 个游戏目录");
            Changed?.Invoke();
        }));
    }

    /// <summary>导入本地 Modrinth 整合包(.mrpack),复用下载队列的全自动安装管线。</summary>
    private void OnImportModpack()
    {
        using var dlg = new System.Windows.Forms.OpenFileDialog
        {
            Title = "选择整合包 / 版本导出包 / 下载清单",
            Filter = "整合包与导出包 (*.mrpack;*.zip;*.mcdl.json)|*.mrpack;*.zip;*.mcdl.json|" +
                     "Modrinth 整合包 (*.mrpack)|*.mrpack|ZIP 压缩包 (*.zip)|*.zip|" +
                     "下载清单 (*.mcdl.json)|*.mcdl.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(new DialogKit.OwnerHandle(DialogKit.HandleOf(Window.GetWindow(this)!))) !=
            System.Windows.Forms.DialogResult.OK) return;
        HandleImportFile(dlg.FileName);
    }

    /// <summary>按扩展名/内容识别并导入(文件对话框与拖拽共用)。</summary>
    public void HandleImportFile(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        var root = AppServices.ActiveGameDirectory;

        // 启动器导出的下载清单:按原版 + 加载器 + 模组重建一个实例
        if (path.EndsWith(".mcdl.json", StringComparison.OrdinalIgnoreCase))
        {
            ImportMcdlManifest(path);
            return;
        }

        string title = Path.GetFileNameWithoutExtension(path);
        string slug = title;
        string versionNumber = "1.0";
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            // 自家导出的完整版本包:versions/<id> + instances/<id> + libraries
            if (IsExportedVersionZip(zip))
            {
                var imported = AppServices.ImportExportedVersion(path, out var importError);
                if (imported == null)
                {
                    Show(new InfoWindow("无法导入版本包", importError));
                    return;
                }
                Toast.ShowNearOrb("已导入版本:" + imported);
                Changed?.Invoke();
                return;
            }
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.Equals("modrinth.index.json", StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                // 不是 Modrinth 格式:先按已知格式分类;解压式整合包(博主自制 zip)走"选基础版本"导入
                var verdict = ClassifyArchive(zip);
                if (verdict.Kind == "plain")
                {
                    ImportPlainPack(path);
                    return;
                }
                Show(new InfoWindow("无法导入这个整合包", verdict.Message));
                return;
            }
            using var reader = new StreamReader(entry.Open());
            var index = JObject.Parse(reader.ReadToEnd());
            title = index.Value<string>("name") ?? title;
            versionNumber = index.Value<string>("versionId") ?? versionNumber;
            var gameVersion = index["dependencies"]?.Value<string>("minecraft");
            var loader = (index["dependencies"] as JObject)?.Properties()
                .FirstOrDefault(p => p.Name != "minecraft")?.Name;
            if (string.IsNullOrEmpty(gameVersion))
                throw new InvalidOperationException("整合包索引缺少 minecraft 版本依赖");
            if (loader == "quilt")
                throw new InvalidOperationException("该整合包使用 Quilt 加载器,暂不支持");
        }
        catch (Exception ex)
        {
            Show(new InfoWindow("无法导入整合包", ex.Message));
            return;
        }

        var packVersionId = DownloadQueue.SanitizePackVersionId(slug, versionNumber);
        var versionsRoot = AppServices.VersionsRoot;
        if (File.Exists(Path.Combine(versionsRoot, packVersionId, packVersionId + ".json")))
        {
            Show(new InfoWindow("无法导入整合包",
                "当前游戏目录里已经安装过这个整合包版本(" + packVersionId + "),请先删除旧版本再导入。"));
            return;
        }

        var meta = new JObject
        {
            ["mrpackLocalPath"] = path,
            ["packSlug"] = slug,
            ["versionNumber"] = versionNumber,
            ["title"] = title,
            ["versionsRoot"] = versionsRoot,
            ["librariesRoot"] = AppServices.LibrariesRoot,
            ["assetsRoot"] = AppServices.AssetsRoot,
            ["instancesRoot"] = Path.Combine(root, "instances"),
            // 外部目录(共享根布局)没有 instances 隔离:实例目录就是根目录本身
            ["instanceDir"] = AppServices.InstanceDirectoryIn(root, packVersionId),
        }.ToString();

        var task = AppServices.Queue!.Enqueue(new DownloadTaskRecord
        {
            Name = title,
            Kind = "modpack-install",
            Meta = meta,
        });
        DownloadFocus.Register(task.TaskId, "versions");
        Toast.ShowNearOrb("整合包安装已开始:" + title);

        // 装完把整合包名写进版本档案(与下载页同款逻辑)
        var displayName = title + " " + versionNumber;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(1000).ConfigureAwait(false);
                var rec = AppServices.Queue?.GetTasks()?.FirstOrDefault(t => t.TaskId == task.TaskId);
                if (rec == null) return;
                if (rec.Status == DownloadTaskStatus.Downloading || rec.Status == DownloadTaskStatus.Queued)
                    continue;
                if (rec.Status == DownloadTaskStatus.Completed)
                {
                    var profile = AppServices.VersionProfiles.Get(packVersionId);
                    profile.DisplayName = displayName;
                    AppServices.VersionProfiles.Set(packVersionId, profile);
                    // 装完了刷新版本列表(面板还开着的话立即可见)
                    _ = Dispatcher.BeginInvoke(new Action(() => Changed?.Invoke()));
                }
                return;
            }
        });

        Changed?.Invoke();
    }

    /// <summary>不是 Modrinth 格式时分类:能导入的解压式包返回 plain,其余返回看得懂的说明。</summary>
    private static (string Kind, string Message) ClassifyArchive(System.IO.Compression.ZipArchive zip)
    {
        bool Has(string name) => zip.Entries.Any(e =>
            e.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));

        var nested = zip.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith("/modrinth.index.json", StringComparison.OrdinalIgnoreCase));
        if (nested != null)
            return ("nested",
                "这个整合包的清单在子文件夹里(" + nested.FullName + ")。\n\n" +
                "请解压后把里面的内容重新压成 zip,让 modrinth.index.json 位于压缩包最外层,再导入一次。");

        var manifest = zip.Entries.FirstOrDefault(e =>
            e.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
        if (manifest != null)
        {
            try
            {
                using var reader = new StreamReader(manifest.Open());
                var json = JObject.Parse(reader.ReadToEnd());
                if (json.Value<string>("manifestType") == "minecraftModpack")
                    return ("curseforge",
                        "这个 zip 是 CurseForge 格式的整合包。\n\n" +
                        "启动器目前只支持 Modrinth 格式(.mrpack):CurseForge 整合包需要官方授权才能自动下载文件,暂时装不了。\n\n" +
                        "可以让整合包作者提供 Modrinth 版本,或先用其他启动器安装这一个包。");
            }
            catch (Exception)
            {
                // manifest 读不懂就继续按下面的类型判断
            }
        }

        if (Has("minecraftinstance.json") || Has("mmc-pack.json"))
            return ("instance-archive",
                "这个 zip 是 其他启动器的实例压缩包,不是可安装的整合包。\n\n" +
                "可以先用对应启动器导入,再在「文件夹列表」里添加它的 .minecraft 目录。");

        // 博主自制的"解压即用"包:没有清单,但里面是 mods / config / .minecraft
        if (AppServices.LooksLikePlainPack(zip))
            return ("plain", "");

        return ("unknown",
            "无法识别这个压缩包:里面没有 Modrinth 的 modrinth.index.json,也没有可识别的游戏文件。\n\n" +
            "支持:Modrinth 整合包(.mrpack / 含 modrinth.index.json 的 zip),或包含 mods / config 的解压式整合包。");
    }

    /// <summary>解压式整合包:选一个基础版本后导入到独立实例。</summary>
    private void ImportPlainPack(string zipPath)
    {
        var root = AppServices.ActiveGameDirectory;
        if (!AppServices.IsManagedLayout(root))
        {
            Show(new InfoWindow("请先切换游戏目录",
                "当前选中的是外部游戏目录(共享根布局),解压式整合包会直接覆盖里面的 mods/config。\n\n" +
                "请先切换到启动器自己的「Fuse 启动器文件夹」再导入。"));
            return;
        }
        var versions = Fuse.Core.Versioning.InstalledVersionScanner.Scan(
            AppServices.VersionsRoot, AppServices.InstancesRoot, AppServices.LibrariesRoot,
            externalLayout: false);
        if (versions.Count == 0)
        {
            Show(new InfoWindow("还没有可用的基础版本",
                "请先在下载页安装一个游戏版本。\n\n" +
                "如果整合包里是 Forge / Fabric 模组,要先装好对应加载器的版本,再回来导入。"));
            return;
        }

        var dialog = new ImportPlainPackWindow(
            zipPath, Path.GetFileNameWithoutExtension(zipPath),
            versions.Select(v => (v.Id, VersionsPanel.DisplayName(v))).ToList());
        if (Show(dialog) != true) return;
        Toast.ShowNearOrb("已导入整合包:" + Path.GetFileNameWithoutExtension(zipPath));
        Changed?.Invoke();
    }

    /// <summary>是不是启动器自己导出的完整版本包(versions/&lt;id&gt;/&lt;id&gt;.json + instances/&lt;id&gt;)。</summary>
    private static bool IsExportedVersionZip(System.IO.Compression.ZipArchive zip)
        => zip.Entries.Any(e =>
        {
            var parts = e.FullName.Replace('\\', '/').Split('/');
            return parts.Length == 3 &&
                   parts[0].Equals("versions", StringComparison.OrdinalIgnoreCase) &&
                   parts[2].Equals(parts[1] + ".json", StringComparison.OrdinalIgnoreCase);
        });

    /// <summary>
    /// 导入启动器导出的 .mcdl.json 下载清单:
    /// 原版 → (等待完成) → Fabric 加载器 → 模组,全部进下载队列。
    /// </summary>
    private void ImportMcdlManifest(string path)
    {
        try
        {
            var json = JObject.Parse(File.ReadAllText(path));
            if (!string.Equals(json.Value<string>("kind"), "mcdl-manifest", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("不是启动器导出的下载清单(.mcdl.json)");
            var items = json["items"] as JArray ?? throw new InvalidOperationException("清单内容为空");
            var mc = items.OfType<JObject>().FirstOrDefault(i => i.Value<string>("type") == "mc-version")
                     ?? throw new InvalidOperationException("清单缺少原版版本");
            var gv = mc.Value<string>("versionId")
                     ?? throw new InvalidOperationException("原版版本缺少版本号");
            var loader = items.OfType<JObject>().FirstOrDefault(i => i.Value<string>("type") == "loader");
            var mods = items.OfType<JObject>().Where(i => i.Value<string>("type") == "mod").ToList();

            var root = AppServices.ActiveGameDirectory;
            if (!AppServices.IsManagedLayout(root))
            {
                Show(new InfoWindow("请先切换游戏目录",
                    "外部游戏目录不支持清单导入,请先切换到「Fuse 启动器文件夹」。"));
                return;
            }

            var loaderVersion = loader?.Value<string>("loaderVersion");
            var finalVersionId = string.IsNullOrEmpty(loaderVersion) ? gv : gv + "-fabric-" + loaderVersion;
            var instanceDir = AppServices.InstanceDirectoryIn(root, finalVersionId);
            var versionsRoot = Path.Combine(root, "versions");
            var librariesRoot = Path.Combine(root, "libraries");
            var assetsRoot = Path.Combine(root, "assets");

            var vanillaTask = AppServices.Queue!.Enqueue(new DownloadTaskRecord
            {
                Name = gv,
                Kind = "vanilla-install",
                SourceUrl = mc.Value<string>("url") ?? "",
                ExpectedHash = mc.Value<string>("sha1") ?? "",
                Meta = new JObject
                {
                    ["versionId"] = gv,
                    ["versionsRoot"] = versionsRoot,
                    ["librariesRoot"] = librariesRoot,
                    ["assetsRoot"] = assetsRoot,
                    ["gameDirectory"] = instanceDir,
                }.ToString(),
            });
            DownloadFocus.Register(vanillaTask.TaskId, "versions");

            _ = Task.Run(async () =>
            {
                // 加载器要读 versions/<gv>/<gv>.json,必须等原版装完
                while (true)
                {
                    await Task.Delay(1000).ConfigureAwait(false);
                    var rec = AppServices.Queue?.GetTasks()?.FirstOrDefault(t => t.TaskId == vanillaTask.TaskId);
                    if (rec == null) return;
                    if (rec.Status == DownloadTaskStatus.Queued ||
                        rec.Status == DownloadTaskStatus.Downloading) continue;
                    if (rec.Status != DownloadTaskStatus.Completed)
                    {
                        _ = Dispatcher.BeginInvoke(new Action(() =>
                            Toast.ShowNearOrb("清单导入中止:原版安装失败")));
                        return;
                    }
                    break;
                }

                if (!string.IsNullOrEmpty(loaderVersion))
                {
                    var fabricTask = AppServices.Queue!.Enqueue(new DownloadTaskRecord
                    {
                        Name = finalVersionId,
                        Kind = "fabric-install",
                        Meta = new JObject
                        {
                            ["gameVersion"] = gv,
                            ["mcVersion"] = gv,
                            ["loaderVersion"] = loaderVersion,
                            ["versionsRoot"] = versionsRoot,
                            ["librariesRoot"] = librariesRoot,
                            ["assetsRoot"] = assetsRoot,
                            ["gameDirectory"] = instanceDir,
                            ["instancesRoot"] = Path.Combine(root, "instances"),
                        }.ToString(),
                    });
                    DownloadFocus.Register(fabricTask.TaskId, "versions");
                }

                var modsDir = Path.Combine(instanceDir, "mods");
                foreach (var m in mods)
                {
                    var url = m.Value<string>("url") ?? "";
                    if (string.IsNullOrEmpty(url)) continue;
                    var fileName = m.Value<string>("fileName") ?? "";
                    if (fileName.Length == 0)
                        fileName = Path.GetFileName(new Uri(url).AbsolutePath) ?? "";
                    if (fileName.Length == 0) continue;
                    var task = AppServices.Queue!.Enqueue(new DownloadTaskRecord
                    {
                        Name = fileName,
                        Kind = "file",
                        SourceUrl = url,
                        DestPath = Path.Combine(modsDir, fileName),
                        ExpectedHash = m.Value<string>("sha1") ?? "",
                    });
                    DownloadFocus.Register(task.TaskId, "versions");
                    AppServices.RecordModInstall(instanceDir, url, fileName, m.Value<string>("sha1") ?? "");
                }

                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    Toast.ShowNearOrb("清单导入已开始:" + finalVersionId + " + " + mods.Count + " 个模组");
                    Changed?.Invoke();
                }));
            });

            Toast.ShowNearOrb("正在按清单重建:" + finalVersionId);
        }
        catch (Exception ex)
        {
            Show(new InfoWindow("无法导入清单", ex.Message));
        }
    }

    // ---------- 工具 ----------

    private bool? Show(Window dialog)
    {
        var owner = Window.GetWindow(this);
        if (owner != null) dialog.Owner = owner;
        return dialog.ShowDialog();
    }

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];
}
