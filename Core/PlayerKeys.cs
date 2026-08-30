using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YouAreNotWorthy;

internal enum PersonalKeyMutationResult : byte
{
    InvalidKey,
    NoLocalPlayer,
    Added,
    AlreadyPresent,
    Removed,
    NotPresent
}

internal static class PlayerKeys
{
    private const string RpcSetPlayerKeyDirect = "YNW_SetPlayerKeyDirect";
    private const string RpcDistributePersonalKeyAt = "YNW_DistributePersonalKeyAt";
    private const int MaxPersonalKeyLength = 256;
    internal const float PersonalKeyGrantRadius = 32f;

    private static ZRoutedRpc? _registeredRpc;

    internal static bool Has(string? key)
    {
        string normalizedKey = ValheimNameUtils.NormalizeKey(key);
        if (string.IsNullOrEmpty(normalizedKey))
        {
            return true;
        }

        if ((Object?)Player.m_localPlayer == null
            || !ProgressionIndex.TryGetCanonicalPersonalKey(normalizedKey, out string canonicalKey))
        {
            return false;
        }

        return HasNativeKey(Player.m_localPlayer, canonicalKey);
    }

    internal static bool HasNativeKey(Player? player, string? key)
    {
        if ((Object?)player == null || string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        if (player.HaveUniqueKey(key))
        {
            return true;
        }

        foreach (string nativeKey in player.GetUniqueKeys())
        {
            if (string.Equals(nativeKey, key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static PersonalKeyMutationResult MutateLocal(string? key, bool add)
    {
        if (!ProgressionIndex.TryResolvePersonalKey(key, out string canonicalKey))
        {
            return PersonalKeyMutationResult.InvalidKey;
        }

        Player? player = Player.m_localPlayer;
        if ((Object?)player == null)
        {
            return PersonalKeyMutationResult.NoLocalPlayer;
        }

        List<string> storedKeys = FindNativeKeys(player, canonicalKey);
        if (add)
        {
            if (storedKeys.Count > 0)
            {
                return PersonalKeyMutationResult.AlreadyPresent;
            }

            player.AddUniqueKey(canonicalKey);
            return PersonalKeyMutationResult.Added;
        }

        if (storedKeys.Count == 0)
        {
            return PersonalKeyMutationResult.NotPresent;
        }

        bool removed = false;
        foreach (string storedKey in storedKeys)
        {
            removed |= player.RemoveUniqueKey(storedKey);
        }

        return removed ? PersonalKeyMutationResult.Removed : PersonalKeyMutationResult.NotPresent;
    }

    private static void GrantToPlayer(Player? player, string? key)
    {
        if (player == null || !ProgressionIndex.TryRegisterPersonalKey(key, out string canonicalKey))
        {
            return;
        }

        if ((Object?)Player.m_localPlayer != null && (Object)player == (Object)Player.m_localPlayer)
        {
            _ = MutateLocal(canonicalKey, add: true);
            return;
        }

        long uid = player.GetOwner();
        ZNet? net = ZNet.instance;
        ZNetPeer? peer = uid == 0 || (Object?)net == null || !net.IsServer()
            ? null
            : net.GetPeer(uid);
        if (peer == null || !peer.IsReady() || !peer.m_rpc.IsConnected())
        {
            YouAreNotWorthyPlugin.Log.LogWarning($"Could not route personal key '{canonicalKey}' to the target player.");
            return;
        }

        peer.m_rpc.Invoke(RpcSetPlayerKeyDirect, canonicalKey);
    }

    internal static void EnsurePlayerRpcRegistered()
    {
        ZRoutedRpc? rpc = ZRoutedRpc.instance;
        if (rpc == null)
        {
            return;
        }

        if (_registeredRpc == rpc)
        {
            return;
        }

        try
        {
            rpc.Register<string, Vector3>(RpcDistributePersonalKeyAt, RPC_DistributePersonalKeyAt);
            _registeredRpc = rpc;
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError($"Failed to register YNW personal-key RPCs: {ex}");
        }
    }

    internal static Player? FindPlayerByOwner(long owner)
    {
        foreach (Player player in Player.GetAllPlayers())
        {
            if ((Object?)player != null && player.GetOwner() == owner)
            {
                return player;
            }
        }

        return null;
    }

    internal static bool TryDistributeGlobalKey(string? globalKey, long sender)
    {
        if (!ProgressionIndex.TryRegisterPersonalKey(globalKey, out string canonicalKey))
        {
            return false;
        }

        Player? source = FindPlayerByOwner(sender);
        if ((Object?)source == null)
        {
            YouAreNotWorthyPlugin.Log.LogWarning(
                $"Could not attribute global key '{globalKey}' to a player; no personal keys were distributed.");
            return false;
        }

        return TryDistributePersonalKeyAt(canonicalKey, ((Component)source).transform.position);
    }

    internal static bool TryRequestPersonalKeyAt(
        string? personalKey,
        Vector3 eventPosition,
        bool originatedAsGlobalKey = false)
    {
        if (!TryValidateRequestedPersonalKey(personalKey, out string canonicalKey))
        {
            return false;
        }

        try
        {
            if ((Object?)ZNet.instance != null && ZNet.instance.IsServer())
            {
                if (originatedAsGlobalKey)
                {
                    KeyReferenceWriter.ObserveGlobalKey(canonicalKey);
                }

                return TryDistributePersonalKeyAt(canonicalKey, eventPosition);
            }

            EnsurePlayerRpcRegistered();
            if (ZRoutedRpc.instance == null)
            {
                YouAreNotWorthyPlugin.Log.LogWarning(
                    $"Could not route personal key '{canonicalKey}' at its event position.");
                return false;
            }

            ZRoutedRpc.instance.InvokeRoutedRPC(
                RpcDistributePersonalKeyAt,
                canonicalKey,
                eventPosition);
            return true;
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogWarning(
                $"Failed to route personal key '{canonicalKey}' with its event position: {ex}");
            return false;
        }
    }

    private static bool TryDistributePersonalKeyAt(string? personalKey, Vector3 eventPosition)
    {
        if (!ProgressionIndex.TryRegisterPersonalKey(personalKey, out string canonicalKey))
        {
            return false;
        }

        List<Player> nearby = new();
        Player.GetPlayersInRange(eventPosition, PersonalKeyGrantRadius, nearby);
        foreach (Player player in nearby)
        {
            GrantToPlayer(player, canonicalKey);
        }

        return true;
    }

    internal static void RegisterDirectPeer(ZNet net, ZNetPeer peer)
    {
        if (net.IsServer())
        {
            return;
        }

        try
        {
            peer.m_rpc.Register<string>(RpcSetPlayerKeyDirect, RPC_SetPlayerKeyDirect);
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError($"Failed to register the YNW direct personal-key grant RPC: {ex}");
        }
    }

    private static void RPC_SetPlayerKeyDirect(ZRpc server, string key)
    {
        ZNetPeer? serverPeer = ZNet.instance?.GetServerPeer();
        if (serverPeer == null || !ReferenceEquals(serverPeer.m_rpc, server) || !serverPeer.m_server)
        {
            YouAreNotWorthyPlugin.Log.LogWarning(
                $"Ignored personal-key grant for '{key}' from a non-server connection.");
            return;
        }

        _ = MutateLocal(key, add: true);
    }

    private static void RPC_DistributePersonalKeyAt(
        long sender,
        string key,
        Vector3 eventPosition)
    {
        if ((Object?)ZNet.instance == null || !ZNet.instance.IsServer())
        {
            YouAreNotWorthyPlugin.Log.LogWarning(
                $"Ignored non-server personal-key distribution request for '{SanitizeKeyForLog(key)}'.");
            return;
        }

        if (!TryGetReadySenderPlayer(sender, out _))
        {
            YouAreNotWorthyPlugin.Log.LogWarning(
                $"Ignored personal-key distribution request for '{SanitizeKeyForLog(key)}' from an unready peer.");
            return;
        }

        if (!TryValidateRequestedPersonalKey(key, out string canonicalKey))
        {
            YouAreNotWorthyPlugin.Log.LogWarning(
                $"Ignored invalid personal-key distribution request from peer {sender}.");
            return;
        }

        if (!IsFinite(eventPosition))
        {
            YouAreNotWorthyPlugin.Log.LogWarning(
                $"Ignored personal-key distribution request for '{canonicalKey}' with a non-finite event position.");
            return;
        }

        // The only non-global event-position requests in the current schema are
        // configured custom defeat keys. Infer the reference category from the
        // authoritative server configuration instead of trusting a client flag.
        if (!ProgressionIndex.TryGetDefeatPrefabs(canonicalKey, out _))
        {
            KeyReferenceWriter.ObserveGlobalKey(canonicalKey);
        }

        TryDistributePersonalKeyAt(canonicalKey, eventPosition);
    }

    private static bool TryGetReadySenderPlayer(long sender, out Player player)
    {
        player = null!;
        ZNet? net = ZNet.instance;
        if ((Object?)net == null || !net.IsServer())
        {
            return false;
        }

        ZNetPeer? peer = net.GetPeer(sender);
        Player? candidate = FindPlayerByOwner(sender);
        if (peer == null
            || !peer.IsReady()
            || peer.m_rpc == null
            || !peer.m_rpc.IsConnected()
            || peer.m_characterID.IsNone()
            || peer.m_characterID.UserID != sender
            || (Object?)candidate == null
            || candidate.GetOwner() != sender
            || candidate.GetZDOID() != peer.m_characterID)
        {
            return false;
        }

        player = candidate;
        return true;
    }

    private static bool TryValidateRequestedPersonalKey(string? key, out string canonicalKey)
    {
        string candidate = (key ?? string.Empty).Trim();
        if (candidate.Length == 0 || candidate.Length > MaxPersonalKeyLength)
        {
            canonicalKey = string.Empty;
            return false;
        }

        foreach (char character in candidate)
        {
            if (char.IsControl(character))
            {
                canonicalKey = string.Empty;
                return false;
            }
        }

        return ProgressionIndex.TryRegisterPersonalKey(candidate, out canonicalKey);
    }

    private static bool IsFinite(Vector3 position)
    {
        return !float.IsNaN(position.x)
               && !float.IsInfinity(position.x)
               && !float.IsNaN(position.y)
               && !float.IsInfinity(position.y)
               && !float.IsNaN(position.z)
               && !float.IsInfinity(position.z);
    }

    private static string SanitizeKeyForLog(string? key)
    {
        string raw = key ?? string.Empty;
        int length = Math.Min(raw.Length, MaxPersonalKeyLength);
        char[] sanitized = new char[length];
        for (int i = 0; i < length; i++)
        {
            sanitized[i] = char.IsControl(raw[i]) ? '?' : raw[i];
        }

        return new string(sanitized);
    }

    private static List<string> FindNativeKeys(Player player, string key)
    {
        List<string> matches = new();
        foreach (string nativeKey in player.GetUniqueKeys())
        {
            if (string.Equals(nativeKey, key, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(nativeKey);
            }
        }

        return matches;
    }
}
