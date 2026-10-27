using Avalonia;
using Avalonia.Input.Platform;
using Skua.Core.Interfaces;

namespace Skua.Avalonia.Services;

public class AvaloniaClipboardService : IClipboardService
{
    private global::Avalonia.Input.Platform.IClipboard? GetClipboard()
    {
        return Application.Current?.ApplicationLifetime is global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow?.Clipboard
            : null;
    }

    public void SetText(string text)
    {
        GetClipboard()?.SetTextAsync(text).GetAwaiter().GetResult();
    }

    public void SetData(string format, object data)
    {
        // Avalonia 12 replaced DataObject with the async DataTransfer API.
        // Skua only copies log text, so non-text formats are not needed here.
    }

    public object GetData(string format)
    {
        return string.Empty;
    }

    public string GetText()
    {
        return GetClipboard()?.TryGetTextAsync().GetAwaiter().GetResult() ?? string.Empty;
    }
}
