using CommunityToolkit.Maui;
using MagicRDR.App.Services;
using MagicRDR.App.ViewModels;

namespace MagicRDR.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        MauiAppBuilder builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit(); // FileSaver (Android "create document" dialog)

        builder.Services.AddSingleton<IFileService, MauiFileService>();
        builder.Services.AddSingleton<IDialogService, MauiDialogService>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddTransient<MainPage>();

        return builder.Build();
    }
}
