using Microsoft.Extensions.DependencyInjection;
using SMMS.Core.Translation;
using SMMS.UseCases.Game;
using SMMS.UseCases.Importing;
using SMMS.UseCases.Mods;
using SMMS.UseCases.Network;
using SMMS.UseCases.Profiles;
using SMMS.UseCases.Snapshots;
using SMMS.UseCases.Translation;
using SMMS.UseCases.Translation.Providers;
using SMMS.UseCases.Updates;
using SMMS.UseCases.Vanity;

namespace SMMS.UseCases.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSmmsApplication(this IServiceCollection services)
    {
        services.AddSingleton<GameInstallationService>();
        services.AddSingleton<GameLaunchService>();
        services.AddSingleton<LaunchPreflightService>();
        services.AddSingleton<ModLibraryService>();
        services.AddSingleton<ModToggleService>();
        services.AddSingleton<ProfileService>();
        services.AddSingleton<ProfilePackService>();
        services.AddSingleton<SnapshotService>();
        services.AddSingleton<VanityService>();
        services.AddSingleton<DropImportService>();
        services.AddSingleton<NetworkProbeService>();
        services.AddSingleton<SmapiReleaseService>();
        services.AddSingleton<ModNotesService>();
        services.AddSingleton<TranslationService>();

        // 翻译供应商：每个实现自己处理签名与响应解析，服务层只按配置挑选。
        services.AddSingleton<ITranslationProvider, MyMemoryProvider>();
        services.AddSingleton<ITranslationProvider, BaiduProvider>();
        services.AddSingleton<ITranslationProvider, YoudaoProvider>();
        services.AddSingleton<ITranslationProvider, DeepLProvider>();
        services.AddSingleton<ITranslationProvider, GoogleProvider>();
        services.AddSingleton<ITranslationProvider, AzureProvider>();
        services.AddSingleton<ITranslationProvider, CustomProvider>();
        services.AddSingleton<UpdateCheckService>();
        return services;
    }
}
