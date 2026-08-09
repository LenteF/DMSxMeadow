using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DressMySlugcat;
using DressMySlugcat.Hooks;
using UnityEngine;


namespace DMSxMeadow
{
    public static class SkinRegistration
    {
        private static string CacheSkinsPath // Para la release habrá que cambiar el path al de la workshop
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
            // El resto de skins de DMS ya las añadiré
        };

        /// <summary>
        /// Caché de skins recibidas, agrupada por el SteamID del emisor que las envió.
        /// Permite saber quién aportó cada skin y limpiar TODO lo de un jugador cuando sale
        /// de la partida (HandleDisconnect). H-5 parcial: los bytes viven en memoria.
        /// </summary>
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
                skinsBySender = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal);
                MemorySkinCacheBySender[senderSteamId] = skinsBySender;
            }

            skinsBySender[skinId] = files;
        }

        /// <summary>Borra de la memoria todo lo aportado por un emisor y su carpeta de caché en
        /// disco si dejó de estar referenciada por otros jugadores vivos (JcA-float heap).</summary>
        public static int ClearCachedSkinsFor(string senderSteamId)
        {
            int cleared = 0;
            if (senderSteamId == null) return 0;

            if (MemorySkinCacheBySender.TryGetValue(senderSteamId, out var skinsBySender))
            {
                cleared = skinsBySender.Count;
                MemorySkinCacheBySender.Remove(senderSteamId);

                foreach (string skinId in skinsBySender.Keys)
                {
                    TryDeleteOrphanedCacheFolder(skinId);
                }
            }

            return cleared;
        }

        private static void TryDeleteOrphanedCacheFolder(string skinId)
        {
            bool stillUsedElsewhere = MemorySkinCacheBySender.Values.Any(v => v.ContainsKey(skinId));
            if (stillUsedElsewhere) return;

            try
            {
                string targetFolder = Path.Combine(CacheSkinsPath, skinId);
                if (Directory.Exists(targetFolder))
                {
                    Directory.Delete(targetFolder, true);
                    Plugin.Logger.LogInfo($"[DMSxMeadow] 🧹 Carpeta de caché de skin '{skinId}' eliminada (emisor salió y nadie más la usa).");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[DMSxMeadow] No se pudo borrar la carpeta de caché de '{skinId}': {ex.Message}");
            }
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

            // PASTTA 1: recolectar los PNG de partes (descartando thumbnails/previews/iconos).
            foreach (string filePath in foundFiles)
            {
                string relativePath = filePath.Substring(skinFolder.Length).TrimStart('\\', '/');
                string fileName = Path.GetFileName(filePath);

                if (!".png".Equals(Path.GetExtension(fileName), StringComparison.OrdinalIgnoreCase)) continue;
                if (IsNonPartFile(fileName)) continue;

                pngPaths.Add(relativePath);
            }

            // PASO 2: cada PNG con su TXT par (mismo nombre base) + metadata.json como índice.
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

        /// <summary>Archivos de un paquete de skin que NO son partes del slugcat y nunca deben transferirse (thumbnail, preview, iconos, banners).</summary>
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
                // Caso A: La ruta del mod tiene carpeta dressmyslugcat directa (improbable)
                string directDmsPath = Path.Combine(rootFolder, "dressmyslugcat");
                if (Directory.Exists(directDmsPath))
                {
                    string match = CheckDmsDirectoryForSkin(directDmsPath, skinId);
                    if (match != null) return match;
                }

                // Caso B: Es un contenedor de mods (mods/ o content/312520/)
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
                            // Verificamos si alguna subcadena del ID coincide con el ID guardado en metadata.json
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

                string targetFolder = Path.Combine(CacheSkinsPath, skinId);
                string targetMetadata = Path.Combine(targetFolder, "metadata.json");

                if (Directory.Exists(targetFolder) && File.Exists(targetMetadata))
                {
                    Plugin.Logger.LogWarning($"[DMSxMeadow] ⚡ La skin '{skinId}' ya existe en la caché local. Se omite la copia en disco.");
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

                Plugin.Logger.LogInfo($"[DMSxMeadow] 💾 Skin '{skinId}' guardada por primera vez en caché: {targetFolder}");
                AtlasHooks.ReloadAtlases();
                Plugin.Logger.LogInfo($"[DMSxMeadow] ✅ AtlasHooks.ReloadAtlases() ejecutado.");

                // La recarga de atlas invalida los sprites ya dibujados (invisible/blanco
                // hasta recrearse). Recrear todos los slugs realizados, incluido el local.
                Plugin.ScheduleRecreateAllSlugs();
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[DMSxMeadow] Error al crear la skin caché: {ex}");
                return false;
            }
        }

        public static bool IsSkinAlreadyInstalled(string skinId)
        {
            if (string.IsNullOrEmpty(skinId)) return false;
            if (NativeDmsSkins.Contains(skinId)) return true;

            string installedPath = FindSkinDirectoryOnDisk(skinId);
            if (string.IsNullOrEmpty(installedPath)) return false;

            string normalizedCachePath = Path.GetFullPath(CacheSkinsPath).TrimEnd('\\', '/');
            string normalizedFoundPath = Path.GetFullPath(installedPath).TrimEnd('\\', '/');

            return !normalizedFoundPath.StartsWith(normalizedCachePath, StringComparison.OrdinalIgnoreCase);
        }
    }
}