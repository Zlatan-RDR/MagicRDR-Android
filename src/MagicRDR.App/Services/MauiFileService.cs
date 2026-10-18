using CommunityToolkit.Maui.Storage;

namespace MagicRDR.App.Services;

public sealed class MauiFileService : IFileService
{
    private const int CopyBufferSize = 1 << 20;

    public MauiFileService()
    {
        WorkDirectory = Path.Combine(FileSystem.AppDataDirectory, "work");
        SessionDirectory = Path.Combine(FileSystem.AppDataDirectory, "session");
        Directory.CreateDirectory(WorkDirectory);
        Directory.CreateDirectory(SessionDirectory);
    }

    public string WorkDirectory { get; }

    public string DataDirectory => FileSystem.AppDataDirectory;

    public string SessionDirectory { get; }

    public void CleanWorkDirectory()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(WorkDirectory))
            {
                TryDelete(file);
            }

            foreach (string dir in Directory.EnumerateDirectories(WorkDirectory))
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
    }

    public void CleanCache()
    {
        try
        {
            string cache = FileSystem.CacheDirectory;
            if (!Directory.Exists(cache))
            {
                return;
            }

            foreach (string file in Directory.EnumerateFiles(cache))
            {
                TryDelete(file);
            }

            foreach (string dir in Directory.EnumerateDirectories(cache))
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The path of the picker's own copy, if it is one (a file inside the app's cache folder).</summary>
    private static string? CachePathOf(FileResult result)
    {
        try
        {
            string path = result.FullPath;
            if (!string.IsNullOrEmpty(path) && path.StartsWith(FileSystem.CacheDirectory, StringComparison.Ordinal))
            {
                return path;
            }
        }
        catch (Exception)
        {
            // no physical path: nothing to clean
        }

        return null;
    }

    public async Task<PickedFile?> PickAsync(string title)
    {
        FileResult? result = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = title });
        return result == null ? null : new PickedFile(result.FileName, result.OpenReadAsync, CachePathOf(result));
    }

    public async Task<IReadOnlyList<PickedFile>> PickManyAsync(string title)
    {
        IEnumerable<FileResult>? results = await FilePicker.Default.PickMultipleAsync(new PickOptions { PickerTitle = title });
        if (results == null)
        {
            return Array.Empty<PickedFile>();
        }

        var files = new List<PickedFile>();
        foreach (FileResult result in results)
        {
            files.Add(new PickedFile(result.FileName, result.OpenReadAsync, CachePathOf(result)));
        }

        return files;
    }

    public async Task CopyToAsync(
        PickedFile file,
        string targetPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        try
        {
            await using Stream input = await file.OpenReadAsync();
            long length = 0;
            try
            {
                length = input.CanSeek ? input.Length : 0;
            }
            catch (NotSupportedException)
            {
            }

            await using var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.Asynchronous);
            byte[] buffer = new byte[CopyBufferSize];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                total += read;
                if (length > 0)
                {
                    progress?.Report((double)total / length);
                }
            }
        }
        catch
        {
            TryDelete(targetPath);
            throw;
        }
        finally
        {
            // The picker's cached copy has been read (or the copy failed): it is not needed any more.
            if (file.TemporaryPath != null)
            {
                TryDelete(file.TemporaryPath);
            }
        }
    }

    public async Task<ExportResult> SaveAsync(string suggestedName, Stream content, CancellationToken cancellationToken)
    {
        FileSaverResult result = await FileSaver.Default.SaveAsync(suggestedName, content, cancellationToken);
        return result.IsSuccessful
            ? new ExportResult(true)
            : new ExportResult(false, result.Exception is OperationCanceledException ? "save cancelled" : result.Exception?.Message);
    }

    public async Task<Stream?> OpenPackageFileAsync(string name)
    {
        try
        {
            return await FileSystem.OpenAppPackageFileAsync(name);
        }
        catch (Exception)
        {
            return null;
        }
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
    }
}
