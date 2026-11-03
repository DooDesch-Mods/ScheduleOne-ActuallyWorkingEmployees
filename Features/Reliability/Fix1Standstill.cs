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
    /// Fix 1: employees that keep walking nowhere.
    ///
    /// A walk that ends as Partial (closest reachable point, far from the target) resets the vanilla failure counter,
    /// so the vanilla warp after five failures never fires and the employee retries the same walk forever. The
    /// WalkCallback postfix counts such walks itself and warps the employee to the target at the configured count.
    /// A tick monitor catches the rest: an employee on a work task that stays within the standstill radius for the
    /// configured time, far from its destination, is warped to it. After the configured attempts it is left alone
    /// with a warning until it moves.
    /// </summary>
    internal sealed class Fix1Standstill : IFeature
    {
        internal const string FeatureName = "fix1-standstill";
        public string Name => FeatureName;

        private sealed class Still { internal Vector3 Spot; internal float Since; internal int Tries; internal bool GaveUp; }

        private static readonly Dictionary<int, Still> _still = new Dictionary<int, Still>();
        private static readonly Dictionary<int, int> _partial = new Dictionary<int, int>();
        private static int _checks, _warps, _gaveUp, _partialWarps, _partialsCounted, _refused;
        private float _next;

        public void Init()
        {
#if DEBUG
            DevConsole.Register("standstill", "awe standstill [warp|partial]  (state of every employee / force the fix)", Dev);
#endif
        }

        public void Reset()
        {
            _still.Clear();
            _partial.Clear();
            Rel.ResetActionCounts();
        }

        public string Status =>
            "on=" + (Options.FixStandstill.Value ? 1 : 0) + (Options.FixPartialWalkLoop.Value ? 1 : 0)
            + " checks=" + _checks + " warps=" + _warps + " gaveup=" + _gaveUp
            + " partial=" + _partialsCounted + " partialwarps=" + _partialWarps + " refused=" + _refused;

        public void Tick(float dt)
        {
            if (Time.time < _next) return;
            _next = Time.time + 0.5f;
            if (!Rel.Gate(FeatureName, Options.FixStandstill.Value)) return;

            Rel.EmployeesInto(_employees);
            for (int i = 0; i < _employees.Count; i++)
            {
                Employee emp = _employees[i];
                try { Check(emp); }
                catch (Exception e) { Core.Caught("fix1: check " + Rel.Name(emp), e); }
            }
        }

        private static readonly List<Employee> _employees = new List<Employee>();

        private static void Check(Employee emp)
        {
            int id = emp.GetInstanceID();
            if (emp.Behaviour == null) { Rel.MsgOnce("fix1:nobeh:" + id, "[" + FeatureName + "] " + Rel.Name(emp) + " has no behaviour component."); return; }
            if (emp.Movement == null) { Rel.MsgOnce("fix1:nomove:" + id, "[" + FeatureName + "] " + Rel.Name(emp) + " has no movement component."); return; }

            NpcBehaviour active = emp.Behaviour.activeBehaviour;
            // Not on a task, or walking: nothing to watch. Normal states, so no log line.
            if (active == null || !active.Active || !Rel.IsWork(active) || emp.Movement.IsMoving) { _still.Remove(id); return; }

            Vector3 dest = emp.Movement.CurrentDestination;
            Vector3 here = emp.transform.position;
            float dist = Vector3.Distance(here, dest);
            // No live destination (the flag is cleared when a walk ends) or already there: the employee is working in place.
            // A destination left over from an earlier walk is never warped to.
            if (!emp.Movement._hasDestination || dest == Vector3.zero || dist <= Rel.FarFromTarget) { _still.Remove(id); return; }

            _checks++;
            if (!_still.TryGetValue(id, out Still s))
            {
                _still[id] = new Still { Spot = here, Since = Time.time };
                return;
            }
            if (Vector3.Distance(here, s.Spot) > Options.StandstillRadius.Value)
            {
                s.Spot = here; s.Since = Time.time; s.Tries = 0; s.GaveUp = false;
                return;
            }
            if (s.GaveUp || Time.time - s.Since < Options.StandstillSeconds.Value) return;

            if (s.Tries >= Options.StandstillAttempts.Value)
            {
                s.GaveUp = true;
                _gaveUp++;
                Core.Log?.Warning("[Fix1] gave up on " + Rel.Name(emp) + " (" + Rel.TypeName(active) + "): still " + dist.ToString("F1")
                    + " m from its destination after " + s.Tries + " warp(s); the target is probably off the walkable floor.");
                return;
            }

            s.Tries++;
            Warp(emp, dest, "standstill " + Options.StandstillSeconds.Value.ToString("F0") + "s, try " + s.Tries, false);
            s.Spot = emp.transform.position;
            s.Since = Time.time;
        }

        /// <summary>
        /// Moves the employee to the NavMesh point nearest <paramref name="dest"/> and logs one line. Without a NavMesh
        /// point within 5 m the employee stays where it is and a warning says so.
        /// </summary>
        private static bool Warp(Employee emp, Vector3 dest, string why, bool dev)
        {
            if (!Rel.TryOnNavMesh(dest, out Vector3 target))
            {
                _refused++;
                Core.Log?.Warning("[Fix1] did not warp " + Rel.Name(emp) + " (" + why + "): no NavMesh within 5 m of its destination " + dest + ".");
                return false;
            }
            Vector3 before = emp.transform.position;
            emp.Movement.Warp(target);
            _warps++;
            string line = "[Fix1] warped " + Rel.Name(emp) + " to its destination (" + why + "), was " + Vector3.Distance(before, dest).ToString("F1")
                + " m away, now " + Vector3.Distance(emp.transform.position, dest).ToString("F1") + " m.";
            if (dev) Core.Log?.Msg(line); else Rel.Action("fix1.warp", line);
            return true;
        }

        /// <summary>
        /// Called after every vanilla WalkCallback. A Partial result far from the target is counted as a failure; at the
        /// configured count the employee is warped. <paramref name="force"/> skips the distance test (dev command).
        /// </summary>
        internal static void OnWalkResult(NpcBehaviour b, NPCMovement.WalkResult result, bool force)
        {
            if (!Rel.Gate(FeatureName + ":partial", Options.FixPartialWalkLoop.Value)) return;
            NPC npc = b.Npc;
            if (npc == null) { Rel.MsgOnce("fix1:nonpc", "[" + FeatureName + "] a behaviour has no NPC, walk result ignored."); return; }
            int id = npc.GetInstanceID();

            if (result == NPCMovement.WalkResult.Success) { _partial.Remove(id); return; }
            if (result != NPCMovement.WalkResult.Partial) return;

            Employee emp = npc.TryCast<Employee>();
            if (emp == null) return;   // customers and dealers keep vanilla behaviour
            Vector3 dest = npc.Movement.CurrentDestination;
            float dist = Vector3.Distance(npc.transform.position, dest);
            if (!force && (dest == Vector3.zero || dist <= Rel.FarFromTarget)) { _partial.Remove(id); return; }

            _partial.TryGetValue(id, out int count);
            count++;
            _partialsCounted++;
            int limit = Options.PathFailuresBeforeWarp.Value;
            if (count >= limit || force)
            {
                _partial.Remove(id);
                if (Warp(emp, dest, "partial walk x" + count, force)) _partialWarps++;
                return;
            }
            _partial[id] = count;
            // Raised towards vanilla's limit of five (never to it, so the vanilla warp cannot fire before ours) and never
            // lowered: a higher value is the game's own failure count.
            int shown = Math.Min(count, 4);
            if (b.consecutivePathingFailures < shown) b.consecutivePathingFailures = shown;
        }

#if DEBUG
        private static void Dev(string[] args)
        {
            if (!DevConsole.RequireHost("standstill")) return;
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            Employee first = null;
            foreach (Employee emp in Rel.Employees())
            {
                NpcBehaviour a = emp.Behaviour?.activeBehaviour;
                bool work = Rel.IsWork(a) && a.Active;
                _still.TryGetValue(emp.GetInstanceID(), out Still s);
                _partial.TryGetValue(emp.GetInstanceID(), out int p);
                float dist = Vector3.Distance(emp.transform.position, emp.Movement.CurrentDestination);
                Core.Log?.Msg("[Dev] [Fix1] " + Rel.Name(emp) + ": behaviour=" + (a == null ? "none" : Rel.TypeName(a)) + " work=" + work
                    + " moving=" + emp.Movement.IsMoving + " distToDest=" + dist.ToString("F1")
                    + " stillFor=" + (s == null ? "0" : (Time.time - s.Since).ToString("F1")) + "s tries=" + (s == null ? 0 : s.Tries) + " partial=" + p);
                if (first == null && work && emp.Movement.CurrentDestination != Vector3.zero) first = emp;
            }
            Core.Log?.Msg("[Dev] [Fix1] " + new Fix1Standstill().Status);
            if (mode == "") return;
            if (first == null) { Core.Log?.Warning("[Dev] awe standstill " + mode + ": no employee is on a work task with a destination."); return; }
            if (mode == "warp") Warp(first, first.Movement.CurrentDestination, "dev command", true);
            else if (mode == "partial") OnWalkResult(first.Behaviour.activeBehaviour, NPCMovement.WalkResult.Partial, true);
            else Core.Log?.Warning("[Dev] awe standstill: unknown mode '" + mode + "'.");
        }
#endif
    }

    /// <summary>Behaviour.WalkCallback (Il2CppScheduleOne.NPCs.Behaviour/Behaviour.cs:873).</summary>
    [HarmonyPatch(typeof(NpcBehaviour), nameof(NpcBehaviour.WalkCallback))]
    internal static class Awe_Fix1_WalkCallback_Patch
    {
        private static void Postfix(NpcBehaviour __instance, NPCMovement.WalkResult result)
        {
            // The warp inside OnWalkResult can end a walk and call back into WalkCallback.
            if (!Rel.Enter("fix1.walkcallback")) return;
            try { Fix1Standstill.OnWalkResult(__instance, result, false); }
            catch (Exception e) { Core.Failed("fix1: WalkCallback postfix", e); }
            finally { Rel.Leave("fix1.walkcallback"); }
        }
    }
}
