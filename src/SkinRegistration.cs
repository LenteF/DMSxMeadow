using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
        // Hojas nativas de DMS: se renderizan desde el propio juego, nunca se transfieren
        // (verificado contra DMS/src/plugin/SpriteSheet.cs: DefaultName="rainworld.default",
        // EmptyName="dressmyslugcat.empty").
        public static readonly HashSet<string> NativeDmsSkins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "rainworld.default",
            "dressmyslugcat.empty"
        };

        // H-3: los skinId/fileName llegan de la red y se usan en Path.Combine(CacheSkinsPath, ...)
        // sin validar (path traversal). Solo se aceptan nombres planos: letras, dígitos, '_', '.', '-'.
        private static readonly Regex ValidSkinIdentifierRegex =
            new Regex("^[a-zA-Z0-9_.-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static bool IsValidSkinIdentifier(string value)
        {
            return !string.IsNullOrEmpty(value) && ValidSkinIdentifierRegex.IsMatch(value);
        }

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

        // ===================================================================
        // H-5: RENOMBRADO EN DISCO (anti-contaminación del catálogo local)
        // Cada skin recibida se guarda como "dmsxm_{sufijo_emisor}_{skinId}" y el
        // metadata.json se reescribe con ese mismo id (verificado: AtlasHooks.cs:95-98
        // registra el SpriteSheet con el "id" del metadata). Así el id "oficial" del
        // skin ajeno jamás aparece en el catálogo local (gallery, atlases, autocompletado).
        // El slug del emisor lo referencia por su id original, que se resuelve al
        // renombrado en tiempo de consulta (SkinSerializer.ApplyRemapToCustomization).
        // ===================================================================

        private const string DmsxmSkinPrefix = "dmsxm";

        private static readonly Dictionary<string, Dictionary<string, string>> MemoryRemapBySender =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        /// <summary>Reduce un SteamID (o id de jugador) a caracteres alfanuméricos planos,
        /// manteniendo el id COMPLETO, para usarlo como sufijo de carpeta sin violar la
        /// validación H-3 (^[a-zA-Z0-9_.-]+$). Único límite defensivo: 32 chars, margen de
        /// sobra para SteamIDs de 17 dígitos que llegan por m_SteamID.ToString().</summary>
        private static string SanitizeSteamIdForPath(string steamId)
        {
            if (string.IsNullOrEmpty(steamId)) return "unknown";

            var chars = new List<char>(steamId.Length);
            foreach (char c in steamId)
            {
                if (char.IsLetterOrDigit(c)) chars.Add(c);
            }

            if (chars.Count == 0) return "unknown";

            int maxLen = Math.Min(chars.Count, 32);
            return new string(chars.GetRange(0, maxLen).ToArray());
        }

        /// <summary>Id renombrado determinista de la skin de un emisor: exclusivo de
        /// (emisor, skin original) y estable entre sesiones.</summary>
        public static string ComputeRenamedSkinId(string senderSteamId, string skinId)
        {
            return $"{DmsxmSkinPrefix}_{SanitizeSteamIdForPath(senderSteamId)}_{skinId}";
        }

        /// <summary>Devuelve el id renombrado registrado para (emisor, skin), o null si aún
        /// no se ha guardado en caché ninguna skin de ese emisor.</summary>
        public static string GetActiveRemap(string senderSteamId, string skinId)
        {
            if (string.IsNullOrEmpty(senderSteamId) || string.IsNullOrEmpty(skinId)) return null;

            if (MemoryRemapBySender.TryGetValue(senderSteamId, out var remaps) &&
                remaps.TryGetValue(skinId, out string renamedId))
            {
                return renamedId;
            }

            return null;
        }

        /// <summary>Registra (o devuelve) el id renombrado que se usará en disco para la skin
        /// de un emisor. Se llama al guardar la skin, nunca antes de que exista en la caché.</summary>
        public static string GetOrCreateRemap(string senderSteamId, string skinId)
        {
            if (string.IsNullOrEmpty(senderSteamId) || string.IsNullOrEmpty(skinId))
            {
                return skinId;
            }

            if (!MemoryRemapBySender.TryGetValue(senderSteamId, out var remaps))
            {
                remaps = new Dictionary<string, string>(StringComparer.Ordinal);
                MemoryRemapBySender[senderSteamId] = remaps;
            }

            if (remaps.TryGetValue(skinId, out string existing)) return existing;

            string renamedId = ComputeRenamedSkinId(senderSteamId, skinId);
            remaps[skinId] = renamedId;
            Plugin.Logger.LogInfo($"[DMSxMeadow] 🔀 Skin '{skinId}' de {senderSteamId} registrada bajo el id renombrado '{renamedId}' (H-5: no contamina el catálogo local).");
            return renamedId;
        }

        /// <summary>Ids (renombrados) de las skins guardadas en la caché del mod. Se leen del
        /// metadata.json de cada subcarpeta para ocultarlas del gallery de DMS (H-5).</summary>
        public static HashSet<string> GetCachedSheetIds()
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (!Directory.Exists(CacheSkinsPath)) return ids;

                foreach (string dir in Directory.GetDirectories(CacheSkinsPath))
                {
                    string jsonPath = Path.Combine(dir, "metadata.json");
                    if (!File.Exists(jsonPath)) continue;

                    try
                    {
                        string jsonText = File.ReadAllText(jsonPath);
                        Match match = Regex.Match(jsonText, "\"id\"\\s*:\\s*\"([^\"]+)\"");
                        if (match.Success && !string.IsNullOrEmpty(match.Groups[1].Value))
                        {
                            ids.Add(match.Groups[1].Value);
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Logger.LogWarning($"[DMSxMeadow] No se pudo leer el metadata de la carpeta de caché '{dir}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[DMSxMeadow] No se pudo listar la caché de skins: {ex.Message}");
            }
            return ids;
        }

        /// <summary>Borra de la memoria todo lo aportado por un emisor y su carpeta de caché en
        /// disco (renombrada, exclusiva de (emisor, skin) — ver H-5).</summary>
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
                    DeleteRenamedCacheFolder(senderSteamId, skinId);
                }
            }

            if (MemoryRemapBySender.Remove(senderSteamId))
            {
                Plugin.Logger.LogInfo($"[DMSxMeadow] 🧹 Remaps del jugador '{senderSteamId}' purgados (abandonó).");
            }

            return cleared;
        }

        /// <summary>Purga total al terminar la sesión (el HOST se fue o se salió del
        /// lobby): elimina TODAS las skins recibidas en memoria y sus carpetas
        /// renombradas en disco. Cubre el caso en que los clientes no reciben
        /// HandleDisconnect del host (lobby destruido — ver Plugin.cs).</summary>
        public static int ClearAllCachedSkins()
        {
            int cleared = 0;

            var senders = new List<string>();
            foreach (var key in MemorySkinCacheBySender.Keys) senders.Add(key);
            foreach (var key in MemoryRemapBySender.Keys)
            {
                if (!senders.Contains(key)) senders.Add(key);
            }

            foreach (string senderSteamId in senders)
            {
                if (MemorySkinCacheBySender.TryGetValue(senderSteamId, out var skinsBySender))
                {
                    cleared += skinsBySender.Count;
                    MemorySkinCacheBySender.Remove(senderSteamId);

                    foreach (string skinId in skinsBySender.Keys)
                    {
                        DeleteRenamedCacheFolder(senderSteamId, skinId);
                    }
                }

                if (MemoryRemapBySender.Remove(senderSteamId))
                {
                    Plugin.Logger.LogInfo($"[DMSxMeadow] 🧹 Remaps del jugador '{senderSteamId}' purgados (sesión terminada).");
                }
            }

            return cleared;
        }

        /// <summary>Borra la carpeta de caché en disco de la skin de un emisor. Gracias al
        /// renombrado H-5 cada carpeta es exclusiva de (emisor, skin original), así que se
        /// elimina sin comprobar referencias de otros jugadores.</summary>
        private static void DeleteRenamedCacheFolder(string senderSteamId, string skinId)
        {
            try
            {
                string targetFolder = Path.Combine(CacheSkinsPath, ComputeRenamedSkinId(senderSteamId, skinId));
                if (Directory.Exists(targetFolder))
                {
                    Directory.Delete(targetFolder, true);
                    Plugin.Logger.LogInfo($"[DMSxMeadow] 🧹 Carpeta de caché de la skin '{skinId}' (renombrada) eliminada al salir el emisor {senderSteamId}.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[DMSxMeadow] No se pudo borrar la carpeta de caché renombrada de '{skinId}' ({senderSteamId}): {ex.Message}");
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

        /// <summary>True si dmsPath es EXACTAMENTE la carpeta de caché del mod
        /// (mods/dmsxmeadow/dressmyslugcat): las skins cacheadas no deben resolverse como
        /// skins "instaladas" (H-5).</summary>
        private static bool IsCachePath(string dmsPath)
        {
            try
            {
                if (string.IsNullOrEmpty(dmsPath) || !Directory.Exists(CacheSkinsPath)) return false;
                string normalizedCache = Path.GetFullPath(CacheSkinsPath).TrimEnd('\\', '/');
                string normalizedCandidate = Path.GetFullPath(dmsPath).TrimEnd('\\', '/');
                return normalizedCandidate.Equals(normalizedCache, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
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
                if (Directory.Exists(directDmsPath) && !IsCachePath(directDmsPath))
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
                        if (Directory.Exists(dmsPath) && !IsCachePath(dmsPath))
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
            // Los packs reales agrupan las skins en categorías (p.ej. "Vanilla Scugs\standard",
            // "Misc Cosmetics\face variants\Thin Alt\alt angry eyes"), así que la carpeta de
            // skin con metadata.json puede estar a cualquier profundidad: se recorre recursivo.
            foreach (string skinDir in Directory.GetDirectories(dmsPath, "*", SearchOption.AllDirectories))
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

                // H-5: la skin aterriza en disco con id renombrado (dmsxm_{emisor}_{skinId})
                // y su metadata.json se reescribe con ese id: el catálogo local de DMS no se
                // contamina con ids de skins ajenas descargadas.
                bool remapAlreadyExists = GetActiveRemap(senderSteamId, skinId) != null;
                string renamedId = GetOrCreateRemap(senderSteamId, skinId);
                string targetFolder = Path.Combine(CacheSkinsPath, renamedId);
                string targetMetadata = Path.Combine(targetFolder, "metadata.json");

                if (Directory.Exists(targetFolder) && File.Exists(targetMetadata))
                {
                    Plugin.Logger.LogWarning($"[DMSxMeadow] ⚡ La skin '{skinId}' (renombrada '{renamedId}') ya existe en la caché local. Se omite la copia en disco.");

                    // La carpeta puede venir de una sesión anterior (remap recién creado en
                    // esta): el slug del emisor se recrea para aplicar la resolución
                    // original -> renombrado. Los atlas ya la tienen si arrancó con ella.
                    if (!remapAlreadyExists && !string.IsNullOrEmpty(senderSteamId))
                    {
                        Plugin.Logger.LogInfo($"[DMSxMeadow] 🔄 Remap nuevo sobre carpeta existente: recreando el slug de {senderSteamId} para aplicar '{renamedId}'.");
                        Plugin.ScheduleRecreateForSteamId(senderSteamId);
                    }
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

                    if (kvp.Key.Equals("metadata.json", StringComparison.OrdinalIgnoreCase))
                    {
                        File.WriteAllBytes(filePath, RewriteMetadataId(kvp.Value, renamedId));
                    }
                    else
                    {
                        File.WriteAllBytes(filePath, kvp.Value);
                    }
                }

                Plugin.Logger.LogInfo($"[DMSxMeadow] 💾 Skin '{skinId}' guardada por primera vez en caché (renombrada '{renamedId}'): {targetFolder}");
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

        /// <summary>Reescribe el campo "id" del metadata.json con el id renombrado (H-5)
        /// — AtlasHooks.cs:95-98 registra el SpriteSheet a partir de esa clave, así el id
        /// oficial del skin ajeno nunca entra al registro de DMS. El resto del JSON queda
        /// intacto (solo se sustituye la primera aparición de la clave "id").</summary>
        private static byte[] RewriteMetadataId(byte[] originalMetadata, string renamedId)
        {
            try
            {
                string jsonText = Encoding.UTF8.GetString(originalMetadata);
                Match match = Regex.Match(jsonText, "\"id\"\\s*:\\s*\"([^\"]*)\"");
                if (!match.Success) return originalMetadata;

                string newJsonText = jsonText.Substring(0, match.Index)
                    + "\"id\": \"" + renamedId + "\""
                    + jsonText.Substring(match.Index + match.Length);

                return Encoding.UTF8.GetBytes(newJsonText);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[DMSxMeadow] No se pudo reescribir el id del metadata.json a '{renamedId}': {ex.Message}");
                return originalMetadata;
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