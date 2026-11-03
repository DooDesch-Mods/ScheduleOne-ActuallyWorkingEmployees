using System;
using MelonLoader;

namespace ActuallyWorkingEmployees.Compat
{
    /// <summary>
    /// Finds Harder Working Employees among the loaded mods. Both mods patch the same employee code, so the result is
    /// a log warning and a line in the app. Nothing is switched off.
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
                    Core.Log?.Warning("[Compat] " + Found.Trim() + " is loaded. Both mods change employees; expect conflicts. "
                        + "Remove one of them if employees behave oddly.");
                    return;
                }
                Core.Log?.Msg("[Compat] Harder Working Employees is not loaded.");
            }
            catch (Exception e) { Core.Caught("compat: scan loaded mods for Harder Working Employees", e); }
        }
    }
}
