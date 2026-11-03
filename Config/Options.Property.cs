namespace ActuallyWorkingEmployees.Config
{
    public static partial class Options
    {
        public static readonly BoolOption EnableEmployeeCapacityOverride = new BoolOption("Property", "EnableEmployeeCapacityOverride",
            "Override employees per property", "Uses the employee limit below for every property. Vanilla: off.",
            false, hostOnly: true, balance: true);

        public static readonly IntOption EmployeeCapacity = new IntOption("Property", "EmployeeCapacity",
            "Employees per property", "Employees a property can hold while the override is on. Vanilla: 10.",
            10, 10, 30, 1, hostOnly: true, balance: true);

        public static readonly BoolOption ShareIdlePoints = new BoolOption("Property", "ShareIdlePoints",
            "Share idle spots", "Extra employees share idle spots when there are fewer spots than employees.",
            true, hostOnly: true, balance: false);

        // CapacityPerProperty is deferred: it needs a string option (an int list per property code), which Option.cs does not support.

        public static readonly BoolOption EmployeeCapacityLossGuard = new BoolOption("Property", "EmployeeCapacityLossGuard",
            "Protect current employees", "Refuses a lower capacity while a property has more employees than that.",
            true, hostOnly: true, balance: false);
    }
}
