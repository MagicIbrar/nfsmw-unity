using System;
using System.Collections.Generic;
using System.IO;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>
    /// The track's world animations (CAnimWorldScene / CWorldAnimEntity / CWorldAnimCtrl, decomp Animation/AnimWorldScene.cpp,
    /// AnimEntity_WorldEntity.cpp, WorldAnimCtrl.cpp, WorldAnimInstanceDirectory.cpp; StartWorldAnimations 0x450310,
    /// InstantiateAnimTree 0x44FFF0, CWorldAnimEntity::Init 0x44F340, CWorldAnimEntityTree Play/Pause/Stop/SetTime
    /// 0x44A230-0x44A300): the tower crane arms, sailboat, cargo ship, blimp and airliner of TRACKS\L2RA.BUN
    /// (<see cref="WorldAnimSet"/>).
    /// <list type="bullet">
    /// <item>InstantiateAnimTree (AnimWorldScene.cpp:55-185): a tree whose track_num does not cover the race's track number
    /// (two byte ranges, `TrackNumber % 100`) is not made; a named range only sets play flag 0x40 (the instance's own
    /// begin/end range is used); two bRandom(1.0) calls step the global sequence [not replayed]; entities are created in
    /// parent-index order, the root (instance hash == tree hash) takes the instance matrix, and only an entity with
    /// neither a start trigger nor a track direction auto-plays (the others stay STOPPED).</item>
    /// <item>CWorldAnimEntity::Init (:119-219): time scale = speed (0 → 1); SetMasterDelayTime(master_delay) and
    /// SetLocalDelayTime(loop_delay) always (flags 0x80/0x100); play flags 0 → 0x28; channels _t/_q/_s per content flag;
    /// 0x40 → SetLoopRange; 0x200 → local delay = bRandom(animLenSec·0.5) and 0x400 → eval time = bRandom(animLenFrames),
    /// each with a fresh seed 0x69BABE69 (so always 0.8260115 × the range); a start or stop trigger → flags 0x810 (triggered
    /// ping-pong). WorldModel{model, LODB, 0, LODZ} for a non-root with a model.</item>
    /// <item>CWorldAnimEntity::UpdateTimeStep (:269-299): while the tree's section is not visible the model is disabled and
    /// the clock stands; otherwise the model is enabled when the controller is not stopped, AdvanceAnimTime (playing only),
    /// the pose is always refreshed and bone 1's global matrix replaces the entity's local matrix (world = instance matrix ×
    /// pose). [ours] "section visible" = the tree's visible-section id is one the WorldStreamer has loaded, else (ids that
    /// are not stream sections) within <see cref="VisibleRange"/> of the camera.</item>
    /// <item>CAnimWorldScene::ControlWorldAnim (:187-206) / the tree's SetTime, Pause (shown, frozen), Play, Stop (hidden):
    /// StartWorldAnimations hides EN_TollBoothArm_01..04, World_RestoreProps shows them paused, CloseAllGarageDoors pauses
    /// EN_Chopshop_xx at 0 — none of those trees exist in L2RA, so they are no-ops in retail.</item>
    /// <item>[ours] Self-starting: the component waits for a WorldStreamer and needs no hook.</item>
    /// </list>
    /// </summary>
    public sealed class WorldAnimations : MonoBehaviour
    {
        public static bool Enabled = true;   // AnimCfg_DisableWorldAnimations / DisableWorldAnimations
        public static WorldAnimations Instance { get; private set; }
        /// <summary>[ours] A tree whose section id is not a stream section shows within this range of the camera.</summary>
        public static float VisibleRange = 1500f;
        /// <summary>GRaceParameters TrackNumber % 100 for the track filter (L2RA's instances have track_num 0: no filter).</summary>
        public static int TrackNumber;
        const uint RandomSeed = 0x69BABE69;

        sealed class Entity
        {
            public WorldAnimEntity Info;
            public AnimCtrl Ctrl;          // null: no channel in the bank (the entity stays at its file local matrix)
            public GameObject Go;
            public bool Missing;
        }

        sealed class Tree
        {
            public WorldAnimTree Data;
            public Matrix4x4 Placement;   // instance matrix (Unity)
            public readonly List<Entity> Entities = new List<Entity>();
        }

        WorldAnimSet _set;
        System.Threading.Tasks.Task<Dictionary<uint, Solid>> _models;   // the wanted model solids of the shared section X0
        readonly List<Tree> _trees = new List<Tree>();
        WorldStreamer _streamer;
        MeshFactory _factory;
        float _poll;
        readonly float[] _sqt = new float[24], _local = new float[16];

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Instance || !Application.isPlaying) return;
            var go = new GameObject("World Animations") { hideFlags = HideFlags.DontSave };
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<WorldAnimations>();
        }

        void Update()
        {
            if (!Enabled) { SetAllVisible(false); return; }
            if (!_streamer)
            {
                _poll -= Time.unscaledDeltaTime;
                if (_poll > 0) return;
                _poll = 1f;
                _streamer = FindFirstObjectByType<WorldStreamer>();
                if (!_streamer) { Clear(); return; }
                if (_set == null && !Load()) { enabled = false; return; }
            }
            if (_set == null) return;
            if (_models == null && _streamer.Track != null)
            {
                var track = _streamer.Track;
                var want = new HashSet<uint>();
                foreach (var t in _trees) foreach (var e in t.Entities) want.Add(e.Info.ModelHash);
                _models = System.Threading.Tasks.Task.Run(() =>
                {
                    var found = new Dictionary<uint, Solid>();
                    var x0 = track.Find("X0");
                    if (x0 == null) return found;
                    var raw = track.ReadRaw(x0);
                    foreach (var so in SolidList.ReadAll(raw, 0, raw.Length)) if (want.Contains(so.Hash)) found[so.Hash] = so;
                    return found;
                });
            }
            // CAnimPlayer::UpdateTime(dT) → CAnimWorldScene::UpdateTime: the sim's frame time
            float dt = Time.deltaTime;
            var cam = Camera.main;
            var eye = cam ? cam.transform.position : (_streamer.Focus ? _streamer.Focus.position : Vector3.zero);
            foreach (var t in _trees)
            {
                bool visible = SectionVisible(t, eye);
                foreach (var e in t.Entities)
                {
                    if (!visible) { if (e.Go) e.Go.SetActive(false); continue; }   // the clock stands with the section unloaded
                    if (!e.Go && !e.Missing) e.Go = Build(e, SectionOf(t.Data.Instance.Section));
                    if (e.Go && e.Ctrl != null && !e.Ctrl.IsStopped) e.Go.SetActive(true);
                    e.Ctrl?.Advance(dt);
                    if (e.Go) Place(t, e);
                }
            }
        }

        /// <summary>TheTrackStreamer.IsSectionVisible(section_number): the bit table of the zone's sections_to_load.</summary>
        bool SectionVisible(Tree t, Vector3 eye)
        {
            var s = SectionOf(t.Data.Instance.Section);
            // a stream section: visible while loaded (WorldStreamer clears Section.Data once the section is built, so the
            // built scenery root is the "loaded" test; Data alone was only true while the section was being built)
            if (s != null) return s.Root != null || s.Data != null;
            Vector3 at = t.Placement.GetColumn(3);
            return (at - eye).sqrMagnitude < VisibleRange * VisibleRange;
        }

        bool Load()
        {
            try
            {
                string path = Path.Combine(GameData.Root ?? "", "TRACKS", "L2RA.BUN");
                if (!File.Exists(path)) return false;
                _set = WorldAnimSet.Load(File.ReadAllBytes(path));
            }
            catch (Exception ex) { Debug.LogWarning("NFSMW world anim: " + ex.Message); return false; }
            int filtered = 0;
            foreach (var tr in _set.Trees)
            {
                var inst = tr.Instance;
                if (!TrackAllowed(inst.TrackNum)) { filtered++; continue; }
                var t = new Tree { Data = tr, Placement = NisCharacter.ToUnity(inst.Matrix) };
                uint playFlags = inst.PlayFlags;
                if (inst.NamedRange != 0) playFlags |= 0x40;   // the named range's own values are discarded (:103-121)
                bool autoPlay = inst.StartTrigger == 0 && inst.TrackDir == 0;
                foreach (var e in tr.Entities)
                {
                    if (e.IsRoot || e.ModelHash == 0) continue;   // the root has no WorldModel
                    var ent = new Entity { Info = e, Ctrl = MakeCtrl(e, inst, playFlags, autoPlay) };
                    t.Entities.Add(ent);
                }
                _trees.Add(t);
            }
            Debug.Log($"NFSMW world anim: {_trees.Count} trees ({filtered} filtered by track number), {_set.Anims.Count} anims");
            return true;
        }

        /// <summary>InstantiateAnimTree's track filter: two byte ranges of track_num, both missed → no tree.</summary>
        static bool TrackAllowed(uint trackNum)
        {
            if (trackNum == 0) return true;
            int min1 = (int)(trackNum >> 8) & 0xFF, max1 = (int)trackNum & 0xFF, min2 = (int)(trackNum >> 24), max2 = (int)(trackNum >> 16) & 0xFF;
            int t = TrackNumber % 100;
            return (t >= min1 && t <= max1) || (t >= min2 && t <= max2);
        }

        /// <summary>CWorldAnimEntity::Init's controller setup; null when the entity has no channel in the bank.</summary>
        AnimCtrl MakeCtrl(WorldAnimEntity e, WorldAnimInstance inst, uint playFlags, bool autoPlay)
        {
            float length = _set.Length(e);
            bool any = false;
            for (int dof = 0; dof < 3; dof++) any |= (e.ContentFlags & (1 << dof)) != 0 && _set.Anim(e.ChannelHash(dof)) != null;
            if (!any) return null;
            var c = new AnimCtrl { World = true, TimeScale = inst.Speed == 0 ? 1f : inst.Speed, AnimLength = length, PlayState = AnimCtrl.Stopped };
            c.SetMasterDelayTime(inst.MasterDelay);
            c.SetLocalDelayTime(inst.LoopDelay);
            c.Flags |= playFlags == 0 ? 0x28u : playFlags;
            if ((c.Flags & 0x40) != 0) c.SetLoopRange(inst.BeginRange, inst.EndRange);
            float eff = c.EffectiveTimeScale;
            if ((c.Flags & 0x200) != 0) { uint seed = RandomSeed; c.SetLocalDelayTime(WorldAnimSet.BRandom(length / 30f / eff * 0.5f, ref seed)); }
            if ((c.Flags & 0x400) != 0) { uint seed = RandomSeed; c.SetEvalTime(WorldAnimSet.BRandom(length, ref seed)); }
            if (inst.StartTrigger != 0 || inst.StopTrigger != 0) { c.Flags &= ~8u; c.Flags |= 0x810; }
            if (autoPlay) c.Play();
            return c;
        }

        WorldStreamer.Section SectionOf(int number)
        {
            // the streamer's worker threads add sections under this lock (WorldStreamer.Scan)
            lock (_streamer.Sections)
                foreach (var s in _streamer.Sections) if (s.Info != null && s.Info.Number == number) return s;
            return null;
        }

        GameObject Build(Entity e, WorldStreamer.Section home)
        {
            // the model solid: the tree's own section first, then any loaded section (the retail models are in the shared
            // section X0: XO_TOWERCRANEARM_1B_00, XO_CARGOSHIPA_1B_00, XO_BLIMP_1B_00; the airliner's XO_AIRLINERA_1B_LB_00
            // and the sailboat's model are in no section, so the exe draws neither either)
            Solid solid = home?.Data?.Solids?.Find(s => s.Hash == e.Info.ModelHash);
            if (solid == null && _models != null && _models.IsCompleted && !_models.IsFaulted) _models.Result.TryGetValue(e.Info.ModelHash, out solid);
            if (solid == null)
            {
                if (_models != null && _models.IsCompleted) e.Missing = true;
                return null;
            }
            _factory ??= new MeshFactory(GameData.Textures);
            var built = _factory.Build(solid, PartKind.World);
            if (built == null) { e.Missing = true; return null; }
            var go = new GameObject("ANM " + solid.Name);
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = built.Mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = built.Materials;
            return go;
        }

        void Place(Tree t, Entity e)
        {
            if (e.Ctrl == null || !_set.Pose(e.Info, e.Ctrl.Eval, _sqt, _local)) Array.Copy(e.Info.LocalMatrix, _local, 16);
            // world = instance matrix * entity local (the anim's bone 1 pose replaces the entity's local matrix); the retail
            // trees are one level deep (root + children), so the parent chain is the instance matrix
            var m = t.Placement * NisCharacter.ToUnity(_local);
            var tr = e.Go.transform;
            tr.SetPositionAndRotation(m.GetColumn(3), Quaternion.LookRotation(m.GetColumn(2), m.GetColumn(1)));
            tr.localScale = new Vector3(m.GetColumn(0).magnitude, m.GetColumn(1).magnitude, m.GetColumn(2).magnitude);
        }

        // ---- CAnimWorldScene::ControlWorldAnim / CWorldAnimEntityTree ----

        /// <summary>
        /// ControlWorldAnim(tree hash, t, pause, hide): t ≥ 0 → SetTime(t) on every entity (SetEvalTime(0) + one UpdateTimeStep(t));
        /// then hide → Stop (eval 0, model hidden), pause → Pause (shown, frozen), else Play. False when no tree has that hash.
        /// </summary>
        public bool ControlWorldAnim(uint treeHash, float t, bool pause, bool hide)
        {
            bool found = false;
            foreach (var tr in _trees)
            {
                if (tr.Data.Hash != treeHash) continue;
                found = true;
                if (!pause && !hide) NfsMw.Audio.MomentStream.WorldAnimUnpaused(tr.Placement.GetColumn(3));   // MGamePlayMoment "unpause" → the moment stream by marker [hook: audio owner]
                foreach (var e in tr.Entities)
                {
                    if (e.Ctrl == null) continue;
                    if (t >= 0f) { e.Ctrl.SetEvalTime(0f); if (e.Ctrl.IsPlaying) e.Ctrl.Advance(t); }
                    if (hide) { e.Ctrl.Stop(); if (e.Go) e.Go.SetActive(false); }
                    else if (pause) { e.Ctrl.Pause(); if (e.Go) e.Go.SetActive(true); }
                    else { if (!e.Ctrl.IsPlaying) e.Ctrl.Play(); if (e.Go) e.Go.SetActive(true); }
                }
            }
            return found;
        }

        public bool ControlWorldAnim(string treeName, float t, bool pause, bool hide) => ControlWorldAnim(EaHash.Hash(treeName), t, pause, hide);

        /// <summary>StartWorldAnimations: the tollbooth arms hidden; World_RestoreProps (ResetWorldAnimations): shown, paused at 0.</summary>
        public void ResetWorldAnimations(bool hide)
        {
            for (int i = 1; i <= 4; i++) ControlWorldAnim($"EN_TollBoothArm_0{i}", 0f, true, hide);
        }

        /// <summary>TrackLoader::InitTopologyAndSceneryGroups → CloseAllGarageDoors: EN_Chopshop_xx paused at 0.</summary>
        public void CloseAllGarageDoors()
        {
            foreach (int n in new[] { 30, 13, 20, 14, 40, 50, 45, 90, 11, 60, 70, 35, 80, 12, 55 }) ControlWorldAnim("EN_Chopshop_" + n, 0f, true, false);
        }

        void SetAllVisible(bool on)
        {
            foreach (var t in _trees) foreach (var e in t.Entities) if (e.Go) e.Go.SetActive(on);
        }

        void Clear()
        {
            foreach (var t in _trees) foreach (var e in t.Entities) { if (e.Go) Destroy(e.Go); e.Go = null; e.Missing = false; }
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }
    }
}
