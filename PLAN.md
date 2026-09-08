# CircleRoyale 项目规划 —— 球球大作战类 · 多人 + 人机 · 霓虹线条风

> 配套文档：[RESEARCH.md](RESEARCH.md)（API 调研与依据）。本规划把调研结论落成可执行的开发路线：里程碑、任务分解、验收标准、技术设计与风险对策。

---

## 1. 目标与范围

**一句话**：俯视角 .io 球球大作战——吃食物成长、大吃小、分裂/吐孢子博弈；真人联机 + bot 填房；Geometry Wars 霓虹矢量画面。

**V1 范围内**：
- 单机可完整游玩（含 bot）
- 多人联机（Steam 大厅，host 权威数值 + owner 移动）
- bot 填房 AI（追食/逃跑/猎杀/游走）
- 霓虹渲染：批量线段/图形 + Bloom + 网格背景 + 拖尾
- HUD：分数、质量、排行榜、死亡重生面板

**V1 范围外（明确不做）**：
- 专用服务器（DedicatedServer）、断线自动重连、观战系统
- 匹配/房间 UI（用 s&box 自带大厅入口 + 控制台 `connect local` 调试）
- 中文本地化（UI 先英文/数字；中文字体方案见 RESEARCH.md §5.6）
- 地图编辑器内容、账号存档、皮肤系统

---

## 2. 默认设计决策（集中在 `GameConfig.cs`，全部可调）

| 项 | 默认值 | 说明 |
|---|---|---|
| 地图 | 4096×4096 正方形，发光边界 | 世界单位 |
| 人数 | MaxPlayers=24 真人 + bot 补齐到 32（含真人，可调） | `sbproj.Metadata.MaxPlayers=24`（已写入） |
| 玩家球视觉 | **玩家头像作圆心（Steam 头像，bot 用几何图案）+ 霓虹线条外环 + 头上名牌** | 用户确认 |
| 食物 | 400 颗，半径 8，质量 1，被吃后 2s 重生 | 6 种霓虹色轮换 |
| 初始球 | 半径 32 / 质量 10；`r = 32 * sqrt(mass/10)` | 面积∝质量 |
| 速度 | `v = 560 * (10/mass)^0.35`，下限 130 | 越大越慢 |
| 吃玩家 | `r1 > r2*1.15` 且圆心距 `< r1 - r2*0.4` | agar 经典规则 |
| 吃食物 | 圆心距 `< r1` | |
| 吞并耗时 | 无（碰到即吃） | 简化 |
| 分裂（P3） | 空格，mass≥64 才可，最多 8 分身，合并冷却 15s | |
| 吐孢子（P3） | W，mass≥36，每颗 -12 质量成食物 | |
| 绿刺（P3） | 12 个，mass≈100，大球撞上炸裂 | |
| 网络频率 | ProjectSettings.UpdateRate=30（默认），插值 0.1s（默认） | 不动 |
| 出生保护 | 3s 无敌（半透明） | |

**已确认决策**（2026-09-05 用户拍板）：最大人数 24；采用预制体（球预制体由用户在编辑器创建，代码按路径 Clone、缺失时回退代码构建）；Steam 公开大厅（`LobbyPrivacy.Public`）；球上名牌做；球体视觉=头像圆心+霓虹线条外环；分裂/吐孢子后置到 M3 但**输入与网络接口先行预留**；**预留道具系统接口**（见 4.6）。

---

## 3. 里程碑

### M0 — 项目骨架与单机移动（先把手感立起来）
任务：
1. 补 `circleroyale.sbproj` Metadata（TickRate 50 / MaxPlayers 8 / GameNetworkType Multiplayer / StartupScene）。
2. `Game/CircleroyaleGame.cs`（`GameObjectSystem<CircleroyaleGame>`）：static 世界根复用（tiaotiao 模式）、屏蔽模板残留、建相机/灯/背景/HUD 挂点；`Game.IsPlaying` 拦截。
3. `Game/GameConfig.cs`：上表全部常量。
4. `Player/Ball.cs` 骨架 + owner 移动（鼠标方向 → `WorldPosition += dir*speed*dt`，速度按质量曲线）。
5. `Rendering/NeonRenderer.cs` + `Rendering/NeonCamera.cs`：SceneDynamicObject 双批次（Lines/Triangles）、`shaders/line.shader` 加色材质、正交相机跟随；静态网格背景（Lines 批）与发光边界。
6. `UI/GameHud.cs(.scss)`：分数/质量面板（先接假数据占位）。

验收：Play 模式下可见霓虹网格 + 可操控球，帧率正常，控制台无红字；热重载后世界不堆叠；停止/重进 Play 正常。

### M1 — 单机核心玩法（零网络代码，验证规则与手感）
任务：
1. `Game/FoodManager.cs`：本地 List 生成 400 食物 + 重生计时；吃判定数据接口。
2. Ball 完整化：吃食物成长、质量→半径→速度曲线、出生保护、死亡（pop 效果占位）。
3. `Bot/BotBrain.cs`：host 侧 FSM（追食/逃离更大/追猎更小/游走），0.25s 决策间隔，6 个 bot。
4. 玩家吃玩家 / 被吃：大吃小判定 → 对方死亡；本机玩家死亡弹面板。
5. 排行榜（host 逻辑本地版）+ 重生按钮 + 相机随质量缩放。

验收：完整 io 闭环——吃食物变大、速度随之变慢、吃 bot、被 bot 吃、死亡重生；排行榜实时正确；长玩 10 分钟无异常日志。

**实施记录（2026-09-05，MCP 实机验收）**：M1 全部落地，bot 数 = BotFillTarget−真人（本机 31+1）。名牌方案改定：世界空间文本（TextRenderer/WorldPanel）在本机 Play-in-editor 环境一律不渲染（字体/字号/朝向/正交相机均排查过，原因未明），改为 **GameHud 投影名牌**——`CameraComponent.ScreenToWorld` 现场标定"世界↔物理像素"仿射映射 + 除以 `RootPanel.Scale`（UI 以 1080p 为基准的逻辑缩放）赋给 Label。排行榜/死亡面板/自排名行全部按设计工作，中文昵称走 SimHei（已复制进 Assets/fonts）。

### M2 — 联机多人（核心里程碑）
任务：
1. `Game/NetworkManager.cs`（`Component + INetworkListener`）：host `OnStart` 时 `Networking.CreateLobby`（**Public 公开大厅**，MaxPlayers 24）；`AcceptConnection` 审批；`OnActive` 生成该连接的 Ball（优先 Clone `ball.prefab`，回退代码构建；`NetworkSpawn(channel)`）；`OnDisconnected` 销毁 + bot 补位。
2. Ball 网络化：`[Sync(FromHost)] Mass/PlayerName/Alive/IsBot`；位置走 owner transform 同步；host 侧移动校验（单帧位移 > max*1.5 拉回，出生/重生白名单）。
3. FoodManager 网络化：`[Sync(FromHost)] NetList<FoodData>`（struct：`Vector3 Pos / byte ColorIndex / bool Alive`），吃/重生改列表元素，自动增量同步。
4. 事件 RPC：`[Rpc.Host] RequestRespawn()`（`Rpc.Caller` 定位玩家）；`[Rpc.Broadcast(NetFlags.Unreliable)] PoppedEffect(pos,color,r)` 广播死亡爆裂。
5. 排行榜改 `[Sync(FromHost)] string`（0.25s 打包刷新）；HUD 读名字/分数。
6. bot 与真人统一：bot Ball 的 owner=host、`IsBot=true`，AI 只在 host 跑。

验收：同机双开（第二个实例控制台 `connect local`）：两窗口互见对方球平滑移动；A 吃食物/吃 B 两边状态一致；断开客户端后其球消失、bot 自动补位；host 视角零延迟、客户端手感可接受。

**实施记录（2026-09-05）**：上述 1/2/3/6 全部落地，单机 host 回归通过（GameNet+32 球+大厅创建无错误）。实现要点与偏差：
- `GameNet`（NetworkMode.Object，权威端在 EnsureWorld 创建）承载 FoodManager+NetworkManager，随场景复制到客户端；客户端不创建，Tick 懒查找 `_food`。
- 所有权范式（官方 NetworkHelper 同款）：host `OnActive` 里 `Clone + NetworkSpawn(channel)` → 该客户端本地 `IsProxy=false` 并模拟移动，transform 自动同步；球注册统一走 `Ball.OnStart → RegisterBall/SetLocalBall`（host 生成与客户端复制共用入口）。
- `Alive` 字段未实现（死亡=销毁对象，无需状态位）；排行榜**不做打包 string**——Mass 已随 [Sync] 下发，各端本地排序（改设计）；PoppedEffect RPC 留 M3。
- 客户端重生：`[Rpc.Host] RequestRespawn(steamId)`，host 校验 SteamId 找回连接后 `SpawnBall(owner: conn)`。
- host 位移校验：远端球单帧位移 > Speed×3+16 拉回（橡皮筋）。
- API 坑：Component 内裸写 `Network.` 被 `GameObject.Network` 属性遮蔽（需 `global::Sandbox.Network.*`）；`NetList` 用 `Count` 非 `Length`；`Networking.Connections` 已废弃改 `Connection.All`。

**★★ M2 收官记录（2026-09-05 晚，v0.3.0.12-M2，双开实测验收通过）**——以下设计**取代**上方早期实施记录中与之冲突的条目：

1. **GameNet 方案废弃**：FoodManager/NetworkManager 不再挂网络对象（每台机器一份本地实例）。食物走**静态 RPC**：`FoodFull`（新连接定向全量）+ `FoodEaten`/`FoodRespawned`（host 广播增量）+ `RequestFoodFull`（客户端补拉，try/catch）。挂在网络对象上的 NetList 复制不到客户端（实测），静态 RPC 路径 100% 可靠。
2. **玩家球生成时机 = `OnConnected`（Welcome，快照拍摄前）**：入队 → 主线程 `OnUpdate` +0.1s `SpawnBall(owner: channel)` → **球随初始快照到达客户端**。原因：① live create 广播到刚加入的连接会丢（同上下文定向 RPC 却能到，实测）；② OnConnected 跑在网络线程，直接 SpawnBall 会随机抛 ".ctor must be called on the main thread!"——必须入队主线程执行。`OnActive`（快照后）只做食物定向全量 + 无球兜底。
3. **客户端世界 = 直接启用快照世界根**：`AdoptSnapshotWorld()`（EnsureWorld 一次 + ScanBalls 每秒自愈，防"Tick 早于快照应用"竞态）——含球对象的顶层 GO `Enabled=true`，剥掉其 Camera/Hud 子物体（用本机的），本机不再创建背景/霓虹层（快照世界自带）。本机世界根命名 `CircleroyaleGameLocal` 避开同名。**reparent 路线废弃**：客户端改 host 拥有对象的层级会静默失效（Parent= 不报错不生效，球永远 Active=false → 头像不渲染/OnUpdate 不跑）。
4. **死亡 = `Alive` 状态位**（对象永不销毁，`[Sync(FromHost)]`）：NeonRenderer 跳过死者 + `Ball.OnUpdate` 同步隐藏头像精灵（两者都要，漏一个就是"尸体圈"）。重生 = host 置回 Alive=true，客户端 owner 自行随机换位（`_wasAlive` 翻转检测）。
5. **`Ball.IsRemoteOwned`**（host 侧生成意图标记，不联网）：把 host 为远程连接生成的球排除出本机球候选——同机双开时连接是合成 SteamId，与 Game.SteamId 对不上，唯一可靠判别是生成意图。
6. **sbproj `Resources`**：`/scenes/*` `/ui/*` `/fonts/*` 三通配必配（丢失 = 客户端资产全挂）；只能在编辑器**完全关闭**时改（运行中改会被内存态洗掉）。

**已知局限（M3 处理）**：
- 引擎握手偶发非主线程断言（`GetSnapshot`/`.ctor`，引擎层 bug）→ 连入偶发失败，**重连一次即过**；客户端自动重连未做，排 M3 第一项。
- 3+ 人：后加入者的球对"已连接的客户端"依赖 live create 广播（未验证）——做 3 人联机前需实测，不可靠则改为对新球做全量状态补发。
- host 停 Play → 客户端 TCP 僵尸（接收环死亡），必须重启客户端实例（引擎行为，无代码解）。
- 诊断日志已于收官全部摘除；保留低频生命周期日志（join/respawn/eaten）。

### M3 — 进阶玩法与表现力
任务：
1. 分裂（空格）/ 吐孢子（W）/ 合并冷却；多分身控制（一个 Ball 管理细胞列表，输入驱动全部）。
2. 绿刺 + 撞刺炸裂。
3. 表现力：`TrailRenderer` 拖尾、吃食物粒子迸溅、死亡爆裂粒子（`ParticleEffect`+`ParticleSpriteRenderer` 纯代码）、边界脉冲、球随速度微形变。
4. 球体视觉升级（用户已定稿）：头像圆心已在 M0 落地（本地 SteamId + 占位缩略图回退），本阶段补 **bot 几何图案头像** 与 **真人球按 Connection.SteamId 换图**；挂**名牌** WorldPanel（名字 + 质量）；预留的分裂/吐孢子输入与 RPC 接口在本阶段填逻辑并打开开关。

验收：分裂/合并在双开下同步正确；特效不影响帧率（100+ 实体场景 ≥100fps）。

**实施记录（2026-09-05，第 1 批：分裂/吐孢子/合并，v0.4.0.1-M3，单机权威端实机冒烟通过）**：

1. **分身/孢子 = 纯数据实体（`CellPiece`，非 GameObject）**：host 权威模拟（冲量衰减 + 跟随主人转向 + 场地钳制 + 合并检查），状态经 `CellsState` 静态 RPC 全量快照广播（`CellWire[]`，15Hz 节拍），客户端镜像列表只做渲染与 `DrawPos` 指数平滑。**不走网络对象的理由**：运行时 live create 到已连接客户端的可靠性未验证（M2 遗留），食物同款静态 RPC 是本项目实测 100% 可靠的唯一通道；且广播对刚加入的连接也有效（与食物增量同路径），中途加入能看到在飞的分身。
2. **主球架构不动**：仍是 owner 模拟的网络对象（transform 同步）；分裂只把质量对半弹出去，`Mass` 走既有 `[Sync(FromHost)]`。**主球被吃 = 整个玩家死亡**（分身随葬 `PopPiecesOf`），完全复用 M2 的 Alive 位/重生流/断线清理。
3. **输入与 RPC**：`Ball.OnFixedUpdate`（owner）读键——权威端直调 `DoSplit/DoEject`，客户端发 `RequestSplit/RequestEject`（`[Rpc.Host]` 实例 RPC，host 校验 `Rpc.Caller` 归属）；吐孢子按住连吐，客户端与 host 双侧节流。分裂方向 = 当前输入方向，静止时沿用最后移动方向，再退随机。
4. **吃判定扩展**（TickEating）：分身×食物、孢子被球/分身吞（吐出 0.5s 后才可食）、跨主人球×分身与分身×分身大吃小；同主人合并走 TickCells（冷却 15s 后碰主球/大分身即并）。孢子总量上限 96（挤最老）。
5. **表现**：NeonRenderer 画分身（无头像迷你球：盘+环）与孢子（小盘+细环）；NeonCamera 取景把自家分身包进来（`OwnerSteamId` 匹配，双开合成 ID 也能对上）；排行榜/HUD 分数改用**总质量**（主球+分身，`TotalMassFor`）。
6. 已知留待：客户端按键→host 执行的**分身手感延迟**（RPC 往返，本地双开≈0，公网=ping，接受）；bot 不分裂（v1，BotBrain 不感知分身威胁）；死亡爆裂特效（PoppedEffect）属本里程碑第 2 批。

**实施记录（2026-09-05，第 2 批：绿刺，v0.4.0.2-M3）**：
1. **静态障碍、数据同步**：12 刺 host 生成（避开中心 512 半径出生区），`SpikesFull(Vector2[])` 静态 RPC 广播 + OnActive 定向补发 + RequestFoodFull 顺带补发（幂等）；客户端只读 `game.Spikes` 画。
2. **判定（TickSpikes）**：细胞半径 > `SpikeRadius×1.1` 且压上刺芯（距离 < 自身半径 + `SpikeRadius×0.35`）→ 炸裂；**小于门槛从刺底安全穿过**（agar 规则，可躲刺后）；孢子碰刺芯被吸收（=喂刺）。
3. **炸裂**：主球 = `DoBurst` 连环强制对半分裂到分身槽满或质量 < 2×SplitMinMass（返回 bool——大球停刺上会每帧触发，用槽位/质量门槛自然收敛，挡特效/日志刷屏）；大分身撞刺 = 对半裂两个（槽满被吸收）；各重置合并冷却。**主球架构不动**：炸裂只产生分身数据，不碰死亡/重生流。
4. **bot 避刺**：BotBrain 在 Think 产出方向后做后处理——体积大到会炸时强导向绕开（感知距离 = 自身半径 + SpikeRadius×2.2）；小 bot 无视（能穿）。
5. 渲染：12 尖星形（描边 + 低透明填充，固定相位不旋转）。

**实施记录（2026-09-05，第 3 批：表现力，v0.4.0.3-M3）**：
1. **NeonRenderer 内置 CPU 粒子系统**（走同一霓虹加色批次，风格统一、零资产依赖）：`Particle{Pos,Vel,Life,Size,Color}`，阻尼飞散 + 淡出，上限 `FxMaxParticles=600`（超出挤最老，保帧率优先）。静态入口 `NeonRenderer.Pop`（死亡/爆裂，速度随半径感知）/`Spark`（吃食物）/`Puff`（分裂/吐孢子生成瞬间）。
2. **触发点与同步**：host 侧两处死亡 + DoBurst/BurstPiece → `SpawnPopFx`（本地 Pop + `PoppedEffect` RPC，`[Rpc.Broadcast(NetFlags.Unreliable)]`，丢帧特效无所谓不占可靠通道）；客户端 `OnPoppedEffectRemote`/`OnFoodEatenRemote`（**应用置死前**先取食物位置/颜色放火花）落地。分裂/孢子喷发 = host 本地 + 客户端 `OnCellsStateRemote` 新 id 检测补放——零额外 RPC。

**实施记录（2026-09-05，第 4 批：bot 几何头像，v0.4.0.4-M3）**：
- `BotAvatar.GetOrCreate(名字, 色号)`：FNV-1a 哈希名字 → `System.Random` 稳定种子 → 4 种图样（同心环/辐射条/点阵/波纹）× 3 变体，`Texture.Create(128,128,ImageFormat.RGBA8888).WithData(byte[]).Finish()` 程序化生成（API 已从 api.json 核实），圆形裁切+边缘 AA+中心亮渐变，背景透明透出球体霓虹底色。**同一 bot 各端生成同一张，零同步**；失败回退占位图。
- Ball：bot 分支接入 + 头像重试循环放开到 bot（名字/色号同步可能晚于 OnStart，此前 bot 只装占位图一次定型）。

**实施记录（2026-09-05，第 5 批：客户端握手卡死自动重连，v0.4.0.5-M3）**：
- NetworkManager.OnUpdate 客户端看门狗：连入后 12s（`ReconnectAfterSeconds`）仍无本机球 → `Networking.Disconnect()` + `Connect(记录的 HostConnection.Address)`，最多 5 次；成功入局复位计数；`IsConnecting` 期间不打断。地址在握手早期就随 HostConnection 出现，**首连失败也拿得到**。
- 边界划定：只管"卡在握手中途"（引擎偶发非主线程断言场景）；已入局后的掉线/TCP 僵尸态不归它管（无代码可救，不空转重试）。

**M3 剩余**：双开实测（分裂/绿刺炸裂双端同步、看门狗真实触发一次）、表现力第二批（拖尾/边界脉冲/速度微形变）按需后置。

### M4 — 打磨与发布准备（实施记录 2026-09-05 深夜，v0.5.0.0~0.4-M4）

**批1 背景网格（v0.5.0.0）**：`warp_grid.shader`（仿 snap_grid 逐像素网格 + 双层正弦扭曲 + 呼吸脉冲，参数由 GridBackdrop 每帧 `Attributes.Set("WarpTime",…)` 推入——引擎 g_flTime 在可见源无声明不赌）**编译通过、渲染成功**，但与尖刺同屏时绿色系表现不稳（见批4 备注），**用户拍板先禁用**：GridBackdrop 回退静态线网格，shader 文件保留在 `Assets/shaders/warp_grid.shader`（含 `.shader_c`，以后想开随时换回）。cr_arena_size 改动仍会触发网格重建（OnUpdate 检测半宽变化）。

**批2 音效（v0.5.0.1）**：6 个 8-bit 风格 WAV（Python stdlib 合成，`Assets/sounds/cr_*.wav`）+ 6 个 `.sound` SoundEvent（3D 定位、PitchRandom 0.06-0.1、距离衰减 2600-3600）——**编辑器自动导入链 wav→vsnd_c→sound_c 全通**。`GameSfx` 静态助手（try/catch 静默缺资源；`IsMine` 判定"本机耳朵"——客户端用本机球 OwnerSteamId 比对，双开合成 ID 兼容）。触发点：吃食物（高频，只播自己的+0.06s 限频；`FoodEaten` RPC 加 eaterSteamId 参数）、吞球/死亡爆裂（全场 3D，随 PoppedEffect 路径）、分裂/吐孢子（owner 按键即时播，按质量门槛预判防空响）、重生（owner 检测 Alive 翻转）。⚠️ **待办**：双开需要 sbproj `Resources` 加 `/sounds/*`（须编辑器关闭时改，同 M2 教训）。

**批3 数值平衡 + bot 难度（v0.5.0.2）**：`BotDifficulty` 三档（新兵 50%/老手 35%/王牌 15%，host 本地随机不联网）：决策间隔 0.40/0.25/0.16s、感知范围 ×0.6/×1.0/×1.35、瞄准噪声 ±0.50/±0.18/±0.05rad（低难度打不直）。数值微调：FoodCount 400→460、MinSpeed 130→145、BotRespawnSeconds 1.5→2.0（其余曲线未动，等实测反馈）。

**批4 房间 ConVar + 死亡观战（v0.5.0.3）**：`cr_bots`（场上目标球数，重进 Play 生效）与 `cr_arena_size`（场地半宽 1024-4096 钳制，**ConVarFlags.Replicated 自动下发客户端**——官方 ServerSettings 同款）；GridBackdrop/边界检测半宽变化自动重建（Replicated 值晚到也不怕）。死亡观战：本地球阵亡后镜头平滑跟住总质量榜首（`SpectateTarget`），重生瞬间 SnapTo 弹回自己；死亡面板不变。API 备忘：`Networking.HostConnection` 已废弃改 `Connection.Host`；`MathF.AlmostEqual` 无三参重载。

**用户定稿（v0.5.0.4）**：尖刺渲染改**紫色** (0.85,0.45,1)——排查发现绿色系顶点色在此管线**帧间不稳定**（同一代码 绿(0.32,1,0.42) 一帧渲染品红 RGB(165,48,181)、另一帧又有绿色星形；纯红/紫/青/品红始终稳定；头像贴图(采样路径)的绿色始终正常）。根因未明（Bloom/ACES/顶点格式均嫌疑），已按"品红族稳定"绕过；对照实验截图已清理。**教训：霓虹线框管线里优先用品红/紫/青/白这些已验证稳定的颜色。**

M4 验收待办：双开（含 /sounds/* 补入后）互听音效、convar 实测、15 分钟长跑；Publish 打包按需。

**批5 主菜单（v0.5.0.5）**：`MainMenu`（纯 C# Panel + MainMenu.cs.scss，同 GameHud 模式）——**CIRCLEROYALE** 霓虹标题 + HOST GAME / JOIN GAME / QUIT 三按钮，鼠标点击与键盘 1/2/3 双通道（编辑器内嵌视口鼠标不可靠，键盘保底）。架构改动：EnsureWorld 只建**壳**（世界根 + 背景 + 相机 + 菜单 + NetworkManager 监听器），`_gameStarted` 门控全部模拟；HOST 点击 → `RunHostFlow`（建大厅+开局），JOIN 点击 → `StartJoin("local")` → Tick 里等 `IsActive && !IsHost` 翻转再开局（连接前几帧 IsAuthority 恒为 true 不可信，实测教训）→ 12s 超时回菜单提示重试。**双开启动竞态顺势消灭**：旧流程开机自动建大厅，第二实例必须赶在 CreateLobby 完成前连上才有客户端身份；现在身份由点击决定。QUIT 编辑器内仅提示（standalone-only）。热重载回菜单属预期（系统实例重建）。

**批6 菜单扩充（v0.5.0.6，用户需求）**：新增 **VS BOTS [3]**（人机对战：`RunHostFlow(createLobby:false)` 不建大厅离线开局，键盘 1/2/3/4）；**JOIN [2] 升级为快速加入**——`Networking.QueryLobbies()` 查询本游戏大厅 → **加入人数最多且未满的房间**（官方 MenuHelpers 同款 `TryConnectSteamId(lobby.LobbyId)` 路径），一个能进的都没有/失败 → 回退连 local。⚠️ API 坑：`LobbyInformation` 是**结构体**（影子编译实锤），要用 `LobbyInformation?` + `.Value`；命名空间 `Sandbox.Network`（同 LobbyConfig 需全限定）。

**批7 尖刺定稿（v0.5.0.7，用户需求）**：①渲染**实心化**——星形填充从 0.30 拉到 0.85，加色混合下内部完整发亮（原来只是描边+淡芯）；②**撞刺碎片短冷却**——`CellPiece.MergeCooldown` 改为逐碎片字段（主动分裂仍 15s），撞刺炸出的碎片一律 3s（`SpikePieceMergeCooldown`），被炸掉的量主人很快能吃回来，别人抢食窗口大幅缩短。

**批7.5 菜单按钮丢失修复（v0.5.0.8）**：VS BOTS 按钮加了 C# 忘加 scss 定位——绝对定位没有 top 渲染到看不见处（"按钮消失"）。教训已入记忆：往 Panel 加元素必须同步补 .scss 定位规则。

**批8 背景音乐（v0.5.0.9，用户供曲）**：主菜单 = `chiptune1.mp3`，战斗 = `battle.music.mp3`（用户放入 Assets/sounds/music/，编辑器自动导入 vsnd）。`GameMusic` 静态助手：**走 .sound SoundEvent（UI 标志 = 2D 平面声）+ `Sound.Play(path)`**——⚠️ `ResourceLibrary.Get<SoundFile>` 直接取导入 mp3 返回 null（实测），SoundEvent 是已验证链路；**循环 = Tick 检测 `Finished/IsStopped` 手动重播**（SoundHandle 无循环标志）；切曲旧曲 1s 淡出；音量 `cr_music_volume`（默认 0.4）控制台实时可调。触发：壳建好 → PlayMenu；StartGame → PlayBattle（客户端连上后同样切）。Tick 挂在 `_gameStarted` 门控**之前**（菜单阶段也要循环）。

**批9 音频平衡（v0.5.1.0，用户反馈：音乐太大、音效太小、音乐该走 Music 通道）**：①音乐路由 **Music 混音器**（内置树 Master→Music/Game/UI/Voice，`Sandbox.Audio.Mixer.FindMixerByName("Music")` + `handle.TargetMixer`；⚠️ Mixer 在 `Sandbox.Audio` 命名空间，不在全局）；音乐默认音量 0.4→**0.22**。②音效加大：6 个 .sound Volume 上调 ~60%（eat 0.55 / eat_ball 0.85 / pop 1.0 / split 0.75 / eject 0.6 / respawn 0.8）。③音效先天偏小的根因：**音频监听器在相机上（球上方 1000 单位）**，3D 音效自带 1000 的先天距离，在 2600 衰减半径里已吃掉近半——衰减 Distance 2600→8000，自身事件≈全音量，远处依旧空间衰减。

**批10 音效 2D 定稿（v0.5.1.1~2，用户需求）**：①Music 混音器音量**减半**（`_musicMixer.Volume = 0.5f`，绝对值赋值防热重载叠加）；②**全部音效改 2D 平面声**（6 个 .sound 加 UI 标志，免距离衰减免空间化）——根因③的相机高度衰减问题就此整体绕过，音效只受 Volume 影响，什么位置听到都一样响。

**批11 分身头像+名牌（v0.5.1.3，用户需求：便于区分归属）**：①**bot 独立假 SteamId**（`BotSteamIdBase=91000…` 递增）——分身归属解析的前提（此前 bot 共用 0，分身分不清是谁的），顺带修掉"一个 bot 撞刺占满全体 bot 分身槽"的隐性 bug；②**分身头像 = `PieceAvatarLayer`**（挂在 Neon GO 上随快照复制到客户端）：本地非联网 GameObject 群跟随 `DrawPos`（15Hz 快照镜像驱动，零额外同步），贴图按主人解析（真人 `Texture.LoadAvatar` 缓存+1s 重试+15 次占位图兜底；bot 走 BotAvatar 几何图案），槽按分身 Id 稳定绑定池化（48）；③**分身名牌 = GameHud 投影池扩展**：`Place()` 局部函数统一球/分身的投影+滞回逻辑，分身名字经 `FindBallBySteamId` 取主人球名，名牌池扩到 80（球+分身共用），分身用独立槽表（Key=分身 Id）。

**批12 尖刺 agar 化 + 分裂可靠性（v0.5.1.4，用户需求）**：尖刺规则改为 **agar 病毒**：细胞**比刺小**→碰到即被刺吃（玩家死亡/分身消失；出生保护期免疫）；**比刺大**→把刺吃掉（+100 质量）并**强制触发分裂**（DoBurst 连环炸），刺在别处重生（`RespawnSpike` 重用 SpikesFull 广播，场上恒 12 颗）；孢子喂刺不变。`SpikeBurstRatio` 门槛废弃（不再有"安全穿行"），**BotBrain 改为无条件避刺**。分裂技能可靠性：输入判定从 `OnFixedUpdate`（固定 50Hz 步进，可能错过 Input.Pressed 渲染帧边沿→"按了没反应"）移到 **OnUpdate 每帧路径**；DoSplit 加 host 日志（`[game] split ...`）便于远程诊断。

**批13 出生保护闪烁（v0.5.1.5，用户需求）**：保护期整球**一闪一闪**（方波 3Hz，`SpawnBlinkHz` 可调）——外环/内芯/护盾虚环同步闪烁，暗相保留 35% 保持可见；保护结束恢复常亮。

**批14 门槛下调（v0.5.1.6，用户需求：质量与吐球最低 1）**：分裂门槛 64→**2**（对半分完两半各 ≥1，质量下限 1 不击穿）、吐孢子门槛 36→**1**（可用质量 = 1+12 = 13，吐完恰好落在下限 1）。开局即可分裂/吐球；速度曲线不变（本来就越大越慢：起始 560 → 封顶质量约 145，由 `BaseSpeed/SpeedCurve/MinSpeed` 决定）。

**批15 分身标识失效修复（v0.5.1.7，用户实测反馈）**：截图实锤"分身无头像无名牌"——根因是**存量 bot 的 OwnerSteamId=0**（v0.5.1.3 前生成的 bot 不受新代码影响，热重载不改已存在对象的同步值），分身按 SteamId 反查主人落空。修复：①**ScanBalls 存量 bot 自愈**——host 检测 `IsBot && OwnerSteamId < BotSteamIdBase` 补发唯一假 id（[Sync] 自动下发，`NextBotSteamId` 扫描查重防热重载序列重置碰撞）；②**分身头像 GO 改顶层本地对象**——不再挂 Neon GO（客户端上它是 host 拥有的网络对象，往里挂子物体有静默失效风险，M2 reparent 教训同源）。

**批16 合并手感（v0.5.1.8，用户需求：能合并、冷却 1 秒）**：①合并冷却 15s → **1s**（撞刺碎片同步统一为 1s）；②合并判定从"分身中心嵌进主球"改为**圆缘相触即并**（dist < 主球半径+分身半径；分身互并同改）——用户实测"靠在一起不动不算"的观感修复。注意物理副作用：分身与主球同速同向时差距恒定不会自然并回，需要玩家主动贴上去（与 agar 一致）。

**批17 多身体规则（v0.5.1.9，用户需求，对齐 agar）**：①**R 键所有身体一起吐**——DoEject 重构为遍历主球+每个够质量（≥13）的分身，各从自己边缘弹一颗（SpawnBlob 抽取公共路径，总量上限逐颗收敛）；②**分身护体**：吃分身只丢那一份质量；主球在还有分身时**吃不动**（TickEating 的球×球、分身反吞主球两处加 `CountPiecesOf>0` 拒绝），死要死在最后一个身体上；主球有分身护体时画**常亮暗护盾虚环**（区别于出生保护的闪烁亮环，`DrawShieldRing` 抽取公用）。尖刺例外：主球碰刺仍然直接死（刺是地形级威胁，不受护体保护）。

**批18 对齐设计稿手感（v0.5.1.10，用户贴 agar 设计稿后按优先级落地）**：
①**体积惯性曲线**：转向响应不再固定 8/s，改为 `BaseTurnSpeed(8) × (StartMass/Mass)^TurnCurve(0.2)`、下限 2——6400 质量时约 2.2/s，大球明显笨重必须提前预判走位；**主球与分身同曲线**。
②**吐球改百分比**：每颗孢子消耗 = `max(1, 质量×1%)`，孢子返还 80%（`EjectMassPercent/EjectBlobEfficiency`）——喂球随体量缩放，"输送"有层次；质量下限 1 由 EjectCost 统一保证（击穿则拒绝）。
③**分身上限 8→16**；名牌池 80→160、分身头像池 48→96 同步扩容。
④**倒流引力（秒合的物理基础）**：冷却已过且（**分身比主球重** 或 出生超 8s）的分身，转向目标混入朝主球的引力（`PieceAttractWeight=1` 与摇杆等权混合）——"吐球减重让分身比你重→分身倒流"的技巧由此成立。**分身独立操控不做**：纯键盘/手柄方案下没有自然的逐细胞输入映射（设计稿是指针方案），维持全队同向。

**批19 主菜单鼠标化（v0.5.2.0，用户需求）**：菜单鼠标全死的根因：①scss 写了非法值 `pointer-events: normal`（引擎只认 **auto/none/all**，非法被忽略）；②**s&box 面板默认 PointerEvents=None**——没有任何面板"WantsMouseInput"（=ComputedStyle.PointerEvents==All），引擎的 `DoAnyPanelsWantMouseVisible` 返回 false → 光标不显示、点击无命中。修复：根面板与按钮改 `pointer-events: all`；**代码保险 = 菜单 OnUpdate 每帧 `Mouse.Visibility = MouseVisibility.Visible`（public static 可写），Hide() 恢复 Auto**（游戏期 HUD 是 none，光标自动隐没）。

### M4 — 打磨与发布准备
任务：
1. 背景扭曲网格 shader（仿 `snap_grid.shader` 加正弦扰动）。
2. 音效（`Sound.Play`：吃、成长升级、死亡、分裂）。
3. 数值平衡（速度曲线、食物密度、bot 难度分档）。
4. 房间设置（bot 数量、地图大小 ConVar）、死亡后短暂观战（跟随第一名）。

验收： strangers 可玩——建公开大厅即可被加入；一局 15 分钟无崩溃；打包 `Publish` 可分发（如需）。

---

## 4. 技术设计

### 4.1 架构（数据流）

```
[host Tick: CircleroyaleGame (GameObjectSystem, Stage.UpdateBones)]
  食物重生 → 吃判定(球×食物/球×球) → 死亡/成长 → [Sync(FromHost)] 下发
  bot 数量管理 → 排行榜打包 → PoppedEffect RPC
        ↑ 位置/输入来自各 Ball（owner 模拟，transform 自动同步）
[各客户端]
  Ball(owner) 读鼠标本地模拟移动 → NeonRenderer 画帧 → HUD 读 Sync 值
```

### 4.2 网络清单

| 成员 | 位置 | 类型 | 写者 |
|---|---|---|---|
| `Mass` / `PlayerName` / `Alive` / `IsBot` / `ColorIndex` / `OwnerSteamId` | Ball 组件 | `[Sync(SyncFlags.FromHost)]` | host |
| `Foods` 全量 | NetworkManager 静态 RPC | `[Rpc.Broadcast] FoodFull(FoodData[])` + `Rpc.FilterInclude` 定向 | host→新连接 |
| `Foods` 增量 | NetworkManager 静态 RPC | `[Rpc.Broadcast] FoodEaten(idx)` / `FoodRespawned(idx,FoodData)` | host→全体 |
| `RequestFoodFull()` | NetworkManager 静态 RPC | `[Rpc.Host]` | 客户端→host |
| `RequestRespawn()` | NetworkManager 静态 RPC | `[Rpc.Host]`（`Rpc.Caller` 定位） | 客户端→host |
| `RequestSplit( Vector2 dir )` | Ball | `[Rpc.Host]`（预留，M3 启用） | 客户端→host |
| `RequestEject( Vector2 dir )` | Ball | `[Rpc.Host]`（预留，M3 启用） | 客户端→host |
| `RequestUseItem( int type )` | Ball | `[Rpc.Host]`（预留，道具系统） | 客户端→host |
| `PoppedEffect(...)` | CircleroyaleGame | `[Rpc.Broadcast(NetFlags.Unreliable)]` | host→全体 |
| 位置/旋转 | Ball 的 Transform | 网络 transform（引擎默认插值，owner 写） | owner |

`FoodData` 结构预留道具通道：`{ Vector3 Pos; byte ColorIndex; bool Alive; byte Type }`——`Type=0` 普通食物，`1..n` 道具类型，道具复用食物的生成/同步/拾取管线。
| 位置/旋转 | Ball 的 Transform | 网络 transform（引擎默认插值） | owner |

### 4.3 核心数据结构与流程

```csharp
public struct FoodData { public Vector3 Pos; public byte ColorIndex; public bool Alive; }
```

- **Ball 控制源抽象**：`Ball` 内部 `Vector2 GetMoveDir()` —— 真人 = 本地键盘/摇杆方向（仅 `!IsProxy && !IsBot` 时读 Input）；bot = `BotBrain` 每 0.25s 给出的方向。host 校验只针对真人。
- **死亡=Alive 状态位**（M2 收官定稿）：球对象永不销毁；被吃 = host `MarkDead()`（`Alive=false` 经 `[Sync(FromHost)]` 下发，渲染/AI/判定全部跳过，头像精灵在 `OnUpdate` 里随 Alive 隐藏）；重生 = host 置回 `Alive=true`，owner 端检测翻转后自行随机换位 + 保护期。销毁重建路线废弃（进行中的 NetworkSpawn 不复制给已连接客户端，实测）。
- **Tick 顺序**（同一 Tick 内固定序）：食物重生 → bot 决策 → 位移校验 → 吃判定 → 榜单/事件。

### 4.4 渲染批次（NeonRenderer，每帧重写顶点）

| 批次 | PrimitiveType | 内容 | 材质 |
|---|---|---|---|
| Lines | `Lines` | 网格背景（静态可一次写入）、边界、球外圈（多边形环）、绿刺星形、孢子 | `shaders/line.shader`（加色） |
| Triangles | `Triangles` | 食物小圆扇、球内芯（低透明度）、出生保护罩 | 同上 |
| 拖尾 | TrailRenderer 组件 | 每球一条 | 组件自带 |

顶点估算：400 食物×8 三角 + 20 球×32 线段 + 网格 200 线 ≈ 每帧万级顶点，CPU 写入无压力。

### 4.5 输入映射（默认动作表，无需改 Input.config）

**操控 = 仅键盘 + 手柄**（2026-09-05 用户决策，鼠标操控已移除）。数据源 `Input.AnalogMove`（Vector3：x=前(+)/后(-)、y=左(+)/右(-)）——引擎已把 WASD（模板 Input.config 预定义动作）与手柄左摇杆自动叠加（`ComputeAnalogMove` + 控制器 overlay），死区 0.2 过滤摇杆漂移；顶视相机下 前→相机Up（屏幕上）、左→-相机Right（屏幕左），归一化输出。分裂 = "Jump"(空格)；吐孢子 = **"Reload"(R / 手柄 X)**（M3 修订：原定 "Forward"(W) 是移动前进键，按住移动会持续吐孢子，冲突不可用；R 不与 WASD 冲突且手柄有映射）；重生按钮 = HUD onclick。
（历史：鼠标方案两版都实测不可靠——`Panel.MousePosition` 在编辑器内嵌视口冻结；`Sandbox.Mouse.Position` 直连可用但坐标系与 `Screen.Width/Height` 在 Play-in-editor 下存在错位偏移。既定全键盘/手柄操控，不再回头踩。）

头像圆心（提前到 M0 落地）：`SpriteRenderer`（子物体 Avatar）+ `Sprite.FromTexture`（⚠️ `SpriteRenderer.Texture` setter 是过时空操作）；贴图优先 `Texture.LoadAvatar( Game.SteamId.Value, 128 )`（编辑器内即返回本地 Steam 头像，实测可用），失败回退 `Assets/ui/avatar_placeholder.png` 占位缩略图。

### 4.6 预留接口设计（分裂 / 吐孢子 / 道具）

输入与网络管线一次到位，玩法逻辑用 `GameConfig.EnableSplit / EnableEject / EnablePowerUps` 开关控制，开启时不改架构只填逻辑：

- **分裂/吐孢子**：`Ball.OnFixedUpdate` 的输入钩子已预留（`Input.Pressed("Jump")` / `Input.Down("Forward")`）→ `RequestSplit/RequestEject`（`[Rpc.Host]`）→ host 校验（质量门槛/分身上限/冷却）→ `CircleroyaleGame.DoSplit/DoEject` 执行。Ball 结构上把"控制实体（玩家）"与"细胞实体"分离，M3 加 `Cells` 列表即可支持多分身，不改同步通道。
- **道具系统**：生成/同步/拾取复用食物管线（`FoodData.Type`）；效果用注册表模式——`IPowerUp { void Apply( Ball ball ); void Expire( Ball ball ); float Duration { get; } }`，按类型 id 注册实现类，新增道具 = 新增一个实现类 + 登记进 `GameConfig.PowerUpSpawnTable`，不动 manager；生效期间 host 给 Ball 挂临时 modifier（速度提升/护盾/磁吸食物等）。
- **球预制体**：`Assets/entities/ball/ball.prefab`，结构：根=Ball 组件，子 `Avatar`（头像四边形，M3）、子 `NameTag`（WorldPanel 名牌，M3）。代码用 `GameObject.Clone( "entities/ball/ball.prefab", transform, parent )`（static 重载已核实 `GameObject.Clone.cs:327`），**路径不存在时回退纯代码构建**——预制体缺失不阻塞开发，用户随时可在编辑器补做并调样式。

---

## 5. 文件清单（含里程碑归属）

```
Code/
├── Assembly.cs                        # （已有）global using
├── Game/
│   ├── GameConfig.cs                  # M0 全部可调常量
│   ├── CircleroyaleGame.cs            # M0 世界骨架 / M1 判定主循环 / M2 榜单+事件
│   ├── NetworkManager.cs              # M2 建服/审批/生成球/断线/bot 补位
│   ├── FoodManager.cs                 # M1 本地列表 / M2 NetList 化
│   └── PowerUpManager.cs              # 预留：道具生成/拾取/到期（M4+ 启用）
├── Player/
│   ├── Ball.cs                        # M0 移动 / M1 成长死亡 / M2 同步属性+校验 / M3 分裂
│   └── BallInput.cs                   # M0 鼠标方向 / M3 分裂吐孢子输入
├── Bot/
│   └── BotBrain.cs                    # M1 FSM / M2 与真人统一生成入口
├── Rendering/
│   ├── NeonRenderer.cs                # M0 双批次框架 / M1 接入实体 / M3 特效挂钩
│   ├── GridBackdrop.cs                # M0 静态网格 / M4 扭曲 shader
│   └── NeonCamera.cs                  # M0 跟随 / M1 按质量缩放 / M4 观战跟随
└── UI/
    ├── GameHud.cs / GameHud.cs.scss   # M0 占位 / M1 完整 / M2 网络数据
    └── DeathPanel.cs                  # M1 死亡重生（并入 GameHud 亦可）
```

球走**预制体优先**（用户在编辑器创建 `ball.prefab`，代码按路径 Clone、缺失回退代码构建，见 4.6）；食物/特效等纯程序化实体仍直接代码生成。实体生成沿用 tiaotiao 模式：`static` 世界根 + 场景归属校验复用、`Scene.Children` 屏蔽模板残留。

---

## 6. 测试与验证流程（每个里程碑执行）

1. 保存 → 等 5~8s → MCP `compile_status` 确认编译成功（失败则先修）。
2. `play_start` → `read_console` 查红字 → `camera_screenshot` 看 3D 画面（HUD 以真实视口为准）。
3. M2 起双开：第二个实例 `sbox-dev.exe -project D:\sbox\circleroyale` + 控制台 `connect local`；压力测试用 `net_fakelag 100` / `net_fakepacketloss 5`。
4. 热重载后行为异常 → 停止 Play 重进再验一次（tiaotiao 已知坑）。
5. 长跑测试：M1 起 10 分钟挂机（bot 自吃），查日志无错误累积。

---

## 7. 风险与对策

| 风险 | 等级 | 对策 |
|---|---|---|
| `NetList<FoodData>` 结构同步体积/性能不达标 | 中 | FoodData 压缩（ushort 坐标）；退路：拆"位置静态列表 + Alive 位图"两份同步 |
| owner 移动校验误伤（重生/瞬移/卡顿） | 中 | 校验放宽到 1.5×max 且白名单 host 主动位移；异常只拉回不清退 |
| SceneDynamicObject 材质 combo（加色）不生效 | 低 | 退路 `Material.Load("materials/gizmo/line.vmat")`（引擎调试线同款，效果已验证） |
| 热重载世界堆叠/状态残留 | 低（已知） | static 根 + `Scene==Scene` 校验复用（tiaotiao 成熟模式） |
| 双开测试环境问题（Steam 登录/端口） | 低 | `net_allow_local` 回环 + `connect local`；必要时 `LobbyPrivacy.Private` |
| bot 与真人行为不一致（两套控制代码） | 低 | 控制源抽象进 Ball（GetMoveDir），AI 只产出方向向量 |
| SB1000 白名单踩线（反射等） | 低 | 已知禁用清单（RESEARCH.md §7.1），不引入反射/RenderSettings |

---

## 8. 工作量粗估

| 里程碑 | 估时（代理工作会话） | 依赖 |
|---|---|---|
| M0 骨架 | 1 | 无 |
| M1 单机核心 | 1~2 | M0 |
| M2 联机 | 2 | M1 |
| M3 进阶 | 1~2 | M2 |
| M4 打磨 | 1~2 | M3 |

关键路径是 M2（联机）；M1 的规则函数全部设计为"host 可直接复用"，M2 只加同步壳，不重写逻辑。
---

## 9. ★★ M5 比赛规则 / 击杀播报 / 结算排行（2026-09-07，v0.6.0.2-M5，MCP 实机验收通过）

用户需求五项全落地（编译 0 错 0 警 + 单机 20s 赛程全链路截图验收）：

1. **host 规则设置界面**（MainMenu）：菜单底部 MATCH SETTINGS 区三项，点击循环切换——
   MODE（FFA / TEAM 3人队）、TIME（5/8/12/15/20 分钟）、PLAYERS（12/20/32/48）。
   值存 `MatchState.Pending*`（静态，热重载保留）；只有 HOST/VS BOTS 开局生效。
2. **比赛规则**（MatchState 静态类 + 静态 RPC 同步）：
   - 普通赛：各自为战；团队赛：轮转分队（真人/bot 一视同仁，`Ball.TeamIndex` [Sync(FromHost)]），
     **胜负按队伍 3 人总分**（结算时按 Team 聚合 Mass）。团队赛球色锁队伍色（Palette 前 6 色循环）。
   - 一局 12 分钟默认；host `TickMatch` 权威倒计时，1Hz `MatchTick`（Unreliable）校时 + 各端本地续走；
     归零 `EndMatch` → 按总质量（主球+分身）排序 `ScoreWire[]` → `MatchOver` RPC 广播。
   - 规则经 `MatchSettings` RPC 下发（开局广播 + OnActive 定向补发，中途加入也对表）。
   - MatchOver 后全场冻结：Tick 模拟段、Ball.OnFixedUpdate、分裂/吐孢子输入、重生请求全部拦截。
3. **局内规则 UI**（GameHud）：顶部中央 `模式 + 倒计时`（最后 30 秒变红；**分钟必须截断取整——
   "{t/60f:0}" 会四舍五入把 44 秒显示成 "1:44"**）；团队赛下方队伍总分条（前 5 队 + 本队，
   队色渲染，本队白色加亮）；排行榜行加 [T#] 前缀。
4. **击杀播报**（谁吃了谁）：`BallEaten` RPC 加 massGained 参数，双端统一走
   `CircleroyaleGame.OnBallEaten` → `GameHud.AddKillFeed`——左上角最多 6 条、4.5s 移除、尾段淡出；
   自己吃人金色（+质量），被吃红色；分身反吞主球也走同款播报。
5. **结算面板**（EndBoard，独立 ScreenPanel）：全屏压暗，MATCH OVER + 冠军行（团队赛=队伍总分）+
   本局排行逐行滑入动画（0.22s 逐行 stagger + 透明度/位移渐入，榜外自身行青色高亮）+
   GLOBAL TOP 10 + BACK TO MENU（点击/空格 → `ResetToMenu`）。
   - **上传**：各端各自 `Stats.SetValue("cr_match_mass", 本局总质量)` + `Flush()`（实测云后端收单成功）；
   - **读取**：`Leaderboards.GetFromStat` → SetAggregationMax/SortDescending/FilterByNone/MaxEntries=10
     → `await Refresh()` → Entries（**实测未发布包也拿到了真实云端数据**，全服榜=真人最高单局）；
   - **兜底**：云端空/异常 → `LocalBoard`（FileSystem.Data JSON，同 SteamId 留最好成绩）。
     ⚠️ **Sandbox.Json 序列化只认公共属性**：私有嵌套类+公共字段写出 48 个 `{}`（全 0 空榜实测）——
     Entry 必须顶层公共类 + `{ get; set; }`。
   - `ResetToMenu`：断网（客户端 Disconnect / host 停大厅）、清球/分身/比赛态、菜单重出、
     世界壳留作背景；`StartGame` 复用已有 HUD（不重复建），`NetworkManager.ResetSession` 复位
     _hosted/看门狗。回菜单后可再次 HOST/JOIN（规则设置保留上次选择）。

验收记录（MCP 单机冒烟，20s 赛程）：菜单设置区渲染/循环点击 ✓；倒计时/团队比分条/[T#]榜前缀 ✓；
击杀播报（他人白、被吃红）✓；结算动画 ✓；云端上传+读取 ✓（"1. 飞丶鸟 10"）；本地榜兜底 ✓。
旧 `cr_bots` convar 退役（人数改由菜单 PLAYERS 控制）。双开联机（规则下发/结算广播/各自上传）待实测。

### M5.1 游戏房间二级界面（v0.6.1.1-M5，用户补充需求，MCP 截图验收通过）

用户需求：点 1 HOST / 2 JOIN 后进**游戏房间**页——调参、看玩家列表；host 点 START 才开局；
客户端在房间里"等待游戏开始"。另：TEAM BATTLE 倒计时移到队伍条下方（队伍条位置不动）。

- **LobbyPanel**（独立 ScreenPanel）：GAME LOBBY 标题 + 状态行（ROOM OPEN — N PLAYERS /
  WAITING FOR HOST TO START）+ MATCH SETTINGS 三项（host 可点击循环切换，client 只读展示）+
  PLAYERS 名单 2 列×12 行（0 号房主青色 (HOST) 标注）+ START GAME（仅 host）+ LEAVE（双方）。
- **房间状态机**：HOST/VS BOTS → `OpenHostLobby`（藏菜单亮房间）→ RunHostFlow 建完会话调
  `OnLobbyOpened`（**蛰伏球**：host 球 + 48 预备 bot，Alive=false 不可见不模拟——引擎
  "进行中 NetworkSpawn 不复制到已连接客户端"，球必须在各端初始快照前存在）→ host 点 START →
  `StartMatchFromLobby`：规则生效/投食/尖刺/**激活蛰伏球**（远程真人+host 先激活，bot 按剩余名额
  补齐到 PLAYERS 目标，多余 bot 保持蛰伏）+ **队伍分配在激活时做**（房间阶段模式未定）+
  对等在房间的客户端定向补发食物/尖刺 + MatchStarted 广播。
- **客户端流**：JOIN 连上 → `_lobbyWaiting` → 亮房间（等待房主开局）；房间信息经 `LobbyState`
  RPC 推送（名单/参数变化置脏标主线程统一广播；新连接 OnActive 定向推）；对局中途加入 →
  OnActive 直接推 MatchSettings+MatchStarted（跳过房间直达游戏）。房主断线 → 客户端
  `!Networking.IsActive` 自动回菜单提示 HOST LEFT THE LOBBY。
- **蛰伏球约束**：`SpawnBall(dormant:true)` 必须在 NetworkSpawn 之前置 Alive=false（随初始快照
  下发）；蛰伏球重生走 RespawnBall(teamIndex)——团队分配只在开局激活时执行。
- **HUD 布局定稿（用户）**：队伍条 top 52px 不动；模式+倒计时移到 top 84px（队伍条正下方）。
- 菜单设置区已摘除（设置移进房间）；冒烟钩子（自动开房/自动 START）已摘除。
- 背景网格"消失"误报 = 热重载中间态（世界根重建拆掉 GridBackdrop，OnStart 不重跑），
  重进 Play 即恢复，非回归。

### M5.2 名牌"消失"排查（v0.6.1.3-M5，2026-09-07）——系统被证明无恙，教训在取证工具

用户报告"不显示 bot 名字和网格线"（附截图）。排查结论：
- **投影数学被引擎自证正确**：临时诊断让 `cam.PointToScreenPixels(ball)` 与名牌的 ScreenToWorld 仿射
  逆推同框对比——5 颗样球 **X 坐标 5/5 精确一致**、Y 一致（引擎只把出屏值钳到边缘），同一帧
  `shown=8`（8 个名牌正常显示中）。名牌定位系统无 bug。
- **MCP 截图取证假象**：`camera_screenshot` 按 width/height 重渲染 3D + UI，而名牌按"活动面板
  （1845×912）"布局摆放——截图画布更小（默认 1280×720）时超界名牌被裁掉，于是截图只见 2 个
  名牌而实况 8 个。**MCP UI 截图不可信的又一实锤：只能当存在性参考，不能当位置/数量证据。**
- **用户截图实为 sbox.exe 客户端**：文字全部衬线体（Inter/SimHei 都没加载 = 无项目资产），
  host 编辑器不会如此。客户端 07:55 由 launcher 启动。其"名牌只剩一个"发生在缺资产/可能
  旧程序集的客户端上，host 侧无法远程调试（客户端无 MCP）。
- **网格线在两张截图里都可见**（含客户端）——**推翻 v0.5.2.5 "客户端解析不到 line.shader" 的
  旧结论**（core shader 在 sbox.exe 上可用）；截图右侧黑带 = 3D 视口未铺满窗口（UI 面板延伸
  到黑带上，排行榜画在那里），属窗口/停靠布局观感，不是网格丢失。
- 转发给用户的可靠双开路径：**第二个编辑器实例** `sbox-dev.exe -project D:\sbox\circleroyale`
  （直读磁盘资产、始终最新构建、无下载问题），sbox.exe 客户端留待真实大厅下载链路验证。

### M5.3 名牌丢失真修复（v0.6.1.4-M5，2026-09-07）——改用引擎逐球投影

M5.2 的诊断（引擎 PointToScreenPixels vs 手推 ScreenToWorld 仿射标定同框对比）抓到实锤：
- **活球跟随状态（相机贴自己、正常变焦）：两套投影一致、名牌全好**；
- **死亡观战状态（相机拉到 3800 最远、长距离滑向榜首）：屏内球 bound=True 却 shown=False
  持续数秒**（如 Flux eng=(630,227) 完全在屏内却不显示）——旧标定在"相机快速远滑+最大变焦"
  时与渲染实际状态脱节（ScreenToWorld/PointToScreenPixels 采样时机与渲染帧相机状态存在
  帧间差），且边缘滞回的 Shown 状态会卡在过期值。

**修复**：UpdateNameTags 弃用整套 ScreenToWorld 三点标定，改用 `cam.PointToScreenPixels(worldPos)`
逐球投影（引擎自己的投影，与渲染同源不可能漂移），面板坐标=视口像素/RootPanel.Scale；
顺带：①去掉边缘滞回（每帧纯 in-view 判定）；②死亡/蛰伏球不再挂名牌（消灭浮空尸名）。
同尺寸（1845×912=活动面板）截图验证：死亡观战状态下所有可见球名牌齐全；日志同秒 shown 全 True。

**取证教训（更新）**：MCP camera_screenshot 的 includeUi 按请求尺寸重渲染 UI，与活动面板
（真实窗口）布局不一致时会裁掉/错位 UI 元素——**必须用 width/height=真实面板尺寸截图才有
UI 取证效力**（Box.Rect.Size 可从诊断日志拿到）。此前"名牌只剩 2 个"的截图有一半是这个假象，
另一半（用户手拍图）是热重载窗口期 + 相机状态的真实回归。

### M5.4 agar 多身体规则（v0.6.2.1-M5，2026-09-07，用户定稿 + 实机验证通过）

用户设计：**视角跟随自己最大的身体；单个身体被吃/撞刺只损失那个身体；全部身体死光才算死**。
取代 v0.5.1.9 批17 的"分身护体 + 主球死亡即全灭"规则。

- **晋升机制**（TryPromoteFromPieces）：主球被吃/撞刺时，最大的分身"升级"成新主球——
  球对象原地续命（Mass=分身质量、WorldPosition=分身位置、Alive 不翻转），该分身从列表移除；
  没有分身才走原死亡流（MarkDead → 死亡面板 → 空格重生）。吃球者仍获得被吃身体的全部质量。
  **Alive 不翻转** = 客户端永远看不到"死亡→重生"，不触发随机换位/保护期/重播音效（身份延续）。
- **远端同步**：远程玩家的球归其 owner 模拟，host 设置的位置会被主人写回——晋升时定向发
  `MainPromoted(x,y)` RPC，客户端 `OnMainPromotedRemote` → `Ball.TeleportTo`（瞬移+清速度，
  不触发保护期/音效）；质量经 [Sync(FromHost)] 自动到达。
- **摘除"分身护体"**：球×球、分身×主球两处护体拦截删除——任何身体都可被更大的敌人吃掉
  （agar 原版规则）；NeonRenderer 的护体暗环同步移除（出生保护闪烁环保留）。
- **三处死亡路径全部改走晋升流**：球×球主球被吃、分身反吞主球、主球撞刺。玩家死亡 = 最后一个
  身体也没了（此时 MarkDead → 死亡面板）。
- **视角跟随最大身体**（FocusPositionOf）：NeonCamera 焦点/变焦基准改取"主球+自己分身"中
  质量最大者（分身吃食物可以比主球大，镜头跟大不跟主）。
- **冒烟验证**（临时钩子：充能 400 + 三连分裂 → 200/100/50 三分身）：相机立刻切去跟 200 的
  最大分身 ✓；强制晋升 → "[game] promoted piece -> main: 飞丶鸟 mass=222"（分身期间吃的食物
  一并带入）✓；晋升后三个身体名牌齐全 ✓。临时代码已全部摘除。
- 断线清理（OnDisconnected）仍销毁其球+清分身 ✓ 不受影响。

### M5.5 性能优化 + 菜单背景 bot 对战 + 结算双列（v0.6.3.0-M5，2026-09-07，用户三项需求）

1. **性能**（3 倍场地后 200→~140fps 的根因）：食物 460→4140 颗导致两处 O(n²)——
   ①吃判定 O(48×4140)≈20 万次/帧 → **FoodManager 512 单位空间哈希桶**（Init 建桶/重生挪桶），
   TickEating 球×食物、分身×食物改桶粗筛+精确复核；②**NeonRenderer 食物视口剔除**
   （相机世界包围盒外的 4140 颗直接跳过，画面内只剩 ~20%）。球×球/bot 判定量级不变。
2. **主菜单背景 bot 对战**（用户："现在是个黑幕"）：authority 离线自动跑一场纯 bot 演示赛——
   StartMenuDemo（按菜单当前设置投食/尖刺/满编 bot，全部本地对象不进网络）；
   Tick 门控改为 `!_gameStarted && !_menuDemo` 才零模拟；演示赛时间到 EndMatch 就地重开
   （不走结算/广播）；菜单相机无本机球时观战全场最大身体（TopMassBall + FocusPositionOf）；
   MenuAction(0/1/2) 先 StopMenuDemo（清 bot、重置初始化标记，真实开局的 OnLobbyOpened
   重新投食）；ResetToMenu 后重新开演示赛。
3. **结算面板左右双列**（用户定稿）：MATCH RANKING 左列 + GLOBAL TOP 10 右列并排，
   标题/冠军/回菜单居中；NewRow 改带 left/width 参数。
- 截图验证：菜单背景 bot 对战运行中（bot/尖刺/食物/名牌全在）；热加载失败批次曾污染状态
  （旧世界+旧版本标签混合显示），停 Play 重开后干净。

### M5.6 名牌投影根治（v0.6.3.2-M5，2026-09-07，用户截图实锤驱动）

用户截图（死亡观战+相机滑屏中）再次实锤名牌消失，且黄色观战目标球不在屏幕中心
（粘滞观战生效中）。结合此前数据定位机制：**相机快速滑动时（死亡观战长距离滑屏、高倍变焦），
PointToScreenPixels/引擎查询使用的 sceneCamera 姿态比渲染帧滞后一帧**——高变焦下一帧即数千
世界单位，名牌全部飞出屏；相机静止时无帧间差（此前验证恰好都在静止态，故总是"好的"）。

**根治**：名牌投影不再查询引擎，改**解析计算**——相机为固定正交俯视（ApplyPose 写死旋转），
只用两个渲染真源：`相机 GameObject.WorldPosition` + `Cam.OrthographicHeight`，加固定轴向
（屏幕右=-Y、屏幕下=-X，两次实测确认）：px = W/2 − (wy−cy)/s，py = H/2 − (wx−cx)/s，s=Ortho/H。
**调用时机**：NeonCamera 在 ApplyPose（写姿态）之后立刻调 `Hud.UpdateTagPositions( camPos, ortho )`
——投影与渲染用同一份数据，结构上不可能脱节；变焦更新后再补投一次。OnUpdate 不再驱动名牌。

### M5.7 客户端加入流重构：房间 + 玩家列表同步 + 加入倒计时（v0.6.4.0-M5，2026-09-07，用户定稿）

用户设计与反馈：客户端加入游戏（任何时候）先进二级菜单（房间）并**同步玩家列表**；
host 开始游戏（或已开始）时，JOIN 流程显示**加入倒计时**再进入对局。参考官方
sandbox-main GameManager：OnActive 直接为连接创建/Spawn 玩家对象，不做复杂状态机。

- **中途加入也推玩家列表**：OnActive 对局分支现在按序推 LobbyState（房间页先填玩家列表）
  → MatchSettings → MatchStarted（倒计时触发）。
- **加入倒计时**（GameConfig.JoinCountdownSeconds=3）：客户端在房间里收到 MatchStarted →
  房间页大字 "JOINING MATCH IN 3→2→1"（LobbyPanel.ShowJoinCountdown，金色居中）→ 归零
  OnMatchStartedLocal 进入对局。给世界快照/食物/玩家列表留同步窗口。ResetToMenu 取消倒计时。
- 此前两个卡房间根因（v0.6.3.3/3.4）一并保留：OnMatchStartedLocal 置 _gameStarted=true
  （客户端状态位对称）；EnsureWorld 对"客户端+会话激活"不亮主菜单 + RequestMatchState
  幂等状态补发（重连/热重载/中途加入全走这条恢复路径）。

### M5.8 对局结束自动回 host 大厅（v0.6.4.2-M5，2026-09-07 用户定稿）

用户反馈：多人对局结束后不应停留在冻结场景，**所有人回到 host 的大厅（房间）**等下一局。

- **AfterSettlement**（结算 10 秒自动 / 空格提前，EndBoard 文案带倒计时 "RETURN TO LOBBY IN n"）：
  - 会话中（Networking.IsActive）→ **ResetToLobby**：会话保留不清——host 回房间控制端
    （_lobbyOpen=true、ShowHost、广播玩家列表），客户端回房间等待页（_lobbyWaiting、ShowClient、
    RequestMatchState 拉列表）；MatchState/比赛态复位；**上一局的球留着当背景**
    （模拟已冻结；下局 START 统一复位，球对象永不销毁）。
  - 单机（无会话）→ ResetToMenu（原路径）。
- **连续多局复位**（StartMatchFromLobby）：改为"全量复位"语义——真人球（host+远程）一律
  RespawnBall（质量/生死态清零），bot 按名额激活、多余 b.Alive=false 蛰伏——不再跳过活球
  （旧逻辑第二局会保留上一局的存活球质量）。下局 START 时统一重建分身列表。

### M5.9 UI 对账器 + 残留会话修复（v0.6.4.5-M5，2026-09-07，用户双开截图实锤）

用户截图两个症状：①客户端加入 host 后**整套 UI 消失**（球/头像/食物正常 = 快照没问题，纯客户端界面状态没建立）；②host 端**房间页和主菜单叠在一起**；③编辑器控制台反复刷 `[net] join: session already active, skip connect`（当过一次 host 后残留会话没断，再点 JOIN 永远静默失败——用户"是不是网络连接问题"猜对）。

- **ReconcileUi（Tick 每帧）**：由状态标志**推导**面板可见性并强制执行——对局中=HUD 在+菜单/房间收；_lobbyOpen=菜单收+ShowHost；客户端在会话未进局=菜单收+ShowClient（顺带补 _lobbyWaiting/RequestMatchState）；无会话=收房间。漏调 Show/Hide、热重载重建面板、残留状态全部下一帧自动纠正，UI 从"命令式"变"声明式"。
- **残留 _joinCountdown 自清**：≥0 但对局已不在/已进局/掉线 → 清 -1。此前残留会让客户端恢复分支（要求 <0）永久卡死——"客户端没 UI"的主嫌疑。
- **MainMenu.Hide 幂等**：已禁用直接 return（不碰光标）——对账器每帧调 Hide 会和房间页的 Mouse.Visibility=Visible 打架。
- **LobbyPanel.Open 幂等**：已显示 early-return，不再重置 _matchRunning/倒计时（否则对账器每帧清掉 JOIN GAME 按钮）。
- **AdoptSnapshotWorld 剥净 host UI**：原只剥 Camera/Hud，现连 MainMenu/LobbyPanel/EndBoard 一起剥——host 快照把整套 UI 复制给客户端，是"双份界面/host 视角房间页"的来源。
- **StartJoin 掐残留会话**：Networking.IsActive 时先 Disconnect 再连（原样 return = 永远连不上）。

### M5.10 掉线自动重连 → 耗尽回主菜单（v0.6.4.6-M5，2026-09-07 用户定稿）

用户需求：客户端断联后自动尝试重连，失败后自动返回主菜单。旧行为两处不配合：房间等待中掉线**立刻**放弃回菜单（不重试），对局中掉线**永远卡住**什么都不做。

- **NetworkManager.TickDisconnectRecovery**（每帧）：从会话里掉出来（房间/对局/倒计时中，`IsClientInSession` 判定）→ 每 `ReconnectRetrySeconds`(2s) 重连一次，最多 `ReconnectMaxAttempts`(5) 次；成功 → NotifyReconnectStarted 走标准加入流回房间；耗尽 → OnReconnectFailed → ResetToMenu + "CONNECTION LOST — COULD NOT REACH HOST"。`Instance != this` 守卫防快照带来的 host 副本双份重连。
- **握手卡死看门狗收编**：TickReconnectWatchdog 只负责拆僵尸连接（Disconnect），重连发起/计数/耗尽处理统一归 TickDisconnectRecovery。
- **旧"HOST LEFT THE LOBBY 立刻回菜单"删除**；ReconcileUi 加分支：`!inSession && IsReconnecting` → 房间页留在原地 + 状态行 "CONNECTION LOST — RECONNECTING ..."（JOIN 按钮收掉）。
- **重连成功状态行恢复**：LobbyPanel.Open 已显示时客户端补刷 RefreshStatus；RefreshStatus 客户端分支按 _matchRunning 区分文案（否则重连回房后 "CONNECTION LOST" 永久挂着）。
- 菜单上的主动断开（LEAVE/结算回菜单）经 ResetSession 清 _reconnecting，不触发重连。

### M5.11 团队模式扩展：2/3/4 人队 + 分队 bug 修复 + 队友积分/名牌队标（v0.6.5.0-M5，2026-09-07 用户需求）

用户需求：二级菜单增加 2 人/4 人团队赛及对应游戏内逻辑；排行榜下方显示队友积分；球上名牌加团队标号。
并质疑"团队数量才 4 个"——**实锤分队 bug**：原 `NextTeamIndex()` 用 `%TeamSize` 轮转，48 人只分出 3 个队（每队 16 人），不是"每队 3 人"。

- **模式枚举改造**：`Ffa/Team2/Team3/Team4`（模式字节直接编码队伍规模，MatchSettings/LobbyState RPC 签名零改动）；`MatchState.TeamSize => 1+(int)CurrentMode`；`IsTeam => CurrentMode >= Team2`。房间页 MODE 点击 4 档循环。
- **分队 bug 修复**：`NextTeamIndex()` 改按块分配 `_teamSeq++ / TeamSize`——48 人 3 人队 = 16 队（T1..T16）；中途加入的球也补分到下一个块。
- **队友免伤**（原版没有，团队赛下队友互吃）：球×球、球×分身（teamOf 字典按主人查队伍）、分身×分身三处同队跳过；孢子仍人人可吃（喂队友是战术）。
- **队色**：Ball/TeamColor 的 Clamp 改取模循环（最多 24 队，6 色循环）。
- **MY TEAM 区**（排行榜/自身行下方）：队头 "MY TEAM [T#]  总分"（队色）+ 成员按质量降序逐行名字+分数，自己那行金色 ">" 高亮；普通赛/无本机球/结算期整块隐藏。
- **名牌 [T#] 前缀**：主球与分身名牌统一 `TagText()`（团队赛且队号≥0 才加）。
- 结算/队伍总分条/播报全部按 TeamSize 泛化，无需改动（EndBoard 按 Team 字段求和天然支持任意队数）。

### M5.12 bot 进阶能力：分裂捕猎 / 吐孢子喂队友 / 团队集结（v0.6.6.0-M5，2026-09-07 用户需求）

用户需求：为 bot 增加分裂、吐孢子、团队配合能力。BotBrain 重构为"态势收集 Survey → 方向决策 DecideDir → 动作层"三段，方向与动作解耦。

- **分裂捕猎**：贴身（自身半径+BotSplitRange×难度系数）、分出去的一半仍够吃猎物（Mass×0.5 > 猎物×EatRatio）、5s 冷却、难度概率门槛（新兵 0.18/老手 0.5/王牌 0.85）→ 朝猎物 DoSplit（与玩家同一条权威路径，内部自带校验）；分身带冲量飞出后跟随主人转向继续压猎物。
- **吐孢子喂队友（养大哥）**：团队赛限定——范围内（1500×系数）最大的、比我大 1.25 倍的队友为喂养目标；无威胁无猎物时朝其移动并 DoEject（多身体齐吐）；喂到对方不足我 1.25 倍自动停（自平衡，bot 不会把自己喂瘦成食物）；0.6s 冷却+决策间隔+难度概率三重节流。
- **团队集结**：团队赛游荡目标不再全场随机，改为围绕随机队友 ±900 范围取点，抱团行动。
- **猎物/威胁跳过队友**：配合 M5.11 的队友免伤——bot 不再追打吃不动也吃不到的队友。
- 新增 GameConfig：BotSplitMinMass(80)/BotSplitRange(620)/BotSplitCooldown(5s)/BotFeedMinMass(36)/BotFeedRange(1500)/BotFeedCooldown(0.6s)。
- 结算期 MatchOver 加了决策冻结守卫（此前 bot 在结算期仍会移动）。

### M5.14 host 心跳检测：杀进程也能回主菜单（v0.6.8.0-M5，2026-09-07 用户截图反馈）

用户反馈：host 结束进程后客户端仍停在对局画面不回主菜单。根因：**host 进程被杀时引擎会话可能不自动失效（僵尸态，Networking.IsActive 恒 true）**——v0.6.4.6 的掉线恢复循环以 IsActive=false 为触发条件，永远不启动。

- **应用层心跳**：对局中 host 本来就 1Hz 广播 MatchTick → `MatchState.SinceLastTick` 每次到达归零；房间等待中 host 每 3s 广播一次 LobbyState 当心跳（名单没变也推）。客户端任一通道静默超过 `HostLivenessTimeout`(6s) → 主动 `Networking.Disconnect()` 拆掉僵尸会话 → IsActive 翻 false → 既有恢复循环接管（重连 → 耗尽 → 回主菜单）。
- **武装/ disarm 细节**：房间心跳收到第一次 LobbyState 才武装（防刚进房误判）；OnMatchStartedLocal 出房间解除武装（防结算回房后旧计时器立即误触发）；Apply/ApplyTick 都归零（开局就有心跳）。
- **重连总时长上限** `ReconnectGiveUpSeconds`(20s)：单次 Connect 可能长时间挂在"正在握手"，超上限无论走到哪步都放弃回主菜单（此前只有次数上限，个别挂死尝试会拖很久）。
- **ResetToMenu 防御**：逐球 try/catch 销毁——客户端销毁 host 拥有的对象可能被引擎拒绝，不能中断回菜单流程。

### M5.15 重连目标修复 + "copy" 异常分析（v0.6.8.1-M5，2026-09-07 用户日志）

用户日志：`03:33:53 [net] host silent beyond liveness timeout — forcing disconnect` 证明 M5.14 心跳生效；但此后 30 秒内无任何 reconnect attempt 日志——重连循环没启动。根因：重连目标只从 `Connection.Host?.Address` 捕获，**Steam 中继连接（房间列表点行加入）该值可能为空**，`_serverAddress is null` 直接 return。

- **JoinLobby/QuickJoin 记住目标** `_lastJoinedLobby`（点行/快速加入时记下大厅信息）；重连首选 `Networking.TryConnectSteamId( lobby.LobbyId )`（与加入同一条 Steam 路径），local 直连的旧 Address 路径保留兜底。
- **"Specified argument out of range (startingTextElement) when running panel event copy"**：引擎文本选择/复制机制在处理 copy 面板事件时内部索引越界（复制控制台日志时触发）——引擎侧、非本游戏代码（游戏面板从不触发 copy 事件），无实害。若不复制文本也反复出现再排查。

### M5.16 残破状态自愈：空屏/无 HUD 一帧纠正（v0.6.8.2-M5，2026-09-07 用户截图）

用户截图：客户端球能控、NPC 冻结、**整套 UI 消失**——典型的"半死不活中间态"（对局标志在但 HUD 引用失效/菜单被藏；疑似热重载重建世界 + 死会话叠加）。

- **ReconcileUi 终态保证**：无会话且房间列表页没开 → **主菜单必须可见**（只在被禁用时 Show，防每帧清状态行）。此前只藏房间不亮菜单——任何路径漏 Show（热重载重建、异常中断）就是全空屏。
- **对局中 HUD 失效自重建**：_gameStarted 分支里 `_hud` 无效 → 直接 CreateHud()（热重载重建世界后引用失效的场景）。
- **心跳强制拆线时同步退出对局态**：_gameStarted=false、_lobbyWaiting=true（保住 IsClientInSession 让恢复循环照常启动）、LocalBall=null——恢复期显示房间页 "CONNECTION LOST — RECONNECTING" 而不是冻结对局画面。
- **LobbyPanel._connectionLost 标志**：SetConnectionLost 置位、UpdateFromHost（收到 host 任意消息）清位、RefreshStatus 尊重之——修掉 v0.6.4.5 引入的"每帧补刷状态行会立刻覆盖掉线提示"的连带 bug。

### M6.5 道具系统第一批（v0.7.3.0-M5，2026-09-08 用户定稿）

用户定稿：第一批 4 道具、拾取即生效（不做背包键位）、护盾 5 秒、质量罐 +300。

- **PowerUpManager**（新，Code/Game/）：与 FoodManager 同款"纯数据 + host 权威 + 静态 RPC"模式。
  **固定槽位制**：场上恒 7 槽（PowerUpOnField），被捡后同槽 6s 重刷（换种类换位置），
  全量同步一个小数组（PowerUpWire[] 顶层 struct——RPC 参数对顶层类型最稳），中途加入/热重载恢复便宜。
- **四个道具**（拾取仅主球，分身不参与）：⚡加速 8s 移速×1.4（黄菱形+拖尾）／🧲磁铁 8s
  吸食半径翻倍（青+十字芯，只影响食物）／🛡护盾 5s 不可被大球/分身吃（蓝+双环，不防尖刺）／
  💊质量罐 +300 即时（品红+实心大芯）。形状微差辅助色弱区分。
- **buff 挂 Ball**：`[Sync(FromHost)] BuffKind/BuffDuration/BuffSerial`，serial 自增保证
  "同类 buff 再捡一次"也触发 [Change]（同值不发包的坑）；各端收到变化重置本地 TimeSince，
  HasBuff = Kind匹配 && 本地计时未到——host 权威与客户端展示同一公式，过期无需通知。
  重生清 buff（机会不跨命携带）。Speed 属性 ×1.4 接入（owner 模拟与 host 位移校验同源，不触发橡皮筋）。
- **同步**：PowerUpFull（开局广播+新连接 OnActive/RequestFoodFull 定向补发）、
  PowerUpPicked/PowerUpRespawned 增量广播；菜单演示赛也生成（无会话广播自动跳过）。
- **渲染**：NeonRenderer 旋转菱形（Time.Now 自转+呼吸缩放）+ 种类色；Spark 加 Color 重载
  （拾取特效走种类色，不复用调色板索引）。
- **HUD**：左下角操作提示上方 buff 行（名称+剩余秒+种类色），SetText 防抖 + 颜色只在种类切换时赋。
- **bot**：猎物扫描跳过带盾球（追也吃不到，不浪费时间）；bot 路过道具自然捡（不主动寻路，第一批）。

### M6.6 道具平衡 + 尖刺分身 + 喂食判定（v0.7.4.0-M5，2026-09-08 用户定稿）

用户担忧"大玩家吃到很多道具"→ 三层平衡方案，A/B/C 拍板 + "血量池"喂食机制。

- **拾取判定封顶**：拾取圈按 `min(球半径, PowerUpPickRadiusCap=200) + PowerUpRadius` 算——
  大球（半径 1200）不再"路过顺走"，必须刻意碾到道具正上方；小球轻松捡（反马太）。
- **尖刺分身道具（Kind.Spike，第 5 种）**：拾取/喂食触发 → 从主人现扣 10% 质量（下限 10）
  生成 `CellPiece.Kind.SpikeMinion`——**跟随主人**（用户定稿 A，TickCells 与分身同分支）、
  **20s 内是"刺"**（TickSpikeMinions：敌方球/分身按野刺规则——比 SpikeRadius 大被炸
  （DoBurst+吃掉分身质量），比刺小被吃（死/晋升，刺保留）；判定芯 = SpikeRadius×SpikeTouchFactor）；
  **20s 到期变回普通分身**（用户定稿 B：质量合体回收，MergeCooldown 重置 1s，倒流引力对它关闭）；
  **炸掉"比主人大的球"→ 主人 3s 无敌**（用户定稿 C：ApplyBuff(Shield,3) 复用——武器专给小球反大球，
  大球炸小球零奖励）。同主人+团队赛队友免疫。渲染=紫星（野刺同族）+主人色描边环（可辨"这是我方武器"）。
- **喂食判定（"血量池"，虎口夺食）**：道具共享积分池（按槽 float[]，谁吐丝都充）；
  孢子碰道具被吸收（PowerUpManager.Feed），**积分 ≥ 本次喂食者当前体重 ×5% → 触发给喂食者**，
  道具消耗重刷+积分清零。吐一颗=0.8% 积分（1% 消耗×0.8 返还）→ 恒定 ~6.25 口达标，
  但门槛按体重——大球 250 积分起步，小球几口就够，大球喂到一半被小球截胡=白丢孢子（用户要的博弈）。
  远程吐丝可隔空喂（不用贴身），身体碰撞拾取保留（免费但要贴脸）。
- **平衡闭环**：捡（封顶：大球难）+ 喂（按体重收费：大球贵）+ 尖刺分身（小球反大球武器）。

### M6.7 鼠标键位并行（v0.7.4.1-M5，2026-09-08 用户需求）

左键吐丝（Attack1）、右键分裂（Attack2）——与空格/R 并存（两套键位共存）。"Attack1/Attack2" 为
引擎内建按钮，官方项目（sandbox-main HandGrenadeWeapon 等）同款 Input.Pressed/Down 用法。
左键吐丝天然朝光标方向（actionDir=瞄准方向）。死亡面板点击/结算 BACK 按钮不受影响
（死亡/结算期输入段早退）。HUD 提示行更新 "SPACE/RMB SPLIT — R/LMB FEED"。

### M6.8 玩法提示轮播（v0.7.4.2-M5，2026-09-08 用户需求）

左下角玩法提示条（.tips，SimHei 中文，操作提示上方/buff 下方）：13 条静态文案（键位/道具/多身体/
喂食虎口夺食/P 退局/大球笨重等）每 8s 轮换，换条瞬间 0.5s 透明度渐入（结束后同值不写样式）；
文本 SetText 防抖。emoji 没字体不用；UI 中文走 SimHei（中文昵称同源已验证可用）。

### M6.9 buff 状态移右下角（v0.7.4.3-M5，2026-09-08 用户需求）

.buff 样式 left:30px → right:30px + text-align:right（右下角，与左下提示区对称；右上被
排行榜/版本占用）。仅样式改动。

### M6.10 道具主动使用（Q/E 背包）+ 喂不中 bug 修复（v0.7.5.0-M5，2026-09-08 用户定稿）

用户需求：①玩法提示改英文 ②场上道具槽 7→9 ③道具改为**主动使用**（Q/E 两格背包）
④"道具不吃喂养"——**是 bug 不是删功能**（我一度误解删了 Feed，已恢复）。

- **背包制**：真人拾取道具**存入 Q/E 两格**（Ball.PowerA/PowerB `[Sync(FromHost)]`，255=空；
  死亡清空；**背包满则道具留在场上不被捡**）；按 Q/E 释放效果（权威端直调 UsePower /
  客户端 RequestUsePower [Rpc.Host] 校验归属）。bot 例外：拾取即用（不囤不按键）。
  ApplyEffect 改 public static（Ball.UsePower 复用）；效果本身经 [Sync]/CellsState 同步，无需额外广播。
- **HUD**：右下角两格背包槽（[Q] MAGNET / [E] —，种类配色，空槽暗淡；颜色只在种类切换时赋）；
  hint 加 "Q/E USE ITEM"。
- **喂不中 bug 根因**：孢子吐出滑行 ~355 距离耗时 ~1.2s，而 EjectedEdibleDelay=0.5s——
  **经过道具的那一帧几乎总在可食延迟期内**，延迟过后孢子早已飞远，喂食判定永远赶不上。
  修复=喂道具判定**提前到 EjectedEdibleDelay 检查之前**（喂食无质量回流，没有"秒吃自己"漏洞，
  豁免安全）。孢子飞行途中经过道具即喂中，瞄准道具吐丝即可。
- 喂养触发**立即生效不入背包**（远程购买语义）；身体碰撞拾取=入背包待按键。
- 提示条 14 条英文文案（含 Q/E 使用与喂食教学）。

### M6.11 背包槽格子化（v0.7.5.1-M5，2026-09-08 用户需求）

右下角背包槽从纯文本升级为**格子样式**：固定 132px 宽深色圆角底块（rgba(12,20,34,.78)+radius 4）、
右对齐文字，有道具=种类色道具名（[Q] MAGNET），空槽=暗灰 [Q] —。槽恒可见（空槽也画格子），
玩家随时知道背包状态。纯样式改动。

### M6.12 个人高光横幅（v0.7.6.0-M5，2026-09-08 用户需求）

屏幕中下（bottom 170 起堆叠，最新最靠下、最多 3 条）**只播自己的**高光事件，30px 大字+
8px 字距+深色三层光晕的艺术字（Inter 800，颜色按事件）：

- **击杀**：`YOU ATE {name}  +{mass}`（金色 #ffcf5c 系）——OnBallEaten 双端统一入口里
  IsMine(eater) 判定（被吃不弹，死亡面板在管）。
- **捡到道具**：`{NAME} STORED`（种类色）——host 存背包处本地播 + PowerBanner(kind,owner,0) 广播，
  客户端 OnPowerBannerRemote 按 IsMine 显示。
- **使用/喂养触发**：`SPEED BOOST!` / `MAGNET ON!` / `SHIELD UP!` / `SPIKE MINION DEPLOYED!` /
  `+300 MASS!`（GameHud.ActivationText 按种类，Q/E 按键本地即时播（不等 host 往返）；
  喂养触发走 Consume→ShowBanner 广播 mode=1）。

动画：0.3s 弹入（透明度+从下落 20px 回位）→ 1.2s 停留 → 0.6s 淡出；堆叠位置变了才写样式
（同值跳过防布局刷新）。bot 拾取/他人事件不弹（IsMine 挡）。

### M6.13 高光横幅夸张化：连杀/震动/大字/音效（v0.7.7.0-M5，2026-09-08 用户需求）

- **击杀连播（combo）**：10 秒窗口内连续击杀累计（GameSfx.RegisterKill，重生 ResetCombo 清零）——
  第 1 杀 `YOU ATE {name} +{mass}`；2/3/4/5+ 杀升级 `DOUBLE KILL! / TRIPLE KILL! / RAMPAGE! /
  GODLIKE!`（.banner.combo 变体：46px/字距 12/金色光晕 text-shadow；普通横幅 30→38px）。
- **屏幕震动**：NeonCamera.Shake(amplitude)——ApplyPose 摆位叠加随机方向偏移（每次摆位衰减
  exp(-6dt)，摆位本身逐帧重写不含残量，震完归位）；名牌投影用同一相机位置抖动一致。
  击杀震 8+combo×1.5（clamp 14）、放技能轻震 5；被吃/捡道具不震。
- **音效**（Python stdlib 合成 wav 22050Hz/16bit/mono + .sound JSON，M4 链路同款）：
  cr_combo.wav（E5-A5-C#6 上扬琶音 0.46s）×三档 .sound（Pitch 1.0/1.15/1.32，共用同一 vsnd，
  combo 档位越高音越高，≥2 杀才播、五杀封顶）+ cr_pickup.wav（A5-E6 叮咚，道具入包/激活配套）。
  GameSfx：RegisterKill（10s 窗口连击计数）/ComboSound/Pickup/ResetCombo（重生清）。
- 触发点：击杀（OnBallEaten IsMine(eater)：combo 横幅+升调音+震）、放技能（Ball.UsePowerSlot：
  横幅+Pickup 音+轻震，本地即时）、存背包/喂养触发（ShowBanner 内 Pickup 音；客户端
  OnPowerBannerRemote 同播）。

### M6.14 房间默认 3 人团队赛 + 设置可点引导（v0.7.8.0-M5，2026-09-08 用户需求）

- **默认模式**：MatchState.PendingMode 默认 Ffa → **Team3**（新开局默认 3 人团队赛；
  客户端房间页文案随 LobbyState 推送自动跟随）。
- **可点引导**：模式行（host）金色呼吸光效（OnUpdate 每帧 Sin 3.2Hz 亮度调制，只动 FontColor
  不碰 Text）；设置行下方加 "CLICK A SETTING TO CYCLE IT" 提示小字（lp-setting-hint，
  top 318px，暗金色，仅 host 显示——客户端不能改设置）。客户端切回时恢复 scss 基色一次
  （_modeGlowActive 标志防残留）。

### M6.14b 开新房强制归位默认模式（v0.7.8.1-M5，2026-09-08 用户实测反馈）

用户实测"还是 FFA"——**热重载保留旧静态值**（PendingMode=Ffa 残留），静态初始化器只在
全新会话生效。修=OpenHostLobby（开新房间）显式 `PendingMode = Team3`——热重载残留也自愈，
无需重启；同一房间连续多局（P 退局/结算回房）不走 OpenHostLobby，玩家改过的模式保留。
**通用教训：静态默认值修改必须配"使用点显式归位"（懒加载自愈模式），不能指望初始化器跨热重载生效。**

### M6.15 房间页道具图例块（v0.7.8.2-M5，2026-09-08 用户需求）

静态图例放玩家列表左侧空列（x=64..558，玩家列表左列从 560 起）："POWERUPS" 小标题 +
5 行（■ 色点+道具名种类色 + 一句话说明灰白）+ 获取方式注脚（PICK UP OR SPIT AT IT —
USE WITH Q/E）。名字/描述每行两个 Label（单 Label 单色的限制），行 Top 代码按行排
（384+i×34）。host/client 都显示（教学信息双方都有用）。

### M6.16 START/JOIN 移右下角（v0.7.8.3-M5，2026-09-08 用户定稿布局）

房间页定稿布局：中间玩家名单、左下道具图例、**右下 START GAME（host）/ JOIN GAME（客户端）**
（right:64px 右对齐，与图例对角平衡）。纯样式改动。

### M6.17 图例块移左下角（v0.7.8.4-M5，2026-09-08 用户截图红箭头标注）

用户截图标注：图例块从左侧中部（352-570）移到**左下角**（红箭头指向的空白区）——
head 660 / 行 690+i×34 / 注脚 862；BOTS note（中下）与 LEAVE（底部居中）不动，
START 保持右下。布局=中间名单 / 左下图例 / 中下提示 / 右下 START。

### M6.18 BOTS 提示/START/LEAVE 移入右下蓝框（v0.7.8.5-M5，2026-09-08 用户截图蓝框定稿）

右下蓝框区域（y 680-970）竖排右对齐：BOTS note top 690 → START/JOIN top 780 → LEAVE top 880。
房间页最终布局：中上设置区、中玩家名单、左下图例块、右下操作区（note/START/LEAVE 竖排）。
