using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DressMySlugcat;
using UnityEngine;


namespace DMSxMeadow
{
    public static class SkinRegistration
    {
        private static string CacheSkinsPath
        {
            get
            {
                string modsPath = Path.Combine(Application.dataPath, "StreamingAssets", "mods");
                return Path.Combine(modsPath, "dmsxmeadow", "dressmyslugcat");
            }
        }
        public static readonly HashSet<string> NativeDmsSkins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "dressmyslugcat.default",
            "dressmyslugcat.empty"
        };

        // ============================================================
        // CACHÉ EN MEMORIA DE SKINS RECIBIDAS (por SteamID del emisor)
        // ============================================================
        private static readonly Dictionary<string, Dictionary<string, Dictionary<string, byte[]>>> MemorySkinCacheBySender =
            new Dictionary<string, Dictionary<string, Dictionary<string, byte[]>>>(StringComparer.Ordinal);

        public static bool HasCachedSkinFrom(string senderSteamId, string skinId)
        {
            return senderSteamId != null
                && MemorySkinCacheBySender.TryGetValue(senderSteamId, out var skinsBySender)
                && skinsBySender.ContainsKey(skinId);
        }

        public static void CacheSkinInMemory(string senderSteamId, string skinId, Dictionary<string, byte[]> files)
        {
            if (senderSteamId == null || skinId == null || files == null) return;

            if (!MemorySkinCacheBySender.TryGetValue(senderSteamId, out var skinsBySender))
            {
                skinsBySender = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.OrdinalIgnoreCase);
                MemorySkinCacheBySender[senderSteamId] = skinsBySender;
            }

            skinsBySender[skinId] = files;
        }

        public static int ClearCachedSkinsFor(string senderSteamId)
        {
            int cleared = 0;
            if (senderSteamId == null) return 0;

            if (MemorySkinCacheBySender.TryGetValue(senderSteamId, out var skinsBySender))
            {
                cleared = skinsBySender.Count;
                MemorySkinCacheBySender.Remove(senderSteamId);
            }

            return cleared;
        }

        public static HashSet<string> GetEquippedSkinIds(Player player)
        {
            HashSet<string> equippedSkins = new HashSet<string>();

            if (player?.playerState == null)
            {
                return equippedSkins;
            }

            string slugcatName = ((ExtEnumBase)player.slugcatStats.name).value;
            int playerNumber = player.playerState.playerNumber;

            Customization customization = SaveManager.Customizations.FirstOrDefault(x => x.Matches(slugcatName, playerNumber));

            if (customization != null && customization.CustomSprites != null)
            {
                foreach (CustomSprite customSprite in customization.CustomSprites)
                {
                    if (customSprite != null && !string.IsNullOrEmpty(customSprite.SpriteSheetID) && !customSprite.SpriteSheetID.Equals(SpriteSheet.DefaultName, StringComparison.OrdinalIgnoreCase))
                    {
                        equippedSkins.Add(customSprite.SpriteSheetID);
                    }
                }
            }

            return equippedSkins;
        }

        public static Dictionary<string, byte[]> ExportEquippedSkinToDTO(string skinId)
        {
            var files = new Dictionary<string, byte[]>();

            string skinFolder = FindSkinDirectoryOnDisk(skinId);
            if (string.IsNullOrEmpty(skinFolder) || !Directory.Exists(skinFolder))
            {
                Plugin.Logger.LogError($"[DMSxMeadow] ❌ No se encontró la carpeta física de la skin '{skinId}' en los mods.");
                return files;
            }

            var foundFiles = Directory.GetFiles(skinFolder, "*.*", SearchOption.AllDirectories);
            var pngPaths = new List<string>();

            // PASO 1: recolectar los PNG de partes
            foreach (string filePath in foundFiles)
            {
                string relativePath = filePath.Substring(skinFolder.Length).TrimStart('\\', '/');
                string fileName = Path.GetFileName(filePath);

                if (!".png".Equals(Path.GetExtension(fileName), StringComparison.OrdinalIgnoreCase)) continue;
                if (IsNonPartFile(fileName)) continue;

                pngPaths.Add(relativePath);
            }

            // PASO 2: PNG con su TXT par + metadata.json
            foreach (string pngRelative in pngPaths)
            {
                string pngFull = Path.Combine(skinFolder, pngRelative);
                files[pngRelative] = File.ReadAllBytes(pngFull);

                string txtRelative = Path.ChangeExtension(pngRelative, ".txt");
                string txtFull = Path.Combine(skinFolder, txtRelative);
                if (File.Exists(txtFull))
                {
                    files[txtRelative] = File.ReadAllBytes(txtFull);
                }
                else
                {
                    Plugin.Logger.LogWarning($"[DMSxMeadow] Skin '{skinId}': '{pngRelative}' no tiene su .txt compañero. Se transfiere igual (DMS la ignorará).");
                }
            }

            string metadataRelative = "metadata.json";
            string metadataFull = Path.Combine(skinFolder, metadataRelative);
            if (File.Exists(metadataFull))
            {
                files[metadataRelative] = File.ReadAllBytes(metadataFull);
            }

            Plugin.Logger.LogInfo($"[DMSxMeadow] 📦 Skin '{skinId}' empaquetada con éxito desde disco ({files.Count} archivos: {pngPaths.Count} partes + txts + metadata).");
            return files;
        }

        private static bool IsNonPartFile(string fileName)
        {
            string name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();

            return name.StartsWith("thumbnail", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("thumb", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("preview", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("icon", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("banner", StringComparison.OrdinalIgnoreCase)
                || name.Equals("logo", StringComparison.OrdinalIgnoreCase);
        }

        private static string FindSkinDirectoryOnDisk(string skinId)
        {
            if (string.IsNullOrEmpty(skinId) || NativeDmsSkins.Contains(skinId)) return null;

            List<string> searchRoots = new List<string>();

            string localModsPath = Path.Combine(Application.dataPath, "StreamingAssets", "mods");
            if (Directory.Exists(localModsPath)) 
            {
                searchRoots.Add(localModsPath);
            }

            try
            {
                foreach (var mod in ModManager.InstalledMods)
                {
                    if (mod != null && !string.IsNullOrEmpty(mod.path) && Directory.Exists(mod.path))
                    {
                        searchRoots.Add(mod.path);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[DMSxMeadow] No se pudo leer ModManager.InstalledMods: {ex.Message}");
            }

            try
            {
                DirectoryInfo dataDir = new DirectoryInfo(Application.dataPath);
                DirectoryInfo steamAppsDir = dataDir.Parent?.Parent;

                if (steamAppsDir != null && steamAppsDir.Exists)
                {
                    string workshopPath = Path.Combine(steamAppsDir.FullName, "workshop", "content", "312520");
                    if (Directory.Exists(workshopPath) && !searchRoots.Contains(workshopPath))
                    {
                        searchRoots.Add(workshopPath);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[DMSxMeadow] No se pudo resolver la ruta directa de Workshop: {ex.Message}");
            }

            foreach (string rootFolder in searchRoots)
            {
                string directDmsPath = Path.Combine(rootFolder, "dressmyslugcat");
                if (Directory.Exists(directDmsPath))
                {
                    string match = CheckDmsDirectoryForSkin(directDmsPath, skinId);
                    if (match != null) return match;
                }

                if (Directory.Exists(rootFolder))
                {
                    foreach (string subDir in Directory.GetDirectories(rootFolder))
                    {
                        string dmsPath = Path.Combine(subDir, "dressmyslugcat");
                        if (Directory.Exists(dmsPath))
                        {
                            string match = CheckDmsDirectoryForSkin(dmsPath, skinId);
                            if (match != null) return match;
                        }
                    }
                }
            }

            Plugin.Logger.LogError($"[DMSxMeadow] ❌ No se encontró la carpeta física para la skin '{skinId}' ni en local ni en Workshop.");
            return null;
        }

        private static string CheckDmsDirectoryForSkin(string dmsPath, string skinId)
        {
            foreach (string skinDir in Directory.GetDirectories(dmsPath))
            {
                string jsonPath = Path.Combine(skinDir, "metadata.json");
                if (File.Exists(jsonPath))
                {
                    try
                    {
                        string jsonText = File.ReadAllText(jsonPath);
                        if (jsonText.Contains($"\"id\": \"{skinId}\"") || jsonText.Contains($"\"id\":\"{skinId}\""))
                        {
                            Plugin.Logger.LogInfo($"[DMSxMeadow] 🎯 Skin '{skinId}' encontrada con éxito en: {skinDir}");
                            return skinDir;
                        }

                        string folderName = Path.GetFileName(skinDir);
                        if (skinId.EndsWith(folderName, StringComparison.OrdinalIgnoreCase) ||
                            skinId.Contains(folderName) ||
                            jsonText.IndexOf(folderName, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            foreach (string part in skinId.Split('.', '_', ' '))
                            {
                                if (part.Length > 2 && jsonText.Contains($"\"{part}\""))
                                {
                                    Plugin.Logger.LogInfo($"[DMSxMeadow] 🎯 Skin '{skinId}' encontrada (Coincidencia Parcial: '{part}') en: {skinDir}");
                                    return skinDir;
                                }
                            }
                        }
                    }
                    catch
                    {

                    }
                }
            }
            return null;
        }

        public static bool SaveAndRegisterCacheSkin(string senderSteamId, string skinId, Dictionary<string, byte[]> files)
        {
            try
            {
                CacheSkinInMemory(senderSteamId, skinId, files);
                Plugin.Logger.LogInfo($"[DMSxMeadow] 🧠 Skin '{skinId}' registrada en memoria (emisor {senderSteamId}).");

                if (IsSkinAlreadyInstalled(skinId))
                {
                    Plugin.Logger.LogWarning($"[DMSxMeadow] ⚡ La skin '{skinId}' ya existe instalada localmente o en la Workshop. No se crea copia en caché.");
                    return true;
                }

                string targetFolder = GetCacheFolderForSender(senderSteamId);
                if (string.IsNullOrEmpty(targetFolder))
                {
                    Plugin.Logger.LogWarning($"[DMSxMeadow] ⚠️ SteamID del emisor no válido para nombre de carpeta ('{senderSteamId}'). Solo se registra en memoria.");
                    return true;
                }
                string targetMetadata = Path.Combine(targetFolder, "metadata.json");

                if (Directory.Exists(targetFolder) && File.Exists(targetMetadata) && FolderHoldsSkin(targetFolder, skinId))
                {
                    Plugin.Logger.LogWarning($"[DMSxMeadow] ⚡ La skin '{skinId}' ya está en la caché de perfil de {senderSteamId}. Se omite la copia en disco.");
                    HotLoadAtlases(skinId, targetFolder);
                    return true;
                }

                if (Directory.Exists(targetFolder))
                {
                    Directory.Delete(targetFolder, true);
                }
                Directory.CreateDirectory(targetFolder);

                foreach (var kvp in files)
                {
                    string filePath = Path.Combine(targetFolder, kvp.Key);
                    string fileDir = Path.GetDirectoryName(filePath);

                    if (!Directory.Exists(fileDir))
                    {
                        Directory.CreateDirectory(fileDir);
                    }

                    File.WriteAllBytes(filePath, kvp.Value);
                }

                Plugin.Logger.LogInfo($"[DMSxMeadow] 💾 Skin '{skinId}' del jugador {senderSteamId} guardada en caché de perfil: {targetFolder}");
                HotLoadAtlases(skinId, targetFolder);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[DMSxMeadow] Error al crear la skin caché: {ex}");
                return false;
            }
        }

        private static void HotLoadAtlases(string skinId, string targetFolder)
        {
            try
            {
                if (string.IsNullOrEmpty(targetFolder) || !Directory.Exists(targetFolder)) return;

                DressMySlugcat.Hooks.AtlasHooks.LoadAtlases(targetFolder);
                Plugin.Logger.LogInfo($"[DMSxMeadow] 🔄 Atlases de '{skinId}' cargados en caliente y registrados en el catálogo de DMS.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[DMSxMeadow] No se pudieron cargar los atlases en caliente de '{skinId}': {ex.Message}");
            }
        }

        private static string GetCacheFolderForSender(string senderSteamId)
        {
            if (string.IsNullOrEmpty(senderSteamId)) return null;

            string normalized = senderSteamId.Trim();
            if (normalized.Length < 4) return null;

            foreach (char ch in normalized)
            {
                if (!char.IsLetterOrDigit(ch) && ch != '_' && ch != '-' && ch != '.')
                {
                    return null;
                }
            }

            return Path.Combine(CacheSkinsPath, normalized);
        }

        private static bool FolderHoldsSkin(string folder, string skinId)
        {
            try
            {
                string metaPath = Path.Combine(folder, "metadata.json");
                if (!File.Exists(metaPath)) return false;

                string jsonText = File.ReadAllText(metaPath);
                int idx = jsonText.IndexOf("\"id\"", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return false;

                int colon = jsonText.IndexOf(':', idx);
                int q1 = jsonText.IndexOf('"', colon);
                int q2 = jsonText.IndexOf('"', q1 + 1);
                if (colon < 0 || q1 < 0 || q2 < idx) return false;

                string storedId = jsonText.Substring(q1 + 1, q2 - q1 - 1);
                return storedId.Equals(skinId, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static void ClearCacheOnStartup()
        {
            try
            {
                if (!Directory.Exists(CacheSkinsPath)) return;

                int removed = 0;
                foreach (string dir in Directory.GetDirectories(CacheSkinsPath))
                {
                    try
                    {
                        Directory.Delete(dir, true);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Logger.LogWarning($"[DMSxMeadow] No se pudo borrar carpeta de caché antigua '{Path.GetFileName(dir)}': {ex.Message}");
                    }
                    removed++;
                }

                Plugin.Logger.LogInfo($"[DMSxMeadow] 🧹 Caché de skins del arranque anterior borrada en disco ({removed} carpeta(s)). La skin se re-transmitirá al volver a entrar.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[DMSxMeadow] No se pudo completar la limpieza de cachés: {ex.Message}");
            }
        }

        public static bool IsSkinAlreadyInstalled(string skinId)
        {
            if (string.IsNullOrEmpty(skinId)) return false;
            if (NativeDmsSkins.Contains(skinId)) return true;

            if (DressMySlugcat.Plugin.SpriteSheets != null
                && DressMySlugcat.Plugin.SpriteSheets.Any(s => s != null && !string.IsNullOrEmpty(s.ID)
                    && s.ID.Equals(skinId, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            string installedPath = FindSkinDirectoryOnDisk(skinId);
            if (string.IsNullOrEmpty(installedPath)) return false;

            string normalizedCachePath = Path.GetFullPath(CacheSkinsPath).TrimEnd('\\', '/');
            string normalizedFoundPath = Path.GetFullPath(installedPath).TrimEnd('\\', '/');

            return !normalizedFoundPath.StartsWith(normalizedCachePath, StringComparison.OrdinalIgnoreCase);
        }
    }
}
