using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ActuallyWorkingEmployees.Networking;
using Il2CppScheduleOne.Quests;   // QuestManager, EQuestState

namespace ActuallyWorkingEmployees.Features.Whip
{
    /// <summary>
    /// Whip messages between a co-op client and the host. The host decides every whip effect; a client only asks.
    ///
    /// S1API has no message channel between peers, so the messages ride the game's quest state RPCs with a private
    /// "AWE:" guid, the same way RVRepairVan does: the client calls QuestManager.SendQuestState (a ServerRpc), the host
    /// answers through ReceiveQuestState (an ObserversRpc). The prefixes take our guids out before the vanilla body sees
    /// them and let every real quest guid through. On this build a remote client's SendQuestState reaches the host as
    /// the relayed ReceiveQuestState, so both prefixes route by role.
    ///
    ///   intent: AWE:whip|clientId|employeeGuid
    ///   result: AWE:whipr|clientId|code|minutes|employeeName|reason
    /// </summary>
    internal static class WhipNet
    {
        private const string Prefix = "AWE:";
        private const string IntentOp = "whip";
        private const string ResultOp = "whipr";
        private static bool _patched;
        internal static int Sent, Received, Answered;

        /// <summary>Patches the two quest RPC bodies. A failure leaves the whip working for the host only, and is logged.</summary>
        internal static void Patch()
        {
            if (_patched) return;
            _patched = true;
            HarmonyLib.Harmony h = Core.Instance?.HarmonyInstance;
            if (h == null) { Core.Log?.Error("[Whip] no Harmony instance: co-op clients cannot use the whip."); return; }
            TryPatch(h, "RpcLogic___SendQuestState", nameof(SendStatePrefix));
            TryPatch(h, "RpcLogic___ReceiveQuestState", nameof(ReceiveStatePrefix));
        }

        private static void TryPatch(HarmonyLib.Harmony h, string namePrefix, string prefixMethod)
        {
            try
            {
                MethodInfo target = typeof(QuestManager)
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name.StartsWith(namePrefix, StringComparison.Ordinal));
                if (target == null) { Core.Log?.Error("[Whip] " + namePrefix + " not found: co-op clients cannot use the whip."); return; }
                h.Patch(target, prefix: new HarmonyMethod(typeof(WhipNet).GetMethod(prefixMethod, BindingFlags.NonPublic | BindingFlags.Static)));
                if (Core.Verbose) Core.Log?.Msg("[Whip] patched " + target.Name + ".");
            }
            catch (Exception e) { Core.Failed("whip: patch " + namePrefix, e); }
        }

        // RpcLogic___SendQuestState(string guid, EQuestState state): guid is argument 0. Runs locally on the sender too.
        private static bool SendStatePrefix(string __0) => !Take(__0);

        // RpcLogic___ReceiveQuestState(NetworkConnection conn, string guid, EQuestState state): guid is argument 1.
        private static bool ReceiveStatePrefix(string __1) => !Take(__1);

        /// <summary>True when the guid is ours; it is then handled here and never reaches the quest code.</summary>
        private static bool Take(string guid)
        {
            if (guid == null || !guid.StartsWith(Prefix, StringComparison.Ordinal)) return false;
            try
            {
                string[] p = guid.Substring(Prefix.Length).Split('|');
                if (p.Length >= 3 && p[0] == IntentOp)
                {
                    if (!Net.IsServer) return true;   // the sender's own local run, or a relay to another client
                    Received++;
                    if (!int.TryParse(p[1], out int client)) { Core.Log?.Warning("[Whip] intent with a bad client id: " + guid); return true; }
                    Whip.HandleIntent(client, p[2]);
                }
                else if (p.Length >= 6 && p[0] == ResultOp)
                {
                    if (Net.IsServer) return true;    // the host told its player directly
                    if (!int.TryParse(p[1], out int client) || client != LocalClientId()) return true;
                    int.TryParse(p[3], out int minutes);
                    Whip.ShowResult(p[2], minutes, p[4], p[5]);
                }
                else Core.Log?.Warning("[Whip] unknown AWE message: " + guid);
            }
            catch (Exception e) { Core.Failed("whip: handle message", e); }
            return true;
        }

        /// <summary>Client: asks the host to whip the employee.</summary>
        internal static void SendIntent(string employeeGuid)
        {
            try
            {
                var qm = NetworkSingleton<QuestManager>.Instance;
                if (qm == null) { Core.Log?.Error("[Whip] no QuestManager: the whip request was not sent."); return; }
                qm.SendQuestState(Prefix + IntentOp + "|" + LocalClientId() + "|" + employeeGuid, EQuestState.Inactive);
                Sent++;
                Core.Log?.Msg("[Whip] request sent to the host for employee " + employeeGuid + ".");
            }
            catch (Exception e) { Core.Failed("whip: send the request", e); }
        }

        /// <summary>Host: tells the requesting client what the whip did.</summary>
        internal static void SendResult(int client, string code, int minutes, string name, string reason)
        {
            try
            {
                var qm = NetworkSingleton<QuestManager>.Instance;
                if (qm == null) { Core.Log?.Error("[Whip] no QuestManager: the whip result was not sent."); return; }
                string msg = Prefix + ResultOp + "|" + client + "|" + code + "|" + minutes + "|" + Clean(name) + "|" + Clean(reason);
                qm.ReceiveQuestState(null, msg, EQuestState.Inactive);
                Answered++;
                Core.Log?.Msg("[Whip] result '" + code + "' sent to client " + client + ".");
            }
            catch (Exception e) { Core.Failed("whip: send the result", e); }
        }

        private static string Clean(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace('|', '/');

        internal static int LocalClientId()
        {
            try
            {
                var cm = Il2CppFishNet.InstanceFinder.ClientManager;
                return cm != null && cm.Connection != null ? cm.Connection.ClientId : -1;
            }
            catch (Exception e) { Core.Caught("whip: read the local client id", e); return -1; }
        }
    }
}
