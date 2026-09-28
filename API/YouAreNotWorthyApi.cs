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

/// <summary>Read-only forced item-use check; unavailable never means allowed.</summary>
public enum ItemUseQueryResult : byte
{
    Invalid = 0,
    Unavailable = 1,
    Allowed = 2,
    MissingRequirement = 3
}

/// <summary>
/// Stable, read-only integration surface for character-aware progression checks.
/// </summary>
/// <remarks>Call this API from Unity's main thread.</remarks>
public static class YouAreNotWorthyApi
{
    public const int ApiVersion = 2;

    /// <summary>
    /// Checks an item's progression requirement without requiring inventory possession.
    /// Intended for integrations that waive an item's cost, not its progression gate.
    /// This strict query does not grant the local administrator/debug bypass.
    /// </summary>
    public static ItemUseQueryResult QueryLocalItemUse(string? prefabName, out string requiredKey)
    {
        requiredKey = string.Empty;
        try
        {
            Player? player = Player.m_localPlayer;
            if (!YouAreNotWorthyPlugin.IsApiReady || (Object?)player == null
                || !player.IsOwner() || player.GetZDOID().IsNone())
                return ItemUseQueryResult.Unavailable;
            return QueryItemUse(prefabName, QueryLocal, out requiredKey);
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError($"YNW API failed to evaluate local item use: {ex}");
            return ItemUseQueryResult.Unavailable;
        }
    }

    /// <summary>
    /// Server-only item-use check for an authenticated connected character. Resolves
    /// the item from this process's ObjectDB and existing tier cache. No inventory,
    /// key, or world mutation. Never inherits the host's administrator/debug bypass.
    /// </summary>
    public static ItemUseQueryResult QueryPeerItemUse(
        ZNetPeer? peer, string? prefabName, out string requiredKey)
    {
        requiredKey = string.Empty;
        try
        {
            if (!YouAreNotWorthyPlugin.IsApiReady
                || !PlayerKeys.TryGetAuthenticatedPeerCharacter(peer, out _))
                return ItemUseQueryResult.Unavailable;
            return QueryItemUse(prefabName, key => QueryPeer(peer, key), out requiredKey);
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError($"YNW API failed to evaluate peer item use: {ex}");
            return ItemUseQueryResult.Unavailable;
        }
    }

    private static ItemUseQueryResult QueryItemUse(
        string? prefabName, Func<string, KeyQueryResult> query, out string requiredKey)
    {
        requiredKey = string.Empty;
        if (string.IsNullOrWhiteSpace(prefabName)) return ItemUseQueryResult.Invalid;
        try
        {
            if (!RestrictionEvaluator.TryGetItemUseRequirement(prefabName!.Trim(), out requiredKey))
                return ItemUseQueryResult.Unavailable;
            if (requiredKey.Length == 0) return ItemUseQueryResult.Allowed;
            return query(requiredKey) switch
            {
                KeyQueryResult.PersonalPresent or KeyQueryResult.SharedPresent => ItemUseQueryResult.Allowed,
                KeyQueryResult.PersonalMissing or KeyQueryResult.SharedMissing => ItemUseQueryResult.MissingRequirement,
                _ => ItemUseQueryResult.Unavailable
            };
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError($"YNW API failed to evaluate item use '{prefabName}': {ex}");
            return ItemUseQueryResult.Unavailable;
        }
    }

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

            if (!PlayerKeys.TryGetAuthenticatedPeerCharacter(peer, out ZDO character))
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

}
