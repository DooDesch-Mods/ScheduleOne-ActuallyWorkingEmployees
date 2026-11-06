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

        public static readonly BoolOption WorkThroughEndOfDay = new BoolOption("General", "WorkThroughEndOfDay",
            "Work through 4 AM", "Employees keep working while the clock stands at 4 AM. Plants and station timers still wait until you sleep. Vanilla: off.",
            false, hostOnly: true, balance: true);

        public static readonly BoolOption VerboseLogging = new BoolOption("General", "VerboseLogging",
            "Verbose log", "Writes detailed lines to the log: option changes and feature start-up.",
            false, hostOnly: false, balance: false);
    }
}
