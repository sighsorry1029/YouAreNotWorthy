using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using YamlDotNet.RepresentationModel;

namespace YouAreNotWorthy;

internal static class ProgressionConfigLoader
{
    internal const string ConfigFileName = "progression.yml";
    internal const string SyncedYamlIdentifier = "ProgressionYaml";

    internal static string ConfigDirectory => Path.Combine(Paths.ConfigPath, YouAreNotWorthyPlugin.ModName);
    internal static string ConfigPath => Path.Combine(ConfigDirectory, ConfigFileName);

    private static bool IsConfigured { get; set; }
    internal static string AppliedYaml { get; private set; } = "";

    internal static bool LoadOrCreate()
    {
        if (TryReadLocal(out string yaml, createIfMissing: true)
            && TryApply(yaml, ConfigPath))
        {
            return true;
        }

        PreserveLastKnownGoodOrUseDefault();
        return IsConfigured;
    }

    internal static bool TryReadValidLocalYaml(out string yaml)
    {
        yaml = "";
        return TryReadLocal(out yaml)
               && TryParseAndValidate(yaml, ConfigPath, out _);
    }

    internal static bool ApplyLocalYaml(string yaml)
    {
        return TryApply(yaml, ConfigPath);
    }

    internal static bool ApplySyncedYaml(string yaml)
    {
        return TryApply(yaml, "ServerSync progression.yml");
    }

    private static bool TryReadLocal(out string yaml, bool createIfMissing = false)
    {
        yaml = "";
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            if (!File.Exists(ConfigPath))
            {
                if (!createIfMissing)
                {
                    YouAreNotWorthyPlugin.Log.LogWarning($"Configuration file not found: {ConfigPath}");
                    return false;
                }

                File.WriteAllText(ConfigPath, DefaultProgressionYaml);
            }

            yaml = File.ReadAllText(ConfigPath);
            return true;
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError($"Failed to read YNW YAML configuration: {ex}");
            return false;
        }
    }

    private static bool TryApply(string yaml, string source)
    {
        if (!TryParseAndValidate(yaml, source, out ProgressionConfig progression))
        {
            return false;
        }

        try
        {
            ProgressionIndex.Configure(progression);
            RestrictionEvaluator.InvalidateItemCache();
            RequirementTextResolver.InvalidateSources();
            ItemReferenceWriter.MarkDirty();
            AppliedYaml = yaml;
            IsConfigured = true;
            return true;
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError($"Failed to apply {source}: {ex}");
            return false;
        }
    }

    private static bool TryParseAndValidate(
        string yaml,
        string source,
        out ProgressionConfig progression)
    {
        progression = new ProgressionConfig();
        if (string.IsNullOrWhiteSpace(yaml))
        {
            YouAreNotWorthyPlugin.Log.LogError($"Invalid {source}: The YAML document is empty.");
            return false;
        }

        List<string> errors = new();
        try
        {
            YamlStream stream = new();
            using StringReader reader = new(yaml);
            stream.Load(reader);
            if (stream.Documents.Count != 1
                || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                YouAreNotWorthyPlugin.Log.LogError(
                    $"Invalid {source}: Expected one progression mapping document.");
                return false;
            }

            ParseRoot(root, progression, errors);
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError($"Failed to parse {source}: {ex}");
            return false;
        }

        if (errors.Count == 0)
        {
            Validate(progression, errors);
        }

        if (errors.Count == 0)
        {
            return true;
        }

        foreach (string error in errors)
        {
            YouAreNotWorthyPlugin.Log.LogError($"Invalid {source}: {error}");
        }

        return false;
    }

    private static void ParseRoot(
        YamlMappingNode root,
        ProgressionConfig progression,
        List<string> errors)
    {
        bool hasDefeatKeys = false;
        HashSet<string> tierIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<YamlNode, YamlNode> entry in root.Children)
        {
            if (!TryReadNonEmptyScalar(entry.Key, out string name))
            {
                errors.Add("Every root entry must have a non-empty scalar name.");
                continue;
            }

            if (string.Equals(name, "defeatKeys", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(name, "defeatKeys", StringComparison.Ordinal))
                {
                    errors.Add($"Reserved root entry '{name}' must be spelled exactly 'defeatKeys'.");
                    continue;
                }

                if (hasDefeatKeys)
                {
                    errors.Add("Duplicate root entry 'defeatKeys'.");
                    continue;
                }

                hasDefeatKeys = true;
                ParseDefeatKeys(entry.Value, progression.DefeatKeys, errors);
                continue;
            }

            if (!tierIds.Add(name))
            {
                errors.Add($"Duplicate item tier id '{name}'.");
                continue;
            }

            if (entry.Value is YamlSequenceNode resourceSequence)
            {
                List<string> resources = ParseResources(resourceSequence, $"tier '{name}'", errors);
                if (resources.Count == 0)
                {
                    errors.Add($"Unrestricted tier '{name}' must contain at least one resource.");
                }

                progression.Tiers.Add(new ItemTierConfig(name, "", resources));
                continue;
            }

            if (entry.Value is YamlMappingNode tierMapping)
            {
                ParseRestrictedTier(name, tierMapping, progression.Tiers, errors);
                continue;
            }

            errors.Add(
                $"Tier '{name}' must be either a resource sequence or a mapping with requiredKey and optional resources.");
        }
    }

    private static void ParseDefeatKeys(
        YamlNode node,
        List<DefeatKeyRule> destination,
        List<string> errors)
    {
        if (node is not YamlSequenceNode sequence)
        {
            errors.Add("defeatKeys must be a sequence.");
            return;
        }

        for (int index = 0; index < sequence.Children.Count; index++)
        {
            string label = $"defeatKeys[{index}]";
            if (sequence.Children[index] is not YamlMappingNode mapping)
            {
                errors.Add($"{label} must be a mapping with prefabs and key.");
                continue;
            }

            DefeatKeyRule rule = new();
            bool hasPrefabs = false;
            bool hasKey = false;
            HashSet<string> fields = new(StringComparer.Ordinal);
            foreach (KeyValuePair<YamlNode, YamlNode> entry in mapping.Children)
            {
                if (!TryReadNonEmptyScalar(entry.Key, out string field))
                {
                    errors.Add($"{label} contains an empty or non-scalar field name.");
                    continue;
                }

                if (!fields.Add(field))
                {
                    errors.Add($"{label} contains duplicate field '{field}'.");
                    continue;
                }

                switch (field)
                {
                    case "prefabs":
                        hasPrefabs = true;
                        if (entry.Value is not YamlSequenceNode prefabs)
                        {
                            errors.Add($"{label}.prefabs must be a sequence.");
                            break;
                        }

                        foreach (YamlNode prefabNode in prefabs.Children)
                        {
                            if (!TryReadNonEmptyScalar(prefabNode, out string prefab))
                            {
                                errors.Add($"{label}.prefabs contains an empty or non-scalar prefab.");
                                continue;
                            }

                            rule.Prefabs.Add(prefab);
                        }

                        break;
                    case "key":
                        hasKey = true;
                        if (!TryReadNonEmptyScalar(entry.Value, out string key))
                        {
                            errors.Add($"{label}.key must be a non-empty scalar.");
                            break;
                        }

                        rule.Key = key;
                        break;
                    default:
                        errors.Add($"{label} contains unknown field '{field}'.");
                        break;
                }
            }

            if (!hasPrefabs)
            {
                errors.Add($"{label} is missing required field 'prefabs'.");
            }

            if (!hasKey)
            {
                errors.Add($"{label} is missing required field 'key'.");
            }

            destination.Add(rule);
        }
    }

    private static void ParseRestrictedTier(
        string id,
        YamlMappingNode mapping,
        List<ItemTierConfig> destination,
        List<string> errors)
    {
        string label = $"tier '{id}'";
        string requiredKey = "";
        List<string> resources = new();
        bool hasRequiredKey = false;
        HashSet<string> fields = new(StringComparer.Ordinal);
        foreach (KeyValuePair<YamlNode, YamlNode> entry in mapping.Children)
        {
            if (!TryReadNonEmptyScalar(entry.Key, out string field))
            {
                errors.Add($"{label} contains an empty or non-scalar field name.");
                continue;
            }

            if (!fields.Add(field))
            {
                errors.Add($"{label} contains duplicate field '{field}'.");
                continue;
            }

            switch (field)
            {
                case "requiredKey":
                    hasRequiredKey = true;
                    if (!TryReadNonEmptyScalar(entry.Value, out requiredKey))
                    {
                        errors.Add($"{label}.requiredKey must be a non-empty scalar.");
                    }

                    break;
                case "resources":
                    if (entry.Value is not YamlSequenceNode resourceSequence)
                    {
                        errors.Add($"{label}.resources must be a sequence.");
                        break;
                    }

                    resources = ParseResources(resourceSequence, label, errors);
                    break;
                default:
                    errors.Add($"{label} contains unknown field '{field}'.");
                    break;
            }
        }

        if (!hasRequiredKey)
        {
            errors.Add($"{label} mapping is missing required field 'requiredKey'.");
        }

        destination.Add(new ItemTierConfig(id, requiredKey, resources));
    }

    private static List<string> ParseResources(
        YamlSequenceNode sequence,
        string label,
        List<string> errors)
    {
        List<string> resources = new();
        foreach (YamlNode node in sequence.Children)
        {
            if (!TryReadNonEmptyScalar(node, out string resource))
            {
                errors.Add($"{label} contains an empty or non-scalar resource.");
                continue;
            }

            resources.Add(resource);
        }

        return resources;
    }

    private static bool TryReadNonEmptyScalar(YamlNode node, out string value)
    {
        value = node is YamlScalarNode scalar ? scalar.Value?.Trim() ?? "" : "";
        return value.Length > 0;
    }

    private static void Validate(ProgressionConfig progression, List<string> errors)
    {
        ValidateDefeatKeys(progression, errors);
        ValidateItemTiers(progression, errors);
        try
        {
            ProgressionIndex.ValidateConfiguration(progression);
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }
    }

    private static void ValidateDefeatKeys(ProgressionConfig progression, List<string> errors)
    {
        for (int index = 0; index < progression.DefeatKeys.Count; index++)
        {
            DefeatKeyRule rule = progression.DefeatKeys[index];
            string label = $"defeatKeys[{index}]";
            if (string.IsNullOrWhiteSpace(rule.Key))
            {
                errors.Add($"{label} has an empty key.");
            }

            if (rule.Prefabs.Count == 0)
            {
                errors.Add($"{label} must contain at least one prefab.");
                continue;
            }

            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (string prefab in rule.Prefabs)
            {
                string normalized = ValheimNameUtils.NormalizePrefabName(prefab);
                if (normalized.Length == 0)
                {
                    errors.Add($"{label} contains an empty prefab.");
                }
                else if (!seen.Add(normalized))
                {
                    errors.Add($"{label} contains duplicate prefab '{prefab}'.");
                }
            }
        }
    }

    private static void ValidateItemTiers(ProgressionConfig progression, List<string> errors)
    {
        HashSet<string> resources = new(StringComparer.Ordinal);
        foreach (ItemTierConfig tier in progression.Tiers)
        {
            foreach (string resource in tier.Resources)
            {
                string normalized = ValheimNameUtils.NormalizeResourceName(resource);
                if (normalized.Length == 0)
                {
                    errors.Add($"Tier '{tier.Id}' contains invalid resource '{resource}'.");
                }
                else if (!resources.Add(normalized))
                {
                    errors.Add(
                        $"Resource '{resource}' appears more than once after normalization; every resource must belong to one tier.");
                }
            }
        }
    }

    private static void PreserveLastKnownGoodOrUseDefault()
    {
        if (IsConfigured)
        {
            YouAreNotWorthyPlugin.Log.LogError("Keeping the last-known-good YNW YAML configuration.");
            return;
        }

        try
        {
            string yaml = DefaultProgressionYaml;
            if (!TryApply(yaml, "the embedded default progression YAML"))
            {
                YouAreNotWorthyPlugin.Log.LogError(
                    "Failed to initialize the embedded default YNW progression YAML.");
                return;
            }

            YouAreNotWorthyPlugin.Log.LogWarning(
                "Using the embedded default YNW progression YAML until the local file is fixed.");
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError(
                $"Failed to initialize the embedded default YNW progression YAML: {ex}");
        }
    }

    private static string DefaultProgressionYaml => LoadEmbeddedYaml(".progression.default.yml");

    private static string LoadEmbeddedYaml(string suffix)
    {
        string? resourceName = typeof(ProgressionConfigLoader)
            .Assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(suffix, StringComparison.Ordinal));
        if (string.IsNullOrEmpty(resourceName))
        {
            throw new InvalidDataException($"The embedded default YAML ending in '{suffix}' was not found.");
        }

        using Stream? stream = typeof(ProgressionConfigLoader).Assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            throw new InvalidDataException($"The embedded default YAML '{resourceName}' could not be opened.");
        }

        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
