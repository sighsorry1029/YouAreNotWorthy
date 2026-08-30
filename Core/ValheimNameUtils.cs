using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YouAreNotWorthy;

internal static class ValheimNameUtils
{
    internal static string NormalizeKey(string? key)
    {
        return (key ?? string.Empty).Trim().ToLowerInvariant();
    }

    internal static string NormalizeResourceName(string? name)
    {
        string value = (name ?? string.Empty).Trim();
        if (value.EndsWith("(Clone)", StringComparison.Ordinal))
        {
            value = value.Substring(0, value.Length - "(Clone)".Length);
        }

        if (value.StartsWith("$item_", StringComparison.OrdinalIgnoreCase))
        {
            value = value.Substring("$item_".Length);
        }
        else if (value.StartsWith("$", StringComparison.Ordinal))
        {
            value = value.Substring(1);
        }

        char[] normalized = new char[value.Length];
        int length = 0;
        foreach (char character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                normalized[length++] = char.ToLowerInvariant(character);
            }
        }

        return new string(normalized, 0, length);
    }

    internal static string NormalizePrefabName(string? name)
    {
        string value = (name ?? string.Empty).Trim();
        if (value.EndsWith("(Clone)", StringComparison.Ordinal))
        {
            value = value.Substring(0, value.Length - "(Clone)".Length);
        }

        return value.ToLowerInvariant();
    }

    internal static string GetPrefabName(GameObject? go)
    {
        if ((Object?)go == null)
        {
            return string.Empty;
        }

        try
        {
            ZNetView view = go.GetComponent<ZNetView>();
            if ((Object?)view != null && view.GetZDO() != null)
            {
                int prefabHash = view.GetZDO().GetPrefab();
                if (prefabHash != 0 && (Object?)ZNetScene.instance != null)
                {
                    GameObject prefab = ZNetScene.instance.GetPrefab(prefabHash);
                    if ((Object?)prefab != null)
                    {
                        return ((Object)prefab).name;
                    }
                }
            }
        }
        catch
        {
        }

        return Utils.GetPrefabName(((Object)go).name);
    }

    internal static string GetItemPrefabName(ItemDrop.ItemData? item)
    {
        if (item == null)
        {
            return string.Empty;
        }

        if ((Object?)item.m_dropPrefab != null)
        {
            return Utils.GetPrefabName(((Object)item.m_dropPrefab).name);
        }

        return item.m_shared?.m_name ?? string.Empty;
    }

    internal static IEnumerable<string> EnumerateItemResourceNames(ItemDrop.ItemData? item)
    {
        if (item == null)
        {
            yield break;
        }

        string prefabName = GetItemPrefabName(item);
        if (!string.IsNullOrWhiteSpace(prefabName))
        {
            yield return prefabName;
        }

        string sharedName = item.m_shared?.m_name ?? "";
        if (!string.IsNullOrWhiteSpace(sharedName)
            && !string.Equals(sharedName, prefabName, StringComparison.OrdinalIgnoreCase))
        {
            yield return sharedName;
        }
    }

    internal static IEnumerable<GameObject> EnumerateRegisteredPrefabs(ZNetScene scene)
    {
        HashSet<int> seen = new();
        foreach (GameObject prefab in scene.m_prefabs ?? Enumerable.Empty<GameObject>())
        {
            if ((Object?)prefab != null && seen.Add(prefab.GetInstanceID()))
            {
                yield return prefab;
            }
        }

        foreach (GameObject prefab in scene.m_nonNetViewPrefabs ?? Enumerable.Empty<GameObject>())
        {
            if ((Object?)prefab != null && seen.Add(prefab.GetInstanceID()))
            {
                yield return prefab;
            }
        }

        foreach (string prefabName in scene.GetPrefabNames() ?? Enumerable.Empty<string>())
        {
            GameObject prefab = scene.GetPrefab(prefabName);
            if ((Object?)prefab != null && seen.Add(prefab.GetInstanceID()))
            {
                yield return prefab;
            }
        }
    }
}
