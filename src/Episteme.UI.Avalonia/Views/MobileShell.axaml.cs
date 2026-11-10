using Avalonia.Controls;
using Episteme.UI.Avalonia.ViewModels;

namespace Episteme.UI.Avalonia.Views;

public partial class MobileShell : UserControl
{
    public MobileShell() => InitializeComponent();
    public MainViewModel? ViewModel => DataContext as MainViewModel;
}
