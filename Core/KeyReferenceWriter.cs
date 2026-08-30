using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Object = UnityEngine.Object;

namespace YouAreNotWorthy;

internal static class KeyReferenceWriter
{
    internal const string ReferenceFileName = "keys.reference.yml";
    internal static string ReferencePath => Path.Combine(
        ProgressionConfigLoader.ConfigDirectory,
        ReferenceFileName);

    private const float ObservationDebounceSeconds = 1f;
    private const float NotReadyRetrySeconds = 1f;
    private const float FailureRetrySeconds = 5f;
    private const int MaxObservedGlobalKeys = 4096;
    private const int MaxObservedGlobalKeyLength = 256;
    private const string PersonalizedHandling = "personalized";
    private const string SharedWorldHandling = "sharedWorld";

    private const int EnumNamePriority = 0;
    private const int DefeatKeyPriority = 1;
    private const int ObservedNamePriority = 2;
    private const int RaidNamePriority = 3;
    private const int WorldFallbackPriority = 4;

    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(
            DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitDefaults)
        .Build();
    private static readonly object Sync = new();
    private static readonly Dictionary<string, ObservedGlobalKey> ObservedGlobalKeys =
        new(StringComparer.OrdinalIgnoreCase);

    private static bool _dirty = true;
    private static bool _shutdown;
    private static bool _runtimeReady;
    private static bool _wasSourceOfTruth;
    private static bool _observationLimitLogged;
    private static long _revision = 1;
    private static float _updateAfter;
    private static float _retryAfter;
    private static ZoneSystem? _lastZoneSystem;
    private static ZNetScene? _lastNetScene;
    private static RandEventSystem? _lastEventSystem;

    internal static void ObserveGlobalKey(string? rawKey)
    {
        string candidate = (rawKey ?? string.Empty).Trim();
        if (candidate.Length == 0
            || candidate.Length > MaxObservedGlobalKeyLength
            || candidate.Any(char.IsControl)
            || !TryParseGlobalKey(candidate, out ParsedGlobalKey parsed))
        {
            return;
        }

        lock (Sync)
        {
            if (_shutdown)
            {
                return;
            }

            if (!ObservedGlobalKeys.TryGetValue(parsed.Identity, out ObservedGlobalKey observed))
            {
                if (ObservedGlobalKeys.Count >= MaxObservedGlobalKeys)
                {
                    if (!_observationLimitLogged)
                    {
                        _observationLimitLogged = true;
                        YouAreNotWorthyPlugin.Log.LogWarning(
                            $"YNW stopped recording newly observed global keys after {MaxObservedGlobalKeys} unique entries.");
                    }

                    return;
                }

                observed = new ObservedGlobalKey(parsed.Identity);
                ObservedGlobalKeys[parsed.Identity] = observed;
            }

            if (!observed.Add(parsed.Spelling, parsed.HasValue))
            {
                return;
            }

            _dirty = true;
            _revision++;
            _updateAfter = Time.realtimeSinceStartup + ObservationDebounceSeconds;
        }
    }

    internal static void Reset()
    {
        lock (Sync)
        {
            ObservedGlobalKeys.Clear();
            _shutdown = false;
            _runtimeReady = false;
            _wasSourceOfTruth = false;
            _observationLimitLogged = false;
            _dirty = true;
            _revision++;
            _updateAfter = 0f;
            _retryAfter = 0f;
            _lastZoneSystem = null;
            _lastNetScene = null;
            _lastEventSystem = null;
        }
    }

    internal static void MarkRuntimeReady()
    {
        lock (Sync)
        {
            if (_shutdown)
            {
                return;
            }

            _runtimeReady = true;
            MarkDirtyLocked();
        }
    }

    internal static void MarkDirty()
    {
        lock (Sync)
        {
            if (!_shutdown)
            {
                MarkDirtyLocked();
            }
        }
    }

    internal static void Shutdown()
    {
        lock (Sync)
        {
            ObservedGlobalKeys.Clear();
            _shutdown = true;
            _runtimeReady = false;
            _wasSourceOfTruth = false;
            _observationLimitLogged = false;
            _dirty = false;
            _revision++;
            _updateAfter = 0f;
            _retryAfter = 0f;
            _lastZoneSystem = null;
            _lastNetScene = null;
            _lastEventSystem = null;
        }
    }

    internal static void TryUpdate(bool isSourceOfTruth)
    {
        bool canWrite = isSourceOfTruth
                        && (Object?)ZNet.instance != null
                        && ZNet.instance.IsServer();
        ZoneSystem? currentZoneSystem = ZoneSystem.instance;
        ZNetScene? currentNetScene = ZNetScene.instance;
        RandEventSystem? currentEventSystem = RandEventSystem.instance;
        lock (Sync)
        {
            if (_shutdown)
            {
                return;
            }

            if (canWrite && !_wasSourceOfTruth)
            {
                MarkDirtyLocked();
            }

            _wasSourceOfTruth = canWrite;
            if (!ReferenceEquals(_lastZoneSystem, currentZoneSystem)
                || !ReferenceEquals(_lastNetScene, currentNetScene)
                || !ReferenceEquals(_lastEventSystem, currentEventSystem))
            {
                _lastZoneSystem = currentZoneSystem;
                _lastNetScene = currentNetScene;
                _lastEventSystem = currentEventSystem;
                MarkDirtyLocked();
            }
        }

        if (!canWrite)
        {
            return;
        }

        float now = Time.realtimeSinceStartup;
        long revision;
        List<ObservedGlobalKeySnapshot> observed;
        lock (Sync)
        {
            if (_shutdown || !_dirty || now < _updateAfter || now < _retryAfter)
            {
                return;
            }

            revision = _revision;
            observed = ObservedGlobalKeys.Values
                .Select(static key => key.Snapshot())
                .ToList();
        }

        if (!TryGetReadyRuntime(
                out ZoneSystem zoneSystem,
                out ZNetScene netScene,
                out RandEventSystem eventSystem))
        {
            lock (Sync)
            {
                if (!_shutdown && _revision == revision)
                {
                    _updateAfter = now + NotReadyRetrySeconds;
                }
            }

            return;
        }

        try
        {
            string content = BuildContent(zoneSystem, netScene, eventSystem, observed);
            WriteIfChanged(content);

            lock (Sync)
            {
                _retryAfter = 0f;
                if (!_shutdown && _revision == revision)
                {
                    _dirty = false;
                }
            }
        }
        catch (Exception ex)
        {
            lock (Sync)
            {
                if (!_shutdown)
                {
                    _dirty = true;
                    _retryAfter = Time.realtimeSinceStartup + FailureRetrySeconds;
                }
            }

            YouAreNotWorthyPlugin.Log.LogWarning(
                $"Failed to update the key reference: {ex.GetType().Name}: {ex.Message}. "
                + $"Retrying in {FailureRetrySeconds:0.#}s.");
        }
    }

    private static bool TryGetReadyRuntime(
        out ZoneSystem zoneSystem,
        out ZNetScene netScene,
        out RandEventSystem eventSystem)
    {
        zoneSystem = ZoneSystem.instance;
        netScene = ZNetScene.instance;
        eventSystem = RandEventSystem.instance;
        return (Object?)zoneSystem != null
               && (Object?)netScene != null
               && (Object?)eventSystem != null
               && _runtimeReady
               && zoneSystem.m_globalKeysValues != null
               && ValheimNameUtils.EnumerateRegisteredPrefabs(netScene).Any()
               && eventSystem.m_events != null
               && eventSystem.m_events.Count > 0;
    }

    private static string BuildContent(
        ZoneSystem zoneSystem,
        ZNetScene netScene,
        RandEventSystem eventSystem,
        IEnumerable<ObservedGlobalKeySnapshot> observed)
    {
        Dictionary<string, GlobalKeyBuilder> globalKeys =
            new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> playerBasedRaidKeys = new(StringComparer.Ordinal);

        AddEnumGlobalKeys(globalKeys);
        AddWorldFallbackKeys(zoneSystem, globalKeys);
        AddRaidKeys(eventSystem, globalKeys, playerBasedRaidKeys);
        AddDefeatKeys(netScene, globalKeys);
        AddObservedKeys(observed, globalKeys);

        KeyReferenceDocument document = new()
        {
            GlobalKeys = globalKeys.Values
                .Select(static key => key.Build())
                .OrderBy(static key => string.Equals(
                    key.Handling,
                    SharedWorldHandling,
                    StringComparison.Ordinal) ? 1 : 0)
                .ThenBy(static key => key.Key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static key => key.Key, StringComparer.Ordinal)
                .ToList(),
            PlayerBasedRaidKeys = playerBasedRaidKeys
                .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static key => key, StringComparer.Ordinal)
                .ToList()
        };

        return BuildHeader() + Serializer.Serialize(document);
    }

    private static void AddEnumGlobalKeys(Dictionary<string, GlobalKeyBuilder> globalKeys)
    {
        foreach (GlobalKeys key in Enum.GetValues(typeof(GlobalKeys)))
        {
            if (key == GlobalKeys.NonServerOption || key == GlobalKeys.Count)
            {
                continue;
            }

            AddGlobalKey(globalKeys, key.ToString(), EnumNamePriority);
        }
    }

    private static void AddWorldFallbackKeys(
        ZoneSystem zoneSystem,
        Dictionary<string, GlobalKeyBuilder> globalKeys)
    {
        foreach (KeyValuePair<string, string> entry in zoneSystem.m_globalKeysValues)
        {
            AddGlobalKey(
                globalKeys,
                entry.Key,
                WorldFallbackPriority,
                hasValue: !string.IsNullOrEmpty(entry.Value),
                isWorldFallback: true);
        }
    }

    private static void AddRaidKeys(
        RandEventSystem eventSystem,
        Dictionary<string, GlobalKeyBuilder> globalKeys,
        HashSet<string> playerBasedRaidKeys)
    {
        foreach (RandomEvent randomEvent in eventSystem.m_events)
        {
            if (randomEvent == null)
            {
                continue;
            }

            AddGlobalKeys(globalKeys, randomEvent.m_requiredGlobalKeys, RaidNamePriority);
            AddGlobalKeys(globalKeys, randomEvent.m_notRequiredGlobalKeys, RaidNamePriority);
            AddPersonalKeys(playerBasedRaidKeys, randomEvent.m_altRequiredPlayerKeysAny);
            AddPersonalKeys(playerBasedRaidKeys, randomEvent.m_altRequiredPlayerKeysAll);
            AddPersonalKeys(playerBasedRaidKeys, randomEvent.m_altNotRequiredPlayerKeys);
        }
    }

    private static void AddDefeatKeys(
        ZNetScene netScene,
        Dictionary<string, GlobalKeyBuilder> globalKeys)
    {
        foreach (GameObject prefab in ValheimNameUtils.EnumerateRegisteredPrefabs(netScene))
        {
            Character character = prefab.GetComponent<Character>();
            if ((Object?)character == null
                || !TryParseGlobalKey(character.m_defeatSetGlobalKey, out ParsedGlobalKey parsed))
            {
                continue;
            }

            GlobalKeyBuilder builder = GetOrCreate(globalKeys, parsed.Identity);
            builder.AddName(parsed.Spelling, DefeatKeyPriority, isWorldFallback: false);
            builder.HasValue |= parsed.HasValue;
            string prefabName = ValheimNameUtils.GetPrefabName(prefab);
            if (prefabName.Length > 0)
            {
                builder.DefeatPrefabs.Add(prefabName);
            }
        }
    }

    private static void AddObservedKeys(
        IEnumerable<ObservedGlobalKeySnapshot> observed,
        Dictionary<string, GlobalKeyBuilder> globalKeys)
    {
        foreach (ObservedGlobalKeySnapshot key in observed)
        {
            GlobalKeyBuilder builder = GetOrCreate(globalKeys, key.Identity);
            foreach (string spelling in key.Spellings)
            {
                builder.AddName(spelling, ObservedNamePriority, isWorldFallback: false);
            }

            builder.HasValue |= key.HasValue;
        }
    }

    private static void AddGlobalKeys(
        Dictionary<string, GlobalKeyBuilder> globalKeys,
        IEnumerable<string>? keys,
        int priority)
    {
        if (keys == null)
        {
            return;
        }

        foreach (string key in keys)
        {
            AddGlobalKey(globalKeys, key, priority);
        }
    }

    private static void AddPersonalKeys(HashSet<string> personalKeys, IEnumerable<string>? keys)
    {
        if (keys == null)
        {
            return;
        }

        foreach (string rawKey in keys)
        {
            string key = (rawKey ?? string.Empty).Trim();
            if (key.Length > 0)
            {
                personalKeys.Add(key);
            }
        }
    }

    private static void AddGlobalKey(
        Dictionary<string, GlobalKeyBuilder> globalKeys,
        string? rawKey,
        int priority,
        bool hasValue = false,
        bool isWorldFallback = false)
    {
        if (!TryParseGlobalKey(rawKey, out ParsedGlobalKey parsed))
        {
            return;
        }

        GlobalKeyBuilder builder = GetOrCreate(globalKeys, parsed.Identity);
        builder.AddName(parsed.Spelling, priority, isWorldFallback);
        builder.HasValue |= hasValue || parsed.HasValue;
    }

    private static GlobalKeyBuilder GetOrCreate(
        Dictionary<string, GlobalKeyBuilder> globalKeys,
        string identity)
    {
        if (!globalKeys.TryGetValue(identity, out GlobalKeyBuilder builder))
        {
            builder = new GlobalKeyBuilder(identity);
            globalKeys[identity] = builder;
        }

        return builder;
    }

    private static bool TryParseGlobalKey(string? rawKey, out ParsedGlobalKey parsed)
    {
        string raw = (rawKey ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            parsed = default;
            return false;
        }

        string identity = ZoneSystem.GetKeyValue(raw, out string value, out _);
        int separator = raw.IndexOf(' ');
        string spelling = (separator > 0 ? raw.Substring(0, separator) : raw).Trim();
        if (identity.Length == 0 || spelling.Length == 0)
        {
            parsed = default;
            return false;
        }

        if (Enum.TryParse(spelling, true, out GlobalKeys enumKey)
            && Enum.IsDefined(typeof(GlobalKeys), enumKey)
            && (enumKey == GlobalKeys.NonServerOption || enumKey == GlobalKeys.Count))
        {
            parsed = default;
            return false;
        }

        parsed = new ParsedGlobalKey(identity, spelling, !string.IsNullOrEmpty(value));
        return true;
    }

    private static string BuildHeader()
    {
        string newline = Environment.NewLine;
        return string.Join(newline, new[]
        {
            "# YouAreNotWorthy key reference",
            "# Generated from the effective loaded runtime. This file is overwritten automatically.",
            "# It is reference-only: YNW does not read it as configuration and does not ServerSync it.",
            "# Only the authoritative server/listen host writes it; remote clients leave local copies untouched.",
            "# This is a discovered catalog, not a world/player progress snapshot; values and presence are omitted.",
            "# globalKeys lists personalized entries first, followed by sharedWorld entries.",
            "#",
            "# YNW personalizes every value-less, non-reserved global key. Its world write is blocked,",
            $"# the same literal is granted as a native character unique key to active players within {PlayerKeys.PersonalKeyGrantRadius:0.##}m",
            "# of the event position, and ordinary global-key reads are evaluated against that character key.",
            "# Value-bearing keys and reserved modifiers/runtime state remain shared world keys.",
            "#",
            "# PlayerEvents OFF: raids use requiredGlobalKeys and forbiddenGlobalKeys. YNW evaluates",
            "# personalizable entries per character while shared entries remain world conditions.",
            "# PlayerEvents ON: when any alternate known-item/player-key list is non-empty, Vanilla uses",
            "# those character conditions instead. With all alternate lists empty, global conditions remain active.",
            "# playerBasedRaidKeys contains only native unique-key literals; known-item conditions are omitted.",
            "# Different literals are not aliases. Global keys are grouped case-insensitively, while player keys",
            "# retain exact casing. aliases records additional loaded spellings of the same global-key identity.",
            "# defeatPrefabs records only root Character.m_defeatSetGlobalKey setters.",
            "# Runtime fields and observed Get/Set calls are discoverable; a mod's dormant hard-coded key may",
            "# appear only after that code path runs. Reload the world after adding or removing content mods.",
            "#"
        }) + newline;
    }

    private static void MarkDirtyLocked()
    {
        _dirty = true;
        _revision++;
        _updateAfter = Time.realtimeSinceStartup + ObservationDebounceSeconds;
        _retryAfter = 0f;
    }

    private static void WriteIfChanged(string content)
    {
        Directory.CreateDirectory(ProgressionConfigLoader.ConfigDirectory);
        string path = ReferencePath;
        string existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        if (string.Equals(existing, content, StringComparison.Ordinal))
        {
            return;
        }

        File.WriteAllText(path, content, Utf8WithoutBom);
        YouAreNotWorthyPlugin.Log.LogInfo($"Updated key reference at {path}.");
    }

    private readonly struct ParsedGlobalKey
    {
        internal ParsedGlobalKey(string identity, string spelling, bool hasValue)
        {
            Identity = identity;
            Spelling = spelling;
            HasValue = hasValue;
        }

        internal string Identity { get; }
        internal string Spelling { get; }
        internal bool HasValue { get; }
    }

    private sealed class ObservedGlobalKey
    {
        private readonly HashSet<string> _spellings = new(StringComparer.Ordinal);

        internal ObservedGlobalKey(string identity)
        {
            Identity = identity;
        }

        internal string Identity { get; }
        internal bool HasValue { get; private set; }

        internal bool Add(string spelling, bool hasValue)
        {
            bool changed = _spellings.Add(spelling);
            if (hasValue && !HasValue)
            {
                HasValue = true;
                changed = true;
            }

            return changed;
        }

        internal ObservedGlobalKeySnapshot Snapshot()
        {
            return new ObservedGlobalKeySnapshot(
                Identity,
                _spellings.OrderBy(static value => value, StringComparer.Ordinal).ToList(),
                HasValue);
        }
    }

    private sealed class ObservedGlobalKeySnapshot
    {
        internal ObservedGlobalKeySnapshot(string identity, List<string> spellings, bool hasValue)
        {
            Identity = identity;
            Spellings = spellings;
            HasValue = hasValue;
        }

        internal string Identity { get; }
        internal List<string> Spellings { get; }
        internal bool HasValue { get; }
    }

    private sealed class GlobalKeyBuilder
    {
        private readonly Dictionary<string, NameCandidate> _names = new(StringComparer.Ordinal);

        internal GlobalKeyBuilder(string identity)
        {
            Identity = identity;
        }

        internal string Identity { get; }
        internal bool HasValue { get; set; }
        internal HashSet<string> DefeatPrefabs { get; } = new(StringComparer.Ordinal);

        internal void AddName(string name, int priority, bool isWorldFallback)
        {
            if (!_names.TryGetValue(name, out NameCandidate candidate)
                || priority < candidate.Priority
                || (candidate.IsWorldFallback && !isWorldFallback))
            {
                _names[name] = new NameCandidate(priority, isWorldFallback);
            }
        }

        internal GlobalKeyReferenceEntry Build()
        {
            KeyValuePair<string, NameCandidate> canonical = _names
                .OrderBy(static entry => entry.Value.Priority)
                .ThenBy(static entry => entry.Value.IsWorldFallback)
                .ThenBy(static entry => entry.Key, StringComparer.Ordinal)
                .FirstOrDefault();
            string canonicalName = canonical.Key ?? Identity;
            List<string> aliases = _names
                .Where(entry => !entry.Value.IsWorldFallback
                                && !string.Equals(entry.Key, canonicalName, StringComparison.Ordinal))
                .Select(static entry => entry.Key)
                .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static value => value, StringComparer.Ordinal)
                .ToList();
            List<string> defeatPrefabs = DefeatPrefabs
                .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static value => value, StringComparer.Ordinal)
                .ToList();

            return new GlobalKeyReferenceEntry
            {
                Key = canonicalName,
                Handling = HasValue || ProgressionIndex.IsSharedWorldKey(canonicalName)
                    ? SharedWorldHandling
                    : PersonalizedHandling,
                Aliases = aliases.Count > 0 ? aliases : null,
                DefeatPrefabs = defeatPrefabs.Count > 0 ? defeatPrefabs : null
            };
        }
    }

    private readonly struct NameCandidate
    {
        internal NameCandidate(int priority, bool isWorldFallback)
        {
            Priority = priority;
            IsWorldFallback = isWorldFallback;
        }

        internal int Priority { get; }
        internal bool IsWorldFallback { get; }
    }

    private sealed class KeyReferenceDocument
    {
        [YamlMember(Order = 1)]
        public List<GlobalKeyReferenceEntry> GlobalKeys { get; set; } = new();

        [YamlMember(Order = 2)]
        public List<string> PlayerBasedRaidKeys { get; set; } = new();
    }

    private sealed class GlobalKeyReferenceEntry
    {
        [YamlMember(Order = 1)]
        public string Key { get; set; } = string.Empty;

        [YamlMember(Order = 2)]
        public string Handling { get; set; } = string.Empty;

        [YamlMember(Order = 3)]
        public List<string>? Aliases { get; set; }

        [YamlMember(Order = 4)]
        public List<string>? DefeatPrefabs { get; set; }
    }
}
