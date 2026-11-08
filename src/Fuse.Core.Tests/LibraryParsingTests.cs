using System.IO;
using System.Linq;
using Fuse.Core.Installer;
using Fuse.Core.Versioning;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Fuse.Core.Tests;

public class LibraryParsingTests
{
    [Fact]
    public void NativesOnlyLibrary_DoesNotSynthesizeMainJar()
    {
        // 1.12.2 的 jinput-platform 只有 classifiers,没有主 jar。
        // 以前会按坐标合成 jinput-platform-2.0.5.jar(默认 Fabric maven),
        // 这个文件根本不存在 → 体检查出"1 个库文件"永远补不完。
        var json = JObject.Parse(
            "{\"name\":\"net.java.jinput:jinput-platform:2.0.5\"," +
            "\"downloads\":{\"classifiers\":{\"natives-windows\":{" +
            "\"path\":\"net/java/jinput/jinput-platform/2.0.5/jinput-platform-2.0.5-natives-windows.jar\"," +
            "\"url\":\"https://libraries.minecraft.net/net/java/jinput/jinput-platform/2.0.5/jinput-platform-2.0.5-natives-windows.jar\"" +
            "}}}}");

        var lib = McLibrary.FromJToken(json);
        Assert.Null(lib.Artifact);
        Assert.NotNull(lib.Classifiers);
    }

    [Fact]
    public void MavenOnlyLibrary_StillSynthesizesArtifact()
    {
        var json = JObject.Parse(
            "{\"name\":\"net.fabricmc:fabric-loader:0.16.0\",\"url\":\"https://maven.fabricmc.net/\"}");
        var lib = McLibrary.FromJToken(json);

        Assert.NotNull(lib.Artifact);
        Assert.Equal("net/fabricmc/fabric-loader/0.16.0/fabric-loader-0.16.0.jar", lib.Artifact!.Path);
        Assert.Equal(
            "https://maven.fabricmc.net/net/fabricmc/fabric-loader/0.16.0/fabric-loader-0.16.0.jar",
            lib.Artifact.Url);
    }

    [Fact]
    public void LibraryFiles_NativesOnly_OnlyPlansNativeJar()
    {
        var json = JObject.Parse(
            "{\"id\":\"1.12.2\",\"libraries\":[{" +
            "\"name\":\"net.java.jinput:jinput-platform:2.0.5\"," +
            "\"downloads\":{\"classifiers\":{\"natives-windows\":{" +
            "\"path\":\"net/java/jinput/jinput-platform/2.0.5/jinput-platform-2.0.5-natives-windows.jar\"," +
            "\"url\":\"https://libraries.minecraft.net/net/java/jinput/jinput-platform/2.0.5/jinput-platform-2.0.5-natives-windows.jar\"" +
            "}}}}]}");
        var version = McVersion.FromJObject(json);

        var files = VanillaInstallPlanner.LibraryFiles(
            version, Path.Combine(Path.GetTempPath(), "fuse-libs"), RuleEnvironment.WindowsX64);

        var one = Assert.Single(files);
        Assert.EndsWith("jinput-platform-2.0.5-natives-windows.jar", one.DestPath);
        Assert.DoesNotContain(files, f => f.DestPath.EndsWith("jinput-platform-2.0.5.jar"));
    }
}
