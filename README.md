# Intent Chaos 意图乱流

[Slay the Spire 2](https://store.steampowered.com/app/2868840/) 的玩法 mod：怪物受到**直接攻击并真实掉血**后，意图立刻从它状态机的全部招式池里重掷成另一个招。

纯代码 mod（无 pck、无前置依赖、不碰美术资源），运行时自适应游戏版本。当前 **v0.3.2**，已在游戏 **0.107.1 – 0.111.0**（稳定版与测试版）验证。

> **English TL;DR** — Monsters reroll their intent to a random *different* move the moment they lose real HP to your direct attacks. Passive damage (relics / powers / end-of-turn) does not trigger it. Scripted moves — death blow & self-destruct, blank hidden intents, must-perform revive states, and one-shot boss openers that hand you cards — are pinned **both ways**, so the game's own scripts are never broken: a self-destruct boss still dies, a split limb never sits on a do-nothing intent, and the Act 2 sandworm keeps its devour gimmick. Pure-code mod, zero dependencies, self-adapts at runtime and puts itself to sleep instead of crashing when the game's API moves.

## 它做了什么

- 被**直接攻击**造成真实掉血 → 该怪物意图立刻重掷，且保证换成**不同**的招式
- 遗物、药水、能力牌、回合结束等**被动伤害不触发**；被格挡完全挡下（不掉血）或被打死也不触发
- 所有 boss 从第一招起就参与随机；例外两条：**女王（第三层）开场三招固定**，以及**沙虫（第二层）的首回合那招固定**（它整场只会出现一次，换走＝吞噬倒计时和 6 张慌乱逃离一起消失）
- boss 招式池额外含一招 **【塞诅咒牌】**：被随机到时**永久**往你的卡组加一张诅咒牌——战斗结束也不会移除，同时立刻塞一张进本战抽牌堆
- 该招式头顶显示紫色召唤物图样，鼠标悬停可查看说明
- 随机使用联机同步 RNG，多人游戏两端一致
- 只有 1 个招式的怪（建筑师、大靶子、OneHp/TenHp、Osty…）池子恒为空，本来就不重掷；普通怪的一次性开场招**故意**允许换走（潮湿邪教徒的祷文那类），只有上面那条 boss 例外被固定

## 核心设计：为什么「排除」必须双向对称

同一个函数要同时决定 **① 不进候选池**（别的怪抽不到它）和 **② 不被换走**（它自己不会被重掷成别的招）。只写一个方向就是**单向门**——本项目四次同源事故全是它：

| 事故 | 症状 | 通用判据（不写怪名/招式名） |
|---|---|---|
| 瀑布巨兽 `EXPLODE_MOVE`（v0.3.0） | 亡语被换走 → 永久 999999999 血、战斗无法结束 | 意图含 `DeathBlowIntent` |
| 千足虫 `DEAD_MOVE`（v0.3.1） | 活肢体顶着一个"什么都不做、再打也不变"的空意图 | 零意图，或意图全是 `HiddenIntent` |
| 千足虫 `REATTACH_MOVE` / 实验体 `RESPAWN_MOVE`（v0.3.1） | 意图钉死 + 打断本体重生脚本 | `MustPerformOnceBeforeTransitioning` |
| 沙虫 `LIQUIFY_GROUND_MOVE`（v0.3.2） | 首回合那招被换走 → 吞噬倒计时与 6 张慌乱逃离整场消失；被随机发到中盘 → 同一玩家挂两条独立倒计时 | boss 房 + 状态机入度 0 的链头 + 意图含 `StatusIntent` |

实现集中在 `IntentChaos/src/IntentRerollPatch.cs`：三类结构性招走 `StructuralReason()`，沙虫那类一次性开场招走 `IsOneShotBossGrant()`（内部用 `IsStrandedInChain()` 算状态机入度，后继读取在 `Compat.GetSuccessorIds`——读不出就保守放行）。候选池与"当前招能不能被换走"共用同一份判据；判据全部来自状态属性（意图类型、`MustPerformOnceBeforeTransitioning`、`MonsterState.CanTransitionAway`、`Creature.HpDisplay`），**不硬编码怪名与招式名**，所以游戏加新怪也不会漏。

**尚未逐条排查**：`SummonIntent` 类召唤招（Fabricator / Fogmog / Queen / TwoTailedRat 等）目前仍可被换走——这是"未查"，不是"已排除"。

## 目录

```
IntentChaos/                 mod 本体
  src/                       C# 源码：ModEntry / IntentRerollPatch / Compat / ModConfig / CurseIntent
  IntentChaos.json           清单（版本号以它为准）
  local.props.example        本机路径模板 → 复制成 local.props（不入库）
  workshop/                  工坊上传包：同步脚本 + IntentChaos.vdf + preview.jpg
_test_compat/                离线适配预检：拿当前安装的本体真跑一遍 Compat.Resolve()
_test_structural/            「结构性招」判定回归测试
_scan/                       影响面静态扫描：统计每只怪的池子变化
_shared/                     两个测试共用的路径解析
```

## 构建与部署

前置：**.NET SDK 9.0 或更高**、游戏本体。

```powershell
# 1. 配置本机路径（一次即可）：把 <GameDir> 改成游戏安装根目录
#    （含 data_sts2_windows_x86_64 的那一层）
Copy-Item IntentChaos\local.props.example IntentChaos\local.props

# 2. 构建（先把游戏关掉，否则 dll 被锁）
cd IntentChaos\src
dotnet build
```

`local.props` 的 `AutoDeploy=true` 会在构建后自动把 json + dll 复制到 `<GameDir>\mods\IntentChaos\`，进游戏即生效。启动日志（`%APPDATA%\SlayTheSpire2\logs\godot.log`）里搜 `IntentChaos` 可确认挂载成功。

路径解析优先级：环境变量 `STS2_GAMEDIR` > `IntentChaos\local.props`，两个离线测试也走同一套（`_shared/LocalPaths.cs`），所以仓库里没有任何写死的本机路径。

## 两个离线检查

```powershell
# ① 本体 API 适配：[OK] 才算与当前游戏版本兼容
dotnet run --project _test_compat

# ② 结构性判定回归：退出码 0 = 该固定的固定、该随机的随机
dotnet run --project _test_structural
```

改过"哪些招不许动"的判据时，还可以用 `_scan` 扫影响面（需要自备本体反编译源码，本仓库不附带）：

```powershell
# 招式分类与池子大小变化（亡语/空白/锁定三类判据）
pwsh -File _scan\scan_moves.ps1 -DecompDir <你的本体反编译树根目录>

# 状态机图扫描：哪些怪有"本体一辈子只走到一次"的开场招（换走就永久消失）
pwsh -File _scan\scan_openings.ps1
```

## 许可

[MIT](LICENSE)。游戏本体与其资源版权归 Mega Crit 所有，本仓库不包含它们。
