namespace ActuallyWorkingEmployees.Config
{
    public static partial class Options
    {
        public static readonly BoolOption FixPartialWalkLoop = new BoolOption("Reliability", "FixPartialWalkLoop",
            "Fix endless partial walks", "Counts a partial walk far from the target as a failure so the employee warps instead of looping.",
            true, hostOnly: true, balance: false);

        public static readonly IntOption PathFailuresBeforeWarp = new IntOption("Reliability", "PathFailuresBeforeWarp",
            "Path failures before warp", "Failed walks in a row before the employee is moved to the target. Vanilla: 5.",
            5, 1, 20, 1, hostOnly: true, balance: false);

        public static readonly BoolOption FixStandstill = new BoolOption("Reliability", "FixStandstill",
            "Fix standing still while walking", "Moves an employee that stands still while it should be walking.",
            true, hostOnly: true, balance: false);

        public static readonly FloatOption StandstillSeconds = new FloatOption("Reliability", "StandstillSeconds",
            "Standstill time before reset", "Seconds an employee may stand still before it is reset.",
            12f, 5f, 60f, 1f, hostOnly: true, balance: false);

        public static readonly FloatOption StandstillRadius = new FloatOption("Reliability", "StandstillRadius",
            "Standstill radius", "Distance in meters an employee must move to count as not standing still.",
            0.5f, 0.1f, 3f, 0.1f, hostOnly: true, balance: false);

        public static readonly IntOption StandstillAttempts = new IntOption("Reliability", "StandstillAttempts",
            "Standstill attempts before giving up", "Resets tried before the employee's current task is released.",
            2, 1, 10, 1, hostOnly: true, balance: false);

        public static readonly BoolOption FixChemistWorkInProgress = new BoolOption("Reliability", "FixChemistWorkInProgress",
            "Fix chemist stuck without cauldron", "Counts cauldrons as work in progress so a chemist does not wait forever.",
            true, hostOnly: true, balance: false);

        public static readonly FloatOption ChemistIdleTimeout = new FloatOption("Reliability", "ChemistIdleTimeout",
            "Chemist idle reset time", "Seconds a chemist may idle before its task is reset. A running station is never interrupted.",
            5f, 2f, 60f, 1f, hostOnly: true, balance: false);

        public static readonly BoolOption FixLockLeak = new BoolOption("Reliability", "FixLockLeak",
            "Release station locks on stop", "Frees station locks when an employee stops working or is fired.",
            true, hostOnly: true, balance: false);

        public static readonly BoolOption FixStuckInventoryItem = new BoolOption("Reliability", "FixStuckInventoryItem",
            "Return stuck carried items", "Puts an item an employee still carries back to its source slot when the task ends.",
            true, hostOnly: true, balance: false);

        public static readonly BoolOption ClearReasons = new BoolOption("Reliability", "ClearReasons",
            "Say why an employee is idle", "Idle employees say the real reason instead of \"nothing to do\".",
            true, hostOnly: true, balance: false);

        public static readonly BoolOption CacheReachability = new BoolOption("Reliability", "CacheReachability",
            "Cache station reachability", "Remembers whether a station can be reached instead of re-checking every tick. Vanilla: off.",
            true, hostOnly: true, balance: false);

        public static readonly FloatOption ReachabilityCacheSeconds = new FloatOption("Reliability", "ReachabilityCacheSeconds",
            "Reachability cache time", "Seconds a cached reachability result is kept.",
            10f, 1f, 60f, 1f, hostOnly: true, balance: false);

        public static readonly BoolOption SleepSafeRelease = new BoolOption("Reliability", "SleepSafeRelease",
            "Release employees before sleep", "Frees employees and their stations before the night is skipped.",
            true, hostOnly: true, balance: false);
    }
}
