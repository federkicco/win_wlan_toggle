using System.Text.Json;

namespace WlanToggle.Core;

/// <summary>An outage we started and have not yet verified as recovered.</summary>
public sealed record PendingOutage(
    string AdapterName,
    DateTimeOffset DisabledAtUtc,
    DateTimeOffset ReenableAtUtc,
    string Source);

/// <summary>
/// Durable "dead-man's switch" record. Written BEFORE the link is taken down and cleared only
/// after the link is verified up again, so a crash at any point leaves enough information to recover.
/// </summary>
public interface IJournalStore
{
    PendingOutage? Load();
    void Save(PendingOutage outage);
    void Clear();
}

public sealed class InMemoryJournalStore : IJournalStore
{
    private PendingOutage? _value;
    public PendingOutage? Load() => _value;
    public void Save(PendingOutage outage) => _value = outage;
    public void Clear() => _value = null;
}

public sealed class FileJournalStore(string path) : IJournalStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public PendingOutage? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<PendingOutage>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt journal must never block recovery; treat as "unknown, assume nothing pending".
            return null;
        }
    }

    public void Save(PendingOutage outage)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // Write-then-replace so a crash mid-write cannot leave a truncated journal.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(outage, Json));
        File.Move(tmp, path, overwrite: true);
    }

    public void Clear()
    {
        try { File.Delete(path); }
        catch (IOException) { }
    }
}
