using System.Collections.Generic;
using NfsMw.Core;
using NfsMw.Core.Lua;
using NfsMw.UI;
using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>
    /// Scripted characters (Game.SpawnCharacter 0x604D40 → GCharacter::Spawn(position, direction, target marker, speed)
    /// 0x601320; UnspawnCharacter 0x604DA0; SetTrafficSpeed 0x605090): the races' random traffic spawns
    /// (RandomSpawnTriggers → TrafficCharacter) — a car at the trigger, driving to its TargetMarker at InitialSpeed
    /// [ours: an AIDriver on the road route]. InitialSpeed is in mph: GCharacter::Spawn stores speed × 0.44703 (m/s) in
    /// mSpawnSpeed (+0x5C).
    /// </summary>
    public static class ScriptedCharacters
    {
        static readonly Dictionary<uint, PlayerCar> Live = new Dictionary<uint, PlayerCar>();

        public static void Spawn(GInstance character, RacePoint at, RacePoint target, float speed)
        {
            if (character == null || at == null) return;
            Unspawn(character);
            var rec = character.Record;
            string car = null;
            var career = GameSession.Career;
            if (career != null && rec.Has("PresetRide")) car = career.CarForPreset(rec.Text("PresetRide"));
            if (car == null && rec.Has("CarType")) car = rec.Text("CarType")?.ToLowerInvariant();
            if (string.IsNullOrEmpty(car) || !System.IO.Directory.Exists(System.IO.Path.Combine(GameData.Root, "CARS", car.ToUpperInvariant())))
            {
                Debug.Log($"NFSMW lua: Drive: SpawnCharacter {character.Name}: no car ({car ?? "none"})");
                return;
            }
            float a = at.Rotation * Mathf.Deg2Rad;
            var fwd = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
            var pos = Coords.ToUnity(at.Position);
            PlayerCar pc;
            try { pc = CarSpawner.Spawn(car, Color.HSVToRGB(Random.value, 0.4f, 0.7f), new OwnedCar(), pos, Quaternion.LookRotation(fwd), "Character " + character.LocalName); }
            catch (System.Exception e) { Debug.LogWarning("NFSMW lua: SpawnCharacter: " + e.Message); return; }
            var ai = pc.gameObject.AddComponent<AIDriver>();
            ai.Car = pc;
            var to = target != null ? Coords.ToUnity(target.Position) : pos + fwd * 300f;
            var path = Roads.Between(pos, to);
            ai.SetPath(path != null && path.Count >= 2 ? path : new List<Vector3> { pos, to });
            float v = Mathf.Max(5f, speed * 0.44703f);   // 0x601320: mph → m/s
            ai.MaxSpeed = v;
            var body = pc.GetComponent<Rigidbody>();
            if (body) body.linearVelocity = fwd * v;
            Live[character.Key] = pc;
            Spawned[character.Key] = Time.time;
        }

        public static void Unspawn(GInstance character)
        {
            if (character == null || !Live.TryGetValue(character.Key, out var pc)) return;
            Live.Remove(character.Key);
            if (pc) Object.Destroy(pc.gameObject);
        }

        /// <summary>Game.SetTrafficSpeed (0x605090): MSetTrafficSpeed(speed, second float, 1) sent to the character's spawned
        /// vehicle [?: the units and the second float; ours mph like Spawn].</summary>
        public static void SetSpeed(GInstance character, float speed, float other)
        {
            if (character == null || !Live.TryGetValue(character.Key, out var pc) || !pc) return;
            var ai = pc.GetComponent<AIDriver>();
            if (ai) ai.MaxSpeed = Mathf.Max(0f, speed * 0.44703f);
        }

        /// <summary>
        /// Game.SendCharacterStimulus (0x60A6E0): the character's spawned vehicle → its IVehicle → the IVehicleAI's stimulus
        /// receiver (vtable +0x74) gets SendStimulus(stringhash32(name), 0, Sim::GetTime(), 2) [?: the receiver's slot]. No
        /// gameplay.bin handler calls it; the AIDriver only notes the stimulus (hook: AI stimuli) [ours].
        /// </summary>
        public static void SendStimulus(GInstance character, string stimulus)
        {
            if (character == null || string.IsNullOrEmpty(stimulus) || !Live.TryGetValue(character.Key, out var pc) || !pc) return;
            Debug.Log($"NFSMW lua: Drive: SendCharacterStimulus {character.LocalName} '{stimulus}' (0x{AttribDatabase.Hash(stimulus):X8})");
            StimulusSent?.Invoke(pc, stimulus);
        }

        /// <summary>A scripted character's vehicle received a named stimulus [hook for the AI code].</summary>
        public static event System.Action<PlayerCar, string> StimulusSent;

        /// <summary>
        /// Game.JackKnife (0x605030): Hermes MJackKnife (kind "MJackKnife" 0x8912E4) posted to the character's spawned vehicle's
        /// AI (AIActionJackKnife 0x4071C0: a tractor-trailer swings its trailer across the road). No gameplay.bin handler calls
        /// it; a trailer here (TractorHitch) is a traffic vehicle's, so the request is only logged [hook: TrafficPhysics].
        /// </summary>
        public static void JackKnife(GInstance character)
        {
            if (character == null || !Live.TryGetValue(character.Key, out var pc) || !pc) return;
            Debug.Log($"NFSMW lua: Drive: JackKnife {character.LocalName} (MJackKnife to its AI)");
            JackKnifeRequested?.Invoke(pc);
        }

        public static event System.Action<PlayerCar> JackKnifeRequested;

        /// <summary>
        /// GManager::UnspawnUselessCharacters (every GManager::Update) → GCharacter::IsNoLongerUseful (0x5EB800): a spawned
        /// character (state 4 / 5) whose vehicle has been going for at least 0.5 [?: IVehicle vtable +0x60] is useless when,
        /// for every local player, it is 100 m or more away and behind him (dot(character − player, player forward) &lt; 0);
        /// then GCharacter::Unspawn (0x5EEB90). Called once per frame from LuaDrive.Update [ours: the GManager tick].
        /// </summary>
        public static void UnspawnUseless()
        {
            if (Live.Count == 0) return;
            var boot = GameBootstrap.Instance;
            if (!boot || !boot.Player) return;
            var players = new List<PlayerCar> { boot.Player };
            if (boot.Player2) players.Add(boot.Player2);
            List<uint> gone = null;
            foreach (var kv in Live)
            {
                var pc = kv.Value;
                if (!pc) { (gone ??= new List<uint>()).Add(kv.Key); continue; }
                if (Time.time - SpawnTime(kv.Key) < 0.5f) continue;
                bool useful = false;
                foreach (var p in players)
                {
                    var d = pc.transform.position - p.transform.position;
                    if (d.magnitude < 100f || Vector3.Dot(d, p.transform.forward) >= 0f) { useful = true; break; }
                }
                if (!useful) (gone ??= new List<uint>()).Add(kv.Key);
            }
            if (gone == null) return;
            foreach (var k in gone)
            {
                if (Live.TryGetValue(k, out var pc) && pc) { Debug.Log($"NFSMW lua: character {pc.name} no longer useful (behind every player, 100 m+): unspawned"); Object.Destroy(pc.gameObject); }
                Live.Remove(k);
                Spawned.Remove(k);
            }
        }

        static readonly Dictionary<uint, float> Spawned = new Dictionary<uint, float>();
        static float SpawnTime(uint key) => Spawned.TryGetValue(key, out var t) ? t : 0f;

        /// <summary>The race is left: its characters go (GCharacter unspawn on Game.AbandonRace / race end).</summary>
        public static void Clear()
        {
            foreach (var pc in Live.Values) if (pc) Object.Destroy(pc.gameObject);
            Live.Clear();
        }
    }
}
