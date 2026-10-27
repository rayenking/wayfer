using Avalonia.Controls;

namespace Skua.App.Avalonia;

/// <summary>
/// Late-bound access to the embedded webview. The DI container creates
/// WebFlashUtil before the MainWindow exists, so the view assigns the
/// control here once it is loaded.
/// </summary>
public sealed class WebViewHost
{
    public NativeWebView? View { get; set; }
}
