using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;
using SharedData = ItemDrop.ItemData.SharedData;

namespace YouAreNotWorthy;

internal readonly struct GuardedItemReference
{
    internal GuardedItemReference(
        string prefabName,
        ItemDrop.ItemData.ItemType itemType,
        string requiredKey)
    {
        PrefabName = prefabName;
        ItemType = itemType;
        RequiredKey = requiredKey;
    }

    internal string PrefabName { get; }
    internal ItemDrop.ItemData.ItemType ItemType { get; }
    internal string RequiredKey { get; }
}

internal static class RestrictionEvaluator
{
    // Snapshot of VNEI 0.17.5's embedded ItemBlacklist.json. This is used only
    // to keep the generated item reference focused; YNW does not depend on VNEI.
    private static readonly HashSet<string> VneiDefaultBlacklistedPrefabs =
        new(StringComparer.Ordinal)
        {
            "Rock_destructible_test",
            "Player",
            "TrainingDummy",
            "HealthUpgrade_Bonemass",
            "HealthUpgrade_GDKing",
            "StaminaUpgrade_Greydwarf",
            "StaminaUpgrade_Troll",
            "StaminaUpgrade_Wraith",
            "IronOre",
            "MineRock_Iron",
            "Pickable_BogIronOre",
            "Trailership",
            "FirTree_oldLog",
            "TrophyDraugrFem",
            "CapeTest",
            "DvergerArbalest",
            "DvergerArbalest_shoot",
            "DvergerStaffSupport",
            "DvergerTest",
            "EvilHeart_Forest",
            "EvilHeart_Swamp",
            "TorchMist",
            "FishAnglerRaw",
            "fallenvalkyrie_screech",
            "fallenvalkyrie_swoopattack",
            "fallenvalkyrie_wingspin"
        };

    private sealed class ResolverState
    {
        internal ObjectDB? ObjectDb;
        internal ZNetScene? NetScene;
        internal int ItemCount;
        internal int RecipeCount;
        internal int PrefabCount;
        internal int NonNetPrefabCount;
        internal Dictionary<SharedData, CompiledItemTier> DirectTiers { get; } = new();
        internal Dictionary<SharedData, List<SharedData[]>> InputPaths { get; } = new();
        internal Dictionary<SharedData, CompiledItemTier?> ResolvedTiers { get; } = new();
        internal HashSet<SharedData> GuardedInteractionItems { get; } = new();
        internal HashSet<string> ReferenceInteractionItemNames { get; } =
            new(StringComparer.Ordinal);
    }

    private static ResolverState? _resolver;
    private static float _nextResolverRetryTime;
    private static bool _resolverFailureLogged;

    internal static void InvalidateItemCache()
    {
        _resolver = null;
        _nextResolverRetryTime = 0f;
        _resolverFailureLogged = false;
    }

    internal static bool TryGetMissingRequirement(
        Player? player,
        ItemDrop.ItemData? item,
        out CompiledItemTier requirement,
        bool forceItemUse = false)
    {
        requirement = null!;
        if ((Object?)player == null
            || (Object?)Player.m_localPlayer == null
            || (Object)player != (Object)Player.m_localPlayer
            || item?.m_shared == null
            || IsAdminDebugBypass())
        {
            return false;
        }

        if (!forceItemUse && !IsGuardedItem(item))
        {
            return false;
        }

        if (!TryResolveTier(item, out CompiledItemTier tier)
            || string.IsNullOrEmpty(tier.RequiredKey)
            || PlayerKeys.HasNativeKey(player, tier.RequiredKey))
        {
            return false;
        }

        requirement = tier;
        return true;
    }

    internal static bool IsGuardedItem(ItemDrop.ItemData? item)
    {
        return IsGuardedItem(item, EnsureResolver());
    }

    internal static bool TryCreateGuardedItemReferenceSnapshot(
        out List<GuardedItemReference> entries)
    {
        entries = new List<GuardedItemReference>();
        ResolverState? state = EnsureResolver();
        ObjectDB? objectDb = state?.ObjectDb;
        if (state == null || (Object?)objectDb == null || objectDb.m_items == null)
        {
            return false;
        }

        HashSet<string> seenPrefabs = new(StringComparer.OrdinalIgnoreCase);
        foreach (GameObject prefab in objectDb.m_items)
        {
            if ((Object?)prefab == null)
            {
                continue;
            }

            ItemDrop? itemDrop = prefab.GetComponent<ItemDrop>();
            ItemDrop.ItemData? item = itemDrop?.m_itemData;
            string prefabName = ValheimNameUtils.GetPrefabName(prefab);
            if (item?.m_shared == null
                || prefabName.Length == 0)
            {
                continue;
            }

            CompiledItemTier? direct = FindDirectTier(item, prefabName);
            bool directlyConfigured = direct != null;
            bool defaultBlacklisted = VneiDefaultBlacklistedPrefabs.Contains(prefabName);
            bool allowWithoutInventoryIcon = defaultBlacklisted && directlyConfigured;
            if (!IsReferenceGuardCandidate(item, state, allowWithoutInventoryIcon)
                || (defaultBlacklisted && !directlyConfigured)
                || !seenPrefabs.Add(prefabName))
            {
                continue;
            }

            CompiledItemTier? resolved = ResolveEffectiveTier(item, state, direct);
            entries.Add(new GuardedItemReference(
                prefabName,
                item.m_shared.m_itemType,
                resolved?.RequiredKey ?? string.Empty));
        }

        return true;
    }

    internal static void RevalidateEquipment(Player? player)
    {
        if ((Object?)player == null
            || (Object?)Player.m_localPlayer == null
            || (Object)player != (Object)Player.m_localPlayer)
        {
            return;
        }

        List<ItemDrop.ItemData> items = new(player.GetInventory().GetAllItems());
        foreach (ItemDrop.ItemData item in items)
        {
            if (item == null
                || (!item.m_equipped && !((Humanoid)player).IsItemEquiped(item))
                || !TryGetMissingRequirement(player, item, out _, forceItemUse: true))
            {
                continue;
            }

            ((Humanoid)player).UnequipItem(item, triggerEquipEffects: false);
        }
    }

    private static bool IsAdminDebugBypass()
    {
        return YouAreNotWorthyPlugin.IsLocalAdmin
               && Player.m_debugMode;
    }

    private static bool IsGuardedItem(ItemDrop.ItemData? item, ResolverState? state)
    {
        if (item?.m_shared == null)
        {
            return false;
        }

        return item.IsEquipable()
               || item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.AmmoNonEquipable
               || item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Consumable
               || (state != null
                   && state.GuardedInteractionItems.Contains(GetResolverSharedData(item)));
    }

    private static bool IsReferenceGuardCandidate(
        ItemDrop.ItemData? item,
        ResolverState state,
        bool allowWithoutInventoryIcon)
    {
        if (item?.m_shared == null)
        {
            return false;
        }

        return state.ReferenceInteractionItemNames.Contains(item.m_shared.m_name)
               || ((allowWithoutInventoryIcon || HasPlayerInventoryIcon(item))
                   && IsGuardedItem(item, state));
    }

    private static bool HasPlayerInventoryIcon(ItemDrop.ItemData item)
    {
        return item.m_shared?.m_icons != null
               && item.m_shared.m_icons.Any(static icon => (Object?)icon != null);
    }

    private static bool TryResolveTier(ItemDrop.ItemData item, out CompiledItemTier tier)
    {
        CompiledItemTier? direct = FindDirectTier(item);
        CompiledItemTier? resolved = ResolveEffectiveTier(
            item,
            EnsureResolver(),
            direct);

        if (resolved == null)
        {
            tier = null!;
            return false;
        }

        tier = resolved;
        return true;
    }

    private static CompiledItemTier? ResolveEffectiveTier(
        ItemDrop.ItemData item,
        ResolverState? state,
        CompiledItemTier? direct)
    {
        return state == null
            ? direct
            : Higher(
                direct,
                ResolveTier(
                    state,
                    GetResolverSharedData(item),
                    new HashSet<SharedData>()));
    }

    private static SharedData GetResolverSharedData(ItemDrop.ItemData item)
    {
        return item.m_dropPrefab?
                   .GetComponent<ItemDrop>()?
                   .m_itemData?
                   .m_shared
               ?? item.m_shared;
    }

    private static ResolverState? EnsureResolver()
    {
        ObjectDB? objectDb = ObjectDB.instance;
        ZNetScene? netScene = ZNetScene.instance;
        int itemCount = objectDb?.m_items?.Count ?? -1;
        int recipeCount = objectDb?.m_recipes?.Count ?? -1;
        int prefabCount = netScene?.m_prefabs?.Count ?? -1;
        int nonNetPrefabCount = netScene?.m_nonNetViewPrefabs?.Count ?? -1;
        if (_resolver != null
            && (Object?)_resolver.ObjectDb == (Object?)objectDb
            && (Object?)_resolver.NetScene == (Object?)netScene
            && _resolver.ItemCount == itemCount
            && _resolver.RecipeCount == recipeCount
            && _resolver.PrefabCount == prefabCount
            && _resolver.NonNetPrefabCount == nonNetPrefabCount)
        {
            return _resolver;
        }

        if ((Object?)objectDb == null)
        {
            return null;
        }

        if (_resolver == null && Time.realtimeSinceStartup < _nextResolverRetryTime)
        {
            return null;
        }

        try
        {
            ResolverState candidate = BuildResolver(objectDb, netScene);
            _resolver = candidate;
            _nextResolverRetryTime = 0f;
            _resolverFailureLogged = false;
            return candidate;
        }
        catch (Exception ex)
        {
            if (!_resolverFailureLogged)
            {
                YouAreNotWorthyPlugin.Log.LogError($"Failed to build the item-tier resolver: {ex}");
                _resolverFailureLogged = true;
            }

            _resolver = null;
            _nextResolverRetryTime = Time.realtimeSinceStartup + 5f;
            return null;
        }
    }

    private static ResolverState BuildResolver(ObjectDB objectDb, ZNetScene? netScene)
    {
        ResolverState state = new()
        {
            ObjectDb = objectDb,
            NetScene = netScene,
            ItemCount = objectDb.m_items?.Count ?? -1,
            RecipeCount = objectDb.m_recipes?.Count ?? -1,
            PrefabCount = netScene?.m_prefabs?.Count ?? -1,
            NonNetPrefabCount = netScene?.m_nonNetViewPrefabs?.Count ?? -1
        };

        foreach (GameObject prefab in objectDb.m_items ?? new List<GameObject>())
        {
            ItemDrop? itemDrop = prefab?.GetComponent<ItemDrop>();
            RegisterItem(state, itemDrop);
            RegisterFeastInputPath(state, prefab, itemDrop);
        }

        foreach (Recipe recipe in objectDb.m_recipes ?? new List<Recipe>())
        {
            if ((Object?)recipe == null || (Object?)recipe.m_item == null)
            {
                continue;
            }

            List<ItemDrop> inputs = new();
            foreach (Piece.Requirement requirement in recipe.m_resources ?? Array.Empty<Piece.Requirement>())
            {
                if (requirement != null
                    && requirement.m_amount > 0
                    && (Object?)requirement.m_resItem != null)
                {
                    inputs.Add(requirement.m_resItem);
                }
            }

            if (recipe.m_requireOnlyOneIngredient)
            {
                foreach (ItemDrop input in inputs)
                {
                    AddInputPath(state, recipe.m_item, input);
                }
            }
            else
            {
                AddInputPath(state, recipe.m_item, inputs);
            }
        }

        if ((Object?)netScene != null)
        {
            List<OfferingBowl> itemStandBowls = new();
            List<ItemStand> itemStands = new();
            foreach (GameObject prefab in ValheimNameUtils.EnumerateRegisteredPrefabs(netScene))
            {
                if ((Object?)prefab == null)
                {
                    continue;
                }

                foreach (CookingStation station in prefab.GetComponentsInChildren<CookingStation>(true))
                {
                    RegisterConversions(state, station);
                }

                foreach (Fermenter fermenter in prefab.GetComponentsInChildren<Fermenter>(true))
                {
                    RegisterConversions(state, fermenter);
                }

                foreach (Smelter smelter in prefab.GetComponentsInChildren<Smelter>(true))
                {
                    RegisterConversions(state, smelter);
                }

                foreach (OfferingBowl bowl in prefab.GetComponentsInChildren<OfferingBowl>(true))
                {
                    if ((Object?)bowl == null)
                    {
                        continue;
                    }

                    if (bowl.m_useItemStands)
                    {
                        itemStandBowls.Add(bowl);
                        RegisterReferenceOnlyInteractionItem(state, bowl.m_bossItem);
                    }
                    else if ((Object?)bowl.m_bossItem != null)
                    {
                        RegisterGuardedInteractionItem(state, bowl.m_bossItem);
                    }
                }

                itemStands.AddRange(prefab.GetComponentsInChildren<ItemStand>(true));

                foreach (Door door in prefab.GetComponentsInChildren<Door>(true))
                {
                    if ((Object?)door != null && (Object?)door.m_keyItem != null)
                    {
                        RegisterGuardedInteractionItem(state, door.m_keyItem);
                    }
                }
            }

            RegisterItemStandOfferingItems(state, objectDb, itemStandBowls, itemStands);
        }

        return state;
    }

    private static void RegisterFeastInputPath(
        ResolverState state,
        GameObject? prefab,
        ItemDrop? rootItem)
    {
        if ((Object?)prefab == null)
        {
            return;
        }

        Feast? feast = prefab.GetComponent<Feast>();
        Piece? piece = prefab.GetComponent<Piece>();
        if ((Object?)feast == null || (Object?)piece == null)
        {
            return;
        }

        ItemDrop? foodItem = (Object?)feast.m_foodItem != null
            ? feast.m_foodItem
            : rootItem;
        IEnumerable<ItemDrop> placementResources =
            (piece.m_resources ?? Array.Empty<Piece.Requirement>())
            .Where(static requirement =>
                requirement != null
                && requirement.m_amount > 0
                && (Object?)requirement.m_resItem != null)
            .Select(static requirement => requirement.m_resItem);

        AddInputPath(state, foodItem, placementResources);
    }

    private static void RegisterGuardedInteractionItem(
        ResolverState state,
        ItemDrop? itemDrop)
    {
        if ((Object?)itemDrop == null || itemDrop.m_itemData?.m_shared == null)
        {
            return;
        }

        RegisterItem(state, itemDrop);
        state.GuardedInteractionItems.Add(itemDrop.m_itemData.m_shared);
        state.ReferenceInteractionItemNames.Add(itemDrop.m_itemData.m_shared.m_name);
    }

    private static void RegisterReferenceOnlyInteractionItem(
        ResolverState state,
        ItemDrop? itemDrop)
    {
        if ((Object?)itemDrop == null || itemDrop.m_itemData?.m_shared == null)
        {
            return;
        }

        RegisterItem(state, itemDrop);
        state.ReferenceInteractionItemNames.Add(itemDrop.m_itemData.m_shared.m_name);
    }

    private static void RegisterItemStandOfferingItems(
        ResolverState state,
        ObjectDB objectDb,
        IEnumerable<OfferingBowl> bowls,
        IEnumerable<ItemStand> itemStands)
    {
        string[] prefixes = bowls
            .Select(static bowl => bowl.m_itemStandPrefix ?? string.Empty)
            .Where(static prefix => prefix.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (prefixes.Length == 0)
        {
            return;
        }

        foreach (ItemStand itemStand in itemStands)
        {
            if ((Object?)itemStand == null
                || !prefixes.Any(prefix => ((Object)itemStand.gameObject).name.StartsWith(
                    prefix,
                    StringComparison.Ordinal)))
            {
                continue;
            }

            HashSet<string> unsupportedItemNames = new(
                (itemStand.m_unsupportedItems ?? new List<ItemDrop>())
                .Where(static item => (Object?)item != null && item.m_itemData?.m_shared != null)
                .Select(static item => item.m_itemData.m_shared.m_name),
                StringComparer.Ordinal);
            foreach (ItemDrop supportedItem in itemStand.m_supportedItems ?? new List<ItemDrop>())
            {
                if ((Object?)supportedItem == null
                    || supportedItem.m_itemData?.m_shared == null
                    || unsupportedItemNames.Contains(supportedItem.m_itemData.m_shared.m_name))
                {
                    continue;
                }

                RegisterReferenceOnlyInteractionItem(state, supportedItem);
            }

            HashSet<ItemDrop.ItemData.ItemType> supportedTypes = new(
                itemStand.m_supportedTypes ?? new List<ItemDrop.ItemData.ItemType>());
            if (supportedTypes.Count == 0)
            {
                continue;
            }

            foreach (GameObject itemPrefab in objectDb.m_items ?? new List<GameObject>())
            {
                ItemDrop? itemDrop = itemPrefab?.GetComponent<ItemDrop>();
                if ((Object?)itemDrop == null
                    || itemDrop.m_itemData?.m_shared == null)
                {
                    continue;
                }

                GameObject dropPrefab = (Object?)itemDrop.m_itemData.m_dropPrefab != null
                    ? itemDrop.m_itemData.m_dropPrefab!
                    : itemPrefab!;
                if (!HasPlayerInventoryIcon(itemDrop.m_itemData)
                    || (Object?)ItemStand.GetAttachPrefab(dropPrefab) == null
                    || !supportedTypes.Contains(itemDrop.m_itemData.m_shared.m_itemType)
                    || unsupportedItemNames.Contains(itemDrop.m_itemData.m_shared.m_name))
                {
                    continue;
                }

                RegisterReferenceOnlyInteractionItem(state, itemDrop);
            }
        }
    }

    private static void RegisterConversions(ResolverState state, CookingStation? station)
    {
        if ((Object?)station == null)
        {
            return;
        }

        if (station.m_conversion == null)
        {
            return;
        }

        foreach (CookingStation.ItemConversion conversion in station.m_conversion)
        {
            AddInputPath(state, conversion?.m_to, conversion?.m_from);
        }
    }

    private static void RegisterConversions(ResolverState state, Fermenter? fermenter)
    {
        if ((Object?)fermenter == null)
        {
            return;
        }

        if (fermenter.m_conversion == null)
        {
            return;
        }

        foreach (Fermenter.ItemConversion conversion in fermenter.m_conversion)
        {
            AddInputPath(state, conversion?.m_to, conversion?.m_from);
        }
    }

    private static void RegisterConversions(ResolverState state, Smelter? smelter)
    {
        if ((Object?)smelter == null)
        {
            return;
        }

        if (smelter.m_conversion == null)
        {
            return;
        }

        foreach (Smelter.ItemConversion conversion in smelter.m_conversion)
        {
            AddInputPath(state, conversion?.m_to, conversion?.m_from);
        }
    }

    private static void AddInputPath(ResolverState state, ItemDrop? output, ItemDrop? input)
    {
        if ((Object?)input != null)
        {
            AddInputPath(state, output, new[] { input });
        }
    }

    private static void AddInputPath(
        ResolverState state,
        ItemDrop? output,
        IEnumerable<ItemDrop> inputs)
    {
        if ((Object?)output == null || output.m_itemData?.m_shared == null)
        {
            return;
        }

        RegisterItem(state, output);
        List<SharedData> inputData = new();
        foreach (ItemDrop input in inputs)
        {
            if ((Object?)input == null || input.m_itemData?.m_shared == null)
            {
                continue;
            }

            RegisterItem(state, input);
            if (!inputData.Contains(input.m_itemData.m_shared))
            {
                inputData.Add(input.m_itemData.m_shared);
            }
        }

        if (inputData.Count == 0)
        {
            return;
        }

        SharedData outputData = output.m_itemData.m_shared;
        if (!state.InputPaths.TryGetValue(outputData, out List<SharedData[]> paths))
        {
            paths = new List<SharedData[]>();
            state.InputPaths[outputData] = paths;
        }

        SharedData[] inputPath = inputData.ToArray();
        foreach (SharedData[] existingPath in paths)
        {
            if (existingPath.Length != inputPath.Length)
            {
                continue;
            }

            bool exactMatch = true;
            for (int index = 0; index < inputPath.Length; index++)
            {
                if (ReferenceEquals(existingPath[index], inputPath[index]))
                {
                    continue;
                }

                exactMatch = false;
                break;
            }

            if (exactMatch)
            {
                return;
            }
        }

        paths.Add(inputPath);
    }

    private static void RegisterItem(ResolverState state, ItemDrop? itemDrop)
    {
        if ((Object?)itemDrop == null || itemDrop.m_itemData?.m_shared == null)
        {
            return;
        }

        CompiledItemTier? direct = FindDirectTier(
            itemDrop.m_itemData,
            ((Object)((Component)itemDrop).gameObject).name);
        if (direct == null)
        {
            return;
        }

        SharedData shared = itemDrop.m_itemData.m_shared;
        if (!state.DirectTiers.TryGetValue(shared, out CompiledItemTier existing)
            || direct.Rank > existing.Rank)
        {
            state.DirectTiers[shared] = direct;
        }
    }

    private static CompiledItemTier? ResolveTier(
        ResolverState state,
        SharedData shared,
        HashSet<SharedData> visiting)
    {
        if (state.ResolvedTiers.TryGetValue(shared, out CompiledItemTier? cached))
        {
            return cached;
        }

        state.DirectTiers.TryGetValue(shared, out CompiledItemTier direct);
        if (!visiting.Add(shared))
        {
            return direct;
        }

        CompiledItemTier? easiestPath = null;
        if (state.InputPaths.TryGetValue(shared, out List<SharedData[]> paths))
        {
            foreach (SharedData[] path in paths)
            {
                CompiledItemTier? pathTier = null;
                foreach (SharedData input in path)
                {
                    pathTier = Higher(pathTier, ResolveTier(state, input, visiting));
                }

                if (pathTier == null)
                {
                    // An unclassified production path is less restrictive than every ranked path.
                    easiestPath = null;
                    break;
                }

                if (easiestPath == null || pathTier.Rank < easiestPath.Rank)
                {
                    easiestPath = pathTier;
                }
            }
        }

        visiting.Remove(shared);
        CompiledItemTier? resolved = Higher(direct, easiestPath);
        state.ResolvedTiers[shared] = resolved;
        return resolved;
    }

    private static CompiledItemTier? FindDirectTier(
        ItemDrop.ItemData item,
        string? explicitPrefabName = null)
    {
        CompiledItemTier? direct = null;
        if (!string.IsNullOrWhiteSpace(explicitPrefabName)
            && ProgressionIndex.TryGetItemTier(explicitPrefabName, out CompiledItemTier explicitTier))
        {
            direct = explicitTier;
        }

        foreach (string name in ValheimNameUtils.EnumerateItemResourceNames(item))
        {
            if (ProgressionIndex.TryGetItemTier(name, out CompiledItemTier candidate))
            {
                direct = Higher(direct, candidate);
            }
        }

        return direct;
    }

    private static CompiledItemTier? Higher(CompiledItemTier? left, CompiledItemTier? right)
    {
        if (left == null)
        {
            return right;
        }

        return right != null && right.Rank > left.Rank ? right : left;
    }
}
