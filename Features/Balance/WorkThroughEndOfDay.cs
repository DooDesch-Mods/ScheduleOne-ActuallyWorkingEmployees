using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppScheduleOne.Employees;
using Il2CppScheduleOne.GameTime;
using ActuallyWorkingEmployees.Compat;
using ActuallyWorkingEmployees.Config;
using ActuallyWorkingEmployees.Features.Reliability;
using ActuallyWorkingEmployees.Networking;
#if DEBUG
using ActuallyWorkingEmployees.Debugging;
#endif

namespace ActuallyWorkingEmployees.Features.Balance
{
    /// <summary>
    /// "Work through 4 AM": employees keep working while the clock stands at 4:00 and waits for the player to sleep.
    ///
    /// Vanilla stops them through TimeManager.IsEndOfDay (CurrentTime == 400), read in Employee.UpdateBehaviour (the
    /// "my shift ends at 4AM" branch that sends them outside) and in Employee.CanWork, which every role's
    /// UpdateBehaviour asks before it starts a task. Both are short enough for the native build to inline, so a patch
    /// on them may be applied and still never run. Instead, while one role's UpdateBehaviour runs on the host, the
    /// clock's backing field reads 3:59 and goes back to 4:00 right after. Plants, grow containers, mushroom beds and
    /// the sleep prompt read the clock outside that call and still stop at 4:00.
    /// </summary>
    internal sealed class WorkThroughEndOfDay : IFeature
    {
        internal const string FeatureName = "work-through-4am";
        private const int EndOfDay = 400;
        private const int JustBefore = 359;

        public string Name => FeatureName;

        private static int _swaps, _restoreMisses, _loggedDay = -1;
        private static readonly HashSet<string> _kept = new HashSet<string>();

        public void Init()
        {
#if DEBUG
            DevConsole.Register("endofday", "awe endofday  (option, clock and the employees kept working at 4 AM)", _ => Dev());
#endif
        }

        public void Reset() { _swaps = 0; _restoreMisses = 0; _loggedDay = -1; _kept.Clear(); }
        public void Tick(float dt) { }

        public string Status => "on=" + (Options.WorkThroughEndOfDay.Value ? 1 : 0) + " swaps=" + _swaps + " kept=" + _kept.Count
            + (_restoreMisses > 0 ? " restoreMisses=" + _restoreMisses : "");

        /// <summary>Prefix body. Returns the time manager whose clock now reads 3:59, or null when nothing was changed.</summary>
        internal static TimeManager Begin(Employee emp)
        {
            if (!Options.WorkThroughEndOfDay.Value) return null;   // the steady state, asked every tick per employee
            TimeManager tm = Il2CppScheduleOne.DevUtilities.NetworkSingleton<TimeManager>.Instance;
            if (tm == null) { Rel.MsgOnce(FeatureName + ":notm", "[" + FeatureName + "] no TimeManager yet, employees follow the vanilla 4 AM stop."); return null; }
            if (tm._CurrentTime_k__BackingField != EndOfDay) return null;
            if (!Rel.Gate(FeatureName, true)) return null;
            if (emp == null || emp.Fired) return null;

            tm._CurrentTime_k__BackingField = JustBefore;
            _swaps++;
            Note(emp, tm);
            return tm;
        }

        /// <summary>Finalizer body. Puts 4:00 back unless something else moved the clock during the call.</summary>
        internal static void End(TimeManager tm)
        {
            if (tm == null) return;
            if (tm._CurrentTime_k__BackingField == JustBefore) { tm._CurrentTime_k__BackingField = EndOfDay; return; }
            _restoreMisses++;
            Core.WarnOnce(FeatureName + ":moved", "[" + FeatureName + "] the clock moved to " + tm._CurrentTime_k__BackingField
                + " during an employee update and is left there.");
        }

        /// <summary>One info line per night with the first employee kept at work, so the log shows the option acting.</summary>
        private static void Note(Employee emp, TimeManager tm)
        {
            string name = Rel.Name(emp);
            _kept.Add(name);
            int day = tm.ElapsedDays;
            if (day == _loggedDay) return;
            _loggedDay = day;
            Core.Log?.Msg("[" + FeatureName + "] 4 AM on day " + day + ": employees keep working (first: " + name + ").");
        }

#if DEBUG
        private static void Dev()
        {
            TimeManager tm = Il2CppScheduleOne.DevUtilities.NetworkSingleton<TimeManager>.Instance;
            string clock = tm == null ? "no TimeManager" : tm._CurrentTime_k__BackingField.ToString("0000") + (tm.IsEndOfDay ? " (end of day)" : "");
            Core.Log?.Msg("[awe endofday] option=" + Options.WorkThroughEndOfDay.Value + " host=" + Net.IsServer + " clock=" + clock
                + " swaps=" + _swaps + " restoreMisses=" + _restoreMisses);
            foreach (Employee emp in Rel.Employees())
            {
                string beh = emp.Behaviour != null && emp.Behaviour.activeBehaviour != null ? emp.Behaviour.activeBehaviour.Name : "none";
                bool outside = emp.Behaviour != null && emp.Behaviour.activeBehaviour != null && emp.WaitOutside != null
                    && emp.Behaviour.activeBehaviour.Pointer == emp.WaitOutside.Pointer;
                Core.Log?.Msg("[awe endofday] " + Rel.Name(emp) + " (" + Rel.TypeName(emp) + "): behaviour=" + beh + " waitOutside=" + outside
                    + " paid=" + emp.PaidForToday + " kept=" + _kept.Contains(Rel.Name(emp)));
            }
        }
#endif
    }

    /// <summary>UpdateBehaviour of the four employee roles (Il2CppScheduleOne.Employees, Botanist.cs:1515, Chemist.cs:1043, Cleaner.cs:1147, Packager.cs:998).</summary>
    [HarmonyPatch]
    internal static class Awe_WorkThroughEndOfDay_UpdateBehaviour_Patch
    {
        private static readonly Type[] _roles = { typeof(Botanist), typeof(Chemist), typeof(Cleaner), typeof(Packager) };

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var all = new List<MethodBase>();
            foreach (Type t in _roles)
            {
                MethodBase m = AccessTools.DeclaredMethod(t, nameof(Employee.UpdateBehaviour), Type.EmptyTypes);
                if (m == null) { MelonLoader.MelonLogger.Warning("[Actually Working Employees] " + t.Name + ".UpdateBehaviour not found, " + t.Name + "s stop at 4 AM even with \"Work through 4 AM\" on."); continue; }
                all.Add(m);
            }
            return NativeMethods.OnePerAddress("UpdateBehaviour", all);
        }

        private static void Prefix(Employee __instance, out TimeManager __state)
        {
            __state = null;
            try { __state = WorkThroughEndOfDay.Begin(__instance); }
            catch (Exception e) { Core.Failed("work-through-4am: UpdateBehaviour prefix", e); }
        }

        private static Exception Finalizer(Exception __exception, TimeManager __state)
        {
            try { WorkThroughEndOfDay.End(__state); }
            catch (Exception e) { Core.Failed("work-through-4am: UpdateBehaviour finalizer", e); }
            return __exception;
        }
    }
}
