using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Bootstrap;
using UnityEngine;

namespace YouAreNotWorthy;

internal static class PrefabOwnerResolver
{
    internal const string VanillaOwnerName = "Valheim";
    internal const string UnknownOwnerName = "Unknown / Untracked";

    private const string JotunnModQueryTypeName = "Jotunn.Utils.ModQuery";
    private const int MinimumHeuristicTokenLength = 5;
    private static readonly BindingFlags AnyMember =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly HashSet<string> VanillaPrefabNames =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheSync = new();

    private static bool _vanillaCatalogLoaded;
    private static bool _vanillaCatalogWarningLogged;
    private static bool _ownerWarningLogged;
    private static string _cachedTargetSignature = string.Empty;
    private static IReadOnlyDictionary<string, string>? _cachedOwners;

    internal static void Invalidate()
    {
        lock (CacheSync)
        {
            _cachedTargetSignature = string.Empty;
            _cachedOwners = null;
        }
    }

    internal static IReadOnlyDictionary<string, string> Resolve(IEnumerable<string> prefabNames)
    {
        List<string> targets = (prefabNames ?? Enumerable.Empty<string>())
            .Select(GetPrefabNamePreservingCase)
            .Where(static name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static name => name, StringComparer.Ordinal)
            .ToList();
        string targetSignature = string.Join(
            "\n",
            targets.Select(ValheimNameUtils.NormalizePrefabName));
        lock (CacheSync)
        {
            if (_cachedOwners != null
                && string.Equals(
                    _cachedTargetSignature,
                    targetSignature,
                    StringComparison.Ordinal))
            {
                return _cachedOwners;
            }
        }

        Dictionary<string, string> owners = new(StringComparer.OrdinalIgnoreCase);
        if (targets.Count == 0)
        {
            return owners;
        }

        try
        {
            HashSet<string> targetSet = new(targets, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> jotunnOwners = CollectJotunnOwners(targetSet);
            EnsureVanillaCatalogLoaded();
            Dictionary<string, Dictionary<string, string>> bundleOwners =
                CollectAssetBundleOwners(targetSet, BuildPluginSnapshots());

            foreach (string target in targets)
            {
                string owner = ResolveMappedOwner(target, jotunnOwners);
                if (owner.Length == 0 && IsVanillaPrefab(target))
                {
                    owner = VanillaOwnerName;
                }

                if (owner.Length == 0)
                {
                    owner = ResolveUniqueBundleOwner(target, bundleOwners);
                }

                owners[target] = owner.Length > 0
                    ? NormalizeOwnerName(owner)
                    : UnknownOwnerName;
            }
        }
        catch (Exception ex)
        {
            WarnOwnerResolutionOnce(
                "Could not resolve one or more item prefab owners; unresolved entries will be grouped under '"
                + UnknownOwnerName + "': " + ex.GetBaseException().Message);
            foreach (string target in targets)
            {
                owners[target] = UnknownOwnerName;
            }
        }

        lock (CacheSync)
        {
            _cachedTargetSignature = targetSignature;
            _cachedOwners = owners;
            return _cachedOwners;
        }
    }

    internal static int GetOwnerSortBucket(string? ownerName)
    {
        string normalized = NormalizeOwnerName(ownerName);
        if (normalized.Equals(VanillaOwnerName, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return normalized.Equals(UnknownOwnerName, StringComparison.OrdinalIgnoreCase) ? 2 : 1;
    }

    internal static string NormalizeOwnerName(string? ownerName)
    {
        if (string.IsNullOrWhiteSpace(ownerName))
        {
            return UnknownOwnerName;
        }

        StringBuilder builder = new();
        foreach (char character in ownerName!)
        {
            if (character == '\r' || character == '\n')
            {
                builder.Append(' ');
            }
            else if (!char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        string normalized = builder.ToString().Trim();
        return normalized.Length > 0 ? normalized : UnknownOwnerName;
    }

    private static Dictionary<string, string> CollectJotunnOwners(HashSet<string> targets)
    {
        Dictionary<string, string> owners = new(StringComparer.OrdinalIgnoreCase);
        Type? modQueryType = GetLoadedAssemblies()
            .Select(static assembly => SafeGetType(assembly, JotunnModQueryTypeName))
            .FirstOrDefault(static type => type != null);
        MethodInfo? getPrefab = modQueryType?.GetMethod(
            "GetPrefab",
            AnyMember,
            binder: null,
            types: new[] { typeof(string) },
            modifiers: null);
        if (getPrefab == null)
        {
            return owners;
        }

        foreach (string target in targets)
        {
            foreach (string candidate in EnumerateLookupCandidates(target))
            {
                try
                {
                    object? holder = getPrefab.Invoke(null, new object[] { candidate });
                    if (holder != null && TryResolveSourceModOwner(holder, out string owner))
                    {
                        owners[target] = owner;
                        break;
                    }
                }
                catch
                {
                    // Jotunn is optional and its metadata API may change independently.
                }
            }
        }

        return owners;
    }

    private static bool TryResolveSourceModOwner(object holder, out string ownerName)
    {
        ownerName = string.Empty;
        if (!TryGetRawMemberValue(holder, "SourceMod", out object? sourceMod)
            || sourceMod == null)
        {
            return false;
        }

        string guid = TryGetRawMemberValue(sourceMod, "GUID", out object? guidValue)
            ? (guidValue?.ToString() ?? string.Empty).Trim()
            : string.Empty;
        if (guid.Length > 0 && Chainloader.PluginInfos.TryGetValue(guid, out var pluginInfo))
        {
            ownerName = NormalizeOwnerName(
                string.IsNullOrWhiteSpace(pluginInfo.Metadata.Name)
                    ? pluginInfo.Metadata.GUID
                    : pluginInfo.Metadata.Name);
            return true;
        }

        if (guid.Length > 0)
        {
            ownerName = NormalizeOwnerName(guid);
            return true;
        }

        if (TryGetRawMemberValue(sourceMod, "Name", out object? nameValue))
        {
            ownerName = NormalizeOwnerName(nameValue?.ToString());
            return !ownerName.Equals(UnknownOwnerName, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static Dictionary<string, Dictionary<string, string>> CollectAssetBundleOwners(
        HashSet<string> targets,
        List<PluginSnapshot> plugins)
    {
        Dictionary<string, Dictionary<string, string>> ownersByPrefab =
            new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> lookupTargets = new(StringComparer.OrdinalIgnoreCase);
        foreach (string target in targets)
        {
            lookupTargets.UnionWith(EnumerateLookupCandidates(target));
        }

        AssetBundle[] bundles;
        try
        {
            bundles = AssetBundle.GetAllLoadedAssetBundles()
                .Where(static bundle => bundle != null)
                .OrderBy(static bundle => bundle.name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static bundle => bundle.name ?? string.Empty, StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            return ownersByPrefab;
        }

        foreach (AssetBundle bundle in bundles)
        {
            PluginSnapshot? plugin = ResolveBundleOwner(bundle.name ?? string.Empty, plugins);
            if (plugin == null)
            {
                continue;
            }

            string[] assetNames;
            try
            {
                assetNames = bundle.GetAllAssetNames();
            }
            catch
            {
                continue;
            }

            foreach (string assetName in assetNames)
            {
                if (!assetName.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string prefabName = ValheimNameUtils.NormalizePrefabName(
                    Path.GetFileNameWithoutExtension(assetName));
                if (prefabName.Length == 0 || !lookupTargets.Contains(prefabName))
                {
                    continue;
                }

                if (!ownersByPrefab.TryGetValue(
                        prefabName,
                        out Dictionary<string, string> candidateOwners))
                {
                    candidateOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    ownersByPrefab[prefabName] = candidateOwners;
                }

                candidateOwners[plugin.Identity] = plugin.OwnerName;
            }
        }

        return ownersByPrefab;
    }

    private static PluginSnapshot? ResolveBundleOwner(
        string bundleName,
        IEnumerable<PluginSnapshot> plugins)
    {
        string name = (bundleName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return null;
        }

        List<PluginSnapshot> exact = UniquePlugins(plugins.Where(plugin =>
            plugin.ResourceNames.Any(resource => IsBundleResourceMatch(resource, name))));
        if (exact.Count > 0)
        {
            return exact.Count == 1 ? exact[0] : null;
        }

        string bundleToken = NormalizeToken(Path.GetFileNameWithoutExtension(name));
        if (bundleToken.Length < MinimumHeuristicTokenLength)
        {
            return null;
        }

        List<PluginSnapshot> heuristic = UniquePlugins(plugins.Where(plugin =>
            IsTokenMatch(bundleToken, plugin.PluginNameToken)
            || IsTokenMatch(bundleToken, plugin.PluginGuidToken)
            || IsTokenMatch(bundleToken, plugin.AssemblyNameToken)));
        return heuristic.Count == 1 ? heuristic[0] : null;
    }

    private static List<PluginSnapshot> UniquePlugins(IEnumerable<PluginSnapshot> plugins)
    {
        Dictionary<string, PluginSnapshot> unique = new(StringComparer.OrdinalIgnoreCase);
        foreach (PluginSnapshot plugin in plugins)
        {
            unique[plugin.Identity] = plugin;
        }

        return unique.Values.ToList();
    }

    private static List<PluginSnapshot> BuildPluginSnapshots()
    {
        List<PluginSnapshot> plugins = new();
        foreach (var entry in Chainloader.PluginInfos)
        {
            var pluginInfo = entry.Value;
            string guid = (pluginInfo.Metadata.GUID ?? entry.Key ?? string.Empty).Trim();
            string name = (pluginInfo.Metadata.Name ?? string.Empty).Trim();
            string assemblyName = string.Empty;
            string[] resources = Array.Empty<string>();
            try
            {
                Assembly? assembly = pluginInfo.Instance?.GetType().Assembly;
                assemblyName = assembly?.GetName().Name ?? string.Empty;
                resources = assembly?.GetManifestResourceNames() ?? Array.Empty<string>();
            }
            catch
            {
                // A partially initialized plugin is not a fatal owner lookup failure.
            }

            string identity = guid.Length > 0 ? guid : assemblyName;
            if (identity.Length == 0)
            {
                continue;
            }

            plugins.Add(new PluginSnapshot(
                identity,
                NormalizeOwnerName(name.Length > 0 ? name : identity),
                NormalizeToken(name),
                NormalizeToken(guid),
                NormalizeToken(assemblyName),
                resources));
        }

        return plugins;
    }

    private static bool IsBundleResourceMatch(string? resourceName, string bundleName)
    {
        string resource = (resourceName ?? string.Empty).Trim();
        return resource.Length > 0
               && (resource.Equals(bundleName, StringComparison.OrdinalIgnoreCase)
                   || resource.EndsWith("." + bundleName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTokenMatch(string bundleToken, string pluginToken)
    {
        return pluginToken.Length >= MinimumHeuristicTokenLength
               && (bundleToken.IndexOf(pluginToken, StringComparison.OrdinalIgnoreCase) >= 0
                   || pluginToken.IndexOf(bundleToken, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static string NormalizeToken(string? value)
    {
        StringBuilder builder = new();
        foreach (char character in value ?? string.Empty)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static string ResolveMappedOwner(
        string prefabName,
        IReadOnlyDictionary<string, string> mappings)
    {
        foreach (string candidate in EnumerateLookupCandidates(prefabName))
        {
            if (mappings.TryGetValue(candidate, out string owner) && owner.Length > 0)
            {
                return owner;
            }
        }

        return string.Empty;
    }

    private static string ResolveUniqueBundleOwner(
        string prefabName,
        IReadOnlyDictionary<string, Dictionary<string, string>> mappings)
    {
        Dictionary<string, string> candidates = new(StringComparer.OrdinalIgnoreCase);
        foreach (string lookupName in EnumerateLookupCandidates(prefabName))
        {
            if (!mappings.TryGetValue(
                    lookupName,
                    out Dictionary<string, string> mappedCandidates))
            {
                continue;
            }

            foreach (KeyValuePair<string, string> candidate in mappedCandidates)
            {
                candidates[candidate.Key] = candidate.Value;
            }
        }

        return candidates.Count == 1 ? candidates.Values.First() : string.Empty;
    }

    private static bool IsVanillaPrefab(string prefabName)
    {
        return EnumerateLookupCandidates(prefabName).Any(VanillaPrefabNames.Contains);
    }

    private static IEnumerable<string> EnumerateLookupCandidates(string prefabName)
    {
        string candidate = GetPrefabNamePreservingCase(prefabName);
        if (candidate.Length == 0)
        {
            yield break;
        }

        yield return candidate;
        int aliasSeparator = candidate.IndexOf(':');
        if (aliasSeparator > 0)
        {
            yield return candidate.Substring(0, aliasSeparator);
        }
    }

    private static string GetPrefabNamePreservingCase(string? prefabName)
    {
        string value = (prefabName ?? string.Empty).Trim();
        return value.EndsWith("(Clone)", StringComparison.Ordinal)
            ? value.Substring(0, value.Length - "(Clone)".Length)
            : value;
    }

    private static void EnsureVanillaCatalogLoaded()
    {
        if (_vanillaCatalogLoaded)
        {
            return;
        }

        string directory = Path.Combine(
            Application.dataPath,
            "StreamingAssets",
            "SoftRef");
        string[] paths =
        {
            Path.Combine(directory, "manifest"),
            Path.Combine(directory, "manifest_extended")
        };
        int filesFound = 0;
        int filesRead = 0;
        const string marker = "path in bundle:";
        foreach (string path in paths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            filesFound++;
            try
            {
                foreach (string line in File.ReadLines(path))
                {
                    int markerIndex = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (markerIndex < 0)
                    {
                        continue;
                    }

                    string assetPath = line.Substring(markerIndex + marker.Length).Trim();
                    if (!assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string prefabName = ValheimNameUtils.NormalizePrefabName(
                        Path.GetFileNameWithoutExtension(assetPath));
                    if (prefabName.Length > 0)
                    {
                        VanillaPrefabNames.Add(prefabName);
                    }
                }

                filesRead++;
            }
            catch (Exception ex)
            {
                WarnVanillaCatalogOnce(
                    $"Could not read vanilla prefab manifest '{path}': {ex.GetBaseException().Message}");
            }
        }

        _vanillaCatalogLoaded = filesRead > 0;
        if (filesFound == 0)
        {
            WarnVanillaCatalogOnce(
                $"Vanilla prefab manifests were not found under '{directory}'; vanilla item references may be grouped under '{UnknownOwnerName}'.");
        }
    }

    private static bool TryGetRawMemberValue(
        object value,
        string memberName,
        out object? result)
    {
        result = null;
        try
        {
            Type type = value.GetType();
            PropertyInfo? property = type.GetProperty(memberName, AnyMember);
            if (property != null)
            {
                result = property.GetValue(value, null);
                return true;
            }

            FieldInfo? field = type.GetField(memberName, AnyMember);
            if (field == null)
            {
                return false;
            }

            result = field.GetValue(value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Type? SafeGetType(Assembly assembly, string typeName)
    {
        try
        {
            return assembly.GetType(typeName, throwOnError: false);
        }
        catch
        {
            return null;
        }
    }

    private static Assembly[] GetLoadedAssemblies()
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(static assembly => !assembly.IsDynamic)
            .OrderBy(static assembly => assembly.FullName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static assembly => assembly.FullName ?? string.Empty, StringComparer.Ordinal)
            .ToArray();
    }

    private static void WarnVanillaCatalogOnce(string message)
    {
        if (_vanillaCatalogWarningLogged)
        {
            return;
        }

        _vanillaCatalogWarningLogged = true;
        YouAreNotWorthyPlugin.Log.LogWarning(message);
    }

    private static void WarnOwnerResolutionOnce(string message)
    {
        if (_ownerWarningLogged)
        {
            return;
        }

        _ownerWarningLogged = true;
        YouAreNotWorthyPlugin.Log.LogWarning(message);
    }

    private sealed class PluginSnapshot
    {
        internal PluginSnapshot(
            string identity,
            string ownerName,
            string pluginNameToken,
            string pluginGuidToken,
            string assemblyNameToken,
            string[] resourceNames)
        {
            Identity = identity;
            OwnerName = ownerName;
            PluginNameToken = pluginNameToken;
            PluginGuidToken = pluginGuidToken;
            AssemblyNameToken = assemblyNameToken;
            ResourceNames = resourceNames;
        }

        internal string Identity { get; }
        internal string OwnerName { get; }
        internal string PluginNameToken { get; }
        internal string PluginGuidToken { get; }
        internal string AssemblyNameToken { get; }
        internal string[] ResourceNames { get; }
    }
}
