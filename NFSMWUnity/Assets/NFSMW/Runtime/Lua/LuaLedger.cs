using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace NfsMw.Game.Lua
{
    /// <summary>
    /// The Shadow comparison [ours]. Both flows write normalized records ("Checkpoint racer=2 n=3", "SMS 15",
    /// "UnlockRace race_bin_15/15_1_1_sprint", …): the Lua side from the bindings the scripts call, the C# side from what
    /// LuaFlow sees the hand-ported code do (or from explicit LuaFlow.Observe taps). A record of one side that the other
    /// side does not produce within its window is logged as a divergence ("NFSMW lua: …"). The window is 3 s for race
    /// events and longer where the two flows legitimately take different paths to the same result: a goal is set at
    /// race setup by C# but after the intro NIS by the script (30 s), an SMS is queued until free roam (60 s) [ours].
    /// </summary>
    public sealed class LuaLedger
    {
        public float Window = 3f;

        public float WindowFor(string rec) =>
            rec.StartsWith("SMS") ? 60f : rec.StartsWith("Goal") || rec.StartsWith("UnlockRace") ? 30f : rec.StartsWith("RaceStart") || rec.StartsWith("Bounty") ? 10f : Window;
        public int Matched, LuaOnly, CsOnly;
        readonly List<(float t, string rec)> _lua = new List<(float, string)>(), _cs = new List<(float, string)>();
        public readonly List<string> Recent = new List<string>();

        public void Lua(float now, string rec) => Add(now, rec, _lua, _cs);
        public void CSharp(float now, string rec) => Add(now, rec, _cs, _lua);

        void Add(float now, string rec, List<(float t, string rec)> mine, List<(float t, string rec)> other)
        {
            int i = other.FindIndex(x => x.rec == rec);
            if (i >= 0) { other.RemoveAt(i); Matched++; return; }
            mine.Add((now, rec));
        }

        /// <summary>Logs and drops the records whose window has passed.</summary>
        public void Flush(float now, bool all = false)
        {
            Expire(now, all, _lua, "the Lua flow did", "the C# flow did not");
            Expire(now, all, _cs, "the C# flow did", "the Lua flow did not");
        }

        void Expire(float now, bool all, List<(float t, string rec)> list, string who, string other)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (!all && now - list[i].t < WindowFor(list[i].rec)) continue;
                string line = $"NFSMW lua: divergence at {list[i].t:0.0} s: {who} `{list[i].rec}`, {other}";
                if (list == _lua) LuaOnly++; else CsOnly++;
                Recent.Add(line);
                if (Recent.Count > 100) Recent.RemoveAt(0);
                Debug.Log(line);
                list.RemoveAt(i--);
            }
        }

        public string Summary => $"matched {Matched}, Lua only {LuaOnly}, C# only {CsOnly}, pending {_lua.Count}/{_cs.Count}";
        public IEnumerable<string> PendingLua => _lua.Select(x => x.rec);
    }
}
