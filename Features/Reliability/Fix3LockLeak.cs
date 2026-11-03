using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppFishNet.Object;
using Il2CppScheduleOne.EntityFramework;
using Il2CppScheduleOne.GameTime;
using Il2CppScheduleOne.Management;
using Il2CppScheduleOne.ObjectScripts;
using ActuallyWorkingEmployees.Config;
using NpcBehaviour = Il2CppScheduleOne.NPCs.Behaviour.Behaviour;
using Property = Il2CppScheduleOne.Property.Property;
using Business = Il2CppScheduleOne.Property.Business;
#if DEBUG
using ActuallyWorkingEmployees.Debugging;
#endif

namespace ActuallyWorkingEmployees.Features.Reliability
{
    /// <summary>
    /// Fix 3: station and slot locks that an employee leaves behind.
    ///
    /// Behaviour.Disable only deactivates a behaviour that is Active, so a behaviour that was paused and then disabled
    /// keeps the station user and the slot locks, and Employee.Fire frees nothing. Both are followed by a release of
    /// everything that employee holds. Before a night's sleep every employee is released and again when the clock has
    /// skipped, so no lock survives the night.
    /// </summary>
    internal sealed class Fix3LockLeak : IFeature
    {
        internal const string FeatureName = "fix3-lock-leak";
        internal const string SleepName = "fix3-sleep-release";
        public string Name => FeatureName;

        private static int _stationLocks, _slotLocks, _sweeps, _sleepReleases;

        public void Init()
        {
#if DEBUG
            DevConsole.Register("lockleak", "awe lockleak [release]  (locks held by employees / free them now)", Dev);
#endif
        }

        public void Reset() { }

        public string Status =>
            "on=" + (Options.FixLockLeak.Value ? 1 : 0) + (Options.SleepSafeRelease.Value ? 1 : 0)
            + " sweeps=" + _sweeps + " stationLocks=" + _stationLocks + " slotLocks=" + _slotLocks + " sleepReleases=" + _sleepReleases;

        public void Tick(float dt) { }

        /// <summary>Every placed item of every property and business, each once.</summary>
        private static List<BuildableItem> PlacedItems()
        {
            var items = new List<BuildableItem>();
            var seen = new HashSet<IntPtr>();
            void From(Il2CppSystem.Collections.Generic.List<BuildableItem> list)
            {
                if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                {
                    BuildableItem item = list[i];
                    if (item == null || !seen.Add(item.Pointer)) continue;
                    items.Add(item);
                }
            }
            var properties = Property.Properties;
            if (properties != null) for (int i = 0; i < properties.Count; i++) From(properties[i]?.BuildableItems);
            var businesses = Business.Businesses;
            if (businesses != null) for (int i = 0; i < businesses.Count; i++) From(businesses[i]?.BuildableItems);
            return items;
        }

        private static bool IsStation(BuildableItem item)
        {
            return item.TryCast<LabOven>() != null || item.TryCast<ChemistryStation>() != null
                || item.TryCast<MixingStation>() != null || item.TryCast<Cauldron>() != null
                || item.TryCast<PackagingStation>() != null || item.TryCast<BrickPress>() != null
                || item.TryCast<DryingRack>() != null || item.TryCast<Pot>() != null || item.TryCast<MushroomBed>() != null;
        }

        /// <summary>
        /// Frees the station user and the input slot locks that <paramref name="npc"/> holds. With
        /// <paramref name="countOnly"/> nothing is changed and the locks are only counted.
        /// </summary>
        internal static int ReleaseFor(NPC npc, List<BuildableItem> items, bool countOnly, out int slotLocks)
        {
            slotLocks = 0;
            int stations = 0;
            NetworkObject mine = npc.NetworkObject;
            if (mine == null) { Rel.MsgOnce("fix3:nonetobj:" + npc.GetInstanceID(), "[" + FeatureName + "] " + Rel.Name(npc) + " has no NetworkObject, nothing to release."); return 0; }

            foreach (BuildableItem item in items)
            {
                if (!IsStation(item)) continue;
                IUsable usable = item.TryCast<IUsable>();
                NetworkObject owner = usable?.NPCUserObject;
                if (owner != null && owner.Pointer == mine.Pointer)
                {
                    stations++;
                    if (!countOnly) usable.SetNPCUser(null);
                }
                ITransitEntity entity = item.TryCast<ITransitEntity>();
                var inputs = entity?.InputSlots;
                if (inputs == null) continue;
                bool held = false;
                for (int i = 0; i < inputs.Count; i++)
                {
                    var lk = inputs[i]?.ActiveLock;
                    if (lk != null && lk.LockOwner != null && lk.LockOwner.Pointer == mine.Pointer) { slotLocks++; held = true; }
                }
                if (held && !countOnly) entity.RemoveSlotLocks(mine);
            }
            return stations;
        }

        /// <summary>Releases one employee's locks and logs one line when something was freed.</summary>
        internal static void ReleaseEmployee(NPC npc, string why)
        {
            List<BuildableItem> items = PlacedItems();
            int stations = ReleaseFor(npc, items, false, out int slots);
            _sweeps++;
            if (stations == 0 && slots == 0) return;
            _stationLocks += stations; _slotLocks += slots;
            Rel.Action("fix3.release", "[Fix3] released " + stations + " station lock(s) and " + slots + " slot lock(s) of " + Rel.Name(npc) + " (" + why + ").");
        }

        /// <summary>Releases every employee's locks with one pass over the placed items.</summary>
        internal static void ReleaseAll(string why)
        {
            List<BuildableItem> items = PlacedItems();
            int stations = 0, slots = 0;
            foreach (Employee emp in Rel.Employees())
            {
                stations += ReleaseFor(emp, items, false, out int s);
                slots += s;
            }
            _sweeps++;
            _stationLocks += stations; _slotLocks += slots;
            _sleepReleases++;
            Core.Log?.Msg("[Fix3] released " + stations + " station lock(s) and " + slots + " slot lock(s) of all employees (" + why + ").");
        }

#if DEBUG
        private static void Dev(string[] args)
        {
            if (!DevConsole.RequireHost("lockleak")) return;
            bool release = args.Length > 0 && args[0].Equals("release", StringComparison.OrdinalIgnoreCase);
            List<BuildableItem> items = PlacedItems();
            int total = 0;
            foreach (Employee emp in Rel.Employees())
            {
                int stations = ReleaseFor(emp, items, !release, out int slots);
                total += stations + slots;
                Core.Log?.Msg("[Dev] [Fix3] " + Rel.Name(emp) + ": " + stations + " station lock(s), " + slots + " slot lock(s)"
                    + (release ? (stations + slots > 0 ? " released (dev command)" : "") : "")
                    + " active=" + Rel.TypeName(emp.Behaviour?.activeBehaviour));
            }
            Core.Log?.Msg("[Dev] [Fix3] " + items.Count + " placed item(s), " + total + " lock(s) " + (release ? "released" : "held") + ". " + new Fix3LockLeak().Status);
        }
#endif
    }

    /// <summary>Behaviour.Disable (Il2CppScheduleOne.NPCs.Behaviour/Behaviour.cs:634).</summary>
    [HarmonyPatch(typeof(NpcBehaviour), nameof(NpcBehaviour.Disable))]
    internal static class Awe_Fix3_BehaviourDisable_Patch
    {
        // Active at the call: vanilla deactivates it and frees the locks itself. Otherwise the locks leak.
        private static void Prefix(NpcBehaviour __instance, out bool __state)
        {
            __state = false;
            try { __state = __instance.Active; }
            catch (Exception e) { Core.Caught("fix3: read Behaviour.Active before Disable", e); }
        }

        private static void Postfix(NpcBehaviour __instance, bool __state)
        {
            try
            {
                if (__state) return;
                if (!Rel.Gate(Fix3LockLeak.FeatureName, Options.FixLockLeak.Value)) return;
                Employee emp = __instance.Npc?.TryCast<Employee>();
                if (emp == null) return;
                Fix3LockLeak.ReleaseEmployee(emp, "behaviour " + Rel.TypeName(__instance) + " disabled while not active");
            }
            catch (Exception e) { Core.Failed("fix3: Behaviour.Disable postfix", e); }
        }
    }

    /// <summary>Employee.Fire (Il2CppScheduleOne.Employees/Employee.cs:1405).</summary>
    [HarmonyPatch(typeof(Employee), nameof(Employee.Fire))]
    internal static class Awe_Fix3_EmployeeFire_Patch
    {
        private static void Postfix(Employee __instance)
        {
            try
            {
                if (!Rel.Gate(Fix3LockLeak.FeatureName, Options.FixLockLeak.Value)) return;
                Fix3LockLeak.ReleaseEmployee(__instance, "fired");
            }
            catch (Exception e) { Core.Failed("fix3: Employee.Fire postfix", e); }
        }
    }

    /// <summary>SleepController.RpcLogic___StartSleep_2166136261 (Il2CppScheduleOne.GameTime/SleepController.cs:1230).</summary>
    [HarmonyPatch(typeof(SleepController), nameof(SleepController.RpcLogic___StartSleep_2166136261))]
    internal static class Awe_Fix3_StartSleep_Patch
    {
        // A second StartSleep during a running sleep is ignored by the game; only the call that begins one releases.
        private static void Prefix(SleepController __instance, out bool __state)
        {
            __state = false;
            try { __state = !__instance.IsSleepInProgress; }
            catch (Exception e) { Core.Caught("fix3: read IsSleepInProgress before StartSleep", e); }
        }

        private static void Postfix(bool __state)
        {
            try
            {
                if (!__state) return;
                if (!Rel.Gate(Fix3LockLeak.SleepName, Options.SleepSafeRelease.Value)) return;
                Fix3LockLeak.ReleaseAll("sleep starts");
            }
            catch (Exception e) { Core.Failed("fix3: StartSleep postfix", e); }
        }
    }

    /// <summary>TimeManager.SkipToTimeAndSync (Il2CppScheduleOne.GameTime/TimeManager.cs:2022); only the sleep routine calls it with a sleep running.</summary>
    [HarmonyPatch(typeof(TimeManager), nameof(TimeManager.SkipToTimeAndSync), new Type[] { typeof(int) })]
    internal static class Awe_Fix3_SkipToTime_Patch
    {
        private static void Postfix()
        {
            try
            {
                if (!Rel.Gate(Fix3LockLeak.SleepName, Options.SleepSafeRelease.Value)) return;
                SleepController sleep = NetworkSingleton<SleepController>.Instance;
                if (sleep == null) { Rel.MsgOnce("fix3:nosleepctl", "[" + Fix3LockLeak.SleepName + "] SleepController does not exist, wake release skipped."); return; }
                if (!sleep.IsSleepInProgress) return;   // a skip outside the sleep routine
                Fix3LockLeak.ReleaseAll("clock skipped after sleep");
            }
            catch (Exception e) { Core.Failed("fix3: SkipToTimeAndSync postfix", e); }
        }
    }
}
