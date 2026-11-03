using System;
using HarmonyLib;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Management;
using ActuallyWorkingEmployees.Config;
using MoveItem = Il2CppScheduleOne.NPCs.Behaviour.MoveItemBehaviour;
#if DEBUG
using ActuallyWorkingEmployees.Debugging;
#endif

namespace ActuallyWorkingEmployees.Features.Reliability
{
    /// <summary>
    /// Fix 4: an item an employee picked up for a transit route stays in its inventory when the task is cut short.
    ///
    /// MoveItemBehaviour clears its route in EndTransit and only returns a picked-up item on its own failure paths,
    /// so a Deactivate or Disable in the middle of a transit strands the items. The prefix remembers the route, the
    /// item and the picked-up amount; the postfix puts what is still carried back into the source's output.
    /// </summary>
    internal sealed class Fix4StuckItem : IFeature
    {
        internal const string FeatureName = "fix4-stuck-item";
        public string Name => FeatureName;

        internal sealed class Carry { internal TransitRoute Route; internal ItemInstance Template; internal int Grabbed; }

        private static int _returned, _sourceFull, _inspected;

        public void Init()
        {
#if DEBUG
            DevConsole.Register("stuckitem", "awe stuckitem [cut]  (carried items of every employee / cut the transit of every mover)", Dev);
#endif
        }

        public void Reset() { }
        public void Tick(float dt) { }

        public string Status =>
            "on=" + (Options.FixStuckInventoryItem.Value ? 1 : 0) + " inspected=" + _inspected + " returned=" + _returned + " sourceFull=" + _sourceFull;

        /// <summary>Prefix body: what the behaviour carries right now. Null when it carries nothing worth returning.</summary>
        internal static Carry Capture(MoveItem b)
        {
            if (!Rel.Gate(FeatureName, Options.FixStuckInventoryItem.Value)) return null;
            if (b.assignedRoute == null || b.itemToRetrieveTemplate == null || b.grabbedAmount <= 0) return null;
            return new Carry { Route = b.assignedRoute, Template = b.itemToRetrieveTemplate, Grabbed = b.grabbedAmount };
        }

        /// <summary>Postfix body: returns the still-carried amount to the route's source.</summary>
        internal static void Return(MoveItem b, Carry c, string when, bool dev)
        {
            if (c == null) return;
            NPC npc = b.Npc;
            if (npc == null || npc.Inventory == null) { Rel.MsgOnce("fix4:noinv", "[" + FeatureName + "] a mover has no NPC inventory, nothing returned."); return; }
            _inspected++;

            ItemInstance carried = npc.Inventory.GetFirstIdenticalItem(c.Template);
            if (carried == null) return;   // delivered or returned by vanilla

            int amount = Math.Min(c.Grabbed, carried.Quantity);
            ITransitEntity source = c.Route.Source;
            if (source == null) { Core.Log?.Warning("[Fix4] " + Rel.Name(npc) + " carries " + amount + "x " + carried.Name + " but the route has no source."); return; }

            ItemInstance copy = carried.GetCopy(amount);
            if (source.GetOutputCapacityForItem(copy, npc) < amount)
            {
                _sourceFull++;
                Core.Log?.Warning("[Fix4] " + Rel.Name(npc) + " carries " + amount + "x " + carried.Name + " and " + source.Name + " has no room for it; the item stays.");
                return;
            }

            // Take the amount out of the slot that holds it, then hand the copy to the source.
            var slots = npc.Inventory.ItemSlots;
            for (int i = 0; i < slots.Count; i++)
            {
                ItemSlot slot = slots[i];
                if (slot == null || slot.ItemInstance == null || slot.ItemInstance.Pointer != carried.Pointer) continue;
                slot.ChangeQuantity(-amount);
                break;
            }
            source.InsertItemIntoOutput(copy, npc);
            _returned++;
            string line = "[Fix4] " + Rel.Name(npc) + " put " + amount + "x " + carried.Name + " back into " + source.Name + " (" + when + ").";
            if (dev) Core.Log?.Msg(line); else Rel.Action("fix4.return", line);
        }

#if DEBUG
        private static void Dev(string[] args)
        {
            if (!DevConsole.RequireHost("stuckitem")) return;
            bool cut = args.Length > 0 && args[0].Equals("cut", StringComparison.OrdinalIgnoreCase);
            foreach (Employee emp in Rel.Employees())
            {
                var slots = emp.Inventory?.ItemSlots;
                int carried = 0;
                string what = "";
                if (slots != null)
                    for (int i = 0; i < slots.Count; i++)
                        if (slots[i]?.ItemInstance != null) { carried += slots[i].Quantity; what += " " + slots[i].Quantity + "x" + slots[i].ItemInstance.Name; }
                MoveItem mover = emp.GetComponentInChildren<MoveItem>(true);
                Core.Log?.Msg("[Dev] [Fix4] " + Rel.Name(emp) + ": carries " + carried + (what.Length > 0 ? " (" + what.Trim() + ")" : "")
                    + " mover=" + (mover == null ? "none" : "active=" + mover.Active + " grabbed=" + mover.grabbedAmount));
                // Disable runs the same prefix and postfix as a real interruption.
                if (cut && mover != null && mover.Enabled) mover.Disable_Networked(null);
            }
            Core.Log?.Msg("[Dev] [Fix4] " + new Fix4StuckItem().Status);
        }
#endif
    }

    /// <summary>MoveItemBehaviour.Deactivate (Il2CppScheduleOne.NPCs.Behaviour/MoveItemBehaviour.cs:1117).</summary>
    [HarmonyPatch(typeof(MoveItem), nameof(MoveItem.Deactivate))]
    internal static class Awe_Fix4_MoveItemDeactivate_Patch
    {
        private static void Prefix(MoveItem __instance, out Fix4StuckItem.Carry __state)
        {
            __state = null;
            try { __state = Fix4StuckItem.Capture(__instance); }
            catch (Exception e) { Core.Failed("fix4: Deactivate prefix", e); }
        }

        private static void Postfix(MoveItem __instance, Fix4StuckItem.Carry __state)
        {
            try { Fix4StuckItem.Return(__instance, __state, "task ended", false); }
            catch (Exception e) { Core.Failed("fix4: Deactivate postfix", e); }
        }
    }

    /// <summary>MoveItemBehaviour.Disable (Il2CppScheduleOne.NPCs.Behaviour/MoveItemBehaviour.cs:1128).</summary>
    [HarmonyPatch(typeof(MoveItem), nameof(MoveItem.Disable))]
    internal static class Awe_Fix4_MoveItemDisable_Patch
    {
        private static void Prefix(MoveItem __instance, out Fix4StuckItem.Carry __state)
        {
            __state = null;
            try { __state = Fix4StuckItem.Capture(__instance); }
            catch (Exception e) { Core.Failed("fix4: Disable prefix", e); }
        }

        private static void Postfix(MoveItem __instance, Fix4StuckItem.Carry __state)
        {
            try { Fix4StuckItem.Return(__instance, __state, "task disabled", false); }
            catch (Exception e) { Core.Failed("fix4: Disable postfix", e); }
        }
    }
}
