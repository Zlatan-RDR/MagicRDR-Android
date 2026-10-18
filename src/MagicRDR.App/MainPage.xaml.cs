using MagicRDR.App.ViewModels;

namespace MagicRDR.App;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel _viewModel;

    public MainPage(MainViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;

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
        await _viewModel.InitializeAsync();
    }

    /// <summary>Back closes the viewer first, then goes up a folder, and only then leaves the app.</summary>
    protected override bool OnBackButtonPressed() => _viewModel.HandleBackPressed() || base.OnBackButtonPressed();
}
