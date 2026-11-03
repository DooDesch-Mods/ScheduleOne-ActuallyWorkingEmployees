using System;
using HarmonyLib;
using Il2CppScheduleOne.ObjectScripts;
using ActuallyWorkingEmployees.Config;
#if DEBUG
using ActuallyWorkingEmployees.Debugging;
#endif

namespace ActuallyWorkingEmployees.Features.Reliability
{
    /// <summary>
    /// Fix 5: idle chemists and cleaners say which station or bin holds them back.
    ///
    /// Both roles submit the fixed "There's nothing for me to do right now." when no task is startable. A prefix on
    /// Employee.SubmitNoWorkReason swaps that text for the first blocking station (in the order the chemist tries
    /// them) or for the trash level of the assigned cans. When nothing specific is found the vanilla text stays.
    /// </summary>
    internal sealed class Fix5ClearReasons : IFeature
    {
        internal const string FeatureName = "fix5-clear-reasons";
        internal const string VanillaText = "There's nothing for me to do right now.";
        public string Name => FeatureName;

        private static int _rewritten, _kept;

        public void Init()
        {
#if DEBUG
            DevConsole.Register("reasons", "awe reasons [submit]  (idle reason of every chemist and cleaner / send it to the app)", Dev);
#endif
        }

        public void Reset() { }
        public void Tick(float dt) { }

        public string Status => "on=" + (Options.ClearReasons.Value ? 1 : 0) + " rewritten=" + _rewritten + " kept=" + _kept;

        /// <summary>Prefix body. Replaces the text of the generic idle reason when the role has a better one.</summary>
        internal static void Rewrite(Employee emp, ref string reason, ref string fix)
        {
            if (reason != VanillaText) return;   // a specific reason of the game itself
            if (!Rel.Gate(FeatureName, Options.ClearReasons.Value)) return;
            if (!Explain(emp, out string newReason, out string newFix)) { _kept++; return; }
            reason = newReason;
            fix = newFix;
            _rewritten++;
            Rel.Action("fix5.reason", "[Fix5] " + Rel.Name(emp) + " idle: " + newReason + (newFix.Length > 0 ? " Fix: " + newFix : ""));
        }

        /// <summary>The first thing that blocks this employee, or false when the role has nothing more specific than vanilla.</summary>
        internal static bool Explain(Employee emp, out string reason, out string fix)
        {
            reason = ""; fix = "";
            Chemist chemist = emp.TryCast<Chemist>();
            if (chemist != null) return ExplainChemist(chemist, out reason, out fix);
            Cleaner cleaner = emp.TryCast<Cleaner>();
            if (cleaner != null) return ExplainCleaner(cleaner, out reason, out fix);
            return false;
        }

        private static bool ExplainChemist(Chemist c, out string reason, out string fix)
        {
            reason = ""; fix = "";
            var cfg = c.configuration;
            if (cfg == null) { Rel.MsgOnce("fix5:nocfg:" + c.GetInstanceID(), "[" + FeatureName + "] " + Rel.Name(c) + " has no configuration."); return false; }

            // Same order as Chemist.TryStartNewTask: ovens, chemistry stations, cauldrons, mixing stations.
            foreach (var s in cfg.ChemStations)
            {
                if (s == null) continue;
                var usable = s.TryCast<Il2CppScheduleOne.Management.IUsable>();
                if (usable != null && usable.IsInUse) { reason = "A chemistry station is in use."; return true; }
                if (s.CurrentCookOperation != null) { reason = "A chemistry station is still cooking."; return true; }
                var recipe = (s.Configuration?.TryCast<Il2CppScheduleOne.Management.ChemistryStationConfiguration>())?.Recipe?.SelectedRecipe;
                if (recipe == null) { reason = "A chemistry station has no recipe."; fix = "Pick a recipe in the station's settings."; return true; }
                if (!s.HasIngredientsForRecipe(recipe)) { reason = "A chemistry station is missing ingredients."; fix = "Put the recipe's ingredients into the station."; return true; }
            }
            foreach (var s in cfg.Cauldrons)
            {
                if (s == null) continue;
                switch (s.GetState())
                {
                    case Cauldron.EState.MissingIngredients: reason = "A cauldron is missing ingredients."; fix = "Fill the cauldron's input."; return true;
                    case Cauldron.EState.Cooking: reason = "A cauldron is still cooking."; return true;
                    case Cauldron.EState.OutputFull: reason = "A cauldron's output is full."; fix = "Empty the cauldron or give it a destination."; return true;
                }
            }
            foreach (var s in cfg.MixStations)
            {
                if (s == null) continue;
                var mixCfg = s.Configuration?.TryCast<Il2CppScheduleOne.Management.MixingStationConfiguration>();
                if (!s.CanStartMix()) { reason = "A mixing station cannot start a mix."; fix = "Check the station's ingredients and product."; return true; }
                if (mixCfg != null && s.GetMixQuantity() < mixCfg.StartThrehold.Value)
                {
                    reason = "A mixing station has less than its start threshold.";
                    fix = "Add product or lower the threshold.";
                    return true;
                }
            }
            return false;
        }

        private static bool ExplainCleaner(Cleaner c, out string reason, out string fix)
        {
            reason = ""; fix = "";
            var cfg = c.configuration;
            var bins = cfg?.binItems;
            if (bins == null) { Rel.MsgOnce("fix5:nobins:" + c.GetInstanceID(), "[" + FeatureName + "] " + Rel.Name(c) + " has no bin list."); return false; }
            if (bins.Count == 0) { reason = "No trash cans are assigned to me."; fix = "Assign trash cans with the management clipboard."; return true; }

            float max = 0f;
            for (int i = 0; i < bins.Count; i++)
            {
                var bin = bins[i];
                if (bin?.Container == null) continue;
                max = Math.Max(max, bin.Container.NormalizedTrashLevel);
            }
            reason = "The assigned trash cans are at most " + (int)Math.Round(max * 100f) + "% full.";
            return true;
        }

#if DEBUG
        private static void Dev(string[] args)
        {
            if (!DevConsole.RequireHost("reasons")) return;
            bool submit = args.Length > 0 && args[0].Equals("submit", StringComparison.OrdinalIgnoreCase);
            int n = 0;
            foreach (Employee emp in Rel.Employees())
            {
                if (emp.TryCast<Chemist>() == null && emp.TryCast<Cleaner>() == null) continue;
                n++;
                bool has = Explain(emp, out string r, out string f);
                Core.Log?.Msg("[Dev] [Fix5] " + Rel.Name(emp) + ": " + (has ? r + (f.Length > 0 ? " Fix: " + f : "") : "no specific reason, vanilla text stays"));
                // Goes through the prefix like the game's own idle submit.
                if (submit) emp.SubmitNoWorkReason(VanillaText, "");
            }
            Core.Log?.Msg("[Dev] [Fix5] " + n + " employee(s). " + new Fix5ClearReasons().Status);
        }
#endif
    }

    /// <summary>Employee.SubmitNoWorkReason (Il2CppScheduleOne.Employees/Employee.cs:1663).</summary>
    [HarmonyPatch(typeof(Employee), nameof(Employee.SubmitNoWorkReason))]
    internal static class Awe_Fix5_SubmitNoWorkReason_Patch
    {
        private static void Prefix(Employee __instance, ref string reason, ref string fix)
        {
            try { Fix5ClearReasons.Rewrite(__instance, ref reason, ref fix); }
            catch (Exception e) { Core.Failed("fix5: SubmitNoWorkReason prefix", e); }
        }
    }
}
