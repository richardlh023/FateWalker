# FateWalker — แผนผังการตัดสินใจ (v1.0.0.30)

อ้างอิงโค้ดจริงจาก `Controller/FateController.cs`. แต่ละกล่องระบุ method ที่เกี่ยวข้องเพื่อตามไปอ่านได้เลย.

---

## 1) Top-level: `Tick()` ทุก frame

```mermaid
flowchart TD
  A[Framework.Update → Tick] --> B{State == Stopped?}
  B -->|yes| END[return — bot ไม่ทำอะไร]
  B -->|no| C{IsLoggedIn?}
  C -->|no| STOP[Stop — บอทหยุดเอง]
  C -->|yes| D{BetweenAreas /<br/>CutScene?}
  D -->|yes| END
  D -->|no| E[Sample durability ทุก 5s]
  E --> F{Auto-Repair?<br/>dur < threshold<br/>+ state allows}
  F -->|fire| REP[Transition → Repairing]
  F -->|skip| G{Auto-Trade?<br/>gems ≥ trigger<br/>+ vendor known<br/>+ state allows}
  G -->|fire| TRD[Transition → Trading]
  G -->|skip| H{Session Cap?<br/>elapsed > rolled hours<br/>+ state allows}
  H -->|fire| PRE[EnterPauseSafely → PreparingPause]
  H -->|skip| I{Death Override?<br/>Unconscious +<br/>state allows}
  I -->|fire| DIE[Transition → Dying]
  I -->|skip| J[EnsureSelectYesnoHandled<br/>+ ProcessPendingSelectYesno]
  J --> K[Stats heartbeat 5min]
  K --> L[CheckPluginAvailability]
  L --> M[GenericStuckWatchdog<br/>+ CheckLogicLoop]
  M --> N{Dispatch State}
  N --> S1[Selecting] & S2[Teleporting] & S3[Mounting] & S4[Traveling]
  N --> S5[Interacting] & S6[Engaging] & S7[Dying] & S8[PreparingPause]
  N --> S9[Paused] & S10[Repairing] & S11[Trading] & S12[Recovering]
```

### Exclusion lists (ที่ block trigger preempt)

| Trigger | Excluded states |
|---|---|
| **Auto-Repair** | Repairing, Dying, Paused, **PreparingPause**, Teleporting, Interacting, Trading |
| **Auto-Trade** | Trading, Repairing, Dying, Paused, **PreparingPause**, Teleporting, Interacting |
| **Session Cap** | Paused, **PreparingPause**, Dying, **Repairing**, **Trading** |
| **Death Override** | Stopped, Dying เท่านั้น (อย่างอื่นถูก interrupt ได้) |

---

## 2) `TickSelecting` — เลือก FATE ถัดไป

```mermaid
flowchart TD
  A[TickSelecting] --> B{Player null?}
  B -->|yes| END[return]
  B -->|no| C{workingSet ว่าง?}
  C --> SK1[Skip rotation block]
  C -->|มีติ๊ก| D[outsideWorkingSet?<br/>= ไม่อยู่ใน ticked zones<br/>OR territory ไม่ใช่ FATE zone]
  D --> E{currentZoneMaxed?<br/>Shared FATE rank max<br/>+ SkipMaxed mode}
  E --> F{outside OR maxed?}
  F -->|yes| G[TryRotateZone → Teleporting]
  F -->|no| SK1
  SK1 --> H{ManuallyPickedFateId?}
  H -->|yes| I[ใช้ manual pick]
  H -->|no| J[FateSelector.Evaluate]
  J --> K[filter: zone allowed,<br/>FateState Running/Preparing-with-NPC,<br/>level delta,<br/>blacklist FateId,<br/>blacklist name pattern,<br/>sessionDisabledFateIds,<br/>FateTimeRemaining ≥ N s]
  K --> L{มี FateCandidate?}
  L -->|no| M[drought timer start<br/>หรือ TryRotateZone]
  L -->|yes| N{ThinkBeforePick<br/>humanize delay<br/>ผ่านยัง?}
  N -->|รอ| END
  N -->|ผ่าน| O[Set _targetFateId/Name/Pos<br/>+ landing offset<br/>+ MotivationNpcId<br/>+ Radius]
  O --> P{IsInFateRange?<br/>≤ Radius × 0.7}
  P -->|yes| Q{มี MotivationNpc?}
  Q -->|yes| Interact[→ Interacting]
  Q -->|no| Engage[→ Engaging]
  P -->|no| R{dist > LongRangeTeleportYalms?<br/>default 1500y}
  R -->|yes| TpZone[→ Teleporting<br/>ไป zone aetheryte]
  R -->|no| S{Mounted?}
  S -->|yes| Travel[→ Traveling]
  S -->|no| Mount[→ Mounting]
```

---

## 3) `TryRotateZone` — เลือก zone ถัดไป

```mermaid
flowchart TD
  A[TryRotateZone] --> B{workingSet ว่าง?}
  B -->|yes| Log[Log "tick zones in Zones tab"<br/>+ return false]
  B -->|no| C[Compute urgentRotate =<br/>outside OR not in TerritoryMap OR<br/>currentZone maxed]
  C --> D{urgent?}
  D -->|no| E[Check drought timer<br/>+ humanize hesitate]
  E -->|รอ| END[return false]
  D -->|yes/elapsed| F{Lifestream ready?}
  F -->|no| LOG2[Log + return]
  F -->|yes| G[candidates =<br/>workingSet excl current<br/>+ excl _lastDeparted ถ้า ไม่ urgent<br/>+ excl maxed zones]
  G --> H{candidates ว่าง?}
  H -->|no| I[Pick nextTerritory<br/>round-robin หรือ first ถ้า urgent]
  H -->|yes ครบเงื่อนไข| J{SkipMaxed ON?<br/>+ workingSet.All ติด maxed?}
  J -->|yes| K[**Auto-disable SkipMaxed mode**<br/>+ return false]
  J -->|no| L[Log "no candidate" + return]
  I --> M[Transition → Teleporting<br/>กับ nextTerritory + aetheryte]
```

---

## 4) `TickTeleporting`

```mermaid
flowchart TD
  A[TickTeleporting] --> B{TeleportFired AND<br/>territory matches AND<br/>NOT BetweenAreas?}
  B -->|yes| ARR[Arrived → Selecting]
  B -->|no| C{DryRun? +<br/>2s passed?}
  C -->|yes| DRY[Pretend arrived → Selecting]
  C -->|no| D{1s grace<br/>+ 5s throttle?}
  D -->|รอ| END[return]
  D -->|ผ่าน| E[TryCloseShopExchangeCurrency]
  E --> F[Lifestream.Teleport]
  F -->|ok| G[_teleportFired = true]
  F -->|False| H[Log rejected + retry 5s]
  H --> I{state-entry > 30s timeout?}
  I -->|yes| ABORT[Abort → Selecting]
```

---

## 5) `TickEngaging` — สมองหลักของ combat

```mermaid
flowchart TD
  A[TickEngaging] --> B{FATE found?}
  B -->|no| REC[→ Recovering]
  B -->|yes| C{State == Ending/Failed?}
  C -->|yes| DONE[Log FATE done + IncrementLocal<br/>→ Recovering]
  C -->|no| D{stranded?<br/>distToFate > Radius × 2<br/>+ OOC}
  D -->|yes| MOUNT[Deactivate AI → Mounting]
  D -->|no| E{Panic-escape?<br/>HP < panic threshold}
  E -->|yes| PANIC[Run away → Recovering]
  E -->|no| F{Mounted?}
  F -->|yes| DISMOUNT[TryDismountOrRescue]
  F -->|no| G{BossMod activated?}
  G -->|no| H[Activate BossMod preset<br/>+ DelayMovement=None<br/>+ SetTargetRangeOption<br/>+ RSR Manual mode if RSR backend<br/>+ AutoTarget Retarget=NoTarget]
  G -->|yes| I[EnforceFateMobTarget]
  H --> I
  I --> J[KickIfStuckInEngaging]
  J --> K[ForcePullIfStuck<br/>หาก non-collect FATE]
  K --> L[ApplyLazyDodgeBias *disabled*]
```

### `EnforceFateMobTarget` — กลไก targeting

```mermaid
flowchart TD
  A[Throttle 300-1500ms<br/>humanize delay] --> B{Player null?}
  B --> END[return]
  B --> C[isCollect = IsCollectFate]
  C --> D[Read playerBattalion]
  D --> E[Pre-scan: Forlorn Maiden<br/>NameId 6737 หรือ 6738?]
  E -->|มี| FORLORN[Lock target = Forlorn<br/>+ killPhaseLatch=true<br/>+ return]
  E -->|ไม่มี| F[Scan object table<br/>filter: BattleNpc,<br/>not dead,<br/>Combatant,<br/>Hostile flag,<br/>Battalion != player,<br/>FateId == active,<br/>within Radius × 1.5<br/>EXCEPT committed mob]
  F --> G[Partition: aggro<br/>= TargetObjectId == playerId<br/>vs unaggro]
  G --> H{commit valid?}
  H -->|yes| I[Pick commit]
  H -->|no AND grace<4s| J[Return ไม่เปลี่ยน target]
  H -->|no AND grace>4s| K[Drop commit + log]
  I --> L[killPhaseLatch logic:<br/>set true ถ้า aggro≥Max<br/>release ถ้า aggro=0 ต่อเนื่อง 3s]
  K --> L
  L --> M{isCollect AND aggro=0?}
  M -->|yes| COLLECT[clear commit + target = null<br/>+ Retarget=Never<br/>+ return]
  M -->|no| N[Retarget=NoTarget]
  N --> O{stillPulling?<br/>!isCollect AND !killPhaseLatch<br/>AND aggro<Max AND unaggro>0}
  O --> P[Commit aggro'd? drop, pull next]
  O --> Q[Clear-mode safety drop]
  P --> R{พิจารณา}
  Q --> R
  R -->|commit valid| S[Pick commit]
  R -->|stillPulling| T[Pick nearest unaggro]
  R -->|else aggro>0| U[Pick closest aggro<br/>= KILL mode]
  R -->|aggro=0 unaggro=0| V[Log "no mobs" + return]
  S --> W{pick.Id != current target?}
  T --> W
  U --> W
  W -->|yes| X[Set _targetManager.Target<br/>+ log FATE-target]
  W -->|no| END
```

---

## 6) `TickRepairing`

```mermaid
flowchart TD
  A[TickRepairing] --> T1{4 min timeout?}
  T1 -->|yes| AB[Abort → Selecting]
  T1 -->|no| B{Step 1: TeleportFired?}
  B -->|no| C{rejections ≥3 AND InCombat?}
  C -->|yes| FLEE[FleeCombatForRepair<br/>= run 80y from threat]
  C -->|no| D[5s throttle + Lifestream.Teleport<br/>→ Mender aetheryte]
  D -->|ok| TF[_teleportFired=true + reset reject]
  D -->|reject| INC[increment + retry 5s]
  B -->|yes| E[Wait BetweenAreas]
  E --> F{Mounted?}
  F -->|yes| DM[Dismount throttled]
  F -->|no| G[Find Mender NPC by name<br/>in object table]
  G -->|not found 45s| AB
  G -->|found| H{dist > 3.5y?}
  H -->|yes| WALK[navmesh to Mender]
  H -->|no| I[SelectIconString menu<br/>หา "Repair"]
  I --> J[InteractWith Mender]
  J --> K[ใช้ Repair addon callback 4<br/>= Repair All]
  K --> L[SelectYesno auto-confirm<br/>กิล]
  L --> M{durability restored?<br/>>= threshold + 50}
  M -->|yes| BACK[→ Selecting]
```

---

## 7) `TickTrading`

```mermaid
flowchart TD
  A[TickTrading] --> B{Vendor null?}
  B -->|yes| SEL[→ Selecting]
  B -->|no| C{TeleportFired?}
  C -->|no| D[Lifestream.Teleport<br/>→ vendor.AetheryteId<br/>+ 5s throttle]
  D -->|ok| TF[_tradingTeleportFired=true]
  D -->|reject| RETRY[retry 5s]
  C -->|yes| E{AethernetShard set<br/>AND not fired?}
  E -->|yes| F[Aethernet hop<br/>หลัง main teleport]
  E -->|no| G[Wait BetweenAreas + Dismount]
  G --> H[Find vendor NPC by Name<br/>in object table]
  H -->|not in 5s| LOGN[Log nearby NPCs]
  H -->|45s no find| AB[FinishTrading]
  H -->|found| I{dist > 3.5y?}
  I -->|yes| WALK[navmesh to vendor]
  I -->|no| J{ShopExchangeCurrency open?}
  J -->|yes| K{Survey mode?}
  J -->|no| INT[InteractWith vendor<br/>1.5s throttle]
  K -->|Survey| SUR[Read AtkValues<br/>save DiscoveredItems<br/>FireCallback close]
  K -->|Buy| BUY[Find item by id<br/>compute affordable cap<br/>+ inventory limit<br/>FireCallback purchase]
  BUY -->|insufficient| DONE[FinishTrading]
  SUR --> DONE
  DONE --> Z[TryCloseShopExchangeCurrency<br/>+ Transition Stopped/Selecting]
```

---

## 8) `TickDying` → `TickRecovering`

```mermaid
flowchart TD
  A[TickDying] --> B{Unconscious?}
  B -->|no| REC[→ Recovering]
  B -->|yes| C{Raise received<br/>= rev countdown started?}
  C -->|yes| WAIT[Wait + alive → Recovering]
  C -->|no| D{Raise grace expired<br/>RaiseGraceSeconds?}
  D -->|no| END[return wait]
  D -->|yes| E[Fire GeneralAction Return<br/>= click "Return" dialog]
  E --> F[teleport home aetheryte]
  F --> REC
  REC --> Rec[TickRecovering]
  Rec --> Rec1[Deactivate RSR + BossMod<br/>+ navmesh.Stop]
  Rec1 --> Rec2{died flag?}
  Rec2 -->|yes + zone in workingSet| Tp[Teleport back → Teleporting]
  Rec2 -->|panic flag?| Pan[Wait HP recover]
  Rec2 -->|else| Sel[→ Selecting]
```

---

## 9) `TickPreparingPause` (โหมดกลางหน่วงเข้า Pause)

```mermaid
flowchart TD
  A[TickPreparingPause] --> T{2 min timeout?}
  T -->|yes| PauseHere[EnterPause in place]
  T -->|no| B{InCombat?}
  B -->|yes| FLEE[Find threat → run 80y away<br/>throttle 4s]
  B -->|no| C{TeleportFired?}
  C -->|no| D[Teleport ไป zone primary aetheryte]
  D -->|fail no zone| PauseHere
  D -->|ok| TF[_preparePauseTeleportFired=true]
  C -->|yes| E{2s grace + not BetweenAreas?}
  E -->|รอ| END
  E -->|ผ่าน| EnterPause[EnterPause → Paused]
```

---

## 10) `GenericStuckWatchdog` — กันค้าง (v1.0.0.28+)

```mermaid
flowchart TD
  A[GenericStuckWatchdog] --> B{State ในรายการ idle?<br/>Stopped/Paused/PreparingPause/<br/>Teleporting/Repairing/Trading/<br/>Mounting/Interacting/Recovering/Dying}
  B -->|yes| END[Reset _genericLastPos + return]
  B -->|no| C{Activity gate:<br/>vnav.IsPathRunning OR<br/>Engaging+target?}
  C -->|no| RESET[Reset + return ไม่ jump]
  C -->|yes| D{Player moved > 2y?}
  D -->|yes| UPDATE[Update _genericLastPos]
  D -->|no| E[stillSec elapsed]
  E -->|< 5s| END
  E -->|≥ 5s| F[Log + 8s throttle]
  F --> G{stillSec ≥ 30s?}
  G -->|yes| H{InCombat?}
  H -->|yes| FleeRep[FleeCombatForRepair]
  H -->|no| TP[Lifestream.Teleport<br/>→ zone aetheryte]
  G -->|< 30s| I{canActHeavy?<br/>!Mounted !InFlight !InCombat}
  I -->|no| RESET2[skip + return]
  I -->|yes + stillSec≥15s| RePath[navmesh re-pathfind fly=true<br/>+ stamp _stuckRecoveryIssuedAt]
  I -->|yes + stillSec≥5s| JUMP[GeneralAction.Jump]
```

ถ้า InCombat ใน 12s ถัดไป → `_navmesh.Stop()` ให้ BossMod ครองเอง.

---

## 11) `CheckLogicLoop` (anti-loop watchdog)

```mermaid
flowchart TD
  A[Every LogAction] --> B[RecordLogFingerprint:<br/>regex strip numbers/IDs/durations<br/>+ exclude noisy patterns:<br/>stats, HP, panic, watchdog,<br/>logic-loop self-emit,<br/>FATE-target, force-pull,<br/>BossMod activate/deactivate,<br/>RSR activate/deactivate,<br/>Dismount, FATE done,<br/>pull commit dropped,<br/>collect-FATE, forlorn priority,<br/>Retarget mode]
  B --> C[Sliding window 120s cap 80 entries]
  C --> D{CheckLogicLoop every 15s<br/>+ skip states Stopped/Paused/<br/>PreparingPause/Dying}
  D --> E[Group by fingerprint<br/>find max count]
  E -->|≥ 8 in window| RECOVER[Recovery escalation:<br/>1-2x = soft reset<br/>blacklist FATE for session<br/>+ navmesh.Stop + lifestream.Abort<br/>+ deactivate AI + → Selecting<br/><br/>3rd = EnterPauseSafely 15m]
```

---

# 🔍 ช่องโหว่ / Edge cases ที่ควรเฝ้าระวัง

## High risk

1. **Dialog ค้างนอก ShopExchangeCurrency**
   - เราปิดเฉพาะ ShopExchangeCurrency. ถ้า Mender repair dialog / SelectIconString / SelectString ยังเปิด → teleport block เหมือนกัน
   - **เสนอ**: เพิ่ม close defensive สำหรับ addon อื่นๆ ที่บ่อย: `SelectYesno`, `SelectString`, `SelectIconString`, `Repair`, `Talk`

2. **TickRepairing 4 min timeout**
   - ถ้า Mender ที่ใกล้ aetheryte ไกล (Living Memory, Mare Lamentorum) bot อาจไปไม่ทันใน 4 นาที → abort → คอนติแบบเดิมจะมีบั๊กไม่ซ่อม → durability ลงเรื่อย → ตาย
   - **เสนอ**: เพิ่ม timeout เป็น 6 นาที / ตามระยะ Mender

3. **`workingSet.Count == 0` (ไม่ติ๊ก zone)**
   - บอท Selecting drought → TryRotateZone fail (no candidates) → log อย่างเดียว
   - ไม่ STOP ตัวเอง ผู้ใช้ไม่รู้ว่าทำไมไม่ฟาร์ม → ค้างไปเรื่อย
   - **เสนอ**: ถ้า working set empty + 60s ไม่ทำอะไร → log error + Stop

4. **Forlorn pre-scan ก่อน Battalion check**
   - Pre-scan ใช้แค่ NameID + FateId, ไม่เช็ค Battalion. ถ้ามี FATE NPC ที่บังเอิญใช้ NameID 6737/6738 (rare แต่ทฤษฎีเป็นได้) → bot ตี friendly NPC
   - **เสนอ**: เพิ่ม Battalion check ใน Forlorn pre-scan

5. **`_killPhaseAggroLossAt` 3s debounce**
   - ถ้า mob ตายเร็วเกินไป (พลังสูง) → ตี mob ตัวเดียวต่อ batch → aggro=1→0 ทันที → 3s debounce → bot รอ 3s โดยไม่ทำอะไร → ดูเฉื่อย
   - **เสนอ**: ถ้า aggro=0 + unaggro=0 (ไม่มีอะไรเหลือ) → release latch ทันที

## Medium

6. **`_pullCommitId` grace 4s**
   - Mob despawn (kill) → grace 4s → bot ยืนรอ 4s ก่อน pick ใหม่
   - ปกติ kill เร็ว → grace ไม่ใช่ปัญหา. แต่ตอน mass kill มี gap
   - **เสนอ**: ลด grace เหลือ 2s ถ้า mob.IsDead == true (รู้ตายแล้ว ไม่ใช่ flicker)

7. **Long-range teleport (1500y threshold)**
   - ถ้า FATE อยู่ไกล 1499y → ไม่ใช้ teleport → บินยาว 75 วินาที
   - **เสนอ**: ลด threshold เหลือ 1000y / เพิ่ม slider config

8. **Retarget mode "Never" ตอน collect**
   - ถ้า mob aggro player ระหว่าง collect → switch กลับ "NoTarget" → BossMod pick + fight
   - แต่ถ้า mob อยู่ไกล → BossMod pick mob ไม่ engage → ค้าง?
   - **เสนอ**: ตรวจสอบ logic flow collect → combat → collect transition

9. **Sec cap timer rolled แค่ตอน Start + Resume**
   - ถ้า bot รัน 4h ครั้งแรก → pause → resume → ปกติ. แต่ถ้า user restart plugin กลางทาง → session timer รีเซ็ตเป็น Start fresh → ไม่เคย pause จริง?
   - **เสนอ**: persist session counters across plugin reload? (อาจ over-engineer)

## Low

10. **No watchdog ใน TickInteracting close-in**
    - ถ้า NPC อยู่ไกล bot walk → ถ้าติดหิน → CheckAndRecoverFromStuck restart navmesh — ก็โอเค
    - **เสนอ**: เพิ่ม timeout เฉพาะ TickInteracting (close-in stall)

11. **Forlorn detection ทุก tick scan O(n)**
    - Scan object table 2 ครั้ง (Forlorn pre-scan + general filter). ใน zone ที่มี actor เยอะ → CPU
    - **เสนอ**: รวม scan เป็นครั้งเดียว, mark Forlorn flag

12. **Stuck recovery teleport ถ้า aetheryte ไม่ attuned**
    - bot stuck → escalate → teleport → fail (not attuned) → re-stuck → loop
    - **เสนอ**: detect rejected teleport ใน watchdog tier 3 → fallback action

---

# 📊 State diagram (summary)

```mermaid
stateDiagram-v2
  [*] --> Stopped
  Stopped --> Selecting: Start()
  Selecting --> Teleporting: rotate / long-range
  Selecting --> Mounting: not in range + not mounted
  Selecting --> Traveling: not in range + mounted
  Selecting --> Interacting: in range + has NPC
  Selecting --> Engaging: in range + no NPC
  Mounting --> Traveling: mounted ✓
  Mounting --> Engaging: in combat
  Mounting --> Selecting: timeout
  Traveling --> Engaging: in range + no NPC
  Traveling --> Interacting: in range + has NPC
  Interacting --> Engaging: FATE start ✓
  Interacting --> Selecting: vanish / abort
  Engaging --> Recovering: FATE done / panic
  Engaging --> Mounting: stranded
  Engaging --> Dying: HP=0
  Recovering --> Selecting: timer done
  Recovering --> Teleporting: died + restore
  Teleporting --> Selecting: arrived
  Teleporting --> Selecting: timeout abort
  Dying --> Recovering: alive again
  Repairing --> Selecting: done / timeout
  Trading --> Selecting: done (auto-trade)
  Trading --> Stopped: done (survey-only)
  PreparingPause --> Paused: flee + teleport done
  Paused --> Selecting: timer done
  Any --> Stopped: Stop()
  Any --> Repairing: durability triggers
  Any --> Trading: gems trigger
  Any --> PreparingPause: session cap / logic-loop
  Any --> Dying: Unconscious
```

---

# 🧮 Quick reference — thresholds + timers

| Setting | Default | Range / Source |
|---|---|---|
| `MinDroughtSeconds` | 60 (was, slider 10-600) | drought ก่อน rotate |
| `MinLevelDelta` | -12 | FATE level filter |
| `FateTimeRemainingMinSec` | 60 | FATE expiring soon filter |
| `EngageRangeMultiplier` | 0.7 | × radius = "in range" |
| `MaxAggroCount` | 3 | pull size cap |
| `LongRangeTeleportYalms` | 1500 | trigger zone aetheryte teleport |
| `RepairAtDurabilityPercent` | 30 (user 70+) | trigger repair |
| `SessionCapHours` | 4 ± 0.5 jitter | hard pause |
| `SessionCapPauseMinutes` | 30 ± 10 jitter | macro break |
| `RaiseGraceSeconds` | 30 | wait for raise before Return |
| `PanicHpPercent` | 25 (user 40+) | panic-escape trigger |
| Targeting humanize | 300-1500 ms | EnforceFateMobTarget throttle |
| Pull commit grace | 4 s | commit not visible → drop |
| killPhase debounce | 3 s | aggro=0 sustained → release |
| Stuck escalation | 5s/15s/30s | jump / re-path / teleport |
| Logic-loop window | 120s, ≥8 hits | escalate to soft → 3rd = PauseSafely 15m |
| Force-pull throttle | 3 s, 6s OOC | basic-attack to start combat |
| Humanize jump | 25-75s + walking ≥5y/4s | random ground jump |
