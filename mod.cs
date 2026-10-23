
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace Shovelwaredev.eXPonential
{
    public record ModMetadata : IModMetadata
    {
        public string ModGuid { get; init; } = "com.shovelwaredev.exponential";
        public string Name { get; init; } = "eXPonential";
        public string Author { get; init; } = "ShovelwareDev";
        public List<string>? Contributors { get; init; }
        public Version Version { get; init; } = new("1.0.0");
        public Range SptVersion { get; init; } = new("~4.1.2");
        public List<string>? Incompatibilities { get; init; }
        public Dictionary<string, Range>? ModDependencies { get; init; }
        public string? Url { get; init; }
        public string License { get; init; } = "MIT";
        public bool HasPrepatcher { get; init; } = false;
    }

    public sealed class Config
    {
        public int Multiplier { get; set; } = 2;
    }

    [Injectable]
    public sealed class Mod : IOnLoad, IDisposable
    {
        private readonly GlobalTable _globalTable;
        private readonly TemplateTable _templateTable;
        private readonly string _configPath;

        private readonly Dictionary<string, int> _baseXp = new();
        private readonly Dictionary<string, int> _baseQuestXp = new();

        private readonly object _applyLock = new();
        private FileSystemWatcher? _watcher;
        private CancellationTokenSource? _reloadCts;
        private bool _disposed;

        private static readonly (string Key, string[] Path)[] GlobalXpPaths =
{
    ("expForLevelOneDogtag", new[] { "Exp", "ExpForLevelOneDogtag" }),
    ("expForLockedDoorBreach", new[] { "Exp", "ExpForLockedDoorBreach" }),
    ("expForLockedDoorOpen", new[] { "Exp", "ExpForLockedDoorOpen" }),

    ("heal_expForEnergy", new[] { "Exp", "Heal", "ExpForEnergy" }),
    ("heal_expForHeal", new[] { "Exp", "Heal", "ExpForHeal" }),
    ("heal_expForHydration", new[] { "Exp", "Heal", "ExpForHydration" }),

    ("kill_botExpOnDamageAllHealth",
        new[] { "Exp", "Kill", "BotExperienceOnDamageAllHealth" }),
    ("kill_pmcExpOnDamageAllHealth",
        new[] { "Exp", "Kill", "PmcExperienceOnDamageAllHealth" }),
    ("kill_victimBotLevelExp",
        new[] { "Exp", "Kill", "VictimBotLevelExperience" }),
    ("kill_victimLevelExp",
        new[] { "Exp", "Kill", "VictimLevelExperience" }),

    ("mia_exp_reward",
        new[] { "Exp", "MatchEnd", "MiaExperienceReward" }),
    ("runner_exp_reward",
        new[] { "Exp", "MatchEnd", "RunnerExperienceReward" }),
    ("survived_exp_reward",
        new[] { "Exp", "MatchEnd", "SurvivedExperienceReward" }),
    ("transit_exp_reward",
        new[] { "Exp", "MatchEnd", "TransitExperienceReward" })
};

        public Mod(GlobalTable globalTable, TemplateTable templateTable)
        {
            _globalTable = globalTable;
            _templateTable = templateTable;

            string assemblyDirectory =
                Path.GetDirectoryName(typeof(Mod).Assembly.Location)
                ?? AppContext.BaseDirectory;

            _configPath = Path.Combine(assemblyDirectory, "config.json");
            EnsureConfigExists();
        }

        public Task OnLoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_applyLock)
            {
                CacheBaseValues();
                ApplyMultipliersInternal(GetCurrentMultiplier());
            }

            SetupFileWatcher();
            return Task.CompletedTask;
        }

        private static string Normalize(string name) =>
            new(name.Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant).ToArray());

        private static object? GetMember(object? obj, params string[] names)
        {
            if (obj == null) return null;

            var wanted = names.Select(Normalize).ToHashSet();

            if (obj is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                    if (entry.Key != null &&
                        wanted.Contains(Normalize(entry.Key.ToString()!)))
                        return entry.Value;
            }

            Type type = obj.GetType();

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                if (property.GetIndexParameters().Length == 0 &&
                    wanted.Contains(Normalize(property.Name)))
                    return property.GetValue(obj);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
                if (wanted.Contains(Normalize(field.Name)))
                    return field.GetValue(obj);

            return null;
        }

        private static bool SetMember(object? obj, object? value, params string[] names)
        {
            if (obj == null) return false;

            var wanted = names.Select(Normalize).ToHashSet();

            if (obj is IDictionary dictionary)
            {
                foreach (object key in dictionary.Keys)
                {
                    if (wanted.Contains(Normalize(key.ToString()!)))
                    {
                        dictionary[key] = value;
                        return true;
                    }
                }
                return false;
            }

            Type type = obj.GetType();

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetIndexParameters().Length != 0 ||
                    !wanted.Contains(Normalize(property.Name)) ||
                    !property.CanWrite)
                    continue;

                property.SetValue(obj, ConvertValue(value, property.PropertyType));
                return true;
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (wanted.Contains(Normalize(field.Name)) && !field.IsInitOnly)
                {
                    field.SetValue(obj, ConvertValue(value, field.FieldType));
                    return true;
                }
            }

            return false;
        }

        private static object? ConvertValue(object? value, Type targetType)
        {
            if (value == null) return null;

            Type actualType = Nullable.GetUnderlyingType(targetType) ?? targetType;

            if (actualType.IsInstanceOfType(value)) return value;
            if (actualType.IsEnum) return Enum.Parse(actualType, value.ToString()!, true);

            return Convert.ChangeType(value, actualType);
        }

        private static object? GetPath(object? root, params string[] path)
        {
            object? current = root;

            foreach (string segment in path)
            {
                current = GetMember(current, segment);
                if (current == null) return null;
            }

            return current;
        }

        private static bool TryReadInt(object? obj, out int value, params string[] names)
        {
            object? raw = GetMember(obj, names);

            if (raw == null)
            {
                value = 0;
                return false;
            }

            try
            {
                value = Convert.ToInt32(raw);
                return true;
            }
            catch
            {
                value = 0;
                return false;
            }
        }

        private void EnsureConfigExists()
        {
            if (File.Exists(_configPath)) return;

            File.WriteAllText(_configPath,
                JsonSerializer.Serialize(new Config(),
                    new JsonSerializerOptions { WriteIndented = true }));
        }

        private int GetCurrentMultiplier()
        {
            try
            {
                var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(_configPath));

                if (config != null && config.Multiplier > 0)
                    return config.Multiplier;

                Console.WriteLine("[eXPonential] Multiplier must be greater than zero.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[eXPonential] Config read failed: {ex.Message}");
            }

            return 2;
        }

        private object? GetGlobalsConfiguration() =>
            GetMember(_globalTable, "Configuration", "Config", "Globals");

        private void CacheBaseValues()
        {
            _baseXp.Clear();
            _baseQuestXp.Clear();

            object? configuration = GetGlobalsConfiguration();

            if (configuration != null)
            {
                foreach (var entry in GlobalXpPaths)
                {
                    object? parent = GetPath(configuration, entry.Path[..^1]);

                    if (TryReadInt(parent, out int value, entry.Path[^1]))
                        _baseXp[entry.Key] = value;
                }
            }
            else
            {
                Console.WriteLine("[eXPonential] Could not find globals configuration.");
            }

            foreach (var pair in _templateTable.Quests)
            {
                object? rewards = GetMember(pair.Value, "Rewards", "rewards");
                if (rewards == null) continue;

                foreach (string phase in new[] { "Success", "Fail", "Start" })
                    CacheRewardList(pair.Key, phase, GetMember(rewards, phase));
            }

            Console.WriteLine(
                $"[eXPonential] Cached {_baseXp.Count} global XP values and {_baseQuestXp.Count} quest XP values.");
        }

        private void CacheRewardList(string questId, string phase, object? rewardList)
        {
            if (rewardList is not IEnumerable list || rewardList is string) return;

            int index = 0;

            foreach (object? reward in list)
            {
                if (reward != null &&
                    string.Equals(GetMember(reward, "Type")?.ToString(), "Experience",
                        StringComparison.OrdinalIgnoreCase) &&
                    TryReadInt(reward, out int xp, "Value"))
                    _baseQuestXp[$"{questId}_{phase}_{index}"] = xp;

                index++;
            }
        }

        private void ApplyMultipliers()
        {
            lock (_applyLock)
                ApplyMultipliersInternal(GetCurrentMultiplier());
        }

        private void ApplyMultipliersInternal(int multiplier)
        {
            object? configuration = GetGlobalsConfiguration();

            if (configuration != null)
            {
                foreach (var entry in GlobalXpPaths)
                {
                    if (!_baseXp.TryGetValue(entry.Key, out int baseValue)) continue;

                    object? parent = GetPath(configuration, entry.Path[..^1]);
                    SetMember(parent, baseValue * multiplier, entry.Path[^1]);
                }
            }

            foreach (var pair in _templateTable.Quests)
            {
                object? rewards = GetMember(pair.Value, "Rewards", "rewards");
                if (rewards == null) continue;

                foreach (string phase in new[] { "Success", "Fail", "Start" })
                    ApplyRewardList(pair.Key, phase, GetMember(rewards, phase), multiplier);
            }

            Console.WriteLine($"[eXPonential] Applied {multiplier}x experience multiplier.");
        }

        private void ApplyRewardList(string questId, string phase, object? rewardList, int multiplier)
        {
            if (rewardList is not IEnumerable list || rewardList is string) return;

            int index = 0;

            foreach (object? reward in list)
            {
                if (reward != null &&
                    string.Equals(GetMember(reward, "Type")?.ToString(), "Experience",
                        StringComparison.OrdinalIgnoreCase) &&
                    _baseQuestXp.TryGetValue($"{questId}_{phase}_{index}", out int baseXp))
                {
                    if (!SetMember(reward, baseXp * multiplier, "Value"))
                        Console.WriteLine($"[eXPonential] Could not update quest reward {questId}.");
                }

                index++;
            }
        }

        private void SetupFileWatcher()
        {
            string? directory = Path.GetDirectoryName(_configPath);
            if (string.IsNullOrEmpty(directory)) return;

            _watcher = new FileSystemWatcher(directory)
            {
                Filter = Path.GetFileName(_configPath),
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };

            _watcher.Changed += OnConfigChanged;
            _watcher.Created += OnConfigChanged;
            _watcher.Renamed += OnConfigRenamed;
            _watcher.EnableRaisingEvents = true;
        }

        private void OnConfigRenamed(object sender, RenamedEventArgs e) => ScheduleReload();
        private void OnConfigChanged(object sender, FileSystemEventArgs e) => ScheduleReload();

        private void ScheduleReload()
        {
            CancellationTokenSource cts;

            lock (_applyLock)
            {
                if (_disposed) return;

                _reloadCts?.Cancel();
                _reloadCts?.Dispose();
                _reloadCts = new CancellationTokenSource();
                cts = _reloadCts;
            }

            _ = ReloadAfterDelayAsync(cts);
        }

        private async Task ReloadAfterDelayAsync(CancellationTokenSource cts)
        {
            try
            {
                await Task.Delay(300, cts.Token);
                if (!cts.IsCancellationRequested) ApplyMultipliers();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[eXPonential] Config reload failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            lock (_applyLock)
            {
                _disposed = true;
                _watcher?.Dispose();
                _watcher = null;
                _reloadCts?.Cancel();
                _reloadCts?.Dispose();
                _reloadCts = null;
            }
        }
    }
}
