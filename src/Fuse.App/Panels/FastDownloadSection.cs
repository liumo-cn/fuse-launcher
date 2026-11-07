using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fuse.Core.Download;
using Newtonsoft.Json.Linq;

namespace Fuse.App.Panels;

/// <summary>
/// 高速下载(任意文件):URL 直链 → 用户自选保存位置(默认系统下载文件夹),
/// 复用 ChunkedDownloader 的 Range 分段 + 断点续传 + 哈希校验,进下载队列统一管理。
/// </summary>
public sealed class FastDownloadSection : UserControl
{
    private readonly TextBox _url;
    private readonly TextBox _path;
    private readonly TextBox _hash;
    private readonly TextBlock _hint;
    private Button? _openBrowser;

    public FastDownloadSection()
    {
        var stack = new StackPanel();

        stack.Children.Add(new TextBlock
        {
            Text = "粘贴直链，即刻获取",
            FontSize = 11.5,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });

        stack.Children.Add(FieldLabel("下载地址"));
        _url = Input("下载地址");
        _url.TextChanged += (_, _) =>
        {
            if (_openBrowser != null) _openBrowser.Visibility = Visibility.Collapsed;
            UpdateFileName();
        };
        stack.Children.Add(_url);

        var fileRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        fileRow.ColumnDefinitions.Add(new ColumnDefinition());
        fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = System.Windows.GridLength.Auto });
        stack.Children.Add(FieldLabel("保存到"));
        var browseRow = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        browseRow.ColumnDefinitions.Add(new ColumnDefinition());
        browseRow.ColumnDefinitions.Add(new ColumnDefinition { Width = System.Windows.GridLength.Auto });
        _path = Input(DefaultDirectory());
        _path.Margin = new Thickness(0);
        System.Windows.Automation.AutomationProperties.SetName(_path, "保存位置");
        System.Windows.Controls.Grid.SetColumn(_path, 0);
        browseRow.Children.Add(_path);
        var browse = new Button
        {
            Content = "浏览",
            Style = (Style)Application.Current.Resources["ChipButton"],
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(12, 6, 12, 6),
        };
        System.Windows.Automation.AutomationProperties.SetName(browse, "选择保存位置");
        browse.Click += (_, _) => Browse();
        System.Windows.Controls.Grid.SetColumn(browse, 1);
        browseRow.Children.Add(browse);
        stack.Children.Add(browseRow);

        var hashLabel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        hashLabel.Children.Add(new TextBlock
        {
            Text = "SHA-256 校验值（可选）",
            FontSize = 11,
            Foreground = Secondary,
            Margin = new Thickness(0, 0, 0, 6),
        });
        hashLabel.Children.Add(new TextBlock
        {
            Text = "发布者提供了校验值就填上，下载完成会自动核对，防止文件被篡改或下坏。",
            FontSize = 10.5,
            Foreground = Secondary,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });
        stack.Children.Add(hashLabel);
        _hash = Input("校验值");
        _hash.Margin = new Thickness(0, 4, 0, 8);
        stack.Children.Add(_hash);

        var start = SquareButton("开始下载", primary: true);
        System.Windows.Automation.AutomationProperties.SetName(start, "开始下载");
        start.Click += (_, _) => Start();
        var actionRow = new StackPanel { Orientation = Orientation.Horizontal };
        actionRow.Children.Add(start);

        // 只有“这里确实下不了”(网页链接 / HTTP 拒绝 / Cloudflare 验证)时才显示
        var openBrowser = SquareButton("用浏览器打开", primary: false);
        openBrowser.Margin = new Thickness(10, 0, 0, 0);
        openBrowser.Visibility = Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetName(openBrowser, "用浏览器打开");
        openBrowser.Click += (_, _) =>
        {
            var url = _url.Text.Trim();
            if (url.Length == 0)
            {
                Hint("先粘贴链接", danger: true);
                return;
            }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Hint("打开浏览器失败:" + ex.Message, danger: true);
            }
        };
        _openBrowser = openBrowser;
        actionRow.Children.Add(openBrowser);
        stack.Children.Add(actionRow);

        _hint = new TextBlock
        {
            FontSize = 11,
            Foreground = Secondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        stack.Children.Add(_hint);

        // 右键粘贴支持:URL 框为空时,粘贴板里有 http 链接就自动填
        _url.PreviewMouseRightButtonUp += (_, _) =>
        {
            if (_url.Text.Length == 0 && Clipboard.ContainsText())
            {
                var text = Clipboard.GetText().Trim();
                if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    _url.Text = text;
                    UpdateFileName();
                }
            }
        };

        // 标题 + 卡片一次组装完成:先把 Border 加进 host,再设置 Content;
        // 中途给 Content 赋值会让 Border 被两个父级持有("已经是另一个元素的逻辑子元素")
        var titleText = new TextBlock
        {
            Text = "高速下载",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
        };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var host = new StackPanel();
        host.Children.Add(titleText);
        host.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["OptRow"],
            Child = stack,
        });
        Content = host;
    }

    private static Brush Secondary => (Brush)Application.Current.Resources["TextSecondaryBrush"];

    private static TextBlock FieldLabel(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = Secondary,
        // 下间距 6:贴着输入框会像重叠(截图里"下载地址"和框顶几乎相接)
        Margin = new Thickness(0, 10, 0, 6),
    };

    private static TextBox Input(string name)
    {
        var box = new TextBox
        {
            Style = (Style)Application.Current.Resources["DarkInput"],
        };
        System.Windows.Automation.AutomationProperties.SetName(box, name);
        return box;
    }

    private static string DefaultDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private void Browse()
    {
        using var dialog = new System.Windows.Forms.SaveFileDialog
        {
            FileName = SuggestFileName(),
            InitialDirectory = Path.GetDirectoryName(_path.Text) is { Length: > 0 } dir && Directory.Exists(dir)
                ? dir
                : DefaultDirectory(),
            Filter = "所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(new DialogKit.OwnerHandle(
                DialogKit.HandleOf(Window.GetWindow(this)!))) != System.Windows.Forms.DialogResult.OK) return;
        _path.Text = dialog.FileName;
    }

    private string SuggestFileName()
    {
        var fromUrl = FileNameFromUrl(_url.Text);
        return fromUrl.Length == 0 ? "download.bin" : fromUrl;
    }

    /// <summary>从 URL 尾段推断文件名(忽略 query/fragment);推不出来返回空。</summary>
    internal static string FileNameFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        var text = url!.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri is null)
            return "";
        var last = uri.AbsoluteUri.Split('?', '#')[0].TrimEnd('/').Split('/').LastOrDefault() ?? "";
        if (last.Length == 0 || last.Length > 200) return "";
        // URL 解码(常见 %20 等);失败就用原始尾段
        try { last = Uri.UnescapeDataString(last); } catch (UriFormatException) { }
        if (last.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "";
        return last;
    }

    private void UpdateFileName()
    {
        // URL 变化时,若保存路径还是上一个 URL 推断的名字,就跟着换;
        // 用户手动选过的路径(文件名与 URL 无关)不覆盖。
        var suggested = FileNameFromUrl(_url.Text);
        if (string.IsNullOrEmpty(suggested)) return;
        var current = _path.Text.Trim();
        if (current.Length == 0)
        {
            _path.Text = Path.Combine(DefaultDirectory(), suggested);
            return;
        }
        var existingName = Path.GetFileName(current);
        var urlName = FileNameFromUrl(PreviousUrl);
        if (urlName is { Length: > 0 } && existingName.Equals(urlName, StringComparison.OrdinalIgnoreCase))
            _path.Text = Path.Combine(Path.GetDirectoryName(current) ?? DefaultDirectory(), suggested);
        PreviousUrl = _url.Text;
    }

    private string PreviousUrl { get; set; } = "";

    private void Start()
    {
        var url = _url.Text.Trim();
        var dest = _path.Text.Trim();
        var hash = _hash.Text.Trim();
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        if (_openBrowser != null) _openBrowser.Visibility = Visibility.Collapsed;

        if (url.Length == 0 || dest.Length == 0)
        {
            Hint("请填写下载地址和保存位置", danger: true);
            return;
        }

        // 直链体检:HEAD 预检拿大小/类型/最终 URL,HTML 页面提前警告(百宝箱教程里说的"下到 1KB 网页"就是这种)
        startProbe(url, dest, hash);

        async void startProbe(string probeUrl, string probeDest, string probeHash)
        {
            string finalUrl = probeUrl;
            long? size = null;
            string? contentType = null;
            try
            {
                using var request = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Head, probeUrl);
                using var response = await AppServices.Http.SendAsync(request,
                    System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? probeUrl;
                size = response.Content.Headers.ContentLength;
                contentType = response.Content.Headers.ContentType?.MediaType;
            }
            catch (Exception)
            {
                // HEAD 被禁用的服务器不少:只当“没拿到元数据”,不能作为拒绝下载的依据
            }

            // HEAD 不可信(Cloudflare 挑战/防盗链常对 HEAD 返回 403+HTML,浏览器 GET 却是文件)。
            // 再用 GET + Range 只读响应头确认一次:是真文件就继续,是网页/挑战页才拦。
            try
            {
                using var probe = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Get, finalUrl);
                probe.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 4095);
                using var response = await AppServices.Http.SendAsync(probe,
                    System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                var code = (int)response.StatusCode;
                var mitigated = response.Headers.TryGetValues("cf-mitigated", out var cf) &&
                                cf.Any(v => v.IndexOf("challenge", StringComparison.OrdinalIgnoreCase) >= 0);
                if (!response.IsSuccessStatusCode)
                {
                    // 405/416/501 只说明“服务器不喜欢 Range/GET 探测”，不代表文件不能下:
                    // 继续按 HEAD 的元数据尝试,别把能下的直链误杀。
                    var probeUnsupported = code is 405 or 416 or 501;
                    if (!probeUnsupported)
                    {
                        if (mitigated)
                        {
                            Hint("这个站点启用了 Cloudflare 人机验证，启动器无法绕过。" +
                                 "请点「用浏览器打开」，在浏览器里完成下载。", danger: true);
                            if (_openBrowser != null) _openBrowser.Visibility = Visibility.Visible;
                            return;
                        }
                        var httpHint = $"服务器返回 HTTP {code}（{response.ReasonPhrase}），无法直接下载。" +
                                       "可以点「用浏览器打开」试试。";
                        if (code == 403 && finalUrl.IndexOf("speed.cloudflare.com", StringComparison.OrdinalIgnoreCase) >= 0)
                            httpHint = "Cloudflare 测速源单次上限 100,000,000 字节（100MB）；" +
                                       "把链接里的 bytes 改成 99999999 再试。";
                        Hint(httpHint, danger: true);
                        if (_openBrowser != null) _openBrowser.Visibility = Visibility.Visible;
                        return;
                    }
                    // 探测不支持:清掉 HEAD 的 contentType,避免 challenge 页的 text/html 误判
                    contentType = null;
                }
                else
                {
                    finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? finalUrl;
                    // 206 的 Content-Length 是这次 Range 的长度(4096),不是文件总大小;
                    // 总大小在 Content-Range 里。只有 200 才能用 Content-Length。
                    if (code == 206)
                        size = response.Content.Headers.ContentRange?.Length ?? size;
                    else
                        size = response.Content.Headers.ContentLength ?? size;
                    contentType = response.Content.Headers.ContentType?.MediaType ?? contentType;
                }
            }
            catch (Exception ex)
            {
                // DNS 被拦/域名不存在:下载器只会报“探测文件大小失败”,这里直接说人话。
                if (IsDnsFailure(ex))
                {
                    var host = "";
                    try { host = new Uri(finalUrl).Host; } catch (Exception) { }
                    Hint($"无法解析域名 {host}（当前网络 DNS 可能不可用）。可以点「用浏览器打开」试试。", danger: true);
                    if (_openBrowser != null) _openBrowser.Visibility = Visibility.Visible;
                    return;
                }
                // 其他网络抖动不拦,交给真正的下载器重试
            }

            // FileNameFromUrl 失败时返回空串(不是 null),?? 兜不住,必须显式判空
            var fileName = FileNameFromUrl(finalUrl);
            if (fileName.Length == 0) fileName = "download.bin";
            var finalDest = SameDirectory(probeDest) ? Path.Combine(probeDest, fileName) : probeDest;
            if (Path.GetFileName(finalDest).Length == 0)
            {
                finalDest = Path.Combine(finalDest, fileName);
            }

            if (contentType is "text/html" or "application/xhtml+xml")
            {
                Hint("这个链接返回的是网页而不是文件——确认它是不是资源的真实直链（302 跳转链接常会下到网页）", danger: true);
                if (_openBrowser != null) _openBrowser.Visibility = Visibility.Visible;
                return;
            }

            try
            {
                UserDownloadGuard.Validate(finalUrl, finalDest);
            }
            catch (Exception ex)
            {
                Hint(ex.Message, danger: true);
                return;
            }

            if (size is > 2L * 1024 * 1024 * 1024)
            {
                Hint("文件超过 2GB，请确认磁盘空间足够", danger: true);
                return;
            }

            try
            {
                var meta = new JObject { ["finalUrl"] = finalUrl };
                var task = AppServices.Queue!.Enqueue(new DownloadTaskRecord
                {
                    Name = fileName,
                    Kind = "user-file",
                    SourceUrl = finalUrl,
                    DestPath = finalDest,
                    ExpectedHash = probeHash.Length == 0 ? null : probeHash,
                    Algo = HashAlgo.Sha256,
                    Meta = meta.ToString(),
                });
                DownloadFocus.Register(task.TaskId, "more");
                Hint("已加入下载队列：" + fileName + "（可在下载页「任务」查看进度）");
            }
            catch (Exception ex)
            {
                Hint("加入队列失败:" + ex.Message, danger: true);
            }
        }
    }

    private static bool SameDirectory(string dest)
        => dest.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
           || dest.EndsWith("/", StringComparison.Ordinal)
           || Directory.Exists(dest);

    /// <summary>方形、6px 小圆角按钮(高速下载操作区)。</summary>
    private static Button SquareButton(string text, bool primary)
    {
        var button = new Button
        {
            Content = text,
            Height = 38,
            Padding = new Thickness(18, 0, 18, 0),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = System.Windows.Input.Cursors.Hand,
            BorderThickness = new Thickness(1),
            Background = primary
                ? (Brush)Application.Current.Resources["AccentChipBrush"]
                : new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255)),
            BorderBrush = primary
                ? new SolidColorBrush(Color.FromArgb(0x66, 255, 255, 255))
                : new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)),
        };
        if (primary) button.Foreground = Brushes.White;
        else button.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");

        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        // 自定义模板不会自动使用 Button.Padding:不绑的话中文会被挤在文字宽度里
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty,
            primary
                ? new SolidColorBrush(Color.FromRgb(0xC4, 0x6B, 0xFF))
                : new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255)), "Bd"));
        var pressed = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty,
            primary
                ? new SolidColorBrush(Color.FromRgb(0x8A, 0x2F, 0xD0))
                : new SolidColorBrush(Color.FromArgb(0x44, 255, 255, 255)), "Bd"));
        template.Triggers.Add(hover);
        template.Triggers.Add(pressed);
        button.Template = template;
        return button;
    }

    private static bool IsDnsFailure(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is System.Net.Sockets.SocketException se &&
                (se.SocketErrorCode == System.Net.Sockets.SocketError.HostNotFound ||
                 se.SocketErrorCode == System.Net.Sockets.SocketError.NoData))
                return true;
            if (e is System.Net.WebException we && we.Status == System.Net.WebExceptionStatus.NameResolutionFailure)
                return true;
            var msg = e.Message ?? "";
            if (msg.IndexOf("No such host", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("请求的名称", StringComparison.Ordinal) >= 0 ||
                msg.IndexOf("名称有效", StringComparison.Ordinal) >= 0 ||
                msg.IndexOf("resolve", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    private void Hint(string text, bool danger = false)
    {
        _hint.Text = text;
        _hint.SetResourceReference(TextBlock.ForegroundProperty,
            danger ? "DangerBrush" : "TextSecondaryBrush");
    }

    private void RefreshHintSafe(string text) => Dispatcher.BeginInvoke(new Action(() => Hint(text)));
}
