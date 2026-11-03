using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppScheduleOne.Growing;
using Il2CppScheduleOne.NPCs.Behaviour;
using Il2CppScheduleOne.ObjectScripts;
using ActuallyWorkingEmployees.Compat;
using ActuallyWorkingEmployees.Config;
using ActuallyWorkingEmployees.Features.Reliability;
using ActuallyWorkingEmployees.Networking;

namespace ActuallyWorkingEmployees.Features.Balance
{
    /// <summary>
    /// The grow-container behaviours divide GetActionDuration() by the employee's work speed when an action starts, so
    /// a postfix that returns the wanted seconds is all an action time needs. One patch class per target: a missing
    /// target must not take the others down.
    /// </summary>
    internal static class DurationPatches
    {
        // The postfixes run on every action start: they store numbers only, and the text is built when the dev command asks.
        private struct Reading { internal float Result, Before, Value; internal bool PerUnit; }
        private static readonly Dictionary<FloatOption, Reading> _seen = new Dictionary<FloatOption, Reading>();
        private static float _critical = float.NaN, _water = float.NaN;
        private static bool _clampLogged;

        /// <summary>Gap kept between the critical level and the watering level when the pair has to be fixed.</summary>
        private const float PairGap = 0.05f;

        internal static string Seen(FloatOption o)
        {
            if (!_seen.TryGetValue(o, out Reading r)) return "not seen yet";
            if (!r.PerUnit) return Eff.F(r.Result) + " s";
            return Eff.F(r.Result) + " s (vanilla " + Eff.F(r.Before) + " s, " + Eff.F(r.Value) + " s per unit)";
        }

        internal static string SeenThreshold(string which)
        {
            float v = which == "critical" ? _critical : _water;
            return float.IsNaN(v) ? "not seen yet" : Eff.F(v);
        }

        /// <summary>Sets the action's seconds to the option. A vanilla value leaves the game's result untouched.</summary>
        internal static void Fixed(ref float result, FloatOption o)
        {
            if (!Net.IsServer) return;
            try
            {
                if (!Vanilla.Active(o)) return;
                result = o.Value;
                _seen[o] = new Reading { Result = result, Value = o.Value };
            }
            catch (Exception e) { Core.Caught("balance: duration " + o.Key, e); }
        }

        /// <summary>Scales a duration that already contains the unit count: seconds per unit times units.</summary>
        internal static void PerUnit(ref float result, FloatOption o)
        {
            if (!Net.IsServer) return;
            try
            {
                if (!Vanilla.Active(o)) return;
                float before = result;
                result = before * o.Value;
                _seen[o] = new Reading { Result = result, Before = before, Value = o.Value, PerUnit = true };
            }
            catch (Exception e) { Core.Caught("balance: per-unit duration " + o.Key, e); }
        }

        /// <summary>
        /// The critical level and the watering level the botanist uses. The critical level must stay below the watering
        /// level, otherwise a pot counts as urgent and as not yet dry at once; when the pair is inverted the watering level
        /// is raised to the critical level plus a small gap. Returns true when it had to.
        /// </summary>
        private static bool Pair(out float critical, out float water)
        {
            critical = Vanilla.Active(Options.BotanistCriticalWaterLevel) ? Options.BotanistCriticalWaterLevel.Value : Options.BotanistCriticalWaterLevel.Default;
            water = Vanilla.Active(Options.BotanistWaterLevel) ? Options.BotanistWaterLevel.Value : Options.BotanistWaterLevel.Default;
            if (critical < water) { _clampLogged = false; return false; }
            float asked = water;
            water = Math.Min(1f, critical + PairGap);
            if (!_clampLogged)
            {
                _clampLogged = true;
                Core.Log?.Warning("[bal-botanist] Critical water level " + Eff.F(critical) + " is not below water level " + Eff.F(asked)
                    + "; the botanist uses " + Eff.F(water) + " as water level.");
            }
            return true;
        }

        internal static void Threshold(ref float threshold)
        {
            if (!Net.IsServer) return;
            try
            {
                bool isCritical = Math.Abs(threshold - 0.2f) < 0.001f;
                bool isWater = Math.Abs(threshold - 0.3f) < 0.001f;
                if (!isCritical && !isWater) return;
                bool clamped = Pair(out float critical, out float water);
                if (isCritical && Vanilla.Active(Options.BotanistCriticalWaterLevel))
                {
                    threshold = critical;
                    _critical = critical;
                }
                else if (isWater && (clamped || Vanilla.Active(Options.BotanistWaterLevel)))
                {
                    threshold = water;
                    _water = water;
                }
            }
            catch (Exception e) { Core.Caught("balance: water threshold", e); }
        }
    }

    /// <summary>
    /// GetActionDuration of every grow-container behaviour. Several of them compile to the same native function
    /// (AddSoil, Additive and Water all return 10f; Sow and ApplySpawn 15f), so each native address is patched once
    /// (see <see cref="NativeMethods"/>) and the postfix picks the option by the instance's real class.
    /// </summary>
    [HarmonyPatch]
    internal static class Awe_GrowAction_Duration_Patch
    {
        private struct Target { internal Type Type; internal Func<FloatOption> Option; internal bool PerUnit; }

        private static readonly Target[] _targets =
        {
            new Target { Type = typeof(AddSoilToGrowContainerBehaviour), Option = () => Options.BotanistSoilTime },
            new Target { Type = typeof(ApplyAdditiveToGrowContainerBehaviour), Option = () => Options.BotanistAdditiveTime },
            new Target { Type = typeof(ApplySpawnToMushroomBedBehaviour), Option = () => Options.BotanistMushroomBedTime },
            new Target { Type = typeof(SowSeedInPotBehaviour), Option = () => Options.BotanistSowTime },
            new Target { Type = typeof(WaterPotBehaviour), Option = () => Options.BotanistWaterTime },
            new Target { Type = typeof(HarvestPotBehaviour), Option = () => Options.BotanistHarvestTimePerUnit, PerUnit = true },
            new Target { Type = typeof(HarvestMushroomBedBehaviour), Option = () => Options.BotanistMushroomHarvestTimePerUnit, PerUnit = true },
        };

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var all = new List<MethodBase>();
            foreach (Target t in _targets)
            {
                MethodBase m = AccessTools.DeclaredMethod(t.Type, nameof(GrowContainerBehaviour.GetActionDuration), Type.EmptyTypes);
                if (m == null) { MelonLoader.MelonLogger.Warning("[Actually Working Employees] " + t.Type.Name + ".GetActionDuration not found, its action time option has no effect."); continue; }
                all.Add(m);
            }
            return NativeMethods.OnePerAddress("GetActionDuration", all);
        }

        private static void Postfix(GrowContainerBehaviour __instance, ref float __result)
        {
            if (__instance == null) { Rel.MsgOnce("duration:null", "[bal-botanist] GetActionDuration ran without an instance, its value is left unchanged."); return; }
            string cls = Rel.TypeName(__instance);
            foreach (Target t in _targets)
            {
                if (t.Type.Name != cls) continue;
                if (t.PerUnit) DurationPatches.PerUnit(ref __result, t.Option());
                else DurationPatches.Fixed(ref __result, t.Option());
                return;
            }
            // A folded address can also belong to an unrelated method of the game; that caller keeps its value.
            Rel.MsgOnce("duration:other:" + cls, "[bal-botanist] GetActionDuration code is shared with " + cls + ", its value is left unchanged.");
        }
    }

    /// <summary>
    /// The watering thresholds are literals (0.2 critical, 0.3 normal) passed into this check. The same check decides
    /// which pots a botanist may water, so pots a sprinkler covers are refused here when that option is on.
    /// </summary>
    [HarmonyPatch(typeof(WaterPotBehaviour), nameof(WaterPotBehaviour.AreTaskConditionsMetForContainer), new Type[] { typeof(GrowContainer), typeof(float) })]
    internal static class Awe_WaterThreshold_Patch
    {
        private static bool Prefix(GrowContainer container, ref float wateringThreshold, ref bool __result)
        {
            DurationPatches.Threshold(ref wateringThreshold);
            if (!SprinklerCoverage.Refuses(container)) return true;
            __result = false;
            return false;
        }
    }

    internal static class StationPatches
    {
        private static readonly Dictionary<string, string> _seen = new Dictionary<string, string>();
        internal static string Seen(string key) => _seen.TryGetValue(key, out string s) ? s : "not seen yet";
        internal static void Note(string key, string value) { _seen[key] = value; }
    }

    /// <summary>Drying speed: the per-minute multiplier of a drying rack.</summary>
    [HarmonyPatch(typeof(DryingRack), nameof(DryingRack.GetDryMultiplier))]
    internal static class Awe_DryingRack_Speed_Patch
    {
        private static void Postfix(ref float __result)
        {
            if (!Net.IsServer) return;
            try
            {
                float s = StationBalance.Speed(Options.DryingRackSpeed);
                if (Math.Abs(s - 1f) < 1e-4f) return;
                __result *= s;
                StationPatches.Note("rack", "dry multiplier " + Eff.F(__result));
            }
            catch (Exception e) { Core.Caught("balance: drying rack speed", e); }
        }
    }

    /// <summary>Lab oven speed: minutes a cook operation needs.</summary>
    [HarmonyPatch(typeof(OvenCookOperation), nameof(OvenCookOperation.GetCookDuration))]
    internal static class Awe_LabOven_Speed_Patch
    {
        private static void Postfix(ref int __result)
        {
            if (!Net.IsServer) return;
            try
            {
                float s = StationBalance.Speed(Options.LabOvenSpeed);
                if (Math.Abs(s - 1f) < 1e-4f) return;
                __result = Math.Max(1, (int)Math.Round(__result / s));
                StationPatches.Note("oven", "cook duration " + __result + " min");
            }
            catch (Exception e) { Core.Caught("balance: lab oven speed", e); }
        }
    }
}
