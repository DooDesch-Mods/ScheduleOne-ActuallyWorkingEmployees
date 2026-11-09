using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppScheduleOne.GameTime;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Management;
using ActuallyWorkingEmployees.Config;
using ActuallyWorkingEmployees.Features.Reliability;
using ActuallyWorkingEmployees.Networking;
#if DEBUG
using ActuallyWorkingEmployees.Debugging;
#endif

namespace ActuallyWorkingEmployees.Features.Packaging
{
    /// <summary>
    /// Route choice of the packager: batch handling, route priority and route diagnostics.
    ///
    /// Packager.GetTransitRouteReady (Packager.cs:343) takes the first route in list order that has an item to move, a
    /// path to both ends and room in the hands. When batch handling or a route priority is on, a prefix replaces that
    /// loop with the same tests plus two additions: a route whose load is below the batch threshold is skipped until a
    /// timeout runs out, and among the routes that pass, the one with the best score for the chosen priority wins.
    /// With both off the prefix steps aside and the game's own loop runs.
    ///
    /// A packager that finds nothing submits "There's nothing for me to do right now." right after the call. Fix 5's
    /// rewrite asks <see cref="Rewrite"/> first, which swaps in the batch waiting text or the route diagnosis.
    /// </summary>
    internal sealed class PackagerRoutes : IFeature
    {
        internal const string FeatureName = "packager-routes";
        public string Name => FeatureName;

        private enum Release { None, Pass, FullStack, Capacity, EndOfDay, Threshold, Timeout }

        // Start = first moment the load was held back. The record lives until the load leaves or the route has no
        // load any more, so a packager that works on another route in between does not restart the timeout.
        private sealed class Wait { internal float Start; internal int Pass; }

        private sealed class PackerState
        {
            internal readonly Dictionary<long, Wait> Waits = new Dictionary<long, Wait>();
            internal int WaitFrame = -1, Pending, Need, Pass;
            internal string RoutesNoneKey;
            internal string DiagReason, DiagFix;
            internal float LastDiag = -1000f;
        }

        private static readonly Dictionary<int, PackerState> _states = new Dictionary<int, PackerState>();
        private static int _picked, _vetoed, _released, _timeouts, _reordered, _diagnosed;

        public void Init()
        {
#if DEBUG
            DevConsole.Register("batch", "awe batch  (every packager route: ready load, threshold and the decision)", Dev);
            DevConsole.Register("routeui", "awe routeui [routes|count|delete <row>|top|bottom|close]  (give the first packager this many routes, open its panel, count the route rows; delete presses a row's delete button; top/bottom scroll the list)", DevRouteUi);
#endif
        }

        public void Tick(float dt) { }

        public void Reset()
        {
            _states.Clear();
            _picked = _vetoed = _released = _timeouts = _reordered = _diagnosed = 0;
        }

        public string Status =>
            "batch=" + (Options.BatchHandling.Value ? 1 : 0) + " threshold=" + Options.BatchThreshold.Value
            + " mode=" + Options.RoutePriorityMode.Value + " diag=" + (Options.RouteDiagnostics.Value ? 1 : 0)
            + " picked=" + _picked + " vetoed=" + _vetoed + " released=" + _released + " timeouts=" + _timeouts
            + " reordered=" + _reordered + " diagnosed=" + _diagnosed;

        private static float Now => UnityEngine.Time.realtimeSinceStartup;

        private static PackerState StateOf(Packager p)
        {
            int id = p.GetInstanceID();
            if (!_states.TryGetValue(id, out PackerState st)) _states[id] = st = new PackerState();
            return st;
        }

        private static bool BatchActive =>
            Options.BatchHandling.Value && (Options.BatchFullStackOnly.Value || Options.BatchThreshold.Value > 1);

        /// <summary>
        /// Prefix body. True when the choice was made here and the game's loop must not run; <paramref name="result"/> and
        /// <paramref name="item"/> then hold the answer (null route = nothing to do).
        /// </summary>
        internal static bool Select(Packager p, ref ItemInstance item, ref AdvancedTransitRoute result)
        {
            bool batch = BatchActive;
            string mode = Options.RoutePriorityMode.Value;
            bool priority = mode != "Vanilla";
            if (!batch && !priority) return false;
            if (!Net.IsServer) return false;

            var routes = p.configuration?.Routes?.Routes;
            if (routes == null)
            {
                PackerState noRoutes = StateOf(p);
                noRoutes.RoutesNoneKey ??= "routes:none:" + p.GetInstanceID();
                Core.WarnOnce(noRoutes.RoutesNoneKey, Rel.Name(p) + " has no route list; the game's route choice runs.");
                return false;
            }

            PackerState st = StateOf(p);
            st.Pass++;
            float now = Now;
            AdvancedTransitRoute best = null;
            ItemInstance bestItem = null;
            Release bestRelease = Release.None;
            float bestScore = 0f;
            int firstReady = -1, bestIndex = -1;
            bool waiting = false;
            int pending = 0, need = 0;

            for (int i = 0; i < routes.Count; i++)
            {
                AdvancedTransitRoute route = routes[i];
                if (route == null) continue;
                ItemInstance it = route.GetItemReadyToMove();
                if (it == null) continue;
                if (st.Waits.Count > 0 && st.Waits.TryGetValue(route.Pointer.ToInt64(), out Wait seen)) seen.Pass = st.Pass;
                if (!p.Movement.CanGetTo(route.Source, 1f) || !p.Movement.CanGetTo(route.Destination, 1f)
                    || p.Inventory.GetCapacityForItem(it) <= 0) continue;
                if (firstReady < 0) firstReady = i;

                Release rel = Release.Pass;
                if (batch)
                {
                    rel = Decide(route, it, st, now, true, out int n);
                    if (rel == Release.None)
                    {
                        waiting = true;
                        pending = it.Quantity;
                        need = n;
                        _vetoed++;
                        continue;
                    }
                }

                float score = priority ? Score(p, route, it, mode) : 0f;
                if (best == null || score > bestScore)
                {
                    best = route; bestItem = it; bestRelease = rel; bestScore = score; bestIndex = i;
                }
            }

            DropGoneWaits(p, st);

            if (best != null)
            {
                _picked++;
                if (priority && bestIndex != firstReady) _reordered++;
                if (bestRelease != Release.Pass) Departure(p, best, bestItem, bestRelease, st);
                item = bestItem;
                result = best;
                return true;
            }

            item = null;
            result = null;
            if (waiting)
            {
                st.WaitFrame = UnityEngine.Time.frameCount;
                st.Pending = pending;
                st.Need = need;
            }
            return true;
        }

        /// <summary>
        /// Whether the load of <paramref name="route"/> may leave now. <see cref="Release.None"/> = keep waiting. A full
        /// stack, a full destination and the end of the day always release; the threshold releases unless only full
        /// stacks count; the timeout releases whatever is ready. With <paramref name="commit"/> the wait record is
        /// created and refreshed.
        /// </summary>
        private static Release Decide(AdvancedTransitRoute route, ItemInstance it, PackerState st, float now, bool commit, out int need)
        {
            int qty = it.Quantity;
            need = Options.BatchFullStackOnly.Value ? 0 : Options.BatchThreshold.Value;

            int stack = it.StackLimit;
            if (stack > 0 && qty >= stack) return Release.FullStack;

            int room = route.Destination.GetInputCapacityForItem(it, null, true);
            if (room > 0 && qty >= room) return Release.Capacity;

            TimeManager tm = Il2CppScheduleOne.DevUtilities.NetworkSingleton<TimeManager>.Instance;
            if (tm != null && tm.IsEndOfDay) return Release.EndOfDay;

            if (!Options.BatchFullStackOnly.Value && qty >= need) return Release.Threshold;

            long key = route.Pointer.ToInt64();
            st.Waits.TryGetValue(key, out Wait w);
            if (w == null)
            {
                if (!commit) return Release.None;
                w = new Wait { Start = now, Pass = st.Pass };
                st.Waits[key] = w;
                if (Options.LogBatchActivity.Value)
                    Core.Log?.Msg("[Batch] " + route.Source.Name + " -> " + route.Destination.Name + ": " + qty + " " + it.Name
                        + " ready, waiting for " + (need > 0 ? need.ToString() : "a full stack") + " (timeout " + (int)Options.BatchTimeoutSeconds.Value + " s).");
            }
            return now - w.Start >= Options.BatchTimeoutSeconds.Value ? Release.Timeout : Release.None;
        }

        private static readonly List<long> _goneKeys = new List<long>();

        /// <summary>
        /// Removes wait records of routes that had no load (or no longer exist) in the pass that just ran. Records of
        /// routes that still hold a load stay, even while the packager serves another route.
        /// </summary>
        private static void DropGoneWaits(Packager p, PackerState st)
        {
            if (st.Waits.Count == 0) return;
            _goneKeys.Clear();
            foreach (var kv in st.Waits)
                if (kv.Value.Pass != st.Pass) _goneKeys.Add(kv.Key);
            foreach (long key in _goneKeys)
            {
                st.Waits.Remove(key);
                if (Options.LogBatchActivity.Value)
                    Core.Log?.Msg("[Batch] " + Rel.Name(p) + ": wait on a route dropped, it has no load any more.");
            }
            _goneKeys.Clear();
        }

        /// <summary>Books and logs a trip that left after a wait.</summary>
        private static void Departure(Packager p, AdvancedTransitRoute route, ItemInstance it, Release rel, PackerState st)
        {
            long key = route.Pointer.ToInt64();
            if (!st.Waits.TryGetValue(key, out Wait w)) return;   // never waited: a plain departure
            st.Waits.Remove(key);
            _released++;
            if (rel == Release.Timeout) _timeouts++;
            if (Options.LogBatchActivity.Value)
                Core.Log?.Msg("[Batch] " + Rel.Name(p) + " leaves with " + it.Quantity + " x " + it.Name + " after "
                    + (int)(Now - w.Start) + " s (" + rel + ").");
        }

        private static float Score(Packager p, AdvancedTransitRoute route, ItemInstance it, string mode)
        {
            switch (mode)
            {
                case "Fullest": return it.Quantity;
                case "Emptiest":
                    // The destination with the most room left.
                    return route.Destination.GetInputCapacityForItem(it, null, true);
                case "NearestFirst":
                    var points = route.Source.AccessPoints;
                    if (points == null || points.Count == 0 || points[0] == null) return float.MinValue;
                    return -(points[0].position - p.transform.position).sqrMagnitude;
                default: return 0f;
            }
        }

        /// <summary>
        /// Postfix body: when the packager found no route and nothing is held back by a batch, looks for the reason.
        /// </summary>
        internal static void Diagnose(Packager p, AdvancedTransitRoute result)
        {
            if (!Options.RouteDiagnostics.Value || !Net.IsServer) return;
            PackerState st = StateOf(p);
            if (result != null || st.WaitFrame == UnityEngine.Time.frameCount)
            {
                st.DiagReason = null;
                return;
            }
            if (p.TicksSinceLastWork < 2) return;
            float now = Now;
            if (now - st.LastDiag < Options.RouteWarnIntervalSeconds.Value) return;
            st.LastDiag = now;

            if (!FindRouteProblem(p, out string reason, out string fix, out string log))
            {
                st.DiagReason = null;
                return;
            }
            st.DiagReason = reason;
            st.DiagFix = fix;
            _diagnosed++;
            Core.Log?.Warning("[Routes] " + Rel.Name(p) + ": " + log);
        }

        private static bool FindRouteProblem(Packager p, out string reason, out string fix, out string log)
        {
            reason = fix = log = "";
            var routes = p.configuration?.Routes?.Routes;
            if (routes == null || routes.Count == 0) return false;
            for (int i = 0; i < routes.Count; i++)
            {
                AdvancedTransitRoute route = routes[i];
                if (route == null) continue;
                ItemInstance it = route.GetItemReadyToMove();
                if (it == null) continue;
                bool src = route.Source != null && p.Movement.CanGetTo(route.Source, 1f);
                bool dst = route.Destination != null && p.Movement.CanGetTo(route.Destination, 1f);
                if (!src || !dst)
                {
                    string side = src ? "destination" : "source";
                    string name = (src ? route.Destination : route.Source)?.Name ?? "that location";
                    reason = "I can't find a way to walk to " + name + ".";
                    fix = "The route's " + side + " may be on another property or blocked.";
                    log = "route " + side + " '" + name + "' is unreachable; the route is skipped until a path exists.";
                    return true;
                }
                if (p.Inventory.GetCapacityForItem(it) <= 0)
                {
                    reason = "My hands are full.";
                    fix = "Check that the destination has space.";
                    log = "no room in the inventory for " + it.Name + " from '" + route.Source.Name + "'.";
                    return true;
                }
            }
            return false;
        }

        /// <summary>Fix 5 hook: the text a packager shows instead of the generic idle reason, if it has a better one.</summary>
        internal static bool Rewrite(Employee emp, ref string reason, ref string fix)
        {
            Packager p = emp.TryCast<Packager>();
            if (p == null || !_states.TryGetValue(p.GetInstanceID(), out PackerState st)) return false;
            if (st.WaitFrame == UnityEngine.Time.frameCount && Options.ShowBatchWaitingReason.Value)
            {
                reason = st.Need > 0
                    ? "I'm waiting for a full load (" + st.Pending + "/" + st.Need + ")."
                    : "I'm waiting for a full stack (got " + st.Pending + ").";
                fix = "Lower the batch threshold in the Actually Working Employees settings for smaller, more frequent trips.";
                return true;
            }
            if (st.DiagReason != null && Options.RouteDiagnostics.Value)
            {
                reason = st.DiagReason;
                fix = st.DiagFix ?? "";
                return true;
            }
            return false;
        }

#if DEBUG
        private static void DevRouteUi(string[] args)
        {
            if (!DevConsole.RequireHost("routeui")) return;
            var clip = Il2CppScheduleOne.DevUtilities.Singleton<Il2CppScheduleOne.Tools.ManagementClipboard>.Instance;
            if (clip == null) { Core.Log?.Warning("[Dev] [RouteUi] no ManagementClipboard."); return; }
            if (args.Length > 0 && string.Equals(args[0], "count", StringComparison.OrdinalIgnoreCase)) { DevRouteRows(null); return; }
            if (args.Length > 1 && string.Equals(args[0], "delete", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[1], out int nth))
            {
                DevRouteDelete(nth);
                return;
            }
            if (args.Length > 0 && (string.Equals(args[0], "top", StringComparison.OrdinalIgnoreCase) || string.Equals(args[0], "bottom", StringComparison.OrdinalIgnoreCase)))
            {
                bool top = string.Equals(args[0], "top", StringComparison.OrdinalIgnoreCase);
                var panels = UnityEngine.Resources.FindObjectsOfTypeAll<Il2CppScheduleOne.UI.Management.RouteListFieldUI>();
                int moved = 0;
                for (int k = 0; k < panels.Length; k++)
                {
                    var s = panels[k] != null && panels[k].gameObject.activeInHierarchy ? RouteListScroll.Of(panels[k]) : null;
                    if (s == null) continue;
                    s.verticalNormalizedPosition = top ? 1f : 0f;
                    moved++;
                }
                if (moved == 0) Core.Log?.Warning("[Dev] [RouteUi] " + args[0] + ": no open route list with a scroll view.");
                DevRouteRows(null);
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "close", StringComparison.OrdinalIgnoreCase))
            {
                if (clip.IsOpen) clip.Close();
                Core.Log?.Msg("[Dev] [RouteUi] closed.");
                return;
            }
            Packager p = null;
            foreach (Employee emp in Rel.Employees()) { p = emp.TryCast<Packager>(); if (p != null) break; }
            if (p == null || p.configuration?.Routes == null) { Core.Log?.Warning("[Dev] [RouteUi] no packager with a route list. Hire one: addemployee packager <property>."); return; }
            RouteListField field = p.configuration.Routes;
            if (args.Length > 0 && int.TryParse(args[0], out int want))
            {
                int guard = 0;
                while (field.Routes.Count < want && guard++ < 64)
                {
                    int before = field.Routes.Count;
                    field.AddItem(new AdvancedTransitRoute(null, null));
                    if (field.Routes.Count == before) break;
                }
            }
            var list = new Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.Management.IConfigurable>();
            list.Add(new Il2CppScheduleOne.Management.IConfigurable(p.Pointer));
            if (clip.IsOpen) clip.Close();
            clip.Open(list, null);
            DevRouteRows(field);
        }

        /// <summary>
        /// Presses the delete button of the nth row (1-based) of the open route list panel, the way a click does: through
        /// the button's onDeleteClicked event, which RouteListFieldUI.Start or RouteListRows wired.
        /// </summary>
        private static void DevRouteDelete(int nth)
        {
            var panels = UnityEngine.Resources.FindObjectsOfTypeAll<Il2CppScheduleOne.UI.Management.RouteListFieldUI>();
            for (int k = 0; k < panels.Length; k++)
            {
                var panel = panels[k];
                if (panel == null || !panel.gameObject.scene.IsValid() || !panel.gameObject.activeInHierarchy) continue;
                var rows = panel.RouteEntries;
                if (nth < 1 || nth > rows.Length) { Core.Log?.Warning("[Dev] [RouteUi] delete: row " + nth + " of " + rows.Length + " does not exist."); return; }
                RouteListField f = panel.Fields != null && panel.Fields.Count > 0 ? panel.Fields[0] : null;
                int before = f != null ? f.Routes.Count : -1;
                rows[nth - 1].onDeleteClicked.Invoke();
                int after = f != null ? f.Routes.Count : -1;
                Core.Log?.Msg("[Dev] [RouteUi] delete row " + nth + ": routes " + before + " -> " + after + ".");
                DevRouteRows(f);
                return;
            }
            Core.Log?.Warning("[Dev] [RouteUi] delete: no open route list panel.");
        }

        /// <summary>
        /// The scroll state of a panel: viewport and content height, scroll position (1 top, 0 bottom) and whether the Add
        /// button lies fully inside the viewport (addin), measured after a layout pass.
        /// </summary>
        private static string ScrollText(Il2CppScheduleOne.UI.Management.RouteListFieldUI panel)
        {
            var scroll = RouteListScroll.Of(panel);
            if (scroll == null) return " scroll=0";
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(scroll.content);
            Canvas.ForceUpdateCanvases();
            RectTransform view = scroll.viewport;
            Rect vr = view.rect;
            int addIn = -1;
            if (panel.AddButton != null && panel.AddButton.gameObject.activeInHierarchy)
                addIn = Inside(view, vr, panel.AddButton.GetComponent<RectTransform>()) ? 1 : 0;
            // First and last active row (1-based) that lie fully inside the viewport.
            int first = 0, last = 0;
            var rows = panel.RouteEntries;
            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i] == null || !rows[i].gameObject.activeSelf) continue;
                if (!Inside(view, vr, rows[i].GetComponent<RectTransform>())) continue;
                if (first == 0) first = i + 1;
                last = i + 1;
            }
            return " scroll=1 view=" + vr.height.ToString("0") + " content=" + scroll.content.rect.height.ToString("0")
                + " pos=" + scroll.verticalNormalizedPosition.ToString("0.00") + " y=" + scroll.content.anchoredPosition.y.ToString("0")
                + " first=" + first + " last=" + last + " addin=" + addIn;
        }

        /// <summary>
        /// True when <paramref name="item"/> lies vertically inside the viewport rect, compared in the viewport's own units:
        /// the clipboard is a world-space canvas, where a world unit spans hundreds of UI pixels.
        /// </summary>
        private static bool Inside(RectTransform view, Rect vr, RectTransform item)
        {
            Rect r = item.rect;
            float bottom = view.InverseTransformPoint(item.TransformPoint(new Vector3(0f, r.yMin, 0f))).y;
            float top = view.InverseTransformPoint(item.TransformPoint(new Vector3(0f, r.yMax, 0f))).y;
            return bottom >= vr.yMin - 0.5f && top <= vr.yMax + 0.5f;
        }

        /// <summary>Counts the rows of every route list panel in the scene. Rows shown = active row objects.</summary>
        private static void DevRouteRows(RouteListField field)
        {
            var panels = UnityEngine.Resources.FindObjectsOfTypeAll<Il2CppScheduleOne.UI.Management.RouteListFieldUI>();
            int found = 0;
            for (int k = 0; k < panels.Length; k++)
            {
                var panel = panels[k];
                if (panel == null || !panel.gameObject.scene.IsValid()) continue;
                found++;
                var rows = panel.RouteEntries;
                int shown = 0;
                for (int i = 0; i < rows.Length; i++) if (rows[i] != null && rows[i].gameObject.activeSelf) shown++;
                RouteListField f = field ?? (panel.Fields != null && panel.Fields.Count > 0 ? panel.Fields[0] : null);
                Core.Log?.Msg("[Dev] [RouteUi] panel " + panel.name + " active=" + panel.gameObject.activeInHierarchy
                    + ": routes=" + (f != null ? f.Routes.Count : -1) + " max=" + (f != null ? f.MaxRoutes : -1) + " rows=" + rows.Length
                    + " shown=" + shown + " add=" + (panel.AddButton != null && panel.AddButton.gameObject.activeSelf ? 1 : 0)
                    + ScrollText(panel));
            }
            if (found == 0) Core.Log?.Warning("[Dev] [RouteUi] no route list panel in the scene.");
        }

        private static void Dev(string[] args)
        {
            if (!DevConsole.RequireHost("batch")) return;
            int packagers = 0, routesSeen = 0;
            foreach (Employee emp in Rel.Employees())
            {
                Packager p = emp.TryCast<Packager>();
                if (p == null) continue;
                packagers++;
                var routes = p.configuration?.Routes?.Routes;
                if (routes == null) { Core.Log?.Msg("[Dev] [Batch] " + Rel.Name(p) + ": no route list."); continue; }
                PackerState st = StateOf(p);
                for (int i = 0; i < routes.Count; i++)
                {
                    AdvancedTransitRoute route = routes[i];
                    if (route == null) continue;
                    routesSeen++;
                    ItemInstance it = route.GetItemReadyToMove();
                    if (it == null) { Core.Log?.Msg("[Dev] [Batch] " + Rel.Name(p) + " route " + i + ": nothing ready"); continue; }
                    Release rel = Decide(route, it, st, Now, false, out int need);
                    Core.Log?.Msg("[Dev] [Batch] " + Rel.Name(p) + " route " + i + ": ready=" + it.Quantity + " need=" + need
                        + " decision=" + (rel == Release.None ? "wait" : "go:" + rel) + " score=" + Score(p, route, it, Options.RoutePriorityMode.Value));
                }
            }
            Core.Log?.Msg("[Dev] [Batch] " + packagers + " packager(s), " + routesSeen + " route(s). " + new PackagerRoutes().Status);
        }
#endif
    }

    /// <summary>Packager.GetTransitRouteReady (Il2CppScheduleOne.Employees/Packager.cs:1105).</summary>
    [HarmonyPatch(typeof(Packager), nameof(Packager.GetTransitRouteReady))]
    internal static class Awe_Routes_GetTransitRouteReady_Patch
    {
        private static bool Prepare(System.Reflection.MethodBase original) => original == null || ActuallyWorkingEmployees.Compat.NativeMethods.AllowPatch(original);
        private static bool Prefix(Packager __instance, ref ItemInstance item, ref AdvancedTransitRoute __result)
        {
            if (!Rel.Enter("routes-select")) return true;
            try { return !PackagerRoutes.Select(__instance, ref item, ref __result); }
            catch (Exception e)
            {
                Core.Failed("routes: GetTransitRouteReady prefix", e);
                return true;
            }
            finally { Rel.Leave("routes-select"); }
        }

        private static void Postfix(Packager __instance, AdvancedTransitRoute __result)
        {
            try { PackagerRoutes.Diagnose(__instance, __result); }
            catch (Exception e) { Core.Failed("routes: GetTransitRouteReady postfix", e); }
        }
    }
}
