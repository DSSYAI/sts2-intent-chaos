// IntentChaos 离线适配预检：把 mod dll 加载进控制台程序，对"当前安装的游戏本体"
// 真实执行 Compat.Resolve() + Harmony 挂载，验证全部成员名/参数形状仍匹配。
// 游戏更新后运行 `dotnet run` 一键复查；输出 [OK] 即适配，[FAIL] 会给出缺失成员。
using System;
using System.IO;
using System.Reflection;

// 路径不写死：本机配置统一来自 IntentChaos\local.props（见 _shared\LocalPaths.cs）
var gameLib = LocalPaths.GameLibDir;
var modDll = LocalPaths.ModDll;

AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    var p = Path.Combine(gameLib, new AssemblyName(e.Name).Name + ".dll");
    return File.Exists(p) ? Assembly.LoadFrom(p) : null;
};

var mod = Assembly.LoadFrom(modDll);
var compat = mod.GetType("IntentChaos.Compat") ?? throw new Exception("IntentChaos.Compat 类型不存在");

var err = (string?)compat.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static)!
    .Invoke(null, null);
if (err != null)
{
    Console.WriteLine($"[FAIL] 本体 API 解析失败: {err}");
    return 1;
}
Console.WriteLine("[OK] 全部本体 API 解析成功（与当前安装的游戏版本兼容）");
foreach (var f in new[] { "IdxTarget", "IdxResult", "IdxProps", "IdxDealer", "IdxCardSource" })
{
    Console.WriteLine($"  Hook.{f.Replace("Idx", "")} 参数索引 = {compat.GetField(f)!.GetValue(null)}");
}
var curseType = mod.GetType("IntentChaos.Compat+Curse")!;
Console.WriteLine($"  boss诅咒招API组可用 = {curseType.GetField("Available")!.GetValue(null)}" +
    (curseType.GetField("Available")!.GetValue(null) is false
        ? $"（原因：{curseType.GetField("FailReason")!.GetValue(null)}）" : ""));

var target = (MethodInfo)compat.GetField("AfterDamageReceived")!.GetValue(null)!;
var postfix = new HarmonyLib.HarmonyMethod(
    mod.GetType("IntentChaos.IntentRerollPatch")!
        .GetMethod("Postfix", BindingFlags.Public | BindingFlags.Static)!);
try
{
    new HarmonyLib.Harmony("test.intentchaos").Patch(target, postfix: postfix);
    Console.WriteLine($"[OK] Harmony 补丁挂载成功: {target.DeclaringType}.{target.Name}({target.GetParameters().Length} 参)");
}
catch (PlatformNotSupportedException)
{
    // 游戏自带 9.0.7 运行时支持 Harmony；本机若只有 .NET 10 运行时则跳过此步（与版本适配无关）
    Console.WriteLine($"[SKIP] 测试宿主运行时不受游戏版 Harmony 支持，挂载验证留到游戏内启动日志：{target.DeclaringType}.{target.Name}");
}
return 0;
