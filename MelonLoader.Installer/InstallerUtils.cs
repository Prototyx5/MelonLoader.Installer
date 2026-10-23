using Avalonia.Input;
using Avalonia.Platform.Storage;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Semver;

#if LINUX || OSX
using System.Runtime.InteropServices;
#endif

namespace MelonLoader.Installer;

public static partial class InstallerUtils
{
    public static readonly HashSet<string> _validExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe",
        ".app",
        ".x86_64"
    };

    public static HttpClient Http { get; internal set; }

#if LINUX
    private static string[] allOpenCmds = new string[]
        {
            "xdg-open",
            "gnome-open",
            "gvfs-open",
            "kde-open",
            "gio",
        };
#endif

    static InstallerUtils()
    {
        Http = new();
        Http.DefaultRequestHeaders.Add("User-Agent", $"MelonLoader Installer v{Program.VersionName}");
    }

    public static async Task<string?> DownloadFileAsync(string url, Stream destination, InstallProgressEventHandler? onProgress)
    {
        HttpResponseMessage response;
        try
        {
            response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (HttpRequestException)
        {
            return "Could not establish a connection.";
        }
        catch (Exception ex)
        {
            return "Something went wrong while requesting the download files: " + ex.Message;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return response.ReasonPhrase;
            }

            var length = response.Content.Headers.ContentLength ?? 0;

            try
            {
                using var content = await response.Content.ReadAsStreamAsync();

                long position = 0;
                var buffer = new byte[1024 * 16];
                int read;
                while ((read = await content.ReadAsync(buffer)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read));

                    position += read;

                    if (length > 0)
                        onProgress?.Invoke(position / (double)length, null);
                }

                if (length > 0 && position != length)
                    return "The download was interrupted before it completed.";
            }
            catch (Exception ex)
            {
                return "The connection was lost while downloading: " + ex.Message;
            }
        }

        return null;
    }

    /// <summary>
    /// Checks a stream against a checksum published by GitHub ("sha256:&lt;hex&gt;").
    /// </summary>
    /// <param name="stream">The data to check. It is read from the start and left at the start.</param>
    /// <param name="expectedDigest">The expected digest. Nothing is verified if it's missing or uses an unknown algorithm.</param>
    /// <returns>An error message if the data doesn't match, otherwise null.</returns>
    public static string? VerifyDigest(Stream stream, string? expectedDigest)
    {
        const string prefix = "sha256:";

        if (string.IsNullOrEmpty(expectedDigest)
            || !expectedDigest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        string actual;
        try
        {
            stream.Seek(0, SeekOrigin.Begin);
            actual = Convert.ToHexString(SHA256.HashData(stream));
            stream.Seek(0, SeekOrigin.Begin);
        }
        catch
        {
            return "Failed to verify the integrity of the downloaded file.";
        }

        if (!actual.Equals(expectedDigest[prefix.Length..].Trim(), StringComparison.OrdinalIgnoreCase))
            return "The downloaded file does not match the checksum published by GitHub, so it was discarded. Please try again.";

        return null;
    }

    public static string? Extract(Stream archiveStream, string destination, InstallProgressEventHandler? onProgress)
    {
        Directory.CreateDirectory(destination);

        try
        {
            archiveStream.Seek(0, SeekOrigin.Begin);
            using var zip = new ZipArchive(archiveStream, ZipArchiveMode.Read);

            var zipLength = zip.Entries.Count;
            for (var i = 0; i < zipLength; i++)
            {
                var entry = zip.Entries[i];
                if (entry.FullName.EndsWith('/'))
                    continue;

                var dest = Path.Combine(destination, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, true);

                onProgress?.Invoke(i / (double)(zipLength - 1), null);
            }
        }
        catch (InvalidDataException)
        {
            return "Failed to extract MelonLoader: The downloaded data seems to be corrupt.";
        }
        catch
        {
            return "Failed to extract MelonLoader: Failed to extract all files.";
        }

        return null;
    }

    public static bool CheckDragEventForGameExecutable(DragEventArgs evt)
    {
        var data = evt.DataTransfer;
        if (!data.Contains(DataFormat.File))
            return false;

        var files = data.TryGetFiles();
        if (files == null)
            return false;

        return files.Any(IsFileGameExecutable);
    }

    public static bool IsFileGameExecutable(IStorageItem item)
        => IsFileGameExecutable(item.Path.LocalPath);
    public static bool IsFileGameExecutable(string path)
    {
        var extension = Path.GetExtension(path);
        var fileName = Path.GetFileName(path);
        return _validExtensions.Contains(extension);
    }

    public static void OpenFolderInExplorer(string path)
    {
#if LINUX
        string openCmd = string.Empty;
        foreach (var cmd in allOpenCmds)
            if (IsCommandAvailable(cmd))
            {
                openCmd = cmd;
                break;
            }
        if (string.IsNullOrEmpty(openCmd))
            return;
#endif
        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                FileName =
#if OSX
                    "open",
#elif LINUX
                    openCmd,
#else
                    "explorer",
#endif
            };
            
#if LINUX
            if (openCmd == "gio")
                startInfo.ArgumentList.Add("open");
#endif
            
            startInfo.ArgumentList.Add(path);
            Process.Start(startInfo);
        }
        catch { }
    }

#if LINUX
    private static bool IsCommandAvailable(string commandName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return false;

        var paths = pathEnv.Split(':');
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path)
				|| string.IsNullOrWhiteSpace(path))
                continue;

            var fullPath = Path.Combine(path, commandName);
            if (File.Exists(fullPath))
                return true;
        }

        return false;
    }
#endif

#if LINUX || OSX
    // user permissions
    public const int S_IRUSR = 0x100;
    public const int S_IWUSR = 0x80;
    public const int S_IXUSR = 0x40;

    // group permission
    public const int S_IRGRP = 0x20;
    public const int S_IXGRP = 0x8;

    // other permissions
    public const int S_IROTH = 0x4;
    public const int S_IXOTH = 0x1;
        
    [LibraryImport("libc", EntryPoint = "chmod", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Chmod(string pathname, int mode);
#endif
    }

public delegate void InstallProgressEventHandler(double progress, string? newStatus);

public delegate void InstallFinishedEventHandler(string? errorMessage);