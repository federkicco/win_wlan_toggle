using System.Globalization;
using System.Text;
using WlanToggle.Core;

namespace WlanToggle.Windows;

/// <summary>
/// Independent backstop: a one-shot Task Scheduler task running as SYSTEM that executes
/// "<c>WlanToggle.exe restore</c>" shortly after the re-enable deadline. It survives the tray process dying,
/// the RDP session being logged off, and a crash mid-outage. Disarmed once the link is verified up.
/// </summary>
public sealed class ScheduledTaskGuard(string exePath, TimeSpan? margin = null) : IOutageGuard
{
    private const string TaskName = "WlanToggleRestore";
    private static readonly TimeSpan PowerShellTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _margin = margin ?? TimeSpan.FromSeconds(30);

    public Task ArmAsync(DateTimeOffset latestReenableUtc, string adapterName)
    {
        var fireAt = (latestReenableUtc.ToLocalTime().DateTime + _margin)
            .ToString("yyyy-MM-dd'T'HH':'mm':'ss", CultureInfo.InvariantCulture);
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $at = [DateTime]::ParseExact('{{fireAt}}', 'yyyy-MM-ddTHH:mm:ss', [Globalization.CultureInfo]::InvariantCulture)
            $action    = New-ScheduledTaskAction -Execute '{{Quote(exePath)}}' -Argument 'restore'
            $trigger   = New-ScheduledTaskTrigger -Once -At $at
            $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
            $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 10)
            Register-ScheduledTask -TaskName '{{TaskName}}' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
            """;
        return RunPowerShellAsync(script);
    }

    public Task DisarmAsync() => RunPowerShellAsync(
        $"Unregister-ScheduledTask -TaskName '{TaskName}' -Confirm:$false -ErrorAction SilentlyContinue");

    private static string Quote(string s) => s.Replace("'", "''");

    private static Task RunPowerShellAsync(string script)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return ProcessRunner.RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded],
            PowerShellTimeout);
    }
}
