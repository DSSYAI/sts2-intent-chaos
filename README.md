# Intent Chaos 意图乱流

[Slay the Spire 2](https://store.steampowered.com/app/2868840/) 的玩法 mod：怪物受到**直接攻击并真实掉血**后，意图立刻从它状态机的全部招式池里重掷成另一个招。

纯代码 mod（无 pck、无前置依赖、不碰美术资源），运行时自适应游戏版本。当前 **v0.3.1**，已在游戏 **0.107.1 – 0.111.0**（稳定版与测试版）验证。

> **English TL;DR** — Monsters reroll their intent to a random *different* move the moment they lose real HP to your direct attacks. Passive damage (relics / powers / end-of-turn) does not trigger it. Scripted moves — death blow & self-destruct, blank hidden intents, must-perform revive states — are pinned **both ways**, so the game's own scripts are never broken: a self-destruct boss still dies, a split limb never sits on a do-nothing intent. Pure-code mod, zero dependencies, self-adapts at runtime and puts itself to sleep instead of crashing when the game's API moves.

## 玩家能看到的效果

- 被**直接攻击**造成真实掉血 → 该怪物意图立刻重掷，且保证换成**不同**的招式
- 遗物、药水、能力牌、回合结束等**被动伤害不触发**；被格挡完全挡下（不掉血）或被打死也不触发
- 所有 boss 从第一招起就参与随机；**女王（第三层）开场三招固定**，之后才入池
- boss 招式池额外含一招 **【塞诅咒牌】**：被随机到时永久往你的卡组加一张诅咒牌，并立刻塞一张进本战抽牌堆
- 随机使用联机同步 RNG，多人游戏两端一致

## 核心设计：为什么「排除」必须双向对称

同一个函数要同时决定 **① 不进候选池**（别的怪抽不到它）和 **② 不被换走**（它自己不会被重掷成别的招）。只写一个方向就是**单向门**——本项目三次同源事故全是它：

| 事故 | 症状 | 通用判据（不写怪名/招式名） |
|---|---|---|
| 瀑布巨兽 `EXPLODE_MOVE`（v0.3.0） | 亡语被换走 → 永久 999999999 血、战斗无法结束 | 意图含 `DeathBlowIntent` |
| 千足虫 `DEAD_MOVE`（v0.3.1） | 活肢体顶着一个"什么都不做、再打也不变"的空意图 | 零意图，或意图全是 `HiddenIntent` |
| 千足虫 `REATTACH_MOVE` / 实验体 `RESPAWN_MOVE`（v0.3.1） | 意图钉死 + 打断本体重生脚本 | `MustPerformOnceBeforeTransitioning` |

实现集中在 `IntentChaos\src\IntentRerollPatch.cs` 的 `StructuralReason()`，候选池与"当前招能不能被换走"共用同一份判据。判据全部来自状态属性（意图类型、`MustPerformOnceBeforeTransitioning`、`MonsterState.CanTransitionAway`、`Creature.HpDisplay`），**不硬编码怪名与招式名**。

其余已定的边界：

- 只有 1 个招式的怪（Architect、大靶子、OneHp/TenHp、Osty…）池子恒为空，本来就不重掷
- 一次性开场招**故意**允许换走
- `SummonIntent` 类召唤招目前仍可被换走（**未逐条排查**，不是"已排除"）

## 目录

```
IntentChaos/                 mod 本体（活跃工程）
  src/                       C# 源码：ModEntry / IntentRerollPatch / Compat / ModConfig / CurseIntent
  IntentChaos.json           清单（版本号以它为准）
  local.props.example        本机路径模板 → 复制成 local.props（不入库）
  workshop/                  工坊上传包：同步脚本 + IntentChaos.vdf + preview.jpg
_test_compat/                离线适配预检：拿当前安装的本体真跑一遍 Compat.Resolve()
_test_structural/            「结构性招」判定回归测试：反射造本体形状的 MoveState，核对该固定/该随机
_scan/                       影响面静态扫描：解析本体反编译源码，统计每只怪的池子变化
_shared/LocalPaths.cs        两个测试共用的路径解析（仓库里不出现绝对路径）
```

## 构建与部署

前置：**.NET SDK 9.0 或更高**、游戏本体、Windows PowerShell 5.1（工坊脚本用它跑）。

```powershell
# 1. 配置本机路径（一次即可）
Copy-Item IntentChaos\local.props.example IntentChaos\local.props
#    然后把 local.props 里的 <GameDir> 改成你的游戏安装根目录
#    （含 data_sts2_windows_x86_64 的那一层，例：D:\SteamLibrary\steamapps\common\Slay the Spire 2）

# 2. 构建（构建前先把游戏关掉，否则 dll 被锁、报错信息很有误导性）
cd IntentChaos\src
dotnet build
```

`local.props` 的 `AutoDeploy=true` 会在构建后自动把 json + dll 复制到 `<GameDir>\mods\IntentChaos\`，进游戏即生效。启动日志（`%APPDATA%\SlayTheSpire2\logs\godot.log`）里搜 `IntentChaos` 可确认挂载成功。

路径解析优先级：环境变量 `STS2_GAMEDIR` > `IntentChaos\local.props`，两个离线测试也走同一套（`_shared\LocalPaths.cs`）。

## 三个离线检查（改完代码必跑，缺一别声称修好）

```powershell
# ① 本体 API 适配：[OK] 才算与当前游戏版本兼容
dotnet run --project _test_compat

# ② 结构性判定回归：退出码 0 = 该固定的固定、该随机的随机
dotnet run --project _test_structural

# ③ 动过"哪些招不许动"的判据时，先扫影响面（看哪只怪的池子会缩、会不会缩成 0）
powershell -File _scan\scan_moves.ps1 -DecompDir <你的本体反编译树根目录>
```

第 ③ 项需要**自备本体反编译源码**（本仓库不附带，见下节）；`-OutDir` 可指定报告落点，默认写 `_scan\move_classification.csv`。

## 上传创意工坊

```powershell
dotnet build                                     # 先构建
powershell -File IntentChaos\workshop\sync_workshop.ps1 -Version v0.3.2
```

`sync_workshop.ps1` 会：

1. 从权威来源取文件（清单取 `IntentChaos\IntentChaos.json`，dll 取 `src\bin\Debug\`）复制进 `workshop\content\IntentChaos\`（该目录整包上传，不放多余文件）
2. 刷新 `IntentChaos.vdf` 的 `contentfolder` / `previewfile` —— 仓库里这两行存的是 `<REPO>` 占位符，脚本每次按当前仓库位置替换成本机绝对路径（**别手改这两行**）
3. 自检编码与一致性：json/vdf 必须 UTF-8 无 BOM、中文不得损坏、dll 必须与游戏 `mods\` 目录那份 hash 一致，`[OK]` 才算同步成功
4. 打印 SteamCMD 上传命令（上传要你本人 Steam 登录 + Guard 验证码）

文案成品是工作区根的 `Description.md`（多行 BBCode，中英两段），可直接贴进工坊网页编辑框；若要用 SteamCMD 带文案上传，先 `powershell -File IntentChaos\workshop\sync_desc_to_vdf.ps1` 从它灌进 VDF。

## 开发约束（这里死过文件，别绕开）

### 编码：脚本要 BOM，数据不要 BOM

| 文件 | 编码 | 原因 |
|---|---|---|
| 含中文的 `.ps1` | UTF-8 **带** BOM | PS 5.1 无 BOM 会按 ANSI 读 → 中文注释引发"意外的标记"，脚本根本跑不起来 |
| 清单 `.json`、工坊 `.vdf`、`.md` | UTF-8 **无** BOM | 游戏/Godot 侧解析器不接受 BOM |

**禁止用 `Get-Content` / `Set-Content` / `Out-File` 改写含中文的文本文件**：PS 5.1 按 GBK 解释 UTF-8 字节再写回，中文会变成**不可逆**乱码。改写只用 .NET：

```powershell
$t = [IO.File]::ReadAllText($p, [Text.Encoding]::UTF8)
[IO.File]::WriteAllText($p, $t, (New-Object Text.UTF8Encoding($false)))   # .ps1 传 ($true)
```

（v0.3.0 同步时真踩过：破折号 `—` 被吃成 `?`，整段中文全废，只能重写。）

### 版本兼容只走适配层

本体在快速迭代，**业务代码禁止直接调会漂移的本体签名**：

- `IntentChaos\src\Compat.cs` 启动时按"名称 + 参数形状"反射解析本体成员，编译成表达式树委托，零编译期绑定；解析失败 mod 自动休眠，不影响游戏
- 本体改名/删成员 → 在 `Compat.Resolve()` 的成员清单里补新名字，然后重跑 `_test_compat`
- 测试宿主调 mod 的委托前**必须先 `Compat.Resolve()`**，否则那些委托是 null，一调就 NRE
- 补丁体内**任何异常都要吞掉并让 mod 自我休眠**，绝不能外抛——`Hook.AfterDamageReceived` 的异常会杀掉回合循环，表现为"玩家打不出牌"

### PowerShell 5.1 陷阱

- **.NET 文件 API 用的是"进程当前目录"，`cd` 改的是"PS 位置"**：`cd` 之后用相对路径调 `[IO.File]::*`，读会抛异常，写会**在别处静默建同名文件**。一律写绝对路径
- `$home` 是只读自动变量，赋值静默失败并把文件写进用户目录
- 别用 `Select-Object -First N` 截断外部进程的管道——它会中途杀掉进程
- 替换带转义的字符串字段时别用 `[^"]*` 去"找闭合引号"（遇到 `\"` 会提前截断）：**整行替换** + 校验"闭引号之后必须正好是行尾"

## 不随仓库发布的东西

- **游戏本体反编译源码与解包资源**（`_sts2_decomp\`、`_game_pck\` 等）：版权归原作者，且体积过大；需要时用 ILSpy/GDRE 自行解出，再指向 `_scan` 的 `-DecompDir`
- **构建产物**（`bin\`、`obj\`）与 `IntentChaos\workshop\content\`（由 `sync_workshop.ps1` 重新生成）
- 本机配置 `IntentChaos\local.props`

## 许可

[MIT](LICENSE)。游戏本体与其资源版权归 Mega Crit 所有，本仓库不包含它们。
