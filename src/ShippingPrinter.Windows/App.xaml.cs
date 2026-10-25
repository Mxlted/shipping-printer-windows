using System.IO;
using System.Windows;
namespace ShippingPrinter.Windows;
public partial class App : Application
{
    private async void App_Startup(object sender, StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) => { MessageBox.Show(args.Exception.Message, "Shipping Printer", MessageBoxButton.OK, MessageBoxImage.Error); args.Handled = true; };
        var window = new MainWindow(); MainWindow = window; window.Show();
        var paths = e.Args.Where(File.Exists).ToArray();
        if (paths.Length > 0) await window.ImportPathsAsync(paths);
    }
}
