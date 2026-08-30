using System.Collections.Generic;

namespace YouAreNotWorthy;

internal sealed class ProgressionConfig
{
    internal List<DefeatKeyRule> DefeatKeys { get; } = new();
    internal List<ItemTierConfig> Tiers { get; } = new();
}

internal sealed class DefeatKeyRule
{
    internal List<string> Prefabs { get; } = new();
    internal string Key { get; set; } = "";
}

internal sealed class ItemTierConfig
{
    internal ItemTierConfig(string id, string requiredKey, List<string> resources)
    {
        Id = id;
        RequiredKey = requiredKey;
        Resources = resources;
    }

    internal string Id { get; }
    internal string RequiredKey { get; }
    internal List<string> Resources { get; }
}
