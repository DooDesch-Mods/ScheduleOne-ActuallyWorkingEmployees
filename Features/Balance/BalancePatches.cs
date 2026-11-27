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
    /// The grow-container behaviours divide GetActionDuration() by the employee's work speed when an action starts;
    /// <see cref="Awe_GrowAction_Duration_Patch"/> scales that computed wait to the wanted seconds.
    /// One patch class per target: a missing target must not take the others down.
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
    /// Action time of every grow-container behaviour (AWE#54). GrowContainerBehaviour.PerformAction starts a local coroutine
    /// (g__Routine), whose first step computes waitTime = GetActionDuration() / CurrentWorkSpeed and then
    /// counts frames up to it (Il2CppScheduleOne.NPCs.Behaviour/GrowContainerBehaviour.cs, PerformAction).
    ///
    /// Neither neighbour can carry the patch. GetActionDuration is folded in the native build: AddSoil, Additive and Water
    /// (all 10f), and Sow and ApplySpawn (15f), share one function each, so a mod that patches a sibling after AWE detours
    /// the address twice and overflows the stack, as in AWE#51. PerformAction is private and inlined into OnActiveTick,
    /// so a patch on it never runs. The coroutine's MoveNext has its own native code and Unity calls it through the
    /// enumerator interface. After its first step, waitTime is scaled by wanted seconds / vanilla seconds.
    /// </summary>
    [HarmonyPatch]
    internal static class Awe_GrowAction_Duration_Patch
    {
        private const string WaitField = "_waitTime_5__2";

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

        private static PropertyInfo _state, _this, _wait;
        private static MethodBase _target;

        /// <summary>
        /// The MoveNext of the PerformAction coroutine. The interop name of the nested type is generated from its shape,
        /// so the type is found by its waitTime field instead.
        /// </summary>
        private static MethodBase Find()
        {
            if (_target != null) return _target;
            foreach (Type t in typeof(GrowContainerBehaviour).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                PropertyInfo wait = t.GetProperty(WaitField, BindingFlags.Public | BindingFlags.Instance);
                if (wait == null) continue;
                _wait = wait;
                _state = t.GetProperty("__1__state", BindingFlags.Public | BindingFlags.Instance);
                _this = t.GetProperty("__4__this", BindingFlags.Public | BindingFlags.Instance);
                _target = t.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (_state == null || _this == null || _target == null)
                {
                    Core.Log?.Warning("[bal-botanist] the PerformAction coroutine " + t.Name + " lacks state, owner or MoveNext; botanist action times keep their vanilla values.");
                    _target = null;
                    return null;
                }
                return _target;
            }
            Core.Log?.Warning("[bal-botanist] no PerformAction coroutine with " + WaitField + " in GrowContainerBehaviour; botanist action times keep their vanilla values.");
            return null;
        }

        private static bool Prepare(MethodBase original) => original == null ? Find() != null : NativeMethods.AllowPatch(original);

        private static MethodBase TargetMethod() => Find();

        /// <summary>True on the coroutine's first step, the one that computes waitTime.</summary>
        private static void Prefix(object __instance, out bool __state)
        {
            __state = false;
            try { __state = Net.IsServer && (int)_state.GetValue(__instance) == 0; }
            catch (Exception e) { Core.Caught("balance: action time state", e); }
        }

        private static void Postfix(object __instance, bool __state)
        {
            if (!__state) return;
            try { Scale(__instance); }
            catch (Exception e) { Core.Caught("balance: action time", e); }
        }

        private static void Scale(object routine)
        {
            var b = _this.GetValue(routine) as GrowContainerBehaviour;
            if (b == null) { Rel.MsgOnce("duration:null", "[bal-botanist] a PerformAction coroutine has no behaviour, its action time is left unchanged."); return; }
            string cls = Rel.TypeName(b);
            foreach (Target t in _targets)
            {
                if (t.Type.Name != cls) continue;
                float vanilla = b.GetActionDuration();
                float want = vanilla;
                if (t.PerUnit) DurationPatches.PerUnit(ref want, t.Option());
                else DurationPatches.Fixed(ref want, t.Option());
                if (Math.Abs(want - vanilla) < 0.0001f) return;
                if (vanilla <= 0f) { Rel.MsgOnce("duration:zero:" + cls, "[bal-botanist] " + cls + " has a vanilla action time of " + Eff.F(vanilla) + " s, its option is left out."); return; }
                float before = (float)_wait.GetValue(routine);
                float after = before * Math.Max(0f, want) / vanilla;
                _wait.SetValue(routine, after);
#if DEBUG
                float speed = b._botanist != null ? b._botanist.CurrentWorkSpeed : 1f;
                Rel.MsgOnce("duration:wait:" + cls + ":" + Eff.F(want), "[Dev] [bal-botanist] " + cls + " PerformAction wait " + Eff.F(before) + " s -> " + Eff.F(after)
                    + " s: vanilla " + Eff.F(vanilla) + " s, wanted " + Eff.F(want) + " s, work speed x" + Eff.F(speed) + ".");
#endif
                return;
            }
            Rel.MsgOnce("duration:other:" + cls, "[bal-botanist] " + cls + " has no action time option, its value is left unchanged.");
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
