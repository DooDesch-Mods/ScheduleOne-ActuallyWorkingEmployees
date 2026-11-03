#if DEBUG
using System;
using System.Collections.Generic;
using ActuallyWorkingEmployees.Features;
using ActuallyWorkingEmployees.Features.Reliability;
using Il2CppScheduleOne.EntityFramework;
using Il2CppScheduleOne.ObjectScripts;
using Il2CppScheduleOne.Tiles;
using Il2CppScheduleOne.Trash;
using PropertyT = Il2CppScheduleOne.Property.Property;

namespace ActuallyWorkingEmployees.Debugging
{
    /// <summary>
    /// DEBUG-only cleaner test rig, so cleaner walks can be measured like the other roles. 'awe cleanerrig setup [code]'
    /// picks an owned property with a free employee home and a disposal area, places two trash cans on a free patch when
    /// the property has fewer than two, and hires a cleaner there when it has none. Once the cleaner exists, the rig
    /// assigns it the free home and up to six cans, marks it paid for today, and keeps trash lying in the pickup area of
    /// every assigned can, so the cleaner always has work. 'awe cleanerrig status' and 'awe cleanerrig stop'.
    /// </summary>
    internal sealed class CleanerRig : IFeature
    {
        public string Name => "cleanerrig";

        private const int RegionW = 18, RegionH = 4;
        private static readonly int[][] CanTiles = { new[] { 1, 1 }, new[] { 15, 1 } };
        private const int MinTrashPerCan = 4, MaxSpawned = 600;
        private const float FillSeconds = 15f, AssignSeconds = 2f, TrashSpread = 1.8f;

        private readonly List<TrashContainerItem> _cans = new List<TrashContainerItem>();
        private readonly List<BuildableItem> _placed = new List<BuildableItem>();
        private PropertyT _property;
        private BuildableItem _home;
        private Cleaner _cleaner;
        private bool _active, _assigned, _hired;
        private float _nextFill, _nextAssign;
        private int _spawned, _fills;
        private readonly System.Random _rng = new System.Random(7);

        public void Init()
        {
            DevConsole.Register("cleanerrig", "awe cleanerrig <setup [code]|status|stop>  (cleaner with a home, cans and steady trash)", Dev);
        }

        public void Reset()
        {
            _cans.Clear();
            _placed.Clear();
            _property = null;
            _home = null;
            _cleaner = null;
            _active = _assigned = _hired = false;
            _spawned = _fills = 0;
        }

        public string Status => "active=" + _active + " property=" + (_property == null ? "-" : _property.PropertyCode) + " cans=" + _cans.Count
            + " cleaner=" + (_cleaner == null ? "-" : Rel.Name(_cleaner)) + " assigned=" + _assigned + " spawned=" + _spawned + " fills=" + _fills
            + " trashInRadius=" + TrashInRadius();

        private void Dev(string[] args)
        {
            if (!DevConsole.RequireHost("cleanerrig")) return;
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            try
            {
                if (mode == "setup") Setup(args.Length > 1 ? args[1] : null);
                else if (mode == "stop") { _active = false; Core.Log?.Msg("[Dev] [cleanerrig] stopped; " + Status); }
                else Core.Log?.Msg("[Dev] [cleanerrig] " + Status);
            }
            catch (Exception e) { Core.Failed("cleanerrig " + mode, e); }
        }

        private void Setup(string code)
        {
            if (_active) { Core.Log?.Warning("[Dev] [cleanerrig] already set up; " + Status); return; }
            var owned = PropertyT.OwnedProperties;
            if (owned == null || owned.Count == 0) { Core.Log?.Warning("[Dev] [cleanerrig] no owned property."); return; }
            PropertyT best = null;
            BuildableItem bestHome = null;
            int bestCans = -1;
            for (int i = 0; i < owned.Count; i++)
            {
                PropertyT p = owned[i];
                if (p == null || (code != null && !string.Equals(p.PropertyCode, code, StringComparison.OrdinalIgnoreCase))) continue;
                if (p.DisposalArea == null) { Core.Log?.Msg("[Dev] [cleanerrig] " + p.PropertyCode + ": no disposal area."); continue; }
                BuildableItem home = FreeHome(p);
                if (home == null) { Core.Log?.Msg("[Dev] [cleanerrig] " + p.PropertyCode + ": no free employee home."); continue; }
                int cans = CansOf(p, null);
                if (cans > bestCans) { best = p; bestHome = home; bestCans = cans; }
            }
            if (best == null) { Core.Log?.Warning("[Dev] [cleanerrig] no owned property" + (code == null ? "" : " '" + code + "'") + " has a free home and a disposal area."); return; }
            _property = best;
            _home = bestHome;
            _cans.Clear();
            CansOf(best, _cans);
            if (_cans.Count < 2) PlaceCans(best);
            if (_cans.Count == 0) { Core.Log?.Warning("[Dev] [cleanerrig] " + best.PropertyCode + ": no trash can to assign."); _property = null; return; }

            _cleaner = FindCleaner();
            if (_cleaner == null)
            {
                NetworkSingleton<EmployeeManager>.Instance.CreateNewEmployee(best, EEmployeeType.Cleaner);
                _hired = true;
            }
            _active = true;
            _assigned = false;
            _nextAssign = _nextFill = 0f;
            Core.Log?.Msg("[Dev] [cleanerrig] set up on " + best.PropertyCode + ": " + _cans.Count + " can(s), home " + _home.name
                + (_hired ? ", hired a cleaner" : ", cleaner " + Rel.Name(_cleaner)) + ".");
        }

        public void Tick(float dt)
        {
            if (!_active) return;
            float now = Time.time;
            if (!_assigned && now >= _nextAssign)
            {
                _nextAssign = now + AssignSeconds;
                TryAssign();
            }
            if (_assigned && now >= _nextFill)
            {
                _nextFill = now + FillSeconds;
                Fill();
            }
        }

        private void TryAssign()
        {
            if (_cleaner == null || Rel.IsGone(_cleaner)) _cleaner = FindCleaner();
            if (_cleaner == null) return;   // a hired cleaner needs a few seconds to spawn
            var cfg = _cleaner.configuration;
            if (cfg == null) { Rel.MsgOnce("cleanerrig: no config", "[Dev] [cleanerrig] " + Rel.Name(_cleaner) + " has no configuration yet."); return; }
            if (cfg.assignedHome == null) cfg.Home.SetObject(_home, true);
            for (int i = 0; i < _cans.Count && cfg.binItems.Count < Cleaner.MAX_ASSIGNED_BINS; i++)
                if (!Rel.IsGone(_cans[i])) cfg.Bins.AddItem(_cans[i]);
            _cleaner.SetIsPaid();
            _assigned = cfg.binItems.Count > 0 && cfg.assignedHome != null;
            Core.Log?.Msg("[Dev] [cleanerrig] " + Rel.Name(_cleaner) + ": home=" + (cfg.assignedHome != null) + " bins=" + cfg.binItems.Count
                + " paid=" + _cleaner.PaidForToday + (_assigned ? ", rig running." : ", retrying."));
        }

        private void Fill()
        {
            if (!_cleaner.PaidForToday) _cleaner.SetIsPaid();
            if (_spawned >= MaxSpawned) { Rel.MsgOnce("cleanerrig: cap", "[Dev] [cleanerrig] spawned " + _spawned + " trash item(s), the cap; no more are added."); return; }
            var tm = NetworkSingleton<TrashManager>.Instance;
            if (tm == null) { Rel.MsgOnce("cleanerrig: no tm", "[Dev] [cleanerrig] no TrashManager in the scene."); return; }
            int added = 0;
            for (int i = 0; i < _cans.Count; i++)
            {
                TrashContainerItem can = _cans[i];
                if (Rel.IsGone(can)) continue;
                for (int n = can.TrashItemsInRadius.Count; n < MinTrashPerCan && _spawned < MaxSpawned; n++)
                {
                    TrashItem prefab = tm.GetRandomGeneratableTrashPrefab();
                    if (prefab == null) { Rel.MsgOnce("cleanerrig: no prefab", "[Dev] [cleanerrig] no generatable trash prefab."); return; }
                    double a = _rng.NextDouble() * Math.PI * 2, r = 0.8 + _rng.NextDouble() * (TrashSpread - 0.8);
                    Vector3 p = can.transform.position + new Vector3((float)(Math.Cos(a) * r), 0.5f, (float)(Math.Sin(a) * r));
                    if (Rel.TryOnNavMesh(p, out Vector3 snapped)) p = snapped + Vector3.up * 0.3f;
                    tm.CreateTrashItem(prefab.ID, p, Quaternion.identity, Vector3.zero, "");
                    _spawned++;
                    added++;
                }
            }
            _fills++;
            if (added > 0 && (_fills == 1 || _fills % 20 == 0))
                Core.Log?.Msg("[Dev] [cleanerrig] fill #" + _fills + ": added " + added + " trash item(s), " + _spawned + " in total.");
        }

        private static BuildableItem FreeHome(PropertyT p)
        {
            var items = p.BuildableItems;
            if (items == null) return null;
            for (int i = 0; i < items.Count; i++)
            {
                BuildableItem b = items[i];
                if (b == null) continue;
                EmployeeHome h = b.GetComponent<EmployeeHome>();
                if (h != null && h.AssignedEmployee == null) return b;
            }
            return null;
        }

        private static int CansOf(PropertyT p, List<TrashContainerItem> into)
        {
            var items = p.BuildableItems;
            if (items == null) return 0;
            int n = 0;
            for (int i = 0; i < items.Count; i++)
            {
                TrashContainerItem c = items[i] == null ? null : items[i].TryCast<TrashContainerItem>();
                if (c == null || !c.UsableByCleaners) continue;
                n++;
                into?.Add(c);
            }
            return n;
        }

        private void PlaceCans(PropertyT p)
        {
            var def = Il2CppScheduleOne.Registry.GetItem("trashcan");
            var build = NetworkSingleton<Il2CppScheduleOne.Building.BuildManager>.Instance;
            if (def == null || build == null) { Core.Log?.Warning("[Dev] [cleanerrig] item 'trashcan' or the BuildManager is missing; no can placed."); return; }
            var grids = p.Grids;
            if (grids == null) { Core.Log?.Warning("[Dev] [cleanerrig] " + p.PropertyCode + " has no grids; no can placed."); return; }
            for (int g = 0; g < grids.Count; g++)
            {
                Grid grid = grids[g];
                if (grid == null || !FreeRegion(grid, out int ox, out int oy)) continue;
                foreach (int[] t in CanTiles)
                {
                    var item = build.CreateGridItem(def.GetDefaultInstance(1), grid, new Vector2(ox + t[0], oy + t[1]), 0, "", null);
                    TrashContainerItem can = item == null ? null : item.TryCast<TrashContainerItem>();
                    if (can == null) { Core.Log?.Warning("[Dev] [cleanerrig] trash can at " + (ox + t[0]) + "," + (oy + t[1]) + " was not created."); continue; }
                    _placed.Add(item);
                    _cans.Add(can);
                }
                Core.Log?.Msg("[Dev] [cleanerrig] placed " + _placed.Count + " trash can(s) on " + p.PropertyCode + " at tile " + ox + "," + oy + ".");
                return;
            }
            Core.Log?.Warning("[Dev] [cleanerrig] " + p.PropertyCode + ": no free patch of " + RegionW + " x " + RegionH + " tiles for the cans.");
        }

        // Same search as OvenRig: copy the grid once, then look for a free rectangle without interop calls.
        private static bool FreeRegion(Grid grid, out int ox, out int oy)
        {
            ox = oy = 0;
            var tiles = grid.Tiles;
            if (tiles == null || tiles.Count == 0) return false;
            int maxX = 0, maxY = 0;
            for (int i = 0; i < tiles.Count; i++) { if (tiles[i] == null) continue; maxX = Math.Max(maxX, tiles[i].x); maxY = Math.Max(maxY, tiles[i].y); }
            var free = new bool[maxX + 1, maxY + 1];
            for (int i = 0; i < tiles.Count; i++)
            {
                var t = tiles[i];
                if (t == null || t.x < 0 || t.y < 0) continue;
                free[t.x, t.y] = (t.BuildableOccupants == null || t.BuildableOccupants.Count == 0) && t.CanBeBuiltOn();
            }
            for (int x = 0; x + RegionW <= maxX + 1; x++)
                for (int y = 0; y + RegionH <= maxY + 1; y++)
                {
                    bool ok = true;
                    for (int i = 0; i < RegionW && ok; i++)
                        for (int j = 0; j < RegionH && ok; j++)
                            ok = free[x + i, y + j];
                    if (ok) { ox = x; oy = y; return true; }
                }
            return false;
        }

        private Cleaner FindCleaner()
        {
            foreach (Employee e in Rel.Employees())
            {
                Cleaner c = e.TryCast<Cleaner>();
                if (c != null && c.AssignedProperty != null && _property != null && c.AssignedProperty.PropertyCode == _property.PropertyCode) return c;
            }
            return null;
        }

        private int TrashInRadius()
        {
            int n = 0;
            foreach (TrashContainerItem c in _cans) if (!Rel.IsGone(c)) n += c.TrashItemsInRadius.Count;
            return n;
        }
    }
}
#endif
