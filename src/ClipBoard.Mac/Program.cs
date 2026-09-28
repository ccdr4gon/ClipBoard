using Avalonia;

namespace ClipBoard;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS() && !args.Contains("--preview"))
        {
            Console.Error.WriteLine("这是 macOS 版本。Windows 请使用 src/ClipBoard；界面预览使用 --preview。");
            return 1;
        }
        App.Preview = args.Contains("--preview");
        if (OperatingSystem.IsMacOS() && !OperatingSystem.IsMacOSVersionAtLeast(13))
        {
            Console.Error.WriteLine("ClipBoard 需要 macOS 13 或更高版本。");
            return 1;
        }
        if (App.Preview) App.ProfileDirectory = Path.Combine(Path.GetTempPath(), "ClipBoard-Mac-Preview");
        var root = App.ProfileDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "ClipBoard");
        Directory.CreateDirectory(root);
        FileStream instance;
        try { instance = new FileStream(Path.Combine(root, "app.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { Console.Error.WriteLine("ClipBoard 已在运行，请从菜单栏打开。"); return 1; }
        using (instance) BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect()
        .With(new MacOSPlatformOptions { ShowInDock = false }).LogToTrace();
}
