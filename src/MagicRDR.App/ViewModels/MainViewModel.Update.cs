using CommunityToolkit.Mvvm.Input;
using MagicRDR.App.Models;
using MagicRDR.App.Services;
using MagicRDR.Core.Updates;

namespace MagicRDR.App.ViewModels;

/// <summary>
/// In-app updates: now and then the app looks at the project's latest GitHub Release. When it is newer, the user is told
/// and can download it and install it from inside the app. Nothing here can stop the app from working: every failure
/// (no internet, GitHub down, a cancelled download) is swallowed or shown as a short message.
/// </summary>
public partial class MainViewModel
{
    private const string PrefLastCheckUtc = "update.last_check_utc";
    private const string PrefSnoozeVersion = "update.snooze_version";
    private const string PrefSnoozeUntilUtc = "update.snooze_until_utc";

    // How often the app asks GitHub on its own, and how long "Later" keeps one version quiet.
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan SnoozeDuration = TimeSpan.FromHours(24);

    private IUpdateService? _updates;
    private int _updateCheckRunning;

    public void AttachUpdateService(IUpdateService updates) => _updates = updates;

    /// <summary>Checks for a newer version and, if there is one, offers to install it. Never throws.</summary>
    /// <param name="userInitiated">True for a manual check: it ignores the schedule and always says what it found.</param>
    public async Task CheckForUpdateAsync(bool userInitiated = false)
    {
        IUpdateService? updates = _updates;
        if (updates == null || Interlocked.Exchange(ref _updateCheckRunning, 1) == 1)
        {
            return;
        }

        try
        {
            if (!userInitiated && !IsUpdateCheckDue())
            {
                return;
            }

            // Never interrupt an operation or an open file: wait a little, then try again at the next opportunity.
            for (int i = 0; i < 40 && IsLoading; i++)
            {
                await Task.Delay(500);
            }

            if (IsLoading || IsViewerOpen)
            {
                if (userInitiated)
                {
                    StatusMessage = "Finish what you are doing first, then check for updates again.";
                }

                return;
            }

            if (userInitiated)
            {
                StatusMessage = "Checking for updates…";
            }

            UpdateInfo? update;
            try
            {
                update = await updates.CheckAsync(CancellationToken.None);
            }
            catch (Exception)
            {
                if (userInitiated)
                {
                    StatusMessage = "Could not check for updates. Please check your internet connection.";
                }

                return;
            }

            Preferences.Default.Set(PrefLastCheckUtc, DateTime.UtcNow.Ticks);
            if (update == null)
            {
                if (userInitiated)
                {
                    StatusMessage = "You have the latest version.";
                }

                return;
            }

            if (!userInitiated && IsSnoozed(update))
            {
                return;
            }

            await OfferUpdateAsync(updates, update);
        }
        catch (Exception)
        {
            // An update check must never disturb the app.
        }
        finally
        {
            Interlocked.Exchange(ref _updateCheckRunning, 0);
        }
    }

    /// <summary>The "Check for updates" action for a menu or button.</summary>
    [RelayCommand]
    private Task CheckForUpdateNowAsync() => CheckForUpdateAsync(userInitiated: true);

    private async Task OfferUpdateAsync(IUpdateService updates, UpdateInfo update)
    {
        string size = update.Size > 0 ? $"Download size: {SizeFormatter.Format(update.Size)}.\n\n" : string.Empty;
        string notes = string.IsNullOrEmpty(update.WhatsNew) ? string.Empty : $"What's new:\n{update.WhatsNew}\n\n";
        string message = $"Magic RDR {update.VersionText} is available. You have {AppInfo.Current.VersionString}.\n\n"
            + size
            + notes
            + "The update installs over your current version and keeps your data.";

        if (!await _dialogs.ConfirmAsync("Update available", message, "Update now", "Later"))
        {
            Preferences.Default.Set(PrefSnoozeVersion, update.VersionText);
            Preferences.Default.Set(PrefSnoozeUntilUtc, (DateTime.UtcNow + SnoozeDuration).Ticks);
            return;
        }

        if (!updates.CanInstall)
        {
            bool open = await _dialogs.ConfirmAsync(
                "Allow installing updates",
                "Android needs your permission before Magic RDR can install an update.\n\nOn the next screen turn on \"Allow from this source\", then come back to the app. The update will be offered again.",
                "Open settings",
                "Cancel");
            if (open)
            {
                // Forget the schedule so the offer returns as soon as the user is back in the app.
                Preferences.Default.Set(PrefLastCheckUtc, 0L);
                updates.OpenInstallSettings();
            }

            return;
        }

        await RunAsync($"Downloading Magic RDR {update.VersionText}…", async ct =>
        {
            string apkPath = await updates.DownloadAsync(update, Path.Combine(_files.DataDirectory, "updates"), CopyProgress("Downloading update"), ct);

            StatusMessage = "Starting the installer…";
            await updates.InstallAsync(apkPath, ct);

            StatusMessage = "Confirm the installation in the Android screen. Magic RDR closes while it updates; open it again afterwards.";
        });
    }

    private static bool IsUpdateCheckDue()
    {
        long lastTicks = Preferences.Default.Get(PrefLastCheckUtc, 0L);
        if (lastTicks <= 0)
        {
            return true;
        }

        var last = new DateTime(lastTicks, DateTimeKind.Utc);
        DateTime now = DateTime.UtcNow;
        return last > now || now - last >= CheckInterval; // a clock set back also counts as due
    }

    private static bool IsSnoozed(UpdateInfo update)
    {
        string snoozed = Preferences.Default.Get(PrefSnoozeVersion, string.Empty);
        long untilTicks = Preferences.Default.Get(PrefSnoozeUntilUtc, 0L);
        return untilTicks > 0
            && string.Equals(snoozed, update.VersionText, StringComparison.Ordinal)
            && DateTime.UtcNow < new DateTime(untilTicks, DateTimeKind.Utc);
    }
}
