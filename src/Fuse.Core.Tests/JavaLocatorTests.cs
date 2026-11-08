using System;
using System.IO;
using Fuse.Core.Java;
using Xunit;

namespace Fuse.Core.Tests;

public class JavaLocatorTests
{
    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcdl-java-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static JavaRuntime Rt(int major) => new()
    {
        Home = "C:\\Java\\" + major,
        VersionString = major.ToString(),
        MajorVersion = major,
        Source = "test",
    };

    [Fact]
    public void Pick_ExactMajorFirst()
    {
        var all = new System.Collections.Generic.List<JavaRuntime> { Rt(8), Rt(17), Rt(21) };
        Assert.Equal(8, JavaLocator.Pick(all, 8)!.MajorVersion);
        Assert.Equal(17, JavaLocator.Pick(all, 17)!.MajorVersion);
    }

    [Fact]
    public void Pick_DoesNotUseNewerJavaForLegacy()
    {
        // 1.12.2 的 LaunchWrapper 在 Java 21 上 ClassCastException,必须去下 Java 8
        var onlyNew = new System.Collections.Generic.List<JavaRuntime> { Rt(17), Rt(21) };
        Assert.Null(JavaLocator.Pick(onlyNew, 8));
        Assert.Null(JavaLocator.Pick(onlyNew, 16));
    }

    [Fact]
    public void Pick_AllowsNewerJavaForModernVersions()
    {
        var onlyNew = new System.Collections.Generic.List<JavaRuntime> { Rt(17), Rt(21) };
        Assert.Equal(17, JavaLocator.Pick(onlyNew, 17)!.MajorVersion);
        Assert.Equal(21, JavaLocator.Pick(onlyNew, 18)!.MajorVersion);

        var newer = new System.Collections.Generic.List<JavaRuntime> { Rt(19), Rt(21) };
        Assert.Equal(19, JavaLocator.Pick(newer, 17)!.MajorVersion);
    }

    [Fact]
    public void HasServerJvm_RequiresServerJvmDll()
    {
        var root = NewTempRoot();
        try
        {
            var bin = Path.Combine(root, "bin");
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, "java.exe"), "");
            Assert.False(JavaLocator.HasServerJvm(root));

            Directory.CreateDirectory(Path.Combine(bin, "server"));
            File.WriteAllText(Path.Combine(bin, "server", "jvm.dll"), "");
            Assert.True(JavaLocator.HasServerJvm(root));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void HasServerJvm_AcceptsJava8JreLayout()
    {
        var root = NewTempRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "bin"));
            File.WriteAllText(Path.Combine(root, "bin", "java.exe"), "");
            var server = Path.Combine(root, "jre", "bin", "server");
            Directory.CreateDirectory(server);
            File.WriteAllText(Path.Combine(server, "jvm.dll"), "");
            Assert.True(JavaLocator.HasServerJvm(root));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
