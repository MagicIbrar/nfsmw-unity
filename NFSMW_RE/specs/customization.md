# Customization / shop rules (NFS MW 2005 PC v1.3)

Sources: CC0 decomp `refs/nfsmw/src/Speed/Indep/Src/` (paths below are relative to it), `GLOBAL\FE_ATTRIB.bin` (class
`frontend`), `GLOBAL\attributes.bin` (`fecooling`, `junkman`, `pvehicle`), `GLOBAL\gameplay.bin` (`gameplay`).
Data was read with a scratch probe that links `NFSMWUnity/Assets/NFSMW/Core/Attrib.cs` (same approach as
`tools/gpprobe`). File shorthands:

| tag | file |
|---|---|
| CM | `Frontend/MenuScreens/Safehouse/customize/CustomizeManager.cpp` |
| CC | `Frontend/MenuScreens/Safehouse/customize/CarCustomize.cpp` |
| FEC | `Frontend/MenuScreens/Safehouse/customize/FECustomize.hpp` |
| US / USh | `Frontend/Careers/UnlockSystem.cpp` / `.hpp` |
| VDB | `Frontend/Database/VehicleDB.cpp` |
| QRCS | `Frontend/MenuScreens/Safehouse/quickrace/uiQRCarSelect.cpp` (car lot, My Cars, sell, impound) |
| MS | `Frontend/MenuScreens/Safehouse/career/uiMarkerSelect.cpp` |
| CT | `Frontend/MenuScreens/InGame/CustomTuning.cpp` |

`[?]` = not confirmed (function not decompiled, or marked UNSOLVED in the decomp). Companion specs: `career_flow.md`
(bins, rival races, marker screen order), `physics.md` §2 (how an upgrade level / Junkman / tuning changes the car),
`pursuit.md` §12 (busted, fines, impound).

---

## 1. Data model

### 1.1 Records (VDB, VehicleDB.hpp)
* `FECarRecord`: Handle, `FEKey` (frontend collection, gives `Cost`, `UnlockedAt`), `VehicleKey` (pvehicle),
  `Customization` (handle of a FECustomizationRecord), `CareerHandle` (FECareerRecord), FilterBits.
* `FECustomizationRecord` (VDB:1034): `InstalledPartIndices[139]` (one CarPartDB index per CAR_SLOT_ID, invalid =
  stock/none), `InstalledPhysics` = `Physics::Upgrades::Package { int Part[7]; int Junkman; }` (Junkman is a bit mask
  `1<<type`), `Tunings[3]` (3 tuning presets × 7 floats), `ActiveTuning`.
* `FECareerRecord`: `VehicleHeat` (float, the per-car heat), `TheImpoundData`, bounty, infractions.
* Physics types (`Physics::Upgrades::Type`): 0 TIRES, 1 BRAKES, 2 CHASSIS (suspension), 3 TRANSMISSION, 4 ENGINE,
  5 INDUCTION, 6 NOS, PUT_MAX = 7.
* Car slots used by the shop (`World/CarPartID.h`): BODY 23, SPOILER 44, ROOF 62, HOOD 63, FRONT_WHEEL 66 (rims; the
  rear wheel follows), BASE_PAINT 76, VINYL_LAYER0 77, PAINT_RIM 78, VINYL_COLOUR0_0..3 79–82,
  DECAL_FRONT_WINDOW_TEX0 83, DECAL_REAR_WINDOW_TEX0 91, DECAL_LEFT_DOOR_TEX0..5 99–104, TEX6/7 105/106 (= LNUMBER /
  RNUMBER), DECAL_RIGHT_DOOR_TEX0..5 107–112, TEX6/7 113/114 (numbers), DECAL_LEFT_QUARTER_TEX0 115,
  DECAL_RIGHT_QUARTER_TEX0 123, WINDOW_TINT 131, CUSTOM_HUD 132, HUD_BACKING/NEEDLE/CHARACTER_COLOUR 133–135.

### 1.2 SelectablePart / ShoppingCartItem (FEC:107-272)
* `SelectablePart { CarPart* part; int slot; uint level; Type physType; bool isPerf; state; int price; bool junkman }`.
* State = game-state nibble {AVAILABLE 1, LOCKED 2, NEW 3} | player-state bits {INSTALLED 0x10, IN_CART 0x20}.
* `ShoppingCartItem { ToBuy, TradeIn (nullable), bActive = true }`; `GetPartPrice() = ToBuy.price`;
  `GetTradeInPrice() = (int)(TradeIn.price * gTradeInFactor)`, **gTradeInFactor = 0.25** (CC:33).
* A visual part's **upgrade level** is a property of the CarPart itself: `(GroupNumber_UpgradeLevel >> 5) & 7`; the low
  5 bits are the group number (vinyl group, spoiler filter group, …) (`World/CarInfo.hpp:261`). Brand =
  applied attribute `BRAND_NAME`. These come from the GlobalB car-part database (chunk 0x80034602), which the Unity
  port has **not decoded** (see §9 blocker).

---

## 2. Categories and how each list is built

### 2.1 Menu tree (CC:1592-1880, 2067-2082)
`CustomizeMain` → three sub menus. **Hero car** (`CARTYPE_BMWM3GTRE46`, CM:1387) gets only *Visual*, and no rim paint
(CC:1662, 2070).

| Main | Sub-category (eCustomizeCategory, FEC:20) | Slot / physics type | Screen |
|---|---|---|---|
| PARTS 0x801 | BODY_KIT 0x101 | BODY | CustomizeParts |
| | SPOILERS 0x102 | SPOILER | Spoilers (4 filters by group number 0..3, group 4 shown in every filter, CC:2957-3045) |
| | RIM_BRANDS 0x103 → stock + 10 brands 0x702..0x70B | FRONT_WHEEL | Rims (filter by inner radius = rim size, CC:3329-3407) |
| | HOODS 0x104 | HOOD | CustomizeParts |
| | ROOF_SCOOPS 0x105 | ROOF | CustomizeParts |
| PERFORMANCE 0x802 | ENGINE 0x201, TRANSMISSION 0x202, SUSPENSION 0x203 (CHASSIS), NITROUS 0x204, TIRES 0x205, BRAKES 0x206, FORCED_INDUCTION 0x207 (label turbo or supercharger by `IsTurbo()`, CM:1319) | physics type | CustomizePerformance |
| VISUAL 0x803 | PAINT 0x301 | BASE_PAINT (3 brand filters gloss/metallic/pearl, CC:3829) | Paint |
| | VINYL_TYPES 0x302 → stock + 8 groups 0x402..0x409 (flame, tribal, stripe, racing flag, national flag, body, unique, contest = group 0..7) | VINYL_LAYER0 + VINYL_COLOUR0_0..2 | CustomizeParts then Paint (vinyl colours) |
| | RIM_PAINT 0x303 (not hero car) | PAINT_RIM | Paint |
| | WINDOW_TINT 0x304 | WINDOW_TINT | CustomizeParts |
| | DECAL_LOCATION 0x305 → windshield, rear window, left door, right door, left QP, right QP; doors → 6 positions | DECAL_*_TEX0..5 | Decals |
| | NUMBERS 0x306 | DOOR TEX6/TEX7 (both doors) | CustomizeNumbers (CC:4246) |
| | CUSTOM_HUD 0x307 | CUSTOM_HUD + 3 HUD colour slots | CustomHUD / CustomHUDColor |

Backroom (§5) shows the same Parts/Performance/Visual tree but Visual is only Paint, Vinyls, Decals and Custom HUD
(CC:1669-1674).

### 2.2 Visual part list: `GetCarPartList(slot, list, param)` (CM:1200-1287)
For every CarPart of the tuning car's type and the slot (`CarPartDB.NewGetFirstCarPart(carType, slot, 0, ANY)`):
1. Slot filters: BODY → keep only if the part's LOD-1 model has a streaming solid entry; FRONT_WHEEL with
   `param != 0` → keep only `BrandNameHash == param`; VINYL_LAYER0 → keep if the vinyl texture exists, group number ==
   `param`, and brand `SPECIAL` only on the Collector's Edition. (CustomizeParts additionally drops vinyls named
   CE01/CE02/CE03 off-CE: `IsUnlockableAvailable`, US:816).
2. Decals (unlockable DECAL_LEFT_DOOR/RIGHT_DOOR/LEFT_QP): level is fixed per location (windshield 1, doors 2,
   quarter panels 3); in the backroom the location must be unlocked with the backroom rule (§3.3) or the part is skipped.
3. Other parts, level = part's upgrade level: in the **backroom** keep only if `IsCarPartUnlocked(CAREER, …, backroom)`;
   in the front room **drop level-7 (UNIQUE) parts until the career is beaten** (`HasBeatenCareer`).
4. State: LOCKED if `IsPartLocked`, else NEW if `IsPartNew`; |INSTALLED if it is the installed part, else |IN_CART.
   `price = GetPartPrice(part)` (§4). Locked parts are listed (greyed) with an unlock blurb
   `CUSTOMIZATION_<PARTS_BODYKITS|PARTS_SPOILERS|PARTS_HOODS|PARTS_ROOFSCOOPS|PARTS_RIMS|VISUAL_PAINT|VISUAL_RIMPAINT|
   VISUAL_WINDOWTINT|VISUAL_NUMBERS|VISUAL_HUDS|VISUAL_VINYLS|VISUAL_DECALS|PERF_ENGINE|…|PERF_TURBO|PERF_SUPERCHARGER>_<level>`
   (fallback hash 0x9BB9CCC3) (CM:572-673).
* Rims: the brand screen lists only parts with `InnerRadius == current size`; size cycles between the car type's
  `WheelInnerRadiusMin..Max` (CarTypeInfo, CM:1182-1197), starting at the installed rim's size.
* Paint: the swatch list is the BASE_PAINT (or PAINT_RIM / VINYL_COLOURx) parts whose brand matches the filter;
  colour = applied attributes RED/GREEN/BLUE (CC:3884-3908).
* Stock option: Rims and Vinyl groups have an explicit "stock" first entry (stock wheel part / null vinyl).
* A category menu item is greyed (`IsCategoryLocked`, CM:916-1076) when no part in it is unlocked; rim brands and
  vinyl groups are locked when none of their parts is unlocked (CM:1078-1179).

### 2.3 Performance list: `GetPerformancePartsList(type)` (CM:1289-1309)
* N = `GetNumPackages(type)` = the car's pvehicle `<type>_upgrades` (Physics::Upgrades::GetMaxLevel). One package per
  level 1..N. **There is no explicit stock entry.**
* M = `GetMaxPackages(type)`: tires 3, brakes 4, chassis 3, transmission 4, engine 4, induction 3, nos 3 (CM:400-419).
* Package `i` (1..N) uses the **global tier** `t = M − N + i` for both its unlock check and its price. So a car with
  fewer levels skips the cheap tiers (its single engine level is tier 4).
* N per car (pvehicle, brakes/induction/tires/engine/transmission/nos/chassis):
  * 4/3/3/4/4/3/3 (all tiers): a3, a4, clio, cobaltss, cts, eclipsegt, gti, is300, punto, rx8, sl500, supra, tt
  * 3/2/2/3/3/3/2: 997s, caymans, clk500, db9, elise, gto, imprezawrx, lancerevo8, monaro, mustanggt, rx7
  * 2/1/2/2/2/3/1: 911turbo, corvette, gallardo, viper
  * 1/1/1/1/1/3/1: carreragt, fordgt, murcielago, slr
  * 0 except nos 3: bmwm3gtre46, sl65, camaro, corvettec6r, bmwm3gtr (bonus/hero cars)
* **Junkman package** (level 7, `junkman = true`): shown as an extra last entry when
  (`HasBeenAwardedBKReward && !career`) or (`CareerModeHasBeenCompletedAtLeastOnce && !heroCar`), and
  `CanInstallJunkman(type)` (CC:5021-5031); in the career backroom it is the **only** entry (CC:4994-5006).
* Package names: `PN_LEVEL_%d` with `6 − N + level` (Junkman: 0xEDD14807); 3 description lines + brand icons from
  `GetPerfPkgDesc/Brand(type, M−N+level, line)` (CC:4652-4933).
* Quick race only: Button4 in the Performance menu = "max out" (`MaxOutPerformance`, CM:425-470): puts the highest
  unlocked package of every type in the cart.

---

## 3. Unlock rules

### 3.1 Unlock table `TheUnlockData[57]` (USh:12-21, US:19-107)
Per `eUnlockableEntity` (FEReflected.hpp:19): `{CareerUnlockLevel, CareerIsNewPart, CareerTimesSeen, QuickRace…}`.
`UnlockUnlockableThing(ent, CAREER, level)` sets **both** career and quick-race level (= set, not max) and marks it
new (IsNewPart = level). "New" clears after the customize shop has been entered 4 times (`MarkUnlockableThingSeen`,
called for every entity in CustomizeMain's constructor, CC:1888; US:55-72).

Entity map (US:632-709): BODY→BODY_KIT 11, SPOILER→SPOILERS 12, HOOD→HOODS 14, ROOF→ROOF_SCOOPS 15,
FRONT/REAR_WHEEL→RIM_BRANDS 13, CUSTOM_HUD→CUSTOM_HUD 17, WINDOW_TINT→WINDOW_TINT 18, BASE_PAINT **and PAINT_RIM**
→PAINTABLE_BODY 23, VINYL_LAYER0/COLOURs→VINYLS_GROUP_BODY 40, door TEX6/7→DECAL_NUMBERS 43, window TEX0→
DECAL_WINDSHIELD 44, door TEX0-5→DECAL_LEFT_DOOR 46, quarter TEX0→DECAL_LEFT_QP 48, LICENSE_PLATE→16; perf types
TIRES 4, BRAKES 5, CHASSIS 6, TRANSMISSION 7, ENGINE 8, INDUCTION 9, NOS 10.

### 3.2 Is it unlocked? (front room, US:349-379, 109-143)
```
CareerUnlocked(ent, level) = UnlockAllThings || HasBeatenCareer || CareerModeHasBeenCompletedAtLeastOnce
                             || level <= CareerUnlockLevel[ent]
CarPartUnlocked(part)      = part.level == 0 || CareerUnlocked(map(slot), part.level)
PerfUnlocked(type, tier)   = CareerUnlocked(map(type), tier)          // tier = M − N + i (§2.3)
Decal location unlocked    = CareerUnlocked(DECAL_WINDSHIELD,1) / (DECAL_LEFT_DOOR,2) / (DECAL_LEFT_QP,3) (CM:700-719)
```
Quick race uses the QuickRace level (mirrors career) with the same overrides. Filter (CM:556-570): career →
UNLOCK_CAREER_MODE (2), career backroom → UNLOCK_BACKROOM (10 = career bit + 8), otherwise UNLOCK_QUICK_RACE (1).

### 3.3 Backroom rule (US:357-362, 398-511)
With `backroom = true` additionally: `level <= CareerUnlockLevel + 1`, and if `CareerUnlockLevel == gMaxPartLevels[ent]`
then any level ≤ 7 (unique parts). `gMaxPartLevels` (US:22-37): tires 3, brakes 4, chassis 3, transmission 4,
engine 4, induction 3, nos 3, body kit 4, spoilers 5, rim brands 6, hoods 6, roof scoops 6, custom HUD 4, window tint
4, paintable body 3, vinyls (group body) 6. A backroom **category** is open only if a marker of its type is owned
(`IsBackroomAvailable` → `IsMarkerAvailable(marker)`; rim brands / vinyl groups also need ≥1 RIMS / VINYL marker);
a backroom perf category also needs `CanInstallJunkman(type)` (CM:964-998).

### 3.4 Where unlock levels come from (gameplay.bin)
`AwardUnlockUpgrade(inst)` (US:1134-1155) maps `UpgradePartID` (brakes, chassis, engine, induction, nos, tires,
transmission, bodykit, decals, hood, hud, numbers, paint, rims, roofscoop, spoiler, tint, vinyls; US:1101-1132) to an
entity and calls `UnlockUnlockableThing(entity, CAREER, UpgradeLevel)`. `decals` with level 1/2/3 unlocks
DECAL_WINDSHIELD / LEFT_DOOR / LEFT_QP at that level. The items are `gameplay` collections
`race_bin_NN/unlocks/upgrade_<part>N` (class chain `upgrade_<part> < physics_upgrade|visual_upgrade < upgrade`),
listed in the bin's **`BaselineUnlocks`** field (0xBAF89280). The caller is not in the decomp: **[?] assumed applied
when bin NN becomes the current bin** (bin 15 holds all the level-1 unlocks, so it must run at career start / bin entry).

| Bin entered (after beating rival NN+1) | Unlocks set (part = level) |
|---|---|
| 15 (career start) | paint 1, brakes 1, engine 1, hood 1, bodykit 1, tint 1, transmission 1, vinyls 1, spoiler 1, rims 1, roofscoop 1 |
| 14 | nos 1, hood 2, chassis 1, rims 2, vinyls 2, paint 2 |
| 13 | decals 1, hud 1, induction 1, vinyls 3, tires 1, tint 2, roofscoop 2 |
| 12 | transmission 2, decals 2, hood 3, brakes 2, bodykit 2, spoiler 2, numbers 1, rims 3 |
| 11 | decals 3, vinyls 4, tint 3, roofscoop 3, paint 3, engine 2 |
| 10 | nos 2, spoiler 3, rims 4, chassis 2, hud 2, hood 4 |
| 9 | vinyls 5, induction 2, hood 5, tires 2, roofscoop 4, paint 4 |
| 8 | hood 6, bodykit 3, transmission 3, tint 4, spoiler 4, rims 5, hud 3 |
| 7 | vinyls 6, tires 3, spoiler 5, roofscoop 5, brakes 3 |
| 6 | hud 4, engine 3 |
| 5 | nos 3, chassis 3, roofscoop 6, rims 6, bodykit 4 |
| 4 | brakes 4, transmission 4 |
| 2 | engine 4, induction 3 |
| 16, 3, 1 | (none) |

Other upgrade items: `race_bin_15/15_2_1_sprint/rewards` holds level-1 upgrades of every part (also `numbers1`,
`decals1`, `hud1`, `induction1`, `nos1`, `tires1`, `chassis1`) [?] (that event's rewards look like test data); the
Challenge Series event `race_bin_opm/20_5_1_speedtraps` rewards `brakes` level 4 [?].
Example consequence: carreragt's only engine package is tier 4 → locked until bin 2.
Unique (level 7) parts in the front room: only after the career is beaten (§2.2).

### 3.5 Cars (car lot, US:388-396, QRCS)
`frontend/<car>.UnlockedAt` (UInt8) and `.Cost` (Int32) in FE_ATTRIB.bin. Career: a car is buyable when
**`UnlockedAt >= CurrentBin`** (so `UnlockedAt` = the bin entered after beating rival `UnlockedAt+1`; the lock text
uses `UnlockedAt + 1`, QRCS:1135). Prices / UnlockedAt:

| UnlockedAt | cars (cost) |
|---|---|
| 16 (start) | cobaltss 26000, punto 27000, is300 27000, gti 35000 |
| 15 | (bonus/demo only: camaro 5, m3_gtre46 500000, non-customizable) |
| 14 | a3_20t 32000, tt 35000 |
| 13 | a4_32 35000, eclipsegt 30000 |
| 12 | clio 40000, supra 40000 |
| 11 | cts 32000, rx8 32000, mustanggt 36000 |
| 10 | sl500 75000, lancerevo8 36000 |
| 9 | gto 35000, monaro 35000, caymans 60000 |
| 8 | rx7 31000, imprezzawrx 42000 |
| 7 | elise 48000, clk500 75000 |
| 6 | 997s 75000, db9 90000 |
| 5 | viper 98000, gallardo 120000 |
| 4 | corvette_car 80000, 911turbo 105000 |
| 3 | fordgt 270000, murcielago 265000 |
| 2 | carrera_gt 280000, slr 300000 |

Quick race car unlocks (US:155-295): same `UnlockedAt >= CurrentBin`, the 10 starter types always, Blacklist preset
cars BL2..BL15 when `CurrentBin <= n−1`, bonus cars by flags (career beaten, challenge series, Castrol, CE).
Buying (career car lot, QRCS:613-631): `CreateNewCareerCar` (max 25 career cars), `SpendCash(Cost)`.
**Selling** (My Cars, QRCS:475-487, 488-516): not allowed if impounded or if fewer than 2 available career cars;
pays **`Cost / 2`** (FE cost of the car type; parts and upgrades are not refunded). `SpendCash` clamps to 0 when the
amount exceeds the cash (FEDatabase.cpp:312).

---

## 4. Prices

### 4.1 Part price (CM:261-285, US:711-753)
```
GetPartPrice(part):
  if backroom: 0
  if perf:     tier = M − N + part.level;  cost = FECarPartInfo(map(type), tier).Cost
  if slot in {VINYL_COLOUR0_0..3, HUD_BACKING/NEEDLE/CHARACTER_COLOUR}: 0
  else:        cost = FECarPartInfo(map(slot), part.upgradeLevel).Cost
FECarPartInfo(ent, lvl) = first frontend collection whose parent is "carparts" and feCarPartName == ent,
                          entry of its feCarPartInfo[] array {Level, Rep, Cost} with Level == lvl; none → 0
```
Table (FE_ATTRIB `frontend/carparts/*`, feCarPartInfo, Rep is 0 everywhere; level 7 = UNIQUE):

| collection (entity) | L1 | L2 | L3 | L4 | L5 | L6 | L7 |
|---|---|---|---|---|---|---|---|
| engine (8) | 5500 | 14200 | 29800 | 98000 | | | 0 |
| transmission (7) | 4800 | 12200 | 32000 | 75200 | | | 0 |
| chassis (6) | 3600 | 7600 | 10000 | | | | 0 |
| nos (10) | 4900 | 12500 | 18000 | | | | 0 |
| tires (4) | 1400 | 4800 | 11800 | | | | 0 |
| brakes (5) | 3100 | 5200 | 11400 | 26800 | | | 0 |
| induction (9) | 5400 | 25000 | 76000 | | | | 0 |
| bodykits (11) | 5000 | 7500 | 10000 | 15000 | | | 0 |
| spoilers (12) | 1000 | 2000 | 3000 | 5000 | 6000 | | 7000 |
| rims (13) | 1000 | 1500 | 3500 | 4500 | 6000 | 9000 | 10000 |
| hoods (14) | 1500 | 2500 | 3500 | 4500 | 5500 | 7500 | 10000 |
| roofscoops (15) | 500 | 1000 | 1500 | 2000 | 3000 | 4000 | 5000 |
| huds (17) | 500 | 600 | 800 | 1000 | | | 1500 |
| windowtint (18) | 500 | 550 | 600 | 750 | | | 1000 |
| paint (23), used for BASE_PAINT **and** PAINT_RIM | 500 | 1000 | 1500 | | | | 0 |
| rimpaint (24) (unused for prices: PAINT_RIM maps to 23) | 300 | 5 | 5 | | | | 0 |
| vinyls (40) | 500 | 600 | 700 | 800 | 1000 | 1200 | 1500 |
| numbers (43) | 100 | 100 | 100 | | | | 100 |
| decal_window (44) | 100 | 100 | 100 | | | | 100 |
| decal_door (46) | 100 | 100 | 100 | | | | 100 |
| decal_quarter (48) | 100 | 100 | 100 | | | | 0 |

Level 0 (stock parts) = 0 everywhere. Junkman perf (level 7) costs 0 (tier ≥ 7 has no paid entry).
Numbers: the cart hides LEFT_DOOR LNUMBER/RNUMBER and RIGHT_DOOR RNUMBER rows (CC:1188-1191) and
RIGHT_DOOR_RNUMBER (114) is excluded from totals and never charged (CM:186, 212) [?] (net: 3 of the 4 number slots are
charged). Toggling one number row toggles all four (CC:870-903).

### 4.2 Trade-in (CM:56-110, CC:2382-2405)
* `CanTradeIn(part)` (UNSOLVED [?]): never for perf; effectively **only model slots < 76** (body kit, spoiler, roof
  scoop, hood, rims); paint, vinyls, decals, tint and HUD give no trade-in.
* On `AddToCart`, if the slot can trade in and something is installed, a trade-in SelectablePart for the **installed
  part** is created with price = `GetPartPrice(installed)` (so a stock level-0 part gives 0). Replacing a cart item of the
  same slot keeps the old item's trade-in.
* Trade-in credit = `(int)(installedPrice × 0.25)` (shown on the part screen as "trade in value").

### 4.3 Cart totals (CM:178-204) and checkout (CM:206-238)
```
CCT_PART_PRICES = Σ active items (skip slots 79-82, 114, 133-135): backroom ? 1 : item.partPrice
CCT_TRADE_IN    = −Σ active items with trade-in: (int)(item.GetTradeInPrice() × 0.25)
CCT_TOTAL       = PART_PRICES + (backroom ? 0 : TRADE_IN)
```
[?] `GetCartTotal` is UNSOLVED and applies the 0.25 factor twice (GetTradeInPrice already includes it), while
`Checkout` charges `partPrice − GetTradeInPrice()` (factor once). Implement the single factor everywhere (part screen
and checkout agree on it) and treat the double factor as a decomp artefact.
Checkout, for each active item: in career and slot ≠ 114: front room → `cost = partPrice − tradeIn`; `cost >= 0 ?
SpendCash(cost) : AwardCash(−cost)`; backroom → `UtilizeMarker(physType or slot)` (one marker per item). Then perf →
`InstallPerfPkg(type, level)`; visual → `InstallPart(slot, part)` + `UpdateHeatOnVehicle` (§6). Empty cart, reset
preview. Career entry backs up the stable (`BackupCarStable`, CC:1893) and autosaves if dirty.
`InstallPerfPkg` (CM:369-386): level 7 sets the Junkman bit, else `Part[type] = level`; if `SetPackage` fails the change is
reverted (bit cleared / level 0).

### 4.4 Other money
* Impound release fee = `Cost × g_fImpoundPercentageOfOriginalCost` (VDB:900-903; constant not in decomp [?]).
* Bust fine: see `pursuit.md` §12. Cash markers: see §5.

---

## 5. Markers (Junkman / backroom / impound) (USh:87-161, US:830-1099)

### 5.1 Types
`ePossibleMarker`: 1 BRAKES, 2 ENGINE, 3 NOS, 4 INDUCTION, 5 CHASSIS, 6 TIRES, 7 TRANSMISSION, 8 BODY, 9 HOOD,
10 SPOILER, 11 RIMS, 12 ROOF_SCOOP, 13 CUSTOM_HUD, 14 VINYL, 15 DECAL, 16 PAINT (1..16 = customize markers),
17 GET_OUT_OF_JAIL, 18 PINK_SLIP, 19 CASH, 20 ADD_IMPOUND_BOX, 21 IMPOUND_RELEASE.
Inventory: `OwnedMarkers[63] {Marker, Param, State}`; Add = first empty slot, Utilize = first owned match → emptied.
`GetNumCustomizeMarkers` counts types 1..15 (loop `< CUSTOMIZE_LAST` = 16, so **PAINT markers are not counted**,
US:989-995 — the "backroom available" icon ignores paint markers). Saved as raw bytes in the profile.

### 5.2 Award
gameplay reward items: `reward_<x> < reward_backroom|reward_<type> < reward_marker`; `RewardMarkerType` text
(`backroom` + `UpgradePartID` part, `cash_bonus` + `CashReward`, `pink_slip`, `out_of_jail_free`,
`release_car_from_impound`, `add_impound_box`) → marker via `ConvertBigBangMarkerAward` (US:1011-1052).
`AwardMarker(inst, immediate)` (US:1054-1087): immediate → cash paid / rival car given / marker added; else queued
for selection (`TempSelectionMarkers[6]`, pink slip param = `FEngHashString("BL%d", CurrentBin)`).
The last boss race of each bin lists the bin's `rewards` children in `RewardsForWinner` (6 items). After the rival
race, **SafeHouseMarkers.fng** (MS:18-125) shows them face down; the player picks **2**: pink slip →
`AwardRivalCar(BLn)` (preset car with its preset parts and `presetride` performance, VDB:507-537); cash → `AwardCash`;
others → inventory. (Display shuffle: three random swaps among the first 3 cards, `bRandom(3)`.)

| Bin (rival) | 6 marker cards (choose 2) |
|---|---|
| 15 | brakes, rims, vinyl, cash 10000, jail-free, pink slip |
| 14 | chassis, hood, vinyl, cash 12500, impound box, pink slip |
| 13 | body, hud, transmission, cash 15000, cash 10000, pink slip |
| 12 | body, induction, vinyl, impound box, impound release, pink slip |
| 11 | rims, spoiler, tires, cash 20000, jail-free, pink slip |
| 10 | chassis, hood, roofscoop, impound box, jail-free, pink slip |
| 9 | hood, hud, induction, impound box, impound release, pink slip |
| 8 | body, spoiler, transmission, impound box, jail-free, pink slip |
| 7 | spoiler, tires, vinyl, jail-free, impound release, pink slip |
| 6 | chassis, rims, vinyl, cash 50000, jail-free, pink slip |
| 5 | hood, induction, vinyl, impound box, jail-free, pink slip |
| 4 | body, engine, vinyl, cash 100000, impound release, pink slip |
| 3 | rims, roofscoop, tires, impound box, jail-free, pink slip |
| 2 | hud, induction, vinyl, jail-free, impound release, pink slip |
| 1 | none |

Also: prologue 16.2.3 gives 3 jail-free markers immediately; the **first bust** of a career tops jail-free markers up
to 4 if the player has none (uiInfractions.cpp:19-24). Backroom `paint` and `decals` reward items exist as classes but
no bin uses them [?].

### 5.3 Spending
* Backroom (career, not hero car): CustomizeMain Button5 toggles front room / backroom when
  `GetNumCustomizeMarkers() > 0` (CC:1964-1983). If the player owns an INDUCTION or NOS marker that this car cannot use
  (`!CanInstallJunkman`), a warning dialog is shown first; the switch happens only if other usable markers remain.
* In the backroom every item costs exactly **one marker of its type** (cart shows markers left per type, CC:932-1015;
  checkout is always allowed, CC:905-913). Perf categories offer only the **Junkman package** (sets the junkman bit:
  the part's `junkman/default.<type>_package` multipliers on top of the current level, `physics.md` §2); visual
  categories offer parts up to one level above the current unlock (or unique level-7 parts once the category is fully
  unlocked). Visual items bought there reduce heat more (§6).
* GET_OUT_OF_JAIL: on the busted screen, pay the fine with cash (strike +1, infractions served, heat = 1.0) or with the
  marker (infractions of this pursuit waived, no strike) (uiInfractions.cpp:144-167).
* IMPOUND_RELEASE: releases an impounded car (sets heat 0, strikes 0) instead of paying the fee (QRCS:80-85, 290-315).
* ADD_IMPOUND_BOX: `MaxBusted + 1` for the selected car (default 3, capped at `g_MaximumMaximumTimesBusted` [?, 5 per
  game text]) (QRCS:66-70, VDB:1275-1287).

---

## 6. Heat reduction by visual changes (CM:494-553, VDB:1326-1390)
In career only, for each **non-performance** item at checkout:
`VehicleHeat *= fecooling.<field> × mod`, **mod = 0.75 in the backroom, 1.0 in the front room**.
`fecooling/default` (attributes.bin):

| slot bought | field | factor (front) | backroom |
|---|---|---|---|
| BODY (body kit) | NewBodyKit | 0.69 | 0.5175 |
| BASE_PAINT | NewPaint | 0.70 | 0.525 |
| VINYL_LAYER0 | NewVinyl | 0.75 | 0.5625 |
| HOOD | NewHood | 0.90 | 0.675 |
| SPOILER | NewSpoiler | 0.90 | 0.675 |
| FRONT_WHEEL (rims) | NewRim | 0.95 | 0.7125 |
| PAINT_RIM | NewRimPaint | 0.97 | 0.7275 |
| ROOF | NewRoofScoop | 0.97 | 0.7275 |
| any decal TEX0..5 / window / quarter | NewDecal | 0.98 | 0.735 |
| WINDOW_TINT | NewWindowTint | 0.99 | 0.7425 |
| DECAL_LEFT_DOOR_LNUMBER (105) | NewBodyKit per decomp [?] | 0.69 | |

* `NewNumbers` (0.97) exists but `AdjustHeatOnNumbersApplied` is never called; the decomp sends the left-door number
  slot to the body-kit factor (flagged "is this a bug?" in CM:530) [?]. Other number slots, vinyl colours, HUD and
  perf parts do not change heat.
* Factors compound per item (5 decals = 0.98^5). No floor is applied here. The heat meters show `GetActualHeat` and a
  preview (`GetPreviewHeat` = the highlighted part, if different from the installed one, plus all other active cart
  items in other slots; `GetCartHeat` = all active cart items) on a 1..5 scale (`CustomizeMeter` min 1, max 5).
* Other heat changes (for reference): event win / milestone / evade multiply by `pursuitlevels/default`
  `EventWinHeatAdjust` / `MilestoneCompleteHeatAdjust` / `EvadeSuccessHeatAdjust` (VDB:1308-1324); paying a bust fine
  sets 1.0; impound release sets 0.

---

## 7. Performance tuning sliders (they exist in MW: pause menu, CT)
* Pause menu "Performance Tuning" → `Pause_Performance_Tuning.fng` (`CustomTuningScreen`, FEPackageData.cpp:425).
  Not in the safehouse shop.
* 7 sliders: STEERING, HANDLING, BRAKES (bias), RIDEHEIGHT, AERODYNAMICS, NOS, INDUCTION (turbo or supercharger label).
  3 presets (`CurrentTuningType` 0..2, L/R trigger cycles), the active one is stored in the customization record.
* A slider is active only if: STEERING/HANDLING/RIDEHEIGHT → chassis level > 0; BRAKES → brakes level > 0;
  AERODYNAMICS → a non-stock spoiler **or** non-stock body kit installed; NOS → nos level > 0; INDUCTION → induction
  level > 0 (CT:239-281). Inactive sliders are greyed but still shown.
* Range `LowerLimit(path)..UpperLimit(path)` (not decompiled [?]; physics.md assumes −1..+1), step = (max−min)/10,
  shown as `(cur−min)/(max−min)×10` with one decimal (0..10, centre 5). Accept stores all 3 presets + active preset and
  fires `ETuneVehicle`; Back with changes asks to discard. Effects of each value: `physics.md` §2 "Tunings".
* Upgrades themselves (shop): the level L of each type lerps the pvehicle part between stock and the top ref
  (`physics.md` §2); Junkman multiplies selected fields. The shop meters show `Physics::Info::EstimatePerformance`
  TopSpeed / Handling / Acceleration of the preview vs installed setup (CM:472-492, CC:4946-4951).

---

## 8. Shopping cart flow (CC:789-1215, 2283-2380, 1284-1338)
1. Entering the shop (`CustomizeMain`): `TakeControl` copies the installed record into a **preview record**; every
   later selection only changes the preview (car viewer + preview physics).
2. Part screen, Accept on the highlighted part: locked → "locked" sound; already in cart → sound; installed → if a
   different part of that slot is in the cart, remove it (back to installed); else `AddToCart` (replaces any cart item
   for the same slot / physics type) and mark it IN_CART. Nothing is charged yet.
3. Start (any customize screen) → shopping cart (`ShoppingCart.fng`, `ShoppingCart_QR.fng` outside career,
   `ShoppingCart_BACKROOM.fng`). Rows: buying part, price, trade-in; vinyl colours, HUD colours and 3 number slots are
   hidden. Button4 toggles an item's check (active), Button5 unchecks all; header shows parts total, trade-in total,
   total, cash − total (front room) or markers per type and markers left (backroom).
4. Accept: if no active items → empty cart and leave. Else front-room career requires `CCT_TOTAL <= cash`
   (else "not enough cash" dialog 0x0A984A42); confirm dialog → `Checkout()` (§4.3) → back to CustomizeMain
   (leaving the backroom if in it).
5. Back from the cart removes **unchecked** items (and the vinyl colour items when the vinyl is unchecked), resets preview.
6. Backing out of a Parts / Performance / Visual menu with active cart items forces the cart screen open; with only
   inactive items the cart is emptied (CC:1296-1317). Back from CustomizeMain in career returns to free roam.
7. Quick race / non-career: same cart; prices shown, nothing charged, no heat, no markers; Button4 = max performance.

---

## 9. Comparison with the Unity port (deviations)

Files: `Runtime/UI/Screens.cs` (CustomizeScreen 228, PaintScreen 469, PerformanceScreen 541, CarLotScreen 264),
`Runtime/UI/VisualScreens.cs`, `Runtime/Game/CarParts.cs`, `Runtime/Game/GameSession.cs`, `Profile.cs`, `CarSpec.cs`.

1. **Menu tree**: Unity has Paint / Performance / Visual(body kits, spoilers, rims, hoods, roof scoops, vinyls). Game:
   Parts (body kit, spoiler, rims, hood, roof scoop) / Performance / Visual (paint, vinyls, rim paint, window tint, decals,
   numbers, custom HUD). Missing: rim paint, tint, decals, numbers, custom HUD, vinyl colours, paint brand filters,
   rim-size cycling, spoiler filters, hero-car restriction, backroom.
2. **Part source**: Unity derives parts from solid names (CAR_KITnn, STYLEnn_HOOD, shared spoiler/roof/wheel packs,
   VINYLS.BIN) because the CarPartDB (GlobalB chunk 0x80034602) is not decoded. The game's lists, **upgrade levels,
   group numbers, brands and inner radii all come from that DB** → main blocker for exact lists and prices.
3. **Prices invented** in Unity (`KitPrice 2500+1500k`, hood 1500, spoiler 2000, carbon +1500, roof 800, vinyl 1200,
   rim by size `1000+400(size−17)`, "no spoiler" 200, paint flat 500, perf `baseCost×0.04×stage + 1000×stage`). Game:
   FE_ATTRIB `feCarPartInfo[level].Cost` (§4.1); rims priced by part level, not size; carbon parts are just parts with
   their own level.
4. **Performance**: Unity = 3 stages for every type, scaled to the car's `*_upgrades` with `round(stage×N/3)`
   (CarSpec.cs:145). Game = N packages (1..4 per type, §2.3) at tier `M−N+i`, prices by tier, plus Junkman (level 7).
   Unity has no stock/downgrade entry either (the game has none either, but allows buying a lower package).
5. **Unlocks**: Unity locks only body kits (`bin <= 16−3k`, invented); everything else always unlocked. Game: per-bin
   `BaselineUnlocks` levels (§3.4), unique parts after career, locked parts visible with unlock blurb, NEW flags.
6. **Purchase model**: Unity buys and installs immediately per item. Game: preview + cart + checkout, multi-item,
   cash check on the total, **trade-in 25 %** of the installed part's price for body/spoiler/roof/hood/rims.
7. **Heat**: Unity `OwnedCar.Heat` is an int 0..5 and customization never changes it. Game: float `VehicleHeat`
   multiplied per visual item (§6), backroom ×0.75 extra.
8. **Markers/backroom/impound**: absent in Unity (no marker inventory, no SafeHouseMarkers pick-2 screen, no pink-slip
   rival cars, no cash markers, no jail-free/impound markers, no Junkman parts).
9. **Car lot unlock off by one**: Unity `UnlockedAt > Profile.Bin` (+ text "beat Blacklist #UnlockedAt"); game
   `UnlockedAt >= CurrentBin` and text #UnlockedAt+1, where CurrentBin = Unity `Profile.Bin` after a rival falls.
10. **Selling**: Unity hides Sell Car; game sells for `Cost/2` (needs ≥2 cars, not impounded). Max 25 career cars.
11. **Tuning sliders**: Unity's pause "Performance Tuning" opens the shop PerformanceScreen; game opens the 7-slider
    CustomTuningScreen with 3 presets (§7).
12. **Quick race**: Unity charges nothing (matches) but shows no locks; game applies quick-race unlock levels (mirror
    of career) unless the career was completed.

### Blockers / open items
* Decode CarPartDB (GlobalB 0x80034602): per-part slot, upgrade level, group, brand, inner radius, CARBONFIBRE, RGB.
* Caller/timing of `AwardUnlockUpgrade` for `BaselineUnlocks` and of `AwardMarker` (GManager, not in decomp) [?].
* `Physics::Upgrades::CanInstallJunkman`, `Tunings::LowerLimit/UpperLimit`, `g_fImpoundPercentageOfOriginalCost`,
  `g_MaximumMaximumTimesBusted` are not in the decomp: read from speed.exe (Ghidra) when needed.

---

## 10. Last-mile round (2026-10-24): CarCustomize.cpp / CustomizeManager.cpp ported screen by screen

Status: **CarCustomize.cpp ported** (every class of the file has a counterpart; the debug screen DebugCarCustomize.cpp is
N/A), **CustomizeManager.cpp ported**, **UnlockSystem.cpp ported** (the online unlocker and the save buffers are N/A).
Files: `Runtime/UI/Safehouse/CustomizeExe.cs` (tables, CustomizeMeter, CustomizationScreenHelper, TwoStageSlider,
ArrayScroller, FEng helpers), `Runtime/UI/Safehouse/CustomizeScreen.cs` (CustomizeMain), `Runtime/UI/VisualScreens.cs`
(CustomizeCategoryScreen, CustomizeSub = `VisualScreen`, CustomizationScreen, CustomizeParts = `PartScreen`,
CustomizeSpoiler, CustomizeRims, CustomizeDecals), `Runtime/UI/Safehouse/CustomizeShopScreens.cs` (CustomizePaint,
CustomizePerformance, CustomizeHUDColor, CustomizeNumbers), `Runtime/UI/ShopScreens.cs` (CustomizeShoppingCart),
`Runtime/UI/Screens.cs` (IconScrollerMenu), `Runtime/Game/Shop.cs` (manager additions). Harness: `fecust` (object hashes
per package, 150 language hashes, 102 texture hashes, the frontend `p_<type>_<level>` brand arrays) and `fecust objtypes`.

### 10.1 What each screen now does (CC = CarCustomize.cpp line)
* **CustomizeMain** (CC:1881-2082): BuildOptionsList with the exe's icon / name hashes (front 0x6E0CA66C / 0x3987D054 /
  0x3E31BA56, backroom 0x73272ED2 / 0xC61C8D3A / 0xE69D4F7C), MarkUnlockableThingSeen, SetTitle lower-cased into TITLE_GROUP,
  BACKROOM 0xA1CAFF8D / FRONTROOM 0x005C01C5 package messages, RefreshHeader (0xDC6EE739 prompt; the category screen's status
  icon 0xCFFB7033 = locked 0xF0574BB2 / new 0xCFFB7033 + APPEAR, heat meter, cart total 0x7A6D2F71 / cash 0xC60ADCFD or the
  backroom marker lines 0x63CA8308 / 0x83E3CD39 / 0x23D918FE), SwitchRooms (key 1, the 0x003B3E83 warning), key 4 cart,
  key 3 showcase, QUIT_SCREEN 0x6D5D86A1 on back.
* **CustomizeSub** (CC:1347-1880) as `VisualScreen(category)`: Parts / Performance / Visual / RimBrands (stock entry
  0x0F3990B6 + 10 brands, GetRimBrandIndex) / VinylTypes (stock 0x21F3D114 + 8 groups) / DecalLocation / DecalLeftDoor /
  DecalRightDoor positions, with AddCustomOption's backroom skip and lock / new status, the in-cart (0x1A777E25) / installed
  (0x696AE039) mark 0xD0582FEB on the brand and group lists, SetStockPartOption (stock rim / no vinyl straight into the cart,
  installed + in cart → the cart item and the vinyl colours are removed), the rim paint check 0xBDB19A9F, the quick race
  Performance "max" prompt 0x5AEC8D91 + key 2 → 0x892CB612 → MaxOutPerformance, titles 0x055DCE1A / 0x5D285AE7 …
* **CustomizationScreen** (CC:2257-2456) + **CustomizationScreenHelper** (CC:2084-2255): accept rules (locked → flash
  0xCFFB7033 + wrong sound, in cart → flash 0xD0582FEB, installed → drop the slot's cart item, else AddToCart replacing the
  slot's item), DrawTitle, SetCareerStuff (backroom marker lines / cost 0xDBB80EDD, cash 0xC60ADCFD, cart total 0x7A6D2F71,
  trade-in 0xA91EDA8A; outside the career the cash box 0x8D1559A4, the meter, 0x24C6BFAD and 0xEA903012 are hidden),
  SetPartStatus (0x6F25A248 "n" / 0xB2037BDC "N", the unlock overlay 0xEBC3E6B7 with blurb 0xA6298E25 for a locked part),
  the 0.3 s preview delay after a scroll (ScrollTime / bNeedsRefresh, FEMSG_SCREEN_TICK at 0.25 s), trade-in value =
  installed part cost × 0.25 for CanTradeIn slots.
* **CustomizeParts** (CC:2459-2900): body kits / hoods / roofs / tint / custom HUDs / one vinyl group; carbon hoods / roofs
  swap to 0x68495926 / 0xFC618215 (backroom 0x2478E136 / 0xCD6B4E26); the name line 0x5E7B09C9 (LANGUAGEHASH or the DB
  name); vinyls with remap colours (attribute 0x6212682B) label the button "Continue" 0x8098A54C and open the vinyl colour
  screen with the TempColoredPart; HUDs open the gauge colour screen; the HUD preview 0xDEE8632B shows TACH_FILL_nn,
  <rpm>_LINES_nn (7000 / 8000 / 9000 / 10000 by MAX_RPM), TACH_NEEDLE_nn, TURBO_* (turbo cars) from GLOBAL\HUDS_Custom_nn.bin
  (`HudTextures.Ensure`), tinted by the installed / cart colour parts or the defaults.
* **CustomizeSpoiler** (CC:2902-3045): filter 0..3 (group 4 everywhere), L / R triggers cycle it, 0x78008599 ← 0x01F0E2B2 /
  0x0205B328 / 0x09912746 / 0x0E7416FC, selection kept per filter, carbon icons 0x4D1C18BA / 0x611D142A.
* **CustomizeRims** (CC:3293-3453): one brand (GetCategoryBrandHash), inner radius cycled by the triggers between the car
  type's WheelInnerRadiusMin..Max, 0xE6782841 `nn"`, the name minus its 6-character size suffix.
* **CustomizePaint** (CC:3455-3987): ArrayScroller 20 × 4 over COLOR_1..40 (feArrayScrollerMenu.cpp ported as
  `ArrayScroller`: ScrollHor wraps in the row, ScrollVer by rows, HIGHLIGHT / UNHIGHLIGHT / INIT / GREY slot scripts),
  brand filters gloss 0x02DAAB07 / metallic 0x03437A52 / pearl 0x03797533 (texts 0xB6763CDE / 0x452B5481 / 0xB715070A),
  rim paint 0xDA27 ("Powder Coat" 0xB3100A3E, arrows 0x2C3CC2D3 / 0x53639A10 / 0x2C526172 hidden), vinyl colours 0x03E871F1
  per layer (filter texts "Color 1/3" 0x5198BA16 …; one-colour vinyls hide the arrows; TRIGGER 0x1A7240F3 shows them),
  swatch colour = RED / GREEN / BLUE, the vinyl + colours go to the cart only when one differs from the installed set.
* **CustomizeDecals** (CC:3989-4228): removal entry (0x697B4AD4, "Stock" 0x60A662F5), brand decals whose NAME is the brand
  or brand + "_WHITE", triggers toggle black / white (0x889BACB6 ← 0x41F0A3A5 / 0x436A98E9) keeping the brand, locked at the
  location's fixed level (1 / 2 / 3), CurrentDecalLocation from the location menu, GetSlotIDFromCategory.
* **CustomizeNumbers** (CC:4230-4596) on Numbers.fng (`NumbersScreen`): digits 0x2A08BA92 / 0x1A88DC05 ("-" until set),
  left / right switch the digit, up / down count, the preview wears slots 113 / 114 / 105 / 106, accept adds the right-door
  digits plus free left-door copies, key 2 removes (four removal items when installed), the 0x7A8355D9 prompt.
* **CustomizePerformance** (CC:4598-5044): packages as icons 0xB8C8C0D4, Junkman rules (career completed / BK reward
  [?: a bonus car stands in for HasBeenAwardedBKReward], backroom = Junkman only), initial = installed or cart level,
  TwoStageSlider ACCELERATION / HANDLING / TOPSPEED (preview fill, installed handle; `PerformanceRatings.Estimate`),
  DETAIL_TEXT_LINE1..3 + PERFORMANCE_DETAILS_ICON1..3 from PD_<type>_<tier>_<line> (Castrol / rotary variants, tier 0 texts
  for Junkman / backroom), BRAND_ICON_1..3 from the car's frontend `p_<type>_<tier>[line]` (stock tier brands 0xAD6A0504 …),
  0x5E7B09C9 ← PN_LEVEL_(6 − N + level) / 0xEDD14807, QUICKRACE 0xDE511657 outside the career.
* **CustomizeHUDColor** (CC:3047-3291): three layer icons (needle 0x70F56628 / lettering 0xBF6682C9 / face 0xCC9E1CE4),
  COLOR_1..10 swatches of the layer's colour parts, cursor 0xB893252A at (+69, +56), up / down scroll colours, left / right
  change the layer, the gauge preview tinted live; accept puts the HUD and every layer's pick in the cart (a different cart
  HUD loses its colours).
* **CustomizeShoppingCart** (CC:302-1215) on `WidgetMenuScreen` (PART_NAME_n / PRICE_n / TRADE_IN_n / CHECK_ICON_n /
  OPTION_BACKING_n, masters 0xA753C46C / 0xC128B184, 4 rows in the career / 6 outside, no wrap): FEShoppingCartItem names
  ("category: part" with the paint type + SPEECHCOLOUR, rim name + size, carbon, numbers, decal slot forms), check icons
  0x696AE039 / 0xE719881C, CHECKED / UNCHECKED / GREY scripts, Position (check −10, title +30, trade-in right edge +150,
  price right edge +40), header 0x842B0E89 / 0xD57C95E1 "Add" 0x028FEADD / "Remove" 0x5DABCBC0, totals or the backroom
  marker rows (MARKER_GROUP_n / MARKER_NUM_n / MARKER_BLOOM_n with the room's icons 0xEB957471..7 and 0x47DF0E21..23 hidden),
  "… Markers Used / Remaining" 0xA03A752F / 0x4AC68298 (parts) 0x358DB897 / 0x68342700 (performance) 0x93296E59 / 0x78F1C602
  (visual), checkout dialogs 0x71D9E710 / 0x8EBAA44B / 0x04810898, not enough cash 0x0A984A42, back drops the unchecked
  items and the colours of an unchecked vinyl, 0xCF91AACD → ExitShoppingCart (CustomizeMain, leaving the backroom).
* **CustomizeMeter** (CC:212-300): HEAT_BASE_LED_1..10 OFF 0xCCFA / COOL 0x13FF6C / ON 0x63C within the preview's stage,
  HEAT_X<stage> on 0x5FFEE1D8 / 0xE637955C (ZOOM 0x209C24 when the preview changes); drawn on every customize screen, the cart
  and the career Car_Select.

### 10.2 CustomizeManager.cpp additions
GetUnlockHash (`CustomizeTables.UnlockHash`, 0x9BB9CCC3 fallback), IsCategoryLocked / IsRimCategoryLocked /
IsVinylCategoryLocked / IsCategoryNew (CM:740-1180), TempColoredPart (CM:287), GetActivePartFromSlot, GetStockCarPart,
GetInstalledPerfPkg, CanTradeIn, GetPerformanceRating (via PerformanceRatings.Estimate), SetInParts / SetInPerformance
(the cart's marker rows), GetActualHeat / GetPreviewHeat / GetCartHeat (already there). IsTurbo = CarSpec Spool > 0;
IsCastrolCar = Ford GT with the Castrol GT awarded [?: stands in for the easter egg]; IsRotaryCar = rx7 / rx8.

### 10.3 Deviations / open
* The customize screens stay plain FEScreens (no FEng message engine): the package's INIT / leave animations and the
  0x587C018B → EXIT_COMPLETE → switch sequence are collapsed into immediate push / pop; INIT_COMPLETE is taken as immediate.
* IconScrollerMenu (feIconScrollerMenu.cpp) is now exact geometry (see fe_safehouse.md §15.4); the BUTTON_PRESSED → leave
  → EXIT_COMPLETE → React chain acts at once.
* HUD colour cursor offset (+69, +56) is applied in the swatch's parent space [?].
* Showcase::FromIndex / FromFilter / FromColor restores are implicit (the screens stay alive under the showcase).
* `IsBackroomAvailable` is the marker test only (the rim / vinyl marker counts are in the category lock tests, US:398-511).
* The sound triggers (NotifySoundMessage) of the customize screens are not sent (the FE sound layer takes FEng messages).

## Round 9 (2026-10-25) — UnlockSystem re-diff
* EEnterBin 0x62C2D0 calls ClearAllNewStatus 0x577740 (ClearNewUnlock 0x576870 for every entity, both modes) before the bin's
  baseline EAwardUpgrades; `ShopUnlocks.Ensure` now does the same per bin, so only the newest bin's unlocks show as new.
  UNLOCK_IS_OLD is −1 in the exe, the port stores 0 (a level-0 "new" state cannot arise because Ensure clears first) [?].
* No references: CheatToGetMarkers 0x58AE00, GetCarPartRep 0x58AD60, IsBonusCarAvailable 0x58ADB0, OnlineUnlocker::IsCarUnlocked
  0x58A550; the other OnlineUnlocker functions are online only.
* IsEventAvailable / IsTrackUnlocked for quick race: fe_safehouse.md §17.9. FEInfractionsData::GetFineValue keys: §17.10.
