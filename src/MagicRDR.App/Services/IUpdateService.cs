using MagicRDR.Core.Updates;

namespace MagicRDR.App.Services;

/// <summary>Finds, downloads and installs a newer version of the app published as a GitHub Release.</summary>
public interface IUpdateService
{
    /// <summary>True when Android lets this app start installations (the "install unknown apps" switch is on).</summary>
    bool CanInstall { get; }

    /// <summary>Asks GitHub for the latest release. Returns <c>null</c> when this app is already up to date.</summary>
    Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken);

    /// <summary>Downloads the APK into <paramref name="directory"/> and verifies its size and SHA-256. Returns the file path.</summary>
    Task<string> DownloadAsync(UpdateInfo update, string directory, IProgress<double>? progress, CancellationToken cancellationToken);

    /// <summary>Hands the downloaded APK to Android's installer. Android then asks the user to confirm.</summary>
    Task InstallAsync(string apkPath, CancellationToken cancellationToken);

    /// <summary>Opens the Android screen where the user allows this app to install updates.</summary>
    void OpenInstallSettings();
}
