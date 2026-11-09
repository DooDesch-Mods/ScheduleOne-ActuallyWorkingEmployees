using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppScheduleOne.Management;
using Il2CppScheduleOne.UI.Management;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ActuallyWorkingEmployees.Features.Packaging
{
    /// <summary>
    /// The packager's route list panel has a fixed number of row objects in its prefab (RouteListFieldUI.RouteEntries).
    /// Refresh shows one row per route up to that count and hides the rest, so routes above it exist in the data but have
    /// no row, and the Add button stays visible while the click adds a route nobody sees. Before the panel binds its
    /// field's routes, the row array is extended with copies of the last row until it has one row per allowed route.
    ///
    /// RouteListFieldUI.Start wires the delete button of every row in the array. Rows added before Start are wired by
    /// Start; rows added after it get their delete listener here.
    /// </summary>
    internal static class RouteListRows
    {
        private static readonly HashSet<int> _started = new HashSet<int>();
        internal static int Added { get; private set; }

        internal static void MarkStarted(RouteListFieldUI ui) { _started.Add(ui.GetInstanceID()); }

        /// <summary>The highest route limit of the bound fields, or 0 while the panel is not bound.</summary>
        internal static int MaxOf(RouteListFieldUI ui)
        {
            var fields = ui.Fields;
            if (fields == null) return 0;
            int max = 0;
            for (int i = 0; i < fields.Count; i++)
                if (fields[i] != null && fields[i].MaxRoutes > max) max = fields[i].MaxRoutes;
            return max;
        }

        /// <summary>Grows the row array to <paramref name="maxRoutes"/> rows. True when rows were added.</summary>
        internal static bool Ensure(RouteListFieldUI ui, int maxRoutes)
        {
            Il2CppReferenceArray<RouteEntryUI> rows = ui.RouteEntries;
            if (rows == null || rows.Length == 0) { Core.WarnOnce("route-rows:none:" + ui.GetInstanceID(), "[route-rows] " + ui.name + " has no route rows to copy."); return false; }
            int have = rows.Length;
            if (maxRoutes <= have) return false;

            RouteEntryUI template = rows[have - 1];
            if (template == null) { Core.WarnOnce("route-rows:gone:" + ui.GetInstanceID(), "[route-rows] the last route row of " + ui.name + " is gone."); return false; }
            Transform parent = template.transform.parent;
            bool started = _started.Contains(ui.GetInstanceID());
            var grown = new Il2CppReferenceArray<RouteEntryUI>(maxRoutes);
            for (int i = 0; i < have; i++) grown[i] = rows[i];
            for (int i = have; i < maxRoutes; i++)
            {
                GameObject copy = UnityEngine.Object.Instantiate(template.gameObject, parent, false);
                copy.name = template.gameObject.name + " " + (i + 1);
                // The Add button is the last child of the row container; a copy goes right after the previous row.
                copy.transform.SetSiblingIndex(grown[i - 1].transform.GetSiblingIndex() + 1);
                copy.SetActive(false);
                RouteEntryUI row = copy.GetComponent<RouteEntryUI>();
                row.onDeleteClicked = new UnityEvent();
                if (started)
                {
                    RouteEntryUI captured = row;
                    row.onDeleteClicked.AddListener((UnityAction)(() => ui.EntryDeleteClicked(captured)));
                }
                grown[i] = row;
            }
            ui.RouteEntries = grown;
            Added += maxRoutes - have;
            Core.Log?.Msg("[route-rows] " + ui.name + ": route rows " + have + " -> " + maxRoutes + (started ? " (delete wired here)." : " (delete wired by Start)."));
            return true;
        }

        internal static void Reset() { _started.Clear(); }
    }

    /// <summary>
    /// The route rows sit in a container without a scroll view, sized for the five prefab rows plus the Add button.
    /// More rows run past the clipboard's edge. When a panel has more than five rows, its row container is moved into a
    /// masked viewport of the vanilla height with a vertical ScrollRect, so every row and the Add button stay reachable
    /// with the mouse wheel.
    /// </summary>
    internal static class RouteListScroll
    {
        internal const string ViewportName = "AWE Route Viewport";
        private const int VanillaRows = 5;
        private static readonly Dictionary<int, int> _lastCount = new Dictionary<int, int>();

        internal static ScrollRect Of(RouteListFieldUI ui)
        {
            RouteEntryUI first = ui.RouteEntries != null && ui.RouteEntries.Length > 0 ? ui.RouteEntries[0] : null;
            Transform contents = first != null ? first.transform.parent : null;
            Transform view = contents != null ? contents.parent : null;
            if (view == null || view.name != ViewportName) return null;
            return view.GetComponent<ScrollRect>();
        }

        /// <summary>Wraps the row container in a scroll viewport once the panel has more rows than the prefab.</summary>
        internal static void Ensure(RouteListFieldUI ui)
        {
            Il2CppReferenceArray<RouteEntryUI> rows = ui.RouteEntries;
            if (rows == null || rows.Length <= VanillaRows || rows[0] == null) return;
            if (Of(ui) != null) return;

            RectTransform contents = rows[0].transform.parent != null ? rows[0].transform.parent.GetComponent<RectTransform>() : null;
            if (contents == null) { Core.WarnOnce("route-scroll:contents:" + ui.GetInstanceID(), "[route-scroll] " + ui.name + ": the route rows have no RectTransform parent."); return; }
            RectTransform panel = ui.GetComponent<RectTransform>();
            if (contents.parent != ui.transform || panel == null)
            {
                Core.WarnOnce("route-scroll:layout:" + ui.GetInstanceID(), "[route-scroll] " + ui.name + ": the row container " + contents.name + " is not a direct child of the panel; rows stay unscrolled.");
                return;
            }

            var group = contents.GetComponent<VerticalLayoutGroup>();
            float spacing = group != null ? group.spacing : 0f;
            float padding = group != null ? group.padding.top + group.padding.bottom : 0f;
            float rowH = rows[0].GetComponent<RectTransform>().rect.height;
            RectTransform add = ui.AddButton != null ? ui.AddButton.GetComponent<RectTransform>() : null;
            // The Add button may sit on a child of the Add row; the row is the container's child.
            RectTransform addRow = add;
            while (addRow != null && addRow.parent != contents) addRow = addRow.parent != null ? addRow.parent.GetComponent<RectTransform>() : null;
            float addH = addRow != null ? addRow.rect.height : rowH;
            float height = padding + VanillaRows * rowH + addH + VanillaRows * spacing;

            var viewGo = new GameObject(ViewportName);
            var view = viewGo.AddComponent<RectTransform>();
            view.SetParent(ui.transform, false);
            view.SetSiblingIndex(contents.GetSiblingIndex());
            view.anchorMin = contents.anchorMin;
            view.anchorMax = contents.anchorMax;
            view.pivot = new Vector2(0.5f, 1f);
            view.anchoredPosition = contents.anchoredPosition;
            view.sizeDelta = new Vector2(contents.sizeDelta.x, height);
            // The clipboard masks the panel a little above the end of the vanilla row block, which cuts off the last row;
            // the viewport ends where that mask ends.
            float room = RoomBelow(view);
            if (room > rowH * 2f && room < height)
            {
                Core.Log?.Msg("[route-scroll] " + ui.name + ": viewport " + height.ToString("0") + " px ends below the clipboard mask; using the visible " + room.ToString("0") + " px.");
                height = room;
                view.sizeDelta = new Vector2(view.sizeDelta.x, height);
            }
            else if (room < 0f) Core.Log?.Msg("[route-scroll] " + ui.name + ": no mask above the panel; viewport keeps " + height.ToString("0") + " px.");
            viewGo.AddComponent<RectMask2D>();
            // A transparent Graphic makes the gaps between rows catch the mouse wheel too.
            var hit = viewGo.AddComponent<Image>();
            hit.color = new Color(0f, 0f, 0f, 0f);

            contents.SetParent(view, false);
            contents.anchorMin = new Vector2(0f, 1f);
            contents.anchorMax = new Vector2(1f, 1f);
            contents.pivot = new Vector2(0.5f, 1f);
            contents.anchoredPosition = Vector2.zero;
            contents.sizeDelta = new Vector2(0f, contents.sizeDelta.y);
            var fitter = contents.GetComponent<ContentSizeFitter>();
            if (fitter == null) fitter = contents.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scroll = viewGo.AddComponent<ScrollRect>();
            scroll.viewport = view;
            scroll.content = contents;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.inertia = false;
            scroll.scrollSensitivity = rowH > 0f ? rowH * 0.5f : 20f;
            LayoutRebuilder.ForceRebuildLayoutImmediate(contents);
            scroll.verticalNormalizedPosition = 1f;
            Core.Log?.Msg("[route-scroll] " + ui.name + ": " + rows.Length + " rows scroll in a " + height.ToString("0") + " px viewport (row " + rowH.ToString("0") + " px, add " + addH.ToString("0") + " px).");
        }

        /// <summary>
        /// Height from the top of <paramref name="view"/> down to the bottom of the nearest masking ancestor, in the
        /// viewport's own units, minus a small margin. -1 when no ancestor masks.
        /// </summary>
        private static float RoomBelow(RectTransform view)
        {
            Transform t = view.parent;
            while (t != null && t.GetComponent<Mask>() == null && t.GetComponent<RectMask2D>() == null) t = t.parent;
            RectTransform mask = t != null ? t.GetComponent<RectTransform>() : null;
            if (mask == null) return -1f;
            var m = new Il2CppStructArray<Vector3>(4);
            var v = new Il2CppStructArray<Vector3>(4);
            mask.GetWorldCorners(m);
            view.GetWorldCorners(v);
            float maskBottom = view.InverseTransformPoint(m[0]).y;
            float viewTop = view.InverseTransformPoint(v[1]).y;
            return viewTop - maskBottom - 4f;
        }

        /// <summary>After a route was added, scrolls to the end so the new row and the Add button stay in view.</summary>
        internal static void AfterRefresh(RouteListFieldUI ui, int count)
        {
            int id = ui.GetInstanceID();
            bool grew = _lastCount.TryGetValue(id, out int last) && count > last;
            _lastCount[id] = count;
            if (!grew) return;
            ScrollRect scroll = Of(ui);
            if (scroll == null) return;
            LayoutRebuilder.ForceRebuildLayoutImmediate(scroll.content);
            scroll.verticalNormalizedPosition = 0f;
        }

        internal static void Reset() { _lastCount.Clear(); }
    }

    /// <summary>
    /// RouteListFieldUI.Start (Il2CppScheduleOne.UI.Management/RouteListFieldUI.cs:265). The config panel calls Bind
    /// from native code that the IL2CPP build compiles Bind into, so a patch on Bind never runs; Start is called by Unity
    /// and always is. The prefix grows the rows before Start wires their delete buttons, the postfix shows the routes
    /// that Bind's Refresh had no row for.
    /// </summary>
    [HarmonyPatch(typeof(RouteListFieldUI), "Start")]
    internal static class Awe_RouteListFieldUI_Start_Patch
    {
        private static bool Prepare(System.Reflection.MethodBase original) => original == null || ActuallyWorkingEmployees.Compat.NativeMethods.AllowPatch(original);

        private static void Prefix(RouteListFieldUI __instance, out bool __state)
        {
            __state = false;
            try
            {
                int max = RouteListRows.MaxOf(__instance);
                if (max <= 0) { Core.Log?.Msg("[route-rows] " + __instance.name + " starts unbound; rows are checked on its next refresh."); return; }
                __state = RouteListRows.Ensure(__instance, max);
            }
            catch (Exception e) { Core.Failed("route-rows: Start prefix", e); }
        }

        private static void Postfix(RouteListFieldUI __instance, bool __state)
        {
            try
            {
                RouteListRows.MarkStarted(__instance);
                if (__state) __instance.Refresh(__instance.Fields[0].Routes);
                RouteListScroll.Ensure(__instance);
            }
            catch (Exception e) { Core.Failed("route-rows: Start postfix", e); }
        }
    }

    /// <summary>
    /// RouteListFieldUI.Refresh, run on every change of the route list. Grows the rows when the route limit was raised
    /// while the panel was open.
    /// </summary>
    [HarmonyPatch(typeof(RouteListFieldUI), "Refresh")]
    internal static class Awe_RouteListFieldUI_Refresh_Patch
    {
        private static bool Prepare(System.Reflection.MethodBase original) => original == null || ActuallyWorkingEmployees.Compat.NativeMethods.AllowPatch(original);
        private static void Prefix(RouteListFieldUI __instance)
        {
            try
            {
                int max = RouteListRows.MaxOf(__instance);
                if (max > 0) RouteListRows.Ensure(__instance, max);
            }
            catch (Exception e) { Core.Failed("route-rows: Refresh prefix", e); }
        }

        private static void Postfix(RouteListFieldUI __instance)
        {
            try
            {
                RouteListScroll.Ensure(__instance);
                var fields = __instance.Fields;
                if (fields != null && fields.Count > 0 && fields[0] != null) RouteListScroll.AfterRefresh(__instance, fields[0].Routes.Count);
            }
            catch (Exception e) { Core.Failed("route-scroll: Refresh postfix", e); }
        }
    }
}
