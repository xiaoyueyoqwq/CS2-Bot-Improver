# Bot 听从人类指令 — 设计笔记（暂存）

日期：2026-09-10  
状态：只记思路，未实现、未进 Plan Mode、未改代码、未上服务器。

方向已经对齐：不重写 Bot AI，也不用 BotController 锁住 WASD 当傀儡。  
Bot 本来就会打 A/B、会搜点、会转点；缺的不是更强的自主判断，而是服从。人类作判断，AI 执行。执行时仍走现有寻路/枪法/搜点，不另写一套走位。

### 立场

在 AI 行为受限（Valve 脑、发现速度差、不会 IGL）的前提下，最好的补充不是增强 AI 自己的决策，而是绝对服从人类指令。AI 的任务是协助人类，不是替代人类做战术判断。

因此：

- 不去做「更好的自动发现炸弹 / 自己决定转点」这类增强。
- 人类轮盘、标记、无线电一旦构成指令或事实，Bot 按它执行，不拿自己的 CSGameState 旧结论顶回去。
- 经济、发枪已经是 BotBuy 自我决策，且比人类快，不在服从范围内。
- 「绝对服从」指判断权在人类；怎么走到点、怎么架、怎么交火，仍用原有 AI。

开局 3/2 已拍板（A 方案）：人类占主攻名额；假打至少 2 人；主攻人数 > 假打人数。假打点空了立刻会合；假打点有人则留下打、给人类信息。窗口外人类明确改点 → 全员去新点，不再分兵。T/CT 人数规则相同，任务类型不同（Rush vs 堆点/看点），必须按阵营分支，防止用 T 的搜点去驱动 CT。

---

## 1. 问题是什么

人类已经知道炸弹在 B，Bot 还往 A 走。  
人类判断 B 空了要转回 A，Bot 还在 B 蹲。

这不是「Bot 不会打 A 打 B」，而是发现/共享速度和人类差太多。  
要做的是：让 Bot 能被人类的轮盘/无线电/标记喂信息，同时继续走原有 AI。

---

## 2. 已确认规则（2026-09-10）

窗口：从 `round_start` 起 10 秒，**含冻结**。只听人类。回合结束 / 下一局 `round_start` 必须清掉指令和注入的搜索顺序。

### 2.1 编制（T/CT 相同）

- 人类占主攻/堆点名额，不占假打名额。
- 假打/看点组 **至少 2 人**（人数不够时见下）。
- 主攻组人数 **必须大于** 假打组。
- 5 人局（1 人 + 4 bot）= **A 方案**：人类 + 2 bot 主攻（3），2 bot 假打/看点。
- 推算：`fake = min(max(2, floor(n*0.4)), floor((n-1)/2))`，且 `n - fake > fake`；`n < 4` 时不拆，全员主攻（无法同时满足「假打≥2」和「主攻>假打」）。
- T 的 C4 必须在主攻组。CT 无此条。

### 2.2 优先级（后到且更明确的覆盖前面）

1. 回合结束 → 清空，禁止带进下一局。
2. 人类明确改点（窗口外 GoA/GoB / Ping Site）→ **全员去新点，不再分兵**。战场在变，AI 始终跟最后一次明确指令。
3. 已安放 / Ping 炸弹 / PlantedAt* / wePlanted → 全员按该点（CT 回防；T 守包，除非人类又明确改点）。
4. 点已清 / 区域安全 → 离开该点，去另一点或当前主攻点。不要在空点发呆。
5. 开局 10 秒内的 3/2 分配：主攻去指定点；假打/看点去另一点。
6. 假打/看点组 **摸到人 → 留下打**，给人类信息；**没人 → 立刻会合主攻点**。
7. 10 秒内无任何 A/B 指令 → 不注入，各自作战。

窗口内改口（先 A 后 B）：只改 3/2 的主攻点，重新分配，最后一次为准。  
窗口外改口：第 2 条，全员转，拆掉假打。

### 2.3 T（进攻）

开局喊 A：3 人 Rush A，2 人 fake B。  
B 空 → 2 人立刻回 A。B 有人 → 2 人留下打。  
人类后来说转 A / Ping A：包括正在 B 交火的人，**全员去 A**。  
下包事实覆盖搜点；人类再喊另一点则放弃当前目标全员过去。

### 2.4 CT（防守）— 已拍板

人数与 T 相同，**任务不是 Rush**：

- 开局喊 A：3 人 **堆/守 A**，2 人 **看 B**（不是假打进点）。
- 看 B 空 → 立刻回 A 会合，禁止 CT 在空 B 继续蹲。
- 看 B 有人 → 留下打，给人类信息（B 有人/要打 B）。
- 窗口外人类明确 GoA/GoB：全员去该点（转点/回防），不再留人看另一点。
- 包已下 / Ping 炸弹：全员去下包点。BotAI 引擎事件仍有效；人类在蜂鸣前的 ping 必须同样生效。
- 人类 Clear 某点（包未下）：离开该点去另一点。
- 开局无指令：不注入，Valve 默认分点。
- **不要**把 T 的「搜点去下包」搜索顺序套到 CT，否则 CT 会像进攻方一样往点里 Rush。

### 2.5 第一期范围

做：开局 3/2（T Rush + CT 堆/看）、空点会合、接触留下、窗口外全员改点、下包/点已清/Ping 炸弹、回合清空。  
第二期等第一期实测后再做。  
第一期不做：团队聊天「打A/打B」、要手雷、人质图、BotBuy、Hold。

优先级表可随时插档，不必重写第一期。第二期（跟随/支援/中路/敌人 ping）只是在这张有序表里加一行：例如 Follow 低于「明确改点」但高于开局 3/2；NeedBackup 只拉 1–2 个空闲 Bot，不拆掉主攻点。新指令开工时再定档位。

---

## 3. 注入层（不要新寻路）

两类写入，都复用现有 Bot 脑：

1. **世界事实**  
   已安放点、最后敌人位置、掉包、听到的噪音 → `CSGameState` / `CCSBot` 的 LastEnemy / Noise / planted site。
2. **战术意图**  
   开局搜索顺序、Clear 某点、Follow、Hold。不要只写 `GoalPosition`：Idle 会按旧搜索顺序重选目标。写完后可能需要 `Idle()` / `RepathTimer`，让正在 Hide/Guard 的 Bot 重新规划。

BotAI 1.8.9 已经加速的是**引擎事件之后**：安放全队学到点、T 搜已知安放点、CT 全局听蜂鸣/捡包。  
它不消费人类的 ping / 轮盘。剩下的缺口正好是：

- 安放前的执行喊点
- 蜂鸣前人类已经看到/喊出的信息
- 空点转走

监听面（CSS 已有，当前插件都没用）：

- `EventPlayerPing`（Userid, X/Y/Z, Urgent, Entityid）
- `EventPlayerPingStop`
- `CPlayerPing`（Type, Urgent, PlaceName 最长约 18，例如 BombsiteA/B, PingedEntity, Player）
- `CCSPlayer_PingServices.PlayerPing`
- `EventPlayerRadio`（Userid, Slot）
- `EventPlayerChat`（Teamonly, Userid, Text）
- pawn `LastPlaceName`（NadeSystem 出站手雷无线电已经在用）

未解、实现前必须 dump / 实机打日志：

- CS2 的 `m_bombsiteSearchOrder` / `ClearBombsite` / `MarkBombsiteAsPlanted` 偏移，或直接调 native `CSGameState` 函数
- `CPlayerPing.Type` 的实机枚举
- `EventPlayerRadio.Slot` 与 `CW.*` / 经典无线电的对照表

已知冲突：

- `Idle_IsSafeAlwaysFalse` 会顶「守住这个位置」，但对开局 3/2 有利（Bot 会离开出生点）。
- BotState `OnTick` 目前不覆盖 Follow / Goal / Radio。
- NadeSystem 区域接管可能和转点打架。
- `botprofile` Teamwork 40–100 只在走原生无线电响应时才有意义。

不要用 BotController `Lock(All)` + WASD。那是另一条路。

---

## 4. CS2 现在人类怎么发指令

三套并行，插件都要听，不能只认轮盘。

| 通道 | 默认键 | 服务端事件 | 备注 |
| --- | --- | --- | --- |
| 快捷轮盘 1/2/3 | `C`=`+radialradio`，`V`=`+radialradio2`，`+radialradio3` 常未绑定 | `playerchatwheel CW.*` + `EventPlayerRadio` | 每页 8 格，可自定义 |
| 经典无线电菜单 | `Z`=`radio`，另有 `radio1`/`radio2`/`radio3` | `EventPlayerRadio` Slot | `go_a`/`go_b` 在 CS2 上常失效 |
| 准星标记 | 鼠标中键 `player_ping` | `EventPlayerPing` / `CPlayerPing` | 打在准星处；轮盘里的 A/B/中路/炸弹是静音 ping |

玩家可把任意 `#Chatwheel_*` 绑到三页轮盘。默认页只是常用子集，完整指令表比默认 24 格大得多。  
`sv_playerradio_use_allowlist` 默认 1：旧 CS:GO `playerradio Radio.Loc*` 地点喊话默认被拦，私服关掉才通。

轮盘格子编号（顺时针，社区 CFG 常用）：

```
      3
   2     4
 1         5
   8     6
      7
```

### 4.1 默认三页（设置里的出厂绑定）

来源：社区 CFG 指南整理的 Valve 默认，不是我们服务器实测。玩家改过设置后，服务端仍能从 `CW.*` / ping 认出同一条指令。

轮盘 1（`+radialradio`，C）：

1. Go go go!
2. Stay together
3. Request a weapon
4. What's the plan?
5. Fall back
6. We should save
7. We should buy
8. Spread out

轮盘 1 的「要枪 / 该存 / 该买」对人类队友有用。对本仓库无效：Save/Hard 是 BotBuy 自己的经济决策，开局约 1 秒就买完；发枪也是 BotBuy 自己驱动，人类还没翻到轮盘格子 Bot 已经丢出来了。这三条不要接进指令跟随。

轮盘 2（`+radialradio2`，V）：

1. Cheer
2. No
3. Ping Site B
4. Thanks!
5. Sorry
6. Pep talk
7. Ping Site A
8. Yes

轮盘 3（`+radialradio3`）：

1. Enemy spotted
2. Heard noise
3. Multiple enemies here
4. Bomb was dropped
5. Need Backup
6. You De/Activate Bomb
7. Area clear
8. Sniper spotted

注意：默认页**没有** Mid、Rotate to me、Bombsite clear、Planted at A/B。这些要玩家改轮盘或走控制台/绑定才会发出来，但服务端同样收得到。

### 4.2 静音 ping（默认轮盘里最像「打 A/B」的东西）

`CW.GoA` / `CW.GoB` / `CW.GoToLocMid` / `CW.BombAt` 多数情况下**不说话**，只在对应位置丢标记。  
人类说「打 B」时，实际发出的经常是「Ping Site B」，不是经典 `go_b`。  
开局 3/2 必须认这几条，不能只等有语音的 `GoGoGo`。

另有 `chatwheel_ping #Chatwheel_*`：部分指令会在自己头上或准星处再打一个 ping（Follow me / On my way / Rotate to me / Enemy spotted）。

---

## 5. 完整 Chatwheel 表（`playerchatwheel CW.*`）

对照来源：

- 2025 Steam 指南 sountain「Chat Wheel Phrases as Console Commands」（完整 `CW.*` + 是否有语音）
- SteamTracking/GameTracking-CS2 `game/csgo/pak01_dir/resource/csgo_english.txt`（2026-09 仍有这些 `#Chatwheel_*` token）
- 仓库归档 `overrides/archived/localizations/csgo_english.txt` / `csgo_schinese.txt`（中英对照）

有语音以 2025 指南标记为准；Agent 语音包不同，个别会哑。  
`#Chatwheel_*` 是界面/聊天文本；`CW.*` 才是语音槽。两者不是同名。

### 准备 Preparation

| CW.* | #Chatwheel_* | 英 | 中 | 语音 | 对 Bot 的可能用法 |
| --- | --- | --- | --- | --- | --- |
| EcoRound | requestecoround | We should save | 我们应该存钱 | 无 | **不接。** Save/Hard 是 BotBuy 自我决策，开局约 1 秒已买完，人类再喊存/起来不及也没必要 |
| SpendRound | requestspend | We should buy | 我们应该购买 | 无 | **不接。** 同上 |
| NeedDrop | requestweapon | Request a weapon | 需要一把武器 | 无 | **不接。** 发枪积极性由 BotBuy AI 自己驱动，人类还没翻到这个选项 Bot 已经给枪了 |
| NeedPlan | requestplan | What's the plan? | 有什么计划？ | 无 | 弱信号，可不理 |
| NeedLeader | requestleader | Who will lead? | 谁来带队？ | 无 | 弱信号；或让最近 Bot Follow 人类 |

### 移动 Movement

| CW.* | #Chatwheel_* | 英 | 中 | 语音 | 对 Bot 的可能用法 |
| --- | --- | --- | --- | --- | --- |
| GoGoGo | gogogo | Go go go! | 行动！行动！ | 有 | 开局无 A/B 时催离出生；或推进当前目标 |
| OMW | onmyway | On my way | 在路上 | 无 | 信息性；头上 ping |
| FollowMe | followme | Follow me | 跟着我 | 有 | `CCSBot.IsFollowing` + Leader=人类 |
| FollowingYou | followingyou | Following you | 跟着你 | 有 | 人类声明自己在跟；Bot 不必动 |
| GoA | aplan | Ping Site A | 侦测 A 点 | 无（静音 ping） | **开局 3/2 主入口**；局中=执行/转 A |
| GoB | bplan | Ping Site B | 侦测 B 点 | 无（静音 ping） | 同上，B |
| GoToLocMid | midplan | Ping Middle | 侦测中路 | 无（静音 ping） | 第三人称目标：lurk/控中，不要清掉 A/B |

### 命令 Commands

| CW.* | #Chatwheel_* | 英 | 中 | 语音 | 对 Bot 的可能用法 |
| --- | --- | --- | --- | --- | --- |
| Regroup | rotatetome | Rotate to me | 转向我 | 有 | Follow 喊话者；头上 ping |
| StickTogether | sticktogether | Stay together | 集中在一起 | 有 | 收束到人类/C4 附近，不要再拆太开 |
| SpreadOut | spreadout | Spread out | 散开 | 无 | 减弱抱团；可取消 Stick |
| TeamFallBack | fallback | Fall back | 撤退 | 有 | 从当前接触面后撤，不要 Clear 已占点 |
| HoldPosition | holdposition | Hold this position | 守住这个位置 | 有 | Hold/Guard；**和 Idle_IsSafeAlwaysFalse 冲突** |
| CheckHostage | gethostage | Check the Hostage | 检查人质 | 无 | 人质图才有意义 |
| NeedQuiet | quiet | Need quiet | 别出声 | 无 | 弱；最多压 Bot 语音/少走 |
| ImAttacking | attacking | Attacking | 进攻 | 无 | 局中推进当前点，类似 GoGoGo |
| RequestGetHostages | requestgethostages | Get the hostages | 解救人质 | 无 | 人质图 |

### 报告 Report

| CW.* | #Chatwheel_* | 英 | 中 | 语音 | 对 Bot 的可能用法 |
| --- | --- | --- | --- | --- | --- |
| HeardNoise | heardnoise | Heard noise | 听到噪音 | 有 | NoisePosition=喊话者处 |
| SeesEnemy | enemyspotted | Enemy spotted | 发现敌人 | 有 | LastEnemy / 朝 ping 看 |
| SeesSingleEnemy | oneenemyhere | One enemy here | 这儿有名敌人 | 有 | 同上，强度低 |
| SeesEnemiesMultiple | multipleenemieshere | Multiple enemies here | 这里发现数名敌人 | 有 | 同上，强度高；可把搜索偏向该点 |
| NeedBackup | needbackup | Need Backup | 需要支援 | 有 | 最近 1–2 个空闲 Bot 朝喊话者靠 |
| SniperWarning | sniperspotted | Sniper spotted | 发现狙击手 | 有 | LookAtSpot；不要当「清点」 |
| BombCarrierHere | bombcarrierspotted | Bomb carrier spotted | 发现炸弹携带者 | 无 | CT：LastEnemy+搜该侧 |
| InPosition | inposition | In position | 已就位 | 有 | 信息性 |
| CoveringYou | coveringyou | Covering you | 在掩护你 | 无 | 信息性 |
| SectorClear | sectorclear | Area clear | 区域安全 | 有 | **局中转点**：Clear 喊话者所在点 |
| ISeeBomb | bombcarrierspotted | Bomb carrier spotted | 发现炸弹携带者 | 无 | 指南里和 BombCarrierHere 共用文本；实机 Slot 需确认是否同槽 |
| wePlanted | planted | We planted the bomb | 我们已经安放炸弹 | 无 | T 告知已下；CT 应立即当 planted（仍缺 A/B 则等 ping/蜂鸣） |
| PickedUpC4 | bombpickedup | Bomb picked up | 捡起炸弹 | 无 | 取消掉包事实 |
| SeesHostagesBeingTaken | seehostagestaken | The hostages are being taken | 人质正在被解救中 | 无 | 人质图 |
| BombsiteClear | bombsiteclear | Bombsite clear | 清除炸弹 / 点已清 | 有 | **比 SectorClear 更硬**：Clear 对应 Bombsite |
| GuardingA | guardinga | I'm guarding A | 我在防守 A 点 | 无 | 信息性；Bot 可把该点当已有人看 |
| GuardingB | guardingb | I'm guarding B | 我在防守 B 点 | 无 | 同上 |

### 炸弹 Bomb Status

| CW.* | #Chatwheel_* | 英 | 中 | 语音 | 对 Bot 的可能用法 |
| --- | --- | --- | --- | --- | --- |
| IFixBomb | ifixbomb | I'll De/Activate Bomb | 我来拆除/激活炸弹 | 无 | 人类声明自己拆/下；Bot 去 Cover 而不是抢包 |
| YouFixBomb | youfixbomb | You De/Activate Bomb | 你去拆除/激活炸弹 | 无 | 让最近有资格的 Bot 去拆/下 |
| DroppedBomb | droppedbomb | Bomb was dropped | 炸弹已投掷 | 有 | 掉包事实；CT 可搜松包 |
| GuardingDroppedBomb | guardingbomb | Guarding the Bomb | 保卫炸弹 | 无 | 信息性 |
| BombAt | bombat | Ping Bomb | 侦测炸弹 | 无（静音 ping） | 松包/C4 位置；CT 搜 ping 点 |
| WeHaveTheBomb | ihavethebomb | Bomb picked up | 捡起炸弹 | 无 | 同 PickedUpC4 |
| PlantingAtA | plantingata | I'm planting at A | 我正在 A 点安放炸弹 | 无 | T 执行偏置；CT 当 A 即将下 |
| PlantingAtB | plantingatb | I'm planting at B | 我正在 B 点安放炸弹 | 无 | 同上 B |
| PlantedAtA | plantedata | I planted at A | 我已在 A 点安放炸弹 | 无 | **MarkBombsiteAsPlanted(A)**，覆盖开局 3/2 |
| PlantedAtB | plantedatb | I planted at B | 我已在 B 点安放炸弹 | 无 | 同上 B |

### 回应 Responses（多数可不驱动走位）

| CW.* | #Chatwheel_* | 英 | 中 | 语音 |
| --- | --- | --- | --- | --- |
| Agree | affirmative | Yes | 是 | 有 |
| Disagree | negative | No | 否 | 有 |
| Compliment | compliment | Nice! | 漂亮！ | 有 |
| Thanks | thanks | Thanks! | 谢了！ | 有 |
| Cheer | cheer | Cheer | 欢呼！ | 有 |
| PepTalk | peptalk | Pep talk | 加油打气 | 有 |
| Sorry | sorry | Sorry | 对不起 | 无 |
| RoundLost | lostround | Lost this round | 这局白给了 | 有 |
| IKilledSniper | ikilledsniper | I killed sniper | 我击杀了狙击手 | 有 |
| MyHeadshot | gotheadshot | I got a headshot | 一发爆头 | 有 |
| SawHeadshot | sawheadshot | Saw your headshot | 精彩的精准打击 | 有 |

Agree/Disagree 以后若做「Bot 提议、人类确认」才有用，现在不必接走位。

### 投掷物 Grenades

| CW.* | #Chatwheel_* | 英 | 中 | 语音 | 对 Bot 的可能用法 |
| --- | --- | --- | --- | --- | --- |
| NeedDecoy | decoy | Need Decoys | 需要诱饵弹 | 无 | 可转 NadeSystem |
| NeedSmoke | smoke | Need Smokes | 需要烟雾弹 | 无 | 可转 NadeSystem |
| NeedGrenade | grenade | Need Frag Grenades | 需要破片手雷 | 无 | 可转 NadeSystem |
| NeedFire | fire | Need Molotovs | 需要燃烧瓶 | 无 | 可转 NadeSystem |
| NeedFlash | flashbang | Need Flashbangs | 需要闪光震撼弹 | 无 | 可转 NadeSystem |

这些是「我需要」不是「我要扔」。和 NadeSystem 现有出站无线电方向相反：人类要道具 vs Bot 汇报自己在扔。

---

## 6. 有 `#Chatwheel_*` 文本、但 2025 `CW.*` 表里没有的

GameTracking 的 `csgo_english.txt` 里还有这些 token。它们出现在设置分类/旧界面，**不能当成一定能从轮盘发出的指令**。实现时若 `EventPlayerRadio` 打出未知 Slot，再补。

- `plant` Plant the bomb
- `sitea` / `siteb`（和 aplan/bplan 英文相同「Ping Site A/B」，可能是旧名）
- `coverme` Cover me
- `help` Help
- `threeleft` / `twoleft` / `oneleft`
- `covering` Covering
- `pinned` I'm pinned
- `incombat` In combat
- `friendlyfire` Friendly fire
- `lastunitalive` Last unit alive
- `wewon` / `welost` / `wearewinning`
- `wekilledsniper`

分类标题（不是指令）：section_move / prepare / grenades / defend / bomb / enemystatus / enemykilled / combatstatus / responses / command / report。

---

## 7. 经典无线电（`Z` 菜单 / 控制台别名）

这些走 `EventPlayerRadio` Slot，**不是** `CW.*`。CS2 上部分已哑，Hitman 2025 指南用 ✅/❌ 标过。

仍较常可用：

`go` `fallback` `sticktog` `holdpos` `followme` `coverme` `takepoint` `regroup` `report` `reportingin` `enemyspot` `needbackup` `takingfire` `sectorclear` `inposition` `enemydown` `roger` `negative` `thanks` `compliment` `cheer` `getout`

CS2 上常失效或半残：

`go_a` `go_b` `needrop` `sorry`

旧 `playerradio Radio.LocBombA` 一类地点喊话：默认 allowlist 拦。不要作为主入口。

和轮盘的对应关系（语义接近，事件不同）：

| 经典 | 轮盘 |
| --- | --- |
| go | CW.GoGoGo |
| go_a / go_b | CW.GoA / CW.GoB（静音 ping，更常用） |
| followme | CW.FollowMe |
| holdpos | CW.HoldPosition |
| fallback | CW.TeamFallBack |
| sticktog | CW.StickTogether |
| coverme | （轮盘只有 CoveringYou，方向反了） |
| needbackup | CW.NeedBackup |
| sectorclear | CW.SectorClear |
| enemyspot | CW.SeesEnemy |
| needrop | CW.NeedDrop（经典常失效；即便能到也不接 BotBuy） |

开局 3/2 应同时认：`CW.GoA/GoB` ping、经典 `go_a/go_b`（若 Slot 还能到）、以及团队聊天里的「打A/打B」（可选，后做）。

---

## 8. 扩展思路（按值得做的顺序）

下面都还是想法，不是实现清单。

### 第一层：信息差（最贴你说的两个例子）

1. **开局 GoA/GoB → 3/2：Rush + fake，空点立刻会合主攻点**（已拍板；5 人编制数字待确认）
2. **PlantedAtA/B、wePlanted、BombAt ping → MarkBombsiteAsPlanted**  
   人类已经看见下包，不必等蜂鸣。这是「Bot 还往空点走」的主因。
3. **BombsiteClear / SectorClear → ClearBombsite(喊话者所在点或 ping 点)**  
   人类说 B 空了，Bot 把 B 标清，自己的搜点 AI 会去下一个点。不要把人拖去人类身边。
4. **DroppedBomb / Ping Bomb → 松包事实**  
   CT 按现有搜包逻辑走，只是提早知道。

### 第二层：队形/跟随（仍用 CCSBot 字段，不锁 WASD）

5. **FollowMe / Rotate to me → IsFollowing + Leader**
6. **NeedBackup / takingfire → 1–2 个空闲 Bot 朝喊话者**
7. **StickTogether / SpreadOut**  
   收束或取消收束；不要写成硬编队。
8. **HoldPosition**  
   先不要做，或做成短时 Guard。和 `Idle_IsSafeAlwaysFalse` 对打。
9. **TeamFallBack**  
   从接触面后撤到出生/另一点，不要 Clear。

### 第三层：局部感知

10. **HeardNoise / SeesEnemy / 单人/多人 / Sniper / 中键危险 ping**  
    LookAtSpot + LastEnemy/Noise。这是最早提过的「危险标记」，现在排在炸弹事实后面。
11. **GoToLocMid**  
    第三人称目标，不是第三炸弹点。少数人去中，多数仍按 A/B 拆分。
12. **GuardingA/B**  
    该点已有人看，开局拆分时少派。

### 第四层：和现有插件衔接，而不是新 AI

13. **NeedSmoke/Flash/Fire/Grenade/Decoy → NadeSystem 请求（仅作候选）**  
    和 Bot 出站手雷无线电分开，避免对打。经济/发枪不要学这一层。
14. **YouFixBomb → 让有资格的 Bot 去拆/下；IFixBomb → 其他人 Cover**

### 明确不做

- 用 BotController 锁视角/WASD 演人类。
- 把每一条轮盘都映射成强制走位。回应类、Pep talk、Cheer 保持闲聊。
- 重写搜点/走位/枪法。
- 把「Clear B」做成把所有人拉到人类身边。
- **不要用轮盘去指挥 BotBuy。** EcoRound / SpendRound / NeedDrop（以及经典 `needrop`）全部忽略。Save/Hard 是 BotBuy 自己的经济决策，新回合约 1 秒内已经买完；发枪也是同一套 AI 自己驱动，比人类翻轮盘快。人类这三条是给人类队友用的，接进去只会和 BotBuy 抢决策。

---

## 9. 实现前还缺的实测

1. 实机打日志：人类轮盘 GoA/GoB、中键 ping、经典 `go`/`holdpos` 分别产生什么 `EventPlayerRadio.Slot`、`CPlayerPing.Type`、`PlaceName`。
2. `libserver.so` dump：`CSGameState` 搜点顺序、`ClearBombsite`、`MarkBombsiteAsPlanted` 在 CS2 的偏移或函数。
3. 确认 `CW.ISeeBomb` 是否真是独立槽，还是和 `BombCarrierHere` 撞文本。
4. 冻结时间内轮盘是否已经进 `player_radio`（决定 10 秒窗口从哪算）。

---

## 10. 和本仓库的关系

- BotAI 已经在引擎事件后加速学包；本设计补的是**人类事件**。
- NadeSystem 已用 `LastPlaceName` 和出站无线电；入站「需要烟雾」不要复用那条出站路径。
- BotBuy 的 Save/Hard 和发枪都是自我决策，开局极快。指令跟随不要碰 BotBuy，也不要把 EcoRound / SpendRound / NeedDrop 当入口。
- BotState 写 CCSBot 字段的方式可复用；不要在 OnTick 里每帧覆盖 Follow/Goal。

来源（查阅用，不必写进用户 README）：

- https://steamcommunity.com/sharedfiles/filedetails/?id=3522476376
- https://steamcommunity.com/sharedfiles/filedetails/?id=3055798463
- https://steamcommunity.com/sharedfiles/filedetails/?id=3078337270
- https://steamcommunity.com/sharedfiles/filedetails/?id=3038202828
- https://github.com/SteamTracking/GameTracking-CS2 （csgo_english.txt Chatwheel 段，ClientVersion 2000905 / 2026-09-07）
- 仓库 `overrides/archived/localizations/csgo_english.txt` 与 `csgo_schinese.txt`
