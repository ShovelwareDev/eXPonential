using System;
using System.Collections.Generic;
using System.IO;
using IOPath = System.IO.Path;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
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
        public Version Version { get; init; } = new("2.0.0");
        public Range SptVersion { get; init; } = new("~4.1.2");
        public List<string>? Incompatibilities { get; init; }
        public Dictionary<string, Range>? ModDependencies { get; init; }
        public string? Url { get; init; }
        public string License { get; init; } = "MIT";
        public bool HasPrepatcher { get; init; }
    }

    public sealed class Config
    {
        public double Multiplier { get; set; } = 1.0;
        public bool DebugLogging { get; set; }
    }

    [Injectable]
    public sealed class Mod : IOnLoad, IDisposable
    {
        private const string Tag = "[eXPonential]";
        private static readonly string[] RewardPhases = { "Success", "Fail", "Start" };

        // Path is relative to globals.Configuration. WholeNumber rounds before assignment.
        private readonly record struct XpPath(string Key, string Path, bool WholeNumber = false);
        private static readonly XpPath[] GlobalXpPaths =
        {
            new("expForLevelOneDogtag", "Exp.ExpForLevelOneDogtag"),
            new("expForLockedDoorBreach", "Exp.ExpForLockedDoorBreach", true),
            new("expForLockedDoorOpen", "Exp.ExpForLockedDoorOpen", true),
            new("heal_expForEnergy", "Exp.Heal.ExpForEnergy"),
            new("heal_expForHeal", "Exp.Heal.ExpForHeal"),
            new("heal_expForHydration", "Exp.Heal.ExpForHydration"),
            new("kill_botExpOnDamageAllHealth", "Exp.Kill.BotExperienceOnDamageAllHealth", true),
            new("kill_pmcExpOnDamageAllHealth", "Exp.Kill.PmcExperienceOnDamageAllHealth", true),
            new("kill_victimBotLevelExp", "Exp.Kill.VictimBotLevelExperience", true),
            new("kill_victimLevelExp", "Exp.Kill.VictimLevelExperience", true),
            new("mia_exp_reward", "Exp.MatchEnd.MiaExperienceReward", true),
            new("runner_exp_reward", "Exp.MatchEnd.RunnerExperienceReward", true),
            new("survived_exp_reward", "Exp.MatchEnd.SurvivedExperienceReward", true),
            new("transit_exp_reward", "Exp.MatchEnd.TransitExperienceReward", true),
            new("health_breakPart", "Health.Effects.BreakPart.HealExperience", true),
            new("health_fracture", "Health.Effects.Fracture.HealExperience", true),
            new("health_heavyBleeding", "Health.Effects.HeavyBleeding.HealExperience", true),
            new("health_intoxication", "Health.Effects.Intoxication.HealExperience", true),
            new("health_lightBleeding", "Health.Effects.LightBleeding.HealExperience", true),
            new("health_pain", "Health.Effects.Pain.HealExperience", true)
        };

        private readonly GlobalTable _globalTable;
        private readonly TemplateTable _templateTable;
        private readonly string _configPath;
        private readonly Dictionary<string, double> _baseXp = new();
        private readonly Dictionary<string, double> _baseQuestXp = new();
        private readonly Dictionary<int, double> _baseLootAttempts = new();
        private readonly object _applyLock = new();
        private FileSystemWatcher? _watcher;
        private CancellationTokenSource? _reloadCts;
        private bool _disposed;
        private double _lastGoodMultiplier = 1.0;
        private bool _debug;

        public Mod(GlobalTable globalTable, TemplateTable templateTable)
        {
            _globalTable = globalTable;
            _templateTable = templateTable;
            string directory = IOPath.GetDirectoryName(typeof(Mod).Assembly.Location) ?? AppContext.BaseDirectory;
            _configPath = IOPath.Combine(directory, "config.json");
            EnsureConfigExists();
        }

        public Task OnLoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_applyLock)
            {
                Config config = ReadConfig();
                _debug = config.DebugLogging;
                CacheBaseValues();
                ApplyMultipliersInternal(config);
            }
            SetupFileWatcher();
            return Task.CompletedTask;
        }

        // All XP paths below use the concrete SPT 4.1 model types; no reflection is needed.
        private double? ReadGlobalXp(string key)
        {
            var config = _globalTable.Configuration;
            return key switch
            {
                "expForLevelOneDogtag" => config.Exp.ExpForLevelOneDogtag,
                "expForLockedDoorBreach" => config.Exp.ExpForLockedDoorBreach,
                "expForLockedDoorOpen" => config.Exp.ExpForLockedDoorOpen,
                "heal_expForEnergy" => config.Exp.Heal.ExpForEnergy,
                "heal_expForHeal" => config.Exp.Heal.ExpForHeal,
                "heal_expForHydration" => config.Exp.Heal.ExpForHydration,
                "kill_botExpOnDamageAllHealth" => config.Exp.Kill.BotExperienceOnDamageAllHealth,
                "kill_pmcExpOnDamageAllHealth" => config.Exp.Kill.PmcExperienceOnDamageAllHealth,
                "kill_victimBotLevelExp" => config.Exp.Kill.VictimBotLevelExperience,
                "kill_victimLevelExp" => config.Exp.Kill.VictimLevelExperience,
                "mia_exp_reward" => config.Exp.MatchEnd.MiaExperienceReward,
                "runner_exp_reward" => config.Exp.MatchEnd.RunnerExperienceReward,
                "survived_exp_reward" => config.Exp.MatchEnd.SurvivedExperienceReward,
                "transit_exp_reward" => config.Exp.MatchEnd.TransitExperienceReward,
                "health_breakPart" => config.Health.Effects.BreakPart.HealExperience,
                "health_fracture" => config.Health.Effects.Fracture.HealExperience,
                "health_heavyBleeding" => config.Health.Effects.HeavyBleeding.HealExperience,
                "health_intoxication" => config.Health.Effects.Intoxication.HealExperience,
                "health_lightBleeding" => config.Health.Effects.LightBleeding.HealExperience,
                "health_pain" => config.Health.Effects.Pain.HealExperience,
                _ => null
            };
        }

        private void WriteGlobalXp(string key, double value)
        {
            var config = _globalTable.Configuration;
            switch (key)
            {
                case "expForLevelOneDogtag": config.Exp.ExpForLevelOneDogtag = value; break;
                case "expForLockedDoorBreach": config.Exp.ExpForLockedDoorBreach = (int)value; break;
                case "expForLockedDoorOpen": config.Exp.ExpForLockedDoorOpen = (int)value; break;
                case "heal_expForEnergy": config.Exp.Heal.ExpForEnergy = value; break;
                case "heal_expForHeal": config.Exp.Heal.ExpForHeal = value; break;
                case "heal_expForHydration": config.Exp.Heal.ExpForHydration = value; break;
                case "kill_botExpOnDamageAllHealth": config.Exp.Kill.BotExperienceOnDamageAllHealth = value; break;
                case "kill_pmcExpOnDamageAllHealth": config.Exp.Kill.PmcExperienceOnDamageAllHealth = value; break;
                case "kill_victimBotLevelExp": config.Exp.Kill.VictimBotLevelExperience = value; break;
                case "kill_victimLevelExp": config.Exp.Kill.VictimLevelExperience = value; break;
                case "mia_exp_reward": config.Exp.MatchEnd.MiaExperienceReward = (int)value; break;
                case "runner_exp_reward": config.Exp.MatchEnd.RunnerExperienceReward = (int)value; break;
                case "survived_exp_reward": config.Exp.MatchEnd.SurvivedExperienceReward = (int)value; break;
                case "transit_exp_reward": config.Exp.MatchEnd.TransitExperienceReward = value; break;
                case "health_breakPart": config.Health.Effects.BreakPart.HealExperience = value; break;
                case "health_fracture": config.Health.Effects.Fracture.HealExperience = value; break;
                case "health_heavyBleeding": config.Health.Effects.HeavyBleeding.HealExperience = value; break;
                case "health_intoxication": config.Health.Effects.Intoxication.HealExperience = value; break;
                case "health_lightBleeding": config.Health.Effects.LightBleeding.HealExperience = value; break;
                case "health_pain": config.Health.Effects.Pain.HealExperience = value; break;
            }
        }

        private void EnsureConfigExists()
        {
            if (!File.Exists(_configPath))
                File.WriteAllText(_configPath, JsonSerializer.Serialize(new Config(), new JsonSerializerOptions { WriteIndented = true }));
        }

        private Config ReadConfig()
        {
            try
            {
                Config? config = JsonSerializer.Deserialize<Config>(File.ReadAllText(_configPath));
                if (config != null && double.IsFinite(config.Multiplier) && config.Multiplier > 0)
                {
                    _lastGoodMultiplier = config.Multiplier;
                    return config;
                }
                Log($"Invalid multiplier; keeping {_lastGoodMultiplier}x.");
            }
            catch (Exception ex) { Log($"Config read failed ({ex.Message}); keeping {_lastGoodMultiplier}x."); }
            return new Config { Multiplier = _lastGoodMultiplier, DebugLogging = _debug };
        }

        private void Log(string message) => Console.WriteLine($"{Tag} {message}");
        private void Debug(string message) { if (_debug) Log(message); }

        private void CacheBaseValues()
        {
            _baseXp.Clear();
            _baseQuestXp.Clear();
            _baseLootAttempts.Clear();

            foreach (XpPath entry in GlobalXpPaths)
            {
                double? value = ReadGlobalXp(entry.Key);
                if (value.HasValue)
                {
                    _baseXp[entry.Key] = value.Value;
                    Debug($"Cached {entry.Key}={value.Value}.");
                }
                else Debug($"XP field not found: {entry.Path}.");
            }

            int index = 0;
            foreach (var attempt in _globalTable.Configuration.Exp.LootAttempts)
            {
                _baseLootAttempts[index] = attempt.ExperiencePoints;
                Debug($"Cached loot_attempts[{index}].ExperiencePoints={attempt.ExperiencePoints}.");
                index++;
            }

            foreach (var quest in _templateTable.Quests)
            {
                if (quest.Value.Rewards == null) continue;
                foreach (string phase in RewardPhases)
                    if (quest.Value.Rewards.TryGetValue(phase, out List<Reward>? rewards))
                        CacheRewardList(quest.Key.ToString(), phase, rewards);
            }

            Log($"Cached {_baseXp.Count} global XP values, {_baseLootAttempts.Count} loot attempts, and {_baseQuestXp.Count} quest XP values.");
        }

        private void CacheRewardList(string questId, string phase, IEnumerable<Reward> rewards)
        {
            int index = 0;
            foreach (Reward reward in rewards)
            {
                if (reward.Type == RewardType.Experience && reward.Value.HasValue)
                    _baseQuestXp[RewardKey(questId, phase, index)] = reward.Value.Value;
                index++;
            }
        }

        private static string RewardKey(string questId, string phase, int index) => $"{questId}_{phase}_{index}";

        private void ApplyMultipliers()
        {
            lock (_applyLock)
            {
                if (_disposed) return;
                Config config = ReadConfig();
                _debug = config.DebugLogging;
                ApplyMultipliersInternal(config);
            }
        }

        private void ApplyMultipliersInternal(Config config)
        {
            double multiplier = config.Multiplier;
            foreach (XpPath entry in GlobalXpPaths)
            {
                if (!_baseXp.TryGetValue(entry.Key, out double baseValue)) continue;
                double value = baseValue * multiplier;
                if (entry.WholeNumber) value = Math.Round(value, MidpointRounding.AwayFromZero);
                WriteGlobalXp(entry.Key, value);
                Debug($"{entry.Key}: {baseValue} -> {value}.");
            }

            int index = 0;
            foreach (var attempt in _globalTable.Configuration.Exp.LootAttempts)
            {
                if (_baseLootAttempts.TryGetValue(index, out double baseValue))
                {
                    double value = baseValue * multiplier;
                    attempt.ExperiencePoints = value;
                    Debug($"Loot attempt {index}: {baseValue} -> {value}.");
                }
                index++;
            }

            foreach (var quest in _templateTable.Quests)
            {
                if (quest.Value.Rewards == null) continue;
                foreach (string phase in RewardPhases)
                    if (quest.Value.Rewards.TryGetValue(phase, out List<Reward>? rewards))
                        ApplyRewardList(quest.Key.ToString(), phase, rewards, multiplier);
            }
            Log($"Applied {multiplier}x experience multiplier.");
        }

        private void ApplyRewardList(string questId, string phase, IEnumerable<Reward> rewards, double multiplier)
        {
            int index = 0;
            foreach (Reward reward in rewards)
            {
                if (reward.Type == RewardType.Experience &&
                    _baseQuestXp.TryGetValue(RewardKey(questId, phase, index), out double baseXp))
                    reward.Value = baseXp * multiplier;
                index++;
            }
        }

        private void SetupFileWatcher()
        {
            string? directory = IOPath.GetDirectoryName(_configPath);
            if (string.IsNullOrEmpty(directory)) return;
            _watcher = new FileSystemWatcher(directory, IOPath.GetFileName(_configPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = false
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
                _reloadCts = cts = new CancellationTokenSource();
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
            catch (Exception ex) { Log($"Config reload failed: {ex.Message}"); }
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
