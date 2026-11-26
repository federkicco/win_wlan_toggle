using WlanToggle.Core;

namespace WlanToggle.Core.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wlantoggle-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void File_journal_round_trips_and_clears()
    {
        var j = new FileJournalStore(Path.Combine(_dir, "sub", "state.json"));
        Assert.Null(j.Load());
        var p = new PendingOutage("Wi-Fi", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(10), "manual");
        j.Save(p);
        Assert.Equal(p, j.Load());
        j.Clear();
        Assert.Null(j.Load());
        j.Clear(); // idempotent
    }

    [Fact]
    public void Corrupt_journal_is_treated_as_empty_not_fatal()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "state.json");
        File.WriteAllText(path, "{ not json");
        Assert.Null(new FileJournalStore(path).Load());
    }

    [Fact]
    public void Csv_sink_writes_header_once_and_escapes_fields()
    {
        var path = Path.Combine(_dir, "log.csv");
        var sink = new CsvOutageLogSink(path);
        var now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var rec = new OutageRecord("manual", "Wi-Fi", now, now.AddSeconds(10), TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(10.25), TimeSpan.FromSeconds(1.5), 1, true, "a, \"b\"");
        sink.Write(rec);
        sink.Write(rec with { Error = null });

        var lines = File.ReadAllLines(path);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("source,adapter,", lines[0]);
        Assert.Equal("manual,Wi-Fi,2026-01-02T03:04:05.0000000+00:00,2026-01-02T03:04:15.0000000+00:00,10.000,10.250,1.500,1,true,\"a, \"\"b\"\"\"", lines[1]);
    }

    [Fact]
    public void Config_round_trips_and_defaults_when_missing()
    {
        var path = Path.Combine(_dir, "config.json");
        Assert.Equal("Wi-Fi", WlanToggleConfig.Load(path).AdapterName);

        var cfg = new WlanToggleConfig
        {
            AdapterName = "WLAN 2",
            Method = DisconnectMethod.Radio,
            Periodic = new PeriodicOptions { Interval = TimeSpan.FromMinutes(2), Down = TimeSpan.FromSeconds(7), MaxCycles = 10 },
        };
        cfg.Save(path);
        Assert.Contains("\"Radio\"", File.ReadAllText(path));
        var back = WlanToggleConfig.Load(path);
        Assert.Equal(cfg, back with { }); // records compare nested records by value
        back.Validate();
    }

    [Fact]
    public void Config_validation_rejects_unsafe_values()
    {
        var bad = new WlanToggleConfig { Periodic = new PeriodicOptions { Interval = TimeSpan.FromSeconds(10), Down = TimeSpan.FromSeconds(10) } };
        Assert.Throws<ArgumentException>(bad.Validate);
        Assert.Throws<ArgumentException>(new WlanToggleConfig { AdapterName = " " }.Validate);
    }
}
