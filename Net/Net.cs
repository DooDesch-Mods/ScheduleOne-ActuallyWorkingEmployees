using System;
using Il2CppFishNet;
using Il2CppFishNet.Managing;
using Il2CppScheduleOne.Networking;   // Lobby (PersistentSingleton<Lobby>)

namespace ActuallyWorkingEmployees.Networking
{
    /// <summary>
    /// Network role checks. IsServer answers "do I run employee logic?" and is true in single-player, because the host
    /// runs a local FishNet server. IsClientOnly is a connected co-op client: vanilla behaviour, read-only settings.
    /// IsCoop is a real Steam lobby with more than the local player.
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

        /// <summary>True only in a real Steam co-op lobby with more than the local player.</summary>
        internal static bool IsCoop()
        {
            try
            {
                Lobby lobby = PersistentSingleton<Lobby>.Instance;
                return lobby != null && lobby.IsInLobby && lobby.PlayerCount > 1;   // no lobby manager yet: single-player
            }
            catch (Exception e) { Core.Caught("net: read the Steam lobby", e); return false; }
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
