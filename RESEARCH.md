# CircleRoyale 调研报告 —— 球球大作战类游戏（科幻霓虹线条风）

> 调研日期：2026-09-05。目标：在 s&box 上实现球球大作战（Agar.io 类）玩法，支持多人对战 + 人机（bot）对战，美术走 Geometry Wars 式霓虹矢量线条风。本报告只调研不实现，所有 API 均已对照本地引擎源码 / `api/api.json` / 官方示例核实。

---

## 0. 结论速览

1. **circleroyale 目前是纯空模板**（无任何游戏代码），sbproj 缺 `Metadata`（TickRate/MaxPlayers/GameNetworkType/StartupScene 都没配）。
2. **网络架构**：s&box 没有客户端预测 API，但对本类游戏最合适的是「**owner 本地模拟移动 + host 权威数值（质量/分数/吃判定）**」；主机读远端玩家按键有现成通道 `connection.Down(action)`，鼠标方向类模拟量则由客户端用 Unreliable RPC 上报或由 owner 直接模拟。
3. **人机对战**：引擎**没有**任何 bot/假连接 API（已全盘 grep 确认）。可行做法：bot = host 驱动的普通网络对象（`NetworkSpawn()` 在 host 上执行），AI 在 host 的 `OnFixedUpdate` 里跑，客户端只收插值 transform，不占玩家名额。
4. **霓虹渲染**：用 `SceneDynamicObject` 每帧重写顶点（`PrimitiveType.Lines` / `Triangles`）+ `shaders/line.shader`（顶点色、加色混合）批量画线段和发光图形，一个系统一两个 DrawCall 搞定全场；辉光挂 `Sandbox.Bloom` + `Sandbox.Tonemapping` 组件即可，游戏代码可用。
5. **食物几百个不能每个都做网络对象**：用单个管理器 + `[Sync(SyncFlags.FromHost)] NetList<FoodData>` 同步（自动全量+增量），渲染侧客户端批量画。
6. 大量现成模式可直接抄同工作区 `tiaotiao/Code/Jump/`（运行时生成世界、static 世界根、纯 C# HUD、跟随相机）。

---

## 1. 项目现状

```
circleroyale/
├── circleroyale.sbproj      # Title: CircleRoyale, Type: game, Ident: circleroyale
│                            # ⚠ Metadata 是空 {} —— 缺 TickRate/MaxPlayers/GameNetworkType/StartupScene
├── Assets/scenes/minimal.scene   # s&box 自带 minimal 演示：Sun/2D Skybox/Plane/3个掉落方块/相机(FOV60+Bloom)
├── Code/Assembly.cs         # 仅 3 行 global using，无任何类
├── Editor/Assembly.cs
└── ProjectSettings/         # Collision/Input/Platform config（与 tiaotiao 相同的默认动作表）
```

需要补的 sbproj Metadata（照抄 tiaotiao / sandbox-main 的多人配置）：

```json
"Metadata": {
  "MaxPlayers": 16,
  "TickRate": 50,
  "GameNetworkType": "Multiplayer",
  "StartupScene": "scenes/minimal.scene"
}
```

- 场景固定更新频率实际来自场景的 `FixedUpdateFrequency: 50`（minimal.scene 已有）。
- 网络发包频率由 **ProjectSettings → Networking → UpdateRate**（默认 30，源码 `NetworkingSettings.cs:46`）控制，与 sbproj 的 `TickRate` 字段无关（那个字段引擎代码不消费，只是元数据）。
- minimal.scene 里的模板物（方块/平面/演示相机）按 tiaotiao 模式在运行时整体 `Enabled = false` 屏蔽。

---

## 2. 网络 API（重点调研）

### 2.1 联机生命周期

官方参考实现：`sbox-public/game/addons/base/code/Components/Networking/NetworkHelper.cs`、`sandbox-main/sandbox/Code/GameLoop/GameManager.cs`、`sbox-scenestaging/Code/ExampleComponents/GameNetworkManager.cs`。

| 环节 | API | 说明 |
|---|---|---|
| 建服 | `Networking.CreateLobby( new LobbyConfig(){ Privacy = LobbyPrivacy.Public, MaxPlayers = 16, Name = "CircleRoyale", DestroyWhenHostLeaves = true } )` | 在 `ISceneStartup.OnHostInitialize` 或某组件 `OnStart` 里，先判 `Networking.IsActive` |
| 加入 | `Networking.Connect( steamid / "local" / ip )`、`JoinBestLobby( ident )` | 测试用 `connect local`（`net_allow_local` 回环 TCP 55333） |
| 连接审批 | `Component.INetworkListener.AcceptConnection( Connection channel, ref string reason ) => bool` | 任何实现者返回 false 即拒绝（`Kick`） |
| 玩家就绪 | `INetworkListener.OnActive( Connection channel )` | **在这里生成该玩家的球** |
| 生成玩家 | `var go = BallPrefab.Clone( spawnPos ); go.NetworkSpawn( channel );` | 第二个参数把连接设为网络拥有者；内部要求 `connection.CanSpawnObjects`，且 NetworkMode 强制为 `Object` |
| 断线清理 | `INetworkListener.OnDisconnected( Connection channel )` | 拥有者断开时其网络对象默认自动销毁（`NetworkOrphaned.Destroy`），也可自己管理 |
| 主机移交 | `INetworkListener.OnBecameHost( previousHost )` | .io 房间一般不开移交：`DestroyWhenHostLeaves = true` 即可 |

关键源码位置：
- `sbox-public/engine/Sandbox.Engine/Scene/Components/Markers/INetworkListener.cs`（5 个回调全有默认实现，可选实现）
- `sbox-public/engine/Sandbox.Engine/Scene/GameObject/GameObject.Network.cs`（`NetworkSpawn()` :126 / `NetworkSpawn(Connection)` :203 / `NetworkSpawn(NetworkSpawnOptions)` :131）
- `sbox-public/engine/Sandbox.Engine/Systems/Networking/Networking.cs`（`IsActive/IsHost/IsClient`、`SetData/GetData`、`ServerName/MaxPlayers`）

### 2.2 状态同步（[Sync] 家族）

- `[Sync]`：**网络对象 owner 可写**；非 owner（`IsProxy`）写入被静默丢弃（`Component.Network.cs:64-67`）。只在 `NetworkMode.Object` 上生效。
- `[Sync( SyncFlags.FromHost )]`：**只有 host 能写**，客户端只读 —— 血量/质量/分数/排行榜用它。
- `SyncFlags` 全集（`SyncFlags.cs`）：`FromHost=1`（host 写权）、`Query=2`（轮询 getter 代替 setter 打脏）、`Interpolate=4`（远端读取自动插值，**仅支持 float/double/Angles/Rotation/Transform/Vector3**）。
- 支持类型：unmanaged 值类型 + `string` + `GameObject`/`Component`/`GameResource`。
- 最新值语义：确认前又改值则中间值丢失；可靠事件用 RPC。
- `[Change( nameof(OnXxxChanged) )]` 变化回调，签名 `(T old, T new)`；**集合内容变化不触发 [Change]**，要用 `NetList<T>.OnChanged` / `NetDictionary<K,V>.OnChanged`（owner 与所有客户端都触发）。
- `NetList<T>` / `NetDictionary<K,V>`：**只发增量**（Add/Remove/Replace/Reset）；新连接/重连自动收全量（`NetList.cs:332-349`）。写保护与 [Sync] 一致（proxy 写无效）。

### 2.3 RPC

- `[Rpc.Broadcast]`（默认 Reliable，所有人执行）/ `[Rpc.Host]`（只在 host 执行）/ `[Rpc.Owner]`。
- `NetFlags`（`NetworkEnums.cs:4-44`）：`Unreliable=1`、`Reliable=2`、`SendImmediate=4`、`DiscardOnDelay=8`、`HostOnly=16`、`OwnerOnly=32`，组合常量 `UnreliableNoDelay = Unreliable|SendImmediate|DiscardOnDelay`（高频位置类消息用）。
- 限定目标**没有 SendTo**，用作用域过滤：`using ( Rpc.FilterInclude( Network.Owner ) ) { SomeRpc(); }`（官方用例 `sandbox-main/sandbox/Code/Player/PlayerData.cs:40-43`）。
- 客户端 → host 请求模式：`[Rpc.Host] void RequestRespawn()`，host 里用 `Rpc.Caller` 拿来源连接（官方 `GameManager.cs:116-131`）。

### 2.4 输入通道（主机怎么知道玩家按了什么）

- 按键：host 上直接 `connection.Down( "Jump" ) / Pressed / Released`（`Connection.Input.cs:114/134/156`）——远端值来自客户端随每个网络更新上传的 UserCommand 输入位掩码（`Scene.Network.cs:141-181`），本地连接自动回落到本机 `Input`。
- **模拟量（鼠标位置/AnalogMove）不走这条通道**——只有按键位掩码。鼠标瞄准方向要么客户端用 `NetFlags.Unreliable` RPC 定时上报，要么干脆 owner 本地模拟移动（见 2.5）。

### 2.5 推荐架构（本游戏）

**owner 本地模拟移动 + host 权威数值**（.io 游戏标准做法，s&box 无客户端预测 API，所以不要做全 host 模拟移动——自己人也会卡 100ms）：

```
每个连接一颗"玩家球"网络对象（owner = 该连接）
├─ 移动：owner 端 OnFixedUpdate 读 Input（鼠标方向），本地改 WorldPosition
│        → 网络 transform 自动同步给 host 和其他客户端（引擎默认插值）
├─ 质量/分数/名次/死亡：host 判定，[Sync(FromHost)] 下发
│        （host 侧用同步到的位置做吃判定，误差=插值延迟，可接受）
└─ 事件（分裂/吐孢子/被吃）：客户端 [Rpc.Host] 请求 → host 校验 → [Rpc.Broadcast] 广播表现
```

- host 校验移动速度上限（按质量算 max speed，超出拉回）作为轻反作弊。
- host 自己的球：owner = `Connection.Local`，同样走 owner 模拟，零延迟。
- 频率：`ProjectSettings.Networking.UpdateRate` 保持默认 30 即可；`net_interp_time` 默认 0.1s 插值。

### 2.6 断线/重连/其他

- 拥有者断开 → 其网络对象默认销毁；房间随 host 离开销毁（`DestroyWhenHostLeaves`）。
- 公开重连 API 没有（引擎 internal 有自动重连）；`connect local` 多开测试足够。
- 可见性剔除：`Component.INetworkVisible.IsVisibleToConnection`（地图大、人多时再考虑，默认 `AlwaysTransmit`）。
- **没有** `Connection.CanSend` 这种成员（不存在）；定向消息用 `connection.SendMessage<T>( msg )` 或 Rpc.Filter。

---

## 3. 人机对战（Bot）方案

**引擎无任何 bot API**（`sandbox-main`、engine、game 全盘 grep `bot/fakeplayer/fakeconnection/IBot` 零命中；`LocalConnection`/`MockConnection` 是 internal，伪造连接注册不进会话）。

**可行模式：bot = host 拥有的普通网络对象**，与玩家球走同一套同步管线：

```csharp
public sealed class BotBrain : Component
{
	// bot 球与玩家球共用 Ball 组件，只是没有真实 Connection
	protected override void OnFixedUpdate()
	{
		if ( IsProxy ) return;              // 客户端不跑 AI
		// host 上跑：追最近食物 / 逃离比自己大的 / 追猎比自己小的 / 随机游走
		// 直接改 WorldPosition（owner=host，transform 自动同步所有客户端）
	}
}

// host 生成 bot（不占 MaxPlayers，Connection.All 不包含它）：
var bot = BallPrefab.Clone( spawnPos );
bot.NetworkSpawn();                          // host 上执行，owner = Connection.Local(host)
```

- 客户端上 `bot.IsProxy == true`，只收 transform 插值，不跑 AI。
- 名字同步：host 在生成时写 `[Sync(SyncFlags.FromHost)] string PlayerName`。
- 房主可以加"填满房间"逻辑：开局把 bot 补到 N 个，真人挤掉 bot（`OnActive`/`OnDisconnected` 里调整数量）。

---

## 4. 核心玩法的实体与同步设计

| 实体 | 数量级 | 网络方案 | 说明 |
|---|---|---|---|
| 玩家球 | ≤16 | 每连接一个网络对象（Prefab 或代码建），owner 模拟移动 | 质量/分数 `[Sync(FromHost)]`，位置靠 transform 同步 |
| 食物点 | 300~600 | **不做网络对象**。`FoodManager`（host）+ `[Sync(FromHost)] NetList<FoodData>`，`FoodData` 为 struct（位置、颜色索引、存活） | 初始全量、之后只发增量；吃/重生都是改列表元素 |
| 刺（绿刺） | 10~20 | 同上，单独 NetList 或并入 | 被大球撞击后炸裂成质量点 |
| 孢子/吐出物 | 动态少量 | 短生命周期网络对象或并入效果层 | v2 再做 |
| 排行榜 | — | `[Sync(FromHost)] string` 打包文本，host 0.25s 刷新一次 | HUD 直读，最省 |

**玩法判定全部在 host**（`GameObjectSystem<CircleroyaleGame>` 的 Tick 里）：吃食物（距离+半径）、吃玩家（大 25% 且覆盖中心，agar 规则）、分裂合并冷却、边界、死亡重生（`[Rpc.Host] RequestRespawn`）。

**分阶段**（降低风险）：
1. **P0 骨架**：sbproj 配置 + 运行时世界（相机/灯/屏蔽模板物）+ 单机球移动（鼠标方向）。
2. **P1 单机完整**：食物 + 吃 + 成长 + 死亡重生 + bot AI + 排行榜 HUD（全部本地，零网络代码，先把手感做对）。
3. **P2 联机**：NetworkManager（建服/审批/OnActive 生成球）、食物 NetList 化、断线清理、`connect local` 多开测试。
4. **P3 进阶**：分裂/吐孢子/绿刺、拖尾粒子、网格扭曲背景、球上名牌（WorldPanel）。

---

## 5. 霓虹渲染管线（Geometry Wars 风格）

### 5.1 批量线段/图形 —— `SceneDynamicObject`（已核实，public）

`engine/Sandbox.Engine/Systems/SceneSystem/DynamicSceneObject.cs`：

```csharp
var dyno = new SceneDynamicObject( Scene.SceneWorld );
dyno.Material = Material.FromShader( "shaders/line.shader" );   // 顶点色 + 可加色混合
dyno.Flags.CastShadows = false;
// 每帧：
dyno.Clear();
dyno.Init( Graphics.PrimitiveType.Lines );      // 或 Triangles
dyno.AddVertex( new Vertex( posA, color ) );    // 每条线 2 顶点
dyno.AddVertex( new Vertex( posB, color ) );
```

- 同款用法引擎自证：`DebugOverlaySystem.DrawLine.cs:8-45`（用 `materials/gizmo/line.vmat`）。
- 实心图形（食物圆点、球内芯）用 `PrimitiveType.Triangles` 三角扇，顶点色即发光色；**全场实体可以合并进 1~2 个 SceneDynamicObject，一帧一两个 DrawCall**。
- `Vertex` 公共字段：`Position / Color32 / Normal / TexCoord0`（`Vertex.cs:24-41`）。
- `Gizmo` 全套（Line/SolidRing/SolidCircle…）**仅编辑器可用**（`Scene.Tick.cs:58-69`），运行时不可用。
- 备选：`LineRenderer` 组件（拖尾/轨迹，`Gradient Color`、`Curve Width`、`Additive`）、`TrailRenderer`（挂移动物体自动拖尾）。

### 5.2 辉光（Bloom）—— 游戏代码可用

往相机 GameObject 上加组件（官方测试场景 `sbox-scenestaging/Assets/Scenes/Tests/Rendering/bloom.scene` 实测参数）：

```csharp
cam.EnablePostProcessing = true;
go.AddComponent<Bloom>();        // Mode=Additive, Strength≈2~4, Threshold≈0.5
go.AddComponent<Tonemapping>();  // Mode=ACES
```

`RenderSettings` 是用户画质设置且实例 internal（构造/Instance 均 internal，游戏代码无法改画面设置），但 **Bloom/Tonemapping 组件不受影响**，正常用。

### 5.3 背景网格

- v1：深色 `CameraComponent.BackgroundColor`（近黑蓝 #060913）+ 用 5.1 的 Lines 批量画静态网格线（约 100 条线段，廉价且贴合线条风）。
- v2：仿官方 `sandbox-main/sandbox/Assets/shaders/snap_grid.shader`（配套代码 `Code/Weapons/ToolGun/SnapGrid.cs`：大 quad `SceneDynamicObject` + 每帧 6 顶点 + `Material.FromShader("shaders/snap_grid.shader")` + `RenderLayer = SceneRenderLayer.OverlayWithDepth`）自写 PS：世界坐标程序化网格 + 正弦扭曲（随时间/玩家位置扰动）+ 边缘发光。shader 结构（HEADER/MODES/COMMON/VS/PS、`Attribute()` 传参）照抄该文件改 PS 即可。
- 引擎没有现成"霓虹网格"材质，需自写。

### 5.4 粒子拖尾（无需 .vpcf 资源）

- `ParticleEffect` 组件是**纯代码驱动的 CPU 粒子系统**：`MaxParticles / Lifetime / InitialVelocity / Gradient / Brightness`，手动 `Emit( position )`；配 `ParticleSpriteRenderer`（`Texture/Scale/Additive/BillboardAlignment/FaceVelocity/Blur*` 自带运动模糊拖尾）。
- 或直接 `TrailRenderer`（`MaxPoints/PointDistance/LifeTime/Emitting/Gradient Color/Curve Width/BlendMode`）挂在球上。
- 工作区内没有可用的内置 .vpcf 库（只有模板 default.vpcf），所以走组件路线是对的。

### 5.5 俯视相机

```csharp
cam.Orthographic = true;
cam.OrthographicHeight = 1200f;          // 视口高度（世界单位），随玩家质量调大=镜头拉远
cam.ClearFlags = ClearFlags.All;
cam.BackgroundColor = new Color( 0.02f, 0.03f, 0.07f );
cam.IsMainCamera = true;
cam.Priority = -100;                     // 压过模板相机
```

跟随照抄 `tiaotiao/Code/Jump/JumpCamera.cs`：指数平滑 `Vector3.Lerp` + `1 - exp(-speed*dt)`；`OrthographicHeight` 也随 mass 平滑插值。`ZNear` 可为负（官方 orthographic.scene 用 -1000）。

### 5.6 HUD

纯 C# `PanelComponent` + 同名 `类名.cs.scss`（自动加载）——照抄 `tiaotiao/Code/Jump/JumpHud.cs`（`_built` 守卫 + 全字段健康检查 + 异常时整体重建）。内容：左上分数、右上排行榜、死亡/重生面板、倒计时。**UI 文案先用英文/数字**（core 资产无中文字体；要中文需把 CJK 字体放进项目 `Assets/fonts`，如 simhei.ttf 方案）。

---

## 6. 建议代码结构（官方规范：按功能域分目录）

```
Code/
├── Assembly.cs                    # global using（已有）
├── Game/
│   ├── CircleroyaleGame.cs        # GameObjectSystem<CircleroyaleGame>：世界生成、Tick 主循环、吃判定、bot 管理
│   ├── NetworkManager.cs          # Component + INetworkListener：建服/审批/OnActive 生成球/断线清理/填 bot
│   └── FoodManager.cs             # Food NetList 同步 + host 判定
├── Player/
│   ├── Ball.cs                    # 玩家球网络实体：owner 移动、[Sync(FromHost)] 质量/分数/名字
│   └── BallInput.cs               # 鼠标→移动方向、分裂/吐孢子请求
├── Bot/
│   └── BotBrain.cs                # host AI（与 Ball 共用）
├── Rendering/
│   ├── NeonRenderer.cs            # SceneDynamicObject 批量线/三角（收集全场实体每帧重写顶点）
│   ├── GridBackdrop.cs            # v1 网格线 / v2 扭曲网格 shader
│   └── NeonCamera.cs              # 正交跟随 + 按质量缩放
└── UI/
    ├── GameHud.cs / GameHud.cs.scss
    └── Leaderboard.cs
```

实体生成沿用 tiaotiao 模式：`static` 世界根 + 场景归属校验复用、`Scene.Children` 屏蔽模板残留、`Model.Load("models/dev/...")` + `Tint`（球/刺若走 mesh 则由 NeonRenderer 画，不需要 ModelRenderer）。

---

## 7. 坑与风险清单

1. **SB1000 白名单**：游戏代码无 `System.Reflection`；`RenderSettings` 构造/Instance 均 internal（改不了引擎画质，但 Bloom 组件可用）。
2. **Gizmo 仅编辑器**；`DebugOverlay` 静态类在引擎源码里是 internal——批量画线老实用 `SceneDynamicObject`。
3. `Material.Create` 在渲染中调用会抛异常（`Graphics.IsActive` 时）——材质在 OnStart/初始化阶段建好缓存。
4. **`Game.IsPlaying` 拦截**：GameObjectSystem 在编辑模式也 tick。
5. **热重载三坑**（tiaotiao 实测）：实例属性值保留（改 [Property] 默认值要重启 Play）；GameObjectSystem 重建但旧世界对象残留（static 根 + `Scene == Scene` 校验复用）；新 `.razor` 不被热编译（用纯 C# Panel）。
6. 场景 API：`Scene` 即根、无 `Scene.Root`；`new GameObject()` 无父孤立对象要显式 `.Parent`；`LocalScale/WorldScale`（无 `Scale`）；浮点插值 `MathX.Lerp`（`MathF.Lerp` 不存在；`Vector3.Lerp`/`MathF.Exp` 可用）。
7. `[Sync]` 只在 `NetworkMode.Object` 生效；`Interpolate` 仅 float/double/Angles/Rotation/Transform/Vector3；集合变更用 `OnChanged` 不是 `[Change]`。
8. `IsValid()` 判活，别用 `!= null`；`await` 后必须 `if ( !this.IsValid() ) return;`。
9. bot 不走 Connection——名字/分数同步靠 `[Sync(FromHost)]`，别尝试伪造连接。
10. HUD 中文需自备 CJK 字体进 `Assets/fonts`。

---

## 8. 待用户决定的事项

- 目标人数与地图尺寸（影响食物数量、NetList 规模、UpdateRate）。
- 是否需要 Steam 大厅公开匹配（`LobbyPrivacy.Public`）还是先好友/本地联调。
- 分裂/吐孢子/绿刺是否进第一版（建议 P3）。
- 球上是否要名牌（WorldPanel，成本不低）。