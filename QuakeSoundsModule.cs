using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Extensions.GameEventManager;
using Sharp.Modules.ClientPreferences.Shared;
using Sharp.Modules.LocalizerManager.Shared;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.GameEvents;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;

namespace QuakeSounds;

internal sealed class QuakeSoundsModule : IModule, IGameListener, IClientListener
{
    private readonly InterfaceBridge            _bridge;
    private readonly IGameEventManager         _gameEventManager;
    private readonly ILogger<QuakeSoundsModule> _logger;

    // Per-player kill streak tracking (slot-indexed)
    private readonly int[] _killStreaks = new int[64];

    // Whether first blood has occurred this round
    private bool _firstBloodOccurred;

    // ConVars for configuration
    private IConVar? _cvEnabled;
    private IConVar? _cvEnableKillStreaks;
    private IConVar? _cvEnableFirstBlood;
    private IConVar? _cvEnableHeadshot;
    private IConVar? _cvEnableCenterMsg;
    private IConVar? _cvEnableDuringWarmup;
    private IConVar? _cvResetOnDeath;

    // Cached module interfaces
    private IClientPreference? _clientPreference;
    private ILocalizerManager? _localizerManager;

    // Cookie key for mute preference
    private const string MuteCookieKey = "quakesounds_muted";

    // Mute state cache (slot-indexed, null = not loaded)
    private readonly bool?[] _muteCache = new bool?[64];

    // Sound event mappings
    private static readonly Dictionary<int, string> KillStreakSounds = new()
    {
        [2] = "crafting.killstreaks.doublekill",
        [3] = "crafting.killstreaks.3_killingspree",
        [4] = "crafting.killstreaks.4_dominating",
        [5] = "crafting.killstreaks.5_holyshit",
        [6] = "crafting.killstreaks.6_wickedsick",
        [7] = "crafting.killstreaks.7_monsterkill",
        [8] = "crafting.killstreaks.8_unstoppable",
        [9] = "crafting.killstreaks.9_godlike",
    };

    private static readonly Dictionary<int, string> KillStreakNames = new()
    {
        [2] = "DOUBLE KILL",
        [3] = "KILLING SPREE",
        [4] = "DOMINATING",
        [5] = "HOLY SHIT",
        [6] = "WICKED SICK",
        [7] = "MONSTER KILL",
        [8] = "UNSTOPPABLE",
        [9] = "GODLIKE",
    };

    private static readonly Dictionary<int, string> KillStreakColors = new()
    {
        [2] = "#FFFFFF",
        [3] = "#00FF00",
        [4] = "#FFFF00",
        [5] = "#FF8800",
        [6] = "#FF4400",
        [7] = "#FF0000",
        [8] = "#FF00FF",
        [9] = "#FF0000",
    };

    public QuakeSoundsModule(InterfaceBridge            bridge,
                             IGameEventManager          gameEventManager,
                             ILogger<QuakeSoundsModule> logger)
    {
        _bridge           = bridge;
        _gameEventManager = gameEventManager;
        _logger           = logger;
    }

    public bool Init()
    {
        // Register ConVars
        _cvEnabled            = _bridge.ConVarManager.CreateConVar("qs_enabled",        true,  "Enable QuakeSounds plugin");
        _cvEnableKillStreaks   = _bridge.ConVarManager.CreateConVar("qs_killstreaks",    true,  "Enable kill streak sounds");
        _cvEnableFirstBlood    = _bridge.ConVarManager.CreateConVar("qs_firstblood",     true,  "Enable first blood sound");
        _cvEnableHeadshot      = _bridge.ConVarManager.CreateConVar("qs_headshot",       true,  "Enable headshot kill sound");
        _cvEnableCenterMsg     = _bridge.ConVarManager.CreateConVar("qs_center_message", true,  "Enable center HTML messages for streaks");
        _cvEnableDuringWarmup  = _bridge.ConVarManager.CreateConVar("qs_during_warmup",  false, "Enable sounds during warmup");
        _cvResetOnDeath        = _bridge.ConVarManager.CreateConVar("qs_reset_on_death", false, "Reset kill streak on death (instead of only on round start)");

        // Install listeners
        _bridge.ModSharp.InstallGameListener(this);
        _bridge.ClientManager.InstallClientListener(this);

        // Listen for game events
        _gameEventManager.ListenEvent("player_death", OnPlayerDeath);

        // Register chat command: .quakemute
        _bridge.ClientManager.InstallCommandCallback("quakemute", OnQuakeMuteCommand);

        _logger.LogInformation("[QuakeSounds] Initialized");

        return true;
    }

    public void OnPostInit(ServiceProvider provider)
    {
        // Resolve optional interfaces after all modules loaded
        _clientPreference = _bridge.GetClientPreference();

        try
        {
            _localizerManager = _bridge.GetLocalizerManager();
        }
        catch
        {
            _logger.LogWarning("[QuakeSounds] LocalizerManager not available — using fallback messages");
        }

        if (_clientPreference is null)
        {
            _logger.LogWarning("[QuakeSounds] ClientPreference module not available — mute preferences will not persist");
        }
    }

    public void Shutdown()
    {
        _bridge.ModSharp.RemoveGameListener(this);
        _bridge.ClientManager.RemoveClientListener(this);
        _bridge.ClientManager.RemoveCommandCallback("quakemute", OnQuakeMuteCommand);

        Array.Clear(_killStreaks);
        Array.Clear(_muteCache);
    }

    #region IGameListener

    public int ListenerPriority => 0;
    public int ListenerVersion  => IGameListener.ApiVersion;

    public void OnRoundRestart()
    {
        // Reset all kill streaks on round restart
        Array.Clear(_killStreaks);
        _firstBloodOccurred = false;
    }

    #endregion

    #region IClientListener

    int IClientListener.ListenerPriority => 0;
    int IClientListener.ListenerVersion  => IClientListener.ApiVersion;

    public void OnClientPutInServer(IGameClient client)
    {
        if (client.IsFakeClient)
        {
            return;
        }

        _killStreaks[client.Slot] = 0;
        LoadMutePreference(client);
    }

    public void OnClientDisconnected(IGameClient client, NetworkDisconnectionReason reason)
    {
        _killStreaks[client.Slot] = 0;
        _muteCache[client.Slot]  = null;
    }

    #endregion

    #region Mute Preference

    private void LoadMutePreference(IGameClient client)
    {
        if (_clientPreference is null)
        {
            _muteCache[client.Slot] = false;
            return;
        }

        if (!_clientPreference.IsLoaded(client.SteamId))
        {
            _muteCache[client.Slot] = false;
            return;
        }

        var cookie = _clientPreference.GetCookie(client.SteamId, MuteCookieKey);

        _muteCache[client.Slot] = cookie is { Type: CookieValueType.Number } && cookie.GetNumber() == 1;
    }

    private bool IsPlayerMuted(IGameClient client)
    {
        return _muteCache[client.Slot] == true;
    }

    private void ToggleMute(IGameClient client)
    {
        var currentlyMuted = IsPlayerMuted(client);
        var newState        = !currentlyMuted;

        _muteCache[client.Slot] = newState;

        _clientPreference?.SetCookie(client.SteamId, MuteCookieKey, newState ? 1L : 0L);

        if (_localizerManager is not null && _localizerManager.TryGetLocalizer(client, out var localizer))
        {
            var key = newState ? "quakesounds.muted" : "quakesounds.unmuted";
            var msg = localizer.TryGet(key) ?? (newState ? "Sounds muted." : "Sounds unmuted.");
            client.Print(HudPrintChannel.Chat, $" [QuakeSounds] {msg}");
        }
        else
        {
            var msg = newState ? " [QuakeSounds] Sounds muted." : " [QuakeSounds] Sounds unmuted.";
            client.Print(HudPrintChannel.Chat, msg);
        }
    }

    private ECommandAction OnQuakeMuteCommand(IGameClient client, StringCommand command)
    {
        if (client.IsFakeClient)
        {
            return ECommandAction.Skipped;
        }

        ToggleMute(client);
        return ECommandAction.Handled;
    }

    #endregion

    #region Event Handling

    private void OnPlayerDeath(IGameEvent e)
    {
        if (_cvEnabled is not null && !_cvEnabled.GetBool())
        {
            return;
        }

        if (e is not IEventPlayerDeath ev)
        {
            return;
        }

        // Skip during warmup if disabled
        if (_cvEnableDuringWarmup is not null && !_cvEnableDuringWarmup.GetBool())
        {
            if (_bridge.GameRules.IsWarmupPeriod)
            {
                return;
            }
        }

        var victimController = ev.VictimController;

        if (victimController is not { })
        {
            return;
        }

        var victimClient = victimController.GetGameClient();

        // Reset victim streak on death if configured
        if (victimClient is { } vc && _cvResetOnDeath is not null && _cvResetOnDeath.GetBool())
        {
            _killStreaks[vc.Slot] = 0;
        }

        var killerController = ev.KillerController;

        if (killerController is not { })
        {
            return;
        }

        var killerClient = killerController.GetGameClient();

        if (killerClient is not { })
        {
            return;
        }

        if (killerClient.IsFakeClient)
        {
            return;
        }

        // Don't count self-kills
        if (victimController == killerController)
        {
            return;
        }

        // Increment kill streak
        _killStreaks[killerClient.Slot]++;

        var kills = _killStreaks[killerClient.Slot];

        // Process sounds in priority order: first blood > kill streak > headshot
        var soundPlayed = false;

        // First blood check
        if (!_firstBloodOccurred && _cvEnableFirstBlood is not null && _cvEnableFirstBlood.GetBool())
        {
            _firstBloodOccurred = true;
            PlaySoundToAll("kills.firstblood");
            ShowCenterMessageToAll(killerController.PlayerName, "FIRST BLOOD", "#FF0000");
            soundPlayed = true;
        }

        // Kill streak check
        if (!soundPlayed && _cvEnableKillStreaks is not null && _cvEnableKillStreaks.GetBool())
        {
            // Cap at 9 for sound lookup
            var streakKey = Math.Min(kills, 9);

            if (KillStreakSounds.TryGetValue(streakKey, out var streakSound))
            {
                PlaySoundToAll(streakSound);

                if (_cvEnableCenterMsg is not null && _cvEnableCenterMsg.GetBool())
                {
                    var streakName  = KillStreakNames.GetValueOrDefault(streakKey, "GODLIKE");
                    var streakColor = KillStreakColors.GetValueOrDefault(streakKey, "#FF0000");
                    ShowCenterMessageToAll(killerController.PlayerName, streakName, streakColor);
                }

                soundPlayed = true;
            }
        }

        // Headshot sound (to killer only, independent of streak sounds)
        if (ev.Headshot && _cvEnableHeadshot is not null && _cvEnableHeadshot.GetBool())
        {
            PlaySoundToPlayer(killerClient, "effects.hitmark.headshot");
        }
    }

    #endregion

    #region Sound Playback

    private void PlaySoundToAll(string soundEvent)
    {
        var clients = _bridge.ClientManager.GetGameClients(true);

        foreach (var client in clients)
        {
            if (client.IsFakeClient || client.IsHltv)
            {
                continue;
            }

            if (IsPlayerMuted(client))
            {
                continue;
            }

            var controller = client.GetPlayerController();

            if (controller is not { })
            {
                continue;
            }

            controller.EmitSoundClient(soundEvent);
        }
    }

    private static void PlaySoundToPlayer(IGameClient client, string soundEvent)
    {
        var controller = client.GetPlayerController();

        if (controller is not { })
        {
            return;
        }

        controller.EmitSoundClient(soundEvent);
    }

    #endregion

    #region Center HTML Messages

    private void ShowCenterMessageToAll(string playerName, string streakName, string color)
    {
        if (_cvEnableCenterMsg is not null && !_cvEnableCenterMsg.GetBool())
        {
            return;
        }

        var clients = _bridge.ClientManager.GetGameClients(true);

        foreach (var client in clients)
        {
            if (client.IsFakeClient || client.IsHltv)
            {
                continue;
            }

            if (IsPlayerMuted(client))
            {
                continue;
            }

            var controller = client.GetPlayerController();

            if (controller is not { })
            {
                continue;
            }

            var html = BuildCenterHtml(client, playerName, streakName, color);
            controller.Print(HudPrintChannel.Center, html);
        }
    }

    private string BuildCenterHtml(IGameClient client, string playerName, string streakName, string color)
    {
        if (_localizerManager is not null && _localizerManager.TryGetLocalizer(client, out var localizer))
        {
            var localized = localizer.TryGet("quakesounds.streak_message");

            if (localized is not null)
            {
                var formatted = string.Format(localized, playerName, streakName);
                return $"<font color='{color}'><b>{formatted}</b></font>";
            }
        }

        return $"<font color='{color}'><b>{playerName} — {streakName}!</b></font>";
    }

    #endregion
}
