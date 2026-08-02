using System.Windows;

namespace DeskOrganizer.Updater;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 参数: <sourceFile> <targetExePath> <exeName>
        if (e.Args.Length < 3)
        {
            Shutdown(1);
            return;
        }
        Properties["SourceFile"] = e.Args[0];
        Properties["TargetExePath"] = e.Args[1];
        Properties["ExeName"] = e.Args[2];
        base.OnStartup(e);
    }
}
