using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Skua.Avalonia;
using Skua.Avalonia.Services;
using Skua.Core.AppStartup;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Utils;
using Skua.Core.ViewModels;
using System.Globalization;

namespace Skua.App.Avalonia;

public sealed partial class App : Application
{
    public IServiceProvider Services { get; private set; } = null!;
    private IScriptInterface _bot = null!;
    private SkuaStartupHandler? _startup;
    private ControlServer? _controlServer;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

        Services = ConfigureServices();
        Services.GetRequiredService<ISettingsService>().SetApplicationVersion();

        Services.GetRequiredService<IClientFilesService>().CreateDirectories();
        Services.GetRequiredService<IClientFilesService>().CreateFiles();
        Task.Factory.StartNew(async () => await Services.GetRequiredService<IScriptServers>().GetServers());

        _bot = Services.GetRequiredService<IScriptInterface>();
        _ = Services.GetRequiredService<ILogService>();

        // Keep the script loader alive even when the bot window has not been
        // opened yet; --run-script sends StartScriptMessage before any UI.
        _ = Services.GetRequiredService<ScriptLoaderViewModel>();
        _bot.Flash.FlashCall += (name, _) =>
        {
            if (name is "loaded" or "requestLoadGame" or "pre-load")
                Console.WriteLine($"[flash] {name}");
        };

        string[] args = Environment.GetCommandLineArgs();
        _startup = new SkuaStartupHandler(args, _bot, Services.GetRequiredService<ISettingsService>(), Services.GetRequiredService<IThemeService>());
        _startup.Execute();

        // Local HTTP bridge for Skua.Mcp. It is intentionally separate from
        // the MCP stdio transport so an AI agent on another machine can
        // connect over the LAN without embedding the game.
        try
        {
            _controlServer = new ControlServer(
                _bot,
                Services.GetRequiredService<IScriptManager>(),
                Services.GetRequiredService<ILogService>());
            _controlServer.Start();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[control] failed to start: {ex.Message}");
            _controlServer = null;
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindow mainWindow = new();
            desktop.MainWindow = mainWindow;
            desktop.ShutdownRequested += OnShutdownRequested;

            try { Services.GetRequiredService<IPluginManager>().Initialize(); }
            catch (Exception ex) { Console.WriteLine($"[skua] plugin init failed: {ex.Message}"); }

            try { Services.GetRequiredService<IHotKeyService>().Reload(); }
            catch (Exception ex) { Console.WriteLine($"[skua] hotkey init failed: {ex.Message}"); }

            if (Environment.GetEnvironmentVariable("SKUA_OPEN_TOOLS") == "1")
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        Services.GetRequiredService<IWindowService>().ShowWindow<BotWindowViewModel>();
                        Console.WriteLine("[skua] tools window opened");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[skua] tools window failed: {ex}");
                    }
                }, DispatcherPriority.Background);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        try { _controlServer?.Dispose(); } catch { }
        _controlServer = null;

        try { await Ioc.Default.GetRequiredService<IScriptManager>().StopScript(); } catch { }
        try { await ((IScriptInterfaceManager)_bot).StopTimerAsync(); } catch { }

        try { Services.GetRequiredService<ICaptureProxy>().Stop(); } catch { }
        try { await ((IAsyncDisposable)Services.GetRequiredService<IScriptBoost>()).DisposeAsync(); } catch { }
        try { await ((IAsyncDisposable)Services.GetRequiredService<IScriptBotStats>()).DisposeAsync(); } catch { }
        try { await ((IAsyncDisposable)Services.GetRequiredService<IScriptDrop>()).DisposeAsync(); } catch { }

        try { Services.GetRequiredService<IFlashUtil>().Dispose(); } catch { }
        try { _startup?.Dispose(); } catch { }
        _startup = null;

        try { WeakReferenceMessenger.Default.Cleanup(); } catch { }
        try { WeakReferenceMessenger.Default.Reset(); } catch { }
        try { StrongReferenceMessenger.Default.Reset(); } catch { }
    }

    private IServiceProvider ConfigureServices()
    {
        IServiceCollection services = new ServiceCollection();

        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddAvaloniaServices();
        services.AddSingleton<WebViewHost>();
        services.AddSingleton<WebFlashUtil>();
        services.AddSingleton<IFlashUtil>(provider => provider.GetRequiredService<WebFlashUtil>());

        services.AddCommonServices();
        services.AddScriptableObjects();
        services.AddCompiler();
        services.AddSkuaMainAppViewModels();

        ServiceProvider provider = services.BuildServiceProvider();
        Ioc.Default.ConfigureServices(provider);

        return provider;
    }

    // --- Dialog button click handlers ---

    public void DialogOK_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.FindAncestorOfType<Window>() is HostDialog dialog)
        {
            dialog.DialogResult = true;
            dialog.Close();
        }
    }

    public void DialogYesNo_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.FindAncestorOfType<Window>() is HostDialog dialog)
        {
            dialog.DialogResult = btn.Tag?.ToString() == "True";
            dialog.Close();
        }
    }

    public void CustomDialogButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.FindAncestorOfType<Window>() is HostDialog dialog)
        {
            string text = btn.Content?.ToString() ?? string.Empty;
            if (dialog.DataContext is CustomDialogViewModel vm)
            {
                int index = vm.Buttons.IndexOf(text);
                vm.Result = new DialogResult(text, index);
            }
            dialog.DialogResult = true;
            dialog.Close();
        }
    }
}
