using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlockRdpBruteForce.State;

public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly string _path;
    private Dictionary<IPAddress, IpRecord> _records = new();

    public StateStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Environment.ExpandEnvironmentVariables(path);
    }

    public string ResolvedPath => _path;

    public int Count
    {
        get { lock (_gate) return _records.Count; }
    }

    public void Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                _records = new();
                return;
            }
            try
            {
                var json = File.ReadAllText(_path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    _records = new();
                    return;
                }
                var state = JsonSerializer.Deserialize<BlockState>(json, JsonOpts);
                _records = (state?.Ips ?? new())
                    .Where(r => IPAddress.TryParse(r.Ip, out _))
                    .ToDictionary(r => IPAddress.Parse(r.Ip), r => r);
            }
            catch (JsonException)
            {
                _records = new();
            }
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var snapshot = new BlockState
            {
                Ips = _records.Values
                    .OrderBy(r => r.Ip, StringComparer.Ordinal)
                    .ToList(),
            };
            var json = JsonSerializer.Serialize(snapshot, JsonOpts);

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(_path))
                File.Replace(tmp, _path, destinationBackupFileName: null);
            else
                File.Move(tmp, _path);
        }
    }

    public IpRecord Upsert(IPAddress ip, DateTime utcNow, TimeSpan? blockDuration)
    {
        ArgumentNullException.ThrowIfNull(ip);
        lock (_gate)
        {
            if (!_records.TryGetValue(ip, out var rec))
            {
                rec = new IpRecord { Ip = ip.ToString(), FirstSeenUtc = utcNow };
                _records[ip] = rec;
            }
            rec.Count++;
            rec.LastSeenUtc = utcNow;
            rec.BlockedUntilUtc = blockDuration is { } d ? utcNow + d : null;
            return Clone(rec);
        }
    }

    public bool Remove(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        lock (_gate) return _records.Remove(ip);
    }

    public IpRecord? TryGet(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        lock (_gate) return _records.TryGetValue(ip, out var rec) ? Clone(rec) : null;
    }

    public IReadOnlyList<IpRecord> Snapshot()
    {
        lock (_gate) return _records.Values.Select(Clone).ToList();
    }

    public IReadOnlyList<IPAddress> ActiveBlockedIps(DateTime utcNow)
    {
        lock (_gate)
        {
            return _records
                .Where(kv => !kv.Value.BlockedUntilUtc.HasValue || kv.Value.BlockedUntilUtc.Value > utcNow)
                .Select(kv => kv.Key)
                .ToList();
        }
    }

    public IReadOnlyList<IPAddress> ExpiredIps(DateTime utcNow)
    {
        lock (_gate)
        {
            return _records
                .Where(kv => kv.Value.BlockedUntilUtc is { } until && until <= utcNow)
                .Select(kv => kv.Key)
                .ToList();
        }
    }

    private static IpRecord Clone(IpRecord r) => new()
    {
        Ip = r.Ip,
        Count = r.Count,
        FirstSeenUtc = r.FirstSeenUtc,
        LastSeenUtc = r.LastSeenUtc,
        BlockedUntilUtc = r.BlockedUntilUtc,
    };
}
