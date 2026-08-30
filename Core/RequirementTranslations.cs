using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using YamlDotNet.RepresentationModel;

namespace YouAreNotWorthy;

internal static class RequirementTranslations
{
    private const string BlockedKey = "blocked";
    private const string DefeatKey = "defeat";
    private const string DefeatEitherKey = "defeatEither";
    private const string FindKey = "find";
    private const string ObtainKey = "obtain";
    private const string Placeholder = "$1";
    private const string KoreanLanguageName = "Korean";
    private const string ExternalFilePrefix = YouAreNotWorthyPlugin.ModName + ".";
    private const string EmbeddedKoreanResourceName =
        "YouAreNotWorthy.translations.Korean.yml";

    private static readonly object Sync = new();

    private static readonly HashSet<string> AllowedKeys =
        new(StringComparer.Ordinal)
        {
            BlockedKey,
            DefeatKey,
            DefeatEitherKey,
            FindKey,
            ObtainKey
        };

    private static readonly TranslationTemplates English = new(
        "You are not worthy!",
        "Defeat $1!",
        "Defeat either $1!",
        "Find $1!",
        "Obtain $1!");

    private static TranslationTemplates _active = English;
    private static string _loadedLanguage = "";
    private static Dictionary<string, List<string>>? _externalFilesByLanguage;

    internal static void Initialize()
    {
        lock (Sync)
        {
            _externalFilesByLanguage ??= DiscoverExternalTranslationFiles();
        }
    }

    internal static void ReloadForSelectedLanguage()
    {
        lock (Sync)
        {
            LoadSelectedLanguage(force: true);
        }
    }

    internal static string FormatDefeat(string target) =>
        Format(static templates => templates.Defeat, target);

    internal static string FormatBlocked() =>
        Format(static templates => templates.Blocked, string.Empty);

    internal static string FormatDefeatEither(string targets) =>
        Format(static templates => templates.DefeatEither, targets);

    internal static string FormatFind(string target) =>
        Format(static templates => templates.Find, target);

    internal static string FormatObtain(string target) =>
        Format(static templates => templates.Obtain, target);

    private static string Format(
        Func<TranslationTemplates, string> selectTemplate,
        string? argument)
    {
        lock (Sync)
        {
            try
            {
                LoadSelectedLanguage(force: false);
                return selectTemplate(_active).Replace(Placeholder, argument ?? string.Empty);
            }
            catch (Exception ex)
            {
                YouAreNotWorthyPlugin.Log.LogError(
                    $"Failed to format a YNW requirement translation; using English: {ex.Message}");
                return selectTemplate(English)
                    .Replace(Placeholder, argument ?? string.Empty);
            }
        }
    }

    private static void LoadSelectedLanguage(bool force)
    {
        string language = GetSelectedLanguage();
        if (!force && string.Equals(language, _loadedLanguage, StringComparison.Ordinal))
        {
            return;
        }

        _loadedLanguage = language;
        _active = LoadEmbeddedLanguage(language);

        string? path = FindExternalLanguageFile(language);
        if (path == null)
        {
            return;
        }

        try
        {
            string yaml = File.ReadAllText(path);
            if (!TryParse(
                    yaml,
                    path,
                    out Dictionary<string, string> overrides,
                    out string error))
            {
                YouAreNotWorthyPlugin.Log.LogError(
                    $"Invalid YNW translation file '{path}'; keeping the embedded language or English fallback: {error}");
                return;
            }

            _active = _active.WithOverrides(overrides);
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError(
                $"Failed to read YNW translation file '{path}'; keeping the embedded language or English fallback: {ex.Message}");
        }
    }

    private static TranslationTemplates LoadEmbeddedLanguage(string language)
    {
        if (!string.Equals(language, KoreanLanguageName, StringComparison.OrdinalIgnoreCase))
        {
            return English;
        }

        try
        {
            using Stream? stream = typeof(RequirementTranslations)
                .Assembly
                .GetManifestResourceStream(EmbeddedKoreanResourceName);
            if (stream == null)
            {
                throw new InvalidDataException(
                    $"The embedded translation '{EmbeddedKoreanResourceName}' was not found.");
            }

            using StreamReader reader = new(stream);
            string yaml = reader.ReadToEnd();
            if (!TryParse(
                    yaml,
                    EmbeddedKoreanResourceName,
                    out Dictionary<string, string> overrides,
                    out string error))
            {
                throw new InvalidDataException(error);
            }

            return English.WithOverrides(overrides);
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError(
                $"Failed to load the embedded Korean translation; using built-in English translations: {ex.Message}");
            return English;
        }
    }

    internal static string GetSelectedLanguage()
    {
        try
        {
            return PlatformPrefs.GetString("language", "English").Trim() is { Length: > 0 } language
                ? language
                : "English";
        }
        catch
        {
            return "English";
        }
    }

    private static string? FindExternalLanguageFile(string language)
    {
        try
        {
            _externalFilesByLanguage ??= DiscoverExternalTranslationFiles();
            if (!_externalFilesByLanguage.TryGetValue(language, out List<string> externalMatches))
            {
                return null;
            }

            if (externalMatches.Count > 1)
            {
                YouAreNotWorthyPlugin.Log.LogWarning(
                    $"Multiple external YNW translation files match language '{language}'; ignoring all of them and keeping the embedded language or English fallback: {string.Join(", ", externalMatches)}");
                return null;
            }

            return externalMatches[0];
        }
        catch (Exception ex)
        {
            YouAreNotWorthyPlugin.Log.LogError(
                $"Failed to inspect YNW translation files; using built-in English translations: {ex.Message}");
            return null;
        }
    }

    private static Dictionary<string, List<string>> DiscoverExternalTranslationFiles()
    {
        Dictionary<string, List<string>> filesByLanguage =
            new(StringComparer.OrdinalIgnoreCase);
        string searchRoot = Paths.BepInExRootPath;
        if (string.IsNullOrWhiteSpace(searchRoot) || !Directory.Exists(searchRoot))
        {
            return filesByLanguage;
        }

        try
        {
            IEnumerable<string> candidates = Directory
                .EnumerateFiles(
                    searchRoot,
                    ExternalFilePrefix + "*",
                    SearchOption.AllDirectories)
                .Where(static file =>
                    string.Equals(
                        Path.GetExtension(file),
                        ".yml",
                        StringComparison.OrdinalIgnoreCase))
                .OrderBy(static file => file, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static file => file, StringComparer.Ordinal);

            foreach (string file in candidates)
            {
                if (!TryGetExternalLanguage(file, out string language))
                {
                    continue;
                }

                if (!filesByLanguage.TryGetValue(language, out List<string> matches))
                {
                    matches = new List<string>();
                    filesByLanguage[language] = matches;
                }

                matches.Add(file);
            }
        }
        catch (Exception ex)
        {
            filesByLanguage.Clear();
            YouAreNotWorthyPlugin.Log.LogWarning(
                $"Could not scan BepInEx for external YNW translation files; embedded translations will still be used: {ex.Message}");
        }

        return filesByLanguage;
    }

    private static bool TryGetExternalLanguage(string path, out string language)
    {
        language = string.Empty;
        if (!string.Equals(Path.GetExtension(path), ".yml", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string stem = Path.GetFileNameWithoutExtension(path);
        if (!stem.StartsWith(ExternalFilePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        language = stem.Substring(ExternalFilePrefix.Length);
        if (string.IsNullOrWhiteSpace(language)
            || language.Length != language.Trim().Length
            || language.IndexOf('.') >= 0)
        {
            language = string.Empty;
            return false;
        }

        return true;
    }

    private static bool TryParse(
        string yaml,
        string source,
        out Dictionary<string, string> values,
        out string error)
    {
        values = new Dictionary<string, string>(StringComparer.Ordinal);
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(yaml))
        {
            error = $"{source} is empty.";
            return false;
        }

        try
        {
            YamlStream stream = new();
            using StringReader reader = new(yaml);
            stream.Load(reader);
            if (stream.Documents.Count != 1
                || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                error = $"{source} must contain exactly one YAML mapping document.";
                return false;
            }

            foreach (KeyValuePair<YamlNode, YamlNode> entry in root.Children)
            {
                if (entry.Key is not YamlScalarNode keyNode
                    || string.IsNullOrEmpty(keyNode.Value))
                {
                    error = $"{source} contains an empty or non-scalar field name.";
                    return false;
                }

                string key = keyNode.Value!;
                if (!AllowedKeys.Contains(key))
                {
                    error = $"{source} contains unknown field '{key}'.";
                    return false;
                }

                if (values.ContainsKey(key))
                {
                    error = $"{source} contains duplicate field '{key}'.";
                    return false;
                }

                if (entry.Value is not YamlScalarNode valueNode
                    || string.IsNullOrWhiteSpace(valueNode.Value))
                {
                    error = $"{source}.{key} must be a non-empty scalar.";
                    return false;
                }

                string value = valueNode.Value!;
                bool requiresPlaceholder = !string.Equals(key, BlockedKey, StringComparison.Ordinal);
                if (!HasValidPlaceholderSet(value, requiresPlaceholder))
                {
                    error = requiresPlaceholder
                        ? $"{source}.{key} must contain {Placeholder} and may not contain other numeric placeholders."
                        : $"{source}.{key} may not contain numeric placeholders.";
                    return false;
                }

                values.Add(key, value);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"{source} could not be parsed: {ex.Message}";
            return false;
        }
    }

    private static bool HasValidPlaceholderSet(string template, bool requiresPlaceholder)
    {
        bool found = false;
        for (int index = 0; index < template.Length - 1; index++)
        {
            if (template[index] != '$' || !char.IsDigit(template[index + 1]))
            {
                continue;
            }

            int end = index + 2;
            while (end < template.Length && char.IsDigit(template[end]))
            {
                end++;
            }

            if (end - index != Placeholder.Length
                || !string.Equals(
                    template.Substring(index, Placeholder.Length),
                    Placeholder,
                    StringComparison.Ordinal))
            {
                return false;
            }

            found = true;
            index = end - 1;
        }

        return found == requiresPlaceholder;
    }

    private sealed class TranslationTemplates
    {
        internal TranslationTemplates(
            string blocked,
            string defeat,
            string defeatEither,
            string find,
            string obtain)
        {
            Blocked = blocked;
            Defeat = defeat;
            DefeatEither = defeatEither;
            Find = find;
            Obtain = obtain;
        }

        internal string Blocked { get; }
        internal string Defeat { get; }
        internal string DefeatEither { get; }
        internal string Find { get; }
        internal string Obtain { get; }

        internal TranslationTemplates WithOverrides(IReadOnlyDictionary<string, string> values) =>
            new(
                values.TryGetValue(BlockedKey, out string blocked) ? blocked : Blocked,
                values.TryGetValue(DefeatKey, out string defeat) ? defeat : Defeat,
                values.TryGetValue(DefeatEitherKey, out string defeatEither)
                    ? defeatEither
                    : DefeatEither,
                values.TryGetValue(FindKey, out string find) ? find : Find,
                values.TryGetValue(ObtainKey, out string obtain) ? obtain : Obtain);
    }
}
