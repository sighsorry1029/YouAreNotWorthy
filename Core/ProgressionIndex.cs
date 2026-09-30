using System;
using System.Collections.Generic;
using Object = UnityEngine.Object;

namespace YouAreNotWorthy;

internal sealed class CompiledItemTier
{
    internal CompiledItemTier(int rank, string requiredKey)
    {
        Rank = rank;
        RequiredKey = requiredKey;
    }

    internal int Rank { get; }
    internal string RequiredKey { get; }
}

internal static class ProgressionIndex
{
    private static readonly HashSet<string> SharedBooleanKeys = new(StringComparer.Ordinal)
    {
        "season_winter",
        "season_fall",
        "season_summer",
        "season_spring",
        // Path of Valheiman 4.10.1 server placement and one-time cleanup markers.
        "pov_monolith_placed_2",
        "pov_monolith_reset_1",
        "pov_monolith_start_clearance_1",
        "pov_monolith_start_clearance_2",
        "pov_monolith_start_clearance_3"
    };

    private sealed class IndexState
    {
        internal Dictionary<string, List<string>> DefeatKeysByPrefab { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, List<string>> DefeatPrefabsByKey { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, CompiledItemTier> ItemTiersByResource { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, int> ItemTierRanksByKey { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, string> PersonalKeys { get; } = new(StringComparer.Ordinal);
    }

    private static IndexState _current = new();

    internal static void Configure(ProgressionConfig progression)
    {
        _current = Build(progression);
    }

    internal static void ValidateConfiguration(ProgressionConfig progression)
    {
        _ = Build(progression);
    }

    internal static bool TryGetCanonicalPersonalKey(string? key, out string canonicalKey)
    {
        if (!TryGetBooleanKeyName(key, out string booleanKey))
        {
            canonicalKey = string.Empty;
            return false;
        }

        string normalized = ValheimNameUtils.NormalizeKey(booleanKey);
        if (normalized.Length > 0 && _current.PersonalKeys.TryGetValue(normalized, out canonicalKey))
        {
            return true;
        }

        canonicalKey = string.Empty;
        return false;
    }

    internal static bool TryResolvePersonalKey(string? key, out string canonicalKey)
    {
        if (!TryGetBooleanKeyName(key, out canonicalKey))
        {
            return false;
        }

        string normalizedKey = ValheimNameUtils.NormalizeKey(canonicalKey);
        if (normalizedKey.Length == 0 || IsSharedWorldKey(canonicalKey))
        {
            canonicalKey = string.Empty;
            return false;
        }

        if (_current.PersonalKeys.TryGetValue(normalizedKey, out string existingKey))
        {
            canonicalKey = existingKey;
        }

        return true;
    }

    internal static bool TryGetDefeatKeys(string? prefabName, out IReadOnlyList<string> keys)
    {
        string normalized = ValheimNameUtils.NormalizePrefabName(prefabName);
        if (_current.DefeatKeysByPrefab.TryGetValue(normalized, out List<string> configuredKeys))
        {
            keys = configuredKeys;
            return true;
        }

        keys = Array.Empty<string>();
        return false;
    }

    internal static bool TryGetDefeatPrefabs(string? key, out IReadOnlyList<string> prefabs)
    {
        string normalized = ValheimNameUtils.NormalizeKey(key);
        if (normalized.Length > 0
            && _current.DefeatPrefabsByKey.TryGetValue(normalized, out List<string> configuredPrefabs))
        {
            prefabs = configuredPrefabs;
            return true;
        }

        prefabs = Array.Empty<string>();
        return false;
    }

    internal static bool TryGetItemTier(string? resourceName, out CompiledItemTier tier)
    {
        string normalized = ValheimNameUtils.NormalizeResourceName(resourceName);
        return _current.ItemTiersByResource.TryGetValue(normalized, out tier);
    }

    internal static bool TryGetItemTierKeyRank(string? key, out int rank)
    {
        string normalized = ValheimNameUtils.NormalizeKey(key);
        return _current.ItemTierRanksByKey.TryGetValue(normalized, out rank);
    }

    internal static bool TryRegisterPersonalKey(string? key, out string canonicalKey)
    {
        if (!TryResolvePersonalKey(key, out canonicalKey))
        {
            return false;
        }

        string normalizedKey = ValheimNameUtils.NormalizeKey(canonicalKey);
        if (_current.PersonalKeys.TryGetValue(normalizedKey, out string existingKey))
        {
            canonicalKey = existingKey;
            return true;
        }

        _current.PersonalKeys[normalizedKey] = canonicalKey;
        return true;
    }

    internal static bool IsSharedWorldKey(string? key)
    {
        string rawKey = (key ?? string.Empty).Trim();
        if (rawKey.Length == 0)
        {
            return false;
        }

        string booleanKey = ZoneSystem.GetKeyValue(rawKey, out string value, out _);
        if (!string.IsNullOrEmpty(value))
        {
            return true;
        }

        string normalizedKey = ValheimNameUtils.NormalizeKey(booleanKey);
        if (SharedBooleanKeys.Contains(normalizedKey))
        {
            return true;
        }

        // PoV reads these world records through both GetGlobalKey overloads and
        // GetGlobalKeys. Keep bare-name queries shared too (cooldown/total writes
        // have values). Its separate native character records remain untouched.
        if (normalizedKey.StartsWith("pov_monolith_placed_2_", StringComparison.Ordinal)
            || normalizedKey.StartsWith("pov_monolith_layout_1_", StringComparison.Ordinal)
            || normalizedKey.StartsWith("pov_mono_won_", StringComparison.Ordinal)
            || normalizedKey.StartsWith("pov_mono_cooldown_", StringComparison.Ordinal)
            || normalizedKey.StartsWith("pov_rune_", StringComparison.Ordinal)
            || normalizedKey.StartsWith("pov_dng_", StringComparison.Ordinal)
            || normalizedKey.StartsWith("pov_poi_", StringComparison.Ordinal))
        {
            return true;
        }

        if (!Enum.TryParse(booleanKey, true, out GlobalKeys globalKey))
        {
            return false;
        }

        return globalKey <= GlobalKeys.NonServerOption
               || globalKey == GlobalKeys.activeBosses
               || globalKey == GlobalKeys.AshlandsOcean
               || globalKey == GlobalKeys.Count;
    }

    internal static bool HasWorldKey(string? key)
    {
        string rawKey = (key ?? string.Empty).Trim();
        if (rawKey.Length == 0 || (Object?)ZoneSystem.instance == null)
        {
            return false;
        }

        string baseKey = ZoneSystem.GetKeyValue(rawKey.ToLowerInvariant(), out string requiredValue, out _);
        if (!ZoneSystem.instance.m_globalKeysValues.TryGetValue(baseKey, out string currentValue))
        {
            return false;
        }

        return string.IsNullOrEmpty(requiredValue)
               || string.Equals(currentValue, requiredValue, StringComparison.Ordinal);
    }

    private static IndexState Build(ProgressionConfig progression)
    {
        IndexState next = new();
        CompileDefeatKeys(progression, next);
        CompileItemTiers(progression, next);
        return next;
    }

    private static void CompileDefeatKeys(ProgressionConfig progression, IndexState index)
    {
        foreach (DefeatKeyRule rule in progression.DefeatKeys)
        {
            string canonicalKey = rule.Key.Trim();
            AddPersonalKey(index, canonicalKey);
            foreach (string prefab in rule.Prefabs)
            {
                string normalizedPrefab = ValheimNameUtils.NormalizePrefabName(prefab);
                AddUniqueValue(index.DefeatKeysByPrefab, normalizedPrefab, canonicalKey);
                AddUniquePrefab(index.DefeatPrefabsByKey, canonicalKey, prefab);
            }
        }
    }

    private static void CompileItemTiers(
        ProgressionConfig progression,
        IndexState index)
    {
        for (int rank = 0; rank < progression.Tiers.Count; rank++)
        {
            ItemTierConfig tier = progression.Tiers[rank];
            string canonicalKey = tier.RequiredKey.Trim();
            AddPersonalKey(index, canonicalKey);
            string normalizedKey = ValheimNameUtils.NormalizeKey(canonicalKey);
            if (normalizedKey.Length > 0
                && !index.ItemTierRanksByKey.ContainsKey(normalizedKey))
            {
                index.ItemTierRanksByKey[normalizedKey] = rank;
            }

            CompiledItemTier compiled = new(
                rank,
                canonicalKey);

            foreach (string resource in tier.Resources)
            {
                string normalized = ValheimNameUtils.NormalizeResourceName(resource);
                if (index.ItemTiersByResource.ContainsKey(normalized))
                {
                    throw new InvalidOperationException(
                        $"Resource '{resource}' is assigned to more than one item tier.");
                }

                index.ItemTiersByResource[normalized] = compiled;
            }
        }
    }

    private static void AddUniquePrefab(
        Dictionary<string, List<string>> map,
        string key,
        string prefab)
    {
        string normalizedKey = ValheimNameUtils.NormalizeKey(key);
        if (!map.TryGetValue(normalizedKey, out List<string> prefabs))
        {
            prefabs = new List<string>();
            map[normalizedKey] = prefabs;
        }

        string normalizedPrefab = ValheimNameUtils.NormalizePrefabName(prefab);
        foreach (string configuredPrefab in prefabs)
        {
            if (ValheimNameUtils.NormalizePrefabName(configuredPrefab) == normalizedPrefab)
            {
                return;
            }
        }

        prefabs.Add(prefab.Trim());
    }

    private static void AddPersonalKey(IndexState index, string? key)
    {
        string canonicalKey = (key ?? string.Empty).Trim();
        string normalizedKey = ValheimNameUtils.NormalizeKey(canonicalKey);
        if (normalizedKey.Length == 0)
        {
            return;
        }

        if (IsSharedWorldKey(canonicalKey))
        {
            throw new InvalidOperationException(
                $"Global key '{canonicalKey}' is shared world state and cannot be registered as a personal key.");
        }

        if (index.PersonalKeys.TryGetValue(normalizedKey, out string existingKey)
            && !string.Equals(existingKey, canonicalKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Personal key '{canonicalKey}' conflicts with configured casing '{existingKey}'. Use one canonical spelling everywhere.");
        }

        index.PersonalKeys[normalizedKey] = canonicalKey;
    }

    private static bool TryGetBooleanKeyName(string? key, out string booleanKey)
    {
        string rawKey = (key ?? string.Empty).Trim();
        if (rawKey.Length == 0)
        {
            booleanKey = string.Empty;
            return false;
        }

        _ = ZoneSystem.GetKeyValue(rawKey, out string value, out _);
        if (!string.IsNullOrEmpty(value))
        {
            booleanKey = string.Empty;
            return false;
        }

        if (Enum.TryParse(rawKey, true, out GlobalKeys globalKey)
            && Enum.IsDefined(typeof(GlobalKeys), globalKey))
        {
            booleanKey = globalKey.ToString();
            return true;
        }

        booleanKey = rawKey;
        return true;
    }

    private static void AddUniqueValue(
        Dictionary<string, List<string>> map,
        string key,
        string value)
    {
        if (!map.TryGetValue(key, out List<string> values))
        {
            values = new List<string>();
            map[key] = values;
        }

        if (!values.Contains(value))
        {
            values.Add(value);
        }
    }
}
