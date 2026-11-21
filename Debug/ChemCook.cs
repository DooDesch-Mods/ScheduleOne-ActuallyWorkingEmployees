#if DEBUG
using System;
using System.Collections.Generic;
using ActuallyWorkingEmployees.Config;
using ActuallyWorkingEmployees.Features;
using ActuallyWorkingEmployees.Features.Balance;
using ActuallyWorkingEmployees.Features.Reliability;
using ActuallyWorkingEmployees.Networking;
using Il2CppScheduleOne.EntityFramework;
using Il2CppScheduleOne.Management;
using Il2CppScheduleOne.ObjectScripts;
using Il2CppScheduleOne.StationFramework;
using Il2CppScheduleOne.Tiles;
using Il2CppScheduleOne.UI.Stations;
using PropertyT = Il2CppScheduleOne.Property.Property;

namespace ActuallyWorkingEmployees.Debugging
{
    /// <summary>
    /// DEBUG-only measurement of one chemistry station cook (AWE#61).
    ///
    /// 'awe chemcook place [code]' builds a chemistry station and a small storage rack on a free patch of an owned
    /// property and hires a chemist there when the property has none. 'awe chemcook setup [recipe]' assigns the station
    /// to the chemist, selects a recipe, sets the rack as destination and fills the ingredients, so the chemist starts
    /// the cook himself. Each change of state is logged once with the game minutes since the cook started:
    /// started, complete (ChemistryCookOperation.IsComplete), finalized (the operation is gone and the output holds the
    /// product) and moved (the chemist carried the output to the rack).
    /// </summary>
    internal sealed class ChemCook : IFeature
    {
        public string Name => "chemcook";

        private const int RegionW = 10, RegionH = 6;
        private static readonly int[] StationAt = { 1, 2, 0 }, RackAt = { 6, 2, 0 };

        private readonly List<BuildableItem> _items = new List<BuildableItem>();
        private ChemistryStation _station;
        private BuildableItem _rack;
        private PropertyT _property;
        private Chemist _chemist;
        private bool _started, _complete, _finalized, _moved;
        private int _t0, _cook, _vanilla, _output;
        private float _nextRead;
        private string _last = "no cook yet", _recipe = "";
        private readonly System.Text.StringBuilder _events = new System.Text.StringBuilder();

        public void Init()
        {
            DevConsole.Register("chemcook", "awe chemcook <place [code]|setup [recipe]|status|clear>  (one chemistry station cook: start, complete, finalize and chemist move in game minutes)", Dev);
        }

        public void Reset()
        {
            _items.Clear();
            _station = null; _rack = null; _property = null; _chemist = null;
            ClearRun("reset");
        }

        public string Status => _station == null ? "no station" : Line();

        private void ClearRun(string why)
        {
            _started = _complete = _finalized = _moved = false;
            _t0 = _cook = _vanilla = _output = 0;
            _events.Clear();
            _last = "cleared (" + why + ")";
        }

        private void Dev(string[] args)
        {
            if (!DevConsole.RequireHost("chemcook")) return;
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            try
            {
                if (mode == "place") Place(args.Length > 1 ? args[1] : null);
                else if (mode == "setup") Setup(args.Length > 1 ? args[1] : null);
                else if (mode == "clear") Clear();
                else Core.Log?.Msg("[Dev] [chemcook] " + Status);
            }
            catch (Exception e) { Core.Failed("chemcook " + mode, e); }
        }

        private static int GameMinutes() => NetworkSingleton<Il2CppScheduleOne.GameTime.TimeManager>.Instance.GetTotalMinSum();

        private void Place(string code)
        {
            if (_items.Count > 0) { Core.Log?.Warning("[Dev] [chemcook] a rig stands already; 'awe chemcook clear' first."); return; }
            var stationDef = Il2CppScheduleOne.Registry.GetItem("chemistrystation");
            var rackDef = Il2CppScheduleOne.Registry.GetItem("smallstoragerack");
            if (stationDef == null || rackDef == null) { Core.Log?.Warning("[Dev] [chemcook] item 'chemistrystation' or 'smallstoragerack' is not in the registry."); return; }
            var build = NetworkSingleton<Il2CppScheduleOne.Building.BuildManager>.Instance;
            if (build == null) { Core.Log?.Warning("[Dev] [chemcook] no BuildManager in the scene."); return; }

            var owned = PropertyT.OwnedProperties;
            if (owned == null || owned.Count == 0) { Core.Log?.Warning("[Dev] [chemcook] no owned property."); return; }
            Grid grid = null; int ox = 0, oy = 0;
            for (int p = 0; p < owned.Count && grid == null; p++)
            {
                var prop = owned[p];
                if (prop == null || (code != null && !string.Equals(prop.PropertyCode, code, StringComparison.OrdinalIgnoreCase))) continue;
                var grids = prop.Grids;
                for (int g = 0; grids != null && g < grids.Count; g++)
                {
                    if (grids[g] == null || !OvenRig.FreeRegion(grids[g], RegionW, RegionH, out ox, out oy)) continue;
                    grid = grids[g]; _property = prop;
                    break;
                }
            }
            if (grid == null) { Core.Log?.Warning("[Dev] [chemcook] no owned property" + (code == null ? "" : " with code '" + code + "'") + " has a free patch of " + RegionW + " x " + RegionH + " tiles."); return; }

            var s = build.CreateGridItem(stationDef.GetDefaultInstance(1), grid, new Vector2(ox + StationAt[0], oy + StationAt[1]), StationAt[2], "", null);
            var r = build.CreateGridItem(rackDef.GetDefaultInstance(1), grid, new Vector2(ox + RackAt[0], oy + RackAt[1]), RackAt[2], "", null);
            if (s != null) _items.Add(s); else Core.Log?.Warning("[Dev] [chemcook] the chemistry station was not created.");
            if (r != null) _items.Add(r); else Core.Log?.Warning("[Dev] [chemcook] the storage rack was not created.");
            _station = s?.TryCast<ChemistryStation>();
            _rack = r;

            bool hasChemist = FindChemist() != null;
            if (!hasChemist) NetworkSingleton<EmployeeManager>.Instance.CreateNewEmployee(_property, EEmployeeType.Chemist);
            var at = grid.GetTile(new Coordinate(ox + RackAt[0] + 3, oy + RackAt[1]));
            Vector3 player = at != null ? at.transform.position : grid.transform.position;
            Core.Log?.Msg("[Dev] [chemcook] placed " + _items.Count + " item(s) on " + _property.PropertyCode + " at tile " + ox + "," + oy
                + (hasChemist ? "" : ", hired a chemist") + ". player=" + player.x.ToString("F1") + "," + player.y.ToString("F1") + "," + player.z.ToString("F1"));
        }

        private Chemist FindChemist()
        {
            foreach (Employee e in Rel.Employees())
            {
                var c = e.TryCast<Chemist>();
                if (c != null && (_property == null || (c.AssignedProperty != null && c.AssignedProperty.PropertyCode == _property.PropertyCode))) return c;
            }
            return null;
        }

        private void Setup(string recipeName)
        {
            if (_station == null || Rel.IsGone(_station)) { Core.Log?.Warning("[Dev] [chemcook] no rig; 'awe chemcook place' first."); return; }
            if (_station.CurrentCookOperation != null) { Core.Log?.Warning("[Dev] [chemcook] the station still cooks; wait or 'awe chemcook clear'."); return; }
            _chemist = FindChemist();
            if (_chemist == null) { Core.Log?.Warning("[Dev] [chemcook] no chemist on " + _property.PropertyCode + " yet; a hired one needs a few seconds to spawn."); return; }
            var cfg = _station.Configuration?.TryCast<ChemistryStationConfiguration>();
            if (cfg == null) { Core.Log?.Warning("[Dev] [chemcook] the station has no configuration yet."); return; }
            var ccfg = _chemist.configuration;
            if (ccfg == null) { Core.Log?.Warning("[Dev] [chemcook] " + Rel.Name(_chemist) + " has no configuration."); return; }

            StationRecipe recipe = PickRecipe(recipeName);
            if (recipe == null) return;
            for (int i = 0; i < recipe.Ingredients.Count && i < _station.IngredientSlots.Length; i++)
            {
                var ing = recipe.Ingredients[i];
                if (ing.Items == null || ing.Items.Count == 0) { Core.Log?.Warning("[Dev] [chemcook] ingredient " + i + " of " + recipe.RecipeTitle + " names no item."); return; }
                _station.IngredientSlots[i].InsertItem(ing.Items[0].GetDefaultInstance(ing.Quantity));
            }
            if (!_station.HasIngredientsForRecipe(recipe)) { Core.Log?.Warning("[Dev] [chemcook] the ingredients of " + recipe.RecipeTitle + " do not satisfy the station."); return; }

            if (cfg.Recipe.SelectedRecipe == null || cfg.Recipe.SelectedRecipe.RecipeID != recipe.RecipeID) cfg.Recipe.SetRecipe(recipe, true);
            if (_rack != null && cfg.Destination.SelectedObject == null) cfg.Destination.SetObject(_rack, true);
            bool assigned = ccfg.ChemStations != null && ccfg.ChemStations.Contains(_station);
            if (!assigned) ccfg.Stations.AddItem(_station);
            ClearRun("setup");
            _recipe = recipe.RecipeTitle;
            _last = "waiting for the chemist to start";
            Core.Log?.Msg("[Dev] [chemcook] setup: " + recipe.RecipeTitle + " cook=" + recipe.CookTime_Mins + " min game=" + ChemRecipes.VanillaOf(recipe)
                + " min on " + _property.PropertyCode + ", assigned to " + Rel.Name(_chemist) + (assigned ? " (already)" : "")
                + ", destination=" + (cfg.DestinationRoute != null ? "route" : "none") + ", speed=" + Eff.F(Options.ChemistryStationSpeed.Value)
                + " stationSpeed=" + Options.EnableStationSpeed.Value + ".");
        }

        private static StationRecipe PickRecipe(string name)
        {
            var ui = Singleton<ChemistryStationInterface>.Instance;
            var list = ui?.Recipes;
            if (list == null || list.Count == 0) { Core.Log?.Warning("[Dev] [chemcook] no chemistry recipes loaded."); return null; }
            for (int i = 0; i < list.Count; i++)
            {
                StationRecipe r = list[i];
                if (r == null) continue;
                if (name == null || r.RecipeTitle.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 || r.RecipeID.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return r;
            }
            Core.Log?.Warning("[Dev] [chemcook] no recipe matches '" + name + "'.");
            return null;
        }

        private void Clear()
        {
            int n = 0;
            foreach (var item in _items)
            {
                if (Rel.IsGone(item)) continue;
                item.Destroy_Server();
                n++;
            }
            _items.Clear();
            _station = null; _rack = null; _property = null; _chemist = null;
            ClearRun("command");
            Core.Log?.Msg("[Dev] [chemcook] removed " + n + " rig item(s).");
        }

        private void Event(string name, string extra)
        {
            int gm = GameMinutes() - _t0;
            if (_events.Length > 0) _events.Append(',');
            _events.Append(name).Append('@').Append(gm);
            _last = name + "@" + gm;
            Core.Log?.Msg("[Dev] [chemcook] event " + name + " t=" + gm + " game min, cook=" + _cook + " game=" + _vanilla + (extra.Length > 0 ? " " + extra : ""));
        }

        private string Line()
        {
            var op = _station.CurrentCookOperation;
            string head = "station=" + _station.name + " recipe=" + _recipe + " cook=" + _cook + " game=" + _vanilla;
            if (op == null) return head + " op=none output=" + (_station.OutputSlot != null ? _station.OutputSlot.Quantity : -1) + " last=" + _last + " events=" + _events;
            return head + " progress=" + op.CurrentTime + "/" + op.Recipe.CookTime_Mins + " complete=" + op.IsComplete() + " last=" + _last + " events=" + _events;
        }

        public void Tick(float dt)
        {
            if (_station == null || _moved || !Net.IsServer) return;
            float now = Time.unscaledTime;
            if (now < _nextRead) return;
            _nextRead = now + 0.05f;
            ChemistryCookOperation op;
            int output;
            try { op = _station.CurrentCookOperation; output = _station.OutputSlot != null ? _station.OutputSlot.Quantity : 0; }
            catch (Exception e) { Core.Caught("chemcook: read station", e); _station = null; return; }

            if (!_started)
            {
                if (op == null) return;
                _started = true;
                _t0 = GameMinutes();
                _cook = op.Recipe != null ? op.Recipe.CookTime_Mins : -1;
                _vanilla = ChemRecipes.VanillaOf(op.Recipe);
                Event("started", "progress=" + op.CurrentTime);
                return;
            }
            if (op != null)
            {
                if (!_complete && op.IsComplete()) { _complete = true; Event("complete", "progress=" + op.CurrentTime); }
                return;
            }
            if (!_finalized && output > 0) { _finalized = true; _output = output; Event("finalized", "output=" + output); return; }
            if (_finalized && output == 0) { _moved = true; Event("moved", "carried=" + _output); }
        }
    }
}
#endif
