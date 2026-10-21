using MagicRDR.App.Services;
using MagicRDR.App.ViewModels;

namespace MagicRDR.App;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel _viewModel;
    private bool _resumeHooked;

    public MainPage(MainViewModel viewModel, IUpdateService updateService)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        _viewModel.AttachUpdateService(updateService);

        // Extract / save can take minutes on a big archive; a screen that turns off lets Android freeze the app.
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsLoading))
            {
                DeviceDisplay.Current.KeepScreenOn = _viewModel.IsLoading;
            }
        };

        // Remember how far the list is scrolled, and scroll back after a restore.
        FileList.Scrolled += (_, e) => _viewModel.NotifyScrolled(e.FirstVisibleItemIndex);
        _viewModel.ScrollRequested += index =>
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(350), () =>
            {
                if (index > 0)
                {
                    FileList.ScrollTo(index, position: ScrollToPosition.Start, animate: false);
                }
            });
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        HookResume();
        await _viewModel.InitializeAsync();

        // Once the app has settled, look for a newer version (it never throws and never interrupts work).
        await Task.Delay(TimeSpan.FromSeconds(2));
        await _viewModel.CheckForUpdateAsync();
    }

    /// <summary>Also looks for an update when the user comes back to the app (the check itself is limited to once every few hours).</summary>
    private void HookResume()
    {
        if (_resumeHooked || Window is not { } window)
        {
            return;
        }

        _resumeHooked = true;
        window.Resumed += async (_, _) => await _viewModel.CheckForUpdateAsync();
    }

    /// <summary>Back closes the viewer first, then goes up a folder, and only then leaves the app.</summary>
    protected override bool OnBackButtonPressed() => _viewModel.HandleBackPressed() || base.OnBackButtonPressed();
}
