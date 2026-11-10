using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Episteme.UI.Avalonia.Api;
using Episteme.UI.Avalonia.ViewModels;
using Episteme.UI.Avalonia.Views;

namespace Episteme.UI.Avalonia;

public partial class App : Application
{
    public MainViewModel? ViewModel { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // One ViewModel instance for both heads: Desktop (Window) and Android (single view).
        var api = new EpistemeApiClient();
        var vm = new MainViewModel(api);
        ViewModel = vm;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow { DataContext = vm };
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
            singleView.MainView = new MobileShell { DataContext = vm };

        _ = vm.LoadAsync();
        base.OnFrameworkInitializationCompleted();
    }
}
