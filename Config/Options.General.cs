namespace ActuallyWorkingEmployees.Config
{
    public static partial class Options
    {
        public static readonly BoolOption UseVanillaBalance = new BoolOption("General", "UseVanillaBalance",
            "Use vanilla balance", "Every balance setting acts as in the base game while on. Fixes stay active.",
            false, hostOnly: true, balance: false);

        public static readonly BoolOption DisableEmployeeCollision = new BoolOption("General", "DisableEmployeeCollision",
            "Employees pass through each other", "Employees no longer block each other while walking. Vanilla: off.",
            false, hostOnly: true, balance: false);

        public static readonly BoolOption DisableFurnitureCollision = new BoolOption("General", "DisableFurnitureCollision",
            "Employees ignore furniture colliders", "Employees walk through furniture colliders on their path. Vanilla: off.",
            false, hostOnly: true, balance: false);

        public static readonly BoolOption WarnOnHwe = new BoolOption("General", "WarnOnHwe",
            "Warn about Harder Working Employees", "Logs a warning when Harder Working Employees is loaded next to this mod.",
            true, hostOnly: false, balance: false);

        public static readonly BoolOption VerboseLogging = new BoolOption("General", "VerboseLogging",
            "Verbose log", "Writes detailed debug lines to the log.",
            false, hostOnly: false, balance: false);
    }
}
