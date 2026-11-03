using System;
using Il2CppFishNet;
using Il2CppFishNet.Managing;
using Il2CppScheduleOne.Networking;   // Lobby (PersistentSingleton<Lobby>)

namespace ActuallyWorkingEmployees.Networking
{
    /// <summary>
    /// Network role checks. IsServer answers "do I run employee logic?" and is true in single-player, because the host
    /// runs a local FishNet server. IsClientOnly is a connected co-op client: vanilla behaviour, read-only settings.
    /// IsCoop is a Steam lobby with more than the local player.
    ///
    /// All accessors are conservative: any failure returns false and is logged once.
    /// </summary>
    internal static class Net
    {
        /// <summary>True only on the write authority (the host). True in single-player.</summary>
        internal static bool IsServer
        {
            get
            {
                try
                {
                    NetworkManager nm = InstanceFinder.NetworkManager;
                    return nm != null && nm.IsServer;
                }
                catch (Exception e) { Core.Caught("net: read NetworkManager.IsServer", e); return false; }
            }
        }

        /// <summary>True on a co-op client that is connected to someone else's server.</summary>
        internal static bool IsClientOnly
        {
            get
            {
                try
                {
                    NetworkManager nm = InstanceFinder.NetworkManager;
                    return nm != null && nm.IsClient && !nm.IsServer;
                }
                catch (Exception e) { Core.Caught("net: read NetworkManager.IsClient", e); return false; }
            }
        }

        /// <summary>
        /// True in a Steam co-op lobby with more than the local player. The options travel as lobby data, so without a
        /// lobby guest there is nobody to send them to. RemoteClients is reported beside it in the host's guest log line.
        /// </summary>
        internal static bool IsCoop() => LobbyPlayers() > 1;

        /// <summary>Players in the Steam lobby; 0 without a lobby or lobby manager.</summary>
        internal static int LobbyPlayers()
        {
            try
            {
                Lobby lobby = PersistentSingleton<Lobby>.Instance;
                return lobby != null && lobby.IsInLobby ? lobby.PlayerCount : 0;   // no lobby manager yet: single-player
            }
            catch (Exception e) { Core.Caught("net: read the Steam lobby", e); return 0; }
        }

        /// <summary>FishNet connections on this server other than the host's own client; 0 when this is no server.</summary>
        internal static int RemoteClients()
        {
            try
            {
                NetworkManager nm = InstanceFinder.NetworkManager;
                if (nm == null || !nm.IsServer || nm.ServerManager == null || nm.ServerManager.Clients == null) return 0;
                return Math.Max(0, nm.ServerManager.Clients.Count - (nm.IsClient ? 1 : 0));
            }
            catch (Exception e) { Core.Caught("net: read the server's client list", e); return 0; }
        }

        /// <summary>Short role text for status lines and the app.</summary>
        internal static string RoleText()
        {
            if (IsClientOnly) return "client";
            if (IsServer) return IsCoop() ? "host" : "single-player";
            return "offline";
        }
    }
}
