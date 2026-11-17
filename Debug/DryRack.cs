#if DEBUG
using System;
using ActuallyWorkingEmployees.Config;
using ActuallyWorkingEmployees.Features;
using ActuallyWorkingEmployees.Features.Balance;
using ActuallyWorkingEmployees.Features.Reliability;
using Il2CppScheduleOne.ObjectScripts;
using Il2CppScheduleOne.Tiles;
using PropertyT = Il2CppScheduleOne.Property.Property;

namespace ActuallyWorkingEmployees.Debugging
{
    /// <summary>
    /// DEBUG-only measurement of the drying rack speed (AWE#46).
    ///   setup [item] [qty]  uses the first drying rack on an owned property, or places one, fills its input and starts
    ///                       one drying operation the way a player does (DryingRack.StartOperation).
    ///   mark                sets every operation of the rack to 0 drying minutes and starts a live measurement.
    ///   status              live rate since the mark: drying minutes gained per game minute.
    ///   pass [minutes]      sets the operations to 0 and calls DryingRack.OnTimePass(minutes) once, the path a time skip
    ///                       (sleeping) takes; reports the drying minutes gained per game minute.
    ///   clear               removes a rack this command placed.
    /// The expected rate is the vanilla multiplier (temperature) times the speed option.
    /// </summary>
    internal sealed class DryRack : IFeature
    {
        public string Name => "dryrack";

        private DryingRack _rack;
        private bool _placed;
        private int _markMin = -1;
        private float _markTime;

        public void Init()
        {
            DevConsole.Register("dryrack", "awe dryrack <setup [item] [qty]|mark|status|pass [minutes]|clear>  (drying minutes per game minute of one drying rack)", Dev);
        }

        public void Reset()
        {
            _rack = null;
            _placed = false;
            _markMin = -1;
        }

        public void Tick(float dt) { }

        public string Status => _rack == null ? "no rack" : "rack=" + _rack.name + " placed=" + _placed + " mark=" + _markMin;

        private void Dev(string[] args)
        {
            if (!DevConsole.RequireHost("dryrack")) return;
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            try
            {
                if (mode == "setup") Setup(args.Length > 1 ? args[1] : null, args.Length > 2 && int.TryParse(args[2], out int q) ? q : 5);
                else if (mode == "mark") Mark();
                else if (mode == "pass") Pass(args.Length > 1 && int.TryParse(args[1], out int m) ? m : 30);
                else if (mode == "clear") Clear();
                else Report();
            }
            catch (Exception e) { Core.Failed("dryrack " + mode, e); }
        }

        private static int GameMinutes() => NetworkSingleton<Il2CppScheduleOne.GameTime.TimeManager>.Instance.GetTotalMinSum();

        /// <summary>Vanilla multiplier from the rack's temperature, the same formula as DryingRack.GetDryMultiplier.</summary>
        private float VanillaMultiplier()
        {
            float t = _rack.GetAverageTileTemperature();
            return t > 20f ? Mathf.Lerp(1f, 1.5f, (t - 20f) / 20f) : 1f;
        }

        private string Expect() => "speed=" + Eff.F(StationBalance.Speed(Options.DryingRackSpeed)) + " option=" + Eff.F(Options.DryingRackSpeed.Value)
            + " enabled=" + Options.EnableStationSpeed.Value + " vanillaMult=" + Eff.F(VanillaMultiplier())
            + " expected=" + Eff.F(VanillaMultiplier() * StationBalance.Speed(Options.DryingRackSpeed))
            + " getDryMultiplier=" + Eff.F(_rack.GetDryMultiplier());

        private bool Ready()
        {
            if (_rack == null || Rel.IsGone(_rack)) { Core.Log?.Warning("[Dev] [dryrack] no rack; 'awe dryrack setup' first."); _rack = null; return false; }
            var ops = _rack.DryingOperations;
            if (ops == null || ops.Count == 0) { Core.Log?.Warning("[Dev] [dryrack] " + _rack.name + " has no drying operation yet."); return false; }
            return true;
        }

        private void ZeroOperations()
        {
            var ops = _rack.DryingOperations;
            for (int i = 0; i < ops.Count; i++) if (ops[i] != null) ops[i].Time = 0f;
        }

        private void Setup(string itemId, int qty)
        {
            _rack = FindRack(out PropertyT property);
            if (_rack == null && !PlaceRack(out property)) return;
            if (_rack.InputSlot == null) { Core.Log?.Warning("[Dev] [dryrack] " + _rack.name + " has no input slot."); return; }
            string id = itemId ?? "ogkush";
            var def = Il2CppScheduleOne.Registry.GetItem(id);
            if (def == null) { Core.Log?.Warning("[Dev] [dryrack] item '" + id + "' is not in the registry."); return; }
            if (_rack.InputSlot.Quantity == 0) _rack.InputSlot.InsertItem(def.GetDefaultInstance(qty));
            if (!_rack.CanStartOperation()) { Core.Log?.Warning("[Dev] [dryrack] " + _rack.name + " cannot start: input=" + _rack.InputSlot.Quantity + " drying=" + _rack.DryingOperations.Count + "."); return; }
            _rack.StartOperation();
            _markMin = -1;
            Vector3 p = _rack.transform.position;
            Core.Log?.Msg("[Dev] [dryrack] setup: started " + id + " in " + _rack.name + " on " + property.PropertyCode + (_placed ? " (placed)" : "")
                + " rack=" + p.x.ToString("F1") + "," + p.y.ToString("F1") + "," + p.z.ToString("F1") + ".");
        }

        private void Mark()
        {
            if (!Ready()) return;
            ZeroOperations();
            _markMin = GameMinutes();
            _markTime = 0f;
            Core.Log?.Msg("[Dev] [dryrack] mark at game minute " + _markMin + ", " + Expect() + ".");
        }

        private void Report()
        {
            if (!Ready()) return;
            float t = _rack.DryingOperations[0].Time;
            int now = GameMinutes();
            string rate = _markMin < 0 ? "no mark" : (now == _markMin ? "no minute yet" : Eff.F((t - _markTime) / (now - _markMin)));
            Core.Log?.Msg("[Dev] [dryrack] status: ops=" + _rack.DryingOperations.Count + " time=" + Eff.F(t) + " minutes=" + (_markMin < 0 ? 0 : now - _markMin)
                + " rate=" + rate + " " + Expect() + ".");
        }

        private void Pass(int minutes)
        {
            if (!Ready()) return;
            minutes = Math.Max(1, Math.Min(minutes, 40));
            ZeroOperations();
            _markMin = -1;
            _rack.OnTimePass(minutes);
            float t = _rack.DryingOperations.Count > 0 ? _rack.DryingOperations[0].Time : -1f;
            Core.Log?.Msg("[Dev] [dryrack] pass: minutes=" + minutes + " time=" + Eff.F(t) + " rate=" + Eff.F(t / minutes) + " " + Expect() + ".");
        }

        private static DryingRack FindRack(out PropertyT property)
        {
            property = null;
            var owned = PropertyT.OwnedProperties;
            for (int p = 0; owned != null && p < owned.Count; p++)
            {
                var items = owned[p]?.BuildableItems;
                for (int i = 0; items != null && i < items.Count; i++)
                {
                    var r = items[i]?.TryCast<DryingRack>();
                    if (r == null || r.DryingOperations == null || r.DryingOperations.Count > 0) continue;
                    property = owned[p];
                    return r;
                }
            }
            return null;
        }

        private bool PlaceRack(out PropertyT property)
        {
            property = null;
            var def = Il2CppScheduleOne.Registry.GetItem("dryingrack");
            if (def == null) { Core.Log?.Warning("[Dev] [dryrack] item 'dryingrack' is not in the registry."); return false; }
            var build = NetworkSingleton<Il2CppScheduleOne.Building.BuildManager>.Instance;
            if (build == null) { Core.Log?.Warning("[Dev] [dryrack] no BuildManager in the scene."); return false; }
            var owned = PropertyT.OwnedProperties;
            for (int p = 0; owned != null && p < owned.Count; p++)
            {
                var grids = owned[p]?.Grids;
                for (int g = 0; grids != null && g < grids.Count; g++)
                {
                    if (grids[g] == null || !FreeRegion(grids[g], 6, 6, out int ox, out int oy)) continue;
                    var item = build.CreateGridItem(def.GetDefaultInstance(1), grids[g], new Vector2(ox + 2, oy + 2), 0, "", null);
                    _rack = item?.TryCast<DryingRack>();
                    if (_rack == null) { Core.Log?.Warning("[Dev] [dryrack] the drying rack on " + owned[p].PropertyCode + " was not created."); return false; }
                    _placed = true;
                    property = owned[p];
                    return true;
                }
            }
            Core.Log?.Warning("[Dev] [dryrack] no drying rack and no free patch of 6 x 6 tiles on an owned property.");
            return false;
        }

        private static bool FreeRegion(Grid grid, int w, int h, out int ox, out int oy)
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
            for (int x = 0; x + w <= maxX + 1; x++)
                for (int y = 0; y + h <= maxY + 1; y++)
                {
                    bool ok = true;
                    for (int i = 0; i < w && ok; i++)
                        for (int j = 0; j < h && ok; j++)
                            ok = free[x + i, y + j];
                    if (ok) { ox = x; oy = y; return true; }
                }
            return false;
        }

        private void Clear()
        {
            if (_placed && _rack != null && !Rel.IsGone(_rack)) { _rack.Destroy_Server(); Core.Log?.Msg("[Dev] [dryrack] removed the placed rack."); }
            else Core.Log?.Msg("[Dev] [dryrack] nothing placed to remove.");
            Reset();
        }
    }
}
#endif
