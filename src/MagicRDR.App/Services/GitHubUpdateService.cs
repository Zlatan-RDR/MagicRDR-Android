using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using MagicRDR.Core.Updates;

namespace MagicRDR.App.Services;

/// <summary>
/// Update source: the "latest release" of the project's GitHub repository (public repositories need no sign-in).
/// Only the release information and the APK itself are downloaded; nothing about the user or the device is sent.
/// </summary>
public sealed class GitHubUpdateService : IUpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/Zlatan-RDR/MagicRDR-Android/releases/latest";
    private const int BufferSize = 1 << 17;
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

    // One client for the whole app. No overall timeout: a big download on a slow line must not be cut off.
    // The release lookup has its own 15 second limit, and every call can be cancelled.
    private static readonly HttpClient Http = CreateClient();

    public bool CanInstall => ApkInstaller.CanInstall;

    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken)
    {
        if (!UpdateParser.TryParseVersion(AppInfo.Current.VersionString, out Version current))
        {
            return null;
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CheckTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null; // no release published yet (or the repository is not public)
        }

        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

        // A 64-bit process takes the normal APK; a 32-bit one (an armeabi-v7a install) takes the armeabi-v7a APK.
        return UpdateParser.Parse(json, current, Environment.Is64BitProcess);
    }

    public async Task<string> DownloadAsync(UpdateInfo update, string directory, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        foreach (string old in Directory.EnumerateFiles(directory))
        {
            TryDelete(old); // leftovers of an earlier download
        }

        string finalPath = Path.Combine(directory, "MagicRDR-" + update.VersionText + ".apk");
        string partPath = finalPath + ".part";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, update.DownloadUrl);
            using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long total = response.Content.Headers.ContentLength ?? update.Size;
            long received = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    int read;
                    while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        hash.AppendData(buffer, 0, read);
                        received += read;
                        if (total > 0)
                        {
                            progress?.Report(Math.Min(1.0, (double)received / total));
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (update.Size > 0 && received != update.Size)
            {
                throw new InvalidDataException($"The download is incomplete ({received:N0} of {update.Size:N0} bytes). Please try again.");
            }

            string actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.IsNullOrEmpty(update.Sha256) && !string.Equals(actual, update.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The downloaded file failed its integrity check and was discarded. Please try again.");
            }

            File.Move(partPath, finalPath, overwrite: true);
            return finalPath;
        }
        catch
        {
            TryDelete(partPath);
            throw;
        }
    }

    public async Task InstallAsync(string apkPath, CancellationToken cancellationToken)
    {
        await ApkInstaller.InstallAsync(apkPath, cancellationToken).ConfigureAwait(false);

        // Android keeps its own copy once the session is committed, so the download is no longer needed.
        TryDelete(apkPath);
    }

    public void OpenInstallSettings() => ApkInstaller.OpenInstallSettings();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.TryParseAdd("MagicRDR-Android/" + AppInfo.Current.VersionString);
        return client;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
