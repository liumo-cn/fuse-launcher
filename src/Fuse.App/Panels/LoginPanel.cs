using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fuse.Core.Accounts;

namespace Fuse.App.Panels;

/// <summary>登录面板(FR-12):正版(设备码六步链)主钮 + 离线次钮,令牌 DPAPI 加密存储。</summary>
public class LoginPanel : UserControl
{
    private readonly StackPanel _offlineInput;
    private readonly TextBox _username;
    private readonly TextBlock _current;

    public LoginPanel()
    {
        var stack = new StackPanel();

        // 头部:居中头像 → 玩家名 → 皮肤/披风入口(当前登录哪个类型,由下面的卡片高亮体现)
        _avatar = new Image { Width = 96, Height = 96 };
        RenderOptions.SetBitmapScalingMode(_avatar, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(_avatar, EdgeMode.Aliased);
        _avatar.SnapsToDevicePixels = true;
        var avatarBox = new Border
        {
            Width = 96, Height = 96,
            CornerRadius = new CornerRadius(22),
            Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = _avatar,
        };
        stack.Children.Add(avatarBox);

        _current = new TextBlock
        {
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 8, 0, 6),
        };
        _current.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        stack.Children.Add(_current);

        var skinCape = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
        };
        skinCape.Children.Add(SkinCapeChip("皮肤", "skin"));
        skinCape.Children.Add(SkinCapeChip("披风", "cape"));
        stack.Children.Add(skinCape);

        // 已保存账号列表(点击切换;右侧 ✕ 删除)
        _accountsHost = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };
        stack.Children.Add(_accountsHost);

        // 正版主钮
        _msButton = new Button
        {
            Margin = new Thickness(0, 0, 0, 10),
            Content = BuildButtonContent("Ⓜ", "正版账号登录", "使用 Microsoft 账号 · 需自备授权"),
        };
        System.Windows.Automation.AutomationProperties.SetName(_msButton, "正版账号登录");
        _msButton.Click += (_, _) =>
        {
            OpenSection("msa");   // 收起其它两个入口的表单
            var login = new MicrosoftLoginWindow { Owner = null };
            login.ShowDialog();
            RefreshState();
        };
        stack.Children.Add(_msButton);

        // 离线次钮
        _offlineButton = new Button
        {
            Content = BuildButtonContent("●", "离线账号登录", "本地模式 · 不联网验证"),
        };
        System.Windows.Automation.AutomationProperties.SetName(_offlineButton, "离线账号登录");
        stack.Children.Add(_offlineButton);

        // 离线用户名内联输入
        _offlineInput = new StackPanel { Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        // 和外置表单用同一套输入壳:显式浅色前景 + 原生输入框(自定义模板在深底上会把文字裁掉)
        _username = new TextBox();
        _offlineInput.Children.Add(InputShell(_username));
        var go = new Button
        {
            Style = (Style)Application.Current.Resources["ChipButton"],
            Content = "离线开始",
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        go.Click += (_, _) => AcceptOffline();
        _username.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) AcceptOffline(); };
        _offlineInput.Children.Add(go);
        stack.Children.Add(_offlineInput);

        _offlineButton.Click += (_, _) =>
        {
            OpenSection("offline");
            if (_openSection == "offline") _username.Focus();
        };

        // 外置登录(皮肤站 / authlib-injector):第三张卡片 + 内联表单
        _extButton = new Button
        {
            Margin = new Thickness(0, 10, 0, 0),
            Content = BuildButtonContent("♦", "皮肤站外置登录", "LittleSkin 等 authlib-injector 站点"),
        };
        System.Windows.Automation.AutomationProperties.SetName(_extButton, "皮肤站外置登录");
        stack.Children.Add(_extButton);

        _extForm = new StackPanel { Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        _extForm.Children.Add(FieldLabel("站点地址(留空用 LittleSkin)"));
        _addressBox = new TextBox { Text = "littleskin.cn" };
        _extForm.Children.Add(InputShell(_addressBox));
        _extSiteNote = new TextBlock
        {
            FontSize = 10.5,
            Margin = new Thickness(2, 2, 2, 4),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        _extSiteNote.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
        _extForm.Children.Add(_extSiteNote);
        if (ElevationStatus.IsAdministrator)
        {
            var adminHint = new TextBlock
            {
                Text = "管理员模式下浏览器拖拽不可用：复制 LittleSkin 卡片地址后点「从剪贴板识别」。",
                FontSize = 10.5,
                Opacity = 0.66,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 0, 2, 4),
            };
            adminHint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            _extForm.Children.Add(adminHint);
        }
        _extForm.Children.Add(FieldLabel("用户邮箱"));
        _userBox = new TextBox();
        _extForm.Children.Add(InputShell(_userBox));
        _extForm.Children.Add(FieldLabel("密码"));
        _passwordBox = new PasswordBox();
        _extForm.Children.Add(InputShell(_passwordBox));
        _extStatus = new TextBlock
        {
            FontSize = 11, Margin = new Thickness(2, 8, 2, 0),
            TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
        };
        _extStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        _extForm.Children.Add(_extStatus);
        var extGo = new Button
        {
            Style = (Style)Application.Current.Resources["ChipButton"],
            Content = "登录皮肤站",
            Margin = new Thickness(0),
            Height = 30,
            Padding = new Thickness(14, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        extGo.Click += async (_, _) => await AcceptExternalAsync();

        var clip = new Button
        {
            Style = (Style)Application.Current.Resources["ChipButton"],
            Content = "从剪贴板识别",
            Margin = new Thickness(0, 0, 8, 0),
            Height = 30,
            Padding = new Thickness(14, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(clip, "从剪贴板识别认证服务器");
        clip.Click += async (_, _) =>
        {
            var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
            if (!AuthServerDrop.TryExtractUrl(text, out var url))
            {
                _extStatus!.Text = "剪贴板里没有可识别的 https 地址";
                _extStatus.Visibility = Visibility.Visible;
                return;
            }
            await AuthServerDrop.AddAsync(url, Window.GetWindow(this), fromClipboard: true);
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        buttons.Children.Add(clip);
        buttons.Children.Add(extGo);
        _extForm.Children.Add(buttons);
        stack.Children.Add(_extForm);
        _extButton.Click += (_, _) => OpenSection("external");

        Content = new ScrollViewer
        {
            Style = (Style)Application.Current.Resources["ModernScrollViewer"],
            MaxHeight = 520,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = stack,
        };
        RefreshState();
    }

    private Button? _msButton;
    private Button? _offlineButton;
    private Button? _extButton;
    private StackPanel? _extForm;
    private TextBlock? _extStatus;
    private TextBlock? _extSiteNote;
    private TextBox? _addressBox;
    private TextBox? _userBox;
    private PasswordBox? _passwordBox;
    private string? _openSection;
    private Image? _avatar;
    private StackPanel? _accountsHost;

    /// <summary>圆角外壳 + 原生输入框(显式浅色前景):不依赖主题资源键,避免深底黑字看不清。</summary>
    private static Border InputShell(Control input)
    {
        input.Height = 32;
        input.FontSize = 12;
        input.Padding = new Thickness(0);
        input.BorderThickness = new Thickness(0);
        input.Background = Brushes.Transparent;
        input.Foreground = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
        if (input is System.Windows.Controls.Primitives.TextBoxBase tb) tb.CaretBrush = Brushes.White;
        if (input is PasswordBox pb) pb.CaretBrush = Brushes.White;
        input.VerticalContentAlignment = VerticalAlignment.Center;
        return new Border
        {
            Height = 34,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 0, 10, 0),
            Child = input,
            Margin = new Thickness(0, 0, 0, 2),
        };
    }

    /// <summary>三个登录入口互斥展开:打开一个时把其它人的表单收回去。</summary>
    /// <summary>从拖拽/剪贴板识别到认证服务器后预填表单(站点只读)。</summary>
    public void PrefillAuthServer(string apiRoot, string serverName)
    {
        // 直接展开,不能走 OpenSection:它会把"已经展开的 external"当成再次点击而收起
        _offlineInput.Visibility = Visibility.Collapsed;
        if (_extForm != null) _extForm.Visibility = Visibility.Visible;
        _openSection = "external";
        if (_addressBox != null)
        {
            _addressBox.Text = apiRoot;
            _addressBox.IsReadOnly = true;
        }
        if (_extSiteNote != null)
        {
            _extSiteNote.Text = "已识别认证服务器：" + serverName;
            _extSiteNote.Visibility = Visibility.Visible;
        }
        _userBox?.Focus();
    }

    private void OpenSection(string key)
    {
        // 只允许一个下拉表单在场:先无条件全部收起,再只展开最后点击的那个
        _offlineInput.Visibility = Visibility.Collapsed;
        if (_extForm != null) _extForm.Visibility = Visibility.Collapsed;

        var same = _openSection == key;
        _openSection = same ? null : key;
        if (_openSection == "offline") _offlineInput.Visibility = Visibility.Visible;
        if (_openSection == "external" && _extForm != null) _extForm.Visibility = Visibility.Visible;
        if (_openSection == "offline") _userBox?.Focus();
        if (_openSection == "external") _addressBox?.Focus();
    }

    /// <summary>皮肤/披风入口:来源跟账号类型走。外置账号锁在站点,离线/正版用本地皮肤。</summary>
    private Button SkinCapeChip(string text, string kind)
    {
        var chip = new Button
        {
            Style = (Style)Application.Current.Resources["ChipButton"],
            Content = text,
            Margin = new Thickness(4, 0, 4, 0),
        };
        System.Windows.Automation.AutomationProperties.SetName(chip, text);
        chip.Click += (_, _) =>
        {
            var data = AppServices.Accounts.Data;
            var kindName = kind == "cape" ? "披风" : "皮肤";

            if (data.ActiveType == "external" && !string.IsNullOrEmpty(data.ExtApiRoot))
            {
                // 外置账号的皮肤只认站点:本地皮肤对它不生效,入口只负责引导去站点改
                var site = data.ExtApiRoot!.Replace("/api/yggdrasil", "");
                NoticeDialog.Show(
                    "皮肤站账号",
                    $"皮肤站账号的{kindName}只能在皮肤站修改。" + Environment.NewLine +
                    "启动器里的本地皮肤只对离线账号生效。",
                    "打开皮肤站",
                    () => { try { System.Diagnostics.Process.Start(site + "/user/closet"); } catch (Exception) { } });
                return;
            }

            if (data.ActiveType == "msa")
            {
                // 正版账号锁官方皮肤/披风:游戏会用真会话令牌从 Mojang 原生拉取
                NoticeDialog.Show(
                    "正版账号",
                    $"正版账号的{kindName}由 Mojang 官方提供,请在官网修改。" + Environment.NewLine +
                    "启动器里的本地皮肤只对离线账号生效。",
                    "打开官网",
                    () => { try { System.Diagnostics.Process.Start("https://www.minecraft.net/profile"); } catch (Exception) { } });
                return;
            }

            // 离线账号:本地皮肤/披风管理(选 png 存到 skins\local\,不上传、不联网)
            SkinDialog.Show(kind, data.OfflinePlayerName, RefreshState);
        };
        return chip;
    }

    private static TextBlock FieldLabel(string text)
    {
        var label = new TextBlock
        {
            Text = text, FontSize = 11, Margin = new Thickness(2, 10, 2, 4),
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
        return label;
    }

    /// <summary>外置登录:探测站点 → 用户名密码登录 → 保存(密码 DPAPI 加密,每次启动静默刷新)。</summary>
    private async System.Threading.Tasks.Task AcceptExternalAsync()
    {
        var address = string.IsNullOrWhiteSpace(_addressBox!.Text) ? "https://littleskin.cn" : _addressBox.Text.Trim();
        var user = _userBox!.Text.Trim();
        var password = _passwordBox!.Password;
        if (user.Length == 0 || password.Length == 0) { ShowExtStatus("请填写用户名和密码", error: true); return; }

        ShowExtStatus("正在连接站点…", error: false);
        try
        {
            var apiRoot = await Core.Accounts.ExternalAuth.ResolveApiRootAsync(
                AppServices.Http, address, System.Threading.CancellationToken.None);
            ShowExtStatus("正在登录…", error: false);
            var session = await Core.Accounts.ExternalAuth.AuthenticateAsync(
                AppServices.Http, apiRoot, user, password, Guid.NewGuid().ToString("N"),
                System.Threading.CancellationToken.None);
            AppServices.Accounts.UseExternal(apiRoot, user, password, session);
            _extStatus!.Visibility = Visibility.Collapsed;
            _passwordBox!.Password = "";
            RefreshState();
        }
        catch (Exception ex)
        {
            // 密码错误/站点不支持等属于输入问题:内联提示,不弹"坏菜了"
            ShowExtStatus(ex.Message, error: true);
        }
    }

    private void ShowExtStatus(string text, bool error)
    {
        _extStatus!.Text = text;
        if (error) _extStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
        else _extStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        _extStatus.Visibility = Visibility.Visible;
    }

    /// <summary>当前账号类型决定哪张卡片高亮为绿色主钮。</summary>
    private void ApplyHighlight()
    {
        var active = AppServices.Accounts.Data.ActiveType;
        var res = Application.Current.Resources;
        _msButton!.Style = (Style)res[active == "msa" ? "GreenBigButton" : "GlassSecondaryButton"];
        _offlineButton!.Style = (Style)res[active == "offline" ? "GreenBigButton" : "GlassSecondaryButton"];
        if (_extButton != null)
            _extButton.Style = (Style)res[active == "external" ? "GreenBigButton" : "GlassSecondaryButton"];
    }

    private static UIElement BuildButtonContent(string icon, string title, string subtitle)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var ico = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)),
            Child = new TextBlock { Text = icon, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        Grid.SetColumn(ico, 0);
        grid.Children.Add(ico);
        var txt = new StackPanel();
        txt.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold });
        txt.Children.Add(new TextBlock { Text = subtitle, FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(166, 255, 255, 255)) });
        Grid.SetColumn(txt, 1);
        grid.Children.Add(txt);
        return grid;
    }

    private void RefreshState()
    {
        var data = AppServices.Accounts.Data;
        ApplyHighlight();
        string name;
        switch (data.ActiveType)
        {
            case "msa" when !string.IsNullOrEmpty(data.MsPlayerName): name = data.MsPlayerName!; break;
            case "external" when !string.IsNullOrEmpty(data.ExtPlayerName): name = data.ExtPlayerName!; break;
            default: name = data.OfflinePlayerName; break;
        }
        _current!.Text = name;
        _ = LoadAvatarAsync(name);
        RenderAccounts();
    }

    /// <summary>
    /// 从皮肤里裁出头像:脸 8x8 + 帽子层 8x8 叠一次,输出分辨率跟随皮肤(支持 128x128 高清皮肤)。
    /// 64x32 老式皮肤没有帽子层时只取脸部。
    /// </summary>
    private static ImageSource? RenderHeadFromSkin(string skinPath)
    {
        try
        {
            var skin = new BitmapImage();
            using (var fs = System.IO.File.OpenRead(skinPath))
            {
                skin.BeginInit();
                skin.CacheOption = BitmapCacheOption.OnLoad;
                skin.StreamSource = fs;
                skin.EndInit();
                skin.Freeze();
            }
            return RenderHeadFromSkin(skin);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ImageSource? RenderHeadFromSkin(BitmapSource skin)
    {
        try
        {
            var scale = Math.Max(1, skin.PixelWidth / 64);
            var size = 8 * scale;
            var baseHead = new CroppedBitmap(skin, new Int32Rect(8 * scale, 8 * scale, size, size));

            CroppedBitmap? overlay = null;
            try
            {
                // 64x32 老皮肤:40*scale 已经超出宽度,直接跳过帽子层
                if (skin.PixelWidth >= 48 * scale && skin.PixelHeight >= 16 * scale)
                    overlay = new CroppedBitmap(skin, new Int32Rect(40 * scale, 8 * scale, size, size));
            }
            catch (Exception)
            {
                overlay = null;
            }

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(baseHead, new Rect(0, 0, size, size));
                if (overlay != null) dc.DrawImage(overlay, new Rect(0, 0, size, size));
            }

            var renderTarget = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            renderTarget.Render(visual);
            renderTarget.Freeze();
            return renderTarget;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static readonly System.Collections.Generic.Dictionary<string, BitmapSource> _mojangSkinCache
        = new System.Collections.Generic.Dictionary<string, BitmapSource>();

    /// <summary>
    /// 正版账号的官方皮肤:Mojang 会话 API → base64 textures → 皮肤图下载,按 UUID 缓存。
    /// mc-heads 在部分网络下 403,官方会话 API 是唯一不依赖第三方的来源。
    /// </summary>
    private static async System.Threading.Tasks.Task<BitmapSource?> FetchMojangSkinAsync(string uuidNoDashes)
    {
        if (string.IsNullOrEmpty(uuidNoDashes)) return null;
        lock (_mojangSkinCache)
        {
            if (_mojangSkinCache.TryGetValue(uuidNoDashes, out var hit)) return hit;
        }

        var resp = await AppServices.Http.GetAsync(
            "https://sessionserver.mojang.com/session/minecraft/profile/" + uuidNoDashes).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var profile = Newtonsoft.Json.Linq.JObject.Parse(
            await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
        // App 工程没有 PolySharp,JToken.Value<T>() 会错误绑定到 Value<T>(object key),用 ToString() 取值
        var props = profile["properties"] as Newtonsoft.Json.Linq.JArray;
        var encoded = props != null && props.Count > 0 ? props[0]?["value"]?.ToString() : null;
        if (string.IsNullOrEmpty(encoded)) return null;
        var textures = Newtonsoft.Json.Linq.JObject.Parse(
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded!)));
        var skinUrl = textures["textures"]?["SKIN"]?["url"]?.ToString();
        if (string.IsNullOrEmpty(skinUrl)) return null;

        var bytes = await AppServices.Http.GetByteArrayAsync(skinUrl!).ConfigureAwait(false);
        var skin = new BitmapImage();
        using (var ms = new System.IO.MemoryStream(bytes))
        {
            skin.BeginInit();
            skin.CacheOption = BitmapCacheOption.OnLoad;
            skin.StreamSource = ms;
            skin.EndInit();
            skin.Freeze();
        }
        lock (_mojangSkinCache)
        {
            _mojangSkinCache[uuidNoDashes] = skin;
        }
        return skin;
    }

    /// <summary>已保存账号列表:点行切换,✕ 删除。当前账号高亮。</summary>
    private void RenderAccounts()
    {
        if (_accountsHost == null) return;
        _accountsHost.Children.Clear();
        var list = AppServices.Accounts.Accounts;
        if (list.Count <= 1) return;   // 只有一条时不占地方

        foreach (var acc in list)
        {
            var isCurrent = acc.Id == AppServices.Accounts.Data.CurrentAccountId;
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var pick = new Button
            {
                Style = (Style)Application.Current.Resources["ChipButton"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = acc.DisplayName + "  ·  " + acc.TypeLabel,
            };
            System.Windows.Automation.AutomationProperties.SetName(pick, "切换账号 " + acc.DisplayName);
            var id = acc.Id;
            pick.Click += (_, _) =>
            {
                if (!AppServices.Accounts.SwitchTo(id)) return;
                RefreshState();
            };
            if (isCurrent) pick.SetResourceReference(Button.BackgroundProperty, "AccentChipBrush");
            Grid.SetColumn(pick, 0);
            row.Children.Add(pick);

            var del = new Button
            {
                Style = (Style)Application.Current.Resources["ChipButton"],
                Content = "✕",
                Width = 34,
                Height = 30,
                Padding = new Thickness(0),
                FontSize = 12,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
            };
            System.Windows.Automation.AutomationProperties.SetName(del, "删除账号 " + acc.DisplayName);
            del.Click += (_, _) =>
            {
                AppServices.Accounts.RemoveAccount(id);
                RefreshState();
            };
            Grid.SetColumn(del, 1);
            row.Children.Add(del);
            _accountsHost.Children.Add(row);
        }
    }

    /// <summary>
    /// 玩家头像:来源跟账号类型走,互斥 ——
    /// 外置(皮肤站)账号只认站点 /avatar/player/{name};
    /// 正版账号只认官方皮肤(mc-heads 代理 Mojang);
    /// 离线账号优先用本地 skin.png 裁头,没有才退回 mc-heads。
    /// </summary>
    private async System.Threading.Tasks.Task LoadAvatarAsync(string name)
    {
        try
        {
            var data = AppServices.Accounts.Data;
            var isExternal = data.ActiveType == "external" && !string.IsNullOrEmpty(data.ExtApiRoot);
            var isOffline = data.ActiveType != "external" && data.ActiveType != "msa";

            if (isOffline)
            {
                // 本地有皮肤就先渲染本地头像(所有离线账号共用 skins\local)
                var localSkin = System.IO.Path.Combine(AppServices.InstallDirectory, "skins", "local", "skin.png");
                if (System.IO.File.Exists(localSkin))
                {
                    var local = RenderHeadFromSkin(localSkin);
                    if (local != null)
                    {
                        await Dispatcher.InvokeAsync(() => { if (_avatar != null) _avatar.Source = local; });
                        return;
                    }
                }
            }
            else
            {
                // 切到在线账号:先清掉上一个账号的头像,避免站点拉取失败时顶着别人的脸
                await Dispatcher.InvokeAsync(() => { if (_avatar != null) _avatar.Source = null; });
            }

            string url;
            if (isExternal)
            {
                var site = data.ExtApiRoot!.Replace("/api/yggdrasil", "");
                url = site + "/avatar/player/" + Uri.EscapeDataString(name) + "?png";
            }
            else if (data.ActiveType == "msa" && !string.IsNullOrEmpty(data.MsPlayerUuid))
            {
                // 正版:官方皮肤(mc-heads 在部分网络 403,只作兜底)
                var official = await FetchMojangSkinAsync(data.MsPlayerUuid!.Replace("-", "")).ConfigureAwait(false);
                if (official != null)
                {
                    var head = RenderHeadFromSkin(official);
                    if (head != null)
                    {
                        await Dispatcher.InvokeAsync(() => { if (_avatar != null) _avatar.Source = head; });
                        return;
                    }
                }
                url = "https://mc-heads.net/avatar/" + Uri.EscapeDataString(name) + "/100";
            }
            else
            {
                url = "https://mc-heads.net/avatar/" + Uri.EscapeDataString(name) + "/100";
            }

            var bytes = await AppServices.Http.GetByteArrayAsync(url).ConfigureAwait(false);
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            using (var ms = new System.IO.MemoryStream(bytes))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze();
            }
            await Dispatcher.InvokeAsync(() => { if (_avatar != null) _avatar.Source = bitmap; });
        }
        catch (Exception)
        {
            // 头像拉不到不影响登录,留空即可
        }
    }

    private void AcceptOffline()
    {
        var name = _username.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show("请输入用户名。", AppServices.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        AppServices.Accounts.UseOffline(name);   // 同时写入多账号列表
        _ = OfflineAccount.CreateUuid(name);
        RefreshState();
        _offlineInput.Visibility = Visibility.Collapsed;
    }
}
