using System;
using System.Collections.Generic;
using ActuallyWorkingEmployees.Config;
using ActuallyWorkingEmployees.Features.Diagnostics;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.NPCs.Behaviour;
using Il2CppScheduleOne.ObjectScripts;
using NpcBehaviour = Il2CppScheduleOne.NPCs.Behaviour.Behaviour;
#if DEBUG
using ActuallyWorkingEmployees.Debugging;
#endif

namespace ActuallyWorkingEmployees.Features.Reliability
{
    /// <summary>
    /// Fix 7: an employee that stands at its work and never finishes it (AWE#62).
    ///
    /// The station behaviours run their work in a coroutine and set an in-progress flag for it (cookRoutine,
    /// WorkInProgress, PackagingInProgress, a non-idle state). When that coroutine dies, for example on an exception,
    /// the flag stays set, the behaviour stays active and keeps marking the employee as working, and the employee stands
    /// next to the station forever. The phone app then shows "Working." for an employee that does nothing. Fix 1 covers
    /// walks that never arrive; this covers the employee that does not walk at all.
    ///
    /// Progress is anything the work changes: the employee moves, the active task changes, or the items the employee
    /// carries or the station holds change. When none of that happens for the configured time, the task is released the
    /// way the game ends a task (Disable on the host, inside one Fix 3 lock batch), and the employee picks its next task.
    /// A task that stalls again right after a restart doubles the wait, up to four times, so a station that cannot work
    /// is not restarted in a loop.
    /// </summary>
    internal sealed class Fix7StuckWork : IFeature
    {
        internal const string FeatureName = "fix7-stuck-work";
        public string Name => FeatureName;

        private sealed class Watch { internal IntPtr Task; internal Vector3 Spot; internal float Since; internal long Print; internal int Strikes; internal float LastRestart = -1e6f; }

        private static readonly Dictionary<int, Watch> _watch = new Dictionary<int, Watch>();
        private static readonly List<Employee> _employees = new List<Employee>();
        private static readonly List<NpcBehaviour> _buffer = new List<NpcBehaviour>();
        private static int _checks, _restarts, _failed;
        private float _next;

        public void Init()
        {
#if DEBUG
            DevConsole.Register("stuckwork", "awe stuckwork [kill|restart]  (stuck-work state of every employee / end the running work routine of the first employee working in place, so it stands with its task active / restart that employee's task now)", Dev);
#endif
        }

        public void Reset() { _watch.Clear(); }

        public string Status => "on=" + (Options.FixStuckWork.Value ? 1 : 0) + " checks=" + _checks + " restarts=" + _restarts + " failed=" + _failed;

        public void Tick(float dt)
        {
            if (Time.time < _next) return;
            _next = Time.time + 1f;
            if (!Rel.Gate(FeatureName, Options.FixStuckWork.Value)) return;

            Rel.EmployeesInto(_employees);
            for (int i = 0; i < _employees.Count; i++)
            {
                Employee emp = _employees[i];
                try { Check(emp); }
                catch (Exception e) { Core.Caught("fix7: check " + Rel.Name(emp), e); }
            }
        }

        private static void Check(Employee emp)
        {
            int id = emp.GetInstanceID();
            if (emp.Behaviour == null) { Rel.MsgOnce("fix7:nobeh:" + id, "[" + FeatureName + "] " + Rel.Name(emp) + " has no behaviour component."); return; }
            if (emp.Movement == null) { Rel.MsgOnce("fix7:nomove:" + id, "[" + FeatureName + "] " + Rel.Name(emp) + " has no movement component."); return; }

            NpcBehaviour active = emp.Behaviour.activeBehaviour;
            // No task, or a walk (Fix 1 watches walks): nothing to watch. Normal states, so no log line.
            if (active == null || !active.Active || !Rel.IsWork(active) || emp.Movement.IsMoving)
            {
                if (_watch.TryGetValue(id, out Watch idle)) idle.Task = IntPtr.Zero;
                return;
            }

            _checks++;
            Vector3 here = emp.transform.position;
            long print = Fingerprint(emp, active);
            if (!_watch.TryGetValue(id, out Watch w)) { w = new Watch(); _watch[id] = w; }
            if (w.Task != active.Pointer || w.Print != print || Vector3.Distance(here, w.Spot) > Options.StandstillRadius.Value)
            {
                w.Task = active.Pointer; w.Print = print; w.Spot = here; w.Since = Time.time;
                return;
            }

            float allowed = Allowed(emp, active);
            // A stall soon after the last restart is the same stall: wait longer before the next one.
            if (Time.time - w.LastRestart > allowed * 3f) w.Strikes = 0;
            float wait = allowed * (1 << Math.Min(w.Strikes, 2));
            float still = Time.time - w.Since;
            if (still < wait) return;

            string why = Rel.TypeName(active) + " active for " + still.ToString("F0") + " s without progress (limit " + wait.ToString("F0") + " s)";
            if (Restart(emp, why, false))
            {
                w.Strikes++;
                w.LastRestart = Time.time;
            }
            w.Since = Time.time;
        }

        /// <summary>
        /// Seconds a task may stand without progress. Harvests run as one long action of the configured seconds per unit;
        /// a slower work speed stretches every action.
        /// </summary>
        private static float Allowed(Employee emp, NpcBehaviour active)
        {
            float allowed = Options.StuckWorkSeconds.Value;
            string type = Rel.TypeName(active);
            if (type.StartsWith("Harvest", StringComparison.Ordinal))
            {
                float perUnit = Math.Max(Options.BotanistHarvestTimePerUnit.Value, Options.BotanistMushroomHarvestTimePerUnit.Value);
                if (perUnit <= 0f) perUnit = 1f;
                allowed = Math.Max(allowed, perUnit * 30f + 30f);
            }
            var speed = emp.WorkSpeedController;
            if (speed != null && speed.Value > 0f && speed.Value < 1f) allowed /= Math.Max(speed.Value, 0.1f);
            return allowed;
        }

        /// <summary>Item counts the work changes: what the employee carries and what its station holds.</summary>
        private static long Fingerprint(Employee emp, NpcBehaviour active)
        {
            long print = SlotSum(emp.Inventory?.ItemSlots);
            var slots = StationSlots(active);
            if (slots != null) print = print * 1000003L + SlotSum(slots) + 1;
            return print;
        }

        private static long SlotSum(Il2CppSystem.Collections.Generic.List<ItemSlot> slots)
        {
            if (slots == null) return 0;
            long sum = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                ItemSlot s = slots[i];
                if (s == null) continue;
                sum = sum * 31 + s.Quantity;
            }
            return sum;
        }

        /// <summary>The item slots of the station a work behaviour works at, or null for behaviours without one.</summary>
        private static Il2CppSystem.Collections.Generic.List<ItemSlot> StationSlots(NpcBehaviour b)
        {
            var pack = b.TryCast<PackagingStationBehaviour>(); if (pack != null) return pack.Station?.ItemSlots;
            var press = b.TryCast<BrickPressBehaviour>(); if (press != null) return press.Press?.ItemSlots;
            var cauldron = b.TryCast<StartCauldronBehaviour>(); if (cauldron != null) return cauldron.Station?.ItemSlots;
            var chem = b.TryCast<StartChemistryStationBehaviour>(); if (chem != null) return chem.targetStation?.ItemSlots;
            var oven = b.TryCast<StartLabOvenBehaviour>(); if (oven != null) return oven.targetOven?.ItemSlots;
            var finish = b.TryCast<FinishLabOvenBehaviour>(); if (finish != null) return finish.targetOven?.ItemSlots;
            var mix = b.TryCast<StartMixingStationBehaviour>(); if (mix != null) return mix.targetStation?.ItemSlots;
            var rack = b.TryCast<StartDryingRackBehaviour>(); if (rack != null) return rack.Rack?.ItemSlots;
            var stop = b.TryCast<StopDryingRackBehaviour>(); if (stop != null) return stop.Rack?.ItemSlots;
            return null;
        }

        /// <summary>
        /// Ends the employee's work tasks on the host and lets it pick the next one. Used by the watchdog and by the whip.
        /// Returns false when nothing was restarted; every refusal is logged.
        /// </summary>
        internal static bool Restart(Employee emp, string why, bool quiet)
        {
            if (emp == null || emp.Behaviour == null) { Core.Log?.Warning("[Fix7] restart refused: no employee or no behaviour component."); return false; }
            if (!Rel.Enter("fix7.restart")) { Core.Log?.Warning("[Fix7] restart of " + Rel.Name(emp) + " skipped: a restart is already running."); return false; }
            try
            {
                _buffer.Clear();
                NpcBehaviour active = emp.Behaviour.activeBehaviour;
                if (active != null && Rel.IsWork(active)) _buffer.Add(active);
                var enabled = emp.Behaviour.enabledBehaviours;
                for (int i = 0; enabled != null && i < enabled.Count; i++)
                {
                    NpcBehaviour b = enabled[i];
                    if (b != null && Rel.IsWork(b) && !_buffer.Exists(x => x.Pointer == b.Pointer)) _buffer.Add(b);
                }
                Vector3 dest = emp.Movement != null ? emp.Movement.CurrentDestination : Vector3.zero;
                float dist = emp.Movement != null && emp.Movement._hasDestination ? Vector3.Distance(emp.transform.position, dest) : -1f;
                string line = "[Fix7] restarted " + Rel.Name(emp) + " (" + WorkIssueInfo.RoleOf(emp) + "): " + why
                    + (dist >= 0f ? ", " + dist.ToString("F1") + " m from its destination" : ", no destination")
                    + "; released " + _buffer.Count + " task(s)" + (_buffer.Count > 0 ? " (" + string.Join(", ", _buffer.ConvertAll(Rel.TypeName)) + ")" : "") + ".";
                // Logged before the disable calls so a crash inside them leaves this line as the last trace.
                if (quiet) Core.Log?.Msg(line); else Core.Log?.Warning(line);
                emp.Movement?.Stop();
                int count = _buffer.Count;
                var todo = _buffer.ToArray();
                Fix3LockLeak.BeginBatch();
                try { for (int i = 0; i < count; i++) todo[i].Disable_Networked(null); }
                finally { Fix3LockLeak.EndBatch(emp, "stuck-work restart of " + count + " task(s)"); }
                _restarts++;
                return true;
            }
            catch (Exception e)
            {
                _failed++;
                Core.Failed("fix7: restart " + Rel.Name(emp), e);
                return false;
            }
            finally { Rel.Leave("fix7.restart"); }
        }

#if DEBUG
        private static void Dev(string[] args)
        {
            if (!DevConsole.RequireHost("stuckwork")) return;
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            Employee first = null;
            foreach (Employee emp in Rel.Employees())
            {
                NpcBehaviour a = emp.Behaviour?.activeBehaviour;
                bool work = a != null && a.Active && Rel.IsWork(a);
                _watch.TryGetValue(emp.GetInstanceID(), out Watch w);
                bool watched = w != null && w.Task != IntPtr.Zero;
                Core.Log?.Msg("[Dev] [Fix7] " + Rel.Name(emp) + " (" + WorkIssueInfo.RoleOf(emp) + "): behaviour=" + (a == null ? "none" : Rel.TypeName(a)) + " work=" + work
                    + " moving=" + (emp.Movement != null && emp.Movement.IsMoving) + " stillFor=" + (watched ? (Time.time - w.Since).ToString("F0") : "0")
                    + "s limit=" + (work ? Allowed(emp, a).ToString("F0") : "-") + "s strikes=" + (w == null ? 0 : w.Strikes));
                if (first == null && work && emp.Movement != null && !emp.Movement.IsMoving) first = emp;
            }
            Core.Log?.Msg("[Dev] [Fix7] " + new Fix7StuckWork().Status);
            if (mode == "") return;
            if (first == null) { Core.Log?.Warning("[Dev] awe stuckwork " + mode + ": no employee works in place right now."); return; }
            NpcBehaviour task = first.Behaviour.activeBehaviour;
            if (mode == "kill")
            {
                // What an exception inside the work coroutine leaves behind: the routine is gone, the flags and the active
                // task stay.
                task.StopAllCoroutines();
                first.Movement.Stop();
                Core.Log?.Msg("[Dev] [Fix7] killed the work routine of " + Rel.Name(first) + " (" + Rel.TypeName(task) + "); the task stays active.");
            }
            else if (mode == "restart") Restart(first, "dev command", true);
            else Core.Log?.Warning("[Dev] awe stuckwork: unknown mode '" + mode + "'.");
        }
#endif
    }
}
