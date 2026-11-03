using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppFishNet.Object;
using Il2CppScheduleOne.EntityFramework;
using Il2CppScheduleOne.GameTime;
using Il2CppScheduleOne.ItemFramework;
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
            DevConsole.Register("lockleak", "awe lockleak [release|probe]  (locks held by employees / free them now / disable an idle task of a working employee)", Dev);
#endif
        }

        public void Reset() { _pending.Clear(); }

        public string Status =>
            "on=" + (Options.FixLockLeak.Value ? 1 : 0) + (Options.SleepSafeRelease.Value ? 1 : 0)
            + " sweeps=" + _sweeps + " stationLocks=" + _stationLocks + " slotLocks=" + _slotLocks + " sleepReleases=" + _sleepReleases
            + " deferred=" + _deferred + " pending=" + _pending.Count;

        public void Tick(float dt)
        {
            if (_pending.Count == 0 || Time.time < _nextPending) return;
            _nextPending = Time.time + 1f;
            try { ReleasePending(); }
            catch (Exception e) { Core.Failed("fix3: release deferred employees", e); }
        }

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
                if (entity == null) continue;
                var inputs = entity.InputSlots;
                bool heldInput = false;
                if (inputs != null)
                {
                    for (int i = 0; i < inputs.Count; i++)
                        if (OwnedBy(inputs[i], mine)) { slotLocks++; heldInput = true; }
                }
                // RemoveSlotLocks only walks the input slots; a lock on an output slot is removed slot by slot. A slot
                // listed on both sides was counted with the inputs.
                var outputs = entity.OutputSlots;
                if (outputs != null)
                {
                    for (int i = 0; i < outputs.Count; i++)
                    {
                        ItemSlot slot = outputs[i];
                        if (!OwnedBy(slot, mine) || ContainsSlot(inputs, slot)) continue;
                        slotLocks++;
                        if (!countOnly) slot.RemoveLock(false);
                    }
                }
                if (heldInput && !countOnly) entity.RemoveSlotLocks(mine);
            }
            return stations;
        }

        private static bool OwnedBy(ItemSlot slot, NetworkObject owner)
        {
            var lk = slot?.ActiveLock;
            return lk != null && lk.LockOwner != null && lk.LockOwner.Pointer == owner.Pointer;
        }

        private static bool ContainsSlot(Il2CppSystem.Collections.Generic.List<ItemSlot> list, ItemSlot slot)
        {
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null && list[i].Pointer == slot.Pointer) return true;
            return false;
        }

        // Employees whose release waits until none of their other behaviours hold locks.
        private static readonly List<Employee> _pending = new List<Employee>();
        private float _nextPending;
        private static int _deferred;

        /// <summary>
        /// True when one of the employee's behaviours other than <paramref name="except"/> is running or paused. Its station
        /// and slot locks are in use, and a lock records the employee, not the behaviour, so it cannot be told apart from a leak.
        /// </summary>
        internal static bool OtherBehaviourHoldsLocks(Employee emp, NpcBehaviour except)
        {
            var beh = emp.Behaviour;
            if (beh == null) return false;
            IntPtr skip = except == null ? IntPtr.Zero : except.Pointer;
            NpcBehaviour active = beh.activeBehaviour;
            if (active != null && active.Pointer != skip && active.Active) return true;
            var enabled = beh.enabledBehaviours;
            if (enabled == null) return false;
            for (int i = 0; i < enabled.Count; i++)
            {
                NpcBehaviour b = enabled[i];
                if (b == null || b.Pointer == skip) continue;
                if ((b.Active || b.Started) && Rel.IsWork(b)) return true;
            }
            return false;
        }

        /// <summary>
        /// True while a caller disables several behaviours of one employee in a row. The disable hook then skips its own
        /// release and the caller finishes with a single <see cref="EndBatch"/>, so the placed items are walked once.
        /// </summary>
        internal static bool Batching { get; private set; }

        internal static void BeginBatch() { Batching = true; }

        internal static void EndBatch(Employee emp, string why)
        {
            Batching = false;
            if (!Rel.Gate(FeatureName, Options.FixLockLeak.Value)) return;
            if (OtherBehaviourHoldsLocks(emp, null)) { Defer(emp, null); return; }
            ReleaseEmployee(emp, why);
        }

        /// <summary>Remembers an employee whose leaked locks cannot be freed yet; <see cref="Tick"/> frees them once it holds no task.</summary>
        internal static void Defer(Employee emp, NpcBehaviour ended)
        {
            _deferred++;
            for (int i = 0; i < _pending.Count; i++)
                if (_pending[i].Pointer == emp.Pointer) return;
            _pending.Add(emp);
            Rel.Action("fix3.defer", "[Fix3] " + Rel.Name(emp) + " keeps its locks: " + (ended == null ? "a batch of disabled tasks" : Rel.TypeName(ended) + " was disabled")
                + " while another task of the employee is running; they are freed when it is idle.");
        }

        private static void ReleasePending()
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                Employee emp = _pending[i];
                if (Rel.IsGone(emp)) { _pending.RemoveAt(i); Core.Log?.Warning("[Fix3] a deferred employee no longer exists, its release is dropped."); continue; }
                if (OtherBehaviourHoldsLocks(emp, null)) continue;
                _pending.RemoveAt(i);
                ReleaseEmployee(emp, "task finished after a disabled task left locks");
            }
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
            if (args.Length > 0 && args[0].Equals("probe", StringComparison.OrdinalIgnoreCase)) { Probe(); return; }
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

        /// <summary>
        /// Takes an employee that works a task and holds locks, disables a work behaviour of that employee that is neither
        /// running nor started, and compares the locks before and after. The disable runs the same hook as the game's own.
        /// </summary>
        private static void Probe()
        {
            List<BuildableItem> items = PlacedItems();
            foreach (Employee emp in Rel.Employees())
            {
                NpcBehaviour active = emp.Behaviour?.activeBehaviour;
                if (active == null || !active.Active || !Rel.IsWork(active)) continue;
                int sb = ReleaseFor(emp, items, true, out int lb);
                if (sb + lb == 0) continue;

                NpcBehaviour other = null;
                foreach (NpcBehaviour b in emp.GetComponentsInChildren<NpcBehaviour>(true))
                {
                    if (b.Pointer == active.Pointer || b.Active || b.Started || !Rel.IsWork(b)) continue;
                    other = b;
                    break;
                }
                if (other == null) { Core.Log?.Warning("[Dev] [Fix3] probe " + Rel.Name(emp) + ": no idle work behaviour to disable."); continue; }

                other.Disable();
                int sa = ReleaseFor(emp, items, true, out int la);
                Core.Log?.Msg("[Dev] [Fix3] probe " + Rel.Name(emp) + ": active=" + Rel.TypeName(active) + " disabled=" + Rel.TypeName(other)
                    + " locksBefore=" + sb + "/" + lb + " locksAfter=" + sa + "/" + la + " " + (sa >= sb && la >= lb ? "HELD" : "LOST"));
                return;
            }
            Core.Log?.Warning("[Dev] [Fix3] probe: no employee holds locks while working a task.");
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
            if (__state) return;   // was active: vanilla freed the locks itself
            if (Fix3LockLeak.Batching) return;   // the caller releases once after its last disable
            if (!Rel.Enter("fix3.disable")) return;
            try
            {
                if (!Rel.Gate(Fix3LockLeak.FeatureName, Options.FixLockLeak.Value)) return;
                Employee emp = __instance.Npc?.TryCast<Employee>();
                if (emp == null) { Rel.MsgOnce("fix3:notemployee:" + Rel.TypeName(__instance), "[" + Fix3LockLeak.FeatureName + "] " + Rel.TypeName(__instance) + " belongs to an NPC that is no employee, no locks to release."); return; }
                // A running or paused task of the same employee owns the locks that exist now.
                if (Fix3LockLeak.OtherBehaviourHoldsLocks(emp, __instance)) { Fix3LockLeak.Defer(emp, __instance); return; }
                Fix3LockLeak.ReleaseEmployee(emp, "behaviour " + Rel.TypeName(__instance) + " disabled while not active");
            }
            catch (Exception e) { Core.Failed("fix3: Behaviour.Disable postfix", e); }
            finally { Rel.Leave("fix3.disable"); }
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
