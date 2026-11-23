namespace ActuallyWorkingEmployees.Config
{
    public static partial class Options
    {
        public static readonly BoolOption UseVanillaBalance = new BoolOption("General", "UseVanillaBalance",
            "Use vanilla balance", "Pauses your changes to speed, wage, maximums and timing without deleting them. Fixes stay active.",
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

        public static readonly BoolOption PlayersPassThroughEmployees = new BoolOption("General", "PlayersPassThroughEmployees",
            "Players pass through employees", "Players walk through employees. Set by the host, applies to every player. Vanilla: off.",
            false, hostOnly: true, balance: false);

        public static readonly BoolOption DisableFurnitureCollision = new BoolOption("General", "DisableFurnitureCollision",
            "Employees ignore furniture colliders", "Employees walk through furniture colliders on their path. Vanilla: off.",
            false, hostOnly: true, balance: false);

        public static readonly BoolOption WorkThroughEndOfDay = new BoolOption("General", "WorkThroughEndOfDay",
            "Work through 4 AM", "Employees keep working while the clock stands at 4 AM. Plants and station timers still wait until you sleep. Vanilla: off.",
            false, hostOnly: true, balance: true);

        // The whip (AWE#63). Shop and price apply on every machine to its own shop; the effect is decided by the host.
        public static readonly BoolOption WhipInShop = new BoolOption("General", "WhipInShop",
            "Sell the whip", "Lists the whip at the Gas-Mart. Takes effect on the next save load.",
            true, hostOnly: false, balance: false);

        public static readonly IntOption WhipPrice = new IntOption("General", "WhipPrice",
            "Whip price", "Price of the whip at the Gas-Mart. Takes effect on the next save load.",
            150, 0, 5000, 10, hostOnly: false, balance: false);

        public static readonly IntOption WhipBoostPercent = new IntOption("General", "WhipBoostPercent",
            "Whip boost per stack", "Percent of extra work and walk speed per whip stack on a working employee. Walk speed stays at 6 m/s or below.",
            50, 5, 300, 5, hostOnly: true, balance: false);

        public static readonly IntOption WhipBoostMinutes = new IntOption("General", "WhipBoostMinutes",
            "Whip boost duration", "In-game minutes the whip boost lasts. Every hit restarts the timer for all stacks.",
            60, 10, 720, 10, hostOnly: true, balance: false);

        public static readonly IntOption WhipMaxStacks = new IntOption("General", "WhipMaxStacks",
            "Whip max stacks", "Highest number of whip stacks on one employee. Each hit on a working employee adds one.",
            3, 1, 10, 1, hostOnly: true, balance: false);

        public static readonly IntOption WhipResetHits = new IntOption("General", "WhipResetHits",
            "Whip reset hits", "This many hits on the same employee within the reset window drop all stacks and send it back to its waiting spot.",
            5, 2, 20, 1, hostOnly: true, balance: false);

        public static readonly FloatOption WhipResetSeconds = new FloatOption("General", "WhipResetSeconds",
            "Whip reset window", "Real seconds in which the reset hits must land.",
            3f, 1f, 30f, 0.5f, hostOnly: true, balance: false);

        public static readonly BoolOption VerboseLogging = new BoolOption("General", "VerboseLogging",
            "Verbose log", "Writes detailed lines to the log: option changes and feature start-up.",
            false, hostOnly: false, balance: false);
    }
}
