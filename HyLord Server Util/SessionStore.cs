using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace HyLordServerUtil
{
    public class SessionRecord
    {
        public string Hash { get; set; } = "";
        public string LastName { get; set; } = "";
        public long TotalSeconds { get; set; } = 0;
        public DateTime LastSeen { get; set; } = DateTime.MinValue;
    }

    public class SessionStore
    {
        private readonly string path;
        private readonly Dictionary<string, SessionRecord> byHash =
            new(StringComparer.OrdinalIgnoreCase);

        public SessionStore(string path)
        {
            this.path = path;
            Load();
        }

        public SessionRecord GetOrCreate(string hash)
        {
            if (!byHash.TryGetValue(hash, out var rec))
            {
                rec = new SessionRecord { Hash = hash };
                byHash[hash] = rec;
            }
            return rec;
        }

        public void OnJoin(PlayerInfo p)
        {
            if (string.IsNullOrWhiteSpace(p.Hash)) return;

            var rec = GetOrCreate(p.Hash);
            rec.LastName = p.Name;
            Save();
        }

        public void OnLeave(PlayerInfo p)
        {
            if (string.IsNullOrWhiteSpace(p.Hash)) return;

            var rec = GetOrCreate(p.Hash);
            rec.LastName = p.Name;

            var seconds = (long)Math.Max(0, (DateTime.Now - p.JoinedAt).TotalSeconds);
            rec.TotalSeconds += seconds;
            rec.LastSeen = DateTime.Now;

            Save();
        }

        public string GetTotalPlaytimeString(string hash)
        {
            if (string.IsNullOrWhiteSpace(hash)) return "--";
            if (!byHash.TryGetValue(hash, out var rec)) return "--";

            var ts = TimeSpan.FromSeconds(rec.TotalSeconds);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}h {ts.Minutes}m"
                : $"{ts.Minutes}m {ts.Seconds}s";
        }

        private void Load()
        {
            if (!File.Exists(path)) return;

            try
            {
                var json = File.ReadAllText(path);
                var list = JsonSerializer.Deserialize<List<SessionRecord>>(json);
                if (list == null) return;

                byHash.Clear();
                foreach (var rec in list)
                {
                    if (!string.IsNullOrWhiteSpace(rec.Hash))
                        byHash[rec.Hash] = rec;
                }
            }
            catch
            {
            }
        }

        private void Save()
        {
            try
            {
                var list = new List<SessionRecord>(byHash.Values);
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(path, JsonSerializer.Serialize(list, options));
            }
            catch
            {
                // fuckitttt
            }
        }
    }
}