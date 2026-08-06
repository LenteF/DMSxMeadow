using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using UnityEngine;

namespace DMSxMeadow
{
    [Serializable]
    public class MeadowProfileData
    {
        public int InternalProfileNumber;
        public Dictionary<string, DressMySlugcat.Customization> CustomizationsBySlugcat = new Dictionary<string, DressMySlugcat.Customization>();
        public DateTime LastUpdated = DateTime.Now;
    }

    [Serializable]
    public class MeadowDatabase
    {
        public Dictionary<int, MeadowProfileData> Profiles = new Dictionary<int, MeadowProfileData>();
        public int MaxProfiles = 99;
        public DateTime LastUpdated = DateTime.Now;
    }

    public static class MeadowProfileManager
    {
        // ============================================================
        // ARCHIVO 1: Datos de ropa de perfiles extendidos (meadowcustom.dat)
        // ============================================================
        private static string RootPath => $"{Application.persistentDataPath}{Path.DirectorySeparatorChar}dressmyslugcat{Path.DirectorySeparatorChar}";
        private static string SaveFile => RootPath + "meadowcustom.dat";

        // ============================================================
        // ARCHIVO 2: Asignaciones PlayerID -> Perfil (dmsxmeadow.txt)
        // ============================================================
        private static string AssignmentsRootPath => $"{Application.persistentDataPath}{Path.DirectorySeparatorChar}dmsxmeadow{Path.DirectorySeparatorChar}";
        private static string AssignmentsFile => AssignmentsRootPath + "dmsxmeadow.txt";

        private static MeadowDatabase _database;
        public static MeadowDatabase Database => _database ??= Load();

        public static int CurrentProfileNumber = 1;
        public static bool IsMeadowModeActive = false;

        private const int PROFILE_OFFSET = 4;

        private static Dictionary<string, int> _assignments = new Dictionary<string, int>();
        private static bool _assignmentsLoaded = false;
        private static Dictionary<int, MeadowProfileData> _unsavedProfiles = new Dictionary<int, MeadowProfileData>();

        public static int GetInternalProfile(int displayNumber) => displayNumber + PROFILE_OFFSET;
        public static int GetDisplayNumber(int internalNumber) => internalNumber - PROFILE_OFFSET;

        // ============================================================
        // CARGAR / GUARDAR PERFILES EXTENDIDOS
        // ============================================================
        public static MeadowDatabase Load()
        {
            try
            {
                if (File.Exists(SaveFile))
                {
                    using (var fs = new FileStream(SaveFile, FileMode.Open))
                    {
                        var formatter = new BinaryFormatter();
                        var loaded = (MeadowDatabase)formatter.Deserialize(fs);
                        if (loaded != null)
                        {
                            return loaded;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error loading meadow profiles: {ex.Message}");
            }

            var newDb = new MeadowDatabase();
            Save(newDb);
            return newDb;
        }

        public static void Save(MeadowDatabase db = null)
        {
            try
            {
                var toSave = db ?? _database;
                if (toSave == null) return;

                toSave.LastUpdated = DateTime.Now;

                if (!Directory.Exists(RootPath))
                {
                    Directory.CreateDirectory(RootPath);
                }

                using (var fs = new FileStream(SaveFile, FileMode.Create))
                {
                    var formatter = new BinaryFormatter();
                    formatter.Serialize(fs, toSave);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error saving meadow profiles: {ex.Message}");
            }
        }

        // ============================================================
        // CARGAR / GUARDAR ASIGNACIONES
        // ============================================================
        private static void LoadAssignments()
        {
            if (_assignmentsLoaded) return;
            _assignmentsLoaded = true;
            _assignments.Clear();

            try
            {
                if (File.Exists(AssignmentsFile))
                {
                    var lines = File.ReadAllLines(AssignmentsFile);
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;

                        var parts = trimmed.Split(':');
                        if (parts.Length == 2 && int.TryParse(parts[1], out int profileNumber))
                        {
                            var steamId = parts[0].Trim();
                            if (!string.IsNullOrEmpty(steamId))
                            {
                                _assignments[steamId] = profileNumber;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error loading assignments: {ex.Message}");
            }
        }

        public static void SaveAssignments()
        {
            try
            {
                if (!Directory.Exists(AssignmentsRootPath))
                {
                    Directory.CreateDirectory(AssignmentsRootPath);
                }

                var lines = new List<string>();
                lines.Add("# PlayerID:ProfileNumber");
                lines.Add("# Format: STEAM_0:1:12345678:5");
                lines.Add("# or 76561198000000000:6");
                lines.Add("");

                foreach (var kvp in _assignments)
                {
                    lines.Add($"{kvp.Key}:{kvp.Value}");
                }

                File.WriteAllLines(AssignmentsFile, lines);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error saving assignments: {ex.Message}");
            }
        }

        // ============================================================
        // OPERACIONES CON PERFILES
        // ============================================================
        public static MeadowProfileData GetOrCreateProfile(int displayNumber)
        {
            if (displayNumber < 1 || displayNumber > Database.MaxProfiles)
            {
                return null;
            }

            int internalNumber = GetInternalProfile(displayNumber);
            if (Database.Profiles == null)
                Database.Profiles = new Dictionary<int, MeadowProfileData>();

            if (Database.Profiles.TryGetValue(internalNumber, out var persisted))
                return persisted;

            if (_unsavedProfiles.TryGetValue(displayNumber, out var scratch))
                return scratch;

            var newProfile = new MeadowProfileData
            {
                InternalProfileNumber = internalNumber
            };
            _unsavedProfiles[displayNumber] = newProfile;
            return newProfile;
        }

        private static void DiscardUnsavedIfOrphan(int displayNumber)
        {
            if (string.IsNullOrEmpty(GetSteamID(displayNumber)) && _unsavedProfiles.ContainsKey(displayNumber))
            {
                _unsavedProfiles.Remove(displayNumber);
            }
        }

        public static void SetCurrentProfile(int displayNumber)
        {
            if (displayNumber < 1 || displayNumber > Database.MaxProfiles)
            {
                return;
            }

            DiscardUnsavedIfOrphan(CurrentProfileNumber);
            CurrentProfileNumber = displayNumber;
            GetOrCreateProfile(displayNumber);
        }

        public static void SaveCurrentProfile(string slugcatName, DressMySlugcat.Customization customization)
        {
            if (!IsMeadowModeActive) return;
            if (customization == null) return;
            if (string.IsNullOrEmpty(slugcatName)) return;

            var profile = GetOrCreateProfile(CurrentProfileNumber);
            if (profile == null) return;

            if (profile.CustomizationsBySlugcat == null)
                profile.CustomizationsBySlugcat = new Dictionary<string, DressMySlugcat.Customization>();

            profile.CustomizationsBySlugcat[slugcatName] = customization.Copy();
            profile.LastUpdated = DateTime.Now;

            string steamId = GetSteamID(CurrentProfileNumber);
            if (!string.IsNullOrEmpty(steamId))
            {
                int internalNumber = GetInternalProfile(CurrentProfileNumber);
                Database.Profiles[internalNumber] = profile;
                _unsavedProfiles.Remove(CurrentProfileNumber);
                Save();
            }
        }

        public static DressMySlugcat.Customization GetProfileCustomization(int displayNumber, string slugcatName)
        {
            var profile = GetOrCreateProfile(displayNumber);
            if (profile?.CustomizationsBySlugcat != null &&
                profile.CustomizationsBySlugcat.TryGetValue(slugcatName, out var cust))
            {
                return cust;
            }
            return null;
        }

        // ============================================================
        // OPERACIONES CON ASIGNACIONES SteamID
        // ============================================================
        public static void SetSteamID(int displayNumber, string steamID)
        {
            LoadAssignments();

            var oldKeys = new List<string>();
            foreach (var kvp in _assignments)
                if (kvp.Value == displayNumber) oldKeys.Add(kvp.Key);
            foreach (var k in oldKeys) _assignments.Remove(k);

            if (!string.IsNullOrEmpty(steamID))
            {
                _assignments[steamID] = displayNumber;
            }

            SaveAssignments();

            if (!string.IsNullOrEmpty(steamID) && _unsavedProfiles.TryGetValue(displayNumber, out var pending))
            {
                int internalNumber = GetInternalProfile(displayNumber);
                Database.Profiles[internalNumber] = pending;
                _unsavedProfiles.Remove(displayNumber);
                Save();
            }
        }

        public static string GetSteamID(int displayNumber)
        {
            LoadAssignments();

            foreach (var kvp in _assignments)
            {
                if (kvp.Value == displayNumber)
                {
                    return kvp.Key;
                }
            }
            return "";
        }

        public static int GetProfileBySteamID(string steamID)
        {
            LoadAssignments();

            if (string.IsNullOrEmpty(steamID)) return -1;

            if (_assignments.TryGetValue(steamID, out int profileNumber))
            {
                return profileNumber;
            }
            return -1;
        }

        public static DressMySlugcat.Customization GetCustomizationBySteamID(string steamId, string slugcatName)
        {
            LoadAssignments();

            if (string.IsNullOrEmpty(steamId)) return null;
            if (string.IsNullOrEmpty(slugcatName)) return null;

            if (_assignments.TryGetValue(steamId, out int profileNumber))
            {
                return GetProfileCustomization(profileNumber, slugcatName);
            }
            return null;
        }

        // ============================================================
        // MÉTODOS PARA EL MENÚ REMIX
        // ============================================================

        public static List<int> GetAllProfileNumbers()
        {
            var result = new HashSet<int>();

            try
            {
                if (Database.Profiles != null)
                {
                    foreach (int internalNum in Database.Profiles.Keys)
                    {
                        int displayNum = GetDisplayNumber(internalNum);
                        if (displayNum >= 1 && displayNum <= Database.MaxProfiles)
                        {
                            result.Add(displayNum);
                        }
                    }
                }

                LoadAssignments();
                foreach (var kvp in _assignments)
                {
                    if (kvp.Value >= 1 && kvp.Value <= Database.MaxProfiles)
                    {
                        result.Add(kvp.Value);
                    }
                }

                foreach (int displayNum in _unsavedProfiles.Keys)
                {
                    if (displayNum >= 1 && displayNum <= Database.MaxProfiles)
                    {
                        result.Add(displayNum);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error getting all profile numbers: {ex.Message}");
            }

            return result.ToList();
        }

        public static void DeleteProfile(int displayNumber)
        {
            try
            {
                int internalNum = GetInternalProfile(displayNumber);

                if (Database.Profiles != null && Database.Profiles.ContainsKey(internalNum))
                {
                    Database.Profiles.Remove(internalNum);
                    Save();
                }

                if (_unsavedProfiles.ContainsKey(displayNumber))
                {
                    _unsavedProfiles.Remove(displayNumber);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error deleting profile {displayNumber}: {ex.Message}");
            }
        }

        public static void RemoveAssignment(int displayNumber)
        {
            try
            {
                LoadAssignments();

                var keysToRemove = new List<string>();
                foreach (var kvp in _assignments)
                {
                    if (kvp.Value == displayNumber)
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }

                foreach (string key in keysToRemove)
                {
                    _assignments.Remove(key);
                }

                if (keysToRemove.Count > 0)
                {
                    SaveAssignments();
                }

                DiscardUnsavedIfOrphan(displayNumber);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error removing assignment for profile {displayNumber}: {ex.Message}");
            }
        }

        public static bool ProfileExists(int displayNumber)
        {
            try
            {
                int internalNum = GetInternalProfile(displayNumber);
                if (Database.Profiles != null && Database.Profiles.ContainsKey(internalNum))
                {
                    return true;
                }

                if (_unsavedProfiles.ContainsKey(displayNumber))
                {
                    return true;
                }

                LoadAssignments();
                foreach (var kvp in _assignments)
                {
                    if (kvp.Value == displayNumber)
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error checking profile existence: {ex.Message}");
            }

            return false;
        }

        public static int DeleteOrphanProfiles()
        {
            int deleted = 0;
            try
            {
                LoadAssignments();

                var assignedProfiles = new HashSet<int>();
                foreach (var kvp in _assignments)
                {
                    assignedProfiles.Add(kvp.Value);
                }

                if (Database.Profiles != null)
                {
                    var toRemove = new List<int>();
                    foreach (int internalNum in Database.Profiles.Keys)
                    {
                        int displayNum = GetDisplayNumber(internalNum);
                        if (!assignedProfiles.Contains(displayNum))
                        {
                            toRemove.Add(internalNum);
                        }
                    }

                    foreach (int internalNum in toRemove)
                    {
                        Database.Profiles.Remove(internalNum);
                        deleted++;
                    }

                    if (deleted > 0)
                    {
                        Save();
                    }
                }

                var toRemoveMemory = new List<int>();
                foreach (int displayNum in _unsavedProfiles.Keys)
                {
                    if (string.IsNullOrEmpty(GetSteamID(displayNum)))
                    {
                        toRemoveMemory.Add(displayNum);
                    }
                }

                foreach (int displayNum in toRemoveMemory)
                {
                    _unsavedProfiles.Remove(displayNum);
                    deleted++;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error deleting orphan profiles: {ex.Message}");
            }

            return deleted;
        }
    }
}
