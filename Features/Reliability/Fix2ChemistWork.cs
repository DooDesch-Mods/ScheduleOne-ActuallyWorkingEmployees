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
            if (result) return;
            // Host only: a client's answer feeds nothing, and the host decides what the chemist does next.
            if (!Rel.Gate(FeatureName, Options.FixChemistWorkInProgress.Value)) return;
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

            Rel.EmployeesInto(_employees);
            for (int n = 0; n < _employees.Count; n++)
            {
                Employee emp = _employees[n];
                try
                {
                    Chemist c = emp.TryCast<Chemist>();
                    if (c != null) CheckIdle(c, false);
                }
                catch (Exception e) { Core.Caught("fix2: idle check " + Rel.Name(emp), e); }
            }
        }

        private static readonly List<Employee> _employees = new List<Employee>();
        private static readonly List<NpcBehaviour> _stale = new List<NpcBehaviour>();

        /// <summary>Fills the shared buffer with the enabled work behaviours of the chemist.</summary>
        private static List<NpcBehaviour> EnabledWork(Chemist c)
        {
            var result = _stale;
            result.Clear();
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

            // The reset disables behaviours, which runs vanilla and the lock-release hook; a nested reset is skipped.
            if (!Rel.Enter("fix2.reset")) return;
            try
            {
                _idleSince.Remove(id);
                string line = "[Fix2] reset " + Rel.Name(c) + ": " + stale.Count + " task(s) enabled but none running for " + idle.ToString("F1") + " s ("
                    + string.Join(", ", stale.ConvertAll(Rel.TypeName)) + ").";
                // Logged before the disable calls so a crash inside them leaves this line as the last trace.
                if (force) Core.Log?.Msg(line); else Rel.Action("fix2.reset", line);
                // The disables change the enabled list, so they run on a copy. Fix 3 sees them as one batch: one lock release
                // for the chemist instead of one scan of all placed items per behaviour.
                int count = stale.Count;
                var todo = new NpcBehaviour[count];
                stale.CopyTo(todo);
                Fix3LockLeak.BeginBatch();
                try { for (int i = 0; i < count; i++) todo[i].Disable_Networked(null); }
                finally { Fix3LockLeak.EndBatch(c, "idle reset of " + count + " task(s)"); }
                _resets++;
            }
            finally { Rel.Leave("fix2.reset"); }
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
            if (!Rel.Enter("fix2.isanywork")) return;
            try { Fix2ChemistWork.Apply(__instance, ref __result); }
            catch (Exception e) { Core.Failed("fix2: IsAnyWorkInProgress postfix", e); }
            finally { Rel.Leave("fix2.isanywork"); }
        }
    }
}
