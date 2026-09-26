# cs2-vyron-skill

使用 [CounterStrikeSharp](https://docs.cssharp.dev/)（C# 插件框架）为 **CS2 服务器** 实现《三角洲行动》干员 **威龙** 的战术装备 **「动力推进」**：短距离喷气突进 + 击倒/击杀缩减冷却的连续穿梭循环。

核心数值完全对齐原作说明：**单次推进 10 米、基础冷却 15 秒、可空中释放、支持 8 方向（可切换为仅 4 个正方向）**，并且实现了两种模式的冷却循环：

* **烽火地带**：击倒人机 **-7 秒**，击倒/击杀玩家 **-10 秒**；
* **全面战场**：击败敌人 **直接刷新** 冷却（连续击杀 = 近乎无限喷气位移）。

---

## 目录

* [功能一览](#功能一览)
* [与原作机制的对照](#与原作机制的对照)
* [环境要求](#环境要求)
* [构建](#构建)
* [安装部署](#安装部署)
* [玩家使用](#玩家使用)
* [配置文件](#配置文件)
* [管理员命令](#管理员命令)
* [实现要点](#实现要点)
* [测试与验证](#测试与验证)
* [目录结构](#目录结构)
* [已知限制](#已知限制)
* [参考资料](#参考资料)

---

## 功能一览

| 功能 | 说明 |
| --- | --- |
| 8 方向喷气突进 | 依据 WASD 与视角 yaw 合成方向（前/后/左/右 + 四个斜向）；**不按方向键时朝准星方向推进** |
| 仅 4 个正方向 | 服务器默认 `direction_mode` 配置；玩家也可用 `!dashmode` 单独切换自己的「干员偏好」 |
| 10 米 / 15 秒 | 单位换算遵循 Source 引擎比例（1 单位 = 1 英寸），10 米 = 393.7 单位 |
| 空中释放 | `allow_in_air`（默认开启），可从非常规角度切入或空中调整落点 |
| 速度脉冲而非瞬移 | 逐帧写入速度，交由引擎做碰撞与贴墙滑行，不会穿墙 |
| 冷却缩减循环 | 烽火地带 -7s / -10s；全面战场击杀直接刷新 |
| 击倒（DBNO）模块 | 可选开启：致命伤不再致死而是把人打趴（冻结、缴械、可被队友按 E 救起），复刻「击倒玩家 -10 秒」 |
| 反馈 | 聊天提示（彩色）、屏幕中央冷却 HUD、启动/就绪音效 |
| 多语言 | 内置 `lang/en.json` 与 `lang/zh-CN.json`，使用 CounterStrikeSharp 的 `IStringLocalizer`，按玩家客户端语言输出 |
| 管理命令 | 状态查看、模式热切换、冷却重置、配置热重载（`@css/root` 权限） |

## 与原作机制的对照

| 《三角洲行动》设定 | 本插件实现 |
| --- | --- |
| 前/后/左/右 + 四个斜向，共 8 个方向 | `DashMath.ResolveEightWay`：WASD 相对视角 yaw 合成，斜向自动归一化 |
| 设置中可调「干员偏好：仅 4 个正方向」 | `direction_mode: "FourWay"`（整服）或 `!dashmode`（单人生效，取**最近按下**的移动键，杜绝误触斜向） |
| 单次推进 10 米 | `dash.distance_meters: 10` → 393.7 单位；`duration_seconds` 决定喷气时长，速度 = 距离 / 时长 ≈ 1125 u/s |
| 基础冷却 15 秒 | `cooldown.base_seconds: 15` |
| 空中释放 | `dash.allow_in_air: true`（另有 `air_vertical_boost` 可做悬停/抬升） |
| 烽火地带：击倒人机 -7 秒 | `cooldown.bot_knockdown_seconds: 7`（CS2 中 Bot 直接阵亡，等同原作「击倒人机」） |
| 烽火地带：击倒玩家 -10 秒 | 开启 `knockdown` 模块后真实击倒；未开启时由**助攻**（协助击倒）折算，见下 |
| 烽火地带：击杀玩家 -10 秒 | `cooldown.player_kill_seconds: 10` |
| 全面战场：击败敌人直接刷新 | `cooldown.all_out_warfare_kill_refreshes: true` → `ReadyAt = now`，可立即二次突进 |

> CS2 没有原生的「倒地/救援」状态，因此插件提供了两种层次：
> 1. **助攻折算（默认开启）**：你参与击杀的敌人被视作「被你击倒」，给 -10 秒，零副作用；
> 2. **真实击倒模块（默认关闭）**：`knockdown.enabled: true` 时，敌方玩家的致命伤会被改写为「击倒」，随后冻结、缴械、流血倒计时，队友按住 `E` 数秒可救起；救援失败则流血阵亡，补枪的人拿到正常击杀与 -10 秒。

## 环境要求

* CS2 专用服务器（Windows / Linux 均可），已安装：
  * [Metamod:Source 2.x](https://www.metamodsource.net/downloads.php/?branch=master)（build 1467+）
  * [CounterStrikeSharp（with runtime）](https://github.com/roflmuffin/CounterStrikeSharp/releases)
* 构建用 .NET SDK **10.0**（本插件 `TargetFramework` 为 `net10.0`，与 CounterStrikeSharp 1.0.3xx 之后的运行时一致）
* 服务器上的 `addons/counterstrikesharp/api/CounterStrikeSharp.API.dll` 版本，建议与 `src/VyronSkill/VyronSkill.csproj` 中的 `CssApiVersion` 一致（默认 `1.0.371`，已验证同样可编译 `1.0.375`）

## 构建

```shell
dotnet build src/VyronSkill/VyronSkill.csproj -c Release
# 若服务器用的是别的 CounterStrikeSharp 版本：
dotnet build src/VyronSkill/VyronSkill.csproj -c Release -p:CssApiVersion=1.0.375
```

产物位于 `src/VyronSkill/bin/Release/net10.0/`：

```txt
VyronSkill.dll
VyronSkill.deps.json
VyronSkill.pdb
lang/en.json
lang/zh-CN.json
```

> `CounterStrikeSharp.API.dll` **不会**被复制到输出目录（`ExcludeAssets=runtime`），这是插件的正确打包方式：API 由服务器进程提供。

## 安装部署

### 方式一：脚本（推荐）

```powershell
# Windows PowerShell 5.1
powershell -ExecutionPolicy Bypass -File ./scripts/deploy.ps1 -ServerPath "D:\cs2server\game\csgo"

# PowerShell 7+
pwsh ./scripts/deploy.ps1 -ServerPath "D:\cs2server\game\csgo"

# 用指定 CounterStrikeSharp 版本构建后再部署
pwsh ./scripts/deploy.ps1 -ServerPath "D:\cs2server\game\csgo" -CssApiVersion 1.0.375
```

脚本会构建并把 `VyronSkill.dll` / `.deps.json` / `.pdb` / `lang/` 复制到
`<csgo>/addons/counterstrikesharp/plugins/VyronSkill/`（`-SkipBuild` 可跳过构建步骤）。

### 方式二：手动

把上面的产物放进：

```txt
<csgo>/addons/counterstrikesharp/plugins/VyronSkill/
├── VyronSkill.dll
├── VyronSkill.deps.json
├── VyronSkill.pdb            (可选)
└── lang/
    ├── en.json
    └── zh-CN.json
```

重启服务器（或控制台执行 `css_plugins reload VyronSkill`）。首次加载会自动生成配置文件
`addons/counterstrikesharp/configs/plugins/VyronSkill/VyronSkill.json`。

## 玩家使用

| 操作 | 说明 |
| --- | --- |
| `bind "f" "css_vyron"` | 把动力推进绑到按键（客户端控制台执行一次即可）；`bind "f" "+vyron"` 之类的写法不适用 |
| 聊天 `!dash` / `!vyron` / `/dash` | 无需绑定也能释放 |
| 控制台 `css_vyron` / `dash` / `vyron` | 同上 |
| `!dashmode` | 切换自己的「干员偏好」：8 方向 ⇄ 仅 4 个正方向 |
| `!vyronhelp` | 在聊天里显示使用说明 |

方向规则：

* 按住 `W/A/S/D`（可组合出斜向）→ 朝对应方向推进；8 方向模式下 `W+D` 得到 45° 斜向；
* 不按方向键 → 朝**准星朝向**（仅水平分量）推进；
* 仅 4 方向模式 → 只认**最近按下**的那个键，永远不会误触斜向；
* 空中同样可以释放（`allow_in_air`）。

## 配置文件

`addons/counterstrikesharp/configs/plugins/VyronSkill/VyronSkill.json`

```json
{
  "enabled": true,
  "game_mode": "HazardOps",
  "dash": {
    "distance_meters": 10.0,
    "duration_seconds": 0.35,
    "direction_mode": "EightWay",
    "allow_in_air": true,
    "air_vertical_boost": 0.0,
    "block_during_freeze_time": true
  },
  "cooldown": {
    "base_seconds": 15.0,
    "bot_knockdown_seconds": 7.0,
    "player_knockdown_seconds": 10.0,
    "player_kill_seconds": 10.0,
    "all_out_warfare_kill_refreshes": true,
    "count_assist_as_knockdown": true,
    "reset_on_spawn": true,
    "reset_on_round_start": true
  },
  "feedback": {
    "chat_on_use": true,
    "chat_on_cooldown": true,
    "chat_on_reward": true,
    "hud_enabled": true,
    "hud_interval_seconds": 0.25,
    "sound_on_use": "buttons/blip1.wav",
    "sound_on_ready": ""
  },
  "knockdown": {
    "enabled": false,
    "only_in_hazard_ops": true,
    "apply_to_bots": false,
    "health_after_knockdown": 30,
    "bleed_out_seconds": 25.0,
    "max_per_round": 1,
    "headshot_bypasses": true,
    "remove_weapons": true,
    "give_knife_on_revive": true,
    "revive_health": 50,
    "revive_hold_seconds": 3.0,
    "revive_radius_units": 90.0
  },
  "Version": 1
}
```

| 字段 | 默认值 | 说明 |
| --- | --- | --- |
| `enabled` | `true` | 插件总开关 |
| `game_mode` | `HazardOps` | `HazardOps`=烽火地带（-7s/-10s），`AllOutWarfare`=全面战场（击杀刷新） |
| `dash.distance_meters` | `10.0` | 单次推进距离（米） |
| `dash.duration_seconds` | `0.35` | 喷气时长；速度 = 距离 / 时长 |
| `dash.direction_mode` | `EightWay` | `EightWay`=8 方向；`FourWay`=仅 4 个正方向（玩家可用 `!dashmode` 覆盖自己的设置） |
| `dash.allow_in_air` | `true` | 是否允许空中释放 |
| `dash.air_vertical_boost` | `0.0` | 空中推进时的垂直速度（u/s）；`0` = 正常受重力，正数可悬停/抬升 |
| `dash.block_during_freeze_time` | `true` | 准备时间内禁止推进 |
| `cooldown.base_seconds` | `15.0` | 基础冷却 |
| `cooldown.bot_knockdown_seconds` | `7.0` | 烽火地带：击倒人机缩减量 |
| `cooldown.player_knockdown_seconds` | `10.0` | 烽火地带：击倒玩家缩减量 |
| `cooldown.player_kill_seconds` | `10.0` | 烽火地带：击杀玩家缩减量 |
| `cooldown.all_out_warfare_kill_refreshes` | `true` | 全面战场：击杀是否直接刷新冷却 |
| `cooldown.count_assist_as_knockdown` | `true` | 是否把「协助击杀」折算为击倒奖励 |
| `cooldown.reset_on_spawn` | `true` | 出生时冷却直接就绪 |
| `cooldown.reset_on_round_start` | `true` | 回合开始时全服冷却就绪 |
| `feedback.chat_on_use` / `chat_on_cooldown` / `chat_on_reward` | `true` | 聊天反馈开关 |
| `feedback.hud_enabled` / `hud_interval_seconds` | `true` / `0.25` | 屏幕中央冷却 HUD |
| `feedback.sound_on_use` / `sound_on_ready` | `buttons/blip1.wav` / 空 | 客户端音效（`play <文件>`，留空即关闭） |
| `knockdown.enabled` | `false` | 真实击倒（DBNO）模块，见下 |
| `knockdown.only_in_hazard_ops` | `true` | 仅在烽火地带模式下允许击倒 |
| `knockdown.apply_to_bots` | `false` | 是否也把 Bot 打成倒地（默认 Bot 直接阵亡） |
| `knockdown.health_after_knockdown` | `30` | 被击倒后剩余血量（不会超过其当前血量） |
| `knockdown.bleed_out_seconds` | `25.0` | 无人救援时的流血阵亡时间 |
| `knockdown.max_per_round` | `1` | 每人每回合最多被击倒次数（`0` = 不限） |
| `knockdown.headshot_bypasses` | `true` | 爆头直接击杀，不进入倒地 |
| `knockdown.remove_weapons` | `true` | 击倒后缴械 |
| `knockdown.give_knife_on_revive` | `true` | 救起后发一把刀，避免完全空手 |
| `knockdown.revive_health` / `revive_hold_seconds` / `revive_radius_units` | `50` / `3.0` / `90` | 救援后的血量 / 按住 `E` 的时长 / 救援距离（单位） |

配置里所有非法或越界数值都会在加载时被自动修正并在服务器日志中给出警告，不会因为写错一个数字导致插件异常。

## 管理员命令

| 命令 | 权限 | 作用 |
| --- | --- | --- |
| `css_vyron_status` | `@css/root` | 打印当前模式、数值与实际冷却（含每位玩家剩余冷却） |
| `css_vyron_mode <hazard\|warfare>` | `@css/root` | 热切换烽火地带 / 全面战场，并写回配置文件 |
| `css_vyron_reset` | `@css/root` | 立刻让所有玩家冷却就绪（测试用） |
| `css_vyron_reload` | `@css/root` | 从磁盘重新读取配置 |

## 实现要点

* **单位换算**：Source 引擎 `1 单位 = 1 英寸`（玩家高 72 单位 ≈ 1.83 m），故 1 米 = 39.3701 单位，10 米 = 393.7 单位。
* **推进方式**：不用 `Teleport` 瞬移，而是逐帧写入 `pawn.Velocity` / `pawn.AbsVelocity`（`Listeners.OnTick`），让玩家移动代码自己做扫掠碰撞——因此不会穿墙，位移结束还会保留惯性。
* **方向解析**：`Core/DashMath.cs` 是纯数学（无 CounterStrikeSharp 依赖），
  * 8 方向 = `yaw` 的前/右基向量按 `W/S/A/D` 线性组合后归一化；
  * 4 方向 = 由 `Listeners.OnPlayerButtonsChanged` 维护的「按键按下顺序栈」取最近按下的键，因此 `W`+`D` 同按也不会变成斜向。
* **冷却循环**：`Core/CooldownRules.cs` 是纯函数规则表（`ForKill` / `ForKnockdown` / `ForAssist`），
  `PowerBoostService.ApplyReward` 只负责把结果落到 `ReadyAt`（并保证不会变成负冷却）。
* **击倒（DBNO）**：`Listeners.OnPlayerTakeDamagePre` 中若判定这次伤害致命、来源为敌方玩家、非爆头，则把
  `info.Damage` 压到「只掉到 `health_after_knockdown`」的数值，使受害者存活并进入倒地状态（冻结 `FL_FROZEN`、清空水平速度、缴械、倒计时流血）；队友在半径内按住 `E` 达到时长即可救起。该模块默认关闭，因为它会同时改变 CS2 的回合节奏。
  这一改写方式与 CounterStrikeSharp 的实现一致：框架的 `CBaseEntity_TakeDamageOld` detour 会把**原生 `CTakeDamageInfo*` 指针**交给 pre 回调，只有返回 `Handled`/`Stop` 才会拦截伤害，因此 `info.Damage` 的修改会被引擎自身按正常流程结算（护甲、命中部位等逻辑不受影响）。
* **多语言**：`lang/*.json` + `Localizer.ForPlayer(...)`，玩家可用 CS2 自带的 `!lang` 切换语言；带 `{green}` 等颜色标签的文案会被 CounterStrikeSharp 自动转成聊天颜色，HUD 文案则使用 HTML 颜色标签。

## 测试与验证

```shell
dotnet test VyronSkill.slnx
```

* `tests/VyronSkill.Tests` 直接编译 `src/VyronSkill/Core/*.cs`（纯逻辑，不依赖游戏），共 **40 个单元测试**，覆盖：
  * Source yaw 前/右向量约定；
  * 8 方向恰好覆盖 8 个 45° 步进方向且长度归一化；
  * 4 方向只取最近按下的键（不会出现斜向）；
  * 10 米 / 0.35 秒 ⇒ 1124.86 u/s 的速度换算与米/单位往返；
  * 烽火地带 -7s/-10s、全面战场刷新、助攻折算、0 值安全、连续击杀不会产生负冷却；
  * 本地化静态检查：代码里用到的每个 `vyron.*` 文案键都存在于 `lang/*.json`，两种语言的键集合完全一致，且 `{0}`/`{1}` 占位符数量互相对应（避免上线后把键名原文或异常丢给玩家）。
* 插件本体已在 **CounterStrikeSharp.API 1.0.371 与 1.0.375** 上编译通过（0 warning / 0 error）。

> **关于端到端验证的说明**：本仓库的开发环境里没有可运行的 CS2 服务器，因此游戏内行为（速度写入的实际手感、冻结旗标、击倒状态等）**尚未在真实服务器上跑过**。上线前请按下面的清单自查一遍。

服务器内验证清单：

1. `meta list` / `css_plugins list` 能看到 `VyronSkill`，控制台无报错；
2. `!vyronhelp` 有输出，`!dash` 能在 8 个方向（含斜向、含不按键时朝准星）正确推进 10 米左右；
3. `!dashmode` 切到 4 方向后，`W`+`D` 同按只走一个正方向；
4. 空中释放一次，确认不穿墙、落地正常；
5. 冷却 HUD 倒计时正确，冷却结束后有「就绪」音效（若配置了 `sound_on_ready`）；
6. 烽火地带：打死一个人机后聊天提示 `-7 秒`，打死/助攻玩家后提示 `-10 秒`；
7. `css_vyron_mode warfare` 后每次击杀应立即刷新冷却，可连续突进；
8. 开启 `knockdown.enabled` 后：致命伤不死而是倒地、被缴械、队友按住 `E` 可救起、无人救援则流血阵亡、爆头仍然直接击杀、回合开始不会有人卡在冻结状态；
9. 观察者/死亡状态下 `!dash` 只给提示不生效；准备时间内无法推进；
10. 热重载（替换 dll 或在控制台 `css_plugins reload VyronSkill`）后功能正常，且没有玩家停留在冻结状态。

## 目录结构

```txt
cs2-vyron-skill/
├── VyronSkill.slnx                 # 解决方案（插件 + 测试）
├── scripts/deploy.ps1              # 构建并部署到 CS2 服务器
├── src/VyronSkill/
│   ├── VyronSkill.csproj
│   ├── VyronSkillPlugin.cs         # 插件入口：命令、事件、tick 调度
│   ├── Config/VyronSkillConfig.cs  # 配置模型 + 校验
│   ├── Core/                       # 纯逻辑（可单元测试，无游戏依赖）
│   │   ├── GameUnits.cs            # 米 ⇄ Source 单位
│   │   ├── DashMath.cs             # 8/4 方向解析
│   │   └── CooldownRules.cs        # 冷却缩减规则表
│   ├── Game/
│   │   ├── PowerBoostService.cs    # 动力推进本体 + 冷却 + HUD
│   │   ├── MovementInputTracker.cs # 按键按下顺序（4 方向偏好用）
│   │   └── KnockdownService.cs     # 可选击倒(DBNO)/救援模块
│   └── lang/{en,zh-CN}.json
└── tests/VyronSkill.Tests/         # 36 个单元测试
```

## 已知限制

* **击倒模块会改变回合节奏**：最后一个敌人在倒地未死时，回合不会立刻结束，要等其流血阵亡或被补枪；默认关闭。
* CS2 没有原生倒地状态，击倒时**必定**要改写伤害数值。若某次击杀完全不经过 `CBaseEntity_TakeDamageOld`（例如引擎脚本化的处决/自杀类死亡），受害者会正常死亡，此时击杀方按「击杀玩家」获得 -10 秒。
* 倒地期间的冻结依赖 `FL_FROZEN` 实体旗标 + 每帧归零水平速度，属于尽力而为的冻结：极端情况下（例如引擎在回合切换时清旗标）倒地玩家仍可能挪动，插件每帧都会重新施加，因此影响有限。
* 击倒后默认缴械，救起只补一把刀（`remove_weapons` / `give_knife_on_revive` 可调整）。
* 客户端音效通过 `play <文件>` 播放，请使用 CS2 自带的音效文件；文件不存在时只在客户端控制台留下一条警告。
* 插件把「协助击杀」折算成击倒（可通过 `count_assist_as_knockdown` 关闭），这是原作击倒机制在 CS2 中最贴近的近似，但两者并非完全等价。
* Bot 无法主动使用该技能（不会触发命令），因此技能实际只对人类玩家开放。

## 参考资料

* 工作区内 `docfx/` 目录即 CounterStrikeSharp 官方文档镜像（`docs/guides`、`docs/features`、`docs/admin-framework`、`examples`），本插件的命令、事件、配置、本地化、权限等写法均依据其中的说明；
* 官方文档站：<https://docs.cssharp.dev/>
* CounterStrikeSharp 仓库：<https://github.com/roflmuffin/CounterStrikeSharp>
