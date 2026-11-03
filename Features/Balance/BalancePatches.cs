using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppScheduleOne.Growing;
using Il2CppScheduleOne.NPCs.Behaviour;
using Il2CppScheduleOne.ObjectScripts;
using ActuallyWorkingEmployees.Config;
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
        private static readonly Dictionary<string, string> _seen = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> _threshold = new Dictionary<string, string>();

        internal static string Seen(FloatOption o) => _seen.TryGetValue(o.Key, out string s) ? s : "not seen yet";
        internal static string SeenThreshold(string which) => _threshold.TryGetValue(which, out string s) ? s : "not seen yet";

        /// <summary>Sets the action's seconds to the option. A vanilla value leaves the game's result untouched.</summary>
        internal static void Fixed(ref float result, FloatOption o)
        {
            if (!Net.IsServer) return;
            try
            {
                if (!Vanilla.Active(o)) return;
                result = o.Value;
                _seen[o.Key] = Eff.F(result) + " s";
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
                _seen[o.Key] = Eff.F(result) + " s (vanilla " + Eff.F(before) + " s, " + Eff.F(o.Value) + " s per unit)";
            }
            catch (Exception e) { Core.Caught("balance: per-unit duration " + o.Key, e); }
        }

        internal static void Threshold(ref float threshold)
        {
            if (!Net.IsServer) return;
            try
            {
                if (Math.Abs(threshold - 0.2f) < 0.001f && Vanilla.Active(Options.BotanistCriticalWaterLevel))
                {
                    threshold = Options.BotanistCriticalWaterLevel.Value;
                    _threshold["critical"] = Eff.F(threshold);
                }
                else if (Math.Abs(threshold - 0.3f) < 0.001f && Vanilla.Active(Options.BotanistWaterLevel))
                {
                    threshold = Options.BotanistWaterLevel.Value;
                    _threshold["water"] = Eff.F(threshold);
                }
            }
            catch (Exception e) { Core.Caught("balance: water threshold", e); }
        }
    }

    [HarmonyPatch(typeof(AddSoilToGrowContainerBehaviour), nameof(AddSoilToGrowContainerBehaviour.GetActionDuration))]
    internal static class Awe_AddSoil_Duration_Patch
    {
        private static void Postfix(ref float __result) => DurationPatches.Fixed(ref __result, Options.BotanistSoilTime);
    }

    [HarmonyPatch(typeof(ApplyAdditiveToGrowContainerBehaviour), nameof(ApplyAdditiveToGrowContainerBehaviour.GetActionDuration))]
    internal static class Awe_Additive_Duration_Patch
    {
        private static void Postfix(ref float __result) => DurationPatches.Fixed(ref __result, Options.BotanistAdditiveTime);
    }

    [HarmonyPatch(typeof(ApplySpawnToMushroomBedBehaviour), nameof(ApplySpawnToMushroomBedBehaviour.GetActionDuration))]
    internal static class Awe_ApplySpawn_Duration_Patch
    {
        private static void Postfix(ref float __result) => DurationPatches.Fixed(ref __result, Options.BotanistMushroomBedTime);
    }

    [HarmonyPatch(typeof(SowSeedInPotBehaviour), nameof(SowSeedInPotBehaviour.GetActionDuration))]
    internal static class Awe_Sow_Duration_Patch
    {
        private static void Postfix(ref float __result) => DurationPatches.Fixed(ref __result, Options.BotanistSowTime);
    }

    [HarmonyPatch(typeof(WaterPotBehaviour), nameof(WaterPotBehaviour.GetActionDuration))]
    internal static class Awe_Water_Duration_Patch
    {
        private static void Postfix(ref float __result) => DurationPatches.Fixed(ref __result, Options.BotanistWaterTime);
    }

    [HarmonyPatch(typeof(HarvestPotBehaviour), nameof(HarvestPotBehaviour.GetActionDuration))]
    internal static class Awe_HarvestPot_Duration_Patch
    {
        private static void Postfix(ref float __result) => DurationPatches.PerUnit(ref __result, Options.BotanistHarvestTimePerUnit);
    }

    [HarmonyPatch(typeof(HarvestMushroomBedBehaviour), nameof(HarvestMushroomBedBehaviour.GetActionDuration))]
    internal static class Awe_HarvestMushroom_Duration_Patch
    {
        private static void Postfix(ref float __result) => DurationPatches.PerUnit(ref __result, Options.BotanistMushroomHarvestTimePerUnit);
    }

    /// <summary>The watering and misting thresholds are literals (0.2 critical, 0.3 normal) passed into this check.</summary>
    [HarmonyPatch(typeof(WaterPotBehaviour), nameof(WaterPotBehaviour.AreTaskConditionsMetForContainer), new Type[] { typeof(GrowContainer), typeof(float) })]
    internal static class Awe_WaterThreshold_Patch
    {
        private static void Prefix(ref float wateringThreshold) => DurationPatches.Threshold(ref wateringThreshold);
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
