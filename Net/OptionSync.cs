using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ActuallyWorkingEmployees.Config;
using ActuallyWorkingEmployees.Features;
using Il2CppScheduleOne.Networking;   // Lobby, ILobbyService
#if DEBUG
using ActuallyWorkingEmployees.Debugging;
#endif

namespace ActuallyWorkingEmployees.Networking
{
    /// <summary>
    /// Co-op option sync. The host publishes the balance values it effectively runs with in ONE Steam lobby key
    /// (<see cref="LobbyKey"/>); each client reads it and hands the values to <see cref="Option.SetHostValue"/>.
    ///
    /// Wire form: <c>1|&lt;hash8&gt;|&lt;body&gt;</c>. The body lists only host-only options that differ from their default,
    /// grouped as <c>group:id=value,id=value;group:id=value</c> with lower-case group and id (the option key split at the
    /// dot). Option ids are the stable names, so a newer or older mod version never misreads an index. The hash is FNV-1a
    /// 32 over the body: clients use it to detect a change, and to reject a value that Steam truncated or garbled.
    /// Bools travel as 1/0. A present key with an empty body means "host runs AWE, everything at default".
    ///
    /// Steam gives the whole lobby one ~8 KB metadata blob shared with the game and every other mod, and answers a
    /// refused write with silence. So the payload stays small, and every write is read back and compared.
    /// </summary>
    internal sealed class OptionSync : IFeature
    {
        internal const string LobbyKey = "awe_opts";
        private const string WireVersion = "1";
        private const float PollSeconds = 2f;
        private const float RetrySeconds = 10f;
        private const float InfoThrottleSeconds = 10f;
        private const int WarnBytes = 3000;

        public string Name => "coop-sync";

        private float _poll;
        private float _retryIn;
        private float _clock;
        private float _lastInfo = -999f;

        // Host state.
        private string _lastWritten = "";
        private int _writes;
        private int _readbackFail;
        private int _lastBytes;
        private string _lastHash = "";

        // Client state.
        private string _appliedHash = "";
        private int _applied;
        private string _badWire = "";

        private bool _wasClient;

        public void Init()
        {
#if DEBUG
            DevConsole.Register("sync", "awe sync", _ => DevReport());
#endif
        }

        public void Reset()
        {
            if (_appliedHash.Length > 0) ClearHostValues("state reset");
            _lastWritten = "";
            _lastHash = "";
            _lastBytes = 0;
            _poll = 0f;
            _retryIn = 0f;
            _wasClient = false;
        }

        public string Status
            => "role=" + Net.RoleText() + " bytes=" + _lastBytes + " writes=" + _writes + " readback_fail=" + _readbackFail
               + " applied=" + _applied + " hash=" + (_lastHash.Length > 0 ? _lastHash : "-");

        public void Tick(float dt)
        {
            _clock += dt;
            _poll += dt;
            if (_poll < PollSeconds) return;
            _poll = 0f;

            if (Net.IsClientOnly)
            {
                _wasClient = true;
                ClientPoll();
            }
            else
            {
                if (_wasClient)
                {
                    _wasClient = false;
                    if (_appliedHash.Length > 0) ClearHostValues("left the host's session");
                }
                if (Net.IsServer) HostPoll();
            }
        }

        // ---- host ----

        private void HostPoll()
        {
            if (_retryIn > 0f) _retryIn = Math.Max(0f, _retryIn - PollSeconds);

            // A lobby with only the host in it needs nothing yet; the write happens when the first guest arrives.
            // Singleplayer has no lobby at all.
            if (!Net.IsCoop()) return;
            if (_retryIn > 0f) return;

            Lobby lobby = SafeLobby("host poll");
            if (lobby == null) return;

            string wire = Encode(out string hash);
            _lastHash = hash;
            _lastBytes = Utf8(LobbyKey) + Utf8(wire);

            string current = ReadKey(lobby);
            if (current == wire) { _lastWritten = wire; return; }   // already published (also after a lobby change)

            if (_lastBytes > WarnBytes)
                Core.Log?.Warning("[CoopSync] payload is " + _lastBytes + " bytes of Steam's ~8192 lobby budget.");
            Write(lobby, wire, hash);
        }

        private void Write(Lobby lobby, string wire, string hash)
        {
            bool threw = false;
            try { lobby.SetLobbyData(LobbyKey, wire); }
            catch (Exception e)
            {
                threw = true;
                _readbackFail++;
                _retryIn = RetrySeconds;
                Core.Log?.Error("[CoopSync] SetLobbyData threw for key " + LobbyKey + " (" + _lastBytes + " bytes): " + e.GetType().Name + ": " + e.Message);
            }
            if (threw) return;

            string back = ReadKey(lobby);
            if (back != wire)
            {
                _readbackFail++;
                _retryIn = RetrySeconds;
                Core.Log?.Error("[CoopSync] lobby write not confirmed for key " + LobbyKey + " (" + _lastBytes + " bytes, wrote hash " + hash
                    + ", read back " + (back == null ? "nothing" : Utf8(back) + " bytes") + "). Steam refuses writes once the lobby's ~8 KB are used up.");
                return;
            }
            _lastWritten = wire;
            _writes++;
            Info("[CoopSync] published " + OptionCountOf(wire) + " changed option(s), key " + LobbyKey + ", " + _lastBytes + " bytes, hash " + hash + ".");
        }

        // ---- client ----

        private void ClientPoll()
        {
            Lobby lobby = SafeLobby("client poll");
            if (lobby == null) return;

            string wire = ReadKey(lobby);
            if (string.IsNullOrEmpty(wire))
            {
                if (_appliedHash.Length > 0) ClearHostValues("the host's key is gone");
                return;
            }

            if (!TryDecode(wire, out string hash, out List<KeyValuePair<Option, string>> values, out string error))
            {
                if (wire != _badWire)   // the poll repeats every two seconds; report each bad value once
                {
                    _badWire = wire;
                    Core.Log?.Error("[CoopSync] could not decode the host's options (" + Utf8(wire) + " bytes), local values untouched: " + error);
                }
                return;
            }
            _lastHash = hash;
            _lastBytes = Utf8(LobbyKey) + Utf8(wire);
            if (hash == _appliedHash) return;

            ApplyHostValues(values);
            _appliedHash = hash;
            _applied++;
            Info("[CoopSync] applied the host's options: " + values.Count + " changed, hash " + hash + ", " + _lastBytes + " bytes.");
        }

        private static void ApplyHostValues(List<KeyValuePair<Option, string>> changed)
        {
            var map = new Dictionary<Option, string>();
            foreach (var kv in changed) map[kv.Key] = kv.Value;
            foreach (Option o in Options.All)
            {
                if (!o.HostOnly) continue;
                // An option the host left out runs at its default there, so that is the value the client shows.
                o.SetHostValue(map.TryGetValue(o, out string v) ? v : o.DefaultText);
            }
        }

        private void ClearHostValues(string why)
        {
            int n = 0;
            foreach (Option o in Options.All)
            {
                if (!o.HostOnly) continue;
                try { o.ClearHostValue(); n++; }
                catch (Exception e) { Core.Failed("coop-sync: clear host value " + o.Key, e); }
            }
            _appliedHash = "";
            Info("[CoopSync] cleared the host's values on " + n + " option(s): " + why + ".");
        }

        // ---- encoding ----

        /// <summary>Wire text for the options this host effectively runs with.</summary>
        internal static string Encode(out string hash)
        {
            var groups = new List<string>();
            string currentGroup = null;
            var sb = new StringBuilder();
            foreach (Option o in Options.All)
            {
                if (!o.HostOnly) continue;
                string text = EffectiveText(o);
                if (text == o.DefaultText) continue;

                string value = o.Kind == OptionKind.Bool ? (text == "true" ? "1" : "0") : text;
                string group = o.Group.ToLowerInvariant();
                string id = o.Id.ToLowerInvariant();
                if (!Safe(group) || !Safe(id) || !Safe(value))
                {
                    Core.Log?.Error("[CoopSync] option " + o.Key + " skipped: its id or value '" + value + "' contains a reserved character.");
                    continue;
                }
                if (group != currentGroup)
                {
                    if (currentGroup != null) groups.Add(sb.ToString());
                    sb.Clear();
                    sb.Append(group).Append(':');
                    currentGroup = group;
                }
                else sb.Append(',');
                sb.Append(id).Append('=').Append(value);
            }
            if (currentGroup != null) groups.Add(sb.ToString());

            string body = string.Join(";", groups);
            hash = Fnv(body);
            return WireVersion + "|" + hash + "|" + body;
        }

        /// <summary>The value the host's game logic uses: balance options read their default while vanilla balance is forced.</summary>
        private static string EffectiveText(Option o) => o.Balance && Options.VanillaForced ? o.DefaultText : o.ValueText;

        private static bool Safe(string s) => s.IndexOfAny(new[] { ';', ',', '=', ':', '|' }) < 0;

        /// <summary>
        /// Decodes <paramref name="wire"/> without touching any option. Unknown ids are skipped with one warning; any other
        /// problem (version, hash, malformed entry, value out of range) fails the whole decode.
        /// </summary>
        internal static bool TryDecode(string wire, out string hash, out List<KeyValuePair<Option, string>> values, out string error)
        {
            hash = "";
            values = new List<KeyValuePair<Option, string>>();
            error = null;

            string[] parts = wire.Split(new[] { '|' }, 3);
            if (parts.Length != 3) { error = "not in the form version|hash|body"; return false; }
            if (parts[0] != WireVersion) { error = "unsupported wire version '" + parts[0] + "' (this mod reads " + WireVersion + ")"; return false; }
            hash = parts[1];
            string body = parts[2];
            if (Fnv(body) != hash) { error = "hash mismatch, the lobby value is cut off or damaged (header " + hash + ", body " + Fnv(body) + ")"; return false; }

            var unknown = new List<string>();
            if (body.Length > 0)
            {
                foreach (string group in body.Split(';'))
                {
                    int colon = group.IndexOf(':');
                    if (colon <= 0) { error = "group entry without a name: '" + group + "'"; return false; }
                    string gname = group.Substring(0, colon);
                    foreach (string entry in group.Substring(colon + 1).Split(','))
                    {
                        int eq = entry.IndexOf('=');
                        if (eq <= 0) { error = "entry without a value: '" + entry + "'"; return false; }
                        string key = gname + "." + entry.Substring(0, eq);
                        string raw = entry.Substring(eq + 1);
                        Option o = Options.Find(key);
                        if (o == null || !o.HostOnly) { unknown.Add(key); continue; }
                        if (!TryValue(o, raw, out string text, out string why)) { error = key + ": " + why; return false; }
                        values.Add(new KeyValuePair<Option, string>(o, text));
                    }
                }
            }
            if (unknown.Count > 0)
                Core.WarnOnce("coop-sync unknown " + hash, "[CoopSync] the host sent " + unknown.Count + " option(s) this version does not know, skipped: " + string.Join(", ", unknown.Take(8)) + (unknown.Count > 8 ? ", ..." : ""));
            return true;
        }

        private static bool TryValue(Option o, string raw, out string text, out string why)
        {
            text = raw; why = null;
            switch (o.Kind)
            {
                case OptionKind.Bool:
                    if (raw == "1") { text = "true"; return true; }
                    if (raw == "0") { text = "false"; return true; }
                    why = "expected 1 or 0, got '" + raw + "'";
                    return false;
                case OptionKind.Int:
                    if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return true;
                    why = "expected a whole number, got '" + raw + "'";
                    return false;
                case OptionKind.Float:
                    if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && !float.IsNaN(f)) return true;
                    why = "expected a number, got '" + raw + "'";
                    return false;
                case OptionKind.Choice:
                    string match = ((ChoiceOption)o).Choices.FirstOrDefault(c => string.Equals(c, raw, StringComparison.OrdinalIgnoreCase));
                    if (match != null) { text = match; return true; }
                    why = "'" + raw + "' is not one of the choices";
                    return false;
                default:
                    why = "unknown option kind";
                    return false;
            }
        }

        private static string Fnv(string s)
        {
            uint h = 2166136261u;
            foreach (byte b in Encoding.UTF8.GetBytes(s)) { h ^= b; h *= 16777619u; }
            return h.ToString("x8", CultureInfo.InvariantCulture);
        }

        private static int Utf8(string s) => s == null ? 0 : Encoding.UTF8.GetByteCount(s);

        private static int OptionCountOf(string wire)
        {
            int bar = wire.LastIndexOf('|');
            string body = bar >= 0 ? wire.Substring(bar + 1) : "";
            return body.Length == 0 ? 0 : body.Count(c => c == '=');
        }

        // ---- lobby access ----

        private static Lobby SafeLobby(string where)
        {
            try
            {
                Lobby lobby = PersistentSingleton<Lobby>.Instance;
                if (lobby == null) Core.WarnOnce("coop-sync no lobby " + where, "[CoopSync] " + where + ": no Lobby singleton yet.");
                return lobby;
            }
            catch (Exception e) { Core.Caught("coop-sync: Lobby singleton (" + where + ")", e); return null; }
        }

        /// <summary>The key's current value, or null when the lobby cannot be read. Empty means the key is not set.</summary>
        private static string ReadKey(Lobby lobby)
        {
            try
            {
                ILobbyService svc = lobby._lobbyService;
                if (svc == null) { Core.WarnOnce("coop-sync no service", "[CoopSync] the lobby has no lobby service, cannot read " + LobbyKey + "."); return null; }
                return svc.GetLobbyData(LobbyKey) ?? "";
            }
            catch (Exception e) { Core.Failed("coop-sync: GetLobbyData " + LobbyKey, e); return null; }
        }

        private void Info(string msg)
        {
            if (_clock - _lastInfo < InfoThrottleSeconds) return;
            _lastInfo = _clock;
            Core.Log?.Msg(msg);
        }

#if DEBUG
        private void DevReport()
        {
            string key = LobbyKey;
            string wire = null;
            Lobby lobby = SafeLobby("awe sync");
            if (lobby != null) wire = ReadKey(lobby);
            if (Net.IsServer && string.IsNullOrEmpty(wire)) wire = Encode(out _);   // what this host would publish

            Core.Log?.Msg("AWE sync role=" + Net.RoleText() + " key=" + key + " bytes=" + (wire == null ? 0 : Utf8(key) + Utf8(wire))
                + " hash=" + (_lastHash.Length > 0 ? _lastHash : "-") + " writes=" + _writes + " readback_fail=" + _readbackFail
                + " applied=" + _applied + " inLobby=" + (lobby != null && SafeInLobby(lobby)));
            if (string.IsNullOrEmpty(wire)) { Core.Log?.Msg("AWE sync no value on the lobby key."); return; }
            if (!TryDecode(wire, out string h, out var values, out string error))
            {
                Core.Log?.Error("AWE sync decode failed: " + error);
                return;
            }
            Core.Log?.Msg("AWE sync decoded " + values.Count + " option(s), hash " + h + ": "
                + (values.Count == 0 ? "(all default)" : string.Join(" ", values.Select(kv => kv.Key.Key + "=" + kv.Value))));
        }

        private static bool SafeInLobby(Lobby lobby)
        {
            try { return lobby.IsInLobby; }
            catch (Exception e) { Core.Caught("coop-sync: IsInLobby", e); return false; }
        }
#endif
    }
}
