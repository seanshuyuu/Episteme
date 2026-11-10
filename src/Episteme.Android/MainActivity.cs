using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;
using Episteme.UI.Avalonia;

namespace Episteme.Android;

[Activity(
    Label = "Episteme 知书",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity<App>
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // P5 skeleton: no push / deeplink / offline queue yet. See README.md for the boundary list.
    }
}
