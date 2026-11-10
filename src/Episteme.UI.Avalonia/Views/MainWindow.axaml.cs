using Avalonia.Controls;
using Episteme.UI.Avalonia.ViewModels;

namespace Episteme.UI.Avalonia.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
    public MainViewModel? ViewModel => DataContext as MainViewModel;
}
