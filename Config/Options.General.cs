namespace ActuallyWorkingEmployees.Config
{
    public static partial class Options
    {
        public static readonly BoolOption UseVanillaBalance = new BoolOption("General", "UseVanillaBalance",
            "Use vanilla balance", "Every balance setting acts as in the base game while on. Fixes stay active.",
            false, hostOnly: true, balance: false);

        /// <summary>
        /// Lifts the min and max of every number option on this machine. Not host-only: each machine unlocks the options it
        /// may edit, so a client never unlocks the host's settings. The settings app shows it as the switch in its header,
        /// not as a row of the General group.
        /// </summary>
        public static readonly BoolOption AllowOutOfRange = new BoolOption("General", "AllowOutOfRange",
            "Allow values outside the limits", "Number settings accept any value. Values outside the limits are untested.",
            false, hostOnly: false, balance: false);

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
