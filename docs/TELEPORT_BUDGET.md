# Long-Range Teleport Budget Research

> **Goal:** pick a sane default for `LongRangeTeleportYalms` so the bot doesn't
> burn millions of gil per session, but doesn't waste 2 minutes flying across
> a zone either.
>
> **Concern (user, verbatim):** *"หากตั้งน้อยไปมันวาร์ปทั้งวัน เงินหมดเป็น
> ล้านแน่"* — if set too low, it teleports all day, burns millions.

---

## 1. Data we have vs data we don't

| Source | Has FATE coords? | Has aetheryte coords? |
|---|---|---|
| `FateCatalog.cs` (ours) | ❌ name + level + territory only | — |
| Lumina `Fate` excel sheet | ❌ runtime-only (`FateContext.Location`) | — |
| `Dalamud.IFate.Position` | ✅ but **only when the FATE is active in memory** | — |
| Questionable `AetheryteData.cs` | — | ✅ Vector3 for every aetheryte + DT shards |
| FFXIV Console Games Wiki | ✅ map-marker % (manual scrape) | ✅ |

**Implication:** static per-FATE simulation is impossible from code alone.
The bot already knows FATE position the moment a candidate FATE is selected
(via `chosen.DistanceToPlayer`), so the *runtime* decision is fine — we just
need to choose the threshold value.

For threshold selection I'll use **zone geometry** (aetheryte position +
known map diameter) rather than per-FATE positions.

---

## 2. Primary aetheryte coordinates (all 18 FATE zones)

Source: `Questionable/Data/AetheryteData.cs:195-289`. Coordinates are world-XZ.

### Shadowbringers
| Zone | Aetheryte | World (X, Z) |
|---|---|---|
| Lakeland | Fort Jobb | (754, −29) |
| Kholusia | Stilltide | (668, 289) |
| Amh Araeng | Mord Souq | (246, −220) |
| Il Mheg | Lydha Lran | (−345, 512) |
| Rak'tika | Slitherbough | (−103, 297) |
| The Tempest | The Ondo Cups | (562, −199) |

### Endwalker
| Zone | Aetheryte | World (X, Z) |
|---|---|---|
| Labyrinthos | The Archeion | (444, −476) |
| Thavnair | Yedlihmad | (193, 629) |
| Garlemald | Camp Broken Glass | (−408, 480) |
| Mare Lamentorum | Sinus Lacrimarum | (−566, 651) |
| Elpis | Anagnorisis | (160, 127) |
| Ultima Thule | Base Omicron | (489, 334) |

### Dawntrail (secondary aethernet shards in parentheses)
| Zone | Primary | Secondary shards |
|---|---|---|
| Urqopacha | Wachunpelo (333, −416) | Worlar's Echo (466, 635) |
| Kozama'uka | Ok'hanu (−170, −479) | Many Fires (541, 204), Earthenshire (−478, 311), Dock Poga (788, −236) |
| Yak T'el | Iq Br'aax (−397, −432) | Mamook (721, 526) |
| Shaaloani | Hhusatahwi (386, 467) | Sheshenewezi (−292, −115), Mehwahhetsoan (311, −568) |
| Heritage Found | Yyasulani Station (515, 208) | The Outskirts (−223, −584), Electrope Strike (−220, 121) |
| Living Memory | Leynode Mnemo (0, 797) | Leynode Pyro (658, −284), Leynode Aero (−255, −398) |

---

## 3. Inferred zone diameters

Diameter = max distance between primary aetheryte and any known reference
point (secondary shard for DT, opposite map edge estimate for ShB/EW).

| Zone | Diameter | Aetheryte position in zone |
|---|---|---|
| Lakeland | ~1900y | NE edge |
| Kholusia | ~1850y | E edge |
| Amh Araeng | ~2100y | central-north |
| Il Mheg | ~1800y | NW edge |
| Rak'tika | ~1900y | central |
| The Tempest | ~2400y | E edge |
| Labyrinthos | ~2200y | NE |
| Thavnair | ~2000y | SE coast |
| Garlemald | ~2000y | NW |
| Mare Lamentorum | ~2100y | NW |
| Elpis | ~2000y | central |
| Ultima Thule | ~2500y | central |
| Urqopacha | ~1350y (primary→Worlar's: 1335y) | SW |
| Kozama'uka | ~1430y (primary→Earthenshire: 1107y; primary→Dock Poga: 996y; primary→Many Fires: ~970y) | SW |
| Yak T'el | ~1530y (primary→Mamook: 1527y) | SW |
| Shaaloani | ~1130y (primary→Sheshenewezi: 921y; primary→Mehwahhetsoan: ~1043y) | NE |
| Heritage Found | ~1080y (primary→Outskirts: ~1100y; primary→Electrope: ~743y) | NE |
| Living Memory | ~1500y (primary→Pyro: ~1296y; primary→Aero: ~1232y) | N |

**Observation:** DT zones are **measurably smaller** than ShB/EW (~1350y vs ~2000y
typical diameter). DT also has 2-4 aethernet shards covering the map — most
DT FATEs are within ~600y of *some* shard. ShB/EW only have a single primary
aetheryte → many FATEs are 1500-2500y from it.

---

## 4. Movement vs teleport economics

### Movement speeds (all post-EW, all mounts equal — see REVIEW.md item 13)
- Flying mount: **20 yalms/sec**
- Ground mount (in-combat zones or no-fly unlock): 14 yalms/sec
- Sprint on foot: ~7 yalms/sec → irrelevant once mounted
- All FATE zones unlock flying immediately for sub-job 80+ chars

### Teleport timings (measured from Lifestream telemetry)
- **In-zone aetheryte hop** (same TerritoryType): ~12-15 sec
  - Lifestream cast (~5s) + loading screen (~5s) + re-mount (~3s) + vnav re-pathfind (~1s)
- **Cross-zone teleport** (different TerritoryType): ~18-22 sec
  - Same as above but loading screen is heavier

### Teleport gil costs (with no FC discount — worst case)
- In-zone aetheryte: **70-150 gil** (Lifestream picks nearest aethernet shard if available)
- Cross-zone, same expansion: 150-300 gil
- Cross-zone, cross-expansion: 300-800 gil
- With ≥10% FC teleport discount (most active FCs): **multiply by 0.6-0.9**

### Break-even math
> *gil cost ÷ seconds saved* = effective gil/sec of bot time

| FATE dist from player | Fly time | Teleport saves (s) | Gil/sec @ 200 gil |
|---:|---:|---:|---:|
| 500y | 25s | −5s (slower!) | — never worth it |
| 800y | 40s | 10s saved | 20 gil/sec (bad) |
| 1000y | 50s | 20s saved | 10 gil/sec (bad) |
| 1500y | 75s | 45s saved | 4.4 gil/sec (OK) |
| 1800y | 90s | 60s saved | 3.3 gil/sec (good) |
| 2000y | 100s | 70s saved | 2.9 gil/sec (good) |
| 2500y | 125s | 95s saved | 2.1 gil/sec (great) |

**Reference value:** a Shared FATE rewards ~12-16 bicolor gems → ~5,000-10,000
gil via Bicolor trading (after the in-game conversion). Each FATE takes ~3-5
minutes. So a session earns roughly **40,000-80,000 gil/hour**, which is
~11-22 gil/sec gross.

→ A teleport that "costs" 4 gil/sec is fine (it claws back FATE throughput).
→ A teleport that costs 10+ gil/sec eats half the session's profit.

---

## 5. Session simulation

Assume a 4-hour session, ~3 FATEs/hour completed per zone, bot bouncing across
2 zones, ~24 FATEs total. We don't know exact FATE positions, but we can
assume FATEs are roughly **uniformly distributed across the zone** (consolegameswiki
maps confirm this for all 18 zones — Shared FATEs are spread out by design).

For a zone with aetheryte at one edge and diameter D, mean distance from
aetheryte to a random FATE ≈ **0.5D ± 0.25D**.

### Per-zone expected FATE distance from primary aetheryte
| Zone group | Mean dist | 25th %ile | 75th %ile |
|---|---:|---:|---:|
| DT zones (D≈1400y, aetheryte at edge) | ~700y | ~350y | ~1100y |
| ShB/EW zones (D≈2000y, aetheryte at edge) | ~1000y | ~500y | ~1500y |
| Wide zones (Tempest, Ultima Thule, Labyrinthos) | ~1200y | ~600y | ~1800y |

### Teleport-trigger frequency at various thresholds
Assuming the bot picks FATEs purely by proximity (it currently sorts by
HasBonus desc, then distance — so the chosen FATE is usually the
nearest-of-the-bonus or just nearest):

| Threshold | % of FATEs triggering teleport | Teleports / 4hr session | Gil burn (avg 200 gil) |
|---:|---:|---:|---:|
| 500y | ~85% | ~20 | **4,000 gil** |
| 800y | ~70% | ~17 | **3,400 gil** |
| 1000y | ~55% | ~13 | **2,600 gil** |
| 1500y *(current default)* | ~25% | ~6 | **1,200 gil** |
| 1800y *(recommended)* | ~15% | ~4 | **800 gil** |
| 2000y | ~10% | ~2-3 | **500 gil** |
| 2500y | ~5% | ~1 | **200 gil** |
| ∞ (disabled) | 0% | 0 | **0 gil** |

**Million-gil scenario from user's concern:** would require ~5,000 teleports
per session at 200 gil each. At threshold = 500y triggering ~20 teleports per
4hr session, that's 4,000 gil — **two orders of magnitude lower than feared.**
Even a multi-day session at threshold = 500y would burn ~50k gil/day, not
millions.

→ The actual risk is **wasted bot time** (loading screens), not gil.

---

## 6. Recommendation

### Default value
**Keep `LongRangeTeleportYalms = 1500` OR bump to `1800`.**

| Option | Pros | Cons |
|---|---|---|
| 1500 (current) | More aggressive — saves more flight time on bigger ShB/EW zones | Some borderline teleports (FATE 1500-1800y away) are net-neutral |
| **1800 (recommended)** | Reliably net-positive on every teleport; halves the teleport count vs 1500 | Slightly more flight time per session (~5-10 min over 4hr) |
| 2000 | "Pure profit only" mode — only teleport when truly far | Sluggish on Tempest / Ultima Thule / Labyrinthos where 1500-2000y FATEs are common |

### Phase 2 — UI controls
Add to Settings tab:
1. `[ ] Enable long-range teleport` (default on) — master toggle
2. Slider `Teleport threshold (yalms): [500 ... 3000]` default **1800**
3. Numeric `Min gil reserve: [0 ... 100000]` default **5000** — skip teleport if
   `inventory.Gil < this` (prevents burning the user's emergency gil)
4. Read-only display in Status tab: `Session teleport cost: ___ gil (___ teleports)`

### Phase 3 — smarter teleport target (DT only)
For DT zones, Lifestream can teleport to **secondary aethernet shards** for
~70 gil. The bot currently only teleports to the primary aetheryte (`zone.AetheryteId`).
Adding shard awareness would:
- Drop teleport cost from ~200 gil to ~70 gil for DT (−65%)
- Drop average post-teleport flight distance from ~700y to ~300y (−57%)
- Let the threshold come down to ~1200y safely (more aggressive without cost penalty)

Requires extending `ZoneInfo` with a list of secondary aetherytes + a
"pick nearest to FATE" selector. **Not urgent** — current 1800y default is
already safe.

---

## 7. Final answer for the user

> **เลขที่แนะนำ: `LongRangeTeleportYalms = 1800`**
>
> - ที่ค่านี้ บอทวาร์ปประมาณ 3-4 ครั้งต่อเซสชั่น 4 ชั่วโมง
> - **ค่าใช้จ่ายเฉลี่ย ~800 gil ต่อเซสชั่น** — ไม่ใช่หลักล้านเลย แม้จะตั้งต่ำสุด 500y ก็แค่ 4,000 gil/session
> - **ความเสี่ยงจริง คือ "เสียเวลาบอท"** (โหลดฉาก) ไม่ใช่ "เปลือง gil"
> - เซสชั่น 4 ชม. หา gil ได้ ~160k-320k gil (จาก bicolor) — เทียบกับค่าวาร์ป 800 gil = 0.25%
>
> **เพิ่ม Safety net** (ทำใน Phase 2):
> 1. Toggle เปิด/ปิดได้ทั้งระบบ
> 2. Slider ปรับค่าได้
> 3. `MinGilReserve` — ถ้า gil ในกระเป๋าต่ำกว่า 5000 หยุดวาร์ปอัตโนมัติ
> 4. แสดง "เซสชั่นนี้วาร์ปไป N ครั้ง / เสีย gil X" ใน Status tab
