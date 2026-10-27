using Avalonia;
using System.Reflection;

namespace Skua.App.Avalonia;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--server-only"))
        {
            using LocalGameServer server = new();
            server.Start();
            Console.WriteLine("server-only mode; Ctrl-C to exit");
            Thread.Sleep(Timeout.Infinite);
            return;
        }

        AppDomain currentDomain = AppDomain.CurrentDomain;
        currentDomain.AssemblyResolve += ResolveAssemblies;
        currentDomain.UnhandledException += CurrentDomain_UnhandledException;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Exception ex = (Exception)e.ExceptionObject;
        Console.Error.WriteLine($"Application Crash.\nMessage: {ex.Message}\nStackTrace: {ex.StackTrace}");
    }

    private static Assembly? ResolveAssemblies(object? sender, ResolveEventArgs args)
    {
        if (args.Name.Contains(".resources"))
            return null;

        Assembly? assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.FullName == args.Name);
        if (assembly != null)
            return assembly;

        string assemblyName = new AssemblyName(args.Name).Name + ".dll";
        string assemblyPath = Path.Combine(AppContext.BaseDirectory, "Assemblies", assemblyName);
        if (!File.Exists(assemblyPath))
        {
            assemblyPath = Path.Combine(AppContext.BaseDirectory, assemblyName);
            return File.Exists(assemblyPath) ? Assembly.LoadFrom(assemblyPath) : null;
        }
        return Assembly.LoadFrom(assemblyPath);
    }
}
