using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using YamlDotNet.Serialization;

namespace YouAreNotWorthy;

internal enum ItemReferenceWriteResult
{
    Updated,
    Unchanged,
    NotReady,
    ShuttingDown
}

internal enum ItemReferenceRefreshResult
{
    Updated,
    Unchanged,
    NotAuthoritative,
    RuntimeNotReady,
    ShuttingDown,
    Failed
}

internal static class ItemReferenceWriter
{
    internal const string ReferenceFileName = "items.reference.yml";
    internal static string ReferencePath => Path.Combine(
        ProgressionConfigLoader.ConfigDirectory,
        ReferenceFileName);

    private const float ExistenceCheckSeconds = 5f;
    private const float NotReadyRetrySeconds = 1f;
    private const float FailureRetrySeconds = 5f;
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly ISerializer EntrySerializer = new SerializerBuilder().Build();
    private static readonly object Sync = new();
    private static readonly Dictionary<string, int> ItemTypeSortOrder = new(StringComparer.Ordinal)
    {
        ["OneHandedWeapon"] = 0,
        ["TwoHandedWeapon"] = 1,
        ["TwoHandedWeaponLeft"] = 2,
        ["Bow"] = 3,
        ["Shield"] = 4,
        ["Torch"] = 5,
        ["Tool"] = 6,
        ["Attach_Atgeir"] = 7,
        ["Helmet"] = 10,
        ["Chest"] = 11,
        ["Legs"] = 12,
        ["Shoulder"] = 13,
        ["Hands"] = 14,
        ["Utility"] = 15,
        ["Trinket"] = 16,
        ["Consumable"] = 20,
        ["Fish"] = 21,
        ["Ammo"] = 30,
        ["AmmoNonEquipable"] = 31,
        ["Material"] = 40,
        ["Trophy"] = 41,
        ["Misc"] = 50,
        ["None"] = 51
    };

    private static bool _dirty = true;
    private static bool _shutdown;
    private static long _revision = 1;
    private static float _nextExistenceCheck;
    private static float _retryAfter;

    internal static void Reset()
    {
        PrefabOwnerResolver.Invalidate();
        lock (Sync)
        {
            _shutdown = false;
            _dirty = true;
            _revision++;
            _nextExistenceCheck = 0f;
            _retryAfter = 0f;
        }
    }

    internal static void MarkDirty(bool ownerSourcesChanged = false)
    {
        if (ownerSourcesChanged)
        {
            PrefabOwnerResolver.Invalidate();
        }

        lock (Sync)
        {
            if (_shutdown)
            {
                return;
            }

            _dirty = true;
            _revision++;
            _nextExistenceCheck = 0f;
            _retryAfter = 0f;
        }
    }

    internal static void Shutdown()
    {
        lock (Sync)
        {
            _shutdown = true;
            _dirty = false;
            _revision++;
            _nextExistenceCheck = 0f;
            _retryAfter = 0f;
        }
    }

    private static bool NeedsUpdate()
    {
        lock (Sync)
        {
            if (_shutdown)
            {
                return false;
            }

            if (_dirty)
            {
                return true;
            }

            float now = Time.realtimeSinceStartup;
            if (now < _nextExistenceCheck)
            {
                return false;
            }

            _nextExistenceCheck = now + ExistenceCheckSeconds;
            if (File.Exists(ReferencePath))
            {
                return false;
            }

            _dirty = true;
            _revision++;
            return true;
        }
    }

    internal static void TryUpdate(bool isSourceOfTruth)
    {
        if (!isSourceOfTruth
            || (UnityEngine.Object?)ZNet.instance == null
            || !ZNet.instance.IsServer())
        {
            return;
        }

        float now = Time.realtimeSinceStartup;
        lock (Sync)
        {
            if (_shutdown || now < _retryAfter)
            {
                return;
            }
        }

        if (!NeedsUpdate())
        {
            return;
        }

        try
        {
            ItemReferenceWriteResult result = TryWriteIfNeededWithResult();
            lock (Sync)
            {
                if (!_shutdown)
                {
                    _retryAfter = result == ItemReferenceWriteResult.NotReady
                        ? now + NotReadyRetrySeconds
                        : 0f;
                }
            }
        }
        catch (Exception ex)
        {
            ScheduleFailureRetry();
            YouAreNotWorthyPlugin.Log.LogWarning(
                $"Failed to update the item restriction reference: {ex.GetType().Name}: {ex.Message}. "
                + $"Retrying in {FailureRetrySeconds:0.#}s.");
        }
    }

    internal static ItemReferenceRefreshResult TryRefresh(bool isSourceOfTruth)
    {
        if (!isSourceOfTruth
            || (UnityEngine.Object?)ZNet.instance == null
            || !ZNet.instance.IsServer())
        {
            return ItemReferenceRefreshResult.NotAuthoritative;
        }

        lock (Sync)
        {
            if (_shutdown)
            {
                return ItemReferenceRefreshResult.ShuttingDown;
            }
        }

        try
        {
            RestrictionEvaluator.InvalidateItemCache();
            MarkDirty(ownerSourcesChanged: true);
            ItemReferenceWriteResult result = TryWriteIfNeededWithResult();
            lock (Sync)
            {
                if (!_shutdown)
                {
                    _retryAfter = result == ItemReferenceWriteResult.NotReady
                        ? Time.realtimeSinceStartup + NotReadyRetrySeconds
                        : 0f;
                }
            }

            return result switch
            {
                ItemReferenceWriteResult.Updated => ItemReferenceRefreshResult.Updated,
                ItemReferenceWriteResult.Unchanged => ItemReferenceRefreshResult.Unchanged,
                ItemReferenceWriteResult.NotReady => ItemReferenceRefreshResult.RuntimeNotReady,
                ItemReferenceWriteResult.ShuttingDown => ItemReferenceRefreshResult.ShuttingDown,
                _ => ItemReferenceRefreshResult.Failed
            };
        }
        catch (Exception ex)
        {
            ScheduleFailureRetry();
            YouAreNotWorthyPlugin.Log.LogWarning(
                $"Failed to refresh the item restriction reference: {ex.GetType().Name}: {ex.Message}. "
                + $"Retrying automatically in {FailureRetrySeconds:0.#}s.");
            return ItemReferenceRefreshResult.Failed;
        }
    }

    private static ItemReferenceWriteResult TryWriteIfNeededWithResult()
    {
        long revision;
        lock (Sync)
        {
            if (_shutdown)
            {
                return ItemReferenceWriteResult.ShuttingDown;
            }

            if (!_dirty && File.Exists(ReferencePath))
            {
                return ItemReferenceWriteResult.Unchanged;
            }

            revision = _revision;
        }

        if (!RestrictionEvaluator.TryCreateGuardedItemReferenceSnapshot(
                out List<GuardedItemReference> entries))
        {
            return ItemReferenceWriteResult.NotReady;
        }

        string content = BuildContent(entries);
        bool changed = WriteIfChanged(content);
        lock (Sync)
        {
            _nextExistenceCheck = Time.realtimeSinceStartup + ExistenceCheckSeconds;
            if (!_shutdown && _revision == revision)
            {
                _dirty = false;
            }
        }

        return changed
            ? ItemReferenceWriteResult.Updated
            : ItemReferenceWriteResult.Unchanged;
    }

    private static void ScheduleFailureRetry()
    {
        lock (Sync)
        {
            if (_shutdown)
            {
                return;
            }

            _dirty = true;
            _retryAfter = Time.realtimeSinceStartup + FailureRetrySeconds;
        }
    }

    private static string BuildContent(IReadOnlyCollection<GuardedItemReference> references)
    {
        IReadOnlyDictionary<string, string> owners = PrefabOwnerResolver.Resolve(
            references.Select(static entry => entry.PrefabName));
        List<OutputItem> items = references
            .Select(entry => new OutputItem(
                entry,
                owners.TryGetValue(
                    ValheimNameUtils.NormalizePrefabName(entry.PrefabName),
                    out string owner)
                    ? owner
                    : PrefabOwnerResolver.UnknownOwnerName))
            .ToList();

        StringBuilder builder = new();
        builder.Append(BuildHeader());
        foreach (IGrouping<string, OutputItem> ownerGroup in items
                     .OrderBy(static item => PrefabOwnerResolver.GetOwnerSortBucket(item.Owner))
                     .ThenBy(static item => item.Owner, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static item => item.Owner, StringComparer.Ordinal)
                     .GroupBy(static item => item.Owner, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append("# ===== ")
                .Append(PrefabOwnerResolver.NormalizeOwnerName(ownerGroup.Key))
                .AppendLine(" =====");

            foreach (IGrouping<string, OutputItem> itemTypeGroup in ownerGroup
                         .OrderBy(static item => GetItemTypeSortOrder(item.ItemType))
                         .ThenBy(static item => item.ItemType, StringComparer.Ordinal)
                         .ThenBy(static item => item.RequiredKey.Length == 0 ? 0 : 1)
                         .ThenBy(static item => item.RequiredKeyRank)
                         .ThenBy(static item => item.RequiredKey, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(static item => item.RequiredKey, StringComparer.Ordinal)
                         .ThenBy(static item => item.PrefabName, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(static item => item.PrefabName, StringComparer.Ordinal)
                         .GroupBy(static item => item.ItemType, StringComparer.Ordinal))
            {
                builder.Append("# ----- ")
                    .Append(itemTypeGroup.Key)
                    .AppendLine(" -----");
                List<string> lines = itemTypeGroup
                    .Select(static item => item.RequiredKey.Length == 0
                        ? item.PrefabName
                        : item.PrefabName + ", " + item.RequiredKey)
                    .ToList();
                AppendYamlSequence(builder, lines);
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    private static int GetItemTypeSortOrder(string itemType)
    {
        return ItemTypeSortOrder.TryGetValue(itemType, out int order)
            ? order
            : int.MaxValue;
    }

    private static void AppendYamlSequence(StringBuilder builder, List<string> values)
    {
        string yaml = EntrySerializer.Serialize(values)
            .Replace("\r\n", "\n")
            .TrimEnd('\n');
        builder.AppendLine(yaml.Replace("\n", Environment.NewLine));
    }

    private static string BuildHeader()
    {
        string newline = Environment.NewLine;
        return string.Join(newline, new[]
        {
            "# YouAreNotWorthy item restriction reference",
            "# Generated from the effective loaded runtime. This file is overwritten automatically.",
            "# It is reference-only: YNW does not read it as configuration and does not ServerSync it.",
            "# Only the authoritative server/listen host writes it; remote clients leave local copies untouched.",
            "#",
            "# Only statically discoverable player-facing item-use targets evaluated by YNW are listed.",
            "# General equipment, consumables, and ammo require a valid inventory icon, following VNEI's",
            "# lightweight player-item signal. Explicit Door/OfferingBowl/ItemStand items remain even without one.",
            "# Prefabs in VNEI 0.17.5's default blacklist are omitted unless their item is directly listed",
            "# under a progression.yml tier's resources; production-path inheritance is not a direct listing.",
            "# '- Prefab' means the target currently has no effective required personal key.",
            "# '- Prefab, key' means that guarded use is blocked while the player lacks that personal key.",
            "# The key is the final effective result after direct assignment and production-path inheritance.",
            "# Tier-only resources whose pickup, storage, and crafting are not guarded are omitted.",
            "# Explicit item-stand boss-item/supported-item declarations are included without an icon;",
            "# supported-type declarations contribute only icon-backed items.",
            "# Broad allow-all stands and runtime-only stand changes may be absent",
            "# unless the item is independently discoverable as equipment, consumable, ammo, Door key, or direct offering.",
            "# Owner and ItemType headings are presentation-only. ItemType uses the loaded Valheim enum name.",
            "# Within each ItemType, keyless entries come first. Keyed entries follow the first tier where",
            "# their requiredKey appears in progression.yml, then prefab name; repeated keys stay together.",
            "# Owner inference is best-effort; ambiguous or runtime-created prefabs remain Unknown / Untracked.",
            "#"
        }) + newline;
    }

    private static bool WriteIfChanged(string content)
    {
        Directory.CreateDirectory(ProgressionConfigLoader.ConfigDirectory);
        string path = ReferencePath;
        string existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        if (string.Equals(existing, content, StringComparison.Ordinal))
        {
            return false;
        }

        WriteAtomically(path, content);
        YouAreNotWorthyPlugin.Log.LogInfo($"Updated item restriction reference at {path}.");
        return true;
    }

    private static void WriteAtomically(string path, string content)
    {
        string directory = Path.GetDirectoryName(path) ?? ProgressionConfigLoader.ConfigDirectory;
        string tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, content, Utf8WithoutBom);
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception)
            {
                // A stale generated temp file is safer than deleting an unrelated path.
            }
        }
    }

    private sealed class OutputItem
    {
        internal OutputItem(GuardedItemReference item, string owner)
        {
            PrefabName = item.PrefabName;
            ItemType = item.ItemType.ToString();
            RequiredKey = item.RequiredKey;
            RequiredKeyRank = item.RequiredKey.Length == 0
                ? -1
                : ProgressionIndex.TryGetItemTierKeyRank(item.RequiredKey, out int rank)
                    ? rank
                    : int.MaxValue;
            Owner = PrefabOwnerResolver.NormalizeOwnerName(owner);
        }

        internal string PrefabName { get; }
        internal string ItemType { get; }
        internal string RequiredKey { get; }
        internal int RequiredKeyRank { get; }
        internal string Owner { get; }
    }
}
