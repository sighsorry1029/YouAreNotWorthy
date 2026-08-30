using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YouAreNotWorthy;

internal static class RequirementTextResolver
{
    private static readonly object Sync = new();

    private static readonly Dictionary<string, string> HildirChestPrefabs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Hildir1"] = "chest_hildir1",
            ["Hildir2"] = "chest_hildir2",
            ["Hildir3"] = "chest_hildir3"
        };

    private static readonly Dictionary<string, List<string>> DefeatPrefabsByKey =
        new(StringComparer.Ordinal);

    private static readonly Dictionary<string, string> CharacterNameTokensByPrefab =
        new(StringComparer.Ordinal);

    private static readonly Dictionary<string, string> ResolvedText =
        new(StringComparer.Ordinal);

    private static readonly Dictionary<string, string> EnglishNamesByToken =
        new(StringComparer.Ordinal);

    private static readonly HashSet<string> ReportedFailures = new(StringComparer.Ordinal);

    private static bool _sourcesDirty = true;
    private static int _sceneInstanceId;
    private static int _netPrefabCount = -1;
    private static int _nonNetPrefabCount = -1;

    internal static string Resolve(string? requiredKey)
    {
        string canonicalKey = (requiredKey ?? string.Empty).Trim();
        if (canonicalKey.Length == 0)
        {
            return string.Empty;
        }

        lock (Sync)
        {
            string language = RequirementTranslations.GetSelectedLanguage();
            string cacheKey = language + "\0" + ValheimNameUtils.NormalizeKey(canonicalKey);
            try
            {
                EnsureDefeatSources();
                if (ResolvedText.TryGetValue(cacheKey, out string cached))
                {
                    return cached;
                }

                string resolved = ResolveUncached(canonicalKey);
                ResolvedText[cacheKey] = resolved;
                return resolved;
            }
            catch (Exception ex)
            {
                if (ReportedFailures.Add(cacheKey))
                {
                    YouAreNotWorthyPlugin.Log.LogWarning(
                        $"Could not build requirement text for key '{canonicalKey}': {ex.Message}");
                }

                return RequirementTranslations.FormatObtain(canonicalKey);
            }
        }
    }

    internal static void InvalidateSources()
    {
        lock (Sync)
        {
            _sourcesDirty = true;
            ResolvedText.Clear();
            ReportedFailures.Clear();
        }
    }

    internal static void InvalidateLocalization()
    {
        lock (Sync)
        {
            ResolvedText.Clear();
            ReportedFailures.Clear();
            EnglishNamesByToken.Clear();
        }
    }

    private static string ResolveUncached(string requiredKey)
    {
        if (HildirChestPrefabs.TryGetValue(requiredKey, out string chestPrefab))
        {
            return RequirementTranslations.FormatFind(
                ResolvePrefabName(chestPrefab, itemPrefab: true));
        }

        SortedSet<string> prefabs = new(StringComparer.OrdinalIgnoreCase);
        string normalizedKey = ValheimNameUtils.NormalizeKey(requiredKey);

        if (DefeatPrefabsByKey.TryGetValue(normalizedKey, out List<string> defeatPrefabs))
        {
            prefabs.UnionWith(defeatPrefabs);
        }

        if (ProgressionIndex.TryGetDefeatPrefabs(requiredKey, out IReadOnlyList<string> configuredPrefabs))
        {
            prefabs.UnionWith(configuredPrefabs);
        }

        if (prefabs.Count == 0)
        {
            return RequirementTranslations.FormatObtain(requiredKey);
        }

        List<string> names = prefabs
            .Select(static prefab => ResolvePrefabName(prefab, itemPrefab: false))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return names.Count == 1
            ? RequirementTranslations.FormatDefeat(names[0])
            : RequirementTranslations.FormatDefeatEither(string.Join(", ", names));
    }

    private static void EnsureDefeatSources()
    {
        ZNetScene? scene = ZNetScene.instance;
        int sceneId = (Object?)scene == null ? 0 : ((Object)scene).GetInstanceID();
        int netPrefabCount = scene?.m_prefabs?.Count ?? -1;
        int nonNetPrefabCount = scene?.m_nonNetViewPrefabs?.Count ?? -1;
        if (!_sourcesDirty
            && sceneId == _sceneInstanceId
            && netPrefabCount == _netPrefabCount
            && nonNetPrefabCount == _nonNetPrefabCount)
        {
            return;
        }

        ResolvedText.Clear();
        DefeatPrefabsByKey.Clear();
        CharacterNameTokensByPrefab.Clear();
        _sourcesDirty = true;
        if ((Object?)scene == null)
        {
            ReportedFailures.Clear();
            _sourcesDirty = false;
            _sceneInstanceId = sceneId;
            _netPrefabCount = netPrefabCount;
            _nonNetPrefabCount = nonNetPrefabCount;
            return;
        }

        foreach (GameObject prefab in ValheimNameUtils.EnumerateRegisteredPrefabs(scene!))
        {
            Character character = prefab.GetComponent<Character>();
            if ((Object?)character == null)
            {
                continue;
            }

            string prefabName = ValheimNameUtils.GetPrefabName(prefab);
            if (prefabName.Length == 0)
            {
                continue;
            }

            CharacterNameTokensByPrefab[ValheimNameUtils.NormalizePrefabName(prefabName)] =
                character.m_name ?? string.Empty;
            if (!TryGetBooleanKey(character.m_defeatSetGlobalKey, out string defeatKey))
            {
                continue;
            }

            string normalizedKey = ValheimNameUtils.NormalizeKey(defeatKey);
            if (!DefeatPrefabsByKey.TryGetValue(normalizedKey, out List<string> prefabs))
            {
                prefabs = new List<string>();
                DefeatPrefabsByKey[normalizedKey] = prefabs;
            }

            if (!prefabs.Contains(prefabName, StringComparer.OrdinalIgnoreCase))
            {
                prefabs.Add(prefabName);
            }
        }

        ReportedFailures.Clear();
        _sourcesDirty = false;
        _sceneInstanceId = sceneId;
        _netPrefabCount = netPrefabCount;
        _nonNetPrefabCount = nonNetPrefabCount;
    }

    private static bool TryGetBooleanKey(string? rawKey, out string key)
    {
        key = (rawKey ?? string.Empty).Trim();
        if (key.Length == 0)
        {
            return false;
        }

        key = ZoneSystem.GetKeyValue(key, out string value, out _);
        return key.Length > 0 && string.IsNullOrEmpty(value);
    }

    private static string ResolvePrefabName(string prefabName, bool itemPrefab)
    {
        if (!itemPrefab
            && CharacterNameTokensByPrefab.TryGetValue(
                ValheimNameUtils.NormalizePrefabName(prefabName),
                out string registeredNameToken))
        {
            return ResolveNameToken(registeredNameToken, prefabName);
        }

        GameObject? prefab = null;
        if (itemPrefab && (Object?)ObjectDB.instance != null)
        {
            prefab = ObjectDB.instance.GetItemPrefab(prefabName);
        }

        if ((Object?)prefab == null && (Object?)ZNetScene.instance != null)
        {
            prefab = ZNetScene.instance.GetPrefab(prefabName);
        }

        string nameToken = string.Empty;
        if ((Object?)prefab != null)
        {
            if (itemPrefab)
            {
                ItemDrop itemDrop = prefab.GetComponent<ItemDrop>();
                nameToken = itemDrop?.m_itemData?.m_shared?.m_name ?? string.Empty;
            }
            else
            {
                Character character = prefab.GetComponent<Character>();
                nameToken = character?.m_name ?? string.Empty;
            }
        }

        return ResolveNameToken(nameToken, prefabName);
    }

    private static string ResolveNameToken(string? nameToken, string prefabFallback)
    {
        string token = (nameToken ?? string.Empty).Trim();
        if (token.Length == 0)
        {
            return prefabFallback;
        }

        Localization localization = Localization.instance;
        string localized = localization.Localize(token).Trim();
        if (!IsMissingTranslation(token, localized))
        {
            return localized;
        }

        if (token[0] == '$')
        {
            if (!EnglishNamesByToken.TryGetValue(token, out string english))
            {
                try
                {
                    english = localization.TranslateSingleId(token, "English").Trim();
                }
                catch
                {
                    english = string.Empty;
                }

                EnglishNamesByToken[token] = english;
            }

            if (!IsMissingTranslation(token, english))
            {
                return english;
            }
        }

        return prefabFallback;
    }

    private static bool IsMissingTranslation(string token, string localized)
    {
        if (string.IsNullOrWhiteSpace(localized)
            || localized.IndexOf("MISSING KEY", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        return token[0] == '$'
               && (string.Equals(localized, token, StringComparison.Ordinal)
                   || string.Equals(
                       localized,
                       "[" + token.Substring(1) + "]",
                       StringComparison.Ordinal));
    }

}

[HarmonyPatch(typeof(Localization), nameof(Localization.SetupLanguage), typeof(string))]
internal static class RequirementTextLocalizationLifecyclePatch
{
    private static void Postfix()
    {
        RequirementTranslations.ReloadForSelectedLanguage();
        RequirementTextResolver.InvalidateLocalization();
    }
}
