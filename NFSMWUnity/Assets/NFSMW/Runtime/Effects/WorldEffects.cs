using System.Collections.Generic;
using System.Threading.Tasks;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Effects
{
    /// <summary>
    /// Placed emitter groups: the EmitterLibrary entries of chunk 0x3BC00 (Core/EmitterData.cs EmitterReader) in the
    /// showroom sets (FRONTEND\PLATFORMS\*.BIN, all section 0xA28) and the streamed track sections.
    /// EmitterSystem::Loader (EmitterSystem.cpp:1374) starts the 0xA28 libraries at once with flags 0x8020000 (world
    /// effect, always close enough); ServiceWorldEffects (EmitterSystem.cpp:216) starts any other library whose position
    /// is within its group's FarClip + 20 m and 0.55 cos of a view direction, and deletes it when it leaves that range.
    /// Libraries with triggers (WorldFXTrigger, 42 in L2RA) fire on proximity in the game; they are not started here.
    /// </summary>
    public static class WorldEffects
    {
        sealed class Set
        {
            public readonly List<Placement> Items = new List<Placement>();
            public Transform Space;   // null = world (game space = Unity world swizzled)
            public int Layer;
            public bool Active = true, World;
        }

        sealed class Placement
        {
            public EmitterPlacement P;
            public FxFrame Local;
            public FxGroup Group;
            public float Far;
        }

        static readonly List<Set> Sets = new List<Set>();
        static Task<List<EmitterPlacement>> _trackScan;
        static Set _track;
        static WorldStreamer _streamer;
        static float _nextStreamerCheck;

        /// <summary>Registers a set of placements under a Unity transform whose local space is game-local (Coords.ToUnity).</summary>
        public static object Add(IEnumerable<EmitterPlacement> placements, Transform space, int layer)
        {
            var set = new Set { Space = space, Layer = layer };
            Fill(set, placements);
            Sets.Add(set);
            _ = EmitterSystem.Instance;
            return set;
        }

        static void Fill(Set set, IEnumerable<EmitterPlacement> placements)
        {
            var db = EmitterSystem.Db;
            foreach (var p in placements)
            {
                if (p.Section == 0 || p.Triggers.Length > 0) continue;
                var g = db?.Group(p.GroupKey);
                if (g == null || g.Emitters.Length == 0) continue;
                set.Items.Add(new Placement { P = p, Local = FxFrame.FromMatrix(p.Matrix), Far = g.FarClip + 20f });
            }
        }

        public static void Remove(object handle)
        {
            if (!(handle is Set set)) return;
            foreach (var p in set.Items) p.Group?.Kill();
            Sets.Remove(set);
        }

        /// <summary>
        /// Starts the background scan of the track's emitter placements (STREAML2RA.BUN chunk headers only); they are
        /// serviced while a <see cref="WorldStreamer"/> is running.
        /// </summary>
        public static void LoadTrack()
        {
            if (_trackScan != null || _track != null || GameData.Root == null) return;
            string root = GameData.Root;
            _trackScan = Task.Run(() => EmitterReader.ScanTrack(root));
        }

        internal static void Service(EmitterSystem sys)
        {
            if (_track == null && _trackScan != null && _trackScan.IsCompleted)
            {
                if (_trackScan.Status == TaskStatus.RanToCompletion)
                {
                    _track = new Set { World = true };
                    Fill(_track, _trackScan.Result);
                    Sets.Add(_track);
                    Debug.Log($"NFSMW effects: {_track.Items.Count} world emitter placements");
                }
                else Debug.LogWarning("NFSMW effects: track emitter scan failed: " + _trackScan.Exception?.GetBaseException().Message);
                _trackScan = null;
            }
            if (_track != null && Time.unscaledTime >= _nextStreamerCheck)
            {
                _nextStreamerCheck = Time.unscaledTime + 1f;
                if (!_streamer) _streamer = Object.FindAnyObjectByType<WorldStreamer>();
                _track.Active = _streamer && _streamer.isActiveAndEnabled;
            }

            for (int s = Sets.Count - 1; s >= 0; s--)
            {
                var set = Sets[s];
                if (!set.World && !set.Space) { Remove(set); continue; }
                bool on = set.Active && (set.World || set.Space.gameObject.activeInHierarchy);
                var space = set.World ? FxFrame.Identity : FxFrame.Of(set.Space);
                foreach (var p in set.Items)
                {
                    if (p.Group != null && p.Group.Dead) p.Group = null;
                    bool always = p.P.Section == EmitterReader.AlwaysOnSection;
                    var frame = p.Local.In(space);
                    bool want = on && (always || set.Layer != 0 || sys.IsCloseEnough(frame.P, p.Far, true, 0.55f));
                    if (!want)
                    {
                        if (p.Group != null) { p.Group.Kill(); p.Group = null; }
                        continue;
                    }
                    if (p.Group == null)
                    {
                        p.Group = sys.Create(p.P.GroupKey, EmitterSystem.WorldEffect | (always ? EmitterSystem.AlwaysClose : 0u), set.Layer);
                        if (p.Group == null) continue;
                        p.Group.AutoUpdate = true;
                    }
                    p.Group.Frame = frame;
                }
            }
        }
    }
}
