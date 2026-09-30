// 本机路径统一解析：仓库里不出现任何绝对路径，谁 clone 下来都不用改代码。
// 解析顺序：环境变量 STS2_GAMEDIR → <仓库根>\IntentChaos\local.props 的 <GameDir>。
// local.props 是本机配置（.gitignore 里已排除），模板见 IntentChaos\local.props.example。
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

internal static class LocalPaths
{
    /// <summary>仓库根：从程序集所在目录向上找到含 IntentChaos\IntentChaos.json 的那一层。</summary>
    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>游戏安装根目录（例：D:\SteamLibrary\steamapps\common\Slay the Spire 2）。</summary>
    public static string GameDir { get; } = ResolveGameDir();

    /// <summary>游戏所有 .NET dll 所在子目录。</summary>
    public static string GameLibDir => Path.Combine(GameDir, "data_sts2_windows_x86_64");

    /// <summary>本仓库 IntentChaos 的构建产物。</summary>
    public static string ModDll => Path.Combine(RepoRoot, "IntentChaos", "src", "bin", "Debug", "IntentChaos.dll");

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "IntentChaos", "IntentChaos.json"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "找不到仓库根（应含 IntentChaos\\IntentChaos.json）：请在仓库内运行本测试（bin\\Debug 往上找）");
    }

    private static string ResolveGameDir()
    {
        var env = Environment.GetEnvironmentVariable("STS2_GAMEDIR");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        var props = Path.Combine(RepoRoot, "IntentChaos", "local.props");
        if (!File.Exists(props))
        {
            throw new InvalidOperationException(
                $"缺少本机配置：{props}\n" +
                "请把 IntentChaos\\local.props.example 复制为 IntentChaos\\local.props，并把 <GameDir> 填成你的游戏安装目录；" +
                "或者直接设环境变量 STS2_GAMEDIR。");
        }

        var m = Regex.Match(File.ReadAllText(props, Encoding.UTF8), @"<GameDir>\s*([^<]+?)\s*</GameDir>");
        if (!m.Success) throw new InvalidOperationException($"{props} 里没有 <GameDir>，请对照 local.props.example 补上");
        return m.Groups[1].Value;
    }
}
