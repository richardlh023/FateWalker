# Dependencies — สิ่งที่ใช้ vs สิ่งที่ยังไม่ได้ใช้ (v1.0.0.30)

Research จาก source ของทุก plugin ใน `z:/FFMinion/.dalamud/`. แต่ละหัวข้อระบุ IPC name ตรงๆ เพื่อ wrap เพิ่มได้.

---

## vnavmesh — ใช้ ~3 / 20+ endpoints

ที่ใช้: `SimpleMove.PathfindAndMoveCloseTo`, `Path.Stop`, `Path.IsRunning`, `Path.SetTolerance`.

### ยังไม่ใช้ที่น่าสนใจ

| IPC | คำอธิบาย | ช่วยอะไรได้ |
|---|---|---|
| **`Query.Mesh.NearestPointReachable`** | คืน point บน navmesh ที่ใกล้สุด **และเดินถึงได้** | mob ที่อยู่ใน area unreachable (cliff/ดงต้นไม้) → ใช้ point นี้แทน → bot ลงพื้นใกล้สุด แล้ว BossMod เก็บ |
| **`Query.Mesh.PointOnFloor`** | snap point ลงพื้น (Y สูงสุดที่ ≤ Y ต้นทาง) | ใช้กับ landing point ที่อยู่บน ledge → snap ลงพื้นก่อน |
| **`Nav.PathfindCancelable`** | pathfind ที่ cancel ได้ผ่าน CancellationToken | cleaner กว่า Stop() — รู้ว่า cancel สำเร็จเมื่อไหร่ |
| `Query.Mesh.FlagToPoint` | คืน Vector3 ของ map flag | quick "go to my map marker" |
| `Nav.IsAutoLoad` / `SetAutoLoad` | เปิด/ปิด auto-load navmesh ต่อ zone | ลด CPU ในเมือง (ปิด autoload) |

**เสนอใช้**: `NearestPointReachable` ใน RefineLandingTarget — solve "Forlorn spawn ในที่ unreachable" หรือ "FATE center อยู่บน cliff".

### Movement model สำคัญ

vnavmesh **hook ที่ `RMIWalk` / `RMIFly`** ของ game (per `vnavmesh/Movement/OverrideMovement.cs`). Override input vector ทุก frame ระหว่าง pathing. → IKeyState injection ที่เราเคยลอง (Space/W) **ไม่มีผล** ตอน vnav กำลังขับ. ใช้ `_action.Jump()` (GeneralAction 2) แทน.

---

## Lifestream — ใช้ ~5 / 40+ endpoints

ที่ใช้: `Teleport`, `AethernetTeleportById`, `Abort`, `IsAvailable` (custom).

### ยังไม่ใช้ที่น่าสนใจ

| IPC | คำอธิบาย | ช่วยอะไรได้ |
|---|---|---|
| **`GetActiveAetheryte`** | คืน aetheryte ID ที่ player อยู่ใกล้ (0 ถ้าไม่ใกล้) | เช็คก่อน `AethernetHop` ว่ายืนอยู่ที่ aetheryte จริงๆ. ป้องกัน fail silent |
| **`GetRealTerritoryType`** | territory ID แบบ instance-resilient | ใช้แทน `ClientState.TerritoryType` — สำคัญใน zone ที่มี instance |
| **`IsBusy`** | true ถ้า Lifestream มี task queued | เช็คก่อน issue task ใหม่ → กัน rejection silent |
| **`ChangeInstance`** | hop ระหว่าง zone instances 1/2/3 | ถ้า FATE เก่าตลอด → ลอง hop instance |
| `GetNumberOfInstances` / `GetCurrentInstance` | ดู instance ทั้งหมด + อันปัจจุบัน | UI hint, rotation logic |
| `Move(List<Vector3>)` / `MoveEx` | drive movement ผ่าน Lifestream's executor (แทน vnav direct) | alternative ถ้า vnav มีปัญหา |
| `ExecuteCommand(string)` | รัน `/li <args>` | ใช้ Lifestream's shortcut commands |
| `AethernetTeleport(string)` | aethernet hop by NAME ("Bayside Bevy Marketplace") | คล่องตัวกว่า id |
| `TeleportToHome` / `TeleportToFC` / `TeleportToApartment` | quick home teleport | กรณี panic — return to home |

**เสนอใช้**:
1. `GetRealTerritoryType` — แทน `_clientState.TerritoryType` ทุกที่ใน controller (instance-safe)
2. `IsBusy` — เช็คก่อน `Teleport` เพื่อรู้ว่ายังมี task ค้างหรือไม่
3. `ChangeInstance` — เพิ่ม recovery: ถ้าฟาร์มใน zone เดิม 30 นาทีไม่มี FATE → hop instance

---

## BossmodReborn — ใช้ ~3 / 10+ endpoints

ที่ใช้: `Presets.Create`, `Presets.SetActive`, `Presets.ClearActive`, `Presets.AddTransientStrategy`.

### ยังไม่ใช้ที่น่าสนใจ

| IPC | คำอธิบาย | ช่วยอะไรได้ |
|---|---|---|
| **`Presets.GetActive`** | คืนชื่อ preset ที่ active อยู่ | เช็คก่อน activate ว่า user setup อะไรไว้ก่อน → restore เมื่อ stop |
| **`Presets.GetForceDisabled` / `SetForceDisabled`** | force disable autorotation | emergency stop combat (ระหว่าง bot Pause / chat-safety) |
| **`Presets.Delete`** | ลบ preset | cleanup ตอน Dispose plugin |
| **`AI.GetPreset` / `AI.SetPreset`** | **AI preset แยกจาก rotation** | BossMod มีระบบ AI module เพิ่มเติม — เรายังไม่ได้แตะ |
| `Configuration` | get/set plugin config keys | ตั้งค่า BossMod เพิ่มเติมได้แบบ programmatic |

**เสนอใช้**:
1. `Presets.GetActive` — backup user's preset ตอน Start, restore ตอน Stop (เลิก overwrite ของ user)
2. `Presets.Delete` — ลบ "FateWalker - FATE" preset ตอน Dispose (cleanup)
3. ลอง `AI.GetPreset` — อาจเป็นช่องทาง AI ที่ดีกว่า preset-based

### Preset config ที่ใต้ใช้

Preset ของเรามี modules ครบ. แต่ไม่ได้ใช้ทุก track:

**`NormalMovement` มี tracks อื่นๆ ที่ยังไม่ tune**:
- `Range` — strategies (Any / MaxRange / GreedGCDExplicit / GreedLastMomentExplicit / GreedAutomatic) — สำหรับ "stay-at-max-range" caster pattern
- `Cast` — (Leeway / Greedy / FinishMove / DropMove) — แตกต่างเรื่อง "interrupt cast เพื่อ move หรือไม่"
- `ForbiddenZoneCushion` — extra padding for AOE avoid (None / Small / Medium / Large)
- `SpecialModes` — handle Misdirection / forced movement
- `DelayMovement` — เคยลอง, มีบั๊ก, disable แล้ว

**`AutoTarget` มี Track อื่น**:
- `General`: Aggressive / Defensive / Passive — เราใช้ Aggressive (default).
- `FATE`: Enabled / Disabled — เปิดอยู่.

**`FateUtils` ครบ** — Handin / Collect / Sync / Chocobo.

---

## TextAdvance — ใช้ 2 endpoints

ที่ใช้: `Acquire`, `Release` (มีใน TextAdvanceIpc.cs).

แค่นั้นจริงๆ. TextAdvance มี IPC น้อย — ของจริงเป็น auto-handler ภายใน.

---

## RotationSolverReborn — ใช้ 3 endpoints

ที่ใช้: `ChangeOperatingMode`, `AutodutyChangeOperatingMode`, `EnableTargetFreelyOverride` / `DisableTargetFreelyOverride`.

ลบ `AutodutyChangeOperatingMode` ไปแล้วใน v1.0.0.15 (เลิก Auto+Farthest). ของจริงตอนนี้ใช้แค่ Manual + TargetFreely.

RSR ไม่ expose IPC อื่นที่สำคัญ.

---

## YesAlready — ยังไม่ integrate (potential conflict!)

ถ้า user มี **YesAlready** ติดตั้ง (popular) มันก็จะ auto-click SelectYesno เหมือนเรา. → ทั้งสองแข่งกัน. หรือ YesAlready click ผิดสิ่งที่เราต้องการ.

**IPC**: `YesAlready.IsPluginEnabled` / `YesAlready.SetPluginEnabled`.

**เสนอ**:
- ตอน Start → ตรวจ YesAlready installed + enabled → temporarily disable (เพื่อ avoid conflict)
- ตอน Stop → restore previous state

(เช่นเดียวกับที่ Questionable / AutoDuty ทำ — see `Questionable/External/YesAlreadyIpc.cs`)

---

## AutoDuty patterns ที่ลอกได้

### 1. RepairNPC by DataId (แทน name match)

**ของเรา** (TickRepairing):
```csharp
if (!name.Contains("Mender", StringComparison.OrdinalIgnoreCase)) continue;
```
- เปราะ: ภาษาอื่นไม่ใช่ "Mender", NPC ที่มีคำว่า Mender ในชื่อแต่ไม่ใช่ repair NPC

**AutoDuty** ([RepairNPCHelper.cs](z:/FFMinion/.dalamud/autoduty/AutoDuty/Helpers/RepairNPCHelper.cs)):
```csharp
internal class RepairNpcData {
    int RepairIndex;  // ลำดับใน SelectIconString menu
    uint DataId;      // จาก Lumina ENpcResident
}
internal static List<RepairNpcData> RepairNPCs = []; // populated from Lumina

// match by DataId เท่านั้น
```

→ AutoDuty รู้ทุก mender NPC จาก Lumina + รู้ menu index ของ "Repair" option (กรณีมี SelectIconString menu).

**เสนอ**: lift `RepairNpcData` list มาใช้ → bot match ด้วย DataId + รู้ menu index ตายตัว.

### 2. BossMod AI preset separate

AutoDuty รู้ว่า BossMod มี `AI.GetPreset` / `AI.SetPreset` ที่แยกจาก rotation. เราไม่ได้ใช้ — อาจมี AI mode ที่เหมาะกับ FATE farming โดยเฉพาะ.

---

## BOCCHI patterns ที่น่าลอก

### 1. Centroid-based positioning

[BOCCHI/FateActivity.cs](z:/FFMinion/.dalamud/bocchi/BOCCHI/Modules/Automator/FateActivity.cs):
```csharp
var enemy = GetEnemies().Centroid();
if (enemy != null) Svc.Targets.Target = enemy;
```

ใช้ "ศูนย์กลางของกลุ่ม mob" เป็น path target. ตอน AoE farming = bot ยืนกลางกลุ่ม.

→ เสนอ: เพิ่มเป็น option ใน `EnforceFateMobTarget` (ปัจจุบัน pick nearest).

### 2. Effective range = `distance - HitboxRadius`

```csharp
var distance = Vector3.Distance(Player.Position, target.Position) - target.HitboxRadius;
if (distance <= module.Config.EngagementRange)
```

ของเราใช้ raw distance — สำหรับ mob hitbox ใหญ่ (~4y) ทำให้คำนวณผิดด้าน. ใช้ effective distance แทน.

### 3. Vnav stopped exception

```csharp
if (!vnav.IsRunning())
    throw new VnavmeshStoppedException();
```

ถ้า vnav หยุดเอง (path complete / path failed) → throw exception → recovery wrapper จัดการ. cleaner กว่าเช็ค IsPathRunning + ไม่ทำอะไร.

---

## สิ่งที่ Dalamud มีแต่เรายังไม่ใช้

| Service | ใช้ทำอะไรได้ |
|---|---|
| `ITitleScreenMenu` | เพิ่ม "FateWalker" ใน title screen menu (UI sugar) |
| `INotificationManager` | Toast notifications (เช่น "Session cap reached") |
| `IDtrBar` | Server info bar entry (เช่น "Bot: Engaging Lakeland 12/300") |
| `IContextMenu` | Add context menu actions (right-click FATE → blacklist) |
| `IDutyState` | Detect dungeon/raid entry — auto-stop bot |
| `IBuddyList` | Companion/buddy info (chocobo HP, etc.) |
| `IPartyList` | Detect party — skip FATE if in party (avoid stealing) |

**เสนอใช้สำคัญ**:
1. `IDutyState` — ถ้า user join duty พร้อมบอท → auto stop (กัน "AFK in dungeon" detection)
2. `IDtrBar` — แสดง bot state ใน server info bar (เหมือน Lifestream's DTR)
3. `IPartyList` — ถ้ามี party member → ลด aggressiveness (กัน steal pull)

---

## ECommons utilities — ใช้ผ่าน Lifestream/BossMod แต่ไม่เคย direct

ECommons (ใน `z:/FFMinion/.dalamud/*/ECommons/`) มีหลายอย่าง:
- `TaskManager` — task queue with throttling
- `EzThrottler` — pattern-based throttling (เราใช้ rolling DateTime ตอนนี้)
- `EzIPC` — IPC declaration อย่างง่าย
- `GenericHelpers` — เยอะ helper methods
- `Player.Object`, `Svc.*` — quick access wrappers

**เสนอใช้**: ECommons เป็น dependency แทน implementing throttle/queue เอง.

---

# 📊 Priority สำหรับ apply

| Priority | Item | Impact | Effort |
|---|---|---|---|
| **High** | YesAlready integration (avoid conflict) | bot stuck on dialogs ที่ YesAlready click ผิด | 30 min |
| **High** | RepairNPC DataId match (vs name) | ทำงานข้าม locale | 1 hr |
| **High** | `GetRealTerritoryType` (instance-safe) | bug ใน instanced zones | 15 min |
| **High** | `Query.Mesh.NearestPointReachable` ใน RefineLandingTarget | unreachable mob (Forlorn spawn บนหิน) | 30 min |
| **Medium** | `Lifestream.IsBusy` ก่อน Teleport | กัน Lifestream queue ค้าง | 15 min |
| **Medium** | `BossMod.Presets.Delete` ตอน Dispose | cleanup user's preset list | 10 min |
| **Medium** | `BOCCHI distance - HitboxRadius` | melee range calc accurate | 20 min |
| **Medium** | `IDutyState` auto-stop | safer (กัน duty detect) | 20 min |
| **Medium** | `IDtrBar` status | UX improvement | 30 min |
| **Low** | `ChangeInstance` recovery | edge case (zone full) | 1 hr |
| **Low** | `Centroid` positioning option | optional AoE farming | 30 min |
| **Low** | BossMod `AI.GetPreset/SetPreset` | unknown benefit | research first |
| **Low** | ECommons dependency | refactor ตลอด codebase | 4+ hr |

---

# 🎯 จากที่ research มา — top 3 ที่แนะนำทำต่อ

1. **YesAlready conflict** — high impact, low effort. หลายคน install YesAlready แล้วบอทเสียพฤติกรรม.
2. **GetRealTerritoryType** — replace `_clientState.TerritoryType`. instance bug ที่อาจซ่อนอยู่.
3. **NearestPointReachable** ใน RefineLandingTarget — solve Forlorn-on-cliff / mob-in-unreachable area.

อยากให้ implement ตัวไหนก่อน บอกได้.
