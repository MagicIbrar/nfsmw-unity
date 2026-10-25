using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.Core.Lua;
using NfsMw.Game.Gameplay;
using UnityEngine;

namespace NfsMw.Game.Lua
{
    /// <summary>
    /// The GTriggers of the Lua flow (GTrigger ctor 0x601790, NotifySimableTrigger 0x5F6A30, ActivateReferencedTriggers
    /// 0x5E87F0): the triggers connected to a running activity (and, recursively, to its Children) hold an activation
    /// reference and sit in the WTriggerManager (Core/WTrigger.cs: box / cylinder / plane volumes, the per-frame rigid-body
    /// scan with the velocity look-ahead, the fire-on-exit list). A simable inside one for the first time posts
    /// MTriggerEnter (r1.Sender = the trigger, r1.Element = the simable); leaving posts MTriggerExit only when the record
    /// has FireOnExit. MTriggerInside (every tick while inside) is raised as a C# event only: no handler in gameplay.bin
    /// listens for it [?]. The simables are the players' cars and the racers' cars [ours: traffic and cops are simables
    /// in the exe too, but no handler reacts to them].
    /// </summary>
    public sealed class LuaTriggers
    {
        readonly StategraphRuntime _rt;
        readonly uint _trigger;
        float _refreshAt;
        string _runningKey;
        HashSet<uint> _active = new HashSet<uint>();
        readonly HashSet<PlayerCar> _known = new HashSet<PlayerCar>();

        /// <summary>The GTriggers and their WTriggerManager (GManager's trigger objects for this runtime).</summary>
        public readonly GameTriggers Triggers;

        public LuaTriggers(StategraphRuntime rt)
        {
            _rt = rt;
            _trigger = AttribDatabase.Hash("trigger");
            Triggers = new GameTriggers(rt.Data);
            Triggers.Enter += (t, sim) => _rt.Post(StategraphRuntime.TriggerEnter, ("Sender", t.G), ("Element", sim));
            Triggers.Exit += (t, sim) => _rt.Post(StategraphRuntime.TriggerExit, ("Sender", t.G), ("Element", sim));
        }

        public int ActiveCount => Triggers.ActiveCount;

        /// <summary>A gameplay record as a RacePoint (Position, Rotation, Radius, Dimensions).</summary>
        public static RacePoint PointOf(AttribCollection c)
        {
            if (c == null || !c.Has("Position")) return null;
            return new RacePoint
            {
                Name = c.Name,
                Position = c.Vector3("Position"),
                Rotation = c.Float("Rotation"),
                Radius = c.Float("Radius"),
                Width = c.Float("Width"),
                Dims = c.Has("Dimensions") ? c.Vector3("Dimensions") : default,
            };
        }

        /// <summary>ActivateReferencedTriggers for every running activity: the referenced triggers get an activation reference, the others lose theirs.</summary>
        void Refresh()
        {
            var act = new HashSet<uint>();
            var wanted = new List<GInstance>();
            foreach (var a in _rt.RunningActivities) Collect(a.Instance, act, wanted, 0);
            _active = act;
            Triggers.SetActive(wanted);
        }

        void Collect(GInstance g, HashSet<uint> into, List<GInstance> wanted, int depth)
        {
            if (depth > 4) return;
            foreach (var c in _rt.Connections(g))
                if (_rt.Data.DerivesFrom(c.Record, _trigger) && into.Add(c.Key)) wanted.Add(c);
            foreach (var ch in _rt.Children(g))
            {
                if (_rt.Data.DerivesFrom(ch.Record, _trigger) && into.Add(ch.Key)) wanted.Add(ch);
                Collect(ch, into, wanted, depth + 1);
            }
        }

        /// <summary>One frame (WTriggerManager::Update from the sim task): every simable against every active trigger.</summary>
        public void Update(IEnumerable<PlayerCar> simables)
        {
            if (Time.time >= _refreshAt)
            {
                // the active set changes only when activities start or stop
                _refreshAt = Time.time + 0.5f;
                string key = string.Join(",", _rt.RunningActivities.Select(x => x.Instance.Key));
                if (key != _runningKey) { _runningKey = key; Refresh(); }
            }
            var list = simables as IList<PlayerCar> ?? simables.ToList();
            // cars that went away leave their triggers silently
            if (_known.Count > 0)
                foreach (var old in _known.Where(k => !k || !list.Contains(k)).ToList()) { Triggers.Forget(old); _known.Remove(old); }
            foreach (var c in list) if (c) _known.Add(c);
            Triggers.Update(Mathf.Max(Time.deltaTime, 1f / 120f), list);
        }

        /// <summary>Game.ResetTrigger (0x604D10): the trigger forgets who is inside.</summary>
        public void Reset(GInstance g)
        {
            var t = g != null ? Triggers.Find(g.Key) : null;
            t?.Reset(Triggers.Manager);
        }

        /// <summary>The GTrigger of a record, if the runtime made one (icons, debug).</summary>
        public GTrigger Of(GInstance g) => g != null ? Triggers.Find(g.Key) : null;
    }
}
