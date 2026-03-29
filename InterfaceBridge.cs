using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Modules.AdminManager.Shared;
using Sharp.Modules.ClientPreferences.Shared;
using Sharp.Modules.LocalizerManager.Shared;
using Sharp.Modules.MenuManager.Shared;
using Sharp.Shared;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;

namespace QuakeSounds;

internal interface IModule
{
    bool Init();

    void OnPostInit(ServiceProvider provider)
    {
    }

    void Shutdown()
    {
    }
}

internal interface IManager
{
    bool Init();

    void OnPostInit(ServiceProvider provider)
    {
    }

    void Shutdown();
}

internal sealed class InterfaceBridge
{
    private readonly ISharedSystem _sharedSystem;

    internal static InterfaceBridge Instance { get; private set; } = null!;

    public InterfaceBridge(string        dllPath,
                           string        sharpPath,
                           Version       version,
                           ISharedSystem sharedSystem,
                           bool          hotReload,
                           bool          debug)
    {
        DllPath       = dllPath;
        SharpPath     = sharpPath;
        Version       = version;
        _sharedSystem = sharedSystem;
        HotReload     = hotReload;
        Debug         = debug;

        ModSharp        = sharedSystem.GetModSharp();
        ConVarManager   = sharedSystem.GetConVarManager();
        EventManager    = sharedSystem.GetEventManager();
        ClientManager   = sharedSystem.GetClientManager();
        EntityManager   = sharedSystem.GetEntityManager();
        SoundManager    = sharedSystem.GetSoundManager();
        HookManager     = sharedSystem.GetHookManager();

        SharpModule = sharedSystem.GetSharpModuleManager();

        Instance = this;
    }

    public string  DllPath   { get; }
    public string  SharpPath { get; }
    public Version Version   { get; }
    public bool    HotReload { get; }
    public bool    Debug     { get; }

    public IModSharp        ModSharp        { get; }
    public IConVarManager   ConVarManager   { get; }
    public IEventManager    EventManager    { get; }
    public IClientManager   ClientManager   { get; }
    public IEntityManager   EntityManager   { get; }
    public ISoundManager    SoundManager    { get; }
    public IHookManager     HookManager     { get; }

    private ISharpModuleManager SharpModule { get; }

    public IGameRules  GameRules  => ModSharp.GetGameRules();
    public IGlobalVars GlobalVars => ModSharp.GetGlobals();

    public ILoggerFactory LoggerFactory => _sharedSystem.GetLoggerFactory();

    private ILocalizerManager? _cachedLocalizerManager;
    private IClientPreference? _cachedClientPreference;
    private IAdminManager?     _cachedAdminManager;
    private IMenuManager?      _cachedMenuManager;
    private bool               _adminManagerResolved;
    private bool               _menuManagerResolved;

    public ILocalizerManager GetLocalizerManager()
    {
        if (_cachedLocalizerManager is not null)
        {
            return _cachedLocalizerManager;
        }

        var iface = SharpModule.GetRequiredSharpModuleInterface<ILocalizerManager>(ILocalizerManager.Identity);

        if (iface is { IsAvailable: true, Instance: { } instance })
        {
            _cachedLocalizerManager = instance;
            return instance;
        }

        throw new InvalidOperationException($"Required module '{ILocalizerManager.Identity}' could not be loaded or is unavailable.");
    }

    public IClientPreference? GetClientPreference()
    {
        if (_cachedClientPreference is not null)
        {
            return _cachedClientPreference;
        }

        var iface = SharpModule.GetOptionalSharpModuleInterface<IClientPreference>(IClientPreference.Identity);

        if (iface is { IsAvailable: true, Instance: { } instance })
        {
            _cachedClientPreference = instance;
            return instance;
        }

        return null;
    }

    public IAdminManager? GetAdminManager()
    {
        if (_adminManagerResolved)
            return _cachedAdminManager;

        var iface = SharpModule.GetOptionalSharpModuleInterface<IAdminManager>(IAdminManager.Identity);

        if (iface is { IsAvailable: true, Instance: { } instance })
            _cachedAdminManager = instance;

        _adminManagerResolved = true;
        return _cachedAdminManager;
    }

    public IMenuManager? GetMenuManager()
    {
        if (_menuManagerResolved)
            return _cachedMenuManager;

        var iface = SharpModule.GetOptionalSharpModuleInterface<IMenuManager>(IMenuManager.Identity);

        if (iface is { IsAvailable: true, Instance: { } instance })
            _cachedMenuManager = instance;

        _menuManagerResolved = true;
        return _cachedMenuManager;
    }
}
