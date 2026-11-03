#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using ActuallyWorkingEmployees.Config;
using ActuallyWorkingEmployees.Features;
using ActuallyWorkingEmployees.Networking;

namespace ActuallyWorkingEmployees.Debugging
{
    /// <summary>
    /// DEBUG-only dev console commands <c>awehelp</c> and <c>awe ...</c>. The game's console does not log its own output,
    /// so every answer goes to the MelonLoader log. Both Console.SubmitCommand overloads are patched (the string overload
    /// calls the List overload, and depending on the build either prefix is the one that fires), so dispatch dedupes per
    /// frame and command line. Features add subcommands with <see cref="Register"/> from their Init. Compiled out of Release.
    /// </summary>
    internal static class DevConsole
    {
        private sealed class Command
        {
            internal string Usage;
            internal Action<string[]> Handler;
        }

        private static readonly Dictionary<string, Command> _commands = new Dictionary<string, Command>(StringComparer.OrdinalIgnoreCase);
        private static int _lastFrame = -1;
        private static string _lastSig = "";

        static DevConsole()
        {
            Register("help", "awe help", _ => Help());
            Register("list", "awe list [group]", List);
            Register("get", "awe get <key>", Get);
            Register("set", "awe set <key> <value>", Set);
            Register("reset", "awe reset <key|all>", Reset);
            Register("status", "awe status", _ => Status());
            Register("preset", "awe preset <" + string.Join("|", Presets.All.Keys) + ">", Preset);
        }

        /// <summary>Adds <c>awe &lt;sub&gt;</c>. The handler gets the words after the subcommand.</summary>
        internal static void Register(string sub, string usage, Action<string[]> handler)
        {
            if (_commands.ContainsKey(sub)) { Core.Log?.Warning("[Dev] awe " + sub + " registered twice, the second one wins."); }
            _commands[sub] = new Command { Usage = usage, Handler = handler };
        }

        /// <summary>True on the host; otherwise logs that the command needs the host.</summary>
        internal static bool RequireHost(string what)
        {
            if (Net.IsServer) return true;
            Core.Log?.Warning("[Dev] awe " + what + ": run this on the host.");
            return false;
        }

        internal static bool TryHandle(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            return Dispatch(raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        }

        internal static bool TryHandle(Il2CppSystem.Collections.Generic.List<string> args)
        {
            if (args == null || args.Count == 0) return false;
            string[] parts = new string[args.Count];
            for (int i = 0; i < args.Count; i++) parts[i] = args[i];
            return Dispatch(parts);
        }

        /// <summary>True when the line was ours and the game must not see it.</summary>
        private static bool Dispatch(string[] parts)
        {
            if (parts.Length == 0) return false;
            string head = parts[0];
            bool isHelp = string.Equals(head, "awehelp", StringComparison.OrdinalIgnoreCase);
            if (!isHelp && !string.Equals(head, "awe", StringComparison.OrdinalIgnoreCase)) return false;

            string sig = string.Join(" ", parts);
            int frame = Time.frameCount;
            if (frame == _lastFrame && sig == _lastSig) return true;   // the second overload of the same submission
            _lastFrame = frame; _lastSig = sig;

            string sub = isHelp ? "help" : (parts.Length > 1 ? parts[1] : "help");
            string[] rest = isHelp ? Array.Empty<string>() : parts.Skip(2).ToArray();
            try
            {
                if (_commands.TryGetValue(sub, out Command cmd)) cmd.Handler(rest);
                else { Core.Log?.Warning("[Dev] unknown command 'awe " + sub + "'."); Help(); }
            }
            catch (Exception e) { Core.Failed("dev console 'awe " + sub + "'", e); }
            return true;
        }

        private static void Help()
        {
            Core.Log?.Msg("[Dev] Actually Working Employees commands:");
            foreach (Command c in _commands.Values.OrderBy(c => c.Usage, StringComparer.OrdinalIgnoreCase))
                Core.Log?.Msg("[Dev]   " + c.Usage);
            Core.Log?.Msg("[Dev] Groups: " + string.Join(", ", Options.Groups));
        }

        private static void List(string[] args)
        {
            string group = args.Length > 0 ? args[0] : null;
            if (group != null && !Options.Groups.Any(g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase)))
            {
                Core.Log?.Warning("[Dev] awe list: no group '" + group + "'. Groups: " + string.Join(", ", Options.Groups));
                return;
            }
            IEnumerable<Option> opts = group == null ? Options.All : Options.InGroup(group);
            int n = 0;
            foreach (Option o in opts) { Core.Log?.Msg("[Dev]   " + Line(o)); n++; }
            Core.Log?.Msg("[Dev] " + n + " option(s).");
        }

        private static void Get(string[] args)
        {
            if (args.Length < 1) { Core.Log?.Warning("[Dev] awe get <key>"); return; }
            Option o = Options.Find(args[0]);
            if (o == null) { Core.Log?.Warning("[Dev] awe get: no option '" + args[0] + "'."); return; }
            Core.Log?.Msg("[Dev] " + Line(o));
            if (o.Help.Length > 0) Core.Log?.Msg("[Dev]   " + o.Help);
        }

        private static void Set(string[] args)
        {
            if (args.Length < 2) { Core.Log?.Warning("[Dev] awe set <key> <value>"); return; }
            Option o = Options.Find(args[0]);
            if (o == null) { Core.Log?.Warning("[Dev] awe set: no option '" + args[0] + "'."); return; }
            string raw = string.Join(" ", args.Skip(1));
            if (o.TrySet(raw, out string error)) Core.Log?.Msg("[Dev] " + Line(o));
            else Core.Log?.Warning("[Dev] awe set: " + error);
        }

        private static void Reset(string[] args)
        {
            if (args.Length < 1) { Core.Log?.Warning("[Dev] awe reset <key|all>"); return; }
            if (string.Equals(args[0], "all", StringComparison.OrdinalIgnoreCase))
            {
                int n = Options.ResetAllToDefault();
                Core.Log?.Msg("[Dev] awe reset all: " + n + " option(s) reset.");
                return;
            }
            Option o = Options.Find(args[0]);
            if (o == null) { Core.Log?.Warning("[Dev] awe reset: no option '" + args[0] + "'."); return; }
            if (!o.Editable) { Core.Log?.Warning("[Dev] awe reset: the host controls " + o.Key + "."); return; }
            o.ResetToDefault();
            Core.Log?.Msg("[Dev] " + Line(o));
        }

        private static void Preset(string[] args)
        {
            if (args.Length < 1) { Core.Log?.Warning("[Dev] awe preset <" + string.Join("|", Presets.All.Keys) + ">"); return; }
            Presets.Result r = Presets.Apply(args[0]);
            if (r.Refused != null) { Core.Log?.Warning("[Dev] awe preset: " + r.Refused); return; }
            Core.Log?.Msg("[Dev] awe preset " + args[0].ToLowerInvariant() + ": " + r.Changed + " of " + r.Total + " option(s) changed, "
                + r.Errors.Count + " rejected.");
        }

        private static void Status()
        {
            Core.Log?.Msg("[Dev] role=" + Net.RoleText() + " inWorld=" + Core.InWorld + " options=" + Options.All.Count
                + " changed=" + Options.All.Count(o => !o.IsDefault) + " vanillaBalance=" + Options.VanillaForced
                + " features=" + FeatureHost.All.Count);
            foreach (IFeature f in FeatureHost.All)
            {
                string s;
                try { s = f.Status; }
                catch (Exception e) { Core.Caught("dev console: status of " + f.Name, e); s = "status failed"; }
                Core.Log?.Msg("[Dev]   " + f.Name + ": " + s);
            }
        }

        private static string Line(Option o)
        {
            string host = o.HostValueText != null ? "  host=" + o.Describe(o.HostValueText) : "";
            string flags = (o.HostOnly ? "" : " local") + (o.Editable ? "" : " read-only");
            return o.Key + " = " + o.Describe(o.ValueText) + (o.IsDefault ? "" : "  (default " + o.Describe(o.DefaultText) + ")") + host + flags;
        }
    }

    [HarmonyPatch(typeof(Il2CppScheduleOne.Console), nameof(Il2CppScheduleOne.Console.SubmitCommand), new Type[] { typeof(string) })]
    internal static class Awe_Console_SubmitCommand_String_Patch
    {
        private static bool Prefix(string args)
        {
            try { return !DevConsole.TryHandle(args); }
            catch (Exception e) { Core.Failed("dev console (string overload)", e); return true; }
        }
    }

    [HarmonyPatch(typeof(Il2CppScheduleOne.Console), nameof(Il2CppScheduleOne.Console.SubmitCommand), new Type[] { typeof(Il2CppSystem.Collections.Generic.List<string>) })]
    internal static class Awe_Console_SubmitCommand_List_Patch
    {
        private static bool Prefix(Il2CppSystem.Collections.Generic.List<string> args)
        {
            try { return !DevConsole.TryHandle(args); }
            catch (Exception e) { Core.Failed("dev console (list overload)", e); return true; }
        }
    }
}
#endif
