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
    /// configured time is handled in steps. A walk that makes no progress is first stopped (what talking to the
    /// employee does); an employee still far from its destination is then warped to it. After the configured attempts
    /// it is left alone with a warning until it moves.
    /// </summary>
    internal sealed class Fix1Standstill : IFeature
    {
        internal const string FeatureName = "fix1-standstill";
        public string Name => FeatureName;

        private sealed class Still { internal Vector3 Spot; internal float Since; internal int Tries; internal bool Stopped; internal bool GaveUp; }

        private static readonly Dictionary<int, Still> _still = new Dictionary<int, Still>();
        private static readonly Dictionary<int, int> _partial = new Dictionary<int, int>();
        private static int _checks, _warps, _gaveUp, _partialWarps, _partialsCounted, _refused, _stops;
        private float _next;

        public void Init()
        {
#if DEBUG
            DevConsole.Register("standstill", "awe standstill [warp|partial|freeze|thaw|probe x y z|put x y z]  (state of every employee / force the fix / pin a walking employee in place / where a warp to a point lands / move the working employee to a point)", Dev);
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
            + " partial=" + _partialsCounted + " partialwarps=" + _partialWarps + " refused=" + _refused + " stops=" + _stops;

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
            // Not on a task: nothing to watch. A normal state, so no log line.
            if (active == null || !active.Active || !Rel.IsWork(active)) { _still.Remove(id); return; }

            // IsMoving only says the agent holds a path with distance left (NPCMovement.IsMoving), not that it moves. A walk
            // that never advances keeps it true, and the work behaviour waits for the walk forever; talking to the employee
            // ends it through Movement.Stop(). Such a walk is watched like a standstill.
            bool moving = emp.Movement.IsMoving;
            Vector3 dest = emp.Movement.CurrentDestination;
            Vector3 here = emp.transform.position;
            float dist = Vector3.Distance(here, dest);
            bool destFar = emp.Movement._hasDestination && dest != Vector3.zero && dist > Rel.FarFromTarget;
            // Standing without a live destination (the flag is cleared when a walk ends) or already there: the employee is
            // working in place. A destination left over from an earlier walk is never warped to.
            if (!moving && !destFar) { _still.Remove(id); return; }

            _checks++;
            if (!_still.TryGetValue(id, out Still s))
            {
                _still[id] = new Still { Spot = here, Since = Time.time };
                return;
            }
            if (Vector3.Distance(here, s.Spot) > Options.StandstillRadius.Value)
            {
                s.Spot = here; s.Since = Time.time; s.Tries = 0; s.Stopped = false; s.GaveUp = false;
                return;
            }
            if (s.GaveUp || Time.time - s.Since < Options.StandstillSeconds.Value) return;

            string secs = Options.StandstillSeconds.Value.ToString("F0");
            if (moving && !s.Stopped)
            {
                // First step for a walk without progress: end it the way a conversation does. The work behaviour then
                // starts a fresh walk on its next tick.
                s.Stopped = true;
                emp.Movement.Stop();
                _stops++;
                Rel.Action("fix1.stop", "[Fix1] stopped the walk of " + Rel.Name(emp) + " (" + Rel.TypeName(active) + "): no progress for "
                    + secs + "s, " + dist.ToString("F1") + " m from its destination.");
                s.Spot = emp.transform.position;
                s.Since = Time.time;
                return;
            }

            if (!destFar || s.Tries >= Options.StandstillAttempts.Value)
            {
                s.GaveUp = true;
                _gaveUp++;
                Core.Log?.Warning("[Fix1] gave up on " + Rel.Name(emp) + " (" + Rel.TypeName(active) + "): still " + dist.ToString("F1")
                    + " m from its destination after " + (s.Stopped ? "a stopped walk and " : "") + s.Tries + " warp(s)"
                    + (destFar ? "; the target is probably off the walkable floor." : "; no live destination far enough away to warp to."));
                return;
            }

            s.Tries++;
            Warp(emp, dest, "standstill " + secs + "s, try " + s.Tries, false);
            s.Spot = emp.transform.position;
            s.Since = Time.time;
        }

        /// <summary>Largest height between a destination and the point a warp lands on. More is another floor or a roof.</summary>
        private const float WarpMaxRise = 1.0f;
        private static readonly float[] WarpSearch = { 0.5f, 1f, 2f, 3f, 4f, 5f };

        /// <summary>
        /// The point on the employee's own NavMesh nearest <paramref name="dest"/> on the destination's floor. The search
        /// grows in steps, so floor a little to the side wins over a roof straight above: a station's obstacle carves the
        /// floor around its access point, and the next walkable surface within 5 m can be the roof of the room.
        /// </summary>
        internal static bool TryWarpTarget(Employee emp, Vector3 dest, out Vector3 target, out string refusal)
        {
            target = dest;
            var agent = emp.GetComponentInChildren<UnityEngine.AI.NavMeshAgent>(true);
            if (agent == null) { refusal = "it has no NavMesh agent"; return false; }
            foreach (float radius in WarpSearch)
            {
                // A fresh filter per query: the interop struct wraps a boxed IL2CPP value that does not outlive a call.
                var filter = new UnityEngine.AI.NavMeshQueryFilter { agentTypeID = agent.agentTypeID, areaMask = UnityEngine.AI.NavMesh.AllAreas };
                if (!UnityEngine.AI.NavMesh.SamplePosition(dest, out UnityEngine.AI.NavMeshHit hit, radius, filter)) continue;
                float rise = hit.position.y - dest.y;
                if (Mathf.Abs(rise) <= WarpMaxRise) { target = hit.position; refusal = null; return true; }
                refusal = "the nearest walkable floor lies " + rise.ToString("F1") + " m " + (rise > 0 ? "above" : "below") + " it, on another floor";
                return false;
            }
            refusal = "no NavMesh within 5 m of it";
            return false;
        }

        /// <summary>
        /// Moves the employee to the NavMesh point nearest <paramref name="dest"/> on the destination's floor and logs one
        /// line. Without such a point within 5 m the employee stays where it is and a warning says so.
        /// </summary>
        private static bool Warp(Employee emp, Vector3 dest, string why, bool dev)
        {
            if (!TryWarpTarget(emp, dest, out Vector3 target, out string refusal))
            {
                _refused++;
                Core.Log?.Warning("[Fix1] did not warp " + Rel.Name(emp) + " (" + why + ") to its destination " + dest + ": " + refusal + ".");
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
                    + " stillFor=" + (s == null ? "0" : (Time.time - s.Since).ToString("F1")) + "s tries=" + (s == null ? 0 : s.Tries) + " partial=" + p
                    + " pos=" + Pos(emp.transform.position) + " dest=" + Pos(emp.Movement.CurrentDestination));
                if (first == null && work && emp.Movement.CurrentDestination != Vector3.zero) first = emp;
            }
            Core.Log?.Msg("[Dev] [Fix1] " + new Fix1Standstill().Status);
            if (mode == "") return;
            if (mode == "freeze" || mode == "thaw") { DevFreeze(mode == "freeze"); return; }
            if (mode == "probe") { DevProbe(args); return; }
            if (first == null) { Core.Log?.Warning("[Dev] awe standstill " + mode + ": no employee is on a work task with a destination."); return; }
            if (mode == "put")
            {
                if (!TryParsePoint(args, out Vector3 at)) { Core.Log?.Warning("[Dev] awe standstill put: needs x y z."); return; }
                first.Movement.Warp(at);
                Core.Log?.Msg("[Dev] [Fix1] put " + Rel.Name(first) + " at " + Pos(at) + ", now " + Pos(first.transform.position) + ", destination " + Pos(first.Movement.CurrentDestination));
            }
            else if (mode == "warp") Warp(first, first.Movement.CurrentDestination, "dev command", true);
            else if (mode == "partial") OnWalkResult(first.Behaviour.activeBehaviour, NPCMovement.WalkResult.Partial, true);
            else Core.Log?.Warning("[Dev] awe standstill: unknown mode '" + mode + "'.");
        }

        private static bool TryParsePoint(string[] args, out Vector3 p)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var st = System.Globalization.NumberStyles.Float;
            p = Vector3.zero;
            if (args.Length < 4 || !float.TryParse(args[1], st, ci, out float x) || !float.TryParse(args[2], st, ci, out float y)
                || !float.TryParse(args[3], st, ci, out float z)) return false;
            p = new Vector3(x, y, z);
            return true;
        }

        /// <summary>
        /// awe standstill probe x y z: where a warp to that point lands for the first employee, next to the nearest point of
        /// any NavMesh within 5 m.
        /// </summary>
        private static void DevProbe(string[] args)
        {
            if (!TryParsePoint(args, out Vector3 p)) { Core.Log?.Warning("[Dev] awe standstill probe: needs x y z."); return; }
            Employee emp = null;
            foreach (Employee e in Rel.Employees()) { emp = e; break; }
            if (emp == null) { Core.Log?.Warning("[Dev] awe standstill probe: no employee to probe with."); return; }
            string any = Rel.TryOnNavMesh(p, out Vector3 near) ? Pos(near) : "none";
            string warp = TryWarpTarget(emp, p, out Vector3 target, out string refusal) ? Pos(target) : "refused (" + refusal + ")";
            Core.Log?.Msg("[Dev] [Fix1] probe " + Pos(p) + ": nearest any NavMesh " + any + ", warp lands " + warp + " (agent of " + Rel.Name(emp) + ")");
        }

        /// <summary>Position as "x,y,z" with two decimals in invariant culture, for the dev lines the tests parse.</summary>
        private static string Pos(Vector3 v)
            => v.x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ","
             + v.y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ","
             + v.z.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// freeze: sets NavMeshAgent.isStopped on the first employee walking for a work task. The agent keeps its path, so
        /// IsMoving stays true while the employee stands still: a walk that never arrives. thaw: clears it on every employee.
        /// </summary>
        private static void DevFreeze(bool freeze)
        {
            int n = 0;
            foreach (Employee emp in Rel.Employees())
            {
                UnityEngine.AI.NavMeshAgent agent = emp.Movement?._agent;
                if (agent == null) continue;
                if (!freeze)
                {
                    if (!agent.isStopped) continue;
                    agent.isStopped = false;
                    n++;
                    Core.Log?.Msg("[Dev] [Fix1] thawed " + Rel.Name(emp) + ".");
                    continue;
                }
                NpcBehaviour a = emp.Behaviour?.activeBehaviour;
                if (a == null || !a.Active || !Rel.IsWork(a) || !emp.Movement.IsMoving) continue;
                agent.isStopped = true;
                Core.Log?.Msg("[Dev] [Fix1] froze " + Rel.Name(emp) + " (" + Rel.TypeName(a) + "): isStopped=true, moving=" + emp.Movement.IsMoving
                    + ", " + Vector3.Distance(emp.transform.position, emp.Movement.CurrentDestination).ToString("F1") + " m from its destination, pos="
                    + Pos(emp.transform.position) + ".");
                return;
            }
            if (freeze) Core.Log?.Warning("[Dev] awe standstill freeze: no employee is walking for a work task.");
            else Core.Log?.Msg("[Dev] [Fix1] thawed " + n + " employee(s).");
        }
#endif
    }

    /// <summary>Behaviour.WalkCallback (Il2CppScheduleOne.NPCs.Behaviour/Behaviour.cs:873).</summary>
    [HarmonyPatch(typeof(NpcBehaviour), nameof(NpcBehaviour.WalkCallback))]
    internal static class Awe_Fix1_WalkCallback_Patch
    {
        private static bool Prepare(System.Reflection.MethodBase original) => original == null || ActuallyWorkingEmployees.Compat.NativeMethods.AllowPatch(original);
        private static void Postfix(NpcBehaviour __instance, NPCMovement.WalkResult result)
        {
            // The warp inside OnWalkResult can end a walk and call back into WalkCallback.
            if (!Rel.Enter("fix1.walkcallback")) return;
#if DEBUG
            try { WalkStats.Record(__instance, result); }
            catch (Exception e) { Core.Caught("walkstats: record", e); }
#endif
            try { Fix1Standstill.OnWalkResult(__instance, result, false); }
            catch (Exception e) { Core.Failed("fix1: WalkCallback postfix", e); }
            finally { Rel.Leave("fix1.walkcallback"); }
        }
    }
}
