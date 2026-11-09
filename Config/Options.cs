using System;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;

namespace ActuallyWorkingEmployees.Config
{
    /// <summary>
    /// The option registry. Every option is a static field of this partial class, one file per group
    /// (<c>Options.&lt;Group&gt;.cs</c>). Field initializers of all partial files run when the type is first touched,
    /// and each option constructor adds itself to <see cref="Pending"/>; <see cref="Initialize"/> then sorts them into the
    /// fixed group order and binds their MelonPreferences entries.
    ///
    /// Options must not live in nested classes: a nested class initializes lazily and its options would miss Initialize.
    /// </summary>
    public static partial class Options
    {
        /// <summary>Group order of <see cref="All"/>, the app and the dev console.</summary>
        public static IReadOnlyList<string> Groups { get; } = new[]
        {
            "General", "Reliability", "Botanist", "Chemist", "Packager", "Cleaner", "Stations", "Property", "Diagnostics",
        };

        private static List<Option> _all = new List<Option>();
        private static readonly Dictionary<string, Option> _byKey = new Dictionary<string, Option>(StringComparer.OrdinalIgnoreCase);
        private static BoolOption _vanillaBalance;
        private static bool _initialized;

        // Explicit static constructor: without it the type is beforefieldinit and the runtime may skip field initializers
        // that are never read directly, so options would never register.
        static Options() { }

        /// <summary>All options in group order, then declaration order. Empty until Initialize ran.</summary>
        public static IReadOnlyList<Option> All => _all;

        /// <summary>The option for <c>group.id</c>, ignoring case, or null.</summary>
        public static Option Find(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            return _byKey.TryGetValue(key.Trim(), out Option o) ? o : null;
        }

        /// <summary>True while <c>general.usevanillabalance</c> is on: balance options report their defaults.</summary>
        public static bool VanillaForced => _vanillaBalance != null && _vanillaBalance.Stored;

        /// <summary>True while <c>general.allowoutofrange</c> is on: number options accept values outside min and max.</summary>
        public static bool Unlimited => _initialized && AllowOutOfRange.Stored;

        // Turning the switch off clamps every stored value outside the limits again, turning it on releases them; either
        // way the effective value of those options changes, so their listeners (balance sync, the app) hear about it.
        private static void OnUnlimitedChanged(Option _)
        {
            var outside = _all.Where(o => o.IsOutside(o.RawText)).ToList();
            Core.Log?.Msg("[Options] values outside the limits " + (Unlimited ? "allowed" : "not allowed") + "; "
                + outside.Count + " option(s) outside" + (outside.Count > 0 ? ": " + string.Join(", ", outside.Select(o => o.Key + "=" + o.RawText)) : "") + ".");
            foreach (Option o in outside) o.NotifyChanged();
        }

        /// <summary>Options in one group, in declaration order.</summary>
        public static IEnumerable<Option> InGroup(string group)
            => _all.Where(o => string.Equals(o.Group, group, StringComparison.OrdinalIgnoreCase));

        /// <summary>Resets every option this machine may change. Returns how many changed.</summary>
        public static int ResetAllToDefault()
        {
            int changed = 0;
            foreach (Option o in _all)
            {
                if (!o.Editable || o.IsDefault) continue;
                o.ResetToDefault();
                changed++;
            }
            Core.Log?.Msg("[Options] reset " + changed + " option(s) to default.");
            return changed;
        }

        /// <summary>Holder for the constructor list. A nested class, so it exists before any option field initializer runs.</summary>
        private static class Registry
        {
            internal static readonly List<Option> List = new List<Option>();
        }

        internal static List<Option> Pending => Registry.List;

        internal static void Initialize()
        {
            if (_initialized) { Core.Log?.Warning("[Options] Initialize called twice, ignored."); return; }
            _initialized = true;

            var categories = new Dictionary<string, MelonPreferences_Category>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<Option>();
            for (int g = 0; g < Groups.Count; g++)
                ordered.AddRange(Pending.Where(o => string.Equals(o.Group, Groups[g], StringComparison.Ordinal)));

            foreach (Option o in Pending.Except(ordered))
                Core.Log?.Error("[Options] " + o.Key + " has unknown group '" + o.Group + "', not registered.");

            var kept = new List<Option>();
            foreach (Option o in ordered)
            {
                if (_byKey.ContainsKey(o.Key)) { Core.Log?.Error("[Options] duplicate key " + o.Key + ", second one dropped."); continue; }
                try
                {
                    if (!categories.TryGetValue(o.Group, out MelonPreferences_Category cat))
                    {
                        cat = MelonPreferences.CreateCategory("AWE_" + o.Group, "Actually Working Employees - " + o.Group);
                        categories[o.Group] = cat;
                    }
                    o.Bind(cat);
                }
                catch (Exception e) { Core.Failed("options: bind " + o.Key, e); }
                _byKey[o.Key] = o;
                kept.Add(o);
            }
            _all = kept;
            try { OptionsFile.Attach(categories.Values); }
            catch (Exception e) { Core.Failed("options: move categories to " + OptionsFile.FileName, e); }
            _vanillaBalance = Find("general.usevanillabalance") as BoolOption;
            if (_vanillaBalance == null) Core.Log?.Warning("[Options] general.usevanillabalance is missing; balance options use their stored values.");
            AllowOutOfRange.Changed += OnUnlimitedChanged;
            if (Unlimited) Core.Log?.Msg("[Options] values outside the limits are allowed.");

            Core.Log?.Msg("[Options] " + _all.Count + " options in " + categories.Count + " groups, "
                + _all.Count(o => !o.IsDefault) + " changed from default.");
        }
    }
}
