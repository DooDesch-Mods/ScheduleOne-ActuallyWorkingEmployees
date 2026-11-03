namespace ActuallyWorkingEmployees.Config
{
    public static partial class Options
    {
        public static readonly FloatOption PackagerWalkSpeed = new FloatOption("Packager", "WalkSpeed",
            "Walk speed", "Packager walking speed. Vanilla: the game's value.",
            -1f, 0.5f, 6f, 0.1f, hostOnly: true, balance: true, vanillaSentinel: true);

        public static readonly IntOption PackagerDailyWage = new IntOption("Packager", "DailyWage",
            "Daily wage", "Packager daily wage. Vanilla: the game's value.",
            -1, 0, 10000, 10, hostOnly: true, balance: true, vanillaSentinel: true);

        public static readonly IntOption PackagerSigningFee = new IntOption("Packager", "SigningFee",
            "Signing fee", "One-time cost to hire a packager. Vanilla: the game's value.",
            -1, 0, 100000, 50, hostOnly: true, balance: true, vanillaSentinel: true);

        public static readonly IntOption PackagerMaxStations = new IntOption("Packager", "MaxStations",
            "Max stations", "Stations one packager can be assigned. Vanilla: 3.",
            3, 1, 32, 1, hostOnly: true, balance: true);

        public static readonly IntOption PackagerMaxRoutes = new IntOption("Packager", "MaxRoutes",
            "Max routes", "Routes one packager can be assigned. Vanilla: 5.",
            5, 1, 16, 1, hostOnly: true, balance: true);

        public static readonly FloatOption PackagerWorkSpeed = new FloatOption("Packager", "WorkSpeed",
            "Work speed", "Multiplier for the speed of every packager action. Vanilla: 1.",
            1f, 0.25f, 10f, 0.05f, hostOnly: true, balance: true);

        public static readonly FloatOption PackagerPackagingSpeed = new FloatOption("Packager", "PackagingSpeed",
            "Packaging speed", "Multiplier for packaging speed. Vanilla: 1.",
            1f, 0.25f, 10f, 0.05f, hostOnly: true, balance: true);

        public static readonly FloatOption PackagerPackagingStationTime = new FloatOption("Packager", "PackagingStationTime",
            "Packaging station time", "Seconds to package at a packaging station. Vanilla: 5.",
            5f, 0.5f, 60f, 0.5f, hostOnly: true, balance: true);

        public static readonly FloatOption PackagerBrickPressTime = new FloatOption("Packager", "BrickPressTime",
            "Brick press time", "Seconds to press a brick. Vanilla: 15.",
            15f, 0.5f, 120f, 0.5f, hostOnly: true, balance: true);

        public static readonly FloatOption PackagerMoveItemTime = new FloatOption("Packager", "MoveItemTime",
            "Item hand-over time", "Seconds to hand over an item. Vanilla: 0.5.",
            0.5f, 0.1f, 10f, 0.1f, hostOnly: true, balance: true);

        public static readonly BoolOption BatchHandling = new BoolOption("Packager", "BatchHandling",
            "Wait for a worthwhile load", "The packager skips a route until enough items are ready. Vanilla: off, one trip per item.",
            false, hostOnly: true, balance: true);

        public static readonly IntOption BatchThreshold = new IntOption("Packager", "BatchThreshold",
            "Items before a trip", "Items that must be ready before the packager starts a trip. 1 acts as vanilla.",
            10, 1, 100, 1, hostOnly: true, balance: true);

        public static readonly FloatOption BatchTimeoutSeconds = new FloatOption("Packager", "BatchTimeoutSeconds",
            "Batch wait timeout", "Seconds the packager waits for a full load before going anyway.",
            60f, 5f, 600f, 5f, hostOnly: true, balance: true);

        public static readonly BoolOption BatchFullStackOnly = new BoolOption("Packager", "BatchFullStackOnly",
            "Full stacks only", "Batch handling counts only full stacks.",
            false, hostOnly: true, balance: true);

        public static readonly ChoiceOption RoutePriorityMode = new ChoiceOption("Packager", "RoutePriorityMode",
            "Route priority", "Which route the packager picks first. Vanilla: the game's order.",
            "Vanilla", new[] { "Vanilla", "Fullest", "Emptiest", "NearestFirst" }, hostOnly: true, balance: true);

        public static readonly BoolOption RouteDiagnostics = new BoolOption("Packager", "RouteDiagnostics",
            "Route diagnostics", "Logs why a route was skipped.",
            false, hostOnly: true, balance: false);

        public static readonly FloatOption RouteWarnIntervalSeconds = new FloatOption("Packager", "RouteWarnIntervalSeconds",
            "Route warning interval", "Minimum seconds between repeated route warnings.",
            30f, 5f, 300f, 5f, hostOnly: true, balance: false);

        public static readonly BoolOption LogBatchActivity = new BoolOption("Packager", "LogBatchActivity",
            "Log batch activity", "Writes batch waits and departures to the log.",
            true, hostOnly: true, balance: false);

        public static readonly BoolOption ShowBatchWaitingReason = new BoolOption("Packager", "ShowBatchWaitingReason",
            "Show batch waiting reason", "Shows why a packager is waiting for a batch.",
            true, hostOnly: false, balance: false);
    }
}
