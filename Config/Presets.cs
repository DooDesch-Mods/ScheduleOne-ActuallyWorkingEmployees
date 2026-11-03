using System;
using System.Collections.Generic;
using System.Linq;

namespace ActuallyWorkingEmployees.Config
{
    /// <summary>
    /// Named sets of option values that a button or <c>awe preset</c> writes once. A preset is not a mode: after it ran,
    /// the values are ordinary settings, and <c>Reset all</c> takes them back to vanilla.
    /// </summary>
    internal static class Presets
    {
        /// <summary>Preset name to option key and raw value, in the order they are written.</summary>
        internal static readonly IReadOnlyDictionary<string, KeyValuePair<string, string>[]> All =
            new Dictionary<string, KeyValuePair<string, string>[]>(StringComparer.OrdinalIgnoreCase)
            {
                // Every work action twice as fast. Walk speed, wages and assignment limits stay vanilla.
                ["fast"] = new[]
                {
                    new KeyValuePair<string, string>("botanist.workspeed", "2"),
                    new KeyValuePair<string, string>("chemist.workspeed", "2"),
                    new KeyValuePair<string, string>("packager.workspeed", "2"),
                    new KeyValuePair<string, string>("cleaner.workspeed", "2"),
                },
            };

        internal sealed class Result
        {
            internal int Changed;
            internal int Total;
            /// <summary>Why the preset did not run, or null. Set when an option is missing or the host controls it.</summary>
            internal string Refused;
            internal readonly List<string> Errors = new List<string>();
        }

        /// <summary>
        /// Writes every value of the preset. Refuses the whole preset before writing anything when one of its options is
        /// missing or not editable here (a co-op client), so a preset never lands half applied.
        /// </summary>
        internal static Result Apply(string name)
        {
            var r = new Result();
            if (string.IsNullOrWhiteSpace(name) || !All.TryGetValue(name.Trim(), out KeyValuePair<string, string>[] values))
            {
                r.Refused = "no preset '" + name + "'. Presets: " + string.Join(", ", All.Keys);
                Core.Log?.Warning("[Presets] " + r.Refused);
                return r;
            }
            r.Total = values.Length;

            var targets = new List<KeyValuePair<Option, string>>();
            foreach (KeyValuePair<string, string> kv in values)
            {
                Option o = Options.Find(kv.Key);
                if (o == null) { r.Refused = "preset " + name + " names " + kv.Key + ", which does not exist."; Core.Log?.Error("[Presets] " + r.Refused); return r; }
                if (!o.Editable) { r.Refused = "the host controls " + o.Key + "."; Core.Log?.Warning("[Presets] " + name + " refused: " + r.Refused); return r; }
                targets.Add(new KeyValuePair<Option, string>(o, kv.Value));
            }

            foreach (KeyValuePair<Option, string> t in targets)
            {
                string before = t.Key.ValueText;
                if (!t.Key.TrySet(t.Value, out string error))
                {
                    r.Errors.Add(t.Key.Key + ": " + error);
                    Core.Log?.Error("[Presets] " + name + ": " + t.Key.Key + " = '" + t.Value + "' rejected: " + error);
                    continue;
                }
                if (t.Key.ValueText != before) r.Changed++;
            }
            Core.Log?.Msg("[Presets] " + name + " applied: " + r.Changed + " of " + r.Total + " setting(s) changed"
                + (r.Errors.Count > 0 ? ", " + r.Errors.Count + " rejected" : "") + ".");
            return r;
        }
    }
}
