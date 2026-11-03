namespace ActuallyWorkingEmployees.Config
{
    public static partial class Options
    {
        public static readonly BoolOption ShowProblemBadges = new BoolOption("Diagnostics", "ShowProblemBadges",
            "Show problem badges", "Shows problem badges above employees while the management clipboard is open.",
            false, hostOnly: false, balance: false);

        public static readonly BoolOption EnableSetupDoctor = new BoolOption("Diagnostics", "EnableSetupDoctor",
            "Enable Setup Doctor", "Turns on the Setup Doctor, which lists problems in the employee setup.",
            false, hostOnly: true, balance: false);

        public static readonly BoolOption DoctorCheckReachability = new BoolOption("Diagnostics", "DoctorCheckReachability",
            "Doctor: path-test routes", "The Setup Doctor tests whether each route can be walked.",
            false, hostOnly: true, balance: false);

        public static readonly IntOption DoctorMaxFindings = new IntOption("Diagnostics", "DoctorMaxFindings",
            "Doctor: max findings", "Most findings the Setup Doctor lists.",
            25, 5, 200, 5, hostOnly: true, balance: false);

        public static readonly BoolOption EnableAnalytics = new BoolOption("Diagnostics", "EnableAnalytics",
            "Track idle time and reason", "Records how long each employee idles and why.",
            false, hostOnly: true, balance: false);

        public static readonly FloatOption AnalyticsIntervalSeconds = new FloatOption("Diagnostics", "AnalyticsIntervalSeconds",
            "Analytics sampling interval", "Seconds between idle samples.",
            5f, 1f, 60f, 1f, hostOnly: true, balance: false);

        public static readonly BoolOption LogFixActions = new BoolOption("Diagnostics", "LogFixActions",
            "Log every fix action", "Writes a log line for each warp, release and unstick.",
            true, hostOnly: false, balance: false);

        public static readonly BoolOption ShowCoopLockNotice = new BoolOption("Diagnostics", "ShowCoopLockNotice",
            "Show host-controlled notice", "Shows a notice on a co-op client when the host controls a setting.",
            true, hostOnly: false, balance: false);

        // ResetToVanilla is deferred: it is a button, and Options.ResetAllToDefault covers it.
    }
}
