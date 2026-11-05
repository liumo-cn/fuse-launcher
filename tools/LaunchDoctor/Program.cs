using System;
using System.Threading;
using System.Threading.Tasks;
using Fuse.Core.Accounts;
using Fuse.Core.Download;
using Fuse.Core.Java;
using Fuse.Core.Launch;
using Fuse.Core.Pipeline;
using Fuse.Core.Sources;
using Fuse.Core.Versioning;

// 启动诊断：复现外壳的完整启动管线并打印每步结果。
// 用法: dotnet run --project tools/LaunchDoctor -- <版本id> [游戏根目录]
var flags = args.Where(a => a.StartsWith("--")).ToHashSet();
var positional = args.Where(a => !a.StartsWith("--")).ToArray();
var versionId = positional.Length > 0 ? positional[0] : "1.20";
var installRoot = positional.Length > 1 ? positional[1]
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MCDownloader");

var versionsRoot = Path.Combine(installRoot, "versions");
var librariesRoot = Path.Combine(installRoot, "libraries");
var assetsRoot = Path.Combine(installRoot, "assets");
var gameDirectory = Path.Combine(installRoot, "instances", versionId);

Console.WriteLine($"[doctor] 版本={versionId} 根目录={installRoot}");

var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("MCDownloader/doctor");

var pipeline = new LaunchPipeline(http)
{
    AuthProvider = new OfflineAuthProvider("Doctor"),
};

var freshJava = flags.Contains("--fresh-java"); // 无视本机 Java,强制走官方运行时下载
var options = new LaunchOptions
{
    VersionsRoot = versionsRoot,
    VersionId = versionId,
    GameDirectory = gameDirectory,
    LibrariesDirectory = librariesRoot,
    AssetsRoot = assetsRoot,
    WindowTimeoutSeconds = 90,
    JavaRuntimesRoot = Path.Combine(installRoot, "runtimes"),
    IgnoreInstalledJava = freshJava,
};
Console.WriteLine(freshJava ? "[doctor] --fresh-java: 将强制下载官方 Java 运行时" : "[doctor] 使用本机可用 Java");

var sw = System.Diagnostics.Stopwatch.StartNew();
try
{
    var result = await pipeline.LaunchAsync(options, new Progress<LoaderProgress>(p =>
        Console.WriteLine($"[{sw.Elapsed.TotalSeconds:0.0}s] {p.Stage} {p.Progress:P0}")));
    Console.WriteLine($"[doctor] 启动成功！PID={result.GameProcess.Id} Java={result.Java.VersionString}");
    Console.WriteLine("[doctor] 命令行前 500 字：");
    Console.WriteLine(WindowsArgQuoter.Join(result.JvmArgs.Concat(result.GameArgs))[..500]);
}
catch (Exception ex)
{
    Console.WriteLine($"[doctor] 启动失败：{ex.GetType().Name}: {ex.Message}");
    if (ex.InnerException != null) Console.WriteLine("  内层: " + ex.InnerException.Message);
    Console.WriteLine(ex.StackTrace);
    Environment.ExitCode = 1;
}
