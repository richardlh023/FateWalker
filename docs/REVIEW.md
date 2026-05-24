# FateWalker Decision-Logic Review (v1.0.0.30)

ตรวจสอบทุกการตัดสินใจของบอท เทียบกับ:
- **A** = FF14 FATE mechanics จริง (ConsoleGamesWiki + Bicolor wiki)
- **B** = Dependency capabilities (vnav, Lifestream, BossMod, YesAlready, Dalamud services)
- **C** = Patterns จาก plugin อื่น (AutoDuty, BOCCHI, Questionable)

ใช้ ✅ ถูก / ⚠️ ใช้ได้แต่ไม่ดีที่สุด / ❌ ผิด ต้องแก้.

---

## 1) Top-level `Tick()` flow

### ✅ ทำถูก
- Universal guards (Stopped, NotLoggedIn, BetweenAreas, CutScene) — ครบ
- Trigger exclusion matrix (Auto-Repair, Auto-Trade, Session-Cap) — ครบ + recent fix v1.0.0.25 ทำให้กันลูปกัน
- Death override = first-class (interrupts almost everything) — ถูกต้องตาม priority
- Generic stuck watchdog + logic-loop watchdog — defense in depth

### ⚠️ Suboptimal
- **ใช้ `_clientState.TerritoryType`** ทุกที่. ใน instanced zones (Eureka, Bozja, some open-world post-EW) ค่าจะไม่ stable. ควรใช้ `Lifestream.GetRealTerritoryType` (instance-resilient).
- **ไม่ตรวจ `Lifestream.IsBusy()`** ก่อน issue task ใหม่. ถ้า Lifestream มี task ค้าง → ของเรา fire ทับ → silent rejection. ต้นเหตุหลายเคส "teleport rejected loop".

### ❌ ผิด / ขาด
- **ไม่มี YesAlready integration**. ถ้า user มี YesAlready + auto-click Yes ที่ขัดกับเรา → behavior unpredictable. Pattern จาก AutoDuty/Questionable: disable YesAlready ตอน Start, restore ตอน Stop.
- **`IDutyState` service ไม่ใช้**. ถ้า user join duty พร้อมบอท → บอทยังทำงาน → AFK detection ใน dungeon → ban risk.

---

## 2) `TickSelecting` — เลือก FATE

### ✅ ทำถูก
- Pre-rotate ถ้า outside working set / current zone maxed (ที่เพิ่มใน v1.0.0.4-12)
- Manual pick override
- Humanize delay (ThinkBeforePick) ก่อนจะ commit
- Long-range teleport (1500y default) — เป็นประหยัดเวลาบินจริง

### ⚠️ Suboptimal
- **`PrioritizeBonusFates` config ไม่ทำงาน** (`FateSelector.cs` order by `HasBonus` เสมอ). ผู้ใช้ toggle ไม่มีผล — bug ตั้งแต่ v1
- **FATE TimeRemaining filter = static 60s**. ถ้า FATE ห่าง 1400y (ใต้ threshold long-range) → บินใช้เวลา ~70s → ถึงเมื่อ FATE จบ. ควร dynamic = `60 + dist/20`.
- **LongRangeTeleportYalms = 1500 hardcoded** — ไม่มี UI slider. คนต้องแก้ JSON เอง.
- **ไม่ทำ FATE chaining คิดต่อ**. ตี FATE เสร็จ → next pick = "ไกลสุดที่ยัง valid". แต่ถ้ามี FATE 50y away กับ 800y away — 50y ดีกว่าเพราะใช้ Twist of Fate buff ก่อนหมด.

### ❌ ผิด / ขาด
- **ไม่ track "Twist of Fate" buff** (จาก Forlorn Maiden kill). Buff นี้ให้ **+50% gem ใน FATE ถัดไป** (Forlorn rare = +300%). บอทควร:
  - หา FATE ใน zone เดียวกัน อันที่ใกล้สุด + Running state → CHAIN ไม่หยุด
  - ห้าม rotate zone จนกว่า buff หมด
  - ปัจจุบัน: buff หาย → wasted potential 50-300% gem
- **Bonus FATE detection** ใช้ `fate.HasBonus` (Dalamud API). ✅ ถูก. แต่ไม่ได้ใช้กับ filter `MinLevelDelta` — Bonus FATE level ต่ำกว่า cap -12 ก็ควรไป (bonus 100% × low gem ก็ยังคุ้ม). ปัจจุบัน reject.

---

## 3) `TryRotateZone`

### ✅ ทำถูก
- Empty working set log + return (v1.0.0.4 fix)
- City detection (territory ไม่อยู่ใน TerritoryMap → urgent rotate)
- Bypass anti-pingpong filter เมื่อ urgent
- Auto-disable SkipMaxed mode เมื่อทุก zone maxed
- Skip maxed zones จาก candidates

### ⚠️ Suboptimal
- **Round-robin alphabetical** — บาง zone density สูง (Heritage Found = 16 FATEs) บาง zone ต่ำ (Yak T'el = 12, layout broken). ควร weight by `FateCatalog.All.Count(z => z.TerritoryId == t)`.
- **ไม่ track FATE-run-rate per zone** ใน session. ถ้าฟาร์ม Lakeland ได้ 8 FATE/hr และ Kholusia ได้ 2 FATE/hr → adaptive rotate ไปยัง Lakeland ถี่กว่า.

### ❌ ผิด / ขาด
- **`ChangeInstance` ไม่ใช้**. ถ้า zone instance เต็มผู้เล่น (FATEs ถูก kill ครบโดยคนอื่น) → hop instance ดีกว่า rotate ไป zone อื่นไกล. Lifestream มี IPC พร้อมใช้.

---

## 4) `TickTeleporting`

### ✅ ทำถูก
- v1.0.0.4 fix: `_teleportFired && territory matches && !BetweenAreas` (ครบเงื่อนไข arrived)
- 30s timeout abort
- v1.0.0.29 fix: TryCloseShopExchangeCurrency ก่อนยิง

### ⚠️ Suboptimal
- **เช็คเฉพาะ ShopExchangeCurrency**. ของอื่นที่ block teleport เหมือนกัน: `SelectIconString` (vendor menu), `Talk` (NPC dialog ค้าง), `SelectYesno` (confirm dialog ค้าง), `Repair` addon. ควรปิดทุกอันที่อาจ block.
- **ไม่เช็ค `Lifestream.IsBusy()`** ก่อน Teleport call. ถ้า Lifestream มี task ค้างจาก operation อื่น (e.g. previous AethernetHop) → ใหม่ rejected silently.

### ❌ ผิด / ขาด
- **ถ้า teleport rejected เพราะ "not attuned"** → ไม่ detect → retry forever → logic-loop watchdog escalate. ควร detect rejection reason + skip aetheryte ที่ไม่ attuned (mark in config).
- **ไม่ใช้ `GetRealTerritoryType`** สำหรับ arrived check. ใน instanced zone ค่าจาก ClientState อาจ unreliable.

---

## 5) `TickEngaging` + `EnforceFateMobTarget`

### ✅ ทำถูก
- Forlorn Maiden pre-scan (v1.0.0.20)
- Battalion check (v1.0.0.12) เพื่อกัน friendly NPC
- Hostile flag check
- FateId filter
- Pull commit + 4s grace (v1.0.0.14)
- Kill-phase latch + 3s debounce (v1.0.0.16, v1.0.0.27)
- Collect FATE branch (v1.0.0.19, v1.0.0.21)
- Collect retarget mode "Never" (v1.0.0.22)
- Y-axis stranded escape (v1.0.0.22) — mount + fly toward target

### ⚠️ Suboptimal
- **Pull pick = "nearest"** (v1.0.0.15). ทำงานได้ดีพอควร แต่ FATE บาง type (Defense, Escort) มี waves coming from edges → pull "centroid" ของกลุ่ม mob (BOCCHI pattern) จะดีกว่า.
- **Distance calc ใช้ raw `Vector3.Distance`** สำหรับ "in range". BOCCHI ใช้ `dist - HitboxRadius` = effective distance. Mob hitbox 4y ของเรา = "in range" 6y พอที่จะตี แต่ของเราคำนวณว่าใกล้พอแล้ว → bot ไม่เดินเข้า → ทำให้ใช้ ranged abilities.
- **`KickIfStuckInEngaging` ใช้ `_navmesh.PathfindAndMoveCloseTo` กับ fly=true**. ถ้า player on ground → vnav fly path = ignored → ground walk fallback. แล้วถ้า terrain blocked → stuck. ควร call mount → jump → fly ถ้าจำเป็น.
- **Force-pull (v1.0.0.20-ish)** ใช้ basic attack action ID per class. แต่ map ไม่ครบ — บาง job missing. fallback = auto-attack ที่ work only for melee. ranged caster ไม่มี auto-attack → force-pull fail.

### ❌ ผิด / ขาด
- **ไม่ใช้ `vnav.Query.Mesh.NearestPointReachable`** สำหรับ refine landing. ถ้า Forlorn spawn บน cliff / mob ใน area unreachable → vnav fail → bot ค้าง. NearestPointReachable แก้ปัญหานี้.
- **ไม่ track "Twist of Fate" buff** (จาก Forlorn kill). หลังฆ่า Forlorn ใน FATE → bot ควรเร่งเสร็จ FATE นี้ + chain ทันที (อย่ารอ humanize ที่ขาย time). ปัจจุบัน normal flow → buff หมด.
- **ไม่ใช้ Sprint** (General Action 4). Sprint = +30% MS 10s CD 60s. ระหว่าง dismount → walk to mob → fire Sprint = ลดเวลาเดิน. `ActionExecutor.UseSprint()` มีแต่ไม่มีคนเรียก.

---

## 6) `TickRepairing`

### ✅ ทำถูก
- Combat lockout escape (v1.0.0.11 fix) — flee then teleport
- Mid-air dismount rescue (v1.0.0.10)
- 4-min hard timeout
- SelectYesno auto-confirm สำหรับ gil cost

### ⚠️ Suboptimal
- **Mender search by name `.Contains("Mender")`** — fragile. ถ้า client เป็น JP/DE/FR — ชื่อ NPC ต่างกัน → fail. AutoDuty's pattern: ใช้ Lumina ENpcResident DataId match.
- **4-min timeout อาจสั้นไป** ใน zone ที่ Mender ห่างจาก aetheryte (Living Memory, Mare Lamentorum). บินถึง Mender อาจใช้ >2 นาที + flee combat + repair + กลับ = พอเวลาแต่ tight.

### ❌ ผิด / ขาด
- **ไม่จัดการ SelectIconString menu** ในบาง Mender NPC (Merchant & Mender combined). ถ้า menu pop ขึ้น "Repair / Trade" → bot ไม่ click. (เคยมี fix v1.0.0.4-ish? ขอเช็คใหม่)

---

## 7) `TickTrading`

### ✅ ทำถูก
- Aethernet hop หลัง main teleport (v1.0.0.20-ish)
- Same-zone walk vs teleport decision (v1.0.0.18 — IsVendorWalkClose)
- Vendor name match diagnostic (logs nearby NPCs ถ้าหาไม่เจอ)
- Survey-only mode (v1.0.0.20)
- Per-item buy limit (v1.0.0.21)
- Hub vendor warning ใน UI (rank-gated)

### ⚠️ Suboptimal
- **Vendor match by name** — same issue as Mender. AutoDuty pattern: DataId.
- **Trigger gems = 1350 static**. คำนวณ:
  - DT base 16 + Bonus 100% = 32 gem/FATE
  - + Forlorn The Forlorn rare = next FATE +300% = ~64 gem
  - 1500 cap - 1350 trigger = **150 buffer** = ~5 normal FATE หรือ 2-3 lucky FATE → potential waste
  - ควรลด trigger เป็น 1200 หรือ adaptive (1100 ถ้า Twist of Fate buff active)
- **Cap = 1500 hardcoded**. ถูกต้อง ณ patch 7.0 แต่ SE อาจปรับ → ใส่ config ดีกว่า.

### ❌ ผิด / ขาด
- **ไม่ตรวจ "cap reached"** — ถ้า gems == 1500 พอดี → next FATE = 0 reward. Bot ควร PREEMPTIVELY trade ที่ 1500 ทันที (อย่า trigger ที่ 1500+1 ซึ่งไม่มี). ปัจจุบัน trigger ที่ 1350 → ถ้าเร็วมาก อาจถึง 1500 ก่อน trigger fire.
- **ไม่ track "Twist of Fate" buff** → ไม่รู้ว่ารอบหน้าจะได้ gems มาก. Adaptive trigger ทำได้.
- **ไม่มี fallback ถ้า vendor ไม่ unlock** (rank ไม่ถึง). ปัจจุบัน: Survey → teleport ไป → หา NPC ไม่เจอ (NPC ไม่ spawn) → 45s timeout → abort. ไม่ user-friendly.

---

## 8) `TickDying` → `TickRecovering`

### ✅ ทำถูก
- Unconscious detection
- Raise grace seconds
- GeneralAction.Return click หลัง grace
- Restore-after-Return ถ้า zone ใน working set
- Panic-escape state (HP wait)

### ⚠️ Suboptimal
- **`RaiseGraceSeconds` = 30s static**. ใน boss FATE หรือ defense FATE มักจะมี healer raise ภายใน 10-15s. รอ 30s = wasted. แต่ adventurer-only FATEs ก็มี → 30s safe.

### ❌ ผิด / ขาด
- **ไม่ track session deaths trend**. ถ้า die >3 ครั้งใน session → ปรับ PanicHpPercent ขึ้น auto. ปัจจุบัน user ต้องจูนเอง.

---

## 9) `TickPreparingPause` → `TickPaused`

### ✅ ทำถูก
- Flee combat ก่อน teleport
- Teleport ไป zone primary aetheryte
- 2-min hard timeout
- Session cap jitter (v1.0.0.21) — กัน pattern detection

### ⚠️ Suboptimal
- **Teleport ไป zone primary aetheryte** = อยู่ใน FATE zone นั้นต่อ — ปลอดภัยเพราะ aetheryte plaza ไม่มี mob. แต่ "macro break = return to city" pattern (per reference_bot_safety_detection.md) — เมือง = ปลอดภัยกว่า. ควรเลือก city aetheryte ของ expansion (Crystarium / Tuliyollal / etc.).

### ❌ ผิด / ขาด
- **ไม่มี idle behavior** ระหว่าง Paused. Bot ยืนเฉยๆ. มนุษย์จะ:
  - กดดู map / inventory / friend list
  - Emote / sit down
  - หรือไป /afk
  - ปัจจุบันยืนตัวตรงเสาๆ 15-30 นาที = pattern bot ชัดมาก
- **`/afk` ไม่ส่ง** เพื่อแสดง AFK status — มนุษย์ใช้บ่อย.

---

## 10) `GenericStuckWatchdog`

### ✅ ทำถูก
- Excluded states (v1.0.0.28) — รัฐที่ยืนนิ่งเป็นปกติ
- Activity gate (v1.0.0.28) — เฉพาะ vnav running หรือ Engaging+target
- Tier escalation 5s/15s/30s (v1.0.0.24)
- Combat-aware stop navmesh (v1.0.0.25)

### ⚠️ Suboptimal
- **Re-pathfind step (15s) ใช้ fly=true เสมอ**. ถ้า player on ground + ไม่ mounted → vnav จะ fallback to ground path → ถ้าติดหินเดิม → stuck เดิม. ควร mount+fly ก่อน path.
- **Teleport step (30s) ไม่ตรวจ Lifestream.IsBusy** — ถ้า Lifestream task ค้าง → rejected.

### ❌ ผิด / ขาด
- **ไม่ใช้ NearestPointReachable**. ถ้า target อยู่ใน unreachable spot → re-pathfind ก็ stuck ที่จุดเดิม.

---

## 11) `CheckLogicLoop`

### ✅ ทำถูก
- Fingerprint normalization (strip numbers/IDs)
- Exclusion list สำหรับ noisy patterns (v1.0.0.30 — pull commit dropped + 3 อื่น)
- Soft + escalate ใน 3rd recovery

### ⚠️ Suboptimal
- **Threshold 8 ใน 2 min** — อาจไวเกินสำหรับ FATE chain ที่มี events bursting.
- **Recovery clearing target + transition Selecting** = brutal reset. ถ้า loop เกิดใน Trading/Repairing → bot ทิ้ง trade ก่อนเสร็จ.

### ❌ ผิด / ขาด
- **ไม่ track session loop count trend**. ถ้า user เห็น loops=5 ใน 1 hr → bot เสีย — ควร log warning แรงขึ้น / suggest action.

---

# 🎯 Priority แก้ตามผลกระทบ

| Priority | Item | จาก section | Impact |
|---|---|---|---|
| 🔴 **HIGH** | YesAlready integration (disable/restore) | 1 | บั๊กไม่รู้ตัว |
| 🔴 **HIGH** | Track Twist of Fate buff + chain FATE | 2, 5, 7 | wasted gem buff (+50-300%) |
| 🔴 **HIGH** | Lower trigger gems / adaptive | 7 | wasted gems at cap |
| 🔴 **HIGH** | `PrioritizeBonusFates` config dead code | 2 | toggle ไม่มีผล |
| 🟡 **MED** | NearestPointReachable in refine landing | 5, 10 | unreachable mob/spot |
| 🟡 **MED** | `GetRealTerritoryType` แทน ClientState | 1, 4 | instance-safe |
| 🟡 **MED** | Dynamic FATE TimeRemaining filter | 2 | wasted travel |
| 🟡 **MED** | RepairNPC/Vendor DataId match | 6, 7 | locale-safe |
| 🟡 **MED** | Sprint usage after dismount | 5 | -30% travel time |
| 🟡 **MED** | Close more addon types before teleport | 4 | reduces "teleport rejected" |
| 🟡 **MED** | `IDutyState` auto-stop | 1 | safety + ban-risk reduction |
| 🟡 **MED** | Idle behavior during Paused (emote/afk) | 9 | anti-detection |
| 🟢 **LOW** | BOCCHI centroid pull option | 5 | optional optimization |
| 🟢 **LOW** | `dist - HitboxRadius` effective range | 5 | melee correctness |
| 🟢 **LOW** | Zone density weighting in rotation | 3 | optimal FATE-per-hour |
| 🟢 **LOW** | Adaptive RaiseGraceSeconds | 8 | minor time save |
| 🟢 **LOW** | Track FATE rate per zone | 3 | smarter rotation |
| 🟢 **LOW** | Mount selection (fastest, not roulette) | TickMounting | -10-20% travel |
| 🟢 **LOW** | Long-range teleport UI slider | 2 | UX |
| 🟢 **LOW** | `ChangeInstance` recovery | 3 | rare edge case |

---

# 📝 สรุปสั้น

## ที่ทำดี (ภูมิใจได้)
- State machine + transition discipline
- Trigger exclusion matrix แก้ลูปได้ดี
- Defensive watchdogs (movement + logic-loop)
- Humanize jitter (target delay, ThinkBeforePick, session cap jitter)
- Forlorn Maiden priority
- Collect FATE branch + retarget mode "Never"

## ที่ทำพลาด (high impact)
1. **Twist of Fate buff ไม่ track** — เสีย gem rewards 50-300%
2. **YesAlready ไม่ integrate** — risk ขัดกัน (silent bug)
3. **PrioritizeBonusFates dead code** — config UI หลอกตา
4. **Trigger gems 1350 — buffer แคบ** — risk waste at cap
5. **`_clientState.TerritoryType`** ทุกที่ — instance bug

## ที่ทำพลาด (medium impact)
6. ไม่มี Sprint
7. NPC match ด้วยชื่อ (Mender/vendor)
8. Static FATE TimeRemaining filter
9. ไม่ใช้ NearestPointReachable
10. ปิดเฉพาะ ShopExchangeCurrency (มี addon อื่นที่ block teleport)

## ที่ทำพลาด (low impact)
11. Mount Roulette random
12. ไม่มี idle behavior ใน Paused (anti-detection signal)
13. Round-robin alphabetical zone (ไม่ weight density)
14. NormalMovement.Cast strategy ไม่ tune

ทั้งหมดอยู่ใน FateController.cs / FateSelector.cs / JobModuleMap.cs / Configuration.cs — edit ในที่เดียวกันได้.

อยากให้แก้ตัวไหนก่อน บอกได้ — ที่แนะนำเริ่ม: **Twist of Fate** + **YesAlready** + **PrioritizeBonusFates fix** (3 ตัว high-impact, รวมไม่เกิน 1 ชม.).
