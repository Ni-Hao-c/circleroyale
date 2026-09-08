
### M6.0 鼠标瞄准：分裂/吐孢子指向光标 + 外缘方向箭头（v0.7.0.0-M5，2026-09-07 用户需求）

用户需求（附 agar 截图）：增加鼠标（摇杆）对吐丝/分身方向的控制，方向指向鼠标所在位置，球上带方向指示。

- **BallInput.GetAimDir**：光标世界方向——正交俯视相机下 `CameraComponent.ScreenPixelToRay(Mouse.Position)` 的射线原点 XY 就是光标世界坐标，对球求单位方向；光标正贴球心回退移动方向。
- **Ball**：`_lastAimDir`/`AimDir` 字段，每帧更新（不按键也追踪光标）；分裂（Jump）/吐孢子（Reload）的方向从"移动方向"改为"瞄准方向"（光标贴心/丢失时兜底移动方向）。
- **NeonRenderer.WriteAimArrow**：本机球外缘的白色实心小三角（加色批次里的 AddTri 新助手），贴球缘 1.04×半径处、长度随球径 18~64 微缩放，实时指向光标——agar 式方向指示。仅本机球（bot 无）。
- 移动仍是 WASD/手柄（2026-09-05 决策不变），只有"动作方向"（分裂/吐孢子）跟随鼠标。
- 工具链：编辑器未开时用 tools/BuildCheck 影子编译验证——**新 SDK 下默认 AssemblyInfo 生成会重复报错，csproj 补 GenerateAssemblyInfo/GenerateTargetFrameworkAttribute=false**；BallInput 补 using System（编辑器隐式全局 using 与影子工程不一致的坑）。

### M6.1 空格 = 全员分裂（agar 标准，v0.7.1.0-M5，2026-09-07 用户需求）

用户需求："按空格键分身的时候，所有的身体都会分身"——原实现只分裂主球（每按一次+1 身体，铺满 16 分身要按 16 次）。

- **DoSplit 改 agar 标准**：空格 = 主球 + 每个够重的分身**同时对半分裂**，新身体全部朝瞄准方向（光标）弹出；连按 1→2→4→8→16 只要 4 按。
- 每颗身体独立过门槛：对半后质量 ≥ SplitMinMass（不击穿下限 1）才分；分身槽满（16）即停。
- 倒序遍历 + 尾部追加，本轮新生成的分身不参与再分裂；新分身默认合并冷却 1s，不会秒回主球。
- bot 的 split-hunt 也走此路径（全队扑击，5s 冷却 + 概率门槛限流）。

### M6.2 心跳误判修复：中途加入即被踢回主菜单（v0.7.1.1-M5，2026-09-07 用户反馈）

用户反馈："客户端一进入游戏就会返回到主菜单"。根因是 v0.6.8.0 房间心跳的连带 bug：**host 点 START 进对局后房间名单广播（心跳）停止**，但等在房间页的客户端 armed 状态还在——6 秒收不到 LobbyState 就误判"host 死了"→ 强制拆线 → 重连耗尽 → 回主菜单。中途加入的客户端（OnActive 推完 LobbyState/MatchSettings/MatchStarted 后停在房间页）最典型。

- **两条心跳按 host 实际阶段取一**：`matchSilent = (_gameStarted || _matchRunning) && !MatchOver && MatchTick 静默`（对局心跳反过来覆盖"客户端在房间页等、host 对局还在跑"——host 死了 MatchTick 停，照样检测）；`lobbySilent = _lobbyWaiting && armed && !_matchRunning && LobbyState 静默`。
- **EnterClientWaiting 统一入口**（4 处直接赋值收编）：进入等待态时**解除武装 + 重置计时**——进入的时刻 host 不一定在房间阶段，旧计时器/armed 会立即误判；必须等 host 下一条 LobbyState 重新武装（ResetToLobby/重连恢复同样受益）。

### M6.3 客户端输入锁死 + UI 闪烁修复（v0.7.1.2-M5，2026-09-07 用户反馈）

用户反馈：客户端 UI 一闪一闪、不能分身/吐丝、箭头不跟随鼠标。

- **输入锁死根因**：v0.6.9.0 的 `IsClientWaiting` 停控守卫只看 `_lobbyWaiting`——该标志若异常残留为 true（人已在局内），Ball.OnUpdate 在瞄准更新之前 early-return → 移动/分裂/吐孢子/瞄准全锁死，箭头冻在旧方向。修：守卫加 `!IsMatchStarted`——只要还在对局（_gameStarted）就绝不禁输入；停控仅对"P 退局/掉线后"的停泊球（那两种状态 _gameStarted=false）。
- **箭头仅对局中显示**：房间页停泊的球 AimDir 已冻结，画出来没有意义。
- **UI 闪烁根因**：Label.Text 每帧重设（哪怕同值）触发布局重算——ReconcileUi 每帧 ShowClient→RefreshStatus 重刷房间页状态行。修：LobbyPanel/GameHud 全部逐帧文本路径加 `SetText` 防抖（同值不赋）——状态行/设置/名单/质量/排行榜/计时器/队伍条/队友区/名牌。

### M6.4 分身真分离：贴外缘生成（v0.7.2.0-M5，2026-09-07 用户反馈）

用户反馈："主要的分身死去就死亡了，想要全部分身死亡才会死去"。晋升机制本身没问题（三条死亡路径都走 TryPromoteFromPieces），**根因是分身从未真正离开过主球**：
分身生成在主球**内部**（0.4×半径处），冲量行程只有 ~333 世界单位；大球分裂时分身飞不出"主球半径+分身半径"的合并吸附范围，**1s 冷却一到全被吸回主球**——玩家实际永远只有一颗身体，等主球被吃时无身可晋升，直接真死。

- **SpawnPiece**：分身生成位置改为主球外缘（主球半径 + 新分身半径处），冲量 1000 继续外推——出生即在吸附范围之外，合并冷却后也不会被吸回。
- **DoSplit 的分身对半（twin）**：同样贴母分身外缘生成。
- 晋升流程不变：主球被吃 → 最大分身原地升级成新主球（质量/位置承接，Alive 不翻转）→ 全部身体死光才真死。
