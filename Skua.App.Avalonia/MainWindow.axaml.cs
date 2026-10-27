using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.DependencyInjection;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = Ioc.Default.GetRequiredService<MainViewModel>();
        TrySetIcon();
    }

    private void TrySetIcon()
    {
        try
        {
            using Stream stream = AssetLoader.Open(new Uri("avares://Skua.App.Avalonia/Assets/WayferIcon.png"));
            Icon = new WindowIcon(stream);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[wayfer] window icon failed: {ex.Message}");
        }
    }
}
