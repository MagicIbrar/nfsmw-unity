using System.Collections.Generic;
using System.Threading.Tasks;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Game.Police
{
    /// <summary>
    /// gHeliSheetManager (World/HeliSheet.cpp; Core/HeliSheet.cs): the helicopter flight sheets of every drivable section.
    /// The game loads a section's sheet (chunk 0x34159) with the section; here all 435 (51 504 triangles, ~1 MB) are read
    /// from STREAML2RA.BUN once on a worker thread [ours: no streaming], with the drivable section boundaries for
    /// VisibleSectionManager::GetDrivableSectionNumber. Until they are in, <see cref="Ready"/> is false.
    /// </summary>
    public static class HeliSheets
    {
        static Dictionary<int, HeliSheet.Section> _sections;
        static VisibleSections _vis;
        static Task _load;
        static volatile bool _ready;

        public static bool Ready
        {
            get
            {
                if (_ready) return true;
                if (_load == null)
                {
                    GameData.Mount();
                    string root = GameData.Root;
                    _load = Task.Run(() =>
                    {
                        try
                        {
                            var map = new Dictionary<int, HeliSheet.Section>();
                            using (var ts = new TrackStream(root))
                            {
                                var vis = VisibleSections.Read(ts.Bundle);
                                foreach (var s in ts.Sections)
                                {
                                    if (s.IsShared || s.Size <= 0) continue;
                                    var raw = ts.ReadRaw(s);
                                    foreach (var h in HeliSheet.ReadAll(raw, 0, raw.Length)) map[h.Number] = h;
                                }
                                _vis = vis;
                            }
                            _sections = map;
                            _ready = true;
                        }
                        catch (System.Exception e) { Debug.LogWarning("NFSMW heli sheets: " + e.Message); }
                    });
                }
                return false;
            }
        }

        /// <summary>HeliSheetManager::FindHeliPoly at game (x, y).</summary>
        public static bool FindPoly(float x, float y, out HeliSheet.Poly poly)
        {
            poly = default;
            if (!Ready) return false;
            int sec = _vis.FindDrivableSection(x, y);
            if (sec <= 0 || !_sections.TryGetValue(sec, out var s)) return false;   // section 0 / none → no poly
            return HeliSheet.Find(s, x, y, out poly);
        }
    }

    /// <summary>
    /// HeliSheetCoordinate (World/HeliSheet.hpp): a cached triangle and the last elevation (1000 at first). GetElevation
    /// keeps the triangle while the point stays in it, else looks the poly up again; with no poly it returns the previous
    /// elevation as invalid. Unity points: game (x, y) = Unity (x, z), elevation = Unity y.
    /// </summary>
    public sealed class HeliSheetCoordinate
    {
        float _previous = 1000f;
        bool _valid;
        HeliSheet.Poly _poly;

        /// <summary>The sheet height under p and its normal (Unity axes), or false (previous height) with no sheet poly.</summary>
        public bool GetElevation(Vector3 p, out float elevation, out Vector3 normal)
        {
            normal = Vector3.up;
            if (!_valid || !HeliSheet.InTriangle(p.x, p.z, _poly.Vertex(0), _poly.Vertex(1), _poly.Vertex(2)))
            {
                if (!HeliSheets.FindPoly(p.x, p.z, out var poly)) { _valid = false; elevation = _previous; return false; }
                _valid = true;
                _poly = poly;
            }
            elevation = HeliSheet.Elevation(_poly, p.x, p.z, out var n);
            normal = new Vector3(n.X, n.Z, n.Y);   // game → Unity axes
            _previous = elevation;
            return true;
        }
    }
}
