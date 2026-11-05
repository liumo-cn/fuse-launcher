using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Fuse.App.Panels;

/// <summary>
/// 「更多」面板:标题居中、分节可折叠。
/// 关于启动器常驻显示;法律信息 / 许可与版权声明 / 致谢 折叠。
/// 许可条目提供「查看来源网站」「查看许可文档」两个按钮(后者直接跳官方许可文本)。
/// </summary>
public class MorePanel : UserControl
{
    private readonly StackPanel _root = new();
    private ScrollViewer? _scroll;
    private Fold? _licenseFold;

    public MorePanel()
    {
        _root.Children.Add(new FastDownloadSection());
        _root.Children.Add(AboutSection());
        _root.Children.Add(LegalSection());

        _licenseFold = BuildFold("许可与版权声明", open: false);
        _root.Children.Add(_licenseFold.Root);
        BuildLicenseBody(_licenseFold.Body);

        var thanks = BuildFold("致谢", open: false);
        BuildThanksBody(thanks.Body);
        _root.Children.Add(thanks.Root);

        var footer = Line("© 2026 liumo · Fuse Launcher V" + AppServices.AppVersion, 10.5, opacity: 0.5);
        footer.HorizontalAlignment = HorizontalAlignment.Center;
        footer.Margin = new Thickness(0, 6, 0, 4);
        _root.Children.Add(footer);

        _scroll = new ScrollViewer
        {
            Style = (Style)Application.Current.Resources["ModernScrollViewer"],
            MaxHeight = 520,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _root,
        };
        Content = _scroll;
    }

    // ---------- 各分节 ----------

    private static Border AboutSection()
    {
        var body = new StackPanel();
        body.Children.Add(Line("Fuse Launcher  V" + AppServices.AppVersion, 12.5, primary: true, bold: true));
        body.Children.Add(Line("Minecraft 第三方启动器"));
        body.Children.Add(Line("作者：liumo"));
        body.Children.Add(Line("运行环境：Windows 10 / 11 · .NET Framework 4.8"));
        body.Children.Add(Line("联系邮箱：liumo_Mc@outlook.com"));
        return Card("关于启动器", body);
    }

    private Border LegalSection()    {
        var fold = BuildFold("法律信息", open: false);
        var body = fold.Body;

        body.Children.Add(SubHeading("匿名信息与数据", 0));
        body.Children.Add(Line("只收集崩溃日志，里面没有你的个人信息"));

        body.Children.Add(SubHeading("隐私声明与个人信息保护政策", 10));
        body.Children.Add(Line("其实我什么都没有收集 -.-"));

        body.Children.Add(SubHeading("其他信息", 10));
        body.Children.Add(Line("Copyright © 2026 liumo"));
        body.Children.Add(Line("非 MINECRAFT 官方产品。未经 MOJANG 或 MICROSOFT 批准，也不与 MOJANG 或 MICROSOFT 关联。"));

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(Chip("用户协议与免责声明", ShowAgreement));
        buttons.Children.Add(Chip("第三方许可", OpenLicenseSection));
        buttons.Children.Add(Chip("联系作者", () => Open("mailto:liumo_Mc@outlook.com")));
        body.Children.Add(buttons);

        // 法律信息 默认展开(用户只要求"关于"不折叠,这里也直接可见,方便查看声明)
        SetFold(fold, true);
        return fold.Root;
    }

    private void BuildLicenseBody(StackPanel body)
    {
        body.Children.Add(LicenseRow("Newtonsoft.Json",
            "Copyright © 2007 James Newton-King",
            "MIT License",
            "https://www.newtonsoft.com/json",
            "https://opensource.org/license/mit"));
        body.Children.Add(LicenseRow("PolySharp",
            "Copyright © PolySharp contributors",
            "MIT License",
            "https://github.com/Sergio0694/PolySharp",
            "https://opensource.org/license/mit"));
        body.Children.Add(LicenseRow("Microsoft.Win32.Registry / System.Security.*",
            "Copyright © .NET Foundation and Contributors",
            "MIT License",
            "https://github.com/dotnet/runtime",
            "https://opensource.org/license/mit"));
        body.Children.Add(LicenseRow("WPF-Minecraft-Skin-Viewer",
            "Copyright (c) 2026 mokkignomov-design",
            "MIT License",
            "https://github.com/mokkignomov-design/WPF-Minecraft-Skin-Viewer",
            "https://opensource.org/license/mit"));
        body.Children.Add(LicenseRow("authlib-injector",
            "Copyright © yushijinhun · 运行时下载，不随安装包分发",
            "AGPL-3.0",
            "https://github.com/yushijinhun/authlib-injector",
            "https://www.gnu.org/licenses/agpl-3.0.html"));
        body.Children.Add(LicenseRow("CustomSkinLoader",
            "Copyright © xfl03 · 运行时下载，不随安装包分发",
            "GPL 系列许可",
            "https://github.com/xfl03/CustomSkinLoader",
            "https://www.gnu.org/licenses/gpl-3.0.html"));
    }

    private static void BuildThanksBody(StackPanel body)
    {
        body.Children.Add(Line("以下镜像站为本启动器的下载链路提供了稳定、快速的原版资源与 Maven 镜像："));
        body.Children.Add(Line("· BMCLAPI — bmclapi2.bangbang93.com"));
        body.Children.Add(Line("· 齐鲁工业大学开源镜像站 — mirrors4.qlu.edu.cn/bmclapi"));
        body.Children.Add(Line("· HKMC 镜像 — bsmirror.hkmc.online"));
        body.Children.Add(Line("感谢以上服务的维护者。", 11, opacity: 0.7));
        body.Children.Add(Line(""));
        body.Children.Add(Line("数据浮窗的 FPS 采样依赖 Intel 开源的 PresentMon（MIT 许可）："));
        body.Children.Add(Line("· PresentMon — github.com/GameTechDev/PresentMon"));
    }

    // ---------- 按钮动作 ----------

    private void OpenLicenseSection()
    {
        if (_licenseFold == null) return;
        SetFold(_licenseFold, true);
        _licenseFold.Root.BringIntoView();
    }

    private static void ShowAgreement()
    {
        NoticeDialog.Show("用户协议与免责声明",
            "Fuse Launcher 为 Minecraft 第三方启动器，与 Mojang、Microsoft 无关联，亦未获其批准或授权。\n\n" +
            "· 游玩需拥有有效的 Minecraft 账号；离线档案仅供本地测试，不支持正版服务器联机。\n" +
            "· 游戏资源来自 Mojang 官方服务器或其镜像；模组 / 光影数据来自 Modrinth；账号服务由 Microsoft 提供。\n" +
            "· 删除版本会同时删除该实例目录（含存档），请谨慎操作。\n" +
            "· 使用本软件产生的风险由使用者自行承担。",
            null, null);
    }

    // ---------- 结构件 ----------

    private sealed class Fold
    {
        public Border Root = null!;
        public StackPanel Body = null!;
        public TextBlock Chevron = null!;
    }

    private static Fold BuildFold(string title, bool open)
    {
        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        var chevron = new TextBlock
        {
            Text = open ? "▾" : "▸",
            FontSize = 11,
            Opacity = 0.7,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        chevron.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var head = new Grid();
        head.Children.Add(titleText);
        head.Children.Add(chevron);

        var header = new Button
        {
            Style = (Style)Application.Current.Resources["FlatRowButton"],
            Content = head,
            Padding = new Thickness(10, 9, 10, 9),
        };
        System.Windows.Automation.AutomationProperties.SetName(header, title);
        var body = new StackPanel
        {
            Margin = new Thickness(14, 0, 14, 12),
            Visibility = open ? Visibility.Visible : Visibility.Collapsed,
        };

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(body);

        var root = new Border
        {
            Style = (Style)Application.Current.Resources["OptRow"],
            Padding = new Thickness(0),
            Child = stack,
        };
        var fold = new Fold { Root = root, Body = body, Chevron = chevron };
        header.Click += (_, _) => SetFold(fold, body.Visibility != Visibility.Visible);
        return fold;
    }

    private static void SetFold(Fold fold, bool open)
    {
        fold.Body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        fold.Chevron.Text = open ? "▾" : "▸";
    }

    private static Border Card(string title, UIElement body)
    {
        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
        };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        var stack = new StackPanel();
        stack.Children.Add(titleText);
        stack.Children.Add(body);
        return new Border
        {
            Style = (Style)Application.Current.Resources["OptRow"],
            Child = stack,
        };
    }

    private static UIElement LicenseRow(string name, string copyright, string license,
        string sourceUrl, string licenseUrl)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        stack.Children.Add(Line(name, 12, primary: true, bold: true));
        stack.Children.Add(Line(copyright, 11));
        stack.Children.Add(Line("Licensed under the " + license, 11));

        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(Chip("查看来源网站", () => Open(sourceUrl)));
        row.Children.Add(Chip("查看许可文档", () => Open(licenseUrl)));
        stack.Children.Add(row);
        return stack;
    }

    private static Button Chip(string text, Action click)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)Application.Current.Resources["ChipButton"],
            FontSize = 11,
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 8, 6),
        };
        button.Click += (_, _) =>
        {
            try { click(); } catch (Exception) { }
        };
        return button;
    }

    private static void Open(string url)
    {
        try { Process.Start(url); } catch (Exception) { }
    }

    private static TextBlock SubHeading(string text, double topMargin)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, topMargin, 0, 4),
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        return block;
    }

    private static TextBlock Line(string text, double size = 11.5, bool primary = false,
        bool bold = false, double opacity = 0.85)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
            Opacity = opacity,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Margin = new Thickness(0, 0, 0, 4),
        };
        block.SetResourceReference(TextBlock.ForegroundProperty,
            primary ? "TextPrimaryBrush" : "TextSecondaryBrush");
        return block;
    }
}
