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

        if (add)
        {
            foreach (string nativeKey in player.GetUniqueKeys())
            {
                if (string.Equals(nativeKey, canonicalKey, StringComparison.OrdinalIgnoreCase))
                {
                    return PersonalKeyMutationResult.AlreadyPresent;
                }
            }

            player.AddUniqueKey(canonicalKey);
            return PersonalKeyMutationResult.Added;
        }

        List<string> storedKeys = FindNativeKeys(player, canonicalKey);
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

    private static void GrantToPeer(ZNetPeer? peer, string canonicalKey)
    {
        if (peer == null
            || !peer.IsReady()
            || peer.m_rpc == null
            || !peer.m_rpc.IsConnected())
        {
            YouAreNotWorthyPlugin.Log.LogWarning($"Could not route personal key '{canonicalKey}' to the target player.");
            return;
        }

        try
        {
            peer.m_rpc.Invoke(RpcSetPlayerKeyDirect, canonicalKey);
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogWarning(
                $"Could not route personal key '{canonicalKey}' to peer {peer.m_uid}: {ex.Message}");
        }
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

    internal static bool TryGetAuthenticatedPeerCharacter(
        ZNetPeer? peer,
        out ZDO character)
    {
        character = null!;
        ZNet? net = ZNet.instance;
        ZDOMan? zdoMan = ZDOMan.instance;
        ZNetScene? scene = ZNetScene.instance;
        if (peer == null
            || (Object?)net == null
            || !net.IsServer()
            || zdoMan == null
            || (Object?)scene == null
            || !peer.IsReady()
            || peer.m_rpc == null
            || !peer.m_rpc.IsConnected()
            || peer.m_characterID.IsNone()
            || peer.m_characterID.UserID != peer.m_uid
            || !ReferenceEquals(net.GetPeer(peer.m_uid), peer))
        {
            return false;
        }

        ZDO? candidate = zdoMan.GetZDO(peer.m_characterID);
        if (candidate == null
            || !candidate.IsValid()
            || candidate.GetOwner() != peer.m_uid
            || candidate.GetLong(ZDOVars.s_playerID, 0L) == 0L)
        {
            return false;
        }

        GameObject? prefab = scene.GetPrefab(candidate.GetPrefab());
        if ((Object?)prefab == null || prefab.GetComponent<Player>() == null)
        {
            return false;
        }

        character = candidate;
        return true;
    }

    internal static bool TryDistributeGlobalKey(string? globalKey, long sender)
    {
        if (!ProgressionIndex.TryRegisterPersonalKey(globalKey, out string canonicalKey))
        {
            return false;
        }

        if (!TryGetSenderPosition(sender, out Vector3 sourcePosition))
        {
            YouAreNotWorthyPlugin.Log.LogWarning(
                $"Could not attribute global key '{globalKey}' to a player; no personal keys were distributed.");
            return false;
        }

        return TryDistributePersonalKeyAt(canonicalKey, sourcePosition);
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

        ZNet? net = ZNet.instance;
        if ((Object?)net == null || !net.IsServer())
        {
            return false;
        }

        if (TryGetLocalServerPlayerPosition(out Vector3 localPosition)
            && IsWithinGrantRadius(localPosition, eventPosition))
        {
            _ = MutateLocal(canonicalKey, add: true);
        }

        foreach (ZNetPeer peer in net.GetConnectedPeers())
        {
            if (!TryGetAuthenticatedPeerCharacter(peer, out ZDO character))
            {
                continue;
            }

            Vector3 peerPosition = character.GetPosition();
            if (!IsWithinGrantRadius(peerPosition, eventPosition))
            {
                continue;
            }

            GrantToPeer(peer, canonicalKey);
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
        ZNet? net = ZNet.instance;
        if ((Object?)net == null || !net.IsServer())
        {
            YouAreNotWorthyPlugin.Log.LogWarning(
                $"Ignored non-server personal-key distribution request for '{SanitizeKeyForLog(key)}'.");
            return;
        }

        if (!TryGetAuthenticatedPeerCharacter(net.GetPeer(sender), out _))
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

    private static bool TryGetSenderPosition(long sender, out Vector3 position)
    {
        position = Vector3.zero;
        ZNet? net = ZNet.instance;
        if ((Object?)net == null || !net.IsServer())
        {
            return false;
        }

        if (sender == ZNet.GetUID())
        {
            return TryGetLocalServerPlayerPosition(out position);
        }

        if (!TryGetAuthenticatedPeerCharacter(net.GetPeer(sender), out ZDO character))
        {
            return false;
        }

        position = character.GetPosition();
        return IsFinite(position);
    }

    private static bool TryGetLocalServerPlayerPosition(out Vector3 position)
    {
        position = Vector3.zero;
        ZNet? net = ZNet.instance;
        Player? player = Player.m_localPlayer;
        if ((Object?)net == null
            || !net.IsServer()
            || (Object?)player == null
            || !player.IsOwner()
            || player.GetZDOID().IsNone()
            || player.GetPlayerID() == 0L)
        {
            return false;
        }

        position = ((Component)player).transform.position;
        return IsFinite(position);
    }

    private static bool IsWithinGrantRadius(Vector3 playerPosition, Vector3 eventPosition)
    {
        return IsFinite(playerPosition)
               && IsFinite(eventPosition)
               && Vector3.Distance(playerPosition, eventPosition) < PersonalKeyGrantRadius;
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
