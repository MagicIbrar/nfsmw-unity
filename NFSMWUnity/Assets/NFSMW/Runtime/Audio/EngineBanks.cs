using System.Collections.Generic;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.Audio
{
    /// <summary>
    /// The AI / cop engine bank budget (CSTATEMGR_CarState statics, EAXSound/States/Managers/STATEMGR_CarState.cpp:231-398):
    /// at most four engineaudio banks for AI racers and cops together. ResolveCarBanks (PC @0x4F9020, from EAXSound::CommitAssets
    /// @0x4B9030, i.e. when new cars' assets are committed) takes the engines of every AI racer / cop that is not connected yet;
    /// engines over the budget are remapped to a loaded one and the car's engineaudio is swapped (ChangeWithDefault), so it
    /// sounds like that engine (Ginsu files, bank, rpm range ...):
    /// <list type="bullet">
    /// <item>budget: N = wanted + loaded; nothing to do while N ≤ 4 (quick race: no cops) or N ≤ 3 (cops can appear).</item>
    /// <item>wanted engines sorted by engineaudio Priority (high first); MaybeV8 ones and EngType == eENGINE_V8 (2) ones listed.</item>
    /// <item>rule 2 (cops can appear and no cop V8 yet): a wanted V8 (rotating LastV8Used) is loaded as the cop V8, else a random
    /// one of corvette_z06 / for_mus_b / for_mus_c / hum_h1; every MaybeV8 engine then maps to the first loaded engine.</item>
    /// <item>rule 3: from the lowest priority up, a wanted engine with the same EngType as a higher one maps onto it (that one
    /// loads).</item>
    /// <item>rule 4: the lowest wanted engine maps to a loaded one of the same EngType, else a random loaded one.</item>
    /// <item>the rest load as themselves; every pending car takes the first mapping whose Start is its engine.</item>
    /// </list>
    /// DestroyCar (@0x4F3200, EAXSound::DestroyEAXCar) unloads an engine no other car references (the cop V8 stays: the static
    /// CopsCanBeInGame is only ever set true); ResetCarBanks (@0x4F30D0, LoadInGameSoundBanks) clears it all — the port at the
    /// local player's engine creation. The port batches the cars whose engines first tick in the same frame [ours: the exe's
    /// CommitAssets batch]. Rule 4 with nothing loaded (only reachable in a quick race) picks Random(0) in the exe; the port
    /// loads the engine instead [?].
    /// </summary>
    public static class EngineBanks
    {
        struct Mapping { public uint Start, Finish; }

        static readonly List<Mapping> FinalMapping = new List<Mapping>();
        static readonly List<uint> FinalEngines = new List<uint>();
        static readonly List<uint> FinalCopV8Engines = new List<uint>();
        static readonly List<(uint key, EngineAudio car)> EngineToCarMapping = new List<(uint, EngineAudio)>();
        static int _lastV8Used;
        static readonly string[] V8CopEngines = { "corvette_z06", "for_mus_b", "for_mus_c", "hum_h1" };
        const int ENGINE_V8 = 2;

        /// <summary>CSTATEMGR_CarState::ResetCarBanks.</summary>
        public static void Reset()
        {
            FinalMapping.Clear();
            FinalEngines.Clear();
            FinalCopV8Engines.Clear();
            EngineToCarMapping.Clear();
        }

        static AttribCollection Eng(uint cls, uint key)
        {
            var db = GameData.Attributes;
            return db != null && db.Collections.TryGetValue((cls, key), out var c) ? c : null;
        }

        static void AddMapping(uint a, uint b)
        {
            foreach (var m in FinalMapping) if (m.Start == a && m.Finish == b) return;
            FinalMapping.Add(new Mapping { Start = a, Finish = b });
        }

        /// <summary>
        /// ResolveCarBanks for the given unconnected AI racers / cops: returns, per car, the engineaudio key it should use
        /// (its own when unmapped).
        /// </summary>
        public static uint[] Resolve(IList<EngineAudio> cars, bool copsCanBeInGame)
        {
            var result = new uint[cars.Count];
            if (cars.Count == 0) return result;
            uint cls = 0;
            var want = new List<uint>();
            for (int i = 0; i < cars.Count; i++)
            {
                var a = cars[i].Model.A;
                result[i] = a.EngineKey;
                if (a.EngineKey == 0) continue;
                cls = a.EngineClass;
                if (!FinalEngines.Contains(a.EngineKey) && !want.Contains(a.EngineKey)) want.Add(a.EngineKey);
            }
            if (want.Count == 0) return result;   // the exe returns before the mapping pass
            int n = want.Count + FinalEngines.Count;
            bool resolve = copsCanBeInGame ? n > 3 : n > 4;
            if (resolve)
            {
                float Prio(uint k) { var c = Eng(cls, k); return c != null && c.Has("Priority") ? c.Float("Priority") : 0f; }
                int Type(uint k) { var c = Eng(cls, k); return c != null && c.Has("EngType") ? c.Int("EngType") : 0; }
                bool MaybeV8(uint k) { var c = Eng(cls, k); return c != null && c.Bool("MaybeV8"); }
                // std::sort(.., sort_engine_priority): higher Priority first [?: ties keep the list order]
                var sorted = new List<(uint k, int i)>();
                for (int i = 0; i < want.Count; i++) sorted.Add((want[i], i));
                sorted.Sort((x, y) => { int c = Prio(y.k).CompareTo(Prio(x.k)); return c != 0 ? c : x.i.CompareTo(y.i); });
                want.Clear();
                foreach (var s in sorted) want.Add(s.k);
                var upgradeV8 = new List<uint>();
                var v8 = new List<uint>();
                foreach (var k in want)
                {
                    if (MaybeV8(k)) upgradeV8.Add(k);
                    if (Type(k) == ENGINE_V8) v8.Add(k);
                }
                bool done = false;
                if (copsCanBeInGame && FinalCopV8Engines.Count == 0)
                {
                    if (v8.Count != 0)
                    {
                        _lastV8Used %= v8.Count;
                        uint e = v8[_lastV8Used];
                        FinalEngines.Add(e);
                        FinalCopV8Engines.Add(e);
                        AddMapping(e, e);
                        want.Remove(e);
                        _lastV8Used++;
                    }
                    else
                    {
                        var db = GameData.Attributes;
                        var c = db?.Get("engineaudio", V8CopEngines[Random.Range(0, 4)]);
                        uint e = c != null ? c.Key : 0;
                        if (e != 0)
                        {
                            v8.Add(e);
                            FinalEngines.Add(e);
                            FinalCopV8Engines.Add(e);
                            AddMapping(e, e);
                        }
                    }
                    // BeginRule2
                    if (v8.Count > 0) _lastV8Used %= v8.Count;
                    while (upgradeV8.Count != 0 && FinalEngines.Count > 0)
                    {
                        AddMapping(upgradeV8[0], FinalEngines[0]);
                        want.Remove(upgradeV8[0]);
                        upgradeV8.RemoveAt(0);
                        _lastV8Used++;
                        if (FinalEngines.Count + want.Count <= 4) { done = true; break; }
                        if (v8.Count > 0) _lastV8Used %= v8.Count;
                    }
                }
                // BeginRule3
                while (!done)
                {
                    bool restart = false;
                    for (int i = want.Count - 1; i >= 0 && !restart; i--)
                        for (int j = i - 1; j >= 0; j--)
                        {
                            if (Type(want[j]) != Type(want[i])) continue;
                            uint lo = want[i], hi = want[j];
                            AddMapping(lo, hi);
                            AddMapping(hi, hi);
                            FinalEngines.Add(hi);
                            want.Remove(lo);
                            want.Remove(hi);
                            if (FinalEngines.Count + want.Count <= 4) done = true;
                            restart = true;
                            break;
                        }
                    if (!restart) break;
                }
                // BeginRule4
                while (!done && want.Count > 0)
                {
                    uint w = want[want.Count - 1];
                    uint to = 0;
                    foreach (var f in FinalEngines) if (Type(w) == Type(f)) { to = f; break; }
                    if (to == 0)
                    {
                        if (FinalEngines.Count == 0) break;   // Random(0) in the exe [?]
                        to = FinalEngines[Random.Range(0, FinalEngines.Count)];
                    }
                    AddMapping(w, to);
                    want.Remove(w);
                    if (FinalEngines.Count + want.Count <= 4) done = true;
                }
            }
            // LoadRemainingEngines
            foreach (var e in want)
            {
                FinalEngines.Add(e);
                AddMapping(e, e);
            }
            for (int i = 0; i < cars.Count; i++)
            {
                uint own = cars[i].Model.A.EngineKey;
                foreach (var m in FinalMapping)
                    if (m.Start == own)
                    {
                        result[i] = m.Finish;
                        EngineToCarMapping.Add((m.Finish, cars[i]));
                        break;
                    }
            }
            return result;
        }

        /// <summary>CSTATEMGR_CarState::DestroyCar for a car's current engine key.</summary>
        public static void Destroy(EngineAudio car, uint engkey)
        {
            bool stillRefed = false;
            for (int i = EngineToCarMapping.Count - 1; i >= 0; i--)
            {
                if (EngineToCarMapping[i].car == car) EngineToCarMapping.RemoveAt(i);
                else if (EngineToCarMapping[i].key == engkey) stillRefed = true;
            }
            // CopsCanBeInGame here is the static member, only ever set true (CSTATEMGR_* constructors)
            if ((FinalCopV8Engines.Count == 0 || FinalCopV8Engines[0] != engkey) && !stillRefed)
            {
                FinalEngines.Remove(engkey);
                FinalCopV8Engines.Remove(engkey);
                FinalMapping.RemoveAll(m => m.Finish == engkey);
            }
        }
    }
}
