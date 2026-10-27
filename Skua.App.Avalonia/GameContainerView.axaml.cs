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
            GameWebView.Navigate(new Uri(BuildGameUrl()));
            StatusText.Text = "Loading AQW...";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Game server failed: {ex.Message}";
            Console.WriteLine($"[skua] game server failed: {ex}");
        }
    }

    private string BuildGameUrl()
    {
        List<string> query = new();
        string renderer = (Environment.GetEnvironmentVariable("WAYFER_RENDERER") ?? string.Empty).Trim().ToLowerInvariant();
        if (renderer is "webgl" or "wgpu-webgl" or "webgpu" or "canvas")
            query.Add($"renderer={renderer}");
        if (Environment.GetEnvironmentVariable("WAYFER_SMOOTH") == "0")
            query.Add("smooth=0");
        if (Environment.GetEnvironmentVariable("WAYFER_LOWQ") == "0")
            query.Add("lowq=0");
        if (Environment.GetEnvironmentVariable("WAYFER_VERBOSE") == "1")
            query.Add("verbose=1");

        return query.Count == 0 ? _server.BaseUrl : $"{_server.BaseUrl}?{string.Join("&", query)}";
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
