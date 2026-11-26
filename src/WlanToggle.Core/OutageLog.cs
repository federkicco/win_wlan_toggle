using System.Globalization;
using System.Text;

namespace WlanToggle.Core;

public sealed record OutageRecord(
    string Source,
    string AdapterName,
    DateTimeOffset DownAtUtc,
    DateTimeOffset UpAtUtc,
    TimeSpan RequestedDown,
    TimeSpan ActualDown,
    TimeSpan TimeToUp,
    int EnableAttempts,
    bool Recovered,
    string? Error);

public interface IOutageLogSink
{
    void Write(OutageRecord record);
}

public sealed class NullOutageLogSink : IOutageLogSink
{
    public void Write(OutageRecord record) { }
}

public sealed class CsvOutageLogSink(string path) : IOutageLogSink
{
    private const string Header =
        "source,adapter,down_at_utc,up_at_utc,requested_down_s,actual_down_s,time_to_up_s,enable_attempts,recovered,error";
    private readonly object _gate = new();

    public void Write(OutageRecord r)
    {
        lock (_gate)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            if (!File.Exists(path)) sb.AppendLine(Header);
            var inv = CultureInfo.InvariantCulture;
            sb.AppendLine(string.Join(',',
                Escape(r.Source), Escape(r.AdapterName),
                r.DownAtUtc.ToString("O", inv), r.UpAtUtc.ToString("O", inv),
                r.RequestedDown.TotalSeconds.ToString("F3", inv),
                r.ActualDown.TotalSeconds.ToString("F3", inv),
                r.TimeToUp.TotalSeconds.ToString("F3", inv),
                r.EnableAttempts.ToString(inv), r.Recovered ? "true" : "false",
                Escape(r.Error ?? "")));
            File.AppendAllText(path, sb.ToString());
        }
    }

    private static string Escape(string s) =>
        s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
