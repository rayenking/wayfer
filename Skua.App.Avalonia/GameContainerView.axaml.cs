using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.DependencyInjection;
using Skua.Core.Interfaces;

namespace Skua.App.Avalonia;

public partial class GameContainerView : UserControl
{
    private readonly LocalGameServer _server = new();
    private bool _started;
    private WebFlashUtil? _flashUtil;

    public GameContainerView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DetachedFromVisualTree += OnDetached;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_started)
            return;
        _started = true;

        WebViewHost host = Ioc.Default.GetRequiredService<WebViewHost>();
        host.View = GameWebView;

        _flashUtil = Ioc.Default.GetRequiredService<WebFlashUtil>();
        GameWebView.WebMessageReceived += OnWebMessageReceived;

        try
        {
            _server.Start();
            GameWebView.Navigate(new Uri(_server.BaseUrl));
            StatusText.Text = "Loading AQW...";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Game server failed: {ex.Message}";
            Console.WriteLine($"[skua] game server failed: {ex}");
        }
    }

    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        _flashUtil?.HandleWebMessage(e.Body);
        if (e.Body.Contains("\"loaded\"", StringComparison.Ordinal))
            LoadingOverlay.IsVisible = false;
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _server.Dispose();
    }
}
