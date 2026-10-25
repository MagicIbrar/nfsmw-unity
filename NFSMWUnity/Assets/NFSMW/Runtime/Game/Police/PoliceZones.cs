using System.Collections.Generic;
using System.IO;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Game.Police
{
    /// <summary>
    /// TheTrackPathManager.FindZone for the police and the speech AI (Core/TrackPathZones.cs reads the records):
    /// hiding spots (type 8), no-cop-spawn areas (13), pursuit-start areas (14) and the tunnel family (3, 4, 5, 7, 10)
    /// that SFXCTL_Tunnel tests for the player. Zone points are game X/Y = Unity x/z, elevations game Z = Unity y.
    /// Loaded once from the world's L2RA bundle (or the file when no world is up).
    /// </summary>
    public static class PoliceZones
    {
        static List<TrackPathZone>[] _byType;

        static List<TrackPathZone>[] ByType
        {
            get
            {
                if (_byType != null) return _byType;
                _byType = new List<TrackPathZone>[TrackPathZones.Count];
                for (int i = 0; i < _byType.Length; i++) _byType[i] = new List<TrackPathZone>();
                try
                {
                    GameData.Mount();
                    var w = GameBootstrap.Instance ? GameBootstrap.Instance.World : null;
                    var d = w != null && w.Track != null ? w.Track.Bundle : GameFiles.ReadBundle(Path.Combine(GameData.Root, "TRACKS", "L2RA.BUN"));
                    foreach (var z in TrackPathZones.Parse(d))
                        if (z.Type >= 0 && z.Type < _byType.Length) _byType[z.Type].Add(z);
                }
                catch (System.Exception e) { Debug.LogWarning("NFSMW police: track path zones unavailable: " + e.Message); }
                return _byType;
            }
        }

        public static IReadOnlyList<TrackPathZone> OfType(int type) => type >= 0 && type < TrackPathZones.Count ? ByType[type] : new List<TrackPathZone>();

        /// <summary>FindZone iteration: every zone of a type whose polygon holds the point (x/z).</summary>
        public static IEnumerable<TrackPathZone> At(int type, Vector3 pos)
        {
            foreach (var z in OfType(type))
                if (z.Contains(pos.x, pos.z)) yield return z;
        }

        public static TrackPathZone First(int type, Vector3 pos)
        {
            foreach (var z in OfType(type))
                if (z.Contains(pos.x, pos.z)) return z;
            return null;
        }

        /// <summary>
        /// SFXCTL_Tunnel::UpdateIsInTunnel / GetTunnelType: a tunnel, overpass, small overpass, garage or dynamic zone
        /// at the position whose elevation is 0 or at or above the car's height. Dynamic zones count unless their
        /// VisitInfo is 1; visit info is runtime state, never 1 here [?].
        /// </summary>
        public static bool InTunnel(Vector3 pos)
        {
            foreach (int t in TunnelTypes)
                foreach (var z in At(t, pos))
                    if (z.Elevation == 0f || z.Elevation >= pos.y) return true;
            return false;
        }

        static readonly int[] TunnelTypes = { TrackPathZones.Tunnel, TrackPathZones.Overpass, TrackPathZones.OverpassSmall, TrackPathZones.Garage, TrackPathZones.Dynamic };

        /// <summary>
        /// AmIinATunnel(view, CheckOverPass = 1) for the helicopter's LOS (AIVehicleHelicopter.cpp:174): the game reads
        /// the view's precipitation inTunnel / inOverpass flags; here the camera position against the tunnel and
        /// overpass zones with the same elevation rule [?].
        /// </summary>
        public static bool CameraInTunnel()
        {
            var cam = Camera.main;
            if (!cam) return false;
            var p = cam.transform.position;
            foreach (int t in ViewTunnelTypes)
                foreach (var z in At(t, p))
                    if (z.Elevation == 0f || z.Elevation >= p.y) return true;
            return false;
        }

        static readonly int[] ViewTunnelTypes = { TrackPathZones.Tunnel, TrackPathZones.Overpass, TrackPathZones.OverpassSmall };
    }
}
