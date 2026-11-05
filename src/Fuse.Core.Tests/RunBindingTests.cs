using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Data;
using System.Windows.Documents;
using Fuse.Core.Versioning;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>
/// WPF Run.Text 绑定默认是 TwoWay，绑到只读属性（如 InstalledVersionInfo.JavaDisplay）
/// 会在模板实例化时抛 XamlParseException（v0.2.1 启动首页崩溃根因）。
/// 此测试锁定修复：绑定必须显式 Mode=OneWay。
/// </summary>
public class RunBindingTests
{
    [Theory]
    [InlineData("TwoWay", false)]
    [InlineData("OneWay", true)]
    public void RunText_ToReadOnlyProperty(string mode, bool shouldSucceed)
    {
        Exception? error = null;
        string? value = null;

        var thread = new Thread(() =>
        {
            try
            {
                var item = new InstalledVersionInfo { Id = "1.20", JavaMajor = 17, ModsCount = 3 };
                var run = new Run();
                var binding = new Binding("JavaDisplay") { Source = item };
                if (mode == "OneWay") binding.Mode = BindingMode.OneWay;
                run.SetBinding(Run.TextProperty, binding);

                var textBlock = new TextBlock();
                textBlock.Inlines.Add(run);

                // 挂到已加载的可视树强制绑定激活（与 DataTemplate 实例化同路径）
                var window = new Window
                {
                    Content = textBlock,
                    Width = 10,
                    Height = 10,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Left = -200,
                    Top = -200,
                };
                window.Show();
                value = run.Text;
                window.Close();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(10000);

        if (shouldSucceed)
        {
            Assert.Null(error);
            Assert.Equal("Java 17", value);
        }
        else
        {
            Assert.NotNull(error);
            Assert.IsType<InvalidOperationException>(Unwrap(error!));
        }
    }

    private static Exception Unwrap(Exception ex) =>
        ex is XamlParseException p && p.InnerException != null ? p.InnerException : ex;
}
