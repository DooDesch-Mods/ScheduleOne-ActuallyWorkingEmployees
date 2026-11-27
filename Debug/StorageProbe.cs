#if DEBUG
using System;
using System.Globalization;
using ActuallyWorkingEmployees.Features;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Storage;
using Il2CppScheduleOne.UI;
using UnityEngine;

namespace ActuallyWorkingEmployees.Debugging
{
    /// <summary>
    /// DEBUG-only storage moves for AWE#68 (the whip put into a storage stays in the inventory).
    ///   open [name]       opens the nearest storage (or the nearest whose name contains name) the way a player does.
    ///   put id            moves the first inventory stack of item id into the first empty slot of the open storage,
    ///                     with the two calls ItemUIManager.EndDrag makes on a drop into an empty slot.
    ///   count             what the player inventory and the open (or last) storage hold, one line each.
    ///   close / reopen    closes the storage menu / closes it and opens the same storage again.
    ///   list / visuals    every storage with its distance / the models the last storage draws for its contents.
    /// </summary>
    internal sealed class StorageProbe : IFeature
    {
        public string Name => "storeprobe";

        private static StorageEntity _last;

        public void Init()
        {
            DevConsole.Register("store", "awe store <open [name]|put id|count|close|reopen|list|visuals>  (storage moves like a player drag, AWE#68)", Dev);
        }

        public void Reset() { _last = null; }
        public void Tick(float dt) { }
        public string Status => "last=" + (_last != null ? _last.StorageEntityName : "none");

        private static void Dev(string[] args)
        {
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "count";
            try
            {
                switch (mode)
                {
                    case "open": Open(args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1) : ""); return;
                    case "list": List(); return;
                    case "visuals": Visuals(); return;
                    case "put":
                        if (args.Length < 2) { Core.Log?.Warning("[Dev] [store] put: item id missing."); return; }
                        Put(args[1]);
                        return;
                    case "close": Close(); return;
                    case "reopen":
                        Close();
                        if (_last == null) { Core.Log?.Warning("[Dev] [store] reopen: no storage was opened before."); return; }
                        OpenEntity(_last);
                        return;
                    default: Count(); return;
                }
            }
            // Logged every time, not once per key like Core.Failed: a repeated throw is what the probe looks for.
            catch (Exception e) { Core.Log?.Error("[Dev] [store] " + mode + " failed: " + e.GetType().Name + ": " + e.Message); }
        }

        private static void Open(string want)
        {
            Player p = Player.Local;
            if (p == null) { Core.Log?.Warning("[Dev] [store] open: no local player."); return; }
            StorageEntity best = null;
            float bestD = float.MaxValue;
            foreach (StorageEntity s in UnityEngine.Object.FindObjectsOfType<StorageEntity>())
            {
                if (s == null) continue;
                string n = s.StorageEntityName ?? s.name;
                if (want.Length > 0 && n.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0 && s.name.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                float d = Vector3.Distance(p.transform.position, s.transform.position);
                if (d < bestD) { bestD = d; best = s; }
            }
            if (best == null) { Core.Log?.Warning("[Dev] [store] open: no storage" + (want.Length > 0 ? " named '" + want + "'" : "") + "."); return; }
            Core.Log?.Msg("[Dev] [store] open: " + best.StorageEntityName + " (" + best.name + ") " + bestD.ToString("F1", CultureInfo.InvariantCulture) + " m away, " + best.ItemSlots.Count + " slots.");
            OpenEntity(best);
        }

        private static void List()
        {
            Player p = Player.Local;
            int n = 0;
            foreach (StorageEntity s in UnityEngine.Object.FindObjectsOfType<StorageEntity>())
            {
                if (s == null) continue;
                n++;
                float d = p != null ? Vector3.Distance(p.transform.position, s.transform.position) : -1f;
                Core.Log?.Msg("[Dev] [store] list: " + s.StorageEntityName + " (" + s.name + ") " + d.ToString("F1", CultureInfo.InvariantCulture) + " m, " + s.ItemSlots.Count + " slots, " + s.ItemCount + " items.");
            }
            Core.Log?.Msg("[Dev] [store] list: " + n + " storage(s).");
        }

        /// <summary>The models the last storage draws for its contents (racks and shelves; closets draw none).</summary>
        private static void Visuals()
        {
            if (_last == null) { Core.Log?.Warning("[Dev] [store] visuals: no storage opened yet."); return; }
            // The visualizer spawns its models under its ItemContainer, which need not sit under the StorageEntity.
            StorageVisualizer vis = _last.GetComponentInChildren<StorageVisualizer>(true) ?? _last.GetComponentInParent<StorageVisualizer>();
            if (vis == null)
            {
                float bestD = float.MaxValue;
                foreach (StorageVisualizer v in UnityEngine.Object.FindObjectsOfType<StorageVisualizer>())
                {
                    if (v == null) continue;
                    float d = Vector3.Distance(v.transform.position, _last.transform.position);
                    if (d < bestD) { bestD = d; vis = v; }
                }
                if (vis != null && bestD > 3f) vis = null;
            }
            if (vis == null) Core.Log?.Warning("[Dev] [store] visuals: " + _last.StorageEntityName + " has no StorageVisualizer within 3 m.");
            Transform container = vis != null ? (vis.ItemContainer != null ? vis.ItemContainer : vis.transform) : _last.transform;
            Core.Log?.Msg("[Dev] [store] visuals: looking under " + container.name + (vis != null ? " (visualizer " + vis.name + ")" : "") + ".");
            int whips = 0;
            foreach (StoredItem si in UnityEngine.Object.FindObjectsOfType<StoredItem>())
                if (si != null && si.name.StartsWith("AWEWhipStoredItem", StringComparison.Ordinal)) whips++;
            Core.Log?.Msg("[Dev] [store] visuals: " + whips + " whip model(s) in the scene.");
            int n = 0;
            foreach (StoredItem si in container.GetComponentsInChildren<StoredItem>(false))
            {
                if (si == null) continue;
                n++;
                Vector3 p = si.transform.position;
                Core.Log?.Msg("[Dev] [store] visual: " + si.name + " at " + p.x.ToString("F2", CultureInfo.InvariantCulture) + " " + p.y.ToString("F2", CultureInfo.InvariantCulture)
                    + " " + p.z.ToString("F2", CultureInfo.InvariantCulture) + ".");
            }
            Vector3 sp = _last.transform.position;
            Core.Log?.Msg("[Dev] [store] visuals: " + n + " drawn in " + _last.StorageEntityName + " at " + sp.x.ToString("F2", CultureInfo.InvariantCulture) + " "
                + sp.y.ToString("F2", CultureInfo.InvariantCulture) + " " + sp.z.ToString("F2", CultureInfo.InvariantCulture) + ".");
        }

        private static void OpenEntity(StorageEntity s)
        {
            _last = s;
            if (!s.CanBeOpened()) { Core.Log?.Warning("[Dev] [store] open: " + s.StorageEntityName + " cannot be opened right now."); return; }
            s.Open();
            var menu = Singleton<StorageMenu>.Instance;
            Core.Log?.Msg("[Dev] [store] opened " + s.StorageEntityName + ": menu open=" + (menu != null && menu.IsOpen) + ".");
        }

        private static void Close()
        {
            var menu = Singleton<StorageMenu>.Instance;
            if (menu == null || !menu.IsOpen) { Core.Log?.Msg("[Dev] [store] close: no storage menu open."); return; }
            menu.Close();
            Core.Log?.Msg("[Dev] [store] closed the storage menu.");
        }

        private static void Put(string id)
        {
            var menu = Singleton<StorageMenu>.Instance;
            bool menuOpen = menu != null && menu.IsOpen && menu.OpenedStorageEntity != null;
            StorageEntity s = menuOpen ? menu.OpenedStorageEntity : _last;
            if (s == null) { Core.Log?.Warning("[Dev] [store] put: no storage chosen ('awe store open' first)."); return; }
            Core.Log?.Msg("[Dev] [store] put: into " + s.StorageEntityName + ", storage menu " + (menuOpen ? "open" : "closed") + ".");
            var inv = PlayerSingleton<PlayerInventory>.Instance;
            if (inv == null) { Core.Log?.Warning("[Dev] [store] put: no player inventory."); return; }

            // ItemSlot.SlotIndex throws for slots without an owner (the player's), so the indices are counted here.
            ItemSlot from = null;
            int fromIndex = -1, toIndex = -1;
            var slots = inv.GetAllInventorySlots();
            for (int i = 0; i < slots.Count; i++)
            {
                ItemSlot sl = slots[i];
                if (sl?.ItemInstance?.Definition != null && sl.ItemInstance.Definition.ID == id) { from = sl; fromIndex = i; break; }
            }
            if (from == null) { Core.Log?.Warning("[Dev] [store] put: no '" + id + "' in the inventory."); return; }
            ItemSlot to = null;
            for (int i = 0; i < s.ItemSlots.Count; i++)
                if (s.ItemSlots[i] != null && s.ItemSlots[i].ItemInstance == null && !s.ItemSlots[i].IsLocked && !s.ItemSlots[i].IsAddLocked) { to = s.ItemSlots[i]; toIndex = i; break; }
            if (to == null) { Core.Log?.Warning("[Dev] [store] put: " + s.StorageEntityName + " has no empty slot."); return; }

            int amount = from.Quantity;
            Core.Log?.Msg("[Dev] [store] put: " + amount + "x " + id + " from inventory slot " + fromIndex + " into " + s.StorageEntityName + " slot " + toIndex
                + " (equipped=" + (inv.isAnythingEquipped && inv.equippedSlot != null && inv.equippedSlot.ItemInstance == from.ItemInstance) + ").");
            // The two calls of ItemUIManager.EndDrag for a drop on an empty slot.
            to.SetStoredItem(from.ItemInstance.GetCopy(amount));
            from.ChangeQuantity(-amount);
            Core.Log?.Msg("[Dev] [store] put: done, inventory slot " + fromIndex + " now " + Describe(from) + ", storage slot " + toIndex + " now " + Describe(to) + ".");
        }

        private static void Count()
        {
            var inv = PlayerSingleton<PlayerInventory>.Instance;
            string invText = "";
            if (inv != null)
            {
                var slots = inv.GetAllInventorySlots();
                for (int i = 0; i < slots.Count; i++) if (slots[i]?.ItemInstance != null) invText += " " + Describe(slots[i]);
            }
            Core.Log?.Msg("[Dev] [store] inventory:" + (invText.Length > 0 ? invText : " empty"));
            var menu = Singleton<StorageMenu>.Instance;
            StorageEntity s = menu != null && menu.IsOpen && menu.OpenedStorageEntity != null ? menu.OpenedStorageEntity : _last;
            if (s == null) { Core.Log?.Msg("[Dev] [store] storage: none opened yet."); return; }
            string st = "";
            for (int i = 0; i < s.ItemSlots.Count; i++) if (s.ItemSlots[i]?.ItemInstance != null) st += " " + Describe(s.ItemSlots[i]);
            Core.Log?.Msg("[Dev] [store] storage " + s.StorageEntityName + " (menu " + (menu != null && menu.IsOpen ? "open" : "closed") + "):" + (st.Length > 0 ? st : " empty"));
        }

        private static string Describe(ItemSlot sl)
        {
            if (sl == null || sl.ItemInstance == null) return "empty";
            return sl.ItemInstance.Definition?.ID + "x" + sl.Quantity;
        }
    }
}
#endif
