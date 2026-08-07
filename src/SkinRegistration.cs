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

            foreach (string filePath in Directory.GetFiles(skinFolder, "*.*", SearchOption.AllDirectories))
            {
                string relativePath = filePath.Substring(skinFolder.Length).TrimStart('\\', '/');
                files[relativePath] = File.ReadAllBytes(filePath);
            }

            Plugin.Logger.LogInfo($"[DMSxMeadow] 📦 Skin '{skinId}' empaquetada con éxito desde disco ({files.Count} archivos).");
            return files;
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

        public static bool SaveAndRegisterCacheSkin(string skinId, Dictionary<string, byte[]> files)
        {
            try
            {
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