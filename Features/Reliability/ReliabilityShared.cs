using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using ActuallyWorkingEmployees.Compat;
using ActuallyWorkingEmployees.Config;
using ActuallyWorkingEmployees.Networking;

namespace ActuallyWorkingEmployees.Features.Reliability
{
    /// <summary>
    /// Helpers shared by the reliability fixes: the enable/host/HWE gate, throttled action logging, employee
    /// enumeration and the list of behaviours that count as work.
    /// </summary>
    internal static class Rel
    {
        /// <summary>Walking farther than this from the target counts as "not there yet" for the walk and standstill fixes.</summary>
        internal const float FarFromTarget = 1.5f;

        // Class names of the behaviours that do employee work. Everything else (dialogue, flinch, flee, idle, ...) is
        // not a task and is never reset by the standstill fix.
        private static readonly HashSet<string> WorkBehaviours = new HashSet<string>
        {
            "MoveItemBehaviour", "PackagingStationBehaviour", "BrickPressBehaviour",
            "StartCauldronBehaviour", "StartChemistryStationBehaviour", "StartLabOvenBehaviour",
            "FinishLabOvenBehaviour", "StartMixingStationBehaviour", "StartDryingRackBehaviour",
            "StopDryingRackBehaviour", "UseSpawnStationBehaviour", "PickUpTrashBehaviour", "BagTrashCanBehaviour",
            "DisposeTrashBagBehaviour", "EmptyTrashGrabberBehaviour", "AddSoilToGrowContainerBehaviour",
            "ApplyAdditiveToGrowContainerBehaviour", "ApplySpawnToMushroomBedBehaviour", "HarvestMushroomBedBehaviour",
            "HarvestPotBehaviour", "MistMushroomBedBehaviour", "SowSeedInPotBehaviour", "WaterPotBehaviour",
        };

        private static readonly HashSet<string> _said = new HashSet<string>();
        private static readonly Dictionary<string, int> _counts = new Dictionary<string, int>();

        /// <summary>Logs <paramref name="msg"/> at info level once per <paramref name="key"/>.</summary>
        internal static void MsgOnce(string key, string msg)
        {
            if (!_said.Add(key)) return;
            Core.Log?.Msg(msg);
        }

        /// <summary>
        /// True when fix <paramref name="feature"/> may act: its option is on and this is the host. Harder Working
        /// Employees does not switch a fix off; <see cref="HweDetector"/> only warns. Every refusal is logged once with
        /// its reason.
        /// </summary>
        internal static bool Gate(string feature, bool optionOn)
        {
            if (optionOn && Net.IsServer) return true;
            // Refusals are the steady state of a disabled fix and are asked on hot paths: the log keys are built once per feature.
            if (!_gateKeys.TryGetValue(feature, out GateKeys keys))
            {
                keys = new GateKeys
                {
                    OffKey = feature + ":off", OffMsg = "[" + feature + "] switched off by its option.",
                    ClientKey = feature + ":client", ClientMsg = "[" + feature + "] not the host, vanilla behaviour on this client.",
                };
                _gateKeys[feature] = keys;
            }
            if (!optionOn) { MsgOnce(keys.OffKey, keys.OffMsg); return false; }
            MsgOnce(keys.ClientKey, keys.ClientMsg);
            return false;
        }

        private sealed class GateKeys { internal string OffKey, OffMsg, ClientKey, ClientMsg; }
        private static readonly Dictionary<string, GateKeys> _gateKeys = new Dictionary<string, GateKeys>();

        /// <summary>
        /// Writes one fix action line on the first and every 20th occurrence of <paramref name="key"/>, when
        /// "Log every fix action" is on. The count is returned either way.
        /// </summary>
        internal static int Action(string key, string msg)
        {
            _counts.TryGetValue(key, out int n);
            n++;
            _counts[key] = n;
            if (Options.LogFixActions.Value && (n == 1 || n % 20 == 0))
                Core.Log?.Msg(n == 1 ? msg : msg + " (occurrence " + n + ")");
            return n;
        }

        internal static void ResetActionCounts() { _counts.Clear(); }

        /// <summary>Forgets the log-once keys and the action counts, so a new save logs its first occurrences again.</summary>
        internal static void ResetOnceKeys() { _said.Clear(); _counts.Clear(); }

        [ThreadStatic] private static HashSet<string> _inside;

        /// <summary>
        /// Marks hook <paramref name="key"/> as running on this thread. False (and one warning per key) when it already
        /// runs, i.e. the hook was re-entered: the caller then skips its own logic and lets the game's code continue.
        /// Every true result must be paired with <see cref="Leave"/> in a finally block.
        /// </summary>
        internal static bool Enter(string key)
        {
            _inside ??= new HashSet<string>();
            if (_inside.Add(key)) return true;
            Core.WarnOnce("reentry:" + key, "hook '" + key + "' was entered again while it was still running; the nested call is passed through unchanged.");
            return false;
        }

        internal static void Leave(string key) { _inside?.Remove(key); }

        /// <summary>The employees the manager knows. Null when the manager does not exist yet.</summary>
        internal static List<Employee> Employees()
        {
            var list = new List<Employee>();
            EmployeesInto(list);
            return list;
        }

        /// <summary>
        /// Clears <paramref name="into"/> and fills it with the employees the manager knows. Per-tick callers keep one list
        /// and pass it here, so the scan allocates nothing.
        /// </summary>
        internal static void EmployeesInto(List<Employee> into)
        {
            into.Clear();
            EmployeeManager mgr = NetworkSingleton<EmployeeManager>.Instance;
            if (mgr == null) { MsgOnce("rel:nomgr", "[Reliability] EmployeeManager does not exist yet, no employees to look at."); return; }
            var all = mgr.AllEmployees;
            if (all == null) { MsgOnce("rel:nolist", "[Reliability] EmployeeManager.AllEmployees is null."); return; }
            for (int i = 0; i < all.Count; i++)
            {
                Employee e = all[i];
                if (e != null) into.Add(e);
            }
        }

        private static readonly Dictionary<IntPtr, string> _typeNames = new Dictionary<IntPtr, string>();

        /// <summary>IL2CPP class name of the object's real type, e.g. "MoveItemBehaviour". Cached per class.</summary>
        internal static string TypeName(Il2CppObjectBase obj)
        {
            if (obj == null) return "null";
            try
            {
                IntPtr klass = IL2CPP.il2cpp_object_get_class(obj.Pointer);
                if (_typeNames.TryGetValue(klass, out string cached)) return cached;
                string name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass)) ?? "?";
                _typeNames[klass] = name;
                return name;
            }
            catch (Exception e)
            {
                Core.Caught("reliability: read the class name of a behaviour", e);
                return "?";
            }
        }

        /// <summary>True for a behaviour that is an employee task.</summary>
        internal static bool IsWork(Il2CppScheduleOne.NPCs.Behaviour.Behaviour b)
        {
            return b != null && WorkBehaviours.Contains(TypeName(b));
        }

        /// <summary>
        /// Nearest NavMesh point to <paramref name="p"/> within 5 m. False when there is none: a warp to a point off the
        /// NavMesh would strand the employee, so the caller refuses it.
        /// </summary>
        internal static bool TryOnNavMesh(Vector3 p, out Vector3 snapped)
        {
            if (UnityEngine.AI.NavMesh.SamplePosition(p, out UnityEngine.AI.NavMeshHit hit, 5f, UnityEngine.AI.NavMesh.AllAreas))
            {
                snapped = hit.position;
                return true;
            }
            snapped = p;
            return false;
        }

        /// <summary>
        /// True when the interop wrapper is null, points to nothing, or wraps a Unity object the engine already destroyed.
        /// A plain null check does not see the last case: the managed wrapper outlives the native object.
        /// </summary>
        internal static bool IsGone(Il2CppObjectBase o)
        {
            if (o == null || o.Pointer == IntPtr.Zero) return true;
            UnityEngine.Object u = o.TryCast<UnityEngine.Object>();
            if (ReferenceEquals(u, null)) return false;   // not a Unity object: nothing to compare against
            return u == null;                              // Unity's overloaded comparison is true for a destroyed object
        }

        internal static string Name(NPC npc) { return npc == null ? "?" : npc.FullName; }
    }
}
