using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Widget;

namespace MagicRDR.App.Services;

/// <summary>
/// Installs a downloaded APK through Android's own PackageInstaller (the same mechanism app stores use).
/// The APK is streamed into an install session, so no file sharing set-up is needed. Android shows its normal
/// "Do you want to update this app?" screen, and it only accepts the file if it is signed with the same key as the
/// installed app, so a tampered file can never replace the app.
/// </summary>
internal static class ApkInstaller
{
    public const string ResultAction = "com.magicrdr.mobile.UPDATE_INSTALL_RESULT";

    private static Context AppCtx => Android.App.Application.Context;

    public static bool CanInstall => AppCtx.PackageManager?.CanRequestPackageInstalls() ?? false;

    public static void OpenInstallSettings()
    {
        Context context = AppCtx;
        var intent = new Intent(
            "android.settings.MANAGE_UNKNOWN_APP_SOURCES",
            Android.Net.Uri.Parse("package:" + context.PackageName));
        intent.AddFlags(ActivityFlags.NewTask);
        context.StartActivity(intent);
    }

    public static async Task InstallAsync(string apkPath, CancellationToken cancellationToken)
    {
        await Task.Run(() => Install(apkPath, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static void Install(string apkPath, CancellationToken cancellationToken)
    {
        Context context = AppCtx;
        PackageInstaller installer = context.PackageManager?.PackageInstaller
            ?? throw new InvalidOperationException("The Android package installer is not available on this device.");

        long length = new FileInfo(apkPath).Length;
        var sessionParams = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
        sessionParams.SetAppPackageName(context.PackageName);
        sessionParams.SetSize(length);

        int sessionId = installer.CreateSession(sessionParams);
        PackageInstaller.Session session = installer.OpenSession(sessionId)
            ?? throw new InvalidOperationException("Could not open an install session.");
        try
        {
            using (var input = new FileStream(apkPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            using (Stream output = session.OpenWrite("MagicRDR.apk", 0, length)
                ?? throw new InvalidOperationException("Could not write to the install session."))
            {
                byte[] buffer = new byte[1 << 16];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                }

                output.Flush();
                session.Fsync(output);
            }

            var resultIntent = new Intent(context, typeof(UpdateInstallReceiver));
            resultIntent.SetPackage(context.PackageName);
            resultIntent.SetAction(ResultAction);

            // The installer adds its own extras to this intent, so it has to be mutable (required from Android 12).
            PendingIntentFlags flags = PendingIntentFlags.UpdateCurrent;
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                flags |= PendingIntentFlags.Mutable;
            }

            PendingIntent pending = PendingIntent.GetBroadcast(context, sessionId, resultIntent, flags)
                ?? throw new InvalidOperationException("Could not prepare the installer callback.");
            session.Commit(pending.IntentSender ?? throw new InvalidOperationException("Could not prepare the installer callback."));
        }
        catch
        {
            session.Abandon();
            throw;
        }
        finally
        {
            session.Dispose();
        }
    }
}

/// <summary>
/// Receives the installer's progress. When Android needs the user's confirmation it hands over a screen to show;
/// failures are reported with a short message. A successful install replaces the app, so there is nothing to do then.
/// </summary>
[BroadcastReceiver(Enabled = true, Exported = false)]
public class UpdateInstallReceiver : BroadcastReceiver
{
    private const string ExtraStatus = "android.content.pm.extra.STATUS";
    private const string ExtraStatusMessage = "android.content.pm.extra.STATUS_MESSAGE";
    private const string ExtraIntent = "android.intent.extra.INTENT";
    private const int StatusPendingUserAction = -1;
    private const int StatusSuccess = 0;
    private const int StatusFailureAborted = 3;

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context == null || intent == null)
        {
            return;
        }

        int status = intent.GetIntExtra(ExtraStatus, 1);
        if (status == StatusPendingUserAction)
        {
#pragma warning disable CA1422, CS0618 // the typed overload only exists from Android 13; this one works on every version
            Intent? confirm = intent.GetParcelableExtra(ExtraIntent) as Intent;
#pragma warning restore CA1422, CS0618
            if (confirm != null)
            {
                confirm.AddFlags(ActivityFlags.NewTask);
                context.StartActivity(confirm);
            }

            return;
        }

        if (status == StatusSuccess || status == StatusFailureAborted)
        {
            return; // installed, or the user pressed Cancel in Android's own screen
        }

        string reason = intent.GetStringExtra(ExtraStatusMessage) ?? "Android could not install the update.";
        Toast.MakeText(context, "Update not installed: " + reason, ToastLength.Long)?.Show();
    }
}
