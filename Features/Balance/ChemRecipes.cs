using System;
using System.Collections.Generic;
using Il2CppScheduleOne.StationFramework;
using Il2CppScheduleOne.UI.Stations;
using ActuallyWorkingEmployees.Config;

namespace ActuallyWorkingEmployees.Features.Balance
{
    /// <summary>
    /// Chemistry station speed. Every reader of a chemistry cook (the progress check in ChemistryStation.OnTimePass, the
    /// station clock, the finish check of the station screen and the recipe list) reads StationRecipe.CookTime_Mins, so
    /// writing that one field keeps them all on the same time. This avoids the lab oven trap of AWE#32, where one reader
    /// was patched and another still read the game's value, and needs no Harmony patch at all.
    ///
    /// Recipes are ScriptableObject assets shared by every station and they outlive the save, so the game's value is
    /// remembered per recipe for the whole process and written back when the save ends.
    /// </summary>
    internal static class ChemRecipes
    {
        private static readonly Dictionary<int, int> _vanilla = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> _written = new Dictionary<int, int>();
        private static float _lastSpeed = 1f;

        private static Il2CppSystem.Collections.Generic.List<StationRecipe> Recipes(out ChemistryStationInterface ui)
        {
            ui = Singleton<ChemistryStationInterface>.Instance;
            if (ui == null) { Core.InfoOnce("chem-recipes:no-ui", "[bal-stations] the chemistry station screen is not loaded yet; recipes follow once it is."); return null; }
            var list = ui.Recipes;
            if (list == null) Throttle.Warn("bal-stations: chem recipes", "ChemistryStationInterface.Recipes is null; chemistry station speed is not applied.");
            return list;
        }

        /// <summary>Writes the cook time of every chemistry recipe for the speed s. A speed of 1 restores the game's time.</summary>
        internal static void Apply(float s)
        {
            var list = Recipes(out ChemistryStationInterface ui);
            if (list == null) return;
            bool active = Math.Abs(s - 1f) > 1e-4f;
            int changed = 0;
            for (int i = 0; i < list.Count; i++)
            {
                StationRecipe r = list[i];
                if (r == null) continue;
                int id = r.GetInstanceID();
                int live = r.CookTime_Mins;
                if (!_vanilla.TryGetValue(id, out int van)) { van = live; _vanilla[id] = live; }
                else if (_written.TryGetValue(id, out int wrote) ? live != wrote : live != van)
                {
                    Core.Log?.Msg("[bal-stations] recipe " + r.RecipeTitle + ": cook time is now " + live + " min, set by the game or another mod; that is its base time from here on.");
                    van = live;
                    _vanilla[id] = live;
                    _written.Remove(id);
                }
                int want = active ? Math.Max(1, (int)Math.Round(van / s)) : van;
                if (live == want) continue;
                r.CookTime_Mins = want;
                if (active) _written[id] = want; else _written.Remove(id);
                changed++;
            }
            if (changed == 0) return;
            RefreshEntries(ui);
            Core.Log?.Msg("[bal-stations] chemistry station speed " + Eff.F(s) + ": " + changed + " recipe cook time(s) written (" + Describe() + ").");
            _lastSpeed = s;
        }

        /// <summary>Puts the game's cook time back on every recipe this class changed.</summary>
        internal static void Restore()
        {
            if (_written.Count == 0) return;
            var list = Recipes(out ChemistryStationInterface ui);
            if (list == null) { Core.Log?.Warning("[bal-stations] " + _written.Count + " chemistry recipe(s) keep the changed cook time: the recipe list is gone."); _written.Clear(); return; }
            int restored = 0;
            for (int i = 0; i < list.Count; i++)
            {
                StationRecipe r = list[i];
                if (r == null) continue;
                int id = r.GetInstanceID();
                if (!_written.ContainsKey(id) || !_vanilla.TryGetValue(id, out int van)) continue;
                r.CookTime_Mins = van;
                restored++;
            }
            _written.Clear();
            _lastSpeed = 1f;
            RefreshEntries(ui);
            Core.Log?.Msg("[bal-stations] save ended: " + restored + " chemistry recipe cook time(s) back to the game's value.");
        }

        /// <summary>The recipe list of the station screen prints the cook time once, when the screen is built.</summary>
        private static void RefreshEntries(ChemistryStationInterface ui)
        {
            var entries = ui.recipeEntries;
            if (entries == null) { Throttle.Warn("bal-stations: chem entries", "ChemistryStationInterface.recipeEntries is null; the recipe list shows the old cook time."); return; }
            for (int i = 0; i < entries.Count; i++)
            {
                StationRecipeEntry e = entries[i];
                if (e == null || e.Recipe == null) continue;
                try { e.AssignRecipe(e.Recipe); }
                catch (Exception ex) { Core.Caught("bal-stations: recipe entry " + i, ex); }
            }
        }

        /// <summary>First recipe's cook time and its game value, for the status line.</summary>
        internal static string Describe()
        {
            var ui = Singleton<ChemistryStationInterface>.Instance;
            var list = ui?.Recipes;
            if (list == null || list.Count == 0) return "no recipes loaded";
            StationRecipe r = list[0];
            if (r == null) return "no recipes loaded";
            int live = r.CookTime_Mins;
            int van = _vanilla.TryGetValue(r.GetInstanceID(), out int v) ? v : live;
            return r.RecipeTitle + " " + live + " min (game " + van + " min), " + list.Count + " recipes, speed " + Eff.F(_lastSpeed);
        }

        /// <summary>The game's cook time of a recipe, for the dev cook check.</summary>
        internal static int VanillaOf(StationRecipe r)
        {
            if (r == null) return 0;
            return _vanilla.TryGetValue(r.GetInstanceID(), out int v) ? v : r.CookTime_Mins;
        }
    }
}
