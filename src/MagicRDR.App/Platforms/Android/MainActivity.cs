using Android.App;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.OS;
using AndroidX.Core.View;
using AColor = Android.Graphics.Color;

namespace MagicRDR.App;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
                           ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density |
                           ConfigChanges.Locale | ConfigChanges.LayoutDirection | ConfigChanges.FontScale |
                           ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation |
                           ConfigChanges.ColorMode)]
public class MainActivity : MauiAppCompatActivity
{
    // Same colour as the page header / action bar, so the system bars blend into the UI.
    private static readonly AColor BarColor = AColor.ParseColor("#211B16");

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ApplySystemBars();
    }

    public override void OnConfigurationChanged(Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ApplySystemBars();
    }

    /// <summary>
    /// Android 10-14: bars are opaque and take their colour from the window.
    /// Android 15-16: apps are edge-to-edge, the bars are transparent and the window background shows through.
    /// In both cases the icons must be light, because the app is dark.
    /// </summary>
    private void ApplySystemBars()
    {
        var window = Window;
        if (window == null)
        {
            return;
        }

        window.SetBackgroundDrawable(new ColorDrawable(BarColor));

        if (!OperatingSystem.IsAndroidVersionAtLeast(35))
        {
#pragma warning disable CA1422, CS0618
            window.SetStatusBarColor(BarColor);
            window.SetNavigationBarColor(BarColor);
#pragma warning restore CA1422, CS0618
        }

        var controller = WindowCompat.GetInsetsController(window, window.DecorView);
        if (controller != null)
        {
            controller.AppearanceLightStatusBars = false;
            controller.AppearanceLightNavigationBars = false;
        }
    }
}
