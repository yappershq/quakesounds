using Microsoft.Extensions.DependencyInjection;

namespace QuakeSounds;

internal static class DependencyInjections
{
    extension(IServiceCollection services)
    {
        public void AddModuleDi()
        {
            services.AddSingleton<IModule, QuakeSoundsModule>();
        }
    }
}
