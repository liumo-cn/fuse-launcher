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
