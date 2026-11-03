namespace ActuallyWorkingEmployees.Config
{
    public static partial class Options
    {
        public static readonly FloatOption CleanerWalkSpeed = new FloatOption("Cleaner", "WalkSpeed",
            "Walk speed", "Cleaner walking speed. Vanilla: the game's value.",
            -1f, 0.5f, 6f, 0.1f, hostOnly: true, balance: true, vanillaSentinel: true);

        public static readonly IntOption CleanerDailyWage = new IntOption("Cleaner", "DailyWage",
            "Daily wage", "Cleaner daily wage. Vanilla: the game's value.",
            -1, 0, 10000, 10, hostOnly: true, balance: true, vanillaSentinel: true);

        public static readonly IntOption CleanerSigningFee = new IntOption("Cleaner", "SigningFee",
            "Signing fee", "One-time cost to hire a cleaner. Vanilla: the game's value.",
            -1, 0, 100000, 50, hostOnly: true, balance: true, vanillaSentinel: true);

        public static readonly IntOption CleanerMaxBins = new IntOption("Cleaner", "MaxBins",
            "Max bins", "Bins one cleaner can be assigned. Vanilla: 6.",
            6, 1, 32, 1, hostOnly: true, balance: true);

        public static readonly FloatOption CleanerWorkSpeed = new FloatOption("Cleaner", "WorkSpeed",
            "Work speed", "Multiplier for the speed of every cleaner action. Vanilla: 1.",
            1f, 0.25f, 10f, 0.05f, hostOnly: true, balance: true);

        public static readonly FloatOption CleanerTrashPickupTime = new FloatOption("Cleaner", "TrashPickupTime",
            "Trash pickup time", "Seconds to pick up one piece of trash. Vanilla: 0.9.",
            0.9f, 0.1f, 10f, 0.1f, hostOnly: true, balance: true);

        public static readonly FloatOption CleanerBagTime = new FloatOption("Cleaner", "BagTime",
            "Bag trash can time", "Seconds to bag the trash of a bin. Vanilla: 3.",
            3f, 0.5f, 30f, 0.5f, hostOnly: true, balance: true);

        public static readonly FloatOption CleanerDisposeTime = new FloatOption("Cleaner", "DisposeTime",
            "Dispose trash bag time", "Seconds to dispose of a trash bag. Vanilla: 1.2.",
            1.2f, 0.1f, 30f, 0.1f, hostOnly: true, balance: true);

        public static readonly FloatOption CleanerEmptyGrabberTime = new FloatOption("Cleaner", "EmptyGrabberTime",
            "Empty grabber time", "Multiplier for the time to empty the trash grabber; 2 takes twice as long. Vanilla: 1.",
            1f, 0.25f, 10f, 0.05f, hostOnly: true, balance: true);
    }
}
