using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;

namespace Skua.Avalonia.Services;

public static class ConfigureServices
{
    public static IServiceCollection AddAvaloniaServices(this IServiceCollection services)
    {
        // IFlashUtil is registered by the app host: it needs the embedded
        // NativeWebView, which does not exist until the game view is created.
        services.AddSingleton<IDispatcherService, AvaloniaDispatcherService>();
        services.AddSingleton<IClipboardService, AvaloniaClipboardService>();
        services.AddSingleton<IDialogService, AvaloniaDialogService>();
        services.AddSingleton<IWindowService, AvaloniaWindowService>();
        services.AddSingleton<IFileDialogService, AvaloniaFileDialogService>();
        services.AddSingleton<IHotKeyService, AvaloniaHotKeyService>();
        services.AddSingleton<IThemeService, AvaloniaThemeService>();
        services.AddSingleton<ISoundService, CrossPlatformSoundService>();

        return services;
    }
}
