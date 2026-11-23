using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppScheduleOne.Employees;
using ActuallyWorkingEmployees.Compat;
using ActuallyWorkingEmployees.Features.Reliability;
using ActuallyWorkingEmployees.Networking;
using UnityEngine;

namespace ActuallyWorkingEmployees.Features.Whip
{
    /// <summary>
    /// The whip reset (AWE#63): the employee drops its task and walks back to its waiting spot, with every assignment kept.
    ///
    /// Built only from vanilla calls. Fix 7's restart ends the work behaviours with Behaviour.Disable_Networked, the same
    /// call NPCBehaviour.OnKnockOut uses, so each behaviour's own Deactivate releases its station and slot locks.
    /// Employee.SetIdle(true) then enables WaitOutside, the IdleBehaviour vanilla gives an employee with nothing to do;
    /// it walks to the property's idle point. Vanilla would start the next task on the following tick (each role's
    /// UpdateBehaviour calls TryStartNewTask), so the employee never got there. While it walks back, its role's
    /// UpdateBehaviour is skipped; it runs again once WaitOutside reports IsAtIdleLocation, or after
    /// <see cref="MaxSeconds"/> when the point is out of reach.
    /// </summary>
    internal static class WhipReturn
    {
        /// <summary>Longest real time an employee is kept from work on its way back (the far side of the barn takes about 30 s).</summary>
        internal const float MaxSeconds = 60f;

        private sealed class Hold
        {
            internal Employee Emp;
            internal float Until;
            internal float Since;
        }
        private static readonly Dictionary<int, Hold> _holds = new Dictionary<int, Hold>();
        internal static int Sent, Arrived, TimedOut;

        internal static int Count => _holds.Count;

        internal static void ResetSession() { _holds.Clear(); }

        /// <summary>Host: ends the employee's work and sends it to its waiting spot. False when the work could not be ended.</summary>
        internal static bool Send(Employee emp, string why)
        {
            if (!Fix7StuckWork.Restart(emp, why, true)) return false;
            if (emp.WaitOutside == null || emp.WaitOutside.IdlePoint == null)
            {
                Core.Log?.Warning("[Whip] " + Rel.Name(emp) + " has no waiting spot (" + (emp.WaitOutside == null ? "no idle behaviour" : "no idle point")
                    + "); the task was restarted and it goes back to work.");
                return true;
            }
            emp.SetIdle(true);
            float now = Time.unscaledTime;
            _holds[emp.GetInstanceID()] = new Hold { Emp = emp, Since = now, Until = now + MaxSeconds };
            Sent++;
            float dist = Vector3.Distance(emp.transform.position, emp.WaitOutside.IdlePoint.position);
            Core.Log?.Msg("[Whip] " + Rel.Name(emp) + " walks back to its waiting spot, " + dist.ToString("F1") + " m away; work resumes once it is there.");
            return true;
        }

        /// <summary>UpdateBehaviour prefix body: true while the employee is still on its way back.</summary>
        internal static bool Holds(Employee emp)
        {
            if (_holds.Count == 0 || emp == null) return false;
            int id = emp.GetInstanceID();
            if (!_holds.TryGetValue(id, out Hold h)) return false;
            float now = Time.unscaledTime;
            string name = Rel.Name(emp);
            if (emp.Fired || emp.WaitOutside == null)
            {
                _holds.Remove(id);
                Core.Log?.Msg("[Whip] " + name + " is no longer held for the walk back (" + (emp.Fired ? "fired" : "no idle behaviour") + ").");
                return false;
            }
            if (emp.WaitOutside.IsAtIdleLocation())
            {
                _holds.Remove(id);
                Arrived++;
                Core.Log?.Msg("[Whip] " + name + " is back at its waiting spot after " + (now - h.Since).ToString("F1") + " s; work resumes.");
                return false;
            }
            if (now >= h.Until)
            {
                _holds.Remove(id);
                TimedOut++;
                Core.Log?.Warning("[Whip] " + name + " did not reach its waiting spot within " + MaxSeconds.ToString("F0") + " s; work resumes from where it stands.");
                return false;
            }
            return true;
        }

        internal static string Status => "returning=" + _holds.Count + " sent=" + Sent + " arrived=" + Arrived + " timedOut=" + TimedOut;
    }

    /// <summary>
    /// Skips the role's UpdateBehaviour (Il2CppScheduleOne.Employees Botanist.cs:1515, Chemist.cs:1043, Cleaner.cs:1147,
    /// Packager.cs:998) while a whipped employee walks back to its waiting spot, so it does not start the next task first.
    /// </summary>
    [HarmonyPatch]
    internal static class Awe_WhipReturn_UpdateBehaviour_Patch
    {
        private static readonly Type[] _roles = { typeof(Botanist), typeof(Chemist), typeof(Cleaner), typeof(Packager) };

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var all = new List<MethodBase>();
            foreach (Type t in _roles)
            {
                MethodBase m = AccessTools.DeclaredMethod(t, nameof(Employee.UpdateBehaviour), Type.EmptyTypes);
                if (m == null) { MelonLoader.MelonLogger.Warning("[Actually Working Employees] " + t.Name + ".UpdateBehaviour not found, a whipped " + t.Name + " starts its next task before it walks back."); continue; }
                all.Add(m);
            }
            return NativeMethods.OnePerAddress("UpdateBehaviour (whip return)", all);
        }

        private static bool Prefix(Employee __instance)
        {
            try { return !(Net.IsServer && WhipReturn.Holds(__instance)); }
            catch (Exception e) { Core.Failed("whip: return prefix", e); return true; }
        }
    }
}
