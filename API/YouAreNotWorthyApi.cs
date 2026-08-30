using System;
using System.Globalization;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YouAreNotWorthy;

/// <summary>
/// Result of evaluating one key for a specific character.
/// Numeric values are part of the public integration contract.
/// </summary>
public enum KeyQueryResult : byte
{
    Invalid = 0,
    Unavailable = 1,
    PersonalMissing = 2,
    PersonalPresent = 3,
    SharedMissing = 4,
    SharedPresent = 5
}

/// <summary>
/// Stable, read-only integration surface for character-aware progression checks.
/// </summary>
/// <remarks>Call this API from Unity's main thread.</remarks>
public static class YouAreNotWorthyApi
{
    public const int ApiVersion = 1;

    /// <summary>
    /// Evaluates a key for the current local character.
    /// </summary>
    public static KeyQueryResult QueryLocal(string? key)
    {
        try
        {
            if (!TryPrepareKey(
                    key,
                    out string preparedKey,
                    out bool isPersonal,
                    out KeyQueryResult failure))
            {
                return failure;
            }

            Player? player = Player.m_localPlayer;
            if ((Object?)player == null
                || !player.IsOwner()
                || player.GetZDOID().IsNone())
            {
                return KeyQueryResult.Unavailable;
            }

            return isPersonal
                ? ToPersonalResult(PlayerKeys.HasNativeKey(player, preparedKey))
                : QueryShared(preparedKey);
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError(
                $"YNW API failed to evaluate local key '{key ?? string.Empty}': {ex}");
            return KeyQueryResult.Unavailable;
        }
    }

    /// <summary>
    /// Shows YNW's localized center-screen message for a requirement that the
    /// caller has already established is missing for the local character.
    /// </summary>
    /// <param name="key">
    /// The same global-key expression accepted by <see cref="QueryLocal"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the message was displayed; otherwise
    /// <see langword="false"/>. A request can be suppressed when the key is
    /// invalid, YNW or the local character is unavailable, or YNW's shared
    /// blocked-message cooldown is active.
    /// </returns>
    /// <remarks>
    /// Call this method from Unity's main thread. It validates and canonicalizes
    /// <paramref name="key"/>, but deliberately does not evaluate whether the key
    /// is missing. Use <see cref="QueryLocal"/> when the caller has not already
    /// established that result.
    /// </remarks>
    public static bool TryShowLocalMissingRequirement(string? key)
    {
        try
        {
            if (!TryPrepareKey(
                    key,
                    out string preparedKey,
                    out _,
                    out _))
            {
                return false;
            }

            Player? player = Player.m_localPlayer;
            if ((Object?)player == null
                || !player.IsOwner()
                || player.GetZDOID().IsNone())
            {
                return false;
            }

            return ItemRestriction.ShowBlockedMessage(
                RequirementTextResolver.Resolve(preparedKey));
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError(
                $"YNW API failed to show local missing requirement '{key ?? string.Empty}': {ex}");
            return false;
        }
    }

    /// <summary>
    /// Evaluates a key for an authenticated, connected peer. This method is
    /// intended for the authoritative server and never falls back to the host's
    /// local character when the requested peer cannot be resolved.
    /// </summary>
    public static KeyQueryResult QueryPeer(ZNetPeer? peer, string? key)
    {
        try
        {
            if (!TryPrepareKey(
                    key,
                    out string preparedKey,
                    out bool isPersonal,
                    out KeyQueryResult failure))
            {
                return failure;
            }

            if (!TryGetAuthenticatedPeerCharacter(peer, out ZDO character))
            {
                return KeyQueryResult.Unavailable;
            }

            if (!isPersonal)
            {
                return QueryShared(preparedKey);
            }

            return PersonalKeySnapshot.TryHasLiteral(character, preparedKey, out bool hasKey)
                ? ToPersonalResult(hasKey)
                : KeyQueryResult.Unavailable;
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError(
                $"YNW API failed to evaluate peer key '{key ?? string.Empty}': {ex}");
            return KeyQueryResult.Unavailable;
        }
    }

    private static bool TryPrepareKey(
        string? key,
        out string preparedKey,
        out bool isPersonal,
        out KeyQueryResult failure)
    {
        preparedKey = (key ?? string.Empty).Trim();
        isPersonal = false;
        failure = KeyQueryResult.Invalid;
        if (IsInvalidKey(preparedKey))
        {
            return false;
        }

        if (!YouAreNotWorthyPlugin.IsApiReady)
        {
            failure = KeyQueryResult.Unavailable;
            return false;
        }

        if (ProgressionIndex.TryResolvePersonalKey(preparedKey, out string canonicalKey))
        {
            preparedKey = canonicalKey;
            isPersonal = true;
            return true;
        }

        return ProgressionIndex.IsSharedWorldKey(preparedKey);
    }

    private static bool IsInvalidKey(string key)
    {
        if (key.Length == 0)
        {
            return true;
        }

        foreach (char character in key)
        {
            if (char.IsControl(character))
            {
                return true;
            }
        }

        string booleanKey = ZoneSystem.GetKeyValue(key, out _, out _);
        if (long.TryParse(
                booleanKey,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out _))
        {
            return true;
        }

        return Enum.TryParse(booleanKey, true, out GlobalKeys globalKey)
               && Enum.IsDefined(typeof(GlobalKeys), globalKey)
               && globalKey is GlobalKeys.NonServerOption or GlobalKeys.Count;
    }

    private static KeyQueryResult QueryShared(string key)
    {
        return (Object?)ZoneSystem.instance == null
            ? KeyQueryResult.Unavailable
            : ProgressionIndex.HasWorldKey(key)
                ? KeyQueryResult.SharedPresent
                : KeyQueryResult.SharedMissing;
    }

    private static KeyQueryResult ToPersonalResult(bool hasKey)
    {
        return hasKey
            ? KeyQueryResult.PersonalPresent
            : KeyQueryResult.PersonalMissing;
    }

    private static bool TryGetAuthenticatedPeerCharacter(
        ZNetPeer? peer,
        out ZDO character)
    {
        character = null!;
        ZNet? znet = ZNet.instance;
        ZDOMan? zdoMan = ZDOMan.instance;
        if (peer == null
            || (Object?)znet == null
            || !znet.IsServer()
            || zdoMan == null
            || !peer.IsReady()
            || peer.m_rpc == null
            || !peer.m_rpc.IsConnected()
            || peer.m_characterID.IsNone()
            || peer.m_characterID.UserID != peer.m_uid
            || !ReferenceEquals(znet.GetPeer(peer.m_rpc), peer))
        {
            return false;
        }

        ZDO? candidate = zdoMan.GetZDO(peer.m_characterID);
        if (candidate == null
            || !candidate.IsValid()
            || candidate.GetOwner() != peer.m_uid)
        {
            return false;
        }

        Player? livePlayer = null;
        foreach (Player player in Player.GetAllPlayers())
        {
            if ((Object?)player == null
                || player.GetOwner() != peer.m_uid
                || player.GetZDOID() != peer.m_characterID)
            {
                continue;
            }

            if ((Object?)livePlayer != null)
            {
                return false;
            }

            livePlayer = player;
        }

        long playerId = candidate.GetLong(ZDOVars.s_playerID, 0L);
        ZNetView? liveView = (Object?)livePlayer != null
            ? livePlayer.GetComponent<ZNetView>()
            : null;
        if (playerId == 0L
            || (Object?)livePlayer == null
            || livePlayer.GetPlayerID() != playerId
            || (Object?)liveView == null
            || !liveView.IsValid()
            || liveView.GetZDO() != candidate)
        {
            return false;
        }

        character = candidate;
        return true;
    }
}
