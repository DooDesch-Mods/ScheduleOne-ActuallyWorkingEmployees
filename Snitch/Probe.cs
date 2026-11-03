#if SNITCH
using System;
using System.Linq;
using System.Text;
using Snitch.Api;                 // Profiler, Panel, StateSnapshot
using ActuallyWorkingEmployees.Config;
using ActuallyWorkingEmployees.Features;
using ActuallyWorkingEmployees.Networking;

namespace ActuallyWorkingEmployees.Profiling
{
    /// <summary>
    /// DEBUG-only Snitch instrumentation: option state and one status line per feature in a Snitch panel
    /// ("Actually Working Employees"). Found by Profiler.AutoRegister (called from Core) by its name and Register()
    /// method. Everything is a no-op when the Snitch host is absent. Compiled only when SNITCH is defined (Debug +
    /// EnableSnitch); excluded from Release. See Workspace/build/Snitch.props.
    ///
    /// The panel id "AWE" matches the "AWE." sample prefix of FeatureHost.TickAll, so the tick timings group under it.
    /// </summary>
    internal static class SnitchProbe
    {
        // Reused per poll so the main-thread Text provider does not allocate a fresh builder each frame.
        private static readonly StringBuilder _sb = new StringBuilder(256);

        public static void Register()
        {
            Panel p = Profiler.RegisterPanel("AWE", "Actually Working Employees");

            p.Counter("Features", () => FeatureHost.All.Count, "active");
            p.Counter("ChangedOptions", () => Options.All.Count(o => !o.IsDefault), "options");

            p.State(() => new StateSnapshot { Title = "Options" }
                .Add("options", Options.All.Count)
                .Add("changed", Options.All.Count(o => !o.IsDefault))
                .Add("features", FeatureHost.All.Count));

            p.Text(BuildStateText);
            p.Action("Reset all options", () => Options.ResetAllToDefault());
            p.Action("Preset: Fast", () => Presets.Apply("fast"));
            p.Log();

            Core.Log?.Msg("[Snitch] Actually Working Employees panel registered.");
        }

        /// <summary>One line per feature with its live status.</summary>
        private static string BuildStateText()
        {
            _sb.Clear();
            _sb.Append("role: ").Append(Net.RoleText()).Append("  in world: ").Append(Core.InWorld ? "yes" : "no")
                .Append("  vanilla balance: ").Append(Options.VanillaForced ? "on" : "off");
            foreach (IFeature f in FeatureHost.All)
            {
                string s;
                try { s = f.Status; }
                catch (Exception e) { Core.Caught("snitch: status of " + f.Name, e); s = "status failed"; }
                _sb.Append('\n').Append(f.Name).Append(": ").Append(s);
            }
            return _sb.ToString();
        }
    }
}
#endif
