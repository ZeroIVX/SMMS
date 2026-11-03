using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SMMS.Core.Ports;
using SMMS.Infrastructure.Http;
using SMMS.Infrastructure.Mods;
using SMMS.Infrastructure.Nexus;
using SMMS.Infrastructure.Platform.Unsupported;
using SMMS.Infrastructure.Platform.Windows;
using SMMS.Infrastructure.Storage;

namespace SMMS.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册基础设施实现。平台差异在此处一次性决定：
    /// Windows 使用原生实现，其他平台注册显式不支持的占位实现。
    /// </summary>
    public static IServiceCollection AddSmmsInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<IProfileManifestStore, JsonProfileManifestStore>();
        services.AddSingleton<IModScanner, FileSystemModScanner>();
        services.AddSingleton<INexusCatalog, NexusGraphQlCatalog>();

        // 公共端点、无账号要求，但无人值守的长驻客户端应显式声明 UA。
        services.AddSingleton<ISmapiCatalog>(provider =>
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SMMS/0.1");
            return new SmapiCatalogClient(http, provider.GetRequiredService<ILogger<SmapiCatalogClient>>());
        });

        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IGameLocator, WindowsGameLocator>();
            services.AddSingleton<IGameLauncher, WindowsGameLauncher>();
            services.AddSingleton<IProtocolRegistrar, WindowsProtocolRegistrar>();
            services.AddSingleton<IFileSystemLinker, WindowsFileSystemLinker>();
        }
        else
        {
            services.AddSingleton<IGameLocator, UnsupportedGameLocator>();
            services.AddSingleton<IGameLauncher, UnsupportedGameLauncher>();
            services.AddSingleton<IProtocolRegistrar, UnsupportedProtocolRegistrar>();
            services.AddSingleton<IFileSystemLinker, UnsupportedFileSystemLinker>();
        }

        return services;
    }
}
