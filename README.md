# CBB / Botja（修复版）

博兹雅（南方战线 / 天佑女王）自动刷 FATE、自动报名紧急遭遇战（CE）的 Dalamud 插件。

本仓库是 [Phoenixium/CBB](https://github.com/Phoenixium/CBB) 的 fork，保留原作者全部功能，**专门修复了「无法自动加入紧急遭遇战」**，并补上了中日德法客户端与中文界面的兼容。

> 原版在中文客户端上永远不会报名 CE：它按按钮上的**英文文字**去匹配「Register / Commence」，而中文客户端这些按钮是「参加 / 出击」，匹配不上就一直停在 `Waiting for the recruitment window.`，直到 5 分钟超时。

## 安装

1. 游戏内 `/xlplugins` → 设置 → 实验性功能 → **自定义插件仓库**
2. 填入：`https://raw.githubusercontent.com/solenmingco/Botja/main/repo.json`
3. 保存后搜索 **CBB** 安装，游戏内 `/cbb` 打开主窗口，`/cbb config` 打开设置

如果你之前装过 **BozjaPilot 1.0.2.0**，请在插件列表里把它卸载，两者功能重叠，同时开着会互相抢目标。

### 依赖

- **vnavmesh** — 寻路与移动（必需）
- **Lifestream** — 以太之光传送（必需）
- 战斗循环任选其一：Rotation Solver Reborn / WrathCombo / BossMod / BossMod Reborn

## 这次改了什么

CE 报名是唯一必须操作游戏 UI 的环节：遭遇战（Skirmish）只要人在圈里，CE 必须在报名窗口里按下按钮才算报名。修复全部集中在这一块。

| 问题 | 原实现 | 现在 |
|---|---|---|
| 按钮识别 | 只匹配英文 `Register` / `Commence` / `Enter` | 先用窗口自己的行数据 + 按钮事件参数定位，本地化文字只作兜底 |
| 报名窗口打不开 | `Show()` 只尝试一次，失败就永久卡住 | 每 2 秒重试，直到打开 |
| 报名确认 | 先跳到「等待确认」状态，只有弹 `SelectYesno` 才继续 ← 博兹雅根本不弹，所以这里本来就会死等 | 报名后直接进入等待出击，`SelectYesno` 只在点击后 3 秒内应答 |
| 组队 | 非队长直接不报名（博兹雅是**个人报名**，与队长无关） | 已移除该限制 |
| 出击判定 | 读 `DynamicEvent` 硬编码偏移 `0x70` 取准备时长 | 用窗口/容器的剩余时间，准备阶段直接出击；正在打 FATE 时留到剩 15 秒再进 |
| CE 名称 | 用 `DynamicEventId` 去查 Lumina 表，取不到名字就静默放弃点击 | 名字只用于显示，不再参与点击决策 |
| 选择目标 | 取容器里第一个可报名的 | 优先窗口里真实列出的那一个，其次剩余时间最短的 |
| 聊天提示 | 只认 `critical engagement` | 同时认「紧急遭遇战」「特殊场景探索」等本地化文本 |
| 失败原因 | 静默 | 状态栏显示 `Last click: clicked '参加' (param=0, event param 0)`，日志同步输出 |

另外修掉一处会误判的死锁：原版在「已报名」状态只等确认框，不读 CE 状态，因此在没有确认框的客户端上会一直等到超时，永远进不到出击阶段。

## 出问题怎么报

`/cbb` 主窗口 → Debug 页 → **Copy CE dump**，把内容贴出来即可。里面包含：

- 报名 agent 是否激活、addon id
- 窗口自己维护的每一行（事件 ID、状态、剩余秒数、人数、名称）
- 窗口里每个按钮的**文字、事件参数、是否可用**
- 窗口的全部文本节点

有了这些就能直接看出按钮参数是行号还是事件 ID，不用再猜。

## 命令

| 命令 | 作用 |
|---|---|
| `/cbb` | 主窗口（进入博兹雅/天佑女王时自动打开） |
| `/cbb config` | 设置 |

## 功能（原版）

- FATE 列表与优先级权重、进度上限、区域上限、黑名单
- 自动以太之光传送 + vnavmesh 寻路，长途自动上坐骑
- Auto Mode：自动选点、自动移动、后台排队 CE，CE 开始后自动切换到 CE
- 战斗 AI 开关（只在打 FATE/CE 时开启，赶路时关闭）
- 敌对目标提示与仇恨范围可视化
- 鉴定碎片、兑换战果记录

## 构建

```bash
git clone --recursive https://github.com/solenmingco/Botja.git
cd Botja
dotnet build Botja/Botja.csproj -c Release
```

需要 .NET 10 SDK，并且本机装好 Dalamud（构建会从 Dalamud 目录解析程序集）。

## 许可与致谢

代码来自 [Phoenixium/CBB](https://github.com/Phoenixium/CBB)（作者 Phoenixium），依赖 [OhKannaDuh/Ocelot](https://github.com/OhKannaDuh/Ocelot) 框架。本 fork 仅修改紧急遭遇战报名链路，其余逻辑与原版一致。
