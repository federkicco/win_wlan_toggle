using System.Diagnostics;
using System.Text;

namespace WlanToggle.Windows;

internal static class ProcessRunner
{
    /// <summary>Runs a program without a shell or window; throws if it fails or times out.</summary>
    public static async Task<string> RunAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"{exe} did not finish within {timeout.TotalSeconds:F0}s");
        }

        var output = (await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false)).Trim();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(exe)} exited with {p.ExitCode}: {output}");
        return output;
    }
}
