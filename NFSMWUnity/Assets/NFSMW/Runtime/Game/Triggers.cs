using System;
using System.Collections.Generic;
using NfsMw.Core;
using NfsMw.Core.Lua;
using UnityEngine;

namespace NfsMw.Game.Gameplay
{
    /// <summary>
    /// GIcon (Gameplay/GIcon.cpp, ctor 0x5E9EC0): the world / map icon a trigger or marker carries. Types (GetType): 1 sprint,
    /// 2 circuit, 3 drag, 4 knockout, 5 tollbooth, 6 speedtrap race, 7 rival, 8 safehouse, 9 car lot, 10 custom shop,
    /// 11 hiding spot, 12 pursuit breaker, 13 speed trap, 14 speed trap in race, 15 area unlock, 16 checkpoint. Flags: 1 shown
    /// on the map, 2 shown in the world, 4 spawned, 8 visible, 0x80 GPSing.
    /// </summary>
    public sealed class GIcon
    {
        public const int Sprint = 1, Circuit = 2, Drag = 3, Knockout = 4, Tollbooth = 5, SpeedTrapRace = 6, Rival = 7, Safehouse = 8,
            CarLot = 9, CustomShop = 10, HidingSpot = 11, PursuitBreaker = 12, SpeedTrap = 13, SpeedTrapInRace = 14, AreaUnlock = 15, Checkpoint = 16;
        public const int FlagOnMap = 1, FlagInWorld = 2, FlagSpawned = 4, FlagVisible = 8, FlagDisposable = 0x10, FlagGpsing = 0x80;

        public int Type;
        public int Flags;
        public Vector3 Position;

        /// <summary>
        /// GManager::UpdateIconVisibility (0x5FDAE0) / GetIsIconVisible (0x5DDB30): per type the availability byte — races
        /// (1-7) and speed traps (13) AllowEngageEvents (+0x2CE), the safehouse (8) AllowEngageSafehouse (+0x2CF), car lot and
        /// custom shop (9, 10) AllowMenuGates (+0x2D0), hiding spots (11) pursuit cooldown (+0x2C9), pursuit breakers (12) an
        /// active pursuit (+0x2C8), speed traps in a race (14) only in a speedtrap race, area unlocks (15) always; everything
        /// hidden during an NIS (0x9885C8) or by the global switch (0x91E00A); the icon also needs flag 1 (on the map).
        /// </summary>
        public static bool IsVisible(int type, int flags)
        {
            if ((flags & FlagOnMap) == 0) return false;
            if (NisPlayer.Active || HideAll) return false;
            switch (type)
            {
                case 1: case 2: case 3: case 4: case 5: case 6: case 7: case SpeedTrap: return Availability.AllowEngageEvents;
                case Safehouse: return Availability.AllowEngageSafehouse;
                case CarLot: case CustomShop: return Availability.AllowMenuGates;
                case HidingSpot: return Availability.PursuitInCooldown;
                case PursuitBreaker: return Availability.ActivePursuit;
                case SpeedTrapInRace: return RaceManager.Active != null && RaceManager.Active.SpeedTrapRace;
                case AreaUnlock: return true;
            }
            return false;
        }

        /// <summary>The global hide-all switch (0x91E00A) [ours: a debug hook].</summary>
        public static bool HideAll;

        internal GIcon SetFlags(int f) { Flags |= f; return this; }
    }

    /// <summary>
    /// GManager's own icon pool (+0x2BC count, +0x2C4 array of 200 = 0x320 / 4, AllocIcon 0x5E9EC0): the icons no GTrigger owns.
    /// InitializeVaults (0x5F8080) fills it once per GManager: RefreshZoneIcons (0x5EF640; also NotifyGameZonesChanged
    /// 0x5F19C0) frees the disposable hiding-spot icons (FreeDisposableIcons 0x5EEF00) and allocates one type-11 icon per
    /// TrackPathZone of type 8 (TrackPathManager::FindZone loop) at (Position.x, Position.y, Elevation); then the
    /// disposable type-12 icons go and sub_727240 walks the TrackPositionMarker list (0x9AA494) with sub_5EBE70: every marker
    /// named bStringHash("IconMarker") gets a type-12 (pursuit breaker) icon at its position. Both are allocated disposable
    /// (AllocIcon's bool → flag 0x10) and get flags |= 3 (map + world). EBreakerStopCops' handler (sub_620DF0) clears the
    /// on-map flag of the breaker icons within 20 m of the event (HidePursuitBreakerIcon 0x5E4850); RestorePursuitBreakerIcons
    /// (0x5E48F0) sets it again for the icons of a section whose props were restored (sub_6828D0, a streamed section's
    /// smackables back) or for all (-1, World_RestoreProps 0x74D418). The world map (AddIcons) and the minimap read the pool
    /// through GatherVisibleIcons together with the triggers' icons.
    /// </summary>
    public static class GameIcons
    {
        public const int MaxIcons = 200;
        static readonly List<GIcon> _icons = new List<GIcon>();
        static bool _initialised;

        /// <summary>The pool (InitializeVaults on first use).</summary>
        public static IReadOnlyList<GIcon> All { get { Ensure(); return _icons; } }

        public static void Ensure()
        {
            if (_initialised) return;
            _initialised = true;
            try
            {
                RefreshZoneIcons(TrackPathZones.Load(GameData.Root));
                FreeDisposableIcons(GIcon.PursuitBreaker);
                uint iconMarker = EaHash.Hash("IconMarker");
                foreach (var m in TrackMarkers.Load(GameData.Root).All)
                    if (m.NameHash == iconMarker) AllocIcon(GIcon.PursuitBreaker, Coords.ToUnity(m.Position), true)?.SetFlags(GIcon.FlagOnMap | GIcon.FlagInWorld);
            }
            catch (Exception e) { Debug.LogWarning("NFSMW: GManager icons: " + e.Message); }
        }

        /// <summary>AllocIcon (0x5E9EC0): null when the pool holds 200.</summary>
        public static GIcon AllocIcon(int type, Vector3 position, bool disposable)
        {
            if (_icons.Count >= MaxIcons) return null;
            var ic = new GIcon { Type = type, Position = position };
            if (disposable) ic.Flags |= GIcon.FlagDisposable;
            _icons.Add(ic);
            return ic;
        }

        /// <summary>FreeDisposableIcons (0x5EEF00): every disposable icon of that type leaves the pool.</summary>
        public static void FreeDisposableIcons(int type) => _icons.RemoveAll(i => i.Type == type && (i.Flags & GIcon.FlagDisposable) != 0);

        /// <summary>RefreshZoneIcons (0x5EF640): the hiding-spot icons from the type-8 track path zones.</summary>
        public static void RefreshZoneIcons(List<TrackPathZone> zones)
        {
            FreeDisposableIcons(GIcon.HidingSpot);
            if (zones == null) return;
            foreach (var z in zones)
                if (z.Type == TrackPathZones.Hidden) AllocIcon(GIcon.HidingSpot, Coords.ToUnity(z.PosX, z.PosY, z.Elevation), true)?.SetFlags(GIcon.FlagOnMap | GIcon.FlagInWorld);
        }

        /// <summary>HidePursuitBreakerIcon (0x5E4850, radius 20 from sub_620DF0): breaker icons within the radius (3D) lose the on-map flag.</summary>
        public static void HidePursuitBreakerIcon(Vector3 position, float radius = 20f)
        {
            foreach (var ic in All)
                if (ic.Type == GIcon.PursuitBreaker && (ic.Position - position).sqrMagnitude <= radius * radius) ic.Flags &= ~GIcon.FlagOnMap;
        }

        /// <summary>RestorePursuitBreakerIcons(-1) (0x5E48F0 from World_RestoreProps): every breaker icon back on the map
        /// [the per-section call of sub_6828D0 is folded into this: the port's sections do not tag icons].</summary>
        public static void RestorePursuitBreakerIcons()
        {
            foreach (var ic in All) if (ic.Type == GIcon.PursuitBreaker) ic.Flags |= GIcon.FlagOnMap;
        }
    }

    /// <summary>
    /// GManager::UpdateTriggerAvailability (0x5EBA50, every GManager::Update) and UpdatePursuit (0x6013F0): the icon /
    /// trigger availability bytes at GManager +0x2C8..+0x2D0. AllowEngageEvents = AllowMenuGates = !racing &amp;&amp; !pursuit;
    /// AllowEngageSafehouse = !racing &amp;&amp; (!pursuit || the pursuit is in cooldown); +0x2C9 (hiding spots) = roaming (or
    /// a Challenge Series race) and the pursuit in cooldown; +0x2C8 (pursuit breakers) = roaming / CS race / epic pursuit
    /// and an active pursuit not in cooldown.
    /// </summary>
    public static class Availability
    {
        static bool Racing => RaceManager.Active != null;
        static bool Pursuit => Systems.Police != null && Systems.Police.InPursuit;
        static bool Cooldown => Pursuit && Systems.Police.Cooldown > 0;
        public static bool AllowEngageEvents => !Racing && !Pursuit;
        public static bool AllowMenuGates => !Racing && !Pursuit;
        public static bool AllowEngageSafehouse => !Racing && (!Pursuit || Cooldown);
        public static bool PursuitInCooldown => (!Racing || GameSession.Mode == GameMode.ChallengeSeries) && Cooldown;
        public static bool ActivePursuit => (!Racing || GameSession.Mode == GameMode.ChallengeSeries || (RaceManager.Active?.Event?.EpicPursuit ?? false)) && Pursuit && !Cooldown;
    }

    /// <summary>
    /// GTrigger (Gameplay/GTrigger.cpp, ctor 0x601790, 0x5DB470..0x601F77): a GRuntimeInstance (type 5) over a gameplay
    /// trigger record that owns a WTrigger (at +0x28), the list of simables inside (+0x7C), an activation reference count
    /// (+0xC8), its enabled state (+0xC4), particle effects (flares) and a GIcon (+0xC0). The ctor derives the WTrigger from
    /// Position / Rotation / Dimensions / Radius / Width (WTrigger.FromGameplayRecord) and the trigger kind from the record's
    /// template: engagetrigger (icon by race type, 7 for a boss race; +0x18 |= 0x200), zone_carlot (type bit 0x20, icon 9),
    /// zone_customshop (0x40, 10), zone_safehouse (0x80, 8), speedtrap (0x1; icon 13, or 14 for a race trap), finishline
    /// (0x4), time_bonus_checkpoint (0x8), moneybag (0x10), areaunlocktrigger (icon 15). An icon of a record with BinIndex &gt; 0
    /// is shown on the map and in the world (flags 1|2) when the career bin is at or below that BinIndex.
    /// </summary>
    public sealed class GTrigger
    {
        public const byte TypeSpeedTrap = 0x1, TypeFinishLine = 0x4, TypeTimeBonusCheckpoint = 0x8, TypeMoneyBag = 0x10, TypeCarLot = 0x20, TypeCustomShop = 0x40, TypeSafehouse = 0x80;

        public readonly GInstance G;
        public readonly WTrigger W;
        public readonly bool FireOnExit;
        public byte TypeBits;
        public bool IsEngage;
        public GIcon Icon;
        /// <summary>The simables inside (MarkAsInside 0x5F5B50 / MarkAsOutside 0x5ED690).</summary>
        public readonly List<object> Inside = new List<object>();
        public int ActivationRefs { get; private set; }
        public bool Enabled { get; private set; }
        public uint Key => G.Key;
        public string Name => G.Name;

        public GTrigger(GInstance g, GameplayScripts data)
        {
            G = g;
            W = WTrigger.FromGameplayRecord(g.Record, v => Coords.ToUnity(v).ToVec()) ?? WTrigger.Cylinder(Vector3.zero.ToVec(), new Vec3(0, 0, 1), 1f, 4f, WTrigger.GameplayFlags);
            W.Owner = this;
            W.Name = g.Name;
            FireOnExit = g.Record.Has("FireOnExit") && g.Record.Bool("FireOnExit");
            Classify(data);
        }

        void Classify(GameplayScripts data)
        {
            var rec = G.Record;
            bool Is(string template) => data != null && data.DerivesFrom(rec, AttribDatabase.Hash(template));
            int icon = 0;
            if (Is("engagetrigger"))
            {
                IsEngage = true;
                var ev = GameSession.Career != null && rec.Has("TargetActivity") ? GameSession.Career.EventFor(rec.Keys("TargetActivity") is uint[] k && k.Length > 0 ? k[0] : 0) : null;
                if (ev != null) icon = ev.BossRace ? GIcon.Rival : IconOfRaceType(ev.GRaceType);
            }
            else if (Is("zone_carlot")) { TypeBits |= TypeCarLot; icon = GIcon.CarLot; }
            else if (Is("zone_customshop")) { TypeBits |= TypeCustomShop; icon = GIcon.CustomShop; }
            else if (Is("zone_safehouse")) { TypeBits |= TypeSafehouse; icon = GIcon.Safehouse; }
            else if (Is("speedtrap")) { TypeBits |= TypeSpeedTrap; icon = rec.Has("OpenWorldSpeedTrap") && !rec.Bool("OpenWorldSpeedTrap") ? GIcon.SpeedTrapInRace : GIcon.SpeedTrap; }
            else if (Is("finishline")) TypeBits |= TypeFinishLine;
            else if (Is("time_bonus_checkpoint")) TypeBits |= TypeTimeBonusCheckpoint;
            else if (Is("moneybag")) TypeBits |= TypeMoneyBag;
            else if (Is("areaunlocktrigger")) icon = GIcon.AreaUnlock;
            if (icon <= 0) return;
            Icon = new GIcon { Type = icon, Position = Coords.ToUnity(rec.Vector3("Position")) };
            int bin = rec.Has("BinIndex") ? rec.Int("BinIndex") : 0;
            int current = GameSession.Profile?.Bin ?? 15;
            if (bin > 0 && current <= bin) Icon.Flags |= GIcon.FlagOnMap | GIcon.FlagInWorld;
        }

        /// <summary>The ctor's GetRaceType → GIcon switch (0..5 → 1..6): p2p 0 → sprint 1, circuit 1 → 2, drag 2 → 3, knockout 3 → 4, tollbooth 4 → 5, speedtrap 5 → 6; others get no icon.</summary>
        public static int IconOfRaceType(int gRaceType) => gRaceType >= 0 && gRaceType <= 5 ? gRaceType + 1 : 0;

        /// <summary>AddActivationReference (0x5E8540): one more running activity references it; the first enables it.</summary>
        public void AddActivationReference(WTriggerManager mgr)
        {
            ActivationRefs++;
            if (!Enabled) Enable(mgr, true);
        }

        /// <summary>RemoveActivationReference (0x5E85A0): the last reference gone disables it.</summary>
        public void RemoveActivationReference(WTriggerManager mgr)
        {
            if (ActivationRefs > 0) ActivationRefs--;
            if (ActivationRefs == 0 && Enabled) Enable(mgr, false);
        }

        /// <summary>GTrigger::Enable (0x5E2C90): WCollisionAssets::AddTrigger / RemoveTrigger, flag 0x100, the particle effects (not ported).</summary>
        public void Enable(WTriggerManager mgr, bool on)
        {
            if (on) mgr.AddTrigger(W); else mgr.RemoveTrigger(W);
            Enabled = on;
        }

        /// <summary>Game.ResetTrigger (0x604D10): everyone inside is forgotten [?: the exe's exact reset].</summary>
        public void Reset(WTriggerManager mgr)
        {
            foreach (var s in Inside) mgr.ForgetSimable(s);
            Inside.Clear();
        }
    }

    /// <summary>
    /// The trigger side of GManager for the running stategraphs: the GTriggers of the activities' referenced triggers
    /// (ActivateReferencedTriggers 0x5E87F0 / the Reset's deactivation), the WTriggerManager scan of the simables once per
    /// frame (SimSystem::OnTask → WTriggerManager::Update), and GTrigger::NotifySimableTrigger (0x5F6A30): stimulus 1
    /// (inside) → MarkAsInside + MTriggerEnter the first time, MTriggerInside every tick; stimulus 2 (left) → MarkAsOutside
    /// and MTriggerExit only with the record's FireOnExit. The simables tested are the players' and the racers' cars
    /// (traffic and cops are simple rigid bodies in the exe and would fire too; no gameplay handler reacts to them [?]).
    /// </summary>
    public sealed class GameTriggers
    {
        readonly GameplayScripts _data;
        readonly Dictionary<uint, GTrigger> _trigs = new Dictionary<uint, GTrigger>();
        readonly List<WTriggerBody> _bodies = new List<WTriggerBody>();
        public readonly WTriggerManager Manager = new WTriggerManager();
        /// <summary>(trigger, simable): MTriggerEnter / MTriggerInside / MTriggerExit (Hermes 0x5DB090 / 0x5DB250 / 0x5DB170).</summary>
        public event Action<GTrigger, object> Enter, Inside, Exit;

        public GameTriggers(GameplayScripts data)
        {
            _data = data;
            Manager.Fire = OnFire;
        }

        public IEnumerable<GTrigger> All => _trigs.Values;

        /// <summary>GatherVisibleIcons' source: the triggers' icons, then GManager's own pool (hiding spots, pursuit breakers).</summary>
        public IEnumerable<GIcon> Icons
        {
            get
            {
                foreach (var t in _trigs.Values) if (t.Icon != null) yield return t.Icon;
                foreach (var ic in GameIcons.All) yield return ic;
            }
        }
        public int ActiveCount => Manager.Triggers.Count;

        public GTrigger Get(GInstance g)
        {
            if (g == null) return null;
            if (_trigs.TryGetValue(g.Key, out var t)) return t;
            if (!g.Record.Has("Position")) return null;
            t = new GTrigger(g, _data);
            _trigs[g.Key] = t;
            return t;
        }

        public GTrigger Find(uint key) => _trigs.TryGetValue(key, out var t) ? t : null;

        /// <summary>The set of triggers that running activities reference: references added for new ones, removed for the rest.</summary>
        public void SetActive(IEnumerable<GInstance> wanted)
        {
            var want = new HashSet<uint>();
            foreach (var g in wanted)
            {
                var t = Get(g);
                if (t == null) continue;
                want.Add(t.Key);
                if (t.ActivationRefs == 0) t.AddActivationReference(Manager);
            }
            foreach (var t in _trigs.Values)
                if (!want.Contains(t.Key) && t.ActivationRefs > 0) { while (t.ActivationRefs > 0) t.RemoveActivationReference(Manager); t.Inside.Clear(); }
        }

        /// <summary>One sim frame: the cars as rigid bodies against the active triggers.</summary>
        public void Update(float dt, IEnumerable<PlayerCar> cars)
        {
            _bodies.Clear();
            foreach (var car in cars)
            {
                if (!car) continue;
                _bodies.Add(BodyOf(car));
            }
            Manager.Update(dt, _bodies);
        }

        /// <summary>IRigidBody as the trigger manager sees a car: position, velocity, bounding radius, half extents, axes, trigger flags.</summary>
        public static WTriggerBody BodyOf(PlayerCar car)
        {
            var t = car.transform;
            var rb = car.GetComponent<Rigidbody>();
            var col = car.GetComponent<BoxCollider>();
            Vector3 half = col ? Vector3.Scale(col.size, t.lossyScale) * 0.5f : new Vector3(1f, 0.7f, 2.3f);
            Vector3 centre = col ? t.TransformPoint(col.center) : t.position;
            bool human = !car.AiControlled && !car.Traffic;
            return new WTriggerBody
            {
                Simable = car,
                Position = centre.ToVec(),
                Velocity = (rb ? rb.linearVelocity : Vector3.zero).ToVec(),
                HalfExtents = half.ToVec(),
                Right = t.right.ToVec(), Up = t.up.ToVec(), Forward = t.forward.ToVec(),
                Radius = half.magnitude,
                Simple = car.Traffic,
                OwnedByPlayer = human,
                TriggerFlags = car.TriggerFlags,
            };
        }

        /// <summary>WTrigger::FireEvents → EGTriggerInternal → GTrigger::NotifySimableTrigger(simable, stimulus).</summary>
        void OnFire(WTrigger w, object simable, int stimulus)
        {
            if (!(w.Owner is GTrigger t) || simable == null) return;
            if (stimulus == 2)
            {
                t.Inside.Remove(simable);
                if (t.FireOnExit) Exit?.Invoke(t, simable);
            }
            else if (stimulus == 1)
            {
                if (!t.Inside.Contains(simable))
                {
                    t.Inside.Add(simable);
                    Enter?.Invoke(t, simable);
                }
                Inside?.Invoke(t, simable);
            }
        }

        /// <summary>A car that is gone (destroyed, despawned) leaves every trigger silently [ours].</summary>
        public void Forget(object simable)
        {
            Manager.ForgetSimable(simable);
            foreach (var t in _trigs.Values) t.Inside.Remove(simable);
        }

        public void Clear()
        {
            foreach (var t in _trigs.Values) if (t.Enabled) t.Enable(Manager, false);
            _trigs.Clear();
            Manager.Restart();
        }
    }

    public static class VecExt
    {
        public static Vec3 ToVec(this Vector3 v) => new Vec3(v.x, v.y, v.z);
        public static Vector3 ToVector3(this Vec3 v) => new Vector3(v.X, v.Y, v.Z);
    }
}
