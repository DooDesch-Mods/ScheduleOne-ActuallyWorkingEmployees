using System;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using ActuallyWorkingEmployees.Config;
using ActuallyWorkingEmployees.Networking;
#if DEBUG
using ActuallyWorkingEmployees.Debugging;
#endif

namespace ActuallyWorkingEmployees.Features.World
{
    /// <summary>
    /// Lets a property hold more employees than it has idle points.
    ///
    /// Employee.AssignProperty (Employee.cs:215) registers the employee and reads
    /// <c>prop.EmployeeIdlePoints[EmployeeIndex]</c> (:223). With more employees than points the read throws, and the
    /// rest of the assignment (spawn point, the packager's configuration) never runs. For the duration of that call a
    /// prefix swaps in an array that repeats the points, index modulo the point count, and a finalizer puts the
    /// original back. The game's own checks on the array length, such as the capacity warning at Property.cs:896, see
    /// the original.
    ///
    /// Other mods add real idle points in their own prefix on the same method (Based for an expanded property). The
    /// prefix runs last, so their points are there before this one counts, and the finalizer only puts the original
    /// back while the property still holds the array this prefix made. An array another mod set during the call stays.
    /// </summary>
    internal sealed class IdlePointSharing : IFeature
    {
        internal const string FeatureName = "idle-points";
        public string Name => FeatureName;

        private static int _shared, _unchanged, _kept, _recovered;

        public void Init()
        {
#if DEBUG
            DevConsole.Register("idle", "awe idle  (idle points and employees of every property)", Dev);
#endif
        }

        public void Tick(float dt) { }
        public void Reset() { _shared = _unchanged = _kept = _recovered = 0; }

        public string Status => "on=" + (Options.ShareIdlePoints.Value ? 1 : 0) + " role=" + Net.RoleText() + " shared=" + _shared + " unchanged=" + _unchanged + " kept=" + _kept + " recovered=" + _recovered;

        /// <summary>What the finalizer needs: the array to put back and the one the prefix swapped in.</summary>
        internal sealed class Swap
        {
            internal Il2CppReferenceArray<Transform> Original;
            internal IntPtr Extended;
        }

        /// <summary>
        /// Prefix body. The swap to undo after the call, or null when the original array already has a point for the
        /// new employee, or sharing is off on the host. Runs on the host and on a guest.
        /// </summary>
        internal static Swap Extend(Il2CppScheduleOne.Property.Property prop)
        {
            if (prop == null) { Core.WarnOnce("idle:noprop", "idle: AssignProperty ran without a property; the game's assignment runs unchanged."); return null; }
            // The host follows the option. A guest with the mod always extends: the host's employee count decides how
            // many indices a guest reads (Employee.Initialize runs AssignProperty on every client, Employee.cs:757), and a
            // guest that skipped the swap would throw at Employee.cs:223 exactly like the host without it.
            bool guest = Net.IsClientOnly;
            if (!guest)
            {
                if (!Options.ShareIdlePoints.Value) { Core.WarnOnce("idle:off", "idle: sharing is off; the game's assignment runs unchanged."); return null; }
                if (!Net.IsServer) { Core.WarnOnce("idle:offline", "idle: neither host nor guest; the game's assignment runs unchanged."); return null; }
            }
            var points = prop.EmployeeIdlePoints;
            var employees = prop.Employees;
            if (points == null || employees == null)
            {
                Core.WarnOnce("idle:null:" + prop.GetInstanceID(), prop.name + " has no idle point or employee list; the game's assignment runs unchanged.");
                return null;
            }
            // RegisterEmployee appends, so the new employee gets the index Employees.Count.
            int need = employees.Count + 1;
            if (points.Length >= need) { _unchanged++; return null; }
            if (points.Length == 0)
            {
                Core.WarnOnce("idle:zero:" + prop.GetInstanceID(), prop.name + " has no idle points at all; nothing to share.");
                return null;
            }

            var extended = new Il2CppReferenceArray<Transform>(need);
            for (int i = 0; i < need; i++) extended[i] = points[i % points.Length];
            prop.EmployeeIdlePoints = extended;
            _shared++;
            Core.WarnOnce("idle:share:" + prop.GetInstanceID(), prop.name + ": " + need + " employees share " + points.Length + " idle point(s)" + (guest ? " (guest side)." : "."));
            // A guest without AWE reads the same index from the unextended array and throws in Employee.Initialize.
            if (!guest && Net.RemoteClients() > 0)
                Core.WarnOnce("idle:vanilla-guest:" + prop.GetInstanceID(), prop.name + ": " + need + " employees but " + points.Length + " idle point(s) while guests are connected; a guest without AWE gets an IndexOutOfRange for the extra employee.");
            return new Swap { Original = points, Extended = extended.Pointer };
        }

        /// <summary>
        /// Finalizer body. Puts the original array back unless another mod replaced the shared one during the call; that
        /// mod's array holds its own idle points, and putting the original back would drop them.
        /// </summary>
        internal static void Restore(Il2CppScheduleOne.Property.Property prop, Swap swap)
        {
            var now = prop.EmployeeIdlePoints;
            if (now != null && now.Pointer != swap.Extended)
            {
                _kept++;
                Core.InfoOnce("idle:kept:" + prop.GetInstanceID(), prop.name + ": another mod set " + now.Length + " idle point(s) during the assignment; they stay instead of the " + swap.Original.Length + " from before.");
                return;
            }
            prop.EmployeeIdlePoints = swap.Original;
        }

        /// <summary>
        /// Finalizer body for a call that threw. An IndexOutOfRange there means the idle point read at Employee.cs:223
        /// missed although the prefixes made enough points, so a mod changed the property or the employee list during
        /// the call. The read is the last step but one, so setting the idle point and the spawn point here finishes the
        /// assignment. Returns the exception to pass on, null when the assignment was finished.
        /// </summary>
        internal static Exception AfterThrow(Employee emp, Il2CppScheduleOne.Property.Property prop, Exception ex)
        {
            if (ex.GetType().Name != "Il2CppException" || ex.Message.IndexOf("IndexOutOfRangeException", StringComparison.Ordinal) < 0) return ex;
            if (emp == null || prop == null) { Core.WarnOnce("idle:throw:null", "idle: AssignProperty threw an IndexOutOfRange without an employee or property; passed on."); return ex; }
            if (!Net.IsClientOnly && !Options.ShareIdlePoints.Value) { Core.WarnOnce("idle:throw:off", "idle: AssignProperty threw an IndexOutOfRange and sharing is off; passed on."); return ex; }
            var points = prop.EmployeeIdlePoints;
            var employees = prop.Employees;
            int listed = employees != null ? employees.IndexOf(emp) : -1;
            string state = prop.name + " (index " + emp.EmployeeIndex + ", in the list at " + listed + ", " + (points?.Length ?? 0) + " idle point(s), "
                + (employees?.Count ?? 0) + " employee(s), capacity " + prop.EmployeeCapacity + ")";
            if (points == null || points.Length == 0 || listed < 0 || emp.WaitOutside == null)
            {
                Core.Log?.Error("[idle] " + emp.FullName + ": assignment to " + state + " threw an IndexOutOfRange and cannot be finished: "
                    + (listed < 0 ? "the employee is not in the property's list." : emp.WaitOutside == null ? "the employee has no idle behaviour." : "the property has no idle points."));
                return ex;
            }
            emp.WaitOutside.IdlePoint = points[listed % points.Length];
            emp.cachedNPCSpawnPoint = prop.NPCSpawnPoint;
            _recovered++;
            Core.WarnOnce("idle:throw:" + prop.GetInstanceID(), "idle: " + emp.FullName + ": assignment to " + state
                + " threw an IndexOutOfRange; another mod changed the property during the call. The idle point and spawn point are set from the property's points.");
            return null;
        }

#if DEBUG
        /// <summary>Set by 'awe idle break': the next assignment runs with one idle point, the way a mod that shrinks the array would.</summary>
        internal static bool BreakNext;
#endif

#if DEBUG
        private static void Dev(string[] args)
        {
            if (args.Length > 0 && args[0] == "break")
            {
                BreakNext = true;
                Core.Log?.Msg("[Dev] [Idle] the next assignment runs with one idle point (synthetic).");
                return;
            }
            var props = Il2CppScheduleOne.Property.Property.Properties;
            if (props == null) { Core.Log?.Warning("[Dev] [Idle] Property.Properties is null."); return; }
            int n = 0;
            for (int i = 0; i < props.Count; i++)
            {
                var p = props[i];
                if (p == null) continue;
                int pts = p.EmployeeIdlePoints?.Length ?? 0;
                int emps = p.Employees?.Count ?? 0;
                n++;
                Core.Log?.Msg("[Dev] [Idle] " + p.name + ": points=" + pts + " employees=" + emps + (emps > pts ? " shared" : ""));
            }
            Core.Log?.Msg("[Dev] [Idle] " + n + " property(ies). " + new IdlePointSharing().Status);
        }
#endif
    }

    /// <summary>Employee.AssignProperty (Il2CppScheduleOne.Employees/Employee.cs:1273).</summary>
    [HarmonyPatch(typeof(Employee), nameof(Employee.AssignProperty))]
    internal static class Awe_IdlePoints_AssignProperty_Patch
    {
        private static bool Prepare(System.Reflection.MethodBase original) => original == null || ActuallyWorkingEmployees.Compat.NativeMethods.AllowPatch(original);
        // Last, so a mod that adds real idle points in its own prefix has done so before the count.
        [HarmonyPriority(Priority.Last)]
        private static void Prefix(Il2CppScheduleOne.Property.Property prop, out IdlePointSharing.Swap __state)
        {
            __state = null;
            try { __state = IdlePointSharing.Extend(prop); }
            catch (Exception e) { Core.Failed("idle: AssignProperty prefix", e); }
#if DEBUG
            if (IdlePointSharing.BreakNext && prop != null && prop.EmployeeIdlePoints != null && prop.EmployeeIdlePoints.Length > 0)
            {
                IdlePointSharing.BreakNext = false;
                var before = prop.EmployeeIdlePoints;
                var one = new Il2CppReferenceArray<Transform>(1);
                one[0] = before[0];
                prop.EmployeeIdlePoints = one;
                // The finalizer puts back what the property held before the break, through the same pointer check.
                __state = new IdlePointSharing.Swap { Original = __state?.Original ?? before, Extended = one.Pointer };
                Core.Log?.Msg("[Dev] [Idle] " + prop.name + ": this assignment runs with 1 idle point instead of " + before.Length + " (synthetic).");
            }
#endif
        }

        private static Exception Finalizer(Employee __instance, Il2CppScheduleOne.Property.Property prop, IdlePointSharing.Swap __state, Exception __exception)
        {
            if (__exception != null)
            {
                try { __exception = IdlePointSharing.AfterThrow(__instance, prop, __exception); }
                catch (Exception e) { Core.Failed("idle: finish a failed assignment", e); }
            }
            if (__state != null && prop != null)
            {
                try { IdlePointSharing.Restore(prop, __state); }
                catch (Exception e) { Core.Failed("idle: restore the idle points", e); }
            }
            return __exception;
        }
    }
}
