using System;
using System.Collections.Generic;
using HarmonyLib;
using ActuallyWorkingEmployees.Config;
using NpcBehaviour = Il2CppScheduleOne.NPCs.Behaviour.Behaviour;
#if DEBUG
using ActuallyWorkingEmployees.Debugging;
#endif

namespace ActuallyWorkingEmployees.Features.Reliability
{
    /// <summary>
    /// Fix 2: a chemist that works a cauldron is counted as working.
    ///
    /// Chemist.IsAnyWorkInProgress asks five behaviours but not StartCauldronBehaviour, so while a chemist fills a
    /// cauldron the chemist counts as idle and picks a new task. The postfix adds the cauldron behaviour.
    /// A tick monitor also resets a chemist that has an enabled task but no active behaviour for the configured
    /// time; a running station is never interrupted.
    /// </summary>
    internal sealed class Fix2ChemistWork : IFeature
    {
        internal const string FeatureName = "fix2-chemist-work";
        public string Name => FeatureName;

        private static readonly Dictionary<int, float> _idleSince = new Dictionary<int, float>();
        private static int _patched, _resets;
        private float _next;

        public void Init()
        {
#if DEBUG
            DevConsole.Register("chemwork", "awe chemwork [reset]  (cauldron/work state of every chemist / force the idle reset)", Dev);
#endif
        }

        public void Reset() { _idleSince.Clear(); }

        public string Status =>
            "on=" + (Options.FixChemistWorkInProgress.Value ? 1 : 0) + " cauldronWork=" + _patched + " idleResets=" + _resets;

        /// <summary>Postfix body: true when the cauldron behaviour is running although the vanilla check said no.</summary>
        internal static void Apply(Chemist chemist, ref bool result)
        {
            if (result || !Options.FixChemistWorkInProgress.Value) return;
            var cauldron = chemist.StartCauldronBehaviour;
            if (cauldron == null || !cauldron.Active) return;
            result = true;
            _patched++;
        }

        public void Tick(float dt)
        {
            if (Time.time < _next) return;
            _next = Time.time + 1f;
            if (!Rel.Gate(FeatureName, Options.FixChemistWorkInProgress.Value)) return;

            foreach (Employee emp in Rel.Employees())
            {
                try
                {
                    Chemist c = emp.TryCast<Chemist>();
                    if (c != null) CheckIdle(c, false);
                }
                catch (Exception e) { Core.Caught("fix2: idle check " + Rel.Name(emp), e); }
            }
        }

        private static List<NpcBehaviour> EnabledWork(Chemist c)
        {
            var result = new List<NpcBehaviour>();
            var enabled = c.Behaviour.enabledBehaviours;
            if (enabled == null) return result;
            for (int i = 0; i < enabled.Count; i++)
            {
                NpcBehaviour b = enabled[i];
                if (b != null && Rel.IsWork(b)) result.Add(b);
            }
            return result;
        }

        private static void CheckIdle(Chemist c, bool force)
        {
            int id = c.GetInstanceID();
            if (c.Behaviour == null) { Rel.MsgOnce("fix2:nobeh:" + id, "[" + FeatureName + "] " + Rel.Name(c) + " has no behaviour component."); return; }
            List<NpcBehaviour> stale = EnabledWork(c);
            // A running behaviour, or nothing enabled, is the normal case; the idle timer starts only for "enabled but not running".
            if (c.Behaviour.activeBehaviour != null || stale.Count == 0) { _idleSince.Remove(id); return; }

            if (!_idleSince.TryGetValue(id, out float since)) { _idleSince[id] = Time.time; since = Time.time; }
            float idle = Time.time - since;
            if (!force && idle < Options.ChemistIdleTimeout.Value) return;

            _idleSince.Remove(id);
            foreach (NpcBehaviour b in stale) b.Disable_Networked(null);
            _resets++;
            string line = "[Fix2] reset " + Rel.Name(c) + ": " + stale.Count + " task(s) enabled but none running for " + idle.ToString("F1") + " s ("
                + string.Join(", ", stale.ConvertAll(Rel.TypeName)) + ").";
            if (force) Core.Log?.Msg(line); else Rel.Action("fix2.reset", line);
        }

#if DEBUG
        private static void Dev(string[] args)
        {
            if (!DevConsole.RequireHost("chemwork")) return;
            bool reset = args.Length > 0 && args[0].Equals("reset", StringComparison.OrdinalIgnoreCase);
            int n = 0;
            foreach (Employee emp in Rel.Employees())
            {
                Chemist c = emp.TryCast<Chemist>();
                if (c == null) continue;
                n++;
                var cauldron = c.StartCauldronBehaviour;
                _idleSince.TryGetValue(c.GetInstanceID(), out float since);
                Core.Log?.Msg("[Dev] [Fix2] " + Rel.Name(c) + ": cauldronActive=" + (cauldron != null && cauldron.Active)
                    + " isAnyWorkInProgress=" + c.IsAnyWorkInProgress() + " active=" + Rel.TypeName(c.Behaviour?.activeBehaviour)
                    + " enabledWork=" + EnabledWork(c).Count + " idleFor=" + (since == 0 ? "0" : (Time.time - since).ToString("F1")) + "s");
                if (reset) CheckIdle(c, true);
            }
            Core.Log?.Msg("[Dev] [Fix2] " + n + " chemist(s). " + new Fix2ChemistWork().Status);
        }
#endif
    }

    /// <summary>Chemist.IsAnyWorkInProgress (Il2CppScheduleOne.Employees/Chemist.cs:1064).</summary>
    [HarmonyPatch(typeof(Chemist), nameof(Chemist.IsAnyWorkInProgress))]
    internal static class Awe_Fix2_IsAnyWorkInProgress_Patch
    {
        private static void Postfix(Chemist __instance, ref bool __result)
        {
            try { Fix2ChemistWork.Apply(__instance, ref __result); }
            catch (Exception e) { Core.Failed("fix2: IsAnyWorkInProgress postfix", e); }
        }
    }
}
