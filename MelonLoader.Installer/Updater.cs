using System.Diagnostics;
using System.Text.Json.Nodes;

#if LINUX
using System.Runtime.InteropServices;
#endif

namespace MelonLoader.Installer;

public static partial class Updater
{
    public static volatile UpdateState State;
    public static event InstallProgressEventHandler? Progress;

    public static async Task<Task?> UpdateIfPossible()
    {
        if (State != UpdateState.None)
            return null;

        // Don't auto-update on CI builds
        if (Program.Version.Revision > 0)
        {
            State = UpdateState.AlreadyChecked;
            return null;
        }

        var update = await CheckForUpdateAsync();
        if (update == null)
        {
            State = UpdateState.AlreadyChecked;
            return null;
        }

        State = UpdateState.Updating;

        var (downloadUrl, digest) = update.Value;
        return Task.Run(() => UpdateAsync(downloadUrl, digest));
    }

    public static bool WaitAndRemoveApp(string originalPath, int prevPID)
    {
        if (!File.Exists(originalPath))
            return true;

        // Make sure we're not waiting for ourselves.
        if (prevPID == Environment.ProcessId || Path.GetFullPath(originalPath).Equals(Path.GetFullPath(PathManager.ProcessPath!), StringComparison.OrdinalIgnoreCase))
            return false;

        if (prevPID != 0)
        {
            try
            {
                var prevProc = Process.GetProcessById(prevPID);

                if (!prevProc.HasExited)
                    prevProc.WaitForExit();
            }
            catch { }
        }

#if OSX
        
        try
        {
            Directory.Delete(originalPath, true);

            string parentPath = Path.GetDirectoryName(originalPath);
            Directory.Delete(parentPath, true);
        }
        catch
        {
            return false;
        }

#else

        try
        {
            File.Delete(originalPath);
        }
        catch
        {
            return false;
        }

#endif

        return true;
    }

    public static void HandleUpdate(string originalPath, int prevPID)
    {
        if (!WaitAndRemoveApp(originalPath, prevPID))
            return;

#if OSX
        if (!Directory.Exists(originalPath))
            Directory.CreateDirectory(originalPath);
        foreach (string filePath in Directory.GetFiles(PathManager.ProcessPath, "*.*",SearchOption.AllDirectories))
            File.Copy(filePath, filePath.Replace(PathManager.ProcessPath, originalPath), true);

        Process.Start("open", [originalPath, "--args", "-cleanup", PathManager.ProcessPath!, Environment.ProcessId.ToString()]);
#else

        File.Copy(PathManager.ProcessPath!, originalPath, true);
        Process.Start(originalPath, ["-cleanup", PathManager.ProcessPath!, Environment.ProcessId.ToString()]);
#endif
    }

    private static async Task UpdateAsync(string downloadUrl, string? digest)
    {
        var newPath = Path.GetTempFileName()
#if WINDOWS
                      + ".exe"
#endif
                      ;

        await using (var newStr = File.Create(newPath))
        {
            var result = await InstallerUtils.DownloadFileAsync(downloadUrl, newStr, (progress, newStatus) => Progress?.Invoke(progress, newStatus));
            if (result != null)
            {
                newStr.Close();
                TryDelete(newPath);
                throw new Exception("Failed to download the latest installer version: " + result);
            }
        }

        // Never run an executable that doesn't match the checksum GitHub published for it
        using (var verifyStr = File.OpenRead(newPath))
        {
            var digestErr = InstallerUtils.VerifyDigest(verifyStr, digest);
            if (digestErr != null)
            {
                verifyStr.Close();
                TryDelete(newPath);
                throw new Exception("Failed to update the installer: " + digestErr);
            }
        }

#if LINUX

        // Make the file executable
        InstallerUtils.Chmod(newPath, InstallerUtils.S_IRUSR 
            | InstallerUtils.S_IXUSR
            | InstallerUtils.S_IWUSR 
            | InstallerUtils.S_IRGRP 
            | InstallerUtils.S_IXGRP 
            | InstallerUtils.S_IROTH
            | InstallerUtils.S_IXOTH);

#elif OSX

        // Extract Zip
        var archivePath = newPath;
        
        string tempFolderPath = Path.Combine(PathManager.CacheDir, "tmp");
        if (Directory.Exists(tempFolderPath))
            Directory.Delete(tempFolderPath, true);
        Directory.CreateDirectory(tempFolderPath);

        using var zipStr = File.OpenRead(archivePath);
        if (InstallerUtils.Extract(zipStr, tempFolderPath, null) == null)
        {
            if (File.Exists(archivePath))
                File.Delete(archivePath);
            if (Directory.Exists(tempFolderPath))
                Directory.Delete(tempFolderPath, true);
            throw new Exception("Failed to extract the latest installer version");
        }
        if (File.Exists(archivePath))
            File.Delete(archivePath);

        // Find New File
        newPath = Path.Combine(tempFolderPath, "MelonLoader Installer.app");
        if (!File.Exists(newPath))
        {
            if (Directory.Exists(tempFolderPath))
                Directory.Delete(tempFolderPath, true);
            throw new Exception("Failed to extract the latest installer version");
        }

        // Make the file executable
        InstallerUtils.Chmod(newPath, InstallerUtils.S_IRUSR 
            | InstallerUtils.S_IXUSR
            | InstallerUtils.S_IWUSR 
            | InstallerUtils.S_IRGRP 
            | InstallerUtils.S_IXGRP 
            | InstallerUtils.S_IROTH
            | InstallerUtils.S_IXOTH);
#endif

#if OSX
        Process.Start("open", [newPath, "--args", "-handleupdate", PathManager.ProcessPath!, Environment.ProcessId.ToString()]);
#else
        Process.Start(newPath, ["-handleupdate", PathManager.ProcessPath!, Environment.ProcessId.ToString()]);
#endif

        State = UpdateState.Done;
    }

#if WINDOWS
    public static bool CheckLegacyUpdate()
    {
        if (!PathManager.ProcessPath!.EndsWith(".tmp.exe", StringComparison.OrdinalIgnoreCase))
            return false;

        var dir = Path.GetDirectoryName(PathManager.ProcessPath)!;
        var name = Path.GetFileNameWithoutExtension(PathManager.ProcessPath);
        name = name[..^4] + ".exe";

        var final = Path.Combine(dir, name);

        var prevProc = Process.GetProcessesByName(name);

        HandleUpdate(final, prevProc.FirstOrDefault()?.Id ?? 0);
        return true;
    }
#endif

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch { }
    }

    private static async Task<(string Url, string? Digest)?> CheckForUpdateAsync()
    {
        var response = await GitHubApi.GetLatestInstallerRelease();

        // A cached answer only means that GitHub couldn't be reached, so there's nothing to update from
        if (response.Node == null || response.Stale)
            return null;

        var json = response.Node;

        if (json["tag_name"] == null)
            return null;

        var versionStr = json["tag_name"]!.ToString();
        if (versionStr.StartsWith('v'))
            versionStr = versionStr[1..];

        if (!Version.TryParse(versionStr, out var latestVer))
            return null;

        var currentVer = typeof(Updater).Assembly.GetName().Version;
        if (currentVer == null || currentVer >= latestVer)
            return null;

        var asset = json["assets"]?.AsArray().FirstOrDefault((x) =>
        {
            string fileName = x!["name"]!.ToString();
            return fileName.EndsWith(
#if WINDOWS

#if ARM64
                ".arm64" +
#endif
                ".exe"
                
#elif LINUX
                
                ".Linux"
#if ARM64
                + ".arm64"
#endif
                
#elif OSX
                ".MacOS"
#endif
            );
        });

        var assetUrl = asset?["browser_download_url"]?.ToString();
        if (assetUrl == null)
            return null;

        return (assetUrl, asset?["digest"]?.ToString());
    }

    public enum UpdateState
    {
        None,
        Updating,
        Done,
        AlreadyChecked
    }
}
