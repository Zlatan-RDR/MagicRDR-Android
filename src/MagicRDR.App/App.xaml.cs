using MagicRDR.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace MagicRDR.App;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    // IMPORTANT: do not take MainPage (or anything that loads XAML) as a constructor parameter.
    // The DI container would build that page BEFORE this constructor runs, i.e. before App.xaml
    // has been loaded, so every {StaticResource ...} in the page fails with XamlParseException.
    public App(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        UserAppTheme = AppTheme.Dark; // the palette is dark-only
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // App.xaml (colours, styles) is fully loaded by now, so the page can be created safely.
        // The view model is a singleton: if Android destroys and re-creates the window, the open archive
        // and any unsaved edits are still there. Scratch files are cleaned when the app process starts.
        var window = new Window(_services.GetRequiredService<MainPage>());

        // When the app leaves the screen Android may kill it at any moment, so the screen state is saved right then.
        window.Stopped += (_, _) => _services.GetRequiredService<MainViewModel>().OnBackgrounded();
        return window;
    }
}
