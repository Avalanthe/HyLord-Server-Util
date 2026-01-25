using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HyLordServerUtil
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private bool modsMoveEnabled = true;
        public bool ModsMoveEnabled
        {
            get => modsMoveEnabled;
            set
            {
                if (modsMoveEnabled == value) return;
                modsMoveEnabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ModsMoveEnabled)));
            }
        }
        private readonly ServerProcess server;

        private volatile bool showInfo = true;
        private volatile bool showWarn = true;
        private volatile bool showError = true;
        private volatile bool showTimestamps = true;

        private readonly ObservableCollection<PlayerInfo> players = new();
        private readonly SessionStore sessionStore = new("player_sessions.json");
        private DispatcherTimer? sessionTickTimer;
        private int sessionPeakPlayers = 0;

        private readonly List<LogEntry> logBuffer = new();

        private ServerConfig config = null!;
        private readonly string configPath = "config.json";
        private int networkPort = 5520;

        private Timer? autoRestartTimer;
        private DateTime? nextAutoRestartUtc;
        private volatile bool autoRestartArmed = false;
        private bool restartInProgress = false;
        private Timer? autoRestartArmTimer;
        private DispatcherTimer? autoRestartCountdownUi;
        private DateTime? nextAutoRestartLocal;
        private int countdownSecondsRemaining = 0;
        private const int AutoRestartCountdownSeconds = 600;
        private int lastWarnSecond = -1;

        private ServerConfig? worldConfig;
        private readonly string worldConfigPath = @".\universe\worlds\default\config.json";

        private readonly string playersFolder = @".\universe\players";       
        private readonly string permissionsPath = "permissions.json";


        public enum ServerStatus
        {
            Offline,
            Starting,
            Online,
            Crashed
        }
        private ServerStatus serverStatus = ServerStatus.Offline;


        private DispatcherTimer? performanceTimer;
        private readonly TimeSpan perfWarmup = TimeSpan.FromSeconds(30);
        private DateTime perfStartUtc = DateTime.MinValue;
        private bool IsPerfWarmup =>
            perfStartUtc != DateTime.MinValue &&
            (DateTime.UtcNow - perfStartUtc) < perfWarmup;

        private TimeSpan lastCpuTime;
        private DateTime lastCpuCheck;

        private int maxPlayers = 0;

        private const int PerfSeconds = 300;
        private readonly Queue<double> cpuHistory = new();
        private readonly Queue<double> memHistory = new();

        private double cpuPeak = 0;
        private double cpuAvg = 0;
        private double memPeak = 0;

        private DateTime? serverStartTime = null;

        private readonly ObservableCollection<ModInfo> loadedMods = new();
        private readonly ObservableCollection<ModInfo> unloadedMods = new();

        private readonly string modsFolder = @".\mods";
        private readonly string modsUnloadedFolder = @".\mods_unloaded";

        private bool modsBannerVisible;
        public bool ModsBannerVisible
        {
            get => modsBannerVisible;
            set
            {
                if (modsBannerVisible == value) return;
                modsBannerVisible = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ModsBannerVisible)));
            }
        }

        private readonly ObservableCollection<BanEntry> bans = new();
        private readonly string bansPath = @".\bans.json";

        private bool bansBannerVisible;
        public bool BansBannerVisible
        {
            get => bansBannerVisible;
            set
            {
                if (bansBannerVisible == value) return;
                bansBannerVisible = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(BansBannerVisible)));
            }
        }



        private readonly ObservableCollection<BackupInfo> backups = new();
        private readonly string backupsFolder = @".\backups";
        private readonly string universeFolder = @".\universe";

        private Timer? autoBackupArmTimer;
        private DispatcherTimer? autoBackupCountdownUi;
        private DateTime? nextAutoBackupLocal;
        private int backupCountdownSecondsRemaining = 0;

        private const int AutoBackupCountdownSeconds = 0;








        public MainWindow()
        {
            InitializeComponent();
            UpdateServerStatusUI();
            DataContext = this;

            EnsureFoldersExist();
            EnsureConfigsExist();
            EnsureBansFileExists();

            LoadConfig();
            LoadWorldConfig();
            
            InitAutoRestartControls();

            BansList.ItemsSource = bans;
            LoadBans();

            PlayerGrid.ItemsSource = players;
            SetupPlayerSorting();

            sessionTickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            sessionTickTimer.Tick += (_, _) =>
            {
                foreach (var p in players)
                    p.Tick();
            };
            sessionTickTimer.Start();

            server = new ServerProcess();
            HookServerEvents();

            SetupPerformanceTimer();
            HookConsoleFilters();

            LoadedModsList.ItemsSource = loadedMods;
            UnloadedModsList.ItemsSource = unloadedMods;

            _ = LoadModsAsync();
            ModsMoveEnabled = true;
            ModsBannerVisible = false;

            BackupsList.ItemsSource = backups;
            InitAutoBackupControls();
            LoadAutoBackupFromConfig();
            ScheduleAutoBackup();
            _ = LoadBackupsAsync();

            server.AuthRequired += OnAuthRequired;
            server.AuthUrlReceived += OnAuthUrlReceived;
            server.AuthSucceeded += OnAuthSucceeded;

        }

        private async void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (server?.IsRunning == true)
            {
                var result = MessageBox.Show(
                    "The server is still running. Stop it before closing?",
                    "Confirm Exit",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }
            if (server?.Process == null || server.Process.HasExited)
                return;

            AppendLog("Main window closing — stopping server...", LogType.Warning);

            try
            {
                server.SendCommand("stop");

                const int timeoutMs = 8000;
                var exited = await Task.Run(() =>
                    server.Process.WaitForExit(timeoutMs));

                if (!exited)
                {
                    AppendLog("Server did not exit in time — forcing termination", LogType.Error);

                    try
                    {
                        server.Process.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"Failed to force-kill server: {ex.Message}", LogType.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Error while stopping server: {ex.Message}", LogType.Error);
            }
        }




        private void DigitsOnly_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            e.Handled = !e.Text.All(char.IsDigit);
        }


        private void LoadConfig()
        {
            if (!File.Exists(configPath))
            {
                AppendLog("config.json not found", LogType.Error);
                return;
            }

            config = ServerConfig.Load(configPath);

            CfgServerName.Text = config.GetString("ServerName");
            ServerNameDisplay.Text = config.GetString("ServerName");
            CfgMotd.Text = config.GetString("MOTD");
            CfgPassword.Password = config.GetString("Password");

            CfgMaxPlayers.Text = config.GetInt("MaxPlayers").ToString();
            maxPlayers = int.TryParse(CfgMaxPlayers.Text, out var mp) ? mp : 100;

            CfgViewRadius.Text = config.GetInt("MaxViewRadius").ToString();

            CfgNetworkPort.Text = config.GetInt("NetworkPort").ToString();
            networkPort = int.TryParse(CfgNetworkPort.Text, out var port) ? port : 5520;
            UpdatePlayerCount(); 
            
            LoadAutoRestartFromConfig();
            ScheduleAutoRestart();

        }

        private void SaveConfig()
        {

            config.SetString("ServerName", CfgServerName.Text);
            ServerNameDisplay.Text = CfgServerName.Text;
            config.SetString("MOTD", CfgMotd.Text);
            config.SetString("Password", CfgPassword.Password);

            config.SetInt("MaxPlayers", int.TryParse(CfgMaxPlayers.Text, out var mp) ? mp : 100);
            maxPlayers = int.TryParse(CfgMaxPlayers.Text, out var mp2) ? mp2 : 100;

            config.SetInt("MaxViewRadius", int.TryParse(CfgViewRadius.Text, out var vr) ? vr : 32);

            networkPort = int.TryParse(CfgNetworkPort.Text, out var port) ? port : 5520;
            if (networkPort < 1 || networkPort > 65535) networkPort = 5520;
            config.SetInt("NetworkPort", networkPort);

            config.Save(configPath);
            UpdatePlayerCount();

            SaveAutoRestartToConfig();
            SaveAutoBackupToConfig();
            ScheduleAutoRestart();

        }

        private void ConfigReload_Click(object sender, RoutedEventArgs e)
        {
            LoadConfig();
            AppendLog("Config reloaded", LogType.Info);
        }

        private void ConfigSave_Click(object sender, RoutedEventArgs e)
        {
            SaveConfig();
            AppendLog("Config saved", LogType.Info);
        }

        private void ConfigSaveRestart_Click(object sender, RoutedEventArgs e)
        {
            SaveConfig();
            AppendLog("Config saved. Restarting server...", LogType.Warning);
            server.Restart();
        }

        private void LoadWorldConfig()
        {
            if (!File.Exists(worldConfigPath))
            {
                AppendLog($"World config not found: {worldConfigPath}", LogType.Error);
                return;
            }

            worldConfig = ServerConfig.Load(worldConfigPath);

            var seedNode = worldConfig.GetNode("Seed");
            WCfgSeed.Text = seedNode?.ToJsonString().Trim('"') ?? "";

            WCfgIsPvpEnabled.IsChecked = worldConfig.GetBool("IsPvpEnabled");
            WCfgIsFallDamageEnabled.IsChecked = worldConfig.GetBool("IsFallDamageEnabled");
            WCfgIsSpawningNPC.IsChecked = worldConfig.GetBool("IsSpawningNPC");
            WCfgIsSpawnMarkersEnabled.IsChecked = worldConfig.GetBool("IsSpawnMarkersEnabled");
            WCfgIsCompassUpdating.IsChecked = worldConfig.GetBool("IsCompassUpdating");
            WCfgIsSavingPlayers.IsChecked = worldConfig.GetBool("IsSavingPlayers");
            WCfgIsSavingChunks.IsChecked = worldConfig.GetBool("IsSavingChunks");
            WCfgSaveNewChunks.IsChecked = worldConfig.GetBool("SaveNewChunks");
            WCfgIsUnloadingChunks.IsChecked = worldConfig.GetBool("IsUnloadingChunks");
            WCfgIsObjectiveMarkersEnabled.IsChecked = worldConfig.GetBool("IsObjectiveMarkersEnabled");

            AppendLog("World config loaded", LogType.Info);
        }

        private void SaveWorldConfig()
        {
            if (worldConfig == null)
            {
                AppendLog("World config not loaded; cannot save.", LogType.Error);
                return;
            }

            var seedText = (WCfgSeed.Text ?? "").Trim();
            if (long.TryParse(seedText, out var seedLong))
                worldConfig.SetNode("Seed", JsonValue.Create(seedLong));
            else
                worldConfig.SetNode("Seed", JsonValue.Create(seedText));

            worldConfig.SetBool("IsPvpEnabled", WCfgIsPvpEnabled.IsChecked == true);
            worldConfig.SetBool("IsFallDamageEnabled", WCfgIsFallDamageEnabled.IsChecked == true);
            worldConfig.SetBool("IsSpawningNPC", WCfgIsSpawningNPC.IsChecked == true);
            worldConfig.SetBool("IsSpawnMarkersEnabled", WCfgIsSpawnMarkersEnabled.IsChecked == true);
            worldConfig.SetBool("IsCompassUpdating", WCfgIsCompassUpdating.IsChecked == true);
            worldConfig.SetBool("IsSavingPlayers", WCfgIsSavingPlayers.IsChecked == true);
            worldConfig.SetBool("IsSavingChunks", WCfgIsSavingChunks.IsChecked == true);
            worldConfig.SetBool("SaveNewChunks", WCfgSaveNewChunks.IsChecked == true);
            worldConfig.SetBool("IsUnloadingChunks", WCfgIsUnloadingChunks.IsChecked == true);
            worldConfig.SetBool("IsObjectiveMarkersEnabled", WCfgIsObjectiveMarkersEnabled.IsChecked == true);

            worldConfig.Save(worldConfigPath);
            AppendLog("World config saved", LogType.Info);
        }
        private void WorldConfigReload_Click(object sender, RoutedEventArgs e)
        {
            LoadWorldConfig();
        }

        private void WorldConfigSave_Click(object sender, RoutedEventArgs e)
        {
            SaveWorldConfig();
        }
        private void SyncOpsFromPermissions()
        {
            try
            {
                if (!File.Exists(permissionsPath))
                    return;

                var json = File.ReadAllText(permissionsPath);
                var root = JsonNode.Parse(json) as JsonObject;
                if (root == null) return;

                var users = root["users"] as JsonObject;
                if (users == null) return;

                var opByHash = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

                foreach (var kv in users)
                {
                    var hash = kv.Key;
                    if (kv.Value is not JsonObject userObj)
                        continue;

                    var groups = userObj["groups"] as JsonArray;
                    if (groups == null)
                    {
                        opByHash[hash] = false;
                        continue;
                    }

                    bool isOp = groups.Any(g =>
                    {
                        try
                        {
                            var s = g?.GetValue<string>();
                            return string.Equals(s, "OP", StringComparison.OrdinalIgnoreCase);
                        }
                        catch { return false; }
                    });

                    opByHash[hash] = isOp;
                }

                foreach (var p in players)
                {
                    if (string.IsNullOrWhiteSpace(p.Hash)) continue;

                    if (opByHash.TryGetValue(p.Hash, out bool isOp))
                        p.IsOp = isOp;
                    else
                        p.IsOp = false;
                }

                RefreshPlayerSort();
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to read permissions.json: {ex.Message}", LogType.Warning);
            }
        }





        private void InitAutoRestartControls()
        {
            CfgAutoRestartHour.Items.Clear();
            for (int h = 0; h < 24; h++)
                CfgAutoRestartHour.Items.Add(h.ToString("00"));

            CfgAutoRestartMinute.Items.Clear();
            for (int m = 0; m < 60; m += 5)
                CfgAutoRestartMinute.Items.Add(m.ToString("00"));

            CfgAutoRestartDay.Items.Clear();
            foreach (var d in Enum.GetValues(typeof(DayOfWeek)))
                CfgAutoRestartDay.Items.Add(d!.ToString()!);

            CfgAutoRestartMode.SelectedIndex = 0;
            CfgAutoRestartHour.SelectedItem = "03";
            CfgAutoRestartMinute.SelectedItem = "00";
            CfgAutoRestartDay.SelectedItem = DayOfWeek.Sunday.ToString();

            UpdateAutoRestartUiState();
        }
        private void LoadAutoRestartFromConfig()
        {
            if (config == null) return;

            var arNode = config.GetNode("AutoRestart") as JsonObject;
            bool enabled = arNode?["Enabled"]?.GetValue<bool>() ?? false;
            string mode = arNode?["Mode"]?.GetValue<string>() ?? "Daily";
            string time = arNode?["Time"]?.GetValue<string>() ?? "03:00";
            string day = arNode?["DayOfWeek"]?.GetValue<string>() ?? "Sunday";

            CfgAutoRestartEnabled.IsChecked = enabled;

            CfgAutoRestartMode.SelectedIndex = (mode.Equals("Weekly", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;

            var parts = time.Split(':');
            var hh = (parts.Length > 0) ? parts[0].PadLeft(2, '0') : "03";
            var mm = (parts.Length > 1) ? parts[1].PadLeft(2, '0') : "00";

            if (CfgAutoRestartHour.Items.Contains(hh)) CfgAutoRestartHour.SelectedItem = hh;
            else CfgAutoRestartHour.SelectedItem = "03";

            if (CfgAutoRestartMinute.Items.Contains(mm)) CfgAutoRestartMinute.SelectedItem = mm;
            else CfgAutoRestartMinute.SelectedItem = "00";

            if (CfgAutoRestartDay.Items.Contains(day)) CfgAutoRestartDay.SelectedItem = day;
            else CfgAutoRestartDay.SelectedItem = DayOfWeek.Sunday.ToString();

            UpdateAutoRestartUiState();
        }
        private void SaveAutoRestartToConfig()
        {
            if (config == null) return;

            var enabled = CfgAutoRestartEnabled.IsChecked == true;
            var mode = (CfgAutoRestartMode.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Daily";
            var hh = CfgAutoRestartHour.SelectedItem?.ToString() ?? "03";
            var mm = CfgAutoRestartMinute.SelectedItem?.ToString() ?? "00";
            var day = CfgAutoRestartDay.SelectedItem?.ToString() ?? DayOfWeek.Sunday.ToString();

            var ar = new JsonObject
            {
                ["Enabled"] = enabled,
                ["Mode"] = mode,
                ["Time"] = $"{hh}:{mm}",
                ["DayOfWeek"] = day
            };

            config.SetNode("AutoRestart", ar);
        }
        private void AutoRestart_Changed(object sender, RoutedEventArgs e)
        {
            UpdateAutoRestartUiState();

            ScheduleAutoRestart();
        }

        private void UpdateAutoRestartUiState()
        {
            var mode = (CfgAutoRestartMode.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Daily";
            bool weekly = mode.Equals("Weekly", StringComparison.OrdinalIgnoreCase);

            CfgAutoRestartDay.IsEnabled = weekly && (CfgAutoRestartEnabled.IsChecked == true);
            CfgAutoRestartMode.IsEnabled = (CfgAutoRestartEnabled.IsChecked == true);
            CfgAutoRestartHour.IsEnabled = (CfgAutoRestartEnabled.IsChecked == true);
            CfgAutoRestartMinute.IsEnabled = (CfgAutoRestartEnabled.IsChecked == true);

            if (!weekly)
                CfgAutoRestartDay.IsEnabled = false;
        }
        private void ScheduleAutoRestart()
        {
            autoRestartTimer?.Dispose();
            autoRestartTimer = null;

            autoRestartArmTimer?.Dispose();
            autoRestartArmTimer = null;

            autoRestartCountdownUi?.Stop();
            autoRestartCountdownUi = null;

            nextAutoRestartUtc = null;
            nextAutoRestartLocal = null;
            countdownSecondsRemaining = 0;
            autoRestartArmed = false;

            if (CfgAutoRestartEnabled.IsChecked != true)
            {
                CfgNextAutoRestart.Text = "Next: --";
                return;
            }

            var mode = (CfgAutoRestartMode.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Daily";
            var hh = int.TryParse(CfgAutoRestartHour.SelectedItem?.ToString(), out var h) ? h : 3;
            var mm = int.TryParse(CfgAutoRestartMinute.SelectedItem?.ToString(), out var m) ? m : 0;

            var nowLocal = DateTime.Now;
            DateTime nextLocal;

            if (mode.Equals("Weekly", StringComparison.OrdinalIgnoreCase))
            {
                var dayStr = CfgAutoRestartDay.SelectedItem?.ToString() ?? DayOfWeek.Sunday.ToString();
                _ = Enum.TryParse(dayStr, out DayOfWeek targetDay);

                nextLocal = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, hh, mm, 0);

                int daysAhead = ((int)targetDay - (int)nextLocal.DayOfWeek + 7) % 7;
                if (daysAhead == 0 && nextLocal <= nowLocal)
                    daysAhead = 7;

                nextLocal = nextLocal.AddDays(daysAhead);
            }
            else
            {
                nextLocal = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, hh, mm, 0);
                if (nextLocal <= nowLocal)
                    nextLocal = nextLocal.AddDays(1);
            }

            nextAutoRestartLocal = nextLocal;
            nextAutoRestartUtc = nextLocal.ToUniversalTime();
            autoRestartArmed = true;

            UpdateNextAutoRestartLabel();

            var due = nextAutoRestartUtc.Value - DateTime.UtcNow;
            if (due < TimeSpan.Zero) due = TimeSpan.Zero;
            if (due.TotalSeconds <= AutoRestartCountdownSeconds)
            {
                StartAutoRestartCountdown((int)Math.Ceiling(due.TotalSeconds));
                return;
            }
            var armDelay = due - TimeSpan.FromSeconds(AutoRestartCountdownSeconds);
            if (armDelay < TimeSpan.Zero) armDelay = TimeSpan.Zero;

            autoRestartArmTimer = new Timer(_ =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (CfgAutoRestartEnabled.IsChecked != true)
                        return;

                    StartAutoRestartCountdown(AutoRestartCountdownSeconds);
                }));
            }, null, armDelay, Timeout.InfiniteTimeSpan);
        }

        private void UpdateNextAutoRestartLabel()
        {
            if (!nextAutoRestartLocal.HasValue)
            {
                CfgNextAutoRestart.Text = "Next: --";
                return;
            }
            if (countdownSecondsRemaining > 0)
            {
                var t = TimeSpan.FromSeconds(countdownSecondsRemaining);
                CfgNextAutoRestart.Text = $"Next: {nextAutoRestartLocal.Value:yyyy-MM-dd HH:mm} (in {t:mm\\:ss})";
            }
            else
            {
                CfgNextAutoRestart.Text = $"Next: {nextAutoRestartLocal.Value:yyyy-MM-dd HH:mm}";
            }
        }
        private void AutoRestartTimerFired()
        {
            if (!autoRestartArmed) return;

            Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (CfgAutoRestartEnabled.IsChecked != true)
                    return;

                if (server?.IsRunning == true)
                {
                    AppendLog("Auto restart triggered.", LogType.Warning);
                    await RestartServerAsync();
                }
                else
                {
                    AppendLog("Auto restart time reached, but server is offline. Skipping.", LogType.Info);
                }

                ScheduleAutoRestart();
            }));
        }
        private void StartAutoRestartCountdown(int seconds)
        {
            autoRestartCountdownUi?.Stop();

            countdownSecondsRemaining = Math.Max(0, seconds);
            UpdateNextAutoRestartLabel();

            autoRestartCountdownUi = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };

            autoRestartCountdownUi.Tick += async (_, _) =>
            {
                if (CfgAutoRestartEnabled.IsChecked != true)
                {
                    autoRestartCountdownUi?.Stop();
                    countdownSecondsRemaining = 0;
                    UpdateNextAutoRestartLabel();
                    return;
                }

                countdownSecondsRemaining--;
                if (countdownSecondsRemaining < 0)
                    countdownSecondsRemaining = 0;

                UpdateNextAutoRestartLabel();

                TrySendRestartWarnings(countdownSecondsRemaining);

                if (countdownSecondsRemaining == 0)
                {
                    autoRestartCountdownUi?.Stop();
                    if (server?.IsRunning == true)
                    {
                        AppendLog("Auto restart countdown reached zero. Restarting server.", LogType.Warning);
                        await RestartServerAsync();
                    }
                    else
                    {
                        AppendLog("Auto restart reached zero, but server is offline. Skipping.", LogType.Info);
                    }
                    ScheduleAutoRestart();
                }
            };

            autoRestartCountdownUi.Start();

            AppendLog($"Auto restart countdown started: {seconds}s", LogType.Warning);
        }
        private void TrySendRestartWarnings(int secondsLeft)
        {
            if (server?.IsRunning != true) return;
            if (secondsLeft == lastWarnSecond) return;
            lastWarnSecond = secondsLeft;
            if (secondsLeft == 600 || secondsLeft == 540 || secondsLeft == 480 ||
                secondsLeft == 420 || secondsLeft == 360 || secondsLeft == 300 ||
                secondsLeft == 240 || secondsLeft == 180 || secondsLeft == 120 || secondsLeft == 60)
            {
                server.SendCommand($"say [SYSTEM MAINTENANCE] Server restarting in {secondsLeft} minutes");
            } else if (secondsLeft == 45 || secondsLeft == 30 || secondsLeft == 15 ||
                secondsLeft == 10 || secondsLeft == 9 || secondsLeft == 8 ||
                secondsLeft == 7 || secondsLeft == 6 || secondsLeft == 5 ||
                secondsLeft == 4 || secondsLeft == 3 || secondsLeft == 2 ||
                secondsLeft == 1 || secondsLeft == 0)
            {
                server.SendCommand($"say [SYSTEM MAINTENANCE] Server restarting in {secondsLeft} seconds");
            }
        }








        private void EnsureFoldersExist()
        {
            try
            {
                Directory.CreateDirectory(@".\mods");
                Directory.CreateDirectory(@".\mods_unloaded");

                Directory.CreateDirectory(@".\universe");
                Directory.CreateDirectory(@".\universe\worlds");
                Directory.CreateDirectory(@".\universe\worlds\default");
                
                Directory.CreateDirectory(@".\backups");

                AppendLog("Verified required folders", LogType.Info);
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to create folders: {ex.Message}", LogType.Error);
            }
        }
        private void EnsureConfigsExist()
        {
            EnsureServerConfigExists(configPath);
            EnsureWorldConfigExists(worldConfigPath);
        }

        private void EnsureServerConfigExists(string path)
        {
            if (File.Exists(path))
                return;

            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                var root = new JsonObject
                {
                    ["Version"] = 3,
                    ["ServerName"] = "Hytale Server",
                    ["MOTD"] = "",
                    ["Password"] = "",
                    ["NetworkPort"] = 5520,
                    ["MaxPlayers"] = 100,
                    ["MaxViewRadius"] = 32,
                    ["LocalCompressionEnabled"] = false,

                    ["Defaults"] = new JsonObject
                    {
                        ["World"] = "default",
                        ["GameMode"] = "Adventure"
                    },

                    ["ConnectionTimeouts"] = new JsonObject
                    {
                        ["JoinTimeouts"] = new JsonObject()
                    },

                    ["RateLimit"] = new JsonObject(),

                    ["Modules"] = new JsonObject
                    {
                        ["PathPlugin"] = new JsonObject
                        {
                            ["Modules"] = new JsonObject()
                        }
                    },

                    ["LogLevels"] = new JsonObject(),
                    ["Mods"] = new JsonObject(),

                    ["DisplayTmpTagsInStrings"] = false,

                    ["PlayerStorage"] = new JsonObject
                    {
                        ["Type"] = "Hytale"
                    },

                    ["AuthCredentialStore"] = new JsonObject
                    {
                        ["Type"] = "Encrypted",
                        ["Path"] = "auth.enc"
                    },
                    ["AutoRestart"] = new JsonObject
                    {
                        ["Enabled"] = false,
                        ["Mode"] = "Daily",
                        ["Time"] = "03:00",
                        ["DayOfWeek"] = "Sunday"
                    }
                };

                File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                AppendLog("Created missing server config.json", LogType.Warning);
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to create server config: {ex.Message}", LogType.Error);
            }
        }

        private void EnsureWorldConfigExists(string path)
        {
            if (File.Exists(path))
                return;

            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                var uuidBytes = Guid.NewGuid().ToByteArray();
                var uuidBase64 = Convert.ToBase64String(uuidBytes);

                long seed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                var root = new JsonObject
                {
                    ["Version"] = 4,
                    ["UUID"] = new JsonObject
                    {
                        ["$binary"] = uuidBase64,
                        ["$type"] = "04"
                    },

                    ["Seed"] = seed,

                    ["WorldGen"] = new JsonObject
                    {
                        ["Type"] = "Hytale",
                        ["Name"] = "Default"
                    },

                    ["WorldMap"] = new JsonObject
                    {
                        ["Type"] = "WorldGen"
                    },

                    ["ChunkStorage"] = new JsonObject
                    {
                        ["Type"] = "Hytale"
                    },

                    ["ChunkConfig"] = new JsonObject(),

                    ["IsTicking"] = true,
                    ["IsBlockTicking"] = true,

                    ["IsPvpEnabled"] = false,
                    ["IsFallDamageEnabled"] = true,
                    ["IsSpawningNPC"] = true,
                    ["IsSpawnMarkersEnabled"] = true,
                    ["IsCompassUpdating"] = true,
                    ["IsSavingPlayers"] = true,
                    ["IsSavingChunks"] = true,
                    ["SaveNewChunks"] = true,
                    ["IsUnloadingChunks"] = true,
                    ["IsObjectiveMarkersEnabled"] = true,

                    ["IsGameTimePaused"] = false,
                    ["GameTime"] = DateTime.UtcNow.ToString("O"),

                    ["ClientEffects"] = new JsonObject
                    {
                        ["SunHeightPercent"] = 100.0,
                        ["SunAngleDegrees"] = 0.0,
                        ["BloomIntensity"] = 0.3,
                        ["BloomPower"] = 8.0,
                        ["SunIntensity"] = 0.25,
                        ["SunshaftIntensity"] = 0.3,
                        ["SunshaftScaleFactor"] = 4.0
                    },

                    ["RequiredPlugins"] = new JsonObject(),
                    ["IsAllNPCFrozen"] = false,
                    ["GameplayConfig"] = "Default",

                    ["DeleteOnUniverseStart"] = false,
                    ["DeleteOnRemove"] = false,

                    ["ResourceStorage"] = new JsonObject
                    {
                        ["Type"] = "Hytale"
                    },

                    ["Plugin"] = new JsonObject()
                };

                File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                AppendLog("Created missing world config.json (default world)", LogType.Warning);
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to create world config: {ex.Message}", LogType.Error);
            }
        }







        private void TopStart_Click(object sender, RoutedEventArgs e)
        {

            StartServerInternal();
        }

        private void TopStop_Click(object sender, RoutedEventArgs e)
        {
            AppendLog("Stopping server...", LogType.Warning);
            server.Stop();
        }

        private void TopRestart_Click(object sender, RoutedEventArgs e)
        {
            server.NetworkPort = networkPort;
            RestartServerAsync();
        }






        private void HookServerEvents()
        {
            server.OutputReceived += OnServerOutput;
            server.ServerStarted += OnServerStarted;
            server.ServerBooted += OnServerBooted;
            server.ServerStopped += OnServerStopped;
            server.ServerCrashed += OnServerCrashed;
            server.PlayerJoined += OnPlayerJoined;
            server.PlayerLeft += OnPlayerLeft;
            server.PlayerOpChanged += OnPlayerOpChanged;
        }

        private void OnServerStarted()
        {
            Dispatcher.Invoke(() =>
            {
                ModsMoveEnabled = false;
                ModsBannerVisible = true;
                BansBannerVisible = true;
                //StatusText.Text = "Online";
                // StatusLight.Fill = Brushes.LimeGreen;
                serverStatus = ServerStatus.Starting;
                UpdateServerStatusUI();

                serverStartTime = DateTime.Now;
                sessionPeakPlayers = 0;

                cpuHistory.Clear();
                memHistory.Clear();
                cpuPeak = 0;
                memPeak = 0;
                perfStartUtc = DateTime.UtcNow;

                lastCpuTime = server.Process.TotalProcessorTime;
                lastCpuCheck = DateTime.Now;

                AppendLog("Server started", LogType.Info);
            });
        }
        private void OnServerBooted()
        {
            Dispatcher.Invoke(() =>
            {
                serverStatus = ServerStatus.Online;
                UpdateServerStatusUI();
                AppendLog("Server Finished starting", LogType.Info);
            });
        }
        private void OnServerStopped()
        {
            Dispatcher.Invoke(() =>
            {
                ModsMoveEnabled = true;
                ModsBannerVisible = false;
                BansBannerVisible = false;
                serverStatus = ServerStatus.Offline;
                UpdateServerStatusUI();
                //StatusText.Text = "Offline";
                //StatusLight.Fill = Brushes.Red;

                serverStartTime = null;

                AppendLog("Server stopped", LogType.Warning);
            });
        }

        private void OnServerCrashed()
        {
            Dispatcher.Invoke(() =>
            {
                ModsMoveEnabled = true;
                ModsBannerVisible = false;
                BansBannerVisible = false;
                serverStatus = ServerStatus.Crashed;
                UpdateServerStatusUI();
                //StatusText.Text = "Crashed";
                //StatusLight.Fill = Brushes.DarkRed;

                AppendLog("⚠ Server crashed. Auto-restart engaged.", LogType.Error);
            });
        }

        private void StartServerInternal()
        {
            server.NetworkPort = networkPort;
            AppendLog($"Starting server (bind 0.0.0.0:{networkPort})...", LogType.Info);
            server.Start();
        }
        private async Task RestartServerAsync()
        {
            if (restartInProgress)
                return;

            restartInProgress = true;

            try
            {
                if (server?.Process != null && !server.Process.HasExited)
                {
                    AppendLog("Restart: stopping server...", LogType.Warning);

                    try
                    {
                        server.SendCommand("stop");
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"Restart: failed to send stop: {ex.Message}", LogType.Error);
                    }

                    const int stopTimeoutMs = 12000;
                    bool exited = await Task.Run(() =>
                    {
                        try { return server.Process.WaitForExit(stopTimeoutMs); }
                        catch { return true; }
                    });

                    if (!exited)
                    {
                        AppendLog("Restart: server did not stop in time, forcing termination...", LogType.Error);

                        try
                        {
                            server.Process.Kill(entireProcessTree: true);
                        }
                        catch (Exception ex)
                        {
                            AppendLog($"Restart: force-kill failed: {ex.Message}", LogType.Error);
                        }
                    }
                }

                AppendLog("Restart: starting server...", LogType.Warning);

                StartServerInternal();

                AppendLog("Restart: start command issued.", LogType.Info);
            }
            finally
            {
                restartInProgress = false;
            }
        }

        private void UpdateServerStatusUI()
        {
            switch (serverStatus)
            {
                case ServerStatus.Offline:
                    StatusText.Text = "Offline";
                    StatusLight.Fill = new SolidColorBrush(Color.FromRgb(255, 55, 55));
                    break;
                case ServerStatus.Starting:
                    StatusText.Text = "Starting…";
                    StatusLight.Fill = new SolidColorBrush(Color.FromRgb(55, 55, 255));
                    break;
                case ServerStatus.Online:
                    StatusText.Text = "Online";
                    StatusLight.Fill = new SolidColorBrush(Color.FromRgb(55, 255, 55));
                    break;
                case ServerStatus.Crashed:
                    StatusText.Text = "Crashed";
                    StatusLight.Fill = new SolidColorBrush(Color.FromRgb(255, 115, 55));
                    break;
            }
        }









        private void CopyName_Click(object sender, RoutedEventArgs e)
        {
            var p = GetPlayerFromSender(sender);
            if (p == null) return;
            Clipboard.SetText(p.Name);
            AppendLog($"Copied name: {p.Name}", LogType.Info);
        }

        private void CopyHash_Click(object sender, RoutedEventArgs e)
        {
            var p = GetPlayerFromSender(sender);
            if (p == null) return;
            Clipboard.SetText(p.Hash);
            AppendLog($"Copied hash: {p.Hash}", LogType.Info);
        }
        private void OnPlayerJoined(PlayerInfo incoming)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var existing = !string.IsNullOrWhiteSpace(incoming.Hash)
                    ? players.FirstOrDefault(p => p.Hash == incoming.Hash)
                    : players.FirstOrDefault(p => p.Name.Equals(incoming.Name, StringComparison.OrdinalIgnoreCase));

                if (existing == null)
                {
                    incoming.IsOnline = true;
                    players.Add(incoming);
                    sessionStore.OnJoin(incoming);
                }
                else
                {
                    existing.Name = incoming.Name;
                    existing.Hash = incoming.Hash;
                    existing.JoinedAt = DateTime.Now;
                    existing.IsOnline = true;
                    sessionStore.OnJoin(existing);
                }
                SyncOpsFromPermissions();
                UpdatePlayerCount();
                RefreshPlayerSort();
            }));
        }

        private void OnPlayerLeft(PlayerInfo incoming)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                PlayerInfo? existing = null;

                if (!string.IsNullOrWhiteSpace(incoming.Hash))
                    existing = players.FirstOrDefault(p => p.Hash == incoming.Hash);

                existing ??= players.FirstOrDefault(p => p.Name.Equals(incoming.Name, StringComparison.OrdinalIgnoreCase));

                if (existing == null) return;

                existing.IsOnline = false;
                existing.LastSeenAt = DateTime.Now;

                sessionStore.OnLeave(existing);

                UpdatePlayerCount();
                RefreshPlayerSort();
            }));
        }
        private void OnPlayerOpChanged(string name, bool isOp)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var p = players.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (p == null)
                {
                    p = new PlayerInfo
                    {
                        Name = name,
                        Hash = "",
                        IsOnline = false,
                        IsOp = isOp
                    };
                    players.Add(p);
                }
                else
                {
                    p.IsOp = isOp;
                    SyncOpsFromPermissions();
                }

                AppendLog(isOp ? $"{name} marked as operator." : $"{name} unmarked as operator.", LogType.Info);

                RefreshPlayerSort();
            }));
        }

        private void UpdatePlayerCount()
        {
            int online = players.Count(p => p.IsOnline);

            PlayerCountText.Text = $"Players: {online} / {maxPlayers}";

            if (online > sessionPeakPlayers)
            {
                sessionPeakPlayers = online;
                PerfPeakPlayers.Text = $"{sessionPeakPlayers}";
            }
        }
        private void SetupPlayerSorting()
        {
            var view = (ListCollectionView)CollectionViewSource.GetDefaultView(players);
            view.CustomSort = new PlayerComparer();
        }

        private sealed class PlayerComparer : IComparer
        {
            public int Compare(object? x, object? y)
            {
                var a = x as PlayerInfo;
                var b = y as PlayerInfo;
                if (a == null || b == null) return 0;
                /*  0 = Online Op
                    1 = Online
                    2 = Offline Op
                    3 = Offline*/
                int ra = Rank(a);
                int rb = Rank(b);
                int r = ra.CompareTo(rb);
                if (r != 0) return r;

                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            }

            private static int Rank(PlayerInfo p)
            {
                if (p.IsOnline && p.IsOp) return 0;
                if (p.IsOnline && !p.IsOp) return 1;
                if (!p.IsOnline && p.IsOp) return 2;
                return 3;
            }
        }
        private void RefreshPlayerSort()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                CollectionViewSource.GetDefaultView(players)?.Refresh();
            }));
        }
        private void OpToggle_Click(object sender, RoutedEventArgs e)
        {
            var p = GetPlayerFromSender(sender);
            if (p == null) return;

            if (server?.IsRunning != true)
            {
                AppendLog("Server is offline; cannot op/deop.", LogType.Warning);
                return;
            }

            if (p.IsOp)
            {
                server.SendCommand($"op remove {p.Name}");
                AppendLog($"Deop issued: {p.Name}", LogType.Warning);
                p.IsOp = false;
            }
            else
            {
                server.SendCommand($"op add {p.Name}");
                AppendLog($"Op issued: {p.Name}", LogType.Warning);
                p.IsOp = true;
            }

            RefreshPlayerSort();
        }








        private void SetupPerformanceTimer()
        {
            performanceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            performanceTimer.Tick += (_, _) => UpdatePerformance();
            performanceTimer.Start();
        }

        private void UpdatePerformance()
        {
            if (server?.Process == null || server.Process.HasExited)
                return;

            bool warmup = IsPerfWarmup;
            if (!warmup)
            {
                server.Process.Refresh();
                double memMb = server.Process.WorkingSet64 / (1024.0 * 1024.0);
                MemoryText.Text = $"Memory: {memMb:0} MB";
                PerfMemNow.Text = $"{memMb:0} MB";

                var now = DateTime.Now;
                var cpuTime = server.Process.TotalProcessorTime;

                double cpuUsedMs = (cpuTime - lastCpuTime).TotalMilliseconds;
                double elapsedMs = (now - lastCpuCheck).TotalMilliseconds;

                double cpuPercent = 0;
                if (elapsedMs > 0)
                    cpuPercent = cpuUsedMs / (elapsedMs * Environment.ProcessorCount) * 100.0;

                if (cpuPercent < 0) cpuPercent = 0;
                if (cpuPercent > 100) cpuPercent = 100;

                CpuText.Text = $"CPU: {cpuPercent:0.0}%";
                PerfCpuNow.Text = $"{cpuPercent:0.0}%";

                lastCpuTime = cpuTime;
                lastCpuCheck = now;

                if (serverStartTime.HasValue)
                {
                    var up = DateTime.Now - serverStartTime.Value;

                    if (up.TotalDays >= 1)
                        PerfUptime.Text = $"{(int)up.TotalDays}d {up:hh\\:mm\\:ss}";
                    else
                        PerfUptime.Text = up.ToString(@"hh\:mm\:ss");
                }
                else
                {
                    PerfUptime.Text = "--:--:--";
                }

                EnqueueRolling(cpuHistory, cpuPercent, PerfSeconds);
                EnqueueRolling(memHistory, memMb, PerfSeconds);

                cpuPeak = Math.Max(cpuPeak, cpuPercent);
                memPeak = Math.Max(memPeak, memMb);
                cpuAvg = cpuHistory.Count > 0 ? cpuHistory.Average() : 0;

                PerfCpuSummary.Text = $"{cpuPercent:0.0}% / {cpuAvg:0.0}% / {cpuPeak:0.0}%";
                PerfMemSummary.Text = $"{memMb:0} MB / {memPeak:0} MB";

                DrawLineChart(CpuCanvas, cpuHistory, 0, 100);
                DrawLineChart(MemCanvas, memHistory, 0, Math.Max(1, memPeak));
            } else
            {
                var left = (perfWarmup - (DateTime.UtcNow - perfStartUtc));
                PerfCpuSummary.Text = $"Stabilizing...";
                PerfMemSummary.Text = $"{left.Seconds}s";

            }
        }
        private static void EnqueueRolling(Queue<double> q, double value, int max)
        {
            q.Enqueue(value);
            while (q.Count > max)
                q.Dequeue();
        }
        private void DrawLineChart(Canvas canvas, IEnumerable<double> series, double minY, double maxY)
        {
            if (canvas == null) return;

            canvas.Children.Clear();

            double w = canvas.ActualWidth;
            double h = canvas.ActualHeight;
            if (w <= 2 || h <= 2) return;

            var data = series.ToList();
            if (data.Count < 2) return;

            for (int i = 0; i <= 4; i++)
            {
                double y = h * i / 4.0;
                var line = new System.Windows.Shapes.Line
                {
                    X1 = 0,
                    X2 = w,
                    Y1 = y,
                    Y2 = y,
                    Stroke = Brushes.DimGray,
                    StrokeThickness = 1,
                    Opacity = 0.25
                };
                canvas.Children.Add(line);
            }

            var poly = new System.Windows.Shapes.Polyline
            {
                Stroke = Brushes.LightGreen,
                StrokeThickness = 2
            };

            double range = Math.Max(0.0001, maxY - minY);
            double stepX = w / (data.Count - 1);

            for (int i = 0; i < data.Count; i++)
            {
                double v = data[i];
                if (v < minY) v = minY;
                if (v > maxY) v = maxY;

                double x = i * stepX;
                double t = (v - minY) / range;
                double y = (1.0 - t) * (h - 2) + 1;

                poly.Points.Add(new Point(x, y));
            }

            canvas.Children.Add(poly);
        }

        private void PerfCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            DrawLineChart(CpuCanvas, cpuHistory, 0, 100);
            DrawLineChart(MemCanvas, memHistory, 0, Math.Max(1, memPeak));
        }


        private void OnServerOutput(string line)
        {
            LogType type =
                line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ? LogType.Error :
                line.Contains("WARN", StringComparison.OrdinalIgnoreCase) ? LogType.Warning :
                LogType.Info;

            AppendLog(line, type);
        }

        private const int MaxLogLines = 3000;

        private void AppendLog(string message, LogType type)
        {
            var entry = new LogEntry
            {
                Time = DateTime.Now,
                Message = message,
                Type = type
            };

            logBuffer.Add(entry);

            if (logBuffer.Count > MaxLogLines)
                logBuffer.RemoveRange(0, logBuffer.Count - MaxLogLines);

            if (!PassesFilters(entry))
                return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                AppendLogToDocument(entry);
                ConsoleOutput.ScrollToEnd();
            }));
        }
        private bool PassesFilters(LogEntry log)
        {
            if (log.Type == LogType.Info && !showInfo) return false;
            if (log.Type == LogType.Warning && !showWarn) return false;
            if (log.Type == LogType.Error && !showError) return false;
            return true;
        }

        private void AppendLogToDocument(LogEntry log)
        {
            var p = new Paragraph();

            if (showTimestamps)
            {
                p.Inlines.Add(new Run($"[{log.Time:HH:mm:ss}] ")
                {
                    Foreground = Brushes.Gray
                });
            }

            p.Inlines.Add(new Run(log.Message)
            {
                Foreground = log.Type switch
                {
                    LogType.Info => Brushes.LightGray,
                    LogType.Warning => Brushes.Gold,
                    LogType.Error => Brushes.OrangeRed,
                    _ => Brushes.White
                }
            });

            ConsoleOutput.Document.Blocks.Add(p);

            while (ConsoleOutput.Document.Blocks.Count > MaxLogLines)
                ConsoleOutput.Document.Blocks.Remove(ConsoleOutput.Document.Blocks.FirstBlock);
        }


        private void RefreshConsole()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ConsoleOutput.Document.Blocks.Clear();

                foreach (var log in logBuffer)
                {
                    if (!PassesFilters(log)) continue;
                    AppendLogToDocument(log);
                }

                ConsoleOutput.ScrollToEnd();
            }));
        }

        private void HookConsoleFilters()
        {
            void Sync()
            {
                showInfo = InfoToggle.IsChecked == true;
                showWarn = WarnToggle.IsChecked == true;
                showError = ErrorToggle.IsChecked == true;
                showTimestamps = TimestampToggle.IsChecked == true;
            }

            Sync();

            InfoToggle.Checked += (_, _) => { Sync(); RefreshConsole(); };
            InfoToggle.Unchecked += (_, _) => { Sync(); RefreshConsole(); };

            WarnToggle.Checked += (_, _) => { Sync(); RefreshConsole(); };
            WarnToggle.Unchecked += (_, _) => { Sync(); RefreshConsole(); };

            ErrorToggle.Checked += (_, _) => { Sync(); RefreshConsole(); };
            ErrorToggle.Unchecked += (_, _) => { Sync(); RefreshConsole(); };

            TimestampToggle.Checked += (_, _) => { Sync(); RefreshConsole(); };
            TimestampToggle.Unchecked += (_, _) => { Sync(); RefreshConsole(); };
        }


        private void SendCommand()
        {
            if (string.IsNullOrWhiteSpace(CommandInput.Text))
                return;

            server.SendCommand(CommandInput.Text);
            AppendLog($"> {CommandInput.Text}", LogType.Info);

            CommandInput.Clear();
            CommandInput.Focus();
        }

        private void Send_Click(object sender, RoutedEventArgs e)
        {
            SendCommand();
        }

        private void CommandInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                SendCommand();
                e.Handled = true;
            }
        }

        private void ConsoleOutput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ConsoleOutput.Copy();
                e.Handled = true;
            }
        }

        private void Restart_Click(object sender, RoutedEventArgs e)
        {
            AppendLog("Restarting server...", LogType.Warning);
            server.Restart();
        }


        private PlayerInfo? GetPlayerFromSender(object sender)
        {
            return (sender as FrameworkElement)?.DataContext as PlayerInfo;
        }

        private void Op_Click(object sender, RoutedEventArgs e)
        {
            var player = GetPlayerFromSender(sender);
            if (player == null) return;

            server.SendCommand($"op {player.Name}");
            AppendLog($"Opped {player.Name}", LogType.Info);
        }

        private void Kick_Click(object sender, RoutedEventArgs e)
        {
            var player = GetPlayerFromSender(sender);
            if (player == null) return;

            server.SendCommand($"kick {player.Name}");
            AppendLog($"Kicked {player.Name}", LogType.Warning);
        }

        private void Ban_Click(object sender, RoutedEventArgs e)
        {
            var p = GetPlayerFromSender(sender);
            if (p == null) return;

            var confirm = MessageBox.Show(
                $"Ban {p.Name}?",
                "Confirm Ban",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
                return;

           // Resons are broken, leaving this here for when they're not broken.
           /* var dlg = new BanReasonDialog(p.Name) { Owner = this };
            if (dlg.ShowDialog() != true)
                return;

            var reason = dlg.Reason;
           
            if (string.IsNullOrWhiteSpace(reason))
                server.SendCommand($"ban {p.Name}");
            else
                server.SendCommand($"ban {p.Name}  {p.Name} : {reason}");

            AppendLog(string.IsNullOrWhiteSpace(reason)
                ? $"Ban issued: {p.Name}"
                : $"Ban issued: {p.Name} (Reason: {reason})", LogType.Warning);
           */
            server.SendCommand($"ban {p.Name}");
            AppendLog($"Ban issued: {p.Name}", LogType.Warning);
        }








        private void OpenAuthorX_Click(object sender, RoutedEventArgs e)
        {
            var url = "https://x.com/HyLordly ";

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to open author link: {ex.Message}", LogType.Error);
            }
        }
        private void OpenDiscord_Click(object sender, RoutedEventArgs e)
        {
            var url = "https://discord.gg/3jvX6M3n";

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to open Discord link: {ex.Message}", LogType.Error);
            }
        }







        private async Task LoadModsAsync()
        {
            try
            {
                Directory.CreateDirectory(modsFolder);
                Directory.CreateDirectory(modsUnloadedFolder);

                var loadedFiles = EnumerateModFiles(modsFolder).ToList();
                var unloadedFiles = EnumerateModFiles(modsUnloadedFolder).ToList();

                var results = await Task.Run(() =>
                {
                    var loaded = loadedFiles.Select(p => ReadModInfo(p, isLoaded: true)).ToList();
                    var unloaded = unloadedFiles.Select(p => ReadModInfo(p, isLoaded: false)).ToList();
                    return (loaded, unloaded);
                });

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    loadedMods.Clear();
                    foreach (var m in results.loaded.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        TryAttachModConfig(m);
                        loadedMods.Add(m);
                    }
                        


                    unloadedMods.Clear();
                    foreach (var m in results.unloaded.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                        unloadedMods.Add(m);

                    AppendLog($"Mods refreshed. Loaded: {loadedMods.Count}, Unloaded: {unloadedMods.Count}", LogType.Info);
                }));
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to load mods: {ex.Message}", LogType.Error);
            }
        }

        private IEnumerable<string> EnumerateModFiles(string folder)
        {
            if (!Directory.Exists(folder))
                yield break;

            foreach (var f in Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly))
            {
                if (f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    yield return f;
            }
        }

        private ModInfo ReadModInfo(string filePath, bool isLoaded)
        {
            var info = new ModInfo
            {
                FullPath = filePath,
                FileName = Path.GetFileName(filePath),
                IsLoaded = isLoaded,
                Name = "(Unknown)",
                Status = "OK"
            };

            try
            {
                using var fs = File.OpenRead(filePath);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);

                var entry =
                    zip.GetEntry("Manifest.json") ??
                    zip.GetEntry("manifest.json") ??
                    zip.GetEntry("META-INF/Manifest.json") ??
                    zip.GetEntry("META-INF/manifest.json");

                if (entry == null)
                {
                    info.Status = "No Manifest.json";
                    return info;
                }

                using var es = entry.Open();
                using var sr = new StreamReader(es);
                var jsonText = sr.ReadToEnd();

                using var doc = JsonDocument.Parse(jsonText);
                var root = doc.RootElement;

                if (root.TryGetProperty("Group", out var g)) info.Group = g.ToString();
                if (root.TryGetProperty("Name", out var n)) info.Name = n.ToString();
                if (root.TryGetProperty("Version", out var v)) info.Version = v.ToString();
                if (root.TryGetProperty("Description", out var d)) info.Description = d.ToString();

                return info;
            }
            catch (JsonException)
            {
                info.Status = "Invalid manifest";
                return info;
            }
            catch (InvalidDataException)
            {
                info.Status = "Not a zip/jar";
                return info;
            }
            catch (Exception ex)
            {
                info.Status = $"Error: {ex.GetType().Name}";
                return info;
            }
        }
        private void TryAttachModConfig(ModInfo mod)
        {
            mod.ConfigPath = "";

            if (string.IsNullOrWhiteSpace(mod.Group) || string.IsNullOrWhiteSpace(mod.Name))
                return;

            var folderName = $"{mod.Group}_{mod.Name}";
            var folderPath = Path.Combine(modsFolder, folderName);

            if (!Directory.Exists(folderPath))
                return;

            var candidate1 = Path.Combine(folderPath, "config.json");
            var candidate2 = Path.Combine(folderPath, $"{mod.Name}.json");

            if (File.Exists(candidate1)) mod.ConfigPath = candidate1;
            else if (File.Exists(candidate2)) mod.ConfigPath = candidate2;
        }

        private async void ModAction_Click(object sender, RoutedEventArgs e)
        {
            if (server?.IsRunning == true)
            {
                AppendLog("Stop the server before moving mods.", LogType.Warning);
                return;
            }
            if ((sender as FrameworkElement)?.DataContext is not ModInfo mod)
                return;

            try
            {
                Directory.CreateDirectory(modsFolder);
                Directory.CreateDirectory(modsUnloadedFolder);

                var source = mod.FullPath;
                var targetFolder = mod.IsLoaded ? modsUnloadedFolder : modsFolder;
                var target = Path.Combine(targetFolder, Path.GetFileName(source));

                if (File.Exists(target))
                {
                    AppendLog($"Move blocked: {Path.GetFileName(target)} already exists in target folder.", LogType.Warning);
                    return;
                }

                await Task.Run(() => File.Move(source, target));

                AppendLog($"{(mod.IsLoaded ? "Unloaded" : "Loaded")} mod: {mod.Name} ({mod.FileName})", LogType.Info);

                await LoadModsAsync();
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to move mod: {ex.Message}", LogType.Error);
            }
        }
        private void ModConfig_Click(object sender, RoutedEventArgs e)
        {
            if (server?.IsRunning == true)
            {
                AppendLog("Stop the server before editing mod configs (files may be locked).", LogType.Warning);
                return;
            }

            if ((sender as FrameworkElement)?.DataContext is not ModInfo mod) return;
            if (!mod.HasConfig) return;

            var win = new ModConfigEditorWindow(mod.Name, mod.ConfigPath) { Owner = this };
            win.ShowDialog();
        }

        private void RefreshMods_Click(object sender, RoutedEventArgs e)
        {
            _ = LoadModsAsync();
        }

        private void OpenModsFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(modsFolder);
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.GetFullPath(modsFolder),
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to open mods folder: {ex.Message}", LogType.Error);
            }
        }

        private void OpenUnloadedModsFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(modsUnloadedFolder);
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.GetFullPath(modsUnloadedFolder),
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to open unloaded mods folder: {ex.Message}", LogType.Error);
            }
        }







        private void EnsureBansFileExists()
        {
            try
            {
                if (!File.Exists(bansPath))
                    File.WriteAllText(bansPath, "[]");
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to initialize bans.json: {ex.Message}", LogType.Error);
            }
        }
        private void LoadBans()
        {
            try
            {
                EnsureBansFileExists();
                Directory.CreateDirectory(playersFolder);

                var json = File.ReadAllText(bansPath);
                var list = JsonSerializer.Deserialize<List<BanEntry>>(json) ?? new List<BanEntry>();

                foreach (var b in list)
                    b.DisplayName = ResolvePlayerNameFromHash(b.Target);

                bans.Clear();
                foreach (var b in list.OrderByDescending(x => x.Timestamp))
                    bans.Add(b);

                AppendLog($"Loaded {bans.Count} ban(s).", LogType.Info);
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to load bans.json: {ex.Message}", LogType.Error);
            }
        }


        private void SaveBans()
        {
            try
            {
                var list = bans.ToList();
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(bansPath, JsonSerializer.Serialize(list, options));
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to save bans.json: {ex.Message}", LogType.Error);
            }
        }
        private void RefreshBans_Click(object sender, RoutedEventArgs e)
        {
            LoadBans();
        }

        private void OpenBansFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                EnsureBansFileExists();
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.GetFullPath(bansPath),
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to open bans.json: {ex.Message}", LogType.Error);
            }
        }
        private void Unban_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not BanEntry ban)
                return;

            var confirm = MessageBox.Show(
                $"Unban:\nName: {ban.DisplayName}\nHash: {ban.Target}\n\nReason: {ban.Reason}",
                "Confirm Unban",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
                return;

            var removed = bans.Remove(ban);
            if (!removed) return;

            SaveBans();
            AppendLog($"Unbanned (removed from bans.json): {ban.Target}", LogType.Warning);

            if (server?.IsRunning == true)
            {
                if (!string.IsNullOrWhiteSpace(ban.DisplayName) && ban.DisplayName != "(unknown)")
                {
                    server.SendCommand($"unban {ban.DisplayName}");
                    AppendLog($"Sent command: unban {ban.DisplayName}", LogType.Info);
                }
                else
                {
                    AppendLog($"Could not resolve name for hash {ban.Target}. File removed, but no unban command sent.", LogType.Warning);
                }
            }
            else
            {
                AppendLog("Server is offline. Restart server to apply bans.json changes.", LogType.Info);
            }
        }


        private string ResolvePlayerNameFromHash(string hash)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(hash))
                    return "(unknown)";

                Directory.CreateDirectory(playersFolder);

                var path = Path.Combine(playersFolder, $"{hash}.json");
                if (!File.Exists(path))
                    return "(unknown)";

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;

                if (root.TryGetProperty("Components", out var comps) &&
                    comps.TryGetProperty("DisplayName", out var dn1) &&
                    dn1.TryGetProperty("DisplayName", out var dn2) &&
                    dn2.TryGetProperty("RawText", out var raw))
                {
                    var name = raw.GetString();
                    return string.IsNullOrWhiteSpace(name) ? "(unknown)" : name!;
                }

                return "(unknown)";
            }
            catch
            {
                return "(unknown)";
            }
        }






        private void InitAutoBackupControls()
        {
            CfgAutoBackupHour.Items.Clear();
            for (int h = 0; h < 24; h++)
                CfgAutoBackupHour.Items.Add(h.ToString("00"));

            CfgAutoBackupMinute.Items.Clear();
            for (int m = 0; m < 60; m += 5)
                CfgAutoBackupMinute.Items.Add(m.ToString("00"));

            CfgAutoBackupHour.SelectedItem = "03";
            CfgAutoBackupMinute.SelectedItem = "00";
            CfgAutoBackupEnabled.IsChecked = false;
        }
        private async Task LoadBackupsAsync()
        {
            try
            {
                Directory.CreateDirectory(backupsFolder);

                var files = Directory.EnumerateFiles(backupsFolder, "*.zip", SearchOption.TopDirectoryOnly)
                    .Select(p => new FileInfo(p))
                    .OrderByDescending(fi => fi.CreationTimeUtc)
                    .ToList();

                var list = await Task.Run(() =>
                {
                    var tmp = new List<BackupInfo>();
                    foreach (var fi in files)
                    {
                        tmp.Add(new BackupInfo
                        {
                            FullPath = fi.FullName,
                            FileName = fi.Name,
                            CreatedLocal = fi.CreationTime,
                            SizeBytes = fi.Length
                        });
                    }
                    return tmp;
                });

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    backups.Clear();
                    foreach (var b in list)
                        backups.Add(b);
                }));
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to load backups: {ex.Message}", LogType.Error);
            }
        }
        private void RefreshBackups_Click(object sender, RoutedEventArgs e)
        {
            _ = LoadBackupsAsync();
        }

        private void OpenBackupsFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(backupsFolder);
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.GetFullPath(backupsFolder),
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to open backups folder: {ex.Message}", LogType.Error);
            }
        }

        private void BackupNow_Click(object sender, RoutedEventArgs e)
        {
            if (server?.IsRunning == true)
            {
                server.SendCommand("backup");
                AppendLog("Backup command sent.", LogType.Info);
            }
            else
            {
                AppendLog("Server is offline; cannot run backup command.", LogType.Warning);
            }
        }
        private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not BackupInfo backup)
                return;

            var confirm = MessageBox.Show(
                $"Restore this backup?\n\n{backup.FileName}\n\nThis will overwrite the current universe folder.",
                "Confirm Restore",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
                return;

            bool wasRunning = (server?.IsRunning == true);

            try
            {
                Directory.CreateDirectory(backupsFolder);

                AppendLog("Restore: creating manual backup...", LogType.Warning);
                var manualZip = await CreateManualBackupZipAsync();
                AppendLog($"Restore: manual backup created: {Path.GetFileName(manualZip)}", LogType.Info);

                if (wasRunning)
                {
                    AppendLog("Restore: stopping server...", LogType.Warning);
                    await StopServerGracefullyAsync();
                }

                AppendLog($"Restore: restoring universe from {backup.FileName} ...", LogType.Warning);
                await RestoreUniverseFromBackupAsync(backup.FullPath);
                AppendLog("Restore: universe restored.", LogType.Info);

                if (wasRunning)
                {
                    AppendLog("Restore: starting server...", LogType.Warning);
                    TopStart_Click(this, new RoutedEventArgs());
                    AppendLog("Restore: server start issued.", LogType.Info);
                }

                await LoadBackupsAsync();
            }
            catch (Exception ex)
            {
                AppendLog($"Restore failed: {ex.Message}", LogType.Error);
            }
        }
        private async Task StopServerGracefullyAsync()
        {
            if (server?.Process == null || server.Process.HasExited)
                return;

            try { server.SendCommand("stop"); } catch { }

            const int timeoutMs = 15000;

            bool exited = await Task.Run(() =>
            {
                try { return server.Process.WaitForExit(timeoutMs); }
                catch { return true; }
            });

            if (!exited)
            {
                AppendLog("Stop timed out; forcing termination.", LogType.Error);
                try { server.Process.Kill(entireProcessTree: true); } catch { }
            }
        }
        private async Task<string> CreateManualBackupZipAsync()
        {
            Directory.CreateDirectory(backupsFolder);

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var manualName = $"ManualBackup_{stamp}.zip";
            var manualPath = Path.Combine(backupsFolder, manualName);

            if (File.Exists(manualPath))
                manualPath = Path.Combine(backupsFolder, $"ManualBackup_{stamp}_{Guid.NewGuid():N}.zip");

            if (server?.IsRunning == true)
            {
                var before = Directory.EnumerateFiles(backupsFolder, "*.zip")
                    .Select(p => new FileInfo(p))
                    .OrderByDescending(fi => fi.CreationTimeUtc)
                    .FirstOrDefault()?.CreationTimeUtc ?? DateTime.MinValue;

                server.SendCommand("backup");

                var deadline = DateTime.UtcNow.AddSeconds(60);
                FileInfo? newest = null;

                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(500);

                    newest = Directory.EnumerateFiles(backupsFolder, "*.zip")
                        .Select(p => new FileInfo(p))
                        .OrderByDescending(fi => fi.CreationTimeUtc)
                        .FirstOrDefault();

                    if (newest == null) continue;
                    if (newest.CreationTimeUtc <= before) { newest = null; continue; }

                    long size1 = newest.Length;
                    await Task.Delay(500);
                    newest.Refresh();
                    long size2 = newest.Length;

                    if (size2 > 0 && size2 == size1)
                        break;

                    newest = null;
                }

                if (newest == null)
                    throw new Exception("Manual backup zip did not appear after sending backup command.");

                File.Move(newest.FullName, manualPath);
                return manualPath;
            }

            if (!Directory.Exists(universeFolder))
                throw new Exception($"Universe folder not found: {universeFolder}");

            await Task.Run(() =>
            {
                using var zip = ZipFile.Open(manualPath, ZipArchiveMode.Create);

                AddDirectoryToZip(zip, universeFolder, zipRootPrefix: "universe");
            });

            return manualPath;
        }

        private static void AddDirectoryToZip(ZipArchive zip, string sourceDir, string zipRootPrefix)
        {
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(sourceDir, file).Replace("\\", "/");
                var entryName = $"{zipRootPrefix}/{rel}";
                zip.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
            }
        }
        private async Task RestoreUniverseFromBackupAsync(string zipPath)
        {
            if (!File.Exists(zipPath))
                throw new FileNotFoundException("Backup zip not found.", zipPath);

            var tempRoot = Path.Combine(Path.GetTempPath(), "HyLordServerUtil_Restore_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            try
            {
                await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, tempRoot));

                var extractedUniverse = Path.Combine(tempRoot, "universe");
                if (!Directory.Exists(extractedUniverse))
                {
                    extractedUniverse = tempRoot;
                }

                if (Directory.Exists(universeFolder))
                {
                    await Task.Run(() => Directory.Delete(universeFolder, recursive: true));
                }

                await Task.Run(() => CopyDirectory(extractedUniverse, universeFolder));
            }
            finally
            {
                try { Directory.Delete(tempRoot, true); } catch { }
            }
        }

        private static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var name = Path.GetFileName(file);
                File.Copy(file, Path.Combine(destDir, name), overwrite: true);
            }

            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                var name = Path.GetFileName(dir);
                CopyDirectory(dir, Path.Combine(destDir, name));
            }
        }
        private void AutoBackup_Changed(object sender, RoutedEventArgs e)
        {
            ScheduleAutoBackup();
        }
        private void ScheduleAutoBackup()
        {
            autoBackupArmTimer?.Dispose();
            autoBackupArmTimer = null;
            nextAutoBackupLocal = null;

            if (CfgAutoBackupEnabled.IsChecked != true)
            {
                CfgNextAutoBackup.Text = "Next: --";
                return;
            }

            int hh = int.TryParse(CfgAutoBackupHour.SelectedItem?.ToString(), out var h) ? h : 3;
            int mm = int.TryParse(CfgAutoBackupMinute.SelectedItem?.ToString(), out var m) ? m : 0;

            var now = DateTime.Now;
            var next = new DateTime(now.Year, now.Month, now.Day, hh, mm, 0);
            if (next <= now) next = next.AddDays(1);

            nextAutoBackupLocal = next;
            CfgNextAutoBackup.Text = $"Next: {next:yyyy-MM-dd HH:mm}";

            var due = next.ToUniversalTime() - DateTime.UtcNow;
            if (due < TimeSpan.Zero) due = TimeSpan.Zero;

            autoBackupArmTimer = new Timer(_ =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (CfgAutoBackupEnabled.IsChecked != true) return;

                    if (server?.IsRunning == true)
                    {
                        server.SendCommand("backup");
                        AppendLog("Auto backup triggered (backup command sent).", LogType.Warning);

                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(1500);
                            await LoadBackupsAsync();
                        });
                    }
                    else
                    {
                        AppendLog("Auto backup time reached, but server is offline. Skipping.", LogType.Info);
                    }

                    ScheduleAutoBackup();
                }));
            }, null, due, Timeout.InfiniteTimeSpan);
        }

        private void LoadAutoBackupFromConfig()
        {
            if (config == null) return;

            var node = config.GetNode("AutoBackup") as System.Text.Json.Nodes.JsonObject;
            bool enabled = node?["Enabled"]?.GetValue<bool>() ?? false;
            string time = node?["Time"]?.GetValue<string>() ?? "03:00";

            CfgAutoBackupEnabled.IsChecked = enabled;

            var parts = time.Split(':');
            var hh = (parts.Length > 0) ? parts[0].PadLeft(2, '0') : "03";
            var mm = (parts.Length > 1) ? parts[1].PadLeft(2, '0') : "00";

            if (CfgAutoBackupHour.Items.Contains(hh)) CfgAutoBackupHour.SelectedItem = hh;
            if (CfgAutoBackupMinute.Items.Contains(mm)) CfgAutoBackupMinute.SelectedItem = mm;
        }

        private void SaveAutoBackupToConfig()
        {
            if (config == null) return;

            var enabled = CfgAutoBackupEnabled.IsChecked == true;
            var hh = CfgAutoBackupHour.SelectedItem?.ToString() ?? "03";
            var mm = CfgAutoBackupMinute.SelectedItem?.ToString() ?? "00";

            var ar = new System.Text.Json.Nodes.JsonObject
            {
                ["Enabled"] = enabled,
                ["Time"] = $"{hh}:{mm}"
            };

            config.SetNode("AutoBackup", ar);
        }









        private bool authBannerVisible;
        public bool AuthBannerVisible
        {
            get => authBannerVisible;
            set
            {
                if (authBannerVisible == value) return;
                authBannerVisible = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(AuthBannerVisible)));
            }
        }
                private string authUrl = "";
        public string AuthUrl
        {
            get => authUrl;
            set
            {
                if (authUrl == value) return;
                authUrl = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(AuthUrl)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(AuthBannerText)));
            }
        }

        public string AuthBannerText =>
            string.IsNullOrWhiteSpace(AuthUrl)
                ? "Waiting for login URL..."
                : "Open the login URL in your browser to authenticate this server session.";
        private void AuthOpen_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(AuthUrl))
                return;

            if (TryOpenUrl(AuthUrl))
            {
                //AuthBannerVisible = false;
                AppendLog("Opened auth URL in browser.", LogType.Info);
            }
        }

        private void AuthCopy_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(AuthUrl))
                return;

            try
            {
                Clipboard.SetText(AuthUrl);
                AppendLog("Auth URL copied to clipboard.", LogType.Info);
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to copy auth URL: {ex.Message}", LogType.Error);
            }
            //AuthBannerVisible = false;
        }

        private void AuthDismiss_Click(object sender, RoutedEventArgs e)
        {
            AuthBannerVisible = false;
        }

        private void OnAuthRequired()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                AuthUrl = "";
                AuthBannerVisible = true;

                AppendLog("Auth required: requesting browser login...", LogType.Warning);
            }));
        }

        private void OnAuthUrlReceived(string url)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (AuthUrl == url && AuthBannerVisible)
                    return;

                AuthUrl = url;
                AuthBannerVisible = true;

                AppendLog("Auth URL received. Use the banner to open/copy it.", LogType.Info);
            }));
        }
        private void OnAuthSucceeded()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                AuthBannerVisible = false;
                AuthUrl = "";

                AppendLog("Authentication successful.", LogType.Info);
            }));
        }


        private bool TryOpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to open URL: {ex.Message}", LogType.Error);

                try
                {
                    Clipboard.SetText(url);
                    AppendLog("Auth URL copied to clipboard.", LogType.Info);
                }
                catch { }

                return false;
            }
        }






    }
}