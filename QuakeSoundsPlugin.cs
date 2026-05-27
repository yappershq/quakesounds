using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QuakeSounds.Managers;
using Sharp.Shared;
using Sharp.Shared.Abstractions;

namespace QuakeSounds;

public class QuakeSoundsPlugin : IModSharpModule
{
    private readonly ISharedSystem              _shared;
    private readonly ServiceProvider            _serviceProvider;
    private readonly ILogger<QuakeSoundsPlugin> _logger;
    private readonly InterfaceBridge            _bridge;

    public QuakeSoundsPlugin(
        ISharedSystem  sharedSystem,
        string         dllPath,
        string         sharpPath,
        Version        version,
        IConfiguration configuration,
        bool           hotReload)
    {
        _shared = sharedSystem;
        var loggerFactory = sharedSystem.GetLoggerFactory();
        _logger = loggerFactory.CreateLogger<QuakeSoundsPlugin>();

        var bridge = new InterfaceBridge(dllPath,
                                         sharpPath,
                                         version,
                                         sharedSystem,
                                         hotReload,
                                         sharedSystem.GetModSharp()
                                                     .HasCommandLine("-debug"));

        var services = new ServiceCollection();

        services.AddSingleton(bridge);
        services.AddSingleton(loggerFactory);
        services.AddSingleton(sharedSystem);
        services.AddLogging();
        services.AddModuleDi();

        _bridge = bridge;

        _serviceProvider = services.BuildServiceProvider();
    }

    public bool Init()
    {
        foreach (var service in _serviceProvider.GetServices<IModule>())
        {
            try
            {
                if (service.Init())
                {
                    if (_bridge.Debug)
                    {
                        _logger.LogInformation("{Service} Initialized", service.GetType().FullName);
                    }

                    continue;
                }

                _logger.LogError("Failed to init {Service}!", service.GetType().FullName);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to init {Service}!", service.GetType().FullName);
            }

            return false;
        }

        return true;
    }

    public void PostInit()
    {
        foreach (var service in _serviceProvider.GetServices<IModule>())
        {
            try
            {
                service.OnPostInit(_serviceProvider);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error when calling PostInit for {Service}", service.GetType().FullName);
            }
        }
    }

    public void Shutdown()
    {
        foreach (var service in _serviceProvider.GetServices<IModule>())
        {
            try
            {
                service.Shutdown();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error when calling Shutdown for {Service}", service.GetType().FullName);
            }
        }

    }

    public void OnAllModulesLoaded()
    {
        _bridge.GetLocalizerManager().LoadLocaleFile("quakesounds", true);

        var soundPackManager = _serviceProvider.GetRequiredService<SoundPackManager>();
        soundPackManager.LoadPacks(_bridge.SharpPath);

        foreach (var service in _serviceProvider.GetServices<IModule>())
        {
            try
            {
                service.OnAllModulesLoaded(_serviceProvider);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error when calling OnAllModulesLoaded for {Service}", service.GetType().FullName);
            }
        }
    }

    string IModSharpModule.DisplayName   => "QuakeSounds";
    string IModSharpModule.DisplayAuthor => "prefix";
}
