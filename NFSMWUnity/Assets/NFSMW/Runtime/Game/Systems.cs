using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.UI;
using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>World systems that run while driving: traffic and the police.</summary>
    public static class Systems
    {
        static GameObject _root;
        public static TrafficSystem Traffic { get; private set; }
        public static PursuitSystem Police { get; private set; }
        /// <summary>Hud_GenericMessage: a message in the HUD's centre ("^" = new line).</summary>
        public static System.Action<string> HudMessage;

        public static void Ensure()
        {
            if (_root) return;
            _root = new GameObject("World Systems");
            Traffic = _root.AddComponent<TrafficSystem>();
            Police = _root.AddComponent<PursuitSystem>();
            Police.Enabled = GameSession.Mode == GameMode.Career || GameSession.Mode == GameMode.FreeRoam;
            // AICopManager::AICopManager: mLockoutTimer = kCopLockoutTime, no cops for the first 60 s of the world
            Police.ConstructionLockout();
            if (GameSession.Mode == GameMode.Career) _root.AddComponent<EventMarkers>();
        }

        public static void Shutdown()
        {
            if (_root) Object.Destroy(_root);
            _root = null;
            Traffic = null;
            Police = null;
        }

        /// <summary>A path that wanders the road graph from a node (no immediate U-turns), shifted onto the right lane.</summary>
        public static List<Vector3> Wander(int start, int nodes, float laneOffset, int previous = -1)
        {
            var net = Roads.Net;
            var ids = new List<int> { start };
            int prev = previous, cur = start;
            for (int i = 0; i < nodes; i++)
            {
                var links = net.Links[cur].Where(n => n != prev).ToList();
                if (links.Count == 0) links = net.Links[cur];
                if (links.Count == 0) break;
                int next = links[Random.Range(0, links.Count)];
                prev = cur;
                cur = next;
                ids.Add(cur);
            }
            var pts = new List<Vector3>();
            for (int i = 0; i < ids.Count; i++)
            {
                var p = Roads.Node(ids[i]);
                var dir = i + 1 < ids.Count ? Roads.Node(ids[i + 1]) - p : p - Roads.Node(ids[i - 1]);
                dir.y = 0;
                var right = Vector3.Cross(Vector3.up, dir.normalized);
                pts.Add(p + right * LaneOffset(p, right, laneOffset));
            }
            return pts;
        }

        /// <summary>
        /// The graph runs down the middle of the road; the right lane is half way to the kerb. Where the city is
        /// loaded the kerb is found by stepping right until the ground rises (or ends); elsewhere the default is used.
        /// </summary>
        static float LaneOffset(Vector3 centre, Vector3 right, float fallback)
        {
            const int Mask = ~0;
            if (!Physics.Raycast(centre + Vector3.up * 4f, Vector3.down, out var mid, 12f, Mask, QueryTriggerInteraction.Ignore)) return fallback;
            for (float k = 1f; k <= 8f; k += 0.5f)
            {
                bool hit = Physics.Raycast(centre + right * k + Vector3.up * 4f, Vector3.down, out var h, 12f, Mask, QueryTriggerInteraction.Ignore);
                if (hit && h.rigidbody != null && !h.rigidbody.isKinematic) continue;   // a car, not the kerb
                if (!hit || h.point.y > mid.point.y + 0.07f) return Mathf.Clamp(k * 0.5f, 1.1f, fallback + 1f);
            }
            return fallback;
        }

        public static int LastNode(List<Vector3> path) => path.Count == 0 ? -1 : Roads.Nearest(path[path.Count - 1], 50f);
    }

    // PursuitSystem and CollisionMeter: Police/PursuitSystem.cs
}

namespace NfsMw.Game
{
    /// <summary>
    /// Career free roam: a light column over each available race start (the minimap shows the same events); stop
    /// inside one and press Enter to race. Follows the engage stategraphs (specs/gameplay_core.md §6): `bin_race_engage`
    /// offers a race only while it is unlocked and not completed in career; events and menu gates need
    /// GManager::UpdateTriggerAvailability (0x5EBA50: not racing, not in a pursuit; the safehouse also during cooldown).
    /// Also runs `speedtrap_group_monitor` for the open-world speed traps of the bins reached.
    /// </summary>
    public sealed class EventMarkers : MonoBehaviour
    {
        readonly Dictionary<RaceEvent, GameObject> _markers = new Dictionary<RaceEvent, GameObject>();
        readonly Dictionary<string, GameObject> _trapMarkers = new Dictionary<string, GameObject>();
        readonly HashSet<string> _inTrap = new HashSet<string>();
        static readonly Color TrapColor = new Color(0.25f, 0.65f, 1f, 0.45f);
        Material _trap;
        RaceEvent _near;
        float _refresh;
        Material _normal, _boss;
        MaterialPropertyBlock _block;
        static readonly Color NormalColor = new Color(1f, 0.72f, 0.12f, 0.55f), BossColor = new Color(1f, 0.18f, 0.12f, 0.6f);

        MenuZone _zone;
        float _engageAt;

        void Update()
        {
            var boot = GameBootstrap.Instance;
            bool racing = RaceManager.Active != null;
            bool pursuit = Systems.Police != null && Systems.Police.InPursuit;
            // GManager::UpdateTriggerAvailability (0x5EBA50) → the engage icons' visibility (UpdateIconVisibility: types 1-7 need AllowEngageEvents, none during an NIS)
            bool active = boot && boot.Player && Gameplay.Availability.AllowEngageEvents && !NisPlayer.Active && GameSession.Mode == GameMode.Career;
            if (Time.time > _refresh) { _refresh = Time.time + 2f; Sync(); }
            var cam = Camera.main;
            foreach (var kv in _markers)
            {
                var m = kv.Value;
                if (!m) continue;
                m.SetActive(active);
                if (!active || !cam) continue;
                // the column fades out as the camera comes close, and is gone inside it
                var d = m.transform.position - cam.transform.position;
                d.y = 0;
                _block ??= new MaterialPropertyBlock();
                var c = kv.Key.BossRace ? BossColor : NormalColor;
                c.a *= Mathf.InverseLerp(9f, 35f, d.magnitude);
                _block.SetColor("_Color", c);
                m.GetComponent<Renderer>().SetPropertyBlock(_block);
            }
            foreach (var kv in _trapMarkers)
                if (kv.Value) kv.Value.SetActive(boot && boot.Player && !racing && !NisPlayer.Active);
            // (Drive: speedtrap_group_monitor's TriggerEnter does it: NotifySpeedTrapTriggered + AwardPlayerBounty)
            if (boot && boot.Player && !racing && !NisPlayer.Active && GameSession.Mode == GameMode.Career && !GameSession.ScriptsDrive) SpeedTraps(boot.Player);
            _near = null;
            _zone = null;
            bool menus = FrontEnd.Instance != null && FrontEnd.Instance.IsOpen;
            if (!boot || !boot.Player || racing || NisPlayer.Active || menus || GameSession.Mode != GameMode.Career) return;
            var p = boot.Player.transform.position;
            // race engage triggers: radius 25 at the start grid (race_bin_NN/engage)
            if (active)
                foreach (var kv in _markers)
                    if (kv.Value && Flat(kv.Value.transform.position - p) < 25f) _near = kv.Key;
            // menu gates: IsActiveMenuGate (0x604E40) = the gate's icon visible — car lot / custom shop need AllowMenuGates, the safehouse AllowEngageSafehouse (also during cooldown)
            int bin = GameSession.Profile?.Bin ?? 15;
            if (_near == null && GameSession.Profile?.Stage == Story.Free)
                foreach (var z in GameSession.Career.Zones)
                {
                    if (bin > z.BinIndex) continue;
                    if (!(z.Type == "safehouse" ? Gameplay.Availability.AllowEngageSafehouse : Gameplay.Availability.AllowMenuGates)) continue;
                    if (Flat(Coords.ToUnity(z.Position) - p) < z.Radius + 2f) { _zone = z; break; }
                }
            // HUDACTION_ENGAGE_EVENT from the hud action queue (the FE controls page's key, Enter by default)
            if (!boot.ActionPressed("HUDACTION_ENGAGE_EVENT") || Time.time < _engageAt) return;
            _engageAt = Time.time + 0.5f;
            // FEPkg_Hud HUDACTION_ENGAGE_EVENT: a pursuit in PS_COOL_DOWN with the player inside a "safehouse" zone ends entering it
            // (EndPursuitEnteringSafehouse; the safehouse opens after the pursuit result, MEnterSafeHouse)
            var cops = Systems.Police;
            var zone = _zone;
            if (zone != null && zone.Type == "safehouse" && cops != null && cops.InPursuit && cops.InCooldown) { cops.EndPursuitEnteringSafehouse(() => EnterZone(zone)); return; }
            if (_near != null) Engage(_near);
            else if (_zone != null) EnterZone(_zone);
        }

        static float Flat(Vector3 v) { v.y = 0; return v.magnitude; }

        /// <summary>
        /// speedtrap_group_monitor TriggerEnter: entering an active trap's radius faster than its ThreshholdSpeed (km/h)
        /// completes it (NotifySpeedTrapTriggered) and pays its Bounty (AwardPlayerBounty); slower shows HUD_TOO_SLOW.
        /// </summary>
        void SpeedTraps(PlayerCar car)
        {
            var p = car.transform.position;
            foreach (var t in GameSession.ActiveSpeedTraps())
            {
                var c = Coords.ToUnity(t.Point.Position);
                float r = t.Point.Radius > 0 ? t.Point.Radius : 20f;
                bool inside = Flat(c - p) < r && Mathf.Abs(c.y - p.y) < 25f;
                bool was = _inTrap.Contains(t.Name);
                if (inside && !was)
                {
                    float kmh = car.SpeedMps * 3.6f;
                    if (kmh > t.Threshold)
                    {
                        GameSession.CompleteSpeedTrap(t, kmh);
                        Systems.HudMessage?.Invoke($"{FrontEndData.Text("SPEEDTRAP")}^{Settings.Speed(car.SpeedMps)} {Settings.SpeedUnit}^+{t.Bounty:N0}");
                    }
                    else Systems.HudMessage?.Invoke(FrontEndData.Text("HUD_TOO_SLOW"));
                }
                if (inside) _inTrap.Add(t.Name); else _inTrap.Remove(t.Name);
            }
        }

        /// <summary>ENGAGE on a race trigger: EngageEventDialog (Accept / Decline), or the rival challenge for boss races.</summary>
        static void Engage(RaceEvent ev)
        {
            var fe = FrontEnd.Create();
            if (ev.BossRace) { fe.Push(new RivalScreen()); return; }
            fe.Push(new EngageEventScreen(ev));
        }

        /// <summary>Game.DoZoneMenuAction: remember the respawn (and the last safehouse), open the zone's screen.</summary>
        public static void EnterZone(MenuZone z)
        {
            var p = GameSession.Profile;
            Story.ZoneRespawn = z.Respawn;
            if (z.Type == "safehouse") { Story.SetSpawn(z.Respawn); p.Save(); }
            var fe = FrontEnd.Create();
            switch (z.Type)
            {
                case "safehouse": fe.Push(new CareerMenuScreen()); break;
                case "carlot": fe.Push(new CarLotScreen(false)); break;
                default: fe.Push(new CustomizeScreen()); break;
            }
            void Out()
            {
                fe.Closed -= Out;
                var r = Story.ZoneRespawn;
                Story.ZoneRespawn = null;
                if (r != null && GameBootstrap.Instance && GameBootstrap.Instance.Player) GameBootstrap.Instance.PlacePlayer(r.Position, r.Rotation);
            }
            fe.Closed += Out;
        }

        /// <summary>
        /// GManager::RefreshEngageTriggerIcons (0x602840): the current bin's engage triggers whose race is unlocked in career
        /// and not completed (GRaceDatabase flags 8 and 2). The marker stands at the engage trigger, not at the start
        /// grid: accepting warps to racestart. Races without an engage trigger (the later boss races of a chain) get none.
        /// </summary>
        void Sync()
        {
            GameSession.EnsureRaceUnlocks();
            var want = new HashSet<RaceEvent>();
            var bin = GameSession.CurrentBin;
            if (bin != null && bin.EngageTriggers.Count > 0)
            {
                foreach (var (_, target, _) in bin.EngageTriggers)
                    if (GameSession.Career.EventFor(target, bin.Index) is RaceEvent ev && ev.Start != null &&
                        Gameplay.RaceDatabase.IsAvailable(ev, RaceIndex.ContextCareer)) want.Add(ev);
            }
            else
                foreach (var ev in GameSession.AvailableEvents())
                    if (ev.Start != null && !GameSession.Profile.HasWon(ev.Name)) want.Add(ev);
            foreach (var ev in _markers.Keys.Where(e => !want.Contains(e)).ToList())
            {
                if (_markers[ev]) Destroy(_markers[ev]);
                _markers.Remove(ev);
            }
            foreach (var ev in want)
                if (!_markers.ContainsKey(ev)) _markers[ev] = Build(ev);
            // speed trap markers (GIcon kType_SpeedTrap 13) for the traps still open
            var traps = new HashSet<string>();
            foreach (var t in GameSession.ActiveSpeedTraps())
            {
                traps.Add(t.Name);
                if (_trapMarkers.ContainsKey(t.Name)) continue;
                var m = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                Destroy(m.GetComponent<Collider>());
                m.name = "Speedtrap " + t.Name;
                m.transform.SetParent(transform, false);
                m.transform.position = Coords.ToUnity(t.Point.Position) + Vector3.up * 10f;
                m.transform.localScale = new Vector3(5f, 10f, 5f);
                if (!_trap) { _trap = new Material(Shader.Find("NFSMW/Glow")); _trap.SetColor("_Color", TrapColor); }
                m.GetComponent<Renderer>().sharedMaterial = _trap;
                _trapMarkers[t.Name] = m;
            }
            foreach (var name in _trapMarkers.Keys.Where(n => !traps.Contains(n)).ToList())
            {
                if (_trapMarkers[name]) Destroy(_trapMarkers[name]);
                _trapMarkers.Remove(name);
            }
        }

        GameObject Build(RaceEvent ev)
        {
            var m = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(m.GetComponent<Collider>());
            m.name = "Event " + ev.EventId;
            m.transform.SetParent(transform, false);
            m.transform.position = Coords.ToUnity(GameSession.EngagePoint(ev).Position) + Vector3.up * 20f;
            m.transform.localScale = new Vector3(9f, 20f, 9f);
            if (!_normal)
            {
                var shader = Shader.Find("NFSMW/Glow");
                _normal = new Material(shader);
                _normal.SetColor("_Color", NormalColor);
                _boss = new Material(shader);
                _boss.SetColor("_Color", BossColor);
            }
            m.GetComponent<Renderer>().sharedMaterial = ev.BossRace ? _boss : _normal;
            return m;
        }

        void OnGUI()
        {
            string text = _near != null ? $"{_near.TypeName.ToUpperInvariant()} {_near.EventId}" : _zone != null ? ZoneLabel(_zone.Type) : null;
            if (text == null) return;
            // the HUD engage prompt (Hud_MenuZoneTrigger): the zone or event name over the "Enter" key
            float s = Screen.height / 720f;
            var style = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(24 * s), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(1f, 0.8f, 0.2f) } };
            var small = new GUIStyle(style) { fontSize = Mathf.RoundToInt(16 * s), normal = { textColor = Color.white } };
            GUI.Label(new Rect(0, Screen.height * 0.2f, Screen.width, 34 * s), text, style);
            GUI.Label(new Rect(0, Screen.height * 0.2f + 32 * s, Screen.width, 26 * s), _near != null ? "Press Enter to engage event" : "Press Enter to enter", small);
        }

        static string ZoneLabel(string type) => type == "safehouse" ? "SAFE HOUSE" : type == "carlot" ? "CAR LOT" : "CUSTOMIZATION SHOP";
    }
}
