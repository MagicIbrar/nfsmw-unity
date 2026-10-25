using System;
using System.IO;
using System.Linq;
using NfsMw.Core;

// copzones: the TrackPathManager zones the police and speech AI use (Core/TrackPathZones.cs) and the pursuitlevels
// fields of the 911 / hiding rules (Runtime/Game/Police/PoliceData.cs), read from attributes.bin.
static class CopZonesProbe
{
    public static void Run(string root, string[] args)
    {
        var zones = TrackPathZones.Load(root);
        Console.WriteLine($"zones {zones.Count}: " + string.Join(" ", zones.GroupBy(z => z.Type).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}")));
        foreach (int t in new[] { TrackPathZones.Hidden, TrackPathZones.NoCopSpawn, TrackPathZones.PursuitStart })
        {
            Console.WriteLine($"type {t}:");
            foreach (var z in zones.Where(z => z.Type == t))
            {
                float cx = z.Xs.Average(), cy = z.Ys.Average();
                // a point-in-polygon self check at the vertex centroid (convex zones contain it)
                string data = z.Data[0] == 0 ? "both" : z.Data[0] == TrackPathZones.CarHash ? "Car" : z.Data[0] == TrackPathZones.HeliHash ? "Heli" : z.Data[0].ToString("X8");
                Console.WriteLine($"  centre ({cx:0},{cy:0}) unity ({cx:0}, {z.Elevation:0.0}, {cy:0}) size {z.MaxX - z.MinX:0}x{z.MaxY - z.MinY:0} pts {z.Xs.Length} elev {z.Elevation:0.##} data {data} inside {z.Contains(cx, cy)}");
            }
        }
        var db = AttribDatabase.Load(Path.Combine(root, "GLOBAL", "attributes.bin"));
        foreach (var n in new[] { "default", "heat_01", "heat_02", "heat_03", "heat_04", "heat_05", "heat_06" })
        {
            var c = db.Get("pursuitlevels", n);
            if (c == null) { Console.WriteLine($"pursuitlevels/{n}: missing"); continue; }
            string F(string f) => c.Has(f) ? c.Float(f).ToString("0.##") : "-";
            string I(string f) => c.Has(f) ? c.Int(f).ToString() : "-";
            Console.WriteLine($"pursuitlevels/{n}: CTSFor911 {I("CTSFor911")} NumCiviHitsFor911 {I("NumCiviHitsFor911")} Lifetime911 {F("Lifetime911")} TimeInactiveFor911 {F("TimeInactiveFor911")} HiddenZoneTimeMultiplier {F("HiddenZoneTimeMultiplier")} TimeToHideInZone {F("TimeToHideInZone")} roadblockhelichance {F("roadblockhelichance")}");
        }
    }
}
