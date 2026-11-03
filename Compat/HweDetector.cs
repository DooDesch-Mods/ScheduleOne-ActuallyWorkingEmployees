using System;
using MelonLoader;

namespace ActuallyWorkingEmployees.Compat
{
    /// <summary>
    /// Finds Harder Working Employees among the loaded mods. This mod replaces it and does not run alongside it, so
    /// the result is one log warning (when "Warn about Harder Working Employees" is on). Every fix keeps running.
    /// </summary>
    internal static class HweDetector
    {
        internal static bool Loaded { get; private set; }
        internal static string Found { get; private set; } = "";

        internal static void Check()
        {
            try
            {
                foreach (MelonBase melon in MelonBase.RegisteredMelons)
                {
                    string name = melon?.Info?.Name ?? "";
                    string compact = name.Replace(" ", "");
                    if (compact.IndexOf("HarderWorkingEmployees", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    Loaded = true;
                    Found = name + " " + (melon.Info.Version ?? "");
                    if (Config.Options.WarnOnHwe.Value)
                        Core.Log?.Warning("[Compat] " + Found.Trim() + " is loaded. Actually Working Employees replaces Harder Working Employees. "
                            + "Remove HarderWorkingEmployees.dll from the Mods folder.");
                    else
                        Core.Log?.Msg("[Compat] " + Found.Trim() + " is loaded; the warning is switched off by its option.");
                    return;
                }
                Core.Log?.Msg("[Compat] Harder Working Employees is not loaded.");
            }
            catch (Exception e) { Core.Caught("compat: scan loaded mods for Harder Working Employees", e); }
        }
    }
}
