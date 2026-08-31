using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using YamlDotNet.RepresentationModel;

namespace YouAreNotWorthy;

internal static class LocationIconConfigLoader
{
    internal const string ConfigFileName = "locations.yml";
    internal const string SyncedYamlIdentifier = "LocationsYaml";

    internal static string ConfigDirectory => Path.Combine(Paths.ConfigPath, YouAreNotWorthyPlugin.ModName);
    internal static string ConfigPath => Path.Combine(ConfigDirectory, ConfigFileName);

    private static bool IsConfigured { get; set; }
    private static IReadOnlyDictionary<string, string> _current =
        new Dictionary<string, string>(StringComparer.Ordinal);

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
        return TryApply(yaml, "ServerSync locations.yml");
    }

    internal static bool TryGetRequiredKey(string? locationIconIdentifier, out string requiredKey)
    {
        string normalized = ValheimNameUtils.NormalizePrefabName(locationIconIdentifier);
        if (normalized.Length > 0 && _current.TryGetValue(normalized, out requiredKey))
        {
            return true;
        }

        requiredKey = string.Empty;
        return false;
    }

    private static bool TryReadLocal(out string yaml, bool createIfMissing = false)
    {
        yaml = "";
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            if (!TryFindCanonicalConfig(out bool exists))
            {
                return false;
            }

            if (!exists)
            {
                if (!createIfMissing)
                {
                    YouAreNotWorthyPlugin.Log.LogWarning($"Configuration file not found: {ConfigPath}");
                    return false;
                }

                File.WriteAllText(ConfigPath, DefaultLocationsYaml);
            }

            yaml = File.ReadAllText(ConfigPath);
            return true;
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError($"Failed to read YNW locations.yml: {ex}");
            return false;
        }
    }

    private static bool TryFindCanonicalConfig(out bool exists)
    {
        exists = false;
        bool hasCaseVariant = false;
        foreach (string file in Directory.EnumerateFiles(ConfigDirectory))
        {
            string fileName = Path.GetFileName(file);
            if (string.Equals(fileName, ConfigFileName, StringComparison.Ordinal))
            {
                exists = true;
                continue;
            }

            hasCaseVariant |= string.Equals(
                fileName,
                ConfigFileName,
                StringComparison.OrdinalIgnoreCase);
        }

        if (!hasCaseVariant)
        {
            return true;
        }

        YouAreNotWorthyPlugin.Log.LogError(
            $"The YNW location configuration must be named exactly '{ConfigFileName}' and no case variants may coexist.");
        return false;
    }

    private static bool TryApply(string yaml, string source)
    {
        if (!TryParseAndValidate(yaml, source, out Dictionary<string, string> locations))
        {
            return false;
        }

        _current = locations;
        AppliedYaml = yaml;
        IsConfigured = true;
        return true;
    }

    private static bool TryParseAndValidate(
        string yaml,
        string source,
        out Dictionary<string, string> locations)
    {
        locations = new Dictionary<string, string>(StringComparer.Ordinal);
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
                    $"Invalid {source}: Expected one location-icon mapping document.");
                return false;
            }

            ParseRoot(root, locations, errors);
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError($"Failed to parse {source}: {ex}");
            return false;
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
        Dictionary<string, string> locations,
        List<string> errors)
    {
        HashSet<string> identifiers = new(StringComparer.Ordinal);
        Dictionary<string, string> canonicalKeys = new(StringComparer.Ordinal);
        foreach (KeyValuePair<YamlNode, YamlNode> entry in root.Children)
        {
            if (!TryReadNonEmptyScalar(entry.Key, out string identifier))
            {
                errors.Add("Every location-icon identifier must be a non-empty scalar.");
                continue;
            }

            string normalizedIdentifier = ValheimNameUtils.NormalizePrefabName(identifier);
            if (normalizedIdentifier.Length == 0)
            {
                errors.Add($"Location-icon identifier '{identifier}' is invalid.");
                continue;
            }

            if (!identifiers.Add(normalizedIdentifier))
            {
                errors.Add(
                    $"Location-icon identifier '{identifier}' is configured more than once after normalization.");
                continue;
            }

            if (!TryReadNonEmptyScalar(entry.Value, out string configuredKey))
            {
                errors.Add($"Location-icon identifier '{identifier}' must map to a non-empty key scalar.");
                continue;
            }

            if (!ProgressionIndex.TryResolvePersonalKey(configuredKey, out string canonicalKey))
            {
                errors.Add(
                    $"Location-icon identifier '{identifier}' uses shared, value-bearing, or otherwise invalid key '{configuredKey}'.");
                continue;
            }

            string normalizedKey = ValheimNameUtils.NormalizeKey(canonicalKey);
            if (canonicalKeys.TryGetValue(normalizedKey, out string existingKey)
                && !string.Equals(existingKey, canonicalKey, StringComparison.Ordinal))
            {
                errors.Add(
                    $"Required key '{configuredKey}' conflicts with configured casing '{existingKey}'. Use one canonical spelling everywhere.");
                continue;
            }

            canonicalKeys[normalizedKey] = canonicalKey;
            locations.Add(normalizedIdentifier, canonicalKey);
        }
    }

    private static bool TryReadNonEmptyScalar(YamlNode node, out string value)
    {
        value = node is YamlScalarNode scalar ? scalar.Value?.Trim() ?? "" : "";
        return value.Length > 0;
    }

    private static void PreserveLastKnownGoodOrUseDefault()
    {
        if (IsConfigured)
        {
            YouAreNotWorthyPlugin.Log.LogError(
                "Keeping the last-known-good YNW locations.yml configuration.");
            return;
        }

        try
        {
            if (!TryApply(DefaultLocationsYaml, "the embedded default locations YAML"))
            {
                YouAreNotWorthyPlugin.Log.LogError(
                    "Failed to initialize the embedded default YNW locations YAML.");
                return;
            }

            YouAreNotWorthyPlugin.Log.LogWarning(
                "Using the embedded default YNW locations YAML until the local file is fixed.");
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError(
                $"Failed to initialize the embedded default YNW locations YAML: {ex}");
        }
    }

    private static string DefaultLocationsYaml => LoadEmbeddedYaml(".locations.default.yml");

    private static string LoadEmbeddedYaml(string suffix)
    {
        string? resourceName = typeof(LocationIconConfigLoader)
            .Assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(suffix, StringComparison.Ordinal));
        if (string.IsNullOrEmpty(resourceName))
        {
            throw new InvalidDataException($"The embedded default YAML ending in '{suffix}' was not found.");
        }

        using Stream? stream = typeof(LocationIconConfigLoader).Assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            throw new InvalidDataException($"The embedded default YAML '{resourceName}' could not be opened.");
        }

        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
