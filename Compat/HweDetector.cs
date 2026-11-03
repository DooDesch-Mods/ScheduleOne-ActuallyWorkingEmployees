using System;
using MelonLoader;

namespace ActuallyWorkingEmployees.Compat
{
    /// <summary>
    /// Finds Harder Working Employees among the loaded mods. This mod replaces it and does not run alongside it, so
    /// the result is exactly one log warning. Every fix keeps running; patches whose native address another mod has
    /// already patched are skipped by <see cref="NativeMethods"/>.
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
                    Core.Log?.Warning("[Compat] " + Found.Trim() + " is loaded. Actually Working Employees replaces Harder Working Employees. "
                        + "Remove HarderWorkingEmployees.dll from the Mods folder.");
                    return;
                }
                Core.Log?.Msg("[Compat] Harder Working Employees is not loaded.");
            }
            catch (Exception e) { Core.Caught("compat: scan loaded mods for Harder Working Employees", e); }
        }
    }
}
