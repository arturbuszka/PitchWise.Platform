using System.Diagnostics;
using System.Text;
using PitchWise.Data.Outputs;

namespace PitchWise.Broadcast;

/// <summary>
/// ffmpeg outlives a service that is killed, and would go on sending next to the copy that a restarted service
/// starts. In a container this finds nothing, as the kernel ends everything with the container's main process.
/// It matters when the service runs on a developer's machine.
/// </summary>
public static class OrphanSweep
{
    public static int Run()
    {
        var killed = 0;
        if (!Directory.Exists("/proc")) return killed;

        var marker = $"rtsp://{OutputReader.User}:";
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var pid) || pid == Environment.ProcessId) continue;
            try
            {
                var arguments = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "cmdline"))).Split('\0');
                if (Path.GetFileName(arguments[0]) != "ffmpeg" || !arguments.Any(a => a.StartsWith(marker))) continue;
                using var process = Process.GetProcessById(pid);
                process.Kill();
                killed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
                or System.ComponentModel.Win32Exception)
            {
                // It has gone by itself, or isn't ours to end.
            }
        }
        return killed;
    }
}
