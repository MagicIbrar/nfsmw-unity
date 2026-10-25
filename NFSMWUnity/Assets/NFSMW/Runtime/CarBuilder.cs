using System;
using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw
{
    /// <summary>Assembles a car model from CARS\NAME (stock kit, LOD A) with the game's runtime texture replacements.</summary>
    public static class CarBuilder
    {
        public sealed class Result
        {
            public GameObject Root;
            public Transform Body;
            public Transform[] Wheels = new Transform[4]; // FL, FR, RL, RR (pivot at hub)
            public CarSpec Spec;
            public List<Renderer> BrakeLights = new List<Renderer>();
            /// <summary>The car's CarRenderInfo (materials, light states, damage).</summary>
            public CarRender.CarRenderInfo Render;
        }

        static readonly Dictionary<string, CarAsset> Assets = new Dictionary<string, CarAsset>(StringComparer.OrdinalIgnoreCase);
        static readonly List<string> AssetOrder = new List<string>();
        static readonly HashSet<string> TexturesAdded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A CARS folder's geometry and textures, cached (the last few cars, shared part folders always).</summary>
        public static CarAsset Asset(string folder)
        {
            GameData.Mount();
            folder = folder.ToUpperInvariant();
            if (Assets.TryGetValue(folder, out var a)) return a;
            a = CarAsset.Load(GameData.Root, folder);
            Assets[folder] = a;
            bool shared = folder == "WHEELS" || folder == "ROOF" || folder.StartsWith("SPOILER");
            if (!shared)
            {
                AssetOrder.Add(folder);
                while (AssetOrder.Count > 6) { Assets.Remove(AssetOrder[0]); AssetOrder.RemoveAt(0); }
            }
            return a;
        }

        static void AddTextures(string folder, CarAsset asset)
        {
            if (TexturesAdded.Add(folder)) GameData.Textures.Add(asset.Textures, true);
        }

        public static Result Build(string carFolder, Color paint, Transform parent = null, Game.OwnedCar look = null)
        {
            GameData.Mount();
            var asset = Asset(carFolder);
            var lib = GameData.Textures;
            lib.Add(asset.Textures, true);
            var spec = CarSpec.FromAttributes(GameData.Attributes, carFolder.ToLowerInvariant());
            string car = carFolder.ToUpperInvariant();

            var factory = new MeshFactory(lib);
            var r = new Result { Spec = spec, Root = new GameObject(carFolder) };
            if (parent) r.Root.transform.SetParent(parent, false);
            // game car space: X forward, Y left, Z up -> after Coords.ToUnity that is +X forward, +Z left;
            // turn it so the car faces Unity +Z
            var body = new GameObject("Body").transform;
            body.SetParent(r.Root.transform, false);
            body.localRotation = Quaternion.Euler(0, -90, 0);
            // the body model sits on its sills at z = 0; chassis RIDE_HEIGHT (inches, 0x0254 m) lifts it over the wheels.
            // A car with a sim re-places it at RIDE_HEIGHT minus the static sag (PlayerCar.Setup); showroom cars keep this [?]
            body.localPosition = new Vector3(0, (spec.RideHeightFront + spec.RideHeightRear) * 0.5f * 0.0254f, 0);
            r.Body = body;
            // CarRenderInfo: replacement textures (skin, lights, glass, decals), light materials, damage (car_render.md);
            // the rim composite needs the shared WHEELS textures first
            if (look != null && !string.IsNullOrEmpty(look.RimBrand)) AddTextures("WHEELS", Asset("WHEELS"));
            var info = CarRender.CarRenderInfo.Begin(r.Root, car, look, paint, factory, body);
            r.Render = info;

            // the parts to draw, by slot: KIT00 (stock), overridden by the chosen kit and hood
            int kit = look?.Kit ?? 0;
            var slots = new Dictionary<string, Solid>();
            // cop and some traffic models have no plain body: their intact parts are the DAMAGE0 set
            bool damageParts = !asset.Solids.Exists(x => x.Name == car + "_KIT00_BODY_A" || x.Name == car + "_BASE_A");
            foreach (var pass in new[] { car + "_KIT00_", kit > 0 ? $"{car}_KIT{kit:00}_" : null })
            {
                if (pass == null) continue;
                foreach (var s in asset.Solids)
                {
                    string n = s.Name;
                    if (!n.EndsWith("_A") || n.Contains("_DECAL")) continue;
                    if (n.Contains("_DAMAGE") && !(damageParts && n.Contains("_DAMAGE0_"))) continue;
                    if (n.StartsWith(car + "_BASE")) { slots[n] = s; continue; }
                    if (n.StartsWith(pass)) slots[n.Substring(pass.Length)] = s;
                }
            }
            if (look != null && look.Hood > 0 && asset.Solids.Find(x => x.Name == $"{car}_STYLE{look.Hood:00}_HOOD_A") is Solid hood) slots["HOOD_A"] = hood;
            float[] spoilerMarker = null, roofMarker = null;
            foreach (var s in slots.Values)
            {
                spoilerMarker ??= s.Marker("SPOILER");
                roofMarker ??= s.Marker("ROOF_SCOOP");
            }
            if (look != null && look.Spoiler != 0) slots.Remove("SPOILER_A");

            Solid tire = null, frontBrake = null, rearBrake = null;
            CarFlares flares = null;
            foreach (var kv in slots)
            {
                var s = kv.Value;
                string n = s.Name;
                if (n.EndsWith("_FRONT_TIRE_A")) { tire = s; continue; }
                if (n.EndsWith("_FRONT_BRAKE_A")) { frontBrake = s; continue; }
                if (n.EndsWith("_REAR_BRAKE_A")) { rearBrake = s; continue; }
                // UNIVERSAL_SPOILER_BASE (45) is hidden at every LOD unless the spoiler part's level is not 0 (UpdateCarParts tail)
                if (kv.Key == "UNIVERSAL_SPOILER_BASE_A" && (look == null || look.Spoiler == 0)) continue;
                // the hood slot keeps its own group: a carbon hood swaps CARSKIN for CARBONFIBRE / CARBONFIBER
                string group = kv.Key == "HOOD_A" || kv.Key.EndsWith("DAMAGE0_HOOD_A") ? "hood"
                    : kv.Key == "INTERIOR_A" ? "interior" : kv.Key == "DRIVER_A" ? "driver" : kv.Key == "LICENSE_PLATE_A" ? "license" : "";
                var go = info.AddPart(s, body, group, asset);
                if (go && damageParts && n.Contains("_DAMAGE0_")) info.RegisterCopPart(s, go, asset);
                if (go && n.Contains("BRAKELIGHT")) r.BrakeLights.Add(go.GetComponent<MeshRenderer>());
                if (go) (flares ??= r.Root.AddComponent<CarFlares>()).AddMarkers(s, body);
            }

            // aftermarket spoiler and roof scoop on the body's markers
            if (look != null && look.Spoiler > 0 && spoilerMarker != null)
                Mount(info, Game.CarParts.SpoilerFolder(car), Game.CarParts.SpoilerSolid(car, look.Spoiler, look.SpoilerCF), spoilerMarker, body, "spoiler");
            if (look != null && look.Roof > 0 && roofMarker != null)
                Mount(info, "ROOF", Game.CarParts.RoofSolid(look.Roof, look.RoofType, look.RoofCF), roofMarker, body, "roof");

            // the vinyl, base paint and vinyl colours are composited into the skin by CarRenderInfo (CompositeSkin)

            // wheels from chassis/tire attributes: front axle at FRONT_AXLE, rear WHEEL_BASE behind it
            var tireMesh = tire != null ? info.SharedMesh(tire) : null;
            // the slot's models A..E (UpdateCarParts 0x755800; the rear wheel / brake slots take the front models when empty)
            Solid[] tireLods = Lods(asset, tire), fBrakeLods = Lods(asset, frontBrake), rBrakeLods = rearBrake != null ? Lods(asset, rearBrake) : fBrakeLods;
            Solid[] rearTireLods = tireLods;
            var rearTire = asset.Solids.Find(x => x.Name == car + "_KIT00_REAR_TIRE_A");
            if (rearTire != null) rearTireLods = Lods(asset, rearTire);
            float rimScale = 1f;
            int rimSpokes = 0;
            // the FRONT_WHEEL / REAR_WHEEL slot models whose bounding boxes size the drawn wheels (UpdateCarParts 0x755C1D)
            Solid frontWheelSolid = tire, rearWheelSolid = rearTire ?? tire;
            if (look != null && !string.IsNullOrEmpty(look.RimBrand))
            {
                var wheels = Asset("WHEELS");
                AddTextures("WHEELS", wheels);
                var rim = wheels.Solids.Find(x => x.Name == Game.CarParts.RimSolid(look.RimBrand, look.RimStyle, look.RimSize));
                var built = rim != null ? info.SharedMesh(rim) : null;
                if (built != null)
                {
                    frontWheelSolid = rearWheelSolid = rim;
                    // [ours] fallback for cars without an ecar collection: the shared rims are normalised (~0.33 m) and
                    // fitted to the stock tyre (the exe sizes them by WheelRenderGeometry)
                    float stock = tireMesh != null ? tireMesh.Mesh.bounds.extents.x : spec.TireRadiusFront;
                    rimScale = stock / Mathf.Max(0.05f, built.Mesh.bounds.extents.x);
                    tireMesh = built;
                    tireLods = rearTireLods = Lods(wheels, rim);
                    // OnLoaded 0x750B20: the rim part's SPOKE count sets the drawn roll clamp (360 / (2 spokes) - 1)
                    try
                    {
                        var rimPart = Game.CarParts.Db?.PartsFor(car, 66).FirstOrDefault(p => p.ModelHash(0, EaHash.Hash(car)) == rim.Hash);
                        if (rimPart != null && rimPart.Has(SpokeHash)) rimSpokes = rimPart.I(SpokeHash);
                    }
                    catch (Exception) { rimSpokes = 0; }
                }
            }
            var fBrake = frontBrake != null ? info.SharedMesh(frontBrake) : null;
            var rBrake = rearBrake != null ? info.SharedMesh(rearBrake) : null;
            // the exe's drawn wheels (Core WheelRenderGeometry, round 9): ecar TireOffsets / TireSkidWidth / KitWheelOffset
            // against the slot models' boxes; without an ecar collection of its own the car keeps the mesh-fitted rim [ours]
            var geo = new WheelRenderGeometry.Wheel[4];
            bool ecarWheels = false;
            float extraRear = 0f;
            if (frontWheelSolid != null)
            {
                var rearSolid = rearWheelSolid ?? frontWheelSolid;
                float[] mw = { WheelWidth(frontWheelSolid), WheelWidth(rearSolid) };
                float[] mr = { WheelRadius(frontWheelSolid), WheelRadius(rearSolid) };
                var fm = frontWheelSolid.Marker("FRONT_BRAKE");
                var rm = rearSolid.Marker("REAR_BRAKE") ?? fm;
                float[] bm = { fm != null ? fm[13] : 0f, rm != null ? rm[13] : 0f };
                ecarWheels = WheelRenderGeometry.Compute(GameData.Attributes, carFolder, kit, mw, mr, bm, geo);
                extraRear = GameData.Attributes?.Get("ecar", carFolder.ToLowerInvariant())?.Float("ExtraRearTireOffset") ?? 0f;
            }
            float zf = spec.FrontAxle, zr = spec.FrontAxle - spec.WheelBase;
            var hubs = new[]
            {
                new Vector3(-spec.TrackFront / 2, spec.TireRadiusFront, zf),
                new Vector3(spec.TrackFront / 2, spec.TireRadiusFront, zf),
                new Vector3(-spec.TrackRear / 2, spec.TireRadiusRear, zr),
                new Vector3(spec.TrackRear / 2, spec.TireRadiusRear, zr),
            };
            string[] names = { "WheelFL", "WheelFR", "WheelRL", "WheelRR" };
            for (int i = 0; i < 4; i++)
            {
                var pivot = new GameObject(names[i]).transform;
                pivot.SetParent(r.Root.transform, false);
                pivot.localPosition = hubs[i];
                bool right = (i & 1) == 1;
                var spin = new GameObject("Spin").transform;
                spin.SetParent(pivot, false);
                var g = geo[WheelRenderGeometry.GameIndex(i)];
                // the tire model is a right-hand wheel: rim face at game Y~0, tread extending to +Y (inboard); turned to
                // face outward on each side (the left one by 180 deg about the vertical: Render's LeftTireRotateZMatrix,
                // eRotateZ 0x8000)
                var rot = Quaternion.Euler(0, right ? -90 : 90, 0);
                if (tireMesh != null) BuildTire(spin, "Tire", rot, g, ecarWheels, tireMesh, rimScale, info, i < 2 ? tireLods : rearTireLods, i >= 2);
                Transform brakeT = null;
                var brake = i < 2 ? fBrake : rBrake;
                if (brake != null)
                {
                    var b = new GameObject("Brake").transform;
                    b.SetParent(pivot, false);
                    b.localRotation = rot;
                    // brake_trans_pivot: markerY * w - p along the axle when the wheel model has the brake marker, else 0
                    b.localPosition = ecarWheels ? rot * new Vector3(0, 0, g.BrakeY)
                        : -(b.localRotation * new Vector3(0, 0, tireMesh != null ? tireMesh.Mesh.bounds.center.z * rimScale : 0));
                    b.gameObject.layer = CarRender.CarEnvironment.CarLayer;
                    b.gameObject.AddComponent<MeshFilter>().sharedMesh = brake.Mesh;
                    info.Register(b.gameObject.AddComponent<MeshRenderer>(), brake, "brake");
                    info.RegisterWheelLods(b.gameObject, i < 2 ? fBrakeLods : rBrakeLods, i >= 2, true);
                    brakeT = b;
                }
                if (ecarWheels)
                {
                    // drawn centre (game x forward, y left -> Unity z, -x) against the sim's hub; the extra rear wheel
                    // (ExtraRearTireOffset != 0: tire2/3.v3.x += offset, rear model) goes on a second spin
                    var off = pivot.gameObject.AddComponent<CarRender.WheelRenderOffset>();
                    off.Offset = new Vector3(-g.Y - hubs[i].x, 0f, g.X - hubs[i].z);
                    off.Spin = spin;
                    off.Brake = brakeT;
                    off.BrakeBase = brakeT ? brakeT.localPosition : Vector3.zero;
                    if (i >= 2 && extraRear != 0f && tireMesh != null)
                    {
                        var spin2 = new GameObject("SpinExtra").transform;
                        spin2.SetParent(pivot, false);
                        BuildTire(spin2, "TireExtra", rot, g, true, tireMesh, rimScale, info, rearTireLods, true);
                        off.ExtraSpin = spin2;
                        off.ExtraForward = extraRear;
                    }
                    off.Apply();
                }
                r.Wheels[i] = pivot;
            }
            // the kit's DAMAGE0 overlays and decal mounts (RideInfo::SetPart BODY), then the damage behaviour
            info.AddOverlays(asset, kit);
            var damage = CarRender.CarDamage.Attach(r.Root, carFolder, info);
            // parts that come off (the pvehicle's event sequencer: the cops' 'CopCar' engine; car_render.md §11)
            CarRender.CarFragments.Attach(r.Root, carFolder, info, damage);
            Effects.CarEffects.Attach(r, car, slots.Values);
            CarShadow.Attach(r.Root);
            foreach (var sh in r.Root.GetComponentsInChildren<CarShadow>(true)) sh.gameObject.layer = CarRender.CarEnvironment.CarLayer;
            // CarRenderConn::Update: render steering, body / engine animation, flat tyres, road noise (CarRender/CarRenderConn.cs)
            CarRender.CarRenderConn.Attach(r.Root, car, info, body, r.Wheels, rimSpokes);
            return r;
        }

        /// <summary>One tyre model under a spin transform: the ecar scale (r, r, w) about the model origin shifted -p along
        /// the axle (Render's trans_pivot, WheelRenderGeometry), else the mesh-fitted rim centred on the hub [ours].</summary>
        static void BuildTire(Transform spin, string name, Quaternion rot, in WheelRenderGeometry.Wheel g, bool ecar, MeshFactory.Built tireMesh,
            float rimScale, CarRender.CarRenderInfo info, Solid[] lods, bool rear)
        {
            var model = new GameObject(name).transform;
            model.SetParent(spin, false);
            model.localRotation = rot;
            if (ecar)
            {
                // mesh space x = game X, y = game Z, z = game Y (the axle)
                model.localScale = new Vector3(g.RadiusScale, g.RadiusScale, g.WidthScale);
                model.localPosition = rot * new Vector3(0f, 0f, -g.Pivot);
            }
            else
            {
                model.localScale = Vector3.one * rimScale;
                model.localPosition = -(model.localRotation * tireMesh.Mesh.bounds.center) * rimScale;
            }
            model.gameObject.layer = CarRender.CarEnvironment.CarLayer;
            model.gameObject.AddComponent<MeshFilter>().sharedMesh = tireMesh.Mesh;
            info.Register(model.gameObject.AddComponent<MeshRenderer>(), tireMesh, "wheel");
            info.RegisterWheelLods(model.gameObject, lods, rear, false);
        }

        /// <summary>UpdateCarParts 0x755C4A: model width = |bbox max.y - min.y| (game Y, the axle).</summary>
        static float WheelWidth(Solid s) => s == null ? 0f : Mathf.Abs(s.BoundsMax.Y - s.BoundsMin.Y);

        /// <summary>UpdateCarParts 0x755C67: model radius = |bbox max.x - min.x| * 0.5 ([0x8933D4]).</summary>
        static float WheelRadius(Solid s) => s == null ? 0f : Mathf.Abs(s.BoundsMax.X - s.BoundsMin.X) * 0.5f;

        static readonly uint SpokeHash = EaHash.Hash("SPOKE");   // the rim part attribute (CarRenderConn::OnLoaded 0x750B20) [? name]

        /// <summary>The LOD A..E solids of a part (name suffix _A.._E) in an asset.</summary>
        static Solid[] Lods(CarAsset asset, Solid a)
        {
            var r = new Solid[5];
            if (a == null) return r;
            r[0] = a;
            if (!a.Name.EndsWith("_A")) return r;
            string b = a.Name.Substring(0, a.Name.Length - 1);
            for (int k = 1; k < 5; k++) r[k] = asset.Solids.Find(x => x.Name == b + (char)('A' + k));
            return r;
        }

        /// <summary>A shared part (spoiler, roof scoop) placed on a marker of the car body.</summary>
        static void Mount(CarRender.CarRenderInfo info, string folder, string solid, float[] marker, Transform body, string group)
        {
            var asset = Asset(folder);
            AddTextures(folder, asset);
            var s = asset.Solids.Find(x => x.Name == solid);
            if (s == null) { Debug.LogWarning($"NFSMW car: no {solid} in {folder}"); return; }
            // SPOILER_SKIN1 / GLOBAL_SKIN1 / ROOF_SKIN → the skin, CARSKIN → the paint (LightMaterial_Spoiler/Roof are null
            // in CarRenderInfo's ctor, so they fall back to the car skin's material, CarRender.cpp:3064-3072)
            var go = info.AddPart(s, body, group, asset);
            if (!go) return;
            var rot = new float[9];
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++)
                    rot[row * 3 + col] = marker[row * 4 + col];
            WorldStreamer.ApplyMatrix(go.transform, Coords.InstanceMatrix(rot, new Vec3(marker[12], marker[13], marker[14])));
        }
    }
}
