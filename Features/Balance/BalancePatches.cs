using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppScheduleOne.Growing;
using Il2CppScheduleOne.NPCs.Behaviour;
using Il2CppScheduleOne.ObjectScripts;
using Il2CppScheduleOne.UI.Stations;
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
        private static bool Prepare(System.Reflection.MethodBase original) => original == null || ActuallyWorkingEmployees.Compat.NativeMethods.AllowPatch(original);
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

    /// <summary>
    /// Drying time left as the rack's UI shows it. DryingOperationUI.UpdatePosition turns the rate that
    /// DryingRackInterface passed to SetDryRate into "Xh Ym until next tier"; this prefix sets that rate to the rack's
    /// multiplier times the drying speed first. DryingRack.GetDryMultiplier itself is not patched: the native build folds
    /// it with Pot.GetTemperatureGrowthMultiplier (the same code), and a mod that patches the pot method after AWE would
    /// detour that address a second time and overflow the stack (AWE#51, Opioid Expansion).
    /// </summary>
    [HarmonyPatch(typeof(DryingOperationUI), nameof(DryingOperationUI.UpdatePosition))]
    internal static class Awe_DryingRack_TimeLeft_Patch
    {
        private static bool Prepare(System.Reflection.MethodBase original) => original == null || ActuallyWorkingEmployees.Compat.NativeMethods.AllowPatch(original);
        private static void Prefix(DryingOperationUI __instance)
        {
            if (!Net.IsServer || __instance == null) return;
            try
            {
                float s = StationBalance.Speed(Options.DryingRackSpeed);
                if (Math.Abs(s - 1f) < 1e-4f) return;
                DryingRack rack = DryingRackInterface.Instance?.Rack;
                if (rack == null) { Rel.MsgOnce("rack-ui:none", "[bal-stations] a drying operation UI updated without an open drying rack; its time left stays vanilla."); return; }
                float m = rack.GetDryMultiplier() * s;
                __instance._dryMultiplier = m;
                StationPatches.Note("rack-ui", "time left at rate " + Eff.F(m));
            }
            catch (Exception e) { Core.Caught("balance: drying rack time left", e); }
        }
    }

    /// <summary>
    /// Drying speed: DryingRack.OnTimePass adds minutes x GetDryMultiplier() to every drying operation, once per game
    /// minute (OnMinPass) and once per time skip. In the IL2CPP build a Harmony patch on GetDryMultiplier does not reach
    /// that call, and GetDryMultiplier shares its native code with Pot.GetTemperatureGrowthMultiplier. This prefix adds the difference, minutes x multiplier x
    /// (speed - 1), to each operation before the game adds its own share. Each operation then gains minutes x multiplier
    /// x speed, and the game's check for the next quality tier (720 drying minutes) sees the scaled time.
    /// </summary>
    [HarmonyPatch(typeof(DryingRack), nameof(DryingRack.OnTimePass))]
    internal static class Awe_DryingRack_TimePass_Patch
    {
        private static float _logged = float.NaN;

        private static bool Prepare(System.Reflection.MethodBase original) => original == null || ActuallyWorkingEmployees.Compat.NativeMethods.AllowPatch(original);

        private static void Prefix(DryingRack __instance, int minutes)
        {
            if (!Net.IsServer) return;
            if (__instance == null) { Rel.MsgOnce("rack:null", "[bal-stations] DryingRack.OnTimePass ran without an instance, its drying is left unchanged."); return; }
            try
            {
                float s = StationBalance.Speed(Options.DryingRackSpeed);
                if (!(Math.Abs(s - _logged) < 1e-4f))
                {
                    _logged = s;
                    Core.Log?.Msg("[bal-stations] drying racks now dry at speed " + Eff.F(s) + " (option " + Eff.F(Options.DryingRackSpeed.Value)
                        + ", station speed " + (Options.EnableStationSpeed.Value ? "on" : "off") + ").");
                }
                if (Math.Abs(s - 1f) < 1e-4f || minutes <= 0) return;
                var ops = __instance.DryingOperations;
                if (ops == null || ops.Count == 0) return;
                float mult = __instance.GetDryMultiplier();
                float extra = minutes * mult * (s - 1f);
                for (int i = 0; i < ops.Count; i++)
                    if (ops[i] != null) ops[i].Time += extra;
                StationPatches.Note("rack", "dry multiplier " + Eff.F(mult * s) + " (speed " + Eff.F(s) + ")");
            }
            catch (Exception e) { Core.Caught("balance: drying rack time pass", e); }
        }
    }

    /// <summary>
    /// Lab oven speed: minutes a cook operation needs. GetCookDuration caches the ingredient's CookTime in the
    /// operation's cookDuration field. In the IL2CPP build OvenCookOperation.IsComplete and IsReady read that field
    /// without calling the patched method, and they decide the ding, the end of the count and the chemist's collect.
    /// So the postfix writes the scaled minutes into the field too, computed from the module time each call, and every
    /// reader (timer, ding, count, the chemist's choice and his collect) sees one value. A speed of 1 writes the module
    /// time back, which also restores a cook that started at another speed.
    /// </summary>
    [HarmonyPatch(typeof(OvenCookOperation), nameof(OvenCookOperation.GetCookDuration))]
    internal static class Awe_LabOven_Speed_Patch
    {
        private static bool Prepare(System.Reflection.MethodBase original) => original == null || ActuallyWorkingEmployees.Compat.NativeMethods.AllowPatch(original);
        private static void Postfix(OvenCookOperation __instance, ref int __result)
        {
            if (!Net.IsServer) return;
            if (__instance == null) { Rel.MsgOnce("oven:null", "[bal-stations] GetCookDuration ran without an instance, its value is left unchanged."); return; }
            try
            {
                var cookable = __instance.Cookable;
                if (cookable == null) { Rel.MsgOnce("oven:nocookable", "[bal-stations] a lab oven operation has no CookableModule, its cook time is left unchanged."); return; }
                int van = cookable.CookTime;
                float s = StationBalance.Speed(Options.LabOvenSpeed);
                int want = Math.Abs(s - 1f) < 1e-4f ? van : Math.Max(1, (int)Math.Round(van / s));
                if (__instance.cookDuration != want) __instance.cookDuration = want;
                if (__result == want) return;
                __result = want;
                StationPatches.Note("oven", "cook duration " + want + " min");
            }
            catch (Exception e) { Core.Caught("balance: lab oven speed", e); }
        }
    }
}
