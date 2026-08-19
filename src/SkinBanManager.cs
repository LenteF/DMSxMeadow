using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DMSxMeadow
{
    public static class SkinBanManager
    {
        private static readonly string BannedRootPath =
            $"{Application.persistentDataPath}{Path.DirectorySeparatorChar}dmsxmeadow{Path.DirectorySeparatorChar}";
        private static readonly string BannedFile = BannedRootPath + "blacklist.txt";

        private static readonly Dictionary<string, string> BannedById =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static bool _loaded = false;

        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            BannedById.Clear();

            try
            {
                if (File.Exists(BannedFile))
                {
                    foreach (var line in File.ReadAllLines(BannedFile))
                    {
                        var parsed = ParseLine(line);
                        if (string.IsNullOrEmpty(parsed.id)) continue;
                        BannedById[parsed.id] = parsed.name ?? "";
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error loading blacklist: {ex.Message}");
            }
        }

        private static (string name, string id) ParseLine(string line)
        {
            if (line == null) return (null, null);

            string trimmed = line.Trim();
            if (trimmed.Length == 0) return (null, null);

            int tabIndex = trimmed.LastIndexOf('\t');
            if (tabIndex >= 0)
            {
                return (trimmed.Substring(0, tabIndex).Trim(), trimmed.Substring(tabIndex + 1).Trim());
            }

            int end = trimmed.Length;
            while (end > 0 && char.IsWhiteSpace(trimmed[end - 1])) end--;

            int start = end;
            while (start > 0 && !char.IsWhiteSpace(trimmed[start - 1])) start--;

            if (start == end)
            {
                return (null, trimmed);
            }

            return (trimmed.Substring(0, start).Trim(), trimmed.Substring(start, end - start).Trim());
        }

        private static void Save()
        {
            try
            {
                if (!Directory.Exists(BannedRootPath))
                {
                    Directory.CreateDirectory(BannedRootPath);
                }

                var lines = new List<string>();
                foreach (var kvp in BannedById)
                {
                    string name = kvp.Value;
                    lines.Add(string.IsNullOrEmpty(name) ? kvp.Key : $"{name}\t{kvp.Key}");
                }

                File.WriteAllLines(BannedFile, lines);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error saving blacklist: {ex.Message}");
            }
        }

        public static bool IsBanned(string identity)
        {
            Load();
            return !string.IsNullOrEmpty(identity) && BannedById.ContainsKey(identity);
        }

        public static bool ToggleBan(string identity, string displayName = "")
        {
            Load();
            if (string.IsNullOrEmpty(identity)) return false;

            bool banned = !BannedById.ContainsKey(identity);
            if (banned)
            {
                BannedById[identity] = displayName ?? "";
            }
            else
            {
                BannedById.Remove(identity);
            }

            Save();
            return banned;
        }

        public static bool RemoveBan(string identity)
        {
            Load();
            if (string.IsNullOrEmpty(identity)) return false;

            bool removed = BannedById.Remove(identity);
            if (removed)
            {
                Save();
            }
            return removed;
        }

        public static List<(string name, string id)> GetAllBanned()
        {
            Load();
            var result = new List<(string, string)>(BannedById.Count);
            foreach (var kvp in BannedById)
            {
                result.Add((kvp.Value ?? "", kvp.Key));
            }
            return result;
        }
    }
}