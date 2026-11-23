using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using DooDesch.Localization;
using Newtonsoft.Json.Linq;

namespace ActuallyWorkingEmployees.Host
{
    /// <summary>
    /// Localization of the phone app. The English text is the key (<see cref="L10n"/>), the German table is the embedded
    /// <c>Assets/l10n/de.json</c>. Texts that exist only as a sentence built at runtime (the game's own idle reasons, the
    /// reasons this mod writes, the Setup Doctor's findings, error answers) are translated at the edge, when the page asks:
    /// everything inside the mod, the log and the game's own employee panel keep the English text, and the severity of a
    /// reason is still read from the English wording. A sentence with a number or a name in it is matched by a pattern
    /// and handed to L10n as one template ("The assigned trash cans are at most {0}% full.").
    ///
    /// The key lists below are what the tests compare against the German table: a text added to the code needs its line
    /// here (or an <c>L10n.T("...")</c> call), and a line in <c>de.json</c>.
    /// </summary>
    internal static class Loc
    {
        private const string Resource = "ActuallyWorkingEmployees.Assets.l10n.de.json";

        /// <summary>Registers the German table. Call before anything asks for a text.</summary>
        internal static void Register()
        {
            try
            {
                using (Stream s = typeof(Loc).Assembly.GetManifestResourceStream(Resource))
                {
                    if (s == null)
                    {
                        Core.Log?.Warning("[L10n] " + Resource + " is not embedded, the app shows English only.");
                        return;
                    }
                    using (var r = new StreamReader(s))
                    {
                        var table = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (JProperty p in JObject.Parse(r.ReadToEnd()).Properties())
                            if (p.Value.Type == JTokenType.String) table[p.Name] = (string)p.Value;
                        L10n.Register("de", table);
                        Core.Log?.Msg("[L10n] " + table.Count + " German texts registered, language " + L10n.Language + ".");
                    }
                }
            }
            catch (Exception e)
            {
                Core.Log?.Error("[L10n] reading the German table failed (" + e.Message + "), the app shows English only.");
            }
        }

#if DEBUG
        /// <summary>Switches the language of the running game, so one session can show both languages. Debug builds only.</summary>
        internal static bool SetLanguage(string code)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            System.Reflection.FieldInfo lang = typeof(L10n).GetField("_lang", flags);
            System.Reflection.FieldInfo active = typeof(L10n).GetField("_active", flags);
            if (lang == null || active == null)
            {
                Core.Log?.Warning("[L10n] language switch failed: L10n has no _lang/_active field.");
                return false;
            }
            lang.SetValue(null, code);
            active.SetValue(null, null);
            Core.Log?.Msg("[L10n] language switched to '" + code + "'.");
            return true;
        }
#endif

        // l10n-keys:begin
        /// <summary>Texts of the game's own idle reasons and fixes, of the reasons this mod writes, and of the Setup Doctor.</summary>
        internal static readonly string[] ReasonKeys =
        {
            // The game's idle reasons and fixes (Employee, Botanist, Chemist, Packager, Cleaner).
            "I haven't been assigned any stations",
            "You can use your management clipboards to assign stations to me.",
            "I haven't been assigned any trash cans",
            "You can use your management clipboards to assign trash cans to me.",
            "I haven't been assigned anything",
            "You can use your management clipboards to assign me pots, growing racks, etc.",
            "I haven't been assigned to any stations or routes.",
            "You can use your management clipboards to assign stations or routes to me.",
            "There are empty pots, but I don't have any soil to pour.",
            "Make sure there's soil in my supplies stash.",
            "Use your management clipboard to assign a supplies stash to me, then make sure there's soil in it.",
            "There's nothing for me to do right now.",
            "I need one of my assigned stations to have enough product and packaging to get to work.",
            "There is a pot ready for sowing, but I don't have any seeds for it.",
            "Make sure I have the right seeds in my supplies stash.",
            "Use your management clipboards to assign a supplies stash to me, and make sure it contains the right seeds.",
            "I don't have any shroom spawn to mix into my assigned mushroom beds.",
            "Make sure I have shroom spawn my supplies stash.",
            "Use your management clipboards to assign a supplies stash to me, and make sure it contains shroom spawn.",
            "I haven't been assigned a locker",
            "You can use your management clipboard to assign me a locker.",
            "Sorry boss, my shift ends at 4AM.",
            "I haven't been paid yet",
            "You can place cash in my locker.",
            "There is a plant ready for harvest, but it has no destination or it's destination is full.",
            "Use your management clipboard to assign a destination for each of my pots, and make sure the destination isn't full.",
            "There is a mushroom colony ready for harvest, but it has no destination or it's destination is full.",
            "Use your management clipboard to assign a destination for each of my mushroom beds, and make sure the destination isn't full.",

            // Reasons this mod writes instead of "There's nothing for me to do right now." (Fix 5, packager routes).
            "A lab oven is in use.",
            "A lab oven is still cooking.",
            "A lab oven's output is full.",
            "Empty the oven's output.",
            "A lab oven is missing ingredients.",
            "Fill the oven's input.",
            "A chemistry station is in use.",
            "A chemistry station is still cooking.",
            "A chemistry station has no recipe.",
            "Pick a recipe in the station's settings.",
            "A chemistry station is missing ingredients.",
            "Put the recipe's ingredients into the station.",
            "A cauldron is missing ingredients.",
            "Fill the cauldron's input.",
            "A cauldron is still cooking.",
            "A cauldron's output is full.",
            "Empty the cauldron or give it a destination.",
            "A mixing station cannot start a mix.",
            "Check the station's ingredients and product.",
            "A mixing station has less than its start threshold.",
            "Add product or lower the threshold.",
            "No trash cans are assigned to me.",
            "Assign trash cans with the management clipboard.",
            "The assigned trash cans are at most {0}% full.",
            "I can't find a way to walk to {0}.",
            "The route's destination may be on another property or blocked.",
            "The route's source may be on another property or blocked.",
            "My hands are full.",
            "Check that the destination has space.",
            "I'm waiting for a full load ({0}/{1}).",
            "I'm waiting for a full stack (got {0}).",
            "Lower the batch threshold in the Actually Working Employees settings for smaller, more frequent trips.",

            // Employee badges and idle analytics.
            "Working.",
            "No bed or locker assigned.",
            "No reason given.",
            "Other reasons.",
            "Botanist",
            "Chemist",
            "Packager",
            "Cleaner",
            "Employee",

            // Setup Doctor: categories, findings and fixes.
            "Capacity",
            "Station",
            "Route",
            "{0} employees on a property rated for {1}.",
            "Extra employees share idle spots.",
            "Extra employees get no idle spot (ShareIdlePoints is off).",
            "Assign a locker with the management clipboard.",
            "No pots, racks or beds assigned.",
            "Assign stations with the management clipboard.",
            "No stations assigned.",
            "No stations or routes assigned.",
            "Assign stations or add routes with the management clipboard.",
            "No trash cans assigned.",
            "A pot with a plant is not assigned to a botanist.",
            "Assign the pot to a botanist.",
            "A pot is not assigned to any employee.",
            "A packaging station is not assigned to any employee.",
            "A lab oven is not assigned to any employee.",
            "A chemistry station is not assigned to any employee.",
            "A cauldron is not assigned to any employee.",
            "A brick press is not assigned to any employee.",
            "A mixing station is not assigned to any employee.",
            "A drying rack is not assigned to any employee.",
            "A spawn station is not assigned to any employee.",
            "A mushroom bed is not assigned to any employee.",
            "A pot with a plant has no harvest destination.",
            "Set a destination for the pot.",
            "A route has no source or no destination.",
            "Something on the route was sold or removed. Create the route again.",
            "The destination is not accepting items.",
            "It may be switched off or busy. Check it with the management clipboard.",
            "No walkable path to the destination.",
            "No walkable path to the source.",
            "Furniture or a missing connection blocks it. Handlers skip routes they cannot walk.",
            "{0} finding(s), {1} shown, {2} propert(ies), {3} employee(s), {4} route(s), {5} path-tested, {6} ms",

            // Answers of the page's calls and the two hints.
            "Something went wrong.",
            "Unknown setting.",
            "Set by the host.",
            "Host only.",
            "The host controls this setting.",
            "The Setup Doctor is off.",
            "No save is loaded.",
            "The game has no property list yet.",
            "Some settings are set by the host.",
            "Saved mixes and stations that already run keep their old time.",
            "{0}: expected on or off, got '{1}'.",
            "{0}: expected a whole number, got '{1}'.",
            "{0}: expected a number like 1.5, got '{1}'.",
            "{0}: {1} is outside {2}-{3}.",
            "{0}: {1} is outside {2}-{3} (or -1 for vanilla).",
            "{0}: {1} is not a usable number.",
            "{0}: expected one of {1}, got '{2}'.",

            // The whip item and the notice after a whip (AWE#63).
            "Whip",
            "Crack it at an employee. A working employee works faster for a while, an idle or stuck one restarts its task.",
            "{0} works {1}% faster for {2} min.",
            "{0} is at the highest boost, {1}% faster for {2} min.",
            "{0} was whipped too often: boost gone, back to the waiting spot.",
            "{0} restarts the stuck task.",
            "{0} has nothing to do right now.",
            "{0} has nothing to do: {1}",
            "The whip did nothing to {0}.",

            // Words the app shows for a value or a choice.
            "On",
            "Off",
            "Vanilla",
            "Fullest",
            "Emptiest",
            "NearestFirst",

            // Groups of the page.
            "General",
            "Reliability",
            "Stations",
            "Property",
            "Diagnostics",
        };

        /// <summary>Every text app.js shows, in the form it passes to t().</summary>
        internal static readonly string[] PageKeys =
        {
            "Employees",
            "Something went wrong.",
            "Set by the host.",
            "Host",
            "Client",
            "Allow values outside the limits",
            "Values outside the limits are untested. They can stall employees, break stations or slow the game.",
            "Values outside the limits are allowed for your own settings.",
            "Values outside the limits are allowed.",
            "Vanilla balance is on.",
            "Allow",
            "Cancel",
            "Fast",
            "Apply fast preset?",
            "Reset all",
            "Reset all?",
            "Reset group",
            "Refresh",
            "Run check",
            "Clear",
            "No settings.",
            "{0} changed",
            "Default: {0}",
            "Range: {0}-{1}.",
            "Limits: {0}-{1}.",
            "Outside the limits {0}-{1}.",
            "Set to {0}, the minimum.",
            "Set to {0}, the maximum.",
            "Waiting for the host.",
            "Problem",
            "Warning",
            "OK",
            "Info",
            "Employee badges",
            "Setup Doctor",
            "Idle time",
            "Host only.",
            "Off. Turn on {0}.",
            "No employees.",
            "{0} of {1} with a problem",
            "Not run yet.",
            "{0}, {1} s ago",
            "No problems found.",
            "{0} more not shown.",
            "Every {0} s, {1} samples",
            "Idle {0}% ({1} of {2})",
        };
        // l10n-keys:end

        // A sentence with a number or a name in it: the pattern's groups become {0}, {1}, ... of the key.
        private static readonly KeyValuePair<Regex, string>[] Patterns =
        {
            P(@"^The assigned trash cans are at most (\d+)% full\.$", "The assigned trash cans are at most {0}% full."),
            P(@"^I can't find a way to walk to (.+)\.$", "I can't find a way to walk to {0}."),
            P(@"^I'm waiting for a full load \((\d+)/(\d+)\)\.$", "I'm waiting for a full load ({0}/{1})."),
            P(@"^I'm waiting for a full stack \(got (\d+)\)\.$", "I'm waiting for a full stack (got {0})."),
            P(@"^(\d+) employees on a property rated for (\d+)\.$", "{0} employees on a property rated for {1}."),
            P(@"^([\w.]+): expected on or off, got '(.*)'\.$", "{0}: expected on or off, got '{1}'."),
            P(@"^([\w.]+): expected a whole number, got '(.*)'\.$", "{0}: expected a whole number, got '{1}'."),
            P(@"^([\w.]+): expected a number like 1\.5, got '(.*)'\.$", "{0}: expected a number like 1.5, got '{1}'."),
            P(@"^([\w.]+): (\S+) is outside (\S+)-(\S+) \(or -1 for vanilla\)\.$", "{0}: {1} is outside {2}-{3} (or -1 for vanilla)."),
            P(@"^([\w.]+): (\S+) is outside (\S+)-(\S+)\.$", "{0}: {1} is outside {2}-{3}."),
            P(@"^([\w.]+): (\S+) is not a usable number\.$", "{0}: {1} is not a usable number."),
            P(@"^([\w.]+): expected one of (.+), got '(.*)'\.$", "{0}: expected one of {1}, got '{2}'."),
        };

        private static KeyValuePair<Regex, string> P(string pattern, string key)
            => new KeyValuePair<Regex, string>(new Regex(pattern, RegexOptions.CultureInvariant), key);

        /// <summary>
        /// The text in the current language: a sentence with a number or a name through its pattern, any other text through
        /// the table, and the text unchanged when the table has none.
        /// </summary>
        internal static string Text(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            if (L10n.Language == "en") return s;
            if (s.Length > 20)   // the shortest sentence with a pattern is longer; options and numbers skip the matching
            {
                string viaPattern = ViaPattern(s);
                if (viaPattern != null) return viaPattern;
            }
            return L10n.T(s);
        }

        private static string ViaPattern(string s)
        {
            foreach (KeyValuePair<Regex, string> p in Patterns)
            {
                Match m = p.Key.Match(s);
                if (!m.Success) continue;
                var args = new object[m.Groups.Count - 1];
                for (int i = 1; i < m.Groups.Count; i++) args[i - 1] = m.Groups[i].Value;
                return Format(p.Value, args);
            }
            return null;
        }

        /// <summary>Translates <paramref name="key"/> and fills its placeholders; the English text when the German one is broken.</summary>
        internal static string Format(string key, params object[] args)
        {
            try { return L10n.T(key, args); }
            catch (FormatException e)
            {
                Core.Log?.Warning("[L10n] the text for '" + key + "' has broken placeholders (" + e.Message + "), English is shown.");
                return string.Format(key, args);
            }
        }

        /// <summary>Every text app.js asks for, English to the current language. The page reads it once when it opens.</summary>
        internal static JObject PageStrings()
        {
            var o = new JObject();
            foreach (string k in PageKeys) o[k] = L10n.T(k);
            return o;
        }
    }
}
