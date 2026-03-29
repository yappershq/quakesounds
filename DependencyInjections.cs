using Microsoft.Extensions.DependencyInjection;
using QuakeSounds.Managers;
using QuakeSounds.Modules;

namespace QuakeSounds;

internal static class DependencyInjections
{
    extension(IServiceCollection services)
    {
        public void AddModuleDi()
        {
            services.AddSingleton<SoundPackManager>();
            services.AddSingleton<IModule, QuakeSoundsModule>();
        }
    }
}
