using System; using System.IO; using System.Linq; using System.Collections.Generic; using NfsMw.Core;
static class P {
  static void Main(string[] a) {
    string root = a[0];
    var db = AttribDatabase.LoadAll(Path.Combine(root, "GLOBAL", "attributes.bin"), Path.Combine(root, "GLOBAL", "gameplay.bin"));
    var gp = db.OfClass("gameplay").ToList();
    var byKey = gp.ToDictionary(c => c.Key, c => c);
    string N(uint k) => byKey.TryGetValue(k, out var c) ? (c.Name ?? k.ToString("X8")) : k.ToString("X8");
    if (a[1] == "presets") { var cd = CareerData.Load(root); foreach (var kv in cd.PresetCars) if (System.Text.RegularExpressions.Regex.IsMatch(kv.Key, a[2], System.Text.RegularExpressions.RegexOptions.IgnoreCase)) Console.WriteLine(kv.Key + " = " + kv.Value); return; }
    if (a[1] == "blobs") {
      foreach (var c in gp.Where(x => x.Name != null && x.Name.Contains(a[2]))) {
        if (!c.TryGet("bytecode", out int at, out int cnt, out var def)) continue;
        var src = c.Source; var d = src.Data;
        int size = Chunks.I32(d, at); int p = src.Db.Ptr(src.Pack, at + 4);
        string own = c.Has("handler_owner") ? string.Join(",", c.Keys("handler_owner").Select(N)) : "";
        string tl = c.Has("transitionlist") ? string.Join(",", c.Keys("transitionlist").Select(N)) : "";
        Console.WriteLine($"== {c.Name} owner={own} msg={(c.Has("message_id") ? (uint)c.Int("message_id") : 0):X8} trans={tl} size={size} p={p}");
        if (p >= 0 && size > 0) { byte[] raw; try { raw = Compression.Decompress(d, p); } catch (Exception e) { Console.WriteLine("  ERR " + e.Message); continue; }
          Console.WriteLine("   HEX " + BitConverter.ToString(raw).Replace("-", " "));
          var sb = new System.Text.StringBuilder(); foreach (var b in raw) sb.Append(b >= 32 && b < 127 ? (char)b : '.'); Console.WriteLine("   ASC " + sb); }
      }
    } else if (a[1] == "fields") {
      var cls = db.Classes[AttribDatabase.Hash("gameplay")];
      var fl = a[3].Split(',');
      foreach (var f in fl) Console.WriteLine($"field {f}: {(cls.Fields.TryGetValue(AttribDatabase.Hash(f), out var fd) ? "type " + (db.Names.TryGetValue(fd.Type, out var tn) ? tn : fd.Type.ToString("X8")) : "ABSENT")}");
      foreach (var c in gp.Where(x => x.Name != null && System.Text.RegularExpressions.Regex.IsMatch(x.Name, a[2]))) {
        var parts = new List<string>();
        foreach (var f in fl) {
          if (!cls.Fields.TryGetValue(AttribDatabase.Hash(f), out var fd) || !c.Has(f)) continue;
          string tn = db.Names.TryGetValue(fd.Type, out var t2) ? t2 : "";
          string v = tn.Contains("Int") ? c.Int(f).ToString() : tn.Contains("Float") ? c.Float(f).ToString() : tn.Contains("Bool") ? c.Bool(f).ToString() : tn.Contains("Text") ? string.Join("|", c.Texts(f)) : tn.Contains("StringKey") ? c.StringKey(f) : tn.Contains("Vector") || tn.Contains("bVector") ? c.Vector3(f).X+","+c.Vector3(f).Y+","+c.Vector3(f).Z : string.Join(",", c.Keys(f).Select(N));
          parts.Add(f + "=" + v);
        }
        Console.WriteLine(c.Name + ": " + string.Join("  ", parts));
      }
    } else if (a[1] == "cls") {
      var all = AttribDatabase.LoadAll(Path.Combine(root, "GLOBAL", "attributes.bin"), Path.Combine(root, "GLOBAL", "gameplay.bin"), Path.Combine(root, "GLOBAL", "FE_ATTRIB.bin"));
      uint ck = Convert.ToUInt32(a[2], 16);
      Console.WriteLine("class " + (all.Names.TryGetValue(ck, out var cn) ? cn : "?") + " present=" + all.Classes.ContainsKey(ck));
      var fieldNames = new Dictionary<uint, string>();
      foreach (var line in File.ReadAllLines(Path.Combine(root, "..", "NFSMW_RE", "vlt_names_all.txt"))) fieldNames.TryAdd(AttribDatabase.Hash(line.Trim()), line.Trim());
      if (all.Classes.TryGetValue(ck, out var cl)) foreach (var kv in cl.Fields) Console.WriteLine($"  field {(fieldNames.TryGetValue(kv.Key, out var fnm) ? fnm : kv.Key.ToString("X8"))} type {(all.Names.TryGetValue(kv.Value.Type, out var tn) ? tn : kv.Value.Type.ToString("X8"))}");
      foreach (var c in all.Collections.Values.Where(c => c.ClassKey == ck).Take(20)) {
        Console.WriteLine("coll " + (c.Name ?? c.Key.ToString("X8")));
        if (all.Classes.TryGetValue(ck, out var cl2)) foreach (var kv in cl2.Fields) {
          string f = fieldNames.TryGetValue(kv.Key, out var fnm2) ? fnm2 : null; if (f == null || !c.Has(f)) continue;
          string tn = all.Names.TryGetValue(kv.Value.Type, out var t3) ? t3 : "";
          try { Console.WriteLine($"   {f} = {(tn.Contains("Float") ? c.Float(f).ToString() : tn.Contains("Int") ? c.Int(f).ToString() : tn.Contains("Bool") ? c.Bool(f).ToString() : tn.Contains("Text") ? string.Join("|", c.Texts(f)) : string.Join(",", c.Keys(f).Select(x => x.ToString("X8"))))}"); } catch {}
        }
      }
    } else if (a[1] == "pos") {
      foreach (var c in gp.Where(x => x.Name != null && x.Name.Contains(a[2]))) {
        string s = $"{c.Name}";
        if (c.Has("Position")) { var v = c.Vector3("Position"); s += $" pos=({v.X:0.0},{v.Y:0.0},{v.Z:0.0})"; }
        if (c.Has("Rotation")) s += $" rot={c.Float("Rotation"):0.0}";
        if (c.Has("Radius")) s += $" r={c.Float("Radius"):0.0}";
        if (c.Has("Dimensions")) { var v = c.Vector3("Dimensions"); s += $" dim=({v.X:0.0},{v.Y:0.0},{v.Z:0.0})"; }
        foreach (var f in new[]{"RespawnMarker","ForceStartPosition","target"}) if (c.Has(f)) s += $" {f}={string.Join(",", c.Keys(f).Select(N))}";
        Console.WriteLine(s);
      }
    }
  }
}
