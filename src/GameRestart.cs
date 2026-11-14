using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace AstralPartyChatPlugin;

/// <summary>Restarts the game after an update. Same helper as astral-party-korean-plugin.</summary>
internal static class GameRestart
{
    public static ProcessStartInfo CreateStartInfo(string executable, int processId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Game restart requires Windows");
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable) || processId <= 0) throw new IOException("Could not resolve the running game executable");
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var pid = processId.ToString(CultureInfo.InvariantCulture);
        // No executable payload and no PowerShell. The hidden Windows shell waits for this PID,
        // then directly starts the same game executable (Steam may relaunch it through Steam).
        // Paths are passed through environment variables, not interpolated into shell source.
        var command = "for /l %i in (1,0,2) do @(\"" + Path.Combine(system, "tasklist.exe") +
            "\" /fi \"PID eq " + pid + "\" /nh | \"" + Path.Combine(system, "findstr.exe") +
            "\" /r /c:\"[ ]" + pid + "[ ]\" >nul & if errorlevel 1 (start \"\" /d \"%ASTRAL_RESTART_DIRECTORY%\" " +
            "\"%ASTRAL_RESTART_EXECUTABLE%\" & exit) else (\"" + Path.Combine(system, "ping.exe") + "\" -n 2 127.0.0.1 >nul))";
        var info = new ProcessStartInfo(Path.Combine(system, "cmd.exe"), "/d /v:off /c \"" + command + "\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = system
        };
        // Doorstop marks its host process with DOORSTOP_DISABLE/DOORSTOP_* after injecting; an inherited copy
        // makes the relaunched game skip BepInEx entirely, so drop every Doorstop variable.
        var inherited = new string[info.Environment.Count];
        info.Environment.Keys.CopyTo(inherited, 0);
        foreach (var name in inherited)
            if (name.StartsWith("DOORSTOP_", StringComparison.OrdinalIgnoreCase)) info.Environment.Remove(name);
        info.Environment["ASTRAL_RESTART_DIRECTORY"] = Path.GetDirectoryName(executable)!;
        info.Environment["ASTRAL_RESTART_EXECUTABLE"] = executable;
        return info;
    }

    public static void Start(string executable, int processId)
    {
        using var helper = Process.Start(CreateStartInfo(executable, processId));
        if (helper == null) throw new IOException("Could not start the game restart helper");
    }
}
