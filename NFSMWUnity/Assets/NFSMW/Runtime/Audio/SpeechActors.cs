using System.Collections.Generic;
using NfsMw.Game;
using NfsMw.Game.Police;
using UnityEngine;

namespace NfsMw.Audio
{
    /// <summary>Csis enum values used by the speech AI (EAXSound/SND_GEN/COPSPEECH.hpp; each value is a bit flag, the
    /// i-th name of a type = 1 &lt;&lt; i, like <see cref="NfsMw.Core.SpeechScript.EnumValue"/>).</summary>
    static class Csis
    {
        public const int Normal = 1, High = 2;                                   // intensity
        public const int Yes = 1, No = 2;                                        // yes_no
        public const int OneSuspect = 1, MultipleSuspects = 2;                   // num_suspects
        public const int OneUnit = 1, MultipleUnits = 2;                         // num_units
        public const int Use10Code = 1, DontUse10Code = 2;                       // code
        public const int GenericSpeeder = 1, PossibleWanted = 2, HitAndRun = 4, Reckless = 8, UnitRammed = 16;   // pursuit_type
        public const int BuAll = 1, BuAir = 2, BuRhino = 4;                      // backup_type
        public const int BailLow = 1, BailTraffic = 2, BailRoad = 4, BailDamage = 8, BailHigh = 16;               // bailout_type
        public const int RbGeneric = 1, RbSpikes = 2, RbHeli = 4, RbMultiple = 8;                                  // roadblock_type
        public const int EngageRoadblock = 1, EngageSpikes = 2;                  // roadblock_engage_type
        public const int RollRollingRoadblock = 2, RollBox = 4, RollHerding = 16, RollPit = 32;                    // rolling_strategy_type
        public const int SelfRollingRoadblock = 2, SelfHerding = 4, SelfRam = 8, SelfSideRam = 16, SelfPit = 32, SelfSelfPit = 64;  // self_strategy_type
        public const int WorldGeneric = 1, WorldGuardrail = 2, WorldTrain = 4, WorldSemi = 8, WorldGasStation = 16, WorldSpikeBelt = 32, WorldLowHeat = 64;
        public const int SameStrategy = 1, NewStrategy = 2;
        public const int LostRecently = 1, LostForXTime = 2;                     // time_since_lost
        public const int AboutSelf = 1, AboutOther = 2;                          // self_other
        public const int HeliFlight = 1, HeliFuel = 4, HeliDamage = 8;           // heli_bailout_type
        public const int HeliOffsetFollow = 1, HeliRoadblock = 2;                // heli_self_strategy_type
        public const int HeliLostTunnel = 1, HeliLostGeneric = 8;                // heli_lost_visual
        public const int HazardTunnel = 4;                                       // heli_hazard_alert_type
        public const int FirstEncounter = 1, SubsequentEncounter = 2;
        public const int FirstTime = 1, SubsequentTime = 2;                      // first_subsequent
        public const int MeasureGeneric = 1, MeasureImperial = 2, MeasureMetric = 4;
        public const int SpeedOverLimit = 1;
        public const int PosRight = 1, PosLeft = 2, PosAhead = 4, PosBehind = 8; // position
        public const int RegionCoastal = 2;                                      // region (DriverHistory)
        public const int AddrCollege = 1, AddrCity = 2, AddrCoastal = 4, AddrAlpine = 8, AddrGeneric = 16;         // address_group_type
        public const int EvMultipleDown = 1, EvGasFire = 2, EvHeliDown = 4;      // ev_type
        public const int Eta15 = 1, Eta30 = 2, Eta1m = 4, Eta1m30 = 8, Eta2m = 16, Eta2mPlus = 32;                 // disp_backup_eta
        public const int SpikeLeft = 1, SpikeCenter = 2, SpikeRight = 4;         // spikebelt_position
        public const int JurisState = 1, JurisFederal = 2;
        public const int BatRosewood = 1, BatCoastal = 2, BatCity = 4, BatAlpine = 8, BatSuperPursuit = 16, BatRhino = 32, BatAir = 64;
        public const int CarTypePagani = 8388608;
    }

    /// <summary>Speaker ids (EAXSoundTypes.h): 1 Dispatch, 2 Heli, 3..5 Primary1..3, 6..8 Secondary1..3, 9 Cross.</summary>
    static class Speaker
    {
        public const int Dispatch = 1, Heli = 2, Primary1 = 3, Primary2 = 4, Primary3 = 5, Secondary1 = 6, Secondary2 = 7, Secondary3 = 8, Cross = 9;
    }

    /// <summary>
    /// EAXCharacter (Speech/EAXCharacter.h/.cpp): a voice with a call sign (battalion bit + unit number bit) attached to
    /// a vehicle; position, speed (mph), distance to the player, health, line of sight.
    /// </summary>
    public class SpeechCharacter : ISpeechActor
    {
        protected readonly SpeechAI AI;
        public int SpeakerID { get; set; }
        /// <summary>The vehicle (HSIMABLE): a cop car, the helicopter, or nothing (dispatch).</summary>
        public Component Handle;
        public int Callsign, UnitNumber;
        public Vector3 Pos;
        public float Speed, Health = 1f;
        public float Distance { get; protected set; }
        public bool Destroyed, Active, SuspectLOS;
        public bool HasLOS => SuspectLOS;
        public virtual bool InView => SpeechAI.InCameraView(Pos);

        public SpeechCharacter(SpeechAI ai, int speaker, Component handle, int battalion, int unit)
        {
            AI = ai;
            SpeakerID = speaker;
            Handle = handle;
            Callsign = battalion;
            UnitNumber = unit;
        }

        protected double Now => AI.Now;

        public void Say(string ev, Dictionary<string, int> args) => AI.Schedule(ev, args, this);

        protected Dictionary<string, int> Args(string k1 = null, int v1 = 0, string k2 = null, int v2 = 0, string k3 = null, int v3 = 0)
        {
            var d = new Dictionary<string, int> { ["speaker_id"] = SpeakerID };
            if (k1 != null) d[k1] = v1;
            if (k2 != null) d[k2] = v2;
            if (k3 != null) d[k3] = v3;
            return d;
        }

        public virtual void Reset()
        {
            Active = Destroyed = SuspectLOS = false;
            Health = 0f;
            Distance = 0f;
            Pos = Vector3.zero;
            Speed = 0f;
        }

        /// <summary>EAXCharacter::Update: tracks the vehicle while active; a vanished vehicle deactivates the actor.</summary>
        public virtual void Update()
        {
            if (Handle == null) { Active = false; Handle = null; return; }
            if (Active)
            {
                Pos = Handle.transform.position;
                var rb = Handle.GetComponent<Rigidbody>();
                Speed = rb ? rb.linearVelocity.magnitude * 2.23699f : 0f;
                if (Handle is CopHeli h) Speed = h.Velocity.magnitude * 2.23699f;
                Distance = Vector3.Distance(Pos, AI.PlayerPos);
            }
        }

        public bool IsActive => Active;
        public bool IsDead => Destroyed;

        public virtual void Ack() => Say("Acknowledge", Args("yes_no", Destroyed ? Csis.No : Csis.Yes, "intensity", Csis.Normal));
        /// <summary>EAXCharacter::Deny (the intensity is inverted in the game: High when not high intensity).</summary>
        public virtual void Deny() => Say("Acknowledge", Args("yes_no", Destroyed ? Csis.No : Csis.Yes, "intensity", AI.IsHighIntensity ? Csis.Normal : Csis.High));
        public virtual void InterruptStatic() => Say("Interrupts_StaticInterrupt", new Dictionary<string, int>());
        public virtual void InterruptExpletive() => Say("Interrupts_InterruptRam", Args());
        public virtual void InterruptViolent() => Say("Interrupts_InterruptRamHigh", Args());
        public virtual void InterruptComposedLow() => Say("Interrupts_Interrupt", Args("intensity", Csis.Normal));
        public virtual void InterruptComposedHigh() => Say("Interrupts_Interrupt", Args("intensity", Csis.High));
        public virtual void DriverHistory() => Say("AnytimeEvents_DriverHistory", Args("region", Csis.RegionCoastal));
        public virtual void HeatJump(int heatLevel) => Say("AnytimeEvents_HeatJump", Args("heat_level", heatLevel));
    }

    /// <summary>EAXDispatch (Speech/EAXDispatch.cpp): speaker 1, always "in sight".</summary>
    public sealed class SpeechDispatch : SpeechCharacter
    {
        public SpeechDispatch(SpeechAI ai) : base(ai, Speaker.Dispatch, null, 0, 0) { }
        public override bool InView => true;

        public override void Update() => SuspectLOS = true;

        public void BackupReply(SpeechCop cop, bool yes, int type) =>
            Say("Backup_DispBackupReply", Args3("code", Csis.DontUse10Code, "yes_no", yes ? Csis.Yes : Csis.No, "subject_battalion", cop.Callsign,
                "subject_call_sign_id", cop.UnitNumber, "disp_backup_type", type));

        public void ArrestReply() => Say("Arrest_DispArrestReply", Args());

        public void PursuitUpdate(SpeechCop cop) =>
            Say("AnytimeEvents_DispPursuitUpdate", Args("subject_battalion", cop.Callsign, "subject_call_sign_id", cop.UnitNumber));

        public void PursuitEscalationGeneric() => Say("AnytimeEvents_DispPursEscGen", Args("num_suspects", AI.NumSuspects));

        /// <summary>EAXDispatch::PursuitEscalation: address group from the furthest cop's battalion (with the game's
        /// city/coastal swap), pursuit type from the last observation, last known road and heading.</summary>
        public void PursuitEscalation()
        {
            if (!AI.IsHeadingValid) { PursuitEscalationGeneric(); return; }
            var d = Args();
            var furthest = AI.FindFurthestCop(true);
            int addr = Csis.AddrGeneric;
            if (furthest != null)
                switch (furthest.Callsign)
                {
                    case 1: addr = Csis.AddrCollege; break;      // case values are address_group_type constants in the game
                    case 2: addr = Csis.AddrCoastal; break;      // city (2) -> coastal
                    case 4: addr = Csis.AddrCity; break;         // coastal (4) -> city
                    case 8: addr = Csis.AddrAlpine; break;
                }
            d["address_group_type"] = addr;
            switch (AI.LastObservation)
            {
                case SpeechObservation.Collision_Cop_Suspect: d["pursuit_type"] = Csis.UnitRammed; break;
                case SpeechObservation.Collision_Suspect_Suspect:
                case SpeechObservation.Collision_Suspect_Spikebelt: d["pursuit_type"] = Csis.PossibleWanted; break;
                case SpeechObservation.Collision_Suspect_Traffic:
                case SpeechObservation.Collision_Suspect_Train:
                case SpeechObservation.Collision_Suspect_Semi: d["pursuit_type"] = Csis.HitAndRun; break;
                case SpeechObservation.Collision_Suspect_World:
                case SpeechObservation.Collision_Suspect_Structure:
                case SpeechObservation.Collision_Suspect_Tree:
                case SpeechObservation.Collision_Suspect_Guardrail:
                case SpeechObservation.Collision_Suspect_GasStation: d["pursuit_type"] = Csis.Reckless; break;
                default: d["pursuit_type"] = Csis.GenericSpeeder; break;
            }
            d["num_suspects"] = AI.NumSuspects;
            AI.LastKnownLocation(out int region, out int location);
            d["location_region"] = region;
            d["location"] = location;
            d["direction"] = AI.LastKnownDirectionOrRandom();
            Say("AnytimeEvents_DispPursuitEscalation", d);
        }

        public void BackupUpdate(bool yes) => Say("Backup_DispBackupUpdate", Args("yes_no", yes ? Csis.Yes : Csis.No));

        public void BreakAway()
        {
            AI.LastKnownLocation(out int region, out int location);
            Say("AnytimeEvents_DispBreakAway", Args("location_region", region, "location", location, "direction", AI.LastKnownDirectionOrRandom()));
        }

        public void GoAhead() => Say("Setup_DispGoAhead", Args());
        public void TimeExpired() => Say("AnytimeEvents_DispTimeExpired", Args());

        /// <summary>EAXDispatch::Report911 (the 911 flow itself is not ported; kept for completeness).</summary>
        public void Report911(int pursuitType)
        {
            AI.LastKnownLocation(out int region, out int location);
            var d = Args("pursuit_type", pursuitType, "num_suspects", AI.NumSuspects, "encounter", Csis.FirstEncounter);
            d["location_region"] = region;
            d["location"] = location;
            d["direction"] = AI.LastKnownDirectionOrRandom();
            int addr = region == 2 || region == 4 ? Csis.AddrCoastal : region == 8 ? Csis.AddrCity : region == 1 ? 1 : Csis.AddrGeneric;
            if (AI.Rand(3) == 2) addr = Csis.AddrGeneric;
            d["address_group_type"] = addr;
            Say("AnytimeEvents_Disp911Report", d);
        }

        public void RBUpdate(bool yes) =>
            Say("StaticRoadblock_DispRBUpdate", Args("code", Csis.DontUse10Code, "roadblock_type", AI.NumRoadBlocks > 1 ? Csis.RbMultiple : Csis.RbGeneric,
                "yes_no", yes ? Csis.Yes : Csis.No));

        public void RBReply(SpeechCop cop, bool yes, int type)
        {
            if (type <= 0) type = AI.SpikesEnabled ? Csis.RbSpikes : AI.NumRoadBlocks > 1 ? Csis.RbMultiple : Csis.RbGeneric;
            Say("StaticRoadblock_DispRBReply", Args3("code", Csis.DontUse10Code, "roadblock_type", type, "yes_no", yes ? Csis.Yes : Csis.No,
                "subject_battalion", cop.Callsign, "subject_call_sign_id", cop.UnitNumber));
        }

        public void JurisShift(int jurisdiction) => Say("AnytimeEvents_DispJurisShift", Args("jurisdiction", jurisdiction));

        /// <summary>EAXDispatch::BackupETA: only for an ETA between 10 s and the open-ended "over 2 minutes".</summary>
        public void BackupETA()
        {
            float eta = AI.BackupEta;
            int v;
            if (eta > 10f && eta < 20f) v = Csis.Eta15;
            else if (eta > 20f && eta < 40f) v = Csis.Eta30;
            else if (eta > 40f && eta < 75f) v = Csis.Eta1m;
            else if (eta > 75f && eta < 100f) v = Csis.Eta1m30;
            else if (eta > 100f && eta < 140f) v = Csis.Eta2m;
            else if (eta > 140f) v = Csis.Eta2mPlus;
            else return;
            Say("Backup_DispBUETA", Args("disp_backup_eta", v));
        }

        public void VehicleDescription()
        {
            int color = AI.PlayerCarColor;
            if (color == 0) return;
            Say("Setup_DispVehDescrip", Args("car_color", color, "car_type", AI.PlayerVerbalType));
        }

        public void NoVehicleDescription() => Say("Setup_DispNoVehDescrip", Args());
        public void SubRBReply() => Say("StaticRoadblock_DispSubRB", Args());

        Dictionary<string, int> Args3(string k1, int v1, string k2, int v2, string k3, int v3, string k4, int v4, string k5, int v5)
        {
            var d = Args(k1, v1, k2, v2, k3, v3);
            d[k4] = v4;
            d[k5] = v5;
            return d;
        }
    }

    /// <summary>
    /// EAXCop (Speech/EAXCop.cpp): a pursuit unit. Every method fills its Csis struct the way the game does and
    /// schedules it; the speech AI and its flows decide when.
    /// </summary>
    public class SpeechCop : SpeechCharacter
    {
        public int Rank;
        public bool InFormation, InPosition, Ahead;
        public Vector3 TgtOffset;
        public int TrafficHitCount, NumRammed;
        public double TimeNoLOS, LastRammedTime, TLastActivity, TClosingDist;
        /// <summary>The cop car (null for the helicopter).</summary>
        public CopCar Car => Handle as CopCar;

        public SpeechCop(SpeechAI ai, int speaker, Component handle, int battalion, int unit) : base(ai, speaker, handle, battalion, unit)
        {
            TimeNoLOS = ai.Now;
        }

        public virtual bool IsHeli => false;
        public bool IsCross => SpeakerID == Speaker.Cross;
        public bool IsPrimary => SpeakerID == Speaker.Primary1 || SpeakerID == Speaker.Primary2 || SpeakerID == Speaker.Primary3 ||
                                 SpeakerID == Speaker.Heli || SpeakerID == Speaker.Cross;
        public float TimeLastSeen => (float)(Now - TimeNoLOS);
        public float TimeLastRammed => (float)(Now - LastRammedTime);
        public float TimeLastClosing => (float)(Now - TClosingDist);
        public override bool InView => Handle != null && SpeechAI.InCameraView(Handle.transform.position);

        public void JustHitTraffic() => TrafficHitCount++;
        public void WasRammed() { NumRammed++; LastRammedTime = Now; }

        public override void Reset()
        {
            InFormation = InPosition = false;
            TrafficHitCount = 0;
            TimeNoLOS = 0;
            NumRammed = 0;
            LastRammedTime = TLastActivity = TClosingDist = 0;
            base.Reset();
        }

        /// <summary>EAXCop::Update. The closing-distance timer compares the freshly updated distance with itself, so it
        /// is reset on every update with line of sight (PC 0x707BF0 does the same): AssessOutrun never fires.</summary>
        public override void Update()
        {
            base.Update();
            if (Handle == null)
            {
                InFormation = InPosition = false;
                base.Reset();
                return;
            }
            if (SuspectLOS) TimeNoLOS = Now;
            var car = Car;
            if (car != null && Active)
            {
                InFormation = car.InFormation;
                InPosition = car.InPosition;
                TgtOffset = car.PursuitOffset;
            }
            else
            {
                InFormation = InPosition = false;
                TgtOffset = Vector3.zero;
            }
            if (car != null)
            {
                Destroyed = car.Destroyed;
                Health = Mathf.Clamp01(1f - car.HitPoints);
            }
            if (Active && SuspectLOS && Handle != null)
            {
                float dist = Vector3.Distance(Handle.transform.position, AI.PlayerPos);
                if (dist <= Distance) TClosingDist = Now;
            }
        }

        /// <summary>EAXCop::SetRank: dropping to rank 0 says PrimaryEngage and resets.</summary>
        public bool SetRank(int newRank)
        {
            bool r = false;
            if (newRank == 0 && Rank > 0)
            {
                Say("Setup_PrimaryEngage", Args());
                Reset();
                r = true;
            }
            Rank = newRank;
            return r;
        }

        public void SwapVoices(SpeechCop cop)
        {
            int cs = cop.Callsign, un = cop.UnitNumber, sp = cop.SpeakerID;
            cop.SpeakerID = SpeakerID; cop.Callsign = Callsign; cop.UnitNumber = UnitNumber;
            SpeakerID = sp; Callsign = cs; UnitNumber = un;
        }

        /// <summary>EAXCop::SetActive: activation speech in the strategy/lost foci; deactivation (a unit leaving the
        /// chase) says traffic bailout, heli bailout, unit disabled, or a high/low bailout, and other primaries may deny it.</summary>
        public virtual void SetActive(bool activity)
        {
            if (Active == activity) return;
            Active = activity;
            int focus = AI.Focus;
            if (activity)
            {
                if (focus == SpeechAI.kLost || focus == SpeechAI.kStrategyFlow)
                {
                    if (SuspectLOS)
                    {
                        if (AI.Rand01() > 0.5f) Spotted(); else RegainVisual();
                    }
                    else if (focus == SpeechAI.kStrategyFlow && AI.PursuitState != SpeechAI.kInactive)
                    {
                        // the game requires a roadblock that does not contain this car
                        var rb = AI.Roadblock;
                        if (rb != null && !rb.Cars.Contains(Car))
                        {
                            if (AI.Rand01() > 0.5f) UnitBackupReply(); else BackupArrives();
                        }
                    }
                }
                return;
            }
            if (Now - TLastActivity < 2.0) return;
            AI.Mgr.FlushSpeechForActor(this);
            if (!InFormation && !InPosition) return;
            TLastActivity = Now;
            if (TrafficHitCount >= 2)
            {
                BailoutTraffic();
                TrafficHitCount = 0;
                if (AI.PursuitState != SpeechAI.kActive && AI.PursuitState != SpeechAI.kSearching) return;
                AI.RandomBailoutDeny(this);
                return;
            }
            if (IsHeli) { Bailout(); return; }
            if (Destroyed)
            {
                var other = AI.FindClosestCop(true, true);
                if (other != null) other.UnitDisabled(other.SpeakerID != SpeakerID);
                else UnitDisabled(false);
                return;
            }
            if (Health < AI.Tune.MinHealthForCommentary) { HiBailout(); return; }
            LoBailout();
            if (AI.PursuitState != SpeechAI.kActive && AI.PursuitState != SpeechAI.kSearching) return;
            AI.RandomBailoutDeny(this);
        }

        /// <summary>EAXCop::Collision: an observation becomes commentary from this unit.</summary>
        public void Collision(SpeechObservation type, float force)
        {
            int intensity = force > 0.75f ? Csis.High : Csis.Normal;
            switch (type)
            {
                case SpeechObservation.Collision_Suspect_World: ImpactWorld(); break;
                case SpeechObservation.Collision_Suspect_Traffic: Say("AnytimeEvents_CollWorld_Civi", Args("intensity", intensity)); break;
                case SpeechObservation.Collision_Suspect_Semi: ImpactObject(Csis.WorldSemi); break;
                case SpeechObservation.Collision_Suspect_Guardrail: ImpactObject(Csis.WorldGuardrail); break;
                case SpeechObservation.Collision_Suspect_Train: ImpactObject(Csis.WorldTrain); break;
                case SpeechObservation.Collision_Suspect_GasStation:
                    if (AI.PursuitState == SpeechAI.kActive)
                        Say("AnytimeEvents_CollisionWorld", Args("world_object_type", Csis.WorldGasStation, "num_units", Csis.OneUnit));
                    break;
                case SpeechObservation.Collision_Suspect_Spikebelt:
                    // Impact_Suspect_Spikebelt: cuts the radio for it
                    AI.Mgr.ReleaseResource();
                    AI.Mgr.ClearPlayback();
                    ImpactObject(Csis.WorldSpikeBelt);
                    break;
                case SpeechObservation.Behavior_Suspect_Airborne: SuspectAirborne(intensity); break;
                case SpeechObservation.Behavior_Suspect_Rollover: SuspectRollover(intensity); break;
                case SpeechObservation.Behavior_Suspect_Spunout: SuspectSpunout(intensity); break;
            }
        }

        int NumUnits => AI.InPursuit && AI.NumCopsWithLOS > 1 ? Csis.MultipleUnits : Csis.OneUnit;

        void ImpactWorld()
        {
            int type = AI.Mgr.Count("AnytimeEvents_CollisionWorld") > 0 || AI.IsHighIntensity ? Csis.WorldGeneric : Csis.WorldLowHeat;
            Say("AnytimeEvents_CollisionWorld", Args("world_object_type", type, "num_units", NumUnits));
        }

        void ImpactObject(int type) => Say("AnytimeEvents_CollisionWorld", Args("world_object_type", type, "num_units", NumUnits));

        /// <summary>EAXCop::AttemptVehicleStop: the reason from a recent ram, the last infraction, intensity, traffic hits,
        /// havoc, or whether a spotter call was heard before.</summary>
        public void AttemptVehicleStop()
        {
            int type = Csis.GenericSpeeder;
            var m = AI.Mgr;
            bool heardBefore = m.Count("Setup_AttmptVehStp") > 0 || m.Count("Setup_Spotter") > 0 || m.Count("HeliSpecific_HeliSpotter") > 0;
            if (AI.TimeLastNailedCop < 5f) type = Csis.UnitRammed;
            else if (AI.LastInfraction > 0)
            {
                switch (AI.LastInfraction)
                {
                    case SpeechAI.InfSpeeding: case SpeechAI.InfRacing: type = AI.IsHighIntensity ? Csis.PossibleWanted : Csis.GenericSpeeder; break;
                    case SpeechAI.InfReckless: case SpeechAI.InfOffRoad: type = Csis.Reckless; break;
                    case SpeechAI.InfAssault: type = Csis.UnitRammed; break;
                    case SpeechAI.InfHitAndRun: case SpeechAI.InfDamage: type = Csis.HitAndRun; break;
                    case SpeechAI.InfResist: type = Csis.PossibleWanted; break;
                }
            }
            else if (AI.IsHighIntensity) type = Csis.PossibleWanted;
            else if (AI.Rand01() > 0.5f)
            {
                if (AI.NumTrafficHits > 0) type = Csis.HitAndRun;
                else if (AI.Havoc > 1000) type = Csis.Reckless;
                else type = heardBefore ? Csis.PossibleWanted : Csis.GenericSpeeder;
            }
            else type = heardBefore ? Csis.PossibleWanted : Csis.GenericSpeeder;
            var d = Args("pursuit_type", type, "num_suspects", AI.NumSuspects, "speaker_battalion", Callsign);
            d["speaker_call_sign_id"] = UnitNumber;
            Say("Setup_AttmptVehStp", d);
        }

        public virtual void Spotter() => Say("Setup_Spotter", Args("num_suspects", AI.NumSuspects));
        public void SpotterReply() => Say("Setup_SpotterReply", Args("num_suspects", AI.NumSuspects));
        public void Reply911() => Say("AnytimeEvents_Unit911Reply", Args());

        public void ReinitiatePursuit()
        {
            if (AI.TimeSinceLastChase < AI.Tune.TimeConsideredLostNoLOS)
            {
                if (Ahead) Spotted(); else RegainVisual();
                return;
            }
            Say("Setup_ReInitPursuit", Args("time_since_lost", AI.TimeSinceLastChase < 30f ? Csis.LostRecently : Csis.LostForXTime,
                "num_suspects", AI.NumSuspects));
        }

        static readonly float[] SpeedTest = { 100, 120, 140, 160, 180, 200, 220, 240, 260, 280, 300 };

        /// <summary>EAXCop::VehicleReport : colour, make and a speed bracket ("in excess of 120") in the
        /// player's units, or generic / "over the limit".</summary>
        public void VehicleReport()
        {
            int color = AI.PlayerCarColor;
            if (color == 0 || !AI.IsVehicleTypeOK) return;
            var d = Args("car_type", AI.PlayerVerbalType, "car_color", color);
            float speedo = AI.PlayerSpeed;
            if (Settings.Kmh) { d["measurement"] = Csis.MeasureMetric; speedo *= 1.60931f; }   // MPH2KPH: PC .rdata 0x8A0298 = 1.60931
            else d["measurement"] = Csis.MeasureImperial;
            int ndx = 0;
            while (ndx < 11 && speedo >= SpeedTest[ndx]) ndx++;
            if (ndx == 0)
            {
                d["speed"] = Csis.SpeedOverLimit;
                d["measurement"] = Csis.MeasureGeneric;
            }
            else
            {
                if (AI.Rand01() > 0.5f) d["measurement"] = Csis.MeasureGeneric;
                d["speed"] = 2 << (ndx - 1);
            }
            Say("Setup_VehicleReport", d);
        }

        public void InitiatePursuit() => Say("Setup_InitPursuit", Args("num_suspects", AI.NumSuspects));

        /// <summary>EAXCop::LocationReport: direction and the named road (MiscSpeech::GetLocation); nothing off the named roads.</summary>
        public virtual void LocationReport()
        {
            if (!AI.IsHeadingValid) return;
            if (!Core.CopSpeechRules.GetLocation(AI.PlayerRoad, out int region, out int location)) return;
            var d = Args("num_suspects", AI.NumSuspects, "direction", AI.PlayerDirection, "location_region", region);
            d["location"] = location;
            d["encounter"] = AI.Mgr.Count("Setup_LocationReport") > 1 ? Csis.SubsequentEncounter : Csis.FirstEncounter;
            Say("Setup_LocationReport", d);
        }

        public virtual void SelfStrategy(int formation)
        {
            if (AI.PursuitState != SpeechAI.kActive) return;
            int t;
            switch (formation)
            {
                case 1:
                    switch (AI.Rand(4)) { case 0: t = Csis.SelfPit; break; case 1: t = Csis.SelfSelfPit; break; case 2: t = Csis.SelfSideRam; break; default: t = Csis.SelfRam; break; }
                    break;
                case 2: case 3: case 6:
                    t = AI.Rand01() > 0.5f ? Csis.SelfRollingRoadblock : Csis.SelfHerding;
                    break;
                default: return;
            }
            Say("Setup_SelfStrategy", Args("self_strategy_type", t, "code", Csis.DontUse10Code));
        }

        /// <summary>EAXCop::CallforEV: an emergency vehicle for a gas station fire (type 2), a downed heli, or several units down.</summary>
        public void CallforEV(int type)
        {
            if (AI.PursuitState != SpeechAI.kActive || AI.Focus == SpeechAI.kPursuitFlow) return;
            int ev;
            if (type > 0) ev = type;
            else if (IsHeli) ev = Csis.EvHeliDown;
            else
            {
                if (AI.NumCopsDestroyed <= 1) return;
                ev = Csis.EvMultipleDown;
            }
            Say("AnytimeEvents_CallForEV", Args("ev_type", ev));
        }

        public void InitialCallForBackup()
        {
            if (AI.AreRacersNearby) Say("Setup_InitialCallForBU_MS", Args());
            else Say("Setup_InitialCallForBU", Args("code", Csis.DontUse10Code));
        }

        /// <summary>EAXCop::GetBackupTypeFromDispatch: 8 air support, 16 Rhino, 1/2/4/32/64 all units.</summary>
        static int BackupType(int type)
        {
            switch (type)
            {
                case 8: return Csis.BuAir;
                case 16: return Csis.BuRhino;
                case 1: case 2: case 4: case 32: case 64: return Csis.BuAll;
                default: return 0;
            }
        }

        public void CallForBackup(int type) => Say("Backup_CallForBU", Args("code", Csis.DontUse10Code, "backup_type", BackupType(type)));
        public void BackupReminder(int type) => Say("Backup_BUReminder", Args("code", Csis.DontUse10Code, "backup_type", BackupType(type)));
        public void BackupArrives() => Say("Backup_BUArrives", Args("speaker_battalion", Callsign, "speaker_call_sign_id", UnitNumber));
        public void PrimaryEngage() => Say("Setup_PrimaryEngage", Args("speaker_battalion", Callsign, "speaker_call_sign_id", UnitNumber));
        public void UnitBackupReply() => Say("Backup_UnitBUReply", Args());
        public void NegativeBackupReply() => Say("Backup_NegativeBUReply", Args());

        public void InitiateStrategy(int formation)
        {
            int t;
            switch (formation)
            {
                case 1: t = Csis.RollPit; break;
                case 2: t = Csis.RollBox; break;
                case 3: t = Csis.RollRollingRoadblock; break;
                case 6: t = Csis.RollHerding; break;
                default: return;
            }
            Say("RollingStrategy_InitStrategy", Args("code", Csis.DontUse10Code, "rolling_strategy_type", t));
        }

        /// <summary>EAXCop::CallToPosition: the called unit's slot side from this unit's target offset (x right, z ahead).</summary>
        public void CallToPosition(SpeechCop cop)
        {
            float zmag = Mathf.Abs(TgtOffset.z), xmag = Mathf.Abs(TgtOffset.x);
            int pos = xmag > zmag ? (TgtOffset.x > 0f ? Csis.PosRight : Csis.PosLeft) : (TgtOffset.z > 0f ? Csis.PosAhead : Csis.PosBehind);
            var d = Args("intensity", AI.IsHighIntensity ? Csis.High : Csis.Normal, "subject_battalion", cop.Callsign, "subject_call_sign_id", cop.UnitNumber);
            d["position"] = pos;
            Say("RollingStrategy_CallToPosition", d);
        }

        int Intensity => AI.IsHighIntensity ? Csis.High : Csis.Normal;

        public void CallToPositionReminder() => Say("RollingStrategy_CallToPositionRem", Args("intensity", Intensity));
        public void StrategyExecute() => Say("RollingStrategy_StrategyExecute", Args("intensity", Intensity));
        public void IntentToRam() => Say("AnytimeEvents_IntentToRam", Args("intensity", Intensity));
        public void AnticipateSuccess() => Say("Outcome_AnticipateSuccess", Args());
        public void AnticipateFail() => Say("Outcome_AnticipateFail", Args());
        public void LostSuspect() => Say("AnytimeEvents_LostSuspect", Args("intensity", Intensity));
        public void Arrest() => Say("Arrest_Arrest", Args("intensity", Intensity));
        public virtual void LostVisual() => Say("AnytimeEvents_LostVisual", Args("intensity", Intensity));
        public void RegainVisual() => Say("AnytimeEvents_RegainVisual", Args("intensity", Intensity));
        public void OutcomeFail(int intensity = -1) => Say("Outcome_OutcomeFail", Args("intensity", intensity < 0 ? Intensity : intensity));
        public void StrategyReset(bool newStrategy) => Say("Outcome_StrategyReset", Args("intensity", Intensity, "same_new", newStrategy ? Csis.NewStrategy : Csis.SameStrategy));
        public void SwarmingReply() => Say("ExtraCops_SwarmingReply", new Dictionary<string, int>());

        public void Bullhorn()
        {
            if (AI.IsVehicleTypeOK) Say("Setup_Bullhorn", Args());
        }

        public void PreBullhorn() => Say("Setup_BullhornPrefix", Args());

        public virtual void BullhornArrest()
        {
            if (AI.IsVehicleTypeOK) Say("Arrest_BullhornArrest", Args("intensity", Intensity));
        }

        public void SuspectBehavior() => Say("AnytimeEvents_SuspectBehaviour", Args("num_suspects", AI.NumSuspects));
        public void SuspectConfirmed() => Say("Setup_SuspectConfirmed", Args("num_suspects", AI.NumSuspects));
        public void SuspectOutrun() => Say("AnytimeEvents_SuspectOutrun", Args("intensity", Intensity));
        public void SuspectUTurn() => Say("AnytimeEvents_SuspectUTurn", Args("intensity", Intensity));
        public void FocusChange() => Say("AnytimeEvents_FocusChange", Args("intensity", Intensity));
        public void SuspectRollover(int intensity) => Say("AnytimeEvents_CollWorld_Flip", Args("intensity", intensity));
        public void SuspectAirborne(int intensity) => Say("AnytimeEvents_CollWorld_Air", Args("intensity", intensity));
        public void SuspectSpunout(int intensity) => Say("AnytimeEvents_CollWorld_Spin", Args("intensity", intensity));
        public void SuspectBrake() => Say("AnytimeEvents_SuspectBrake", Args());
        public void UnitDisabled(bool other) => Say("AnytimeEvents_UnitDisabled", Args("self_other", other ? Csis.AboutOther : Csis.AboutSelf, "intensity", Intensity));
        public void PursuitUpdateReply() => Say("AnytimeEvents_PursuitUpdateRep", Args());

        bool BailoutAllowed => AI.PursuitDuration >= AI.Tune.MinPursuitDurationForBailouts;
        public virtual void Bailout() { if (BailoutAllowed) Say("AnytimeEvents_Bailout", Args("bailout_type", Csis.BailDamage)); }
        public void LoBailout() { if (BailoutAllowed) Say("AnytimeEvents_Bailout", Args("bailout_type", Csis.BailLow)); }
        public void HiBailout() { if (BailoutAllowed) Say("AnytimeEvents_Bailout", Args("bailout_type", Csis.BailHigh)); }
        public void BailoutBadRoad() { if (BailoutAllowed) Say("AnytimeEvents_Bailout", Args("bailout_type", Csis.BailRoad)); }
        public void BailoutTraffic() { if (BailoutAllowed) Say("AnytimeEvents_Bailout", Args("bailout_type", Csis.BailTraffic)); }
        public void DenyBailout() => Say("AnytimeEvents_BailoutDeny", Args());

        public void CallForRB() => Say("StaticRoadblock_CallForRB", Args("code", Csis.DontUse10Code, "roadblock_type", AI.SpikesEnabled ? Csis.RbSpikes : Csis.RbGeneric));
        public void CallForSubRB() => Say("StaticRoadblock_CallForRB_sub", Args());
        public void RBReminder() => Say("StaticRoadblock_RBReminder", Args("code", Csis.DontUse10Code));
        public void NegRBReply() { if (!IsHeli) Say("StaticRoadblock_NegativeRBReply", Args()); }

        public void RBApproach()
        {
            var rb = AI.Roadblock;
            if (rb == null) return;
            Say("StaticRoadblock_RBApproach", Args("roadblock_type", rb.SpikeStrips > 0 ? Csis.RbSpikes : Csis.RbGeneric));
        }

        public void RBEngage(bool spikesHit)
        {
            if (AI.Roadblock == null) return;
            Say("StaticRoadblock_RBEngage", Args("roadblock_engage_type", spikesHit ? Csis.EngageSpikes : Csis.EngageRoadblock));
        }

        public void RBAverted() => Say("StaticRoadblock_RBAverted", Args());
        public void PursuitApproaching() => Say("StaticRoadblock_PursuitApproaching", Args("num_suspects", AI.NumSuspects));
        public void HeadOn(int intensity) => Say("Interrupts_InterruptRam_HO", Args("intensity", intensity));
        public void TBoned(int intensity) => Say("Interrupts_InterruptRam_TB", Args("intensity", intensity));
        public void SideSwiped(int intensity) => Say("Interrupts_InterruptRam_SS", Args("intensity", intensity));
        public void RearEnded(int intensity) => Say("Interrupts_InterruptRam_RE", Args("intensity", intensity));

        /// <summary>EAXCop::Spotted: intensity High when this unit is doing over 80 mph.</summary>
        public void Spotted() => Say("AnytimeEvents_Spotted", Args("intensity", Speed > 80f ? Csis.High : Csis.Normal));

        public void DirectionChange()
        {
            int dir = AI.PlayerDirection;
            if (dir == 0) return;
            Say("AnytimeEvents_DirectionHigh", Args("direction", dir));
        }

        public void CallForSwarming() => Say("Backup_CallForSwarming", Args());
        public void SpotterWanted() => Say("Setup_SpotterWanted", Args());

        public void Offroad(int id, bool subsequent) =>
            Say("AnytimeEvents_OffroadMoment", Args("offroad_moment_id", id, "first_subsequent", subsequent ? Csis.SubsequentTime : Csis.FirstTime));

        public void WeatherReport() => Say("AnytimeEvents_WeatherReport", Args());
    }

    /// <summary>EAXAirSupport (Speech/EAXAirSupport.cpp): speaker 2, call sign Air Support 1; fuel warnings.</summary>
    public sealed class SpeechHeli : SpeechCop
    {
        const float FuelCritical = 8f;   // HELI_FUEL_CRITICAL_TIME

        public SpeechHeli(SpeechAI ai, CopHeli heli) : base(ai, Speaker.Heli, heli, Csis.BatAir, 1) { }

        public CopHeli Heli => Handle as CopHeli;
        public override bool IsHeli => true;

        public override void Update()
        {
            base.Update();
            var h = Heli;
            if (h != null && AI.Focus != SpeechAI.kPursuitFlow && Active && h.Fuel < FuelCritical) IntentToBail();
        }

        int CauseOfBailout()
        {
            int r = Csis.HeliFuel;
            if (Health < 1f) r = Csis.HeliDamage;
            return r;
        }

        public override void SelfStrategy(int type) => Say("HeliSpecific_HeliSelfStrategy", Args("heli_self_strategy_type", type));
        public void JoinRB() => Say("HeliSpecific_HeliSelfStrategy", Args("heli_self_strategy_type", Csis.HeliRoadblock));

        public override void LostVisual() =>
            Say("HeliSpecific_HeliLostVisual", Args("heli_lost_visual", AI.PlayerInTunnel ? Csis.HeliLostTunnel : Csis.HeliLostGeneric));

        public void IntentToBail() => Say("HeliSpecific_HeliIntentToBail", Args("heli_bailout_type", CauseOfBailout()));
        public override void Bailout() => Say("HeliSpecific_HeliBailout", Args("heli_bailout_type", CauseOfBailout()));
        public void Swarming() => Say("HeliSpecific_HeliSwarming", Args());
        public override void Spotter() => Say("HeliSpecific_HeliSpotter", Args());
        public void HazardAlert(int type) => Say("HeliSpecific_HeliHazardAlert", Args("heli_hazard_alert_type", type));

        public override void BullhornArrest()
        {
            if (AI.IsVehicleTypeOK) Say("HeliSpecific_HeliBullhornArrest", Args());
        }

        public void Quadrant() => Say("HeliSpecific_HeliQuadrent", Args());
        public void QuadrantMoving() => Say("HeliSpecific_HeliQuadrentMoving", Args());
    }
}
