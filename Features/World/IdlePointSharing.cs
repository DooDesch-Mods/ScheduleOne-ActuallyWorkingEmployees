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
    /// </summary>
    internal sealed class IdlePointSharing : IFeature
    {
        internal const string FeatureName = "idle-points";
        public string Name => FeatureName;

        private static int _shared, _unchanged;

        public void Init()
        {
#if DEBUG
            DevConsole.Register("idle", "awe idle  (idle points and employees of every property)", Dev);
#endif
        }

        public void Tick(float dt) { }
        public void Reset() { _shared = _unchanged = 0; }

        public string Status => "on=" + (Options.ShareIdlePoints.Value ? 1 : 0) + " shared=" + _shared + " unchanged=" + _unchanged;

        /// <summary>
        /// Prefix body. The array to put back after the call, or null when the original array already has a point for the
        /// new employee (or sharing is off, or this is not the host).
        /// </summary>
        internal static Il2CppReferenceArray<Transform> Extend(Il2CppScheduleOne.Property.Property prop)
        {
            if (prop == null) return null;
            if (!Options.ShareIdlePoints.Value) return null;
            if (!Net.IsServer) return null;
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
            Core.WarnOnce("idle:share:" + prop.GetInstanceID(), prop.name + ": " + need + " employees share " + points.Length + " idle point(s).");
            return points;
        }

#if DEBUG
        private static void Dev(string[] args)
        {
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
        private static void Prefix(Il2CppScheduleOne.Property.Property prop, out Il2CppReferenceArray<Transform> __state)
        {
            __state = null;
            try { __state = IdlePointSharing.Extend(prop); }
            catch (Exception e) { Core.Failed("idle: AssignProperty prefix", e); }
        }

        private static Exception Finalizer(Il2CppScheduleOne.Property.Property prop, Il2CppReferenceArray<Transform> __state, Exception __exception)
        {
            if (__state != null && prop != null)
            {
                try { prop.EmployeeIdlePoints = __state; }
                catch (Exception e) { Core.Failed("idle: restore the idle points", e); }
            }
            return __exception;
        }
    }
}
