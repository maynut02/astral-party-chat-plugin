using System;
using System.IO;

namespace AstralPartyChatPlugin;

/// <summary>
/// Replaces the running plugin DLL with a verified release. The target is the
/// file BepInEx actually loaded, so a manual install in another plugins folder
/// (or under another file name) is updated in place instead of duplicated.
/// </summary>
internal sealed class UpdateInstaller
{
    private readonly string _path;
    private readonly Action<string> _log;

    public UpdateInstaller(string pluginPath, Action<string> log)
    {
        _path = Path.GetFullPath(pluginPath);
        _log = log;
    }

    private string Staged => _path + ".new";
    private string Backup => _path + ".old";

    /// <summary>
    /// Removes what an earlier session left behind. A staged file is never
    /// applied here: only bytes verified in this session may replace the DLL.
    /// </summary>
    public void PrepareStartup()
    {
        foreach (var path in new[] { Staged, Staged + ".tmp", Backup })
        {
            try
            {
                if (!File.Exists(path)) continue;
                File.Delete(path);
                _log("Removed previous plugin update file: " + Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                // Neither file is loaded by BepInEx (*.dll only), so a failed cleanup is harmless.
                _log("Plugin update cleanup failed: " + Path.GetFileName(path) + ": " + ex.Message);
            }
        }
    }

    /// <summary>Swaps in the verified DLL. On failure the installed DLL is unchanged.</summary>
    public void Install(byte[] dll)
    {
        try
        {
            // A partial write never carries the staged name.
            WriteComplete(Staged, dll);
            // ReplaceFileW renames the image-mapped DLL to the backup instead of
            // deleting it, so the running plugin keeps working until restart.
            File.Replace(Staged, _path, Backup);
        }
        catch
        {
            try
            {
                if (!File.Exists(_path) && File.Exists(Backup))
                {
                    File.Move(Backup, _path);
                    _log("Restored installed plugin after failed replacement");
                }
            }
            finally
            {
                TryDelete(Staged + ".tmp");
                TryDelete(Staged);
            }
            throw;
        }
        _log("Installed plugin update: " + Path.GetFileName(_path) + " (previous file retained as .old)");
    }

    private static void WriteComplete(string path, byte[] bytes)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { _log("Could not remove staged update file " + Path.GetFileName(path) + ": " + ex.Message); }
    }
}
