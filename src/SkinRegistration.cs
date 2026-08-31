using DressMySlugcat;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

namespace DMSxMeadow
{
    public static class SkinRegistration
    {
        public static readonly HashSet<string> NativeDmsSkins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "rainworld.default",
            "dressmyslugcat.empty",
            "dressmyslugcat.asymmetrytemplate",
            "dressmyslugcat.template",
            "dressmyslugcat.saintshirt"
        };

        private static readonly Regex ValidSkinIdentifierRegex =
            new Regex("^[a-zA-Z0-9_.-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static bool IsValidSkinIdentifier(string value)
        {
            return !string.IsNullOrEmpty(value) && ValidSkinIdentifierRegex.IsMatch(value);
        }

        // ===================================================================
        // CACHÉ EN MEMORIA POR EMISOR
        // ===================================================================
        private static readonly Dictionary<string, Dictionary<string, Dictionary<string, byte[]>>> MemorySkinCacheBySender =
            new Dictionary<string, Dictionary<string, Dictionary<string, byte[]>>>(StringComparer.Ordinal);

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

        public static bool HasSkinInMemory(string senderSteamId, string skinId)
        {
            if (string.IsNullOrEmpty(senderSteamId) || string.IsNullOrEmpty(skinId)) return false;

            return MemorySkinCacheBySender.TryGetValue(senderSteamId, out var skinsBySender)
                && skinsBySender.ContainsKey(skinId);
        }

        // ===================================================================
        // H-5: RENOMBRADO DEL ID
        // ===================================================================

        private const string DmsxmSkinPrefix = "dmsxm";

        private static readonly Dictionary<string, Dictionary<string, string>> MemoryRemapBySender =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        private static string SanitizeSteamId(string steamId)
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

        public static string ComputeRenamedSkinId(string senderSteamId, string skinId)
        {
            return $"{DmsxmSkinPrefix}_{SanitizeSteamId(senderSteamId)}_{skinId}";
        }

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
            Plugin.Logger.LogDebug($"🔀 Skin '{skinId}' de {senderSteamId} registrada bajo el id renombrado '{renamedId}' (H-5: no contamina el catálogo local).");
            return renamedId;
        }

        // ===================================================================
        // REGISTRO EN MEMORIA DEL SpriteSheet
        // ===================================================================

        private static readonly HashSet<string> RegisteredMemorySheets = new HashSet<string>(StringComparer.Ordinal);

        public static HashSet<string> GetCachedSheetIds()
        {
            return new HashSet<string>(RegisteredMemorySheets, StringComparer.Ordinal);
        }

        public static bool RegisterReceivedSkin(string senderSteamId, string skinId, Dictionary<string, byte[]> files)
        {
            if (string.IsNullOrEmpty(senderSteamId) || string.IsNullOrEmpty(skinId) || files == null) return false;

            try
            {
                string renamedId = ComputeRenamedSkinId(senderSteamId, skinId);

                if (RegisteredMemorySheets.Contains(renamedId))
                {
                    Plugin.Logger.LogDebug($"♻️ Skin '{skinId}' ya registrada en memoria (id '{renamedId}'). Sin re-registro.");
                    return true;
                }

                var sheet = BuildSpriteSheet(renamedId, files);
                if (sheet == null)
                {
                    Plugin.Logger.LogWarning($"⚠️ Skin '{skinId}' de {senderSteamId}: no se pudo construir el SpriteSheet en memoria (archivos incompletos). Skin por defecto (RNF-3).");
                    return false;
                }

                CacheSkinInMemory(senderSteamId, skinId, files);
                GetOrCreateRemap(senderSteamId, skinId);
                DressMySlugcat.Plugin.SpriteSheets.Add(sheet);
                RegisteredMemorySheets.Add(renamedId);

                Plugin.Logger.LogDebug($"🧠 Skin '{skinId}' de {senderSteamId} registrada EN MEMORIA como '{renamedId}' ({sheet.Atlases.Count} atlas, {sheet.Elements.Count + sheet.LeftElements.Count + sheet.RightElements.Count} elementos).");

                Plugin.ScheduleRecreateForSteamId(senderSteamId);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error al registrar la skin '{skinId}' en memoria: {ex}");
                return false;
            }
        }

        private static SpriteSheet BuildSpriteSheet(string renamedId, Dictionary<string, byte[]> files)
        {
            // --- metadata.json ---
            string sheetName = renamedId;
            string sheetAuthor = "DMSxMeadow";
            var defaultColors = new Dictionary<string, Color>();
            var defaultTail = new CustomTail();

            if (files.TryGetValue("metadata.json", out byte[] metadataBytes))
            {
                try
                {
                    ApplyMetadataToSheet(EncodingUtf8(metadataBytes), out sheetName, out sheetAuthor, defaultColors, defaultTail);
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning($"No se pudo parsear el metadata.json de '{renamedId}' ({ex.Message}). Se usa nombre/autor por defecto.");
                }
            }

            var sheet = new SpriteSheet
            {
                ID = renamedId,
                Name = sheetName,
                Author = sheetAuthor,
                Prefix = DressMySlugcat.Plugin.BaseName + "_" + renamedId + "_",
                DefaultColors = defaultColors,
                DefaultTail = defaultTail
            };

            // --- una parte por png con su .txt compañero ---
            foreach (var kvp in files)
            {
                if (!".png".Equals(Path.GetExtension(kvp.Key), StringComparison.OrdinalIgnoreCase)) continue;

                string txtName = Path.ChangeExtension(kvp.Key, ".txt");
                if (!files.TryGetValue(txtName, out byte[] txtBytes))
                {
                    Plugin.Logger.LogWarning($"Skin '{renamedId}': '{kvp.Key}' no tiene su .txt compañero. Se omite esa parte y se construye el resto (mirror AtlasHooks.cs:185-189).");
                    continue;
                }

                FAtlas atlas = BuildAtlasFromPngTxt(renamedId, kvp.Key, kvp.Value, txtBytes);
                if (atlas == null) return null;

                sheet.Atlases.Add(atlas);
            }

            if (sheet.Atlases.Count == 0)
            {
                Plugin.Logger.LogWarning($"Skin '{renamedId}': sin partes registrables (ningún png válido con su .txt). Rechazada.");
                return null;
            }

            try
            {
                sheet.ParseAtlases();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"Error al parsear los atlas de '{renamedId}': {ex.Message}. Rechazada (RNF-3).");
                UnloadAtlases(sheet.Atlases);
                return null;
            }

            return sheet;
        }

        private static MethodInfo _atlasManagerAddAtlasMethod;

        private static FieldInfo _nextAtlasIndexField;

        internal static int NextMemoryAtlasIndex()
        {
            if (_nextAtlasIndexField == null)
            {
                _nextAtlasIndexField = typeof(FAtlasManager).GetField("_nextAtlasIndex", BindingFlags.Static | BindingFlags.NonPublic);
                if (_nextAtlasIndexField == null)
                {
                    throw new Exception("FAtlasManager._nextAtlasIndex no encontrado (API del juego cambiada): no se puede asignar un índice global de atlas.");
                }
            }
            int current = (int)_nextAtlasIndexField.GetValue(null);
            int next = current + 1;
            _nextAtlasIndexField.SetValue(null, next);
            return current;
        }

        internal static void AddAtlasToManager(FAtlas atlas)
        {
            if (_atlasManagerAddAtlasMethod == null)
            {
                _atlasManagerAddAtlasMethod = typeof(FAtlasManager).GetMethod("AddAtlas", BindingFlags.Instance | BindingFlags.NonPublic);
                if (_atlasManagerAddAtlasMethod == null)
                {
                    throw new Exception("FAtlasManager.AddAtlas no encontrado (API del juego cambiada): no se puede registrar el atlas en memoria.");
                }
            }
            _atlasManagerAddAtlasMethod.Invoke(Futile.atlasManager, new object[] { atlas });
        }

        private static FAtlas BuildAtlasFromPngTxt(string renamedId, string pngName, byte[] pngBytes, byte[] txtBytes)
        {
            string atlasName = renamedId + "_" + Path.GetFileNameWithoutExtension(pngName);
            string prefix = DressMySlugcat.Plugin.BaseName + "_" + renamedId + "_";
            Texture2D texture = null;
            FAtlas atlas = null;

            try
            {
                if (Futile.atlasManager.DoesContainAtlas(atlasName))
                {
                    Futile.atlasManager.UnloadAtlas(atlasName);
                }

                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                texture.wrapMode = TextureWrapMode.Repeat;
                texture.anisoLevel = 0;
                texture.filterMode = FilterMode.Point;
                if (!texture.LoadImage(pngBytes))
                {
                    throw new Exception($"No se pudo decodificar el PNG '{pngName}'.");
                }
                Plugin.Logger.LogDebug($"📥 Parte '{pngName}' ({pngBytes.Length} B): textura {texture.width}x{texture.height} {texture.format}, filtro={texture.filterMode}, wrap={texture.wrapMode}, aniso={texture.anisoLevel}.");

                atlas = new FAtlas(atlasName, texture, NextMemoryAtlasIndex(), false);
                Plugin.Logger.LogDebug($"  FAtlas '{atlasName}' creado (index {atlas.index}, textura {texture.width}x{texture.height}).");

                atlas.elements.RemoveAt(0);

                string jsonText = EncodingUtf8(txtBytes);
                JObject json = JObject.Parse(jsonText);
                if (json["frames"] is not JObject framesObj)
                {
                    throw new Exception($"'{Path.GetFileName(Path.ChangeExtension(pngName, ".txt"))}' no es TexturePacker JSON (sin clave 'frames').");
                }

                float resourceScaleInverse = Futile.resourceScaleInverse;
                float texWidth = texture.width;
                float texHeight = texture.height;
                int indexInAtlas = 0;
                bool firstFrameLogged = false;
                Plugin.Logger.LogDebug($"  JSON OK: {framesObj.Count} frame(s) definidos en el .txt de '{pngName}'.");

                foreach (JProperty frameProp in framesObj.Properties())
                {
                    string elementName = frameProp.Name;
                    if (Futile.shouldRemoveAtlasElementFileExtensions)
                    {
                        elementName = Path.GetFileNameWithoutExtension(elementName);
                    }
                    elementName = prefix + elementName;

                    var frameData = (JObject)frameProp.Value;

                    if (frameData["rotated"] != null && (bool)frameData["rotated"])
                    {
                        throw new Exception($"Frame '{frameProp.Name}' marcado como rotated: Futile no lo soporta (ni en disco ni en memoria).");
                    }

                    bool trimmed = frameData["trimmed"] != null && (bool)frameData["trimmed"];

                    var frame = (JObject)frameData["frame"];
                    float x = frame["x"].Value<float>();
                    float y = frame["y"].Value<float>();
                    float w = frame["w"].Value<float>();
                    float h = frame["h"].Value<float>();

                    var sourceSize = (JObject)frameData["sourceSize"];
                    float srcW = sourceSize["w"].Value<float>();
                    float srcH = sourceSize["h"].Value<float>();

                    var spriteSourceSize = (JObject)frameData["spriteSourceSize"];
                    float sssX = spriteSourceSize["x"].Value<float>();
                    float sssY = spriteSourceSize["y"].Value<float>();
                    float sssW = spriteSourceSize["w"].Value<float>();
                    float sssH = spriteSourceSize["h"].Value<float>();

                    var element = new FAtlasElement
                    {
                        name = elementName,
                        indexInAtlas = indexInAtlas++,
                        atlas = atlas,
                        atlasIndex = atlas.index,
                        isTrimmed = trimmed
                    };

                    var uvRect = new Rect(x / texWidth, (texHeight - y - h) / texHeight, w / texWidth, h / texHeight);
                    element.uvRect = uvRect;
                    element.uvTopLeft.Set(uvRect.xMin, uvRect.yMax);
                    element.uvTopRight.Set(uvRect.xMax, uvRect.yMax);
                    element.uvBottomRight.Set(uvRect.xMax, uvRect.yMin);
                    element.uvBottomLeft.Set(uvRect.xMin, uvRect.yMin);
                    element.sourcePixelSize.x = srcW;
                    element.sourcePixelSize.y = srcH;
                    element.sourceSize.x = srcW * resourceScaleInverse;
                    element.sourceSize.y = srcH * resourceScaleInverse;
                    element.sourceRect = new Rect(sssX * resourceScaleInverse, sssY * resourceScaleInverse, sssW * resourceScaleInverse, sssH * resourceScaleInverse);

                    if (!firstFrameLogged)
                    {
                        firstFrameLogged = true;
                        Plugin.Logger.LogDebug($"  Primer frame '{elementName}': txt=({x},{y},{w},{h}) en textura {texWidth}x{texHeight} -> uv={uvRect} (origen TL {element.uvTopLeft}), trimmed={trimmed}, sourceSize={element.sourceSize}");
                    }

                    atlas.elements.Add(element);
                }

                if (indexInAtlas == 0)
                {
                    throw new Exception($"El .txt de '{pngName}' no define frames.");
                }

                AddAtlasToManager(atlas);
                Plugin.Logger.LogDebug($"  Atlas '{atlasName}' registrado en el manager con {indexInAtlas} frame(s).");

                return atlas;
            }
            catch (Exception ex)
            {
                string inner = ex.InnerException != null ? $" => {ex.InnerException.Message}" : "";
                Plugin.Logger.LogWarning($"⛔ Parte '{pngName}' rechazada: {ex.Message}{inner}");
                if (atlas != null)
                {
                    if (Futile.atlasManager.DoesContainAtlas(atlasName))
                    {
                        Futile.atlasManager.UnloadAtlas(atlasName);
                    }
                    else if (texture != null)
                    {
                        UnityEngine.Object.Destroy(texture);
                    }
                }
                else if (texture != null)
                {
                    UnityEngine.Object.Destroy(texture);
                }
                return null;
            }
        }

        private static void ApplyMetadataToSheet(string jsonText, out string name, out string author, Dictionary<string, Color> defaultColors, CustomTail defaultTail)
        {
            name = null;
            author = null;

            JObject root = JObject.Parse(jsonText);
            if (root["name"] != null) name = root["name"].ToString();
            if (root["author"] != null) author = root["author"].ToString();

            if (root["defaults"] is not JObject defaults) return;

            foreach (JProperty spriteProp in defaults.Properties())
            {
                string spriteKey = spriteProp.Name;
                var spriteDict = (JObject)spriteProp.Value;

                if (spriteKey.Equals("tail", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (JProperty defProp in spriteDict.Properties())
                    {
                        string defKey = defProp.Name.ToLowerInvariant();
                        switch (defKey)
                        {
                            case "color":
                                if (ColorUtility.TryParseHtmlString(defProp.Value.ToString(), out var tailColor))
                                {
                                    defaultTail.Color = tailColor;
                                }
                                break;
                            case "length":
                                defaultTail.Length = (float)defProp.Value;
                                break;
                            case "wideness":
                                defaultTail.Wideness = (float)defProp.Value;
                                break;
                            case "roundness":
                                defaultTail.Roundness = (float)defProp.Value;
                                break;
                            case "lift":
                                defaultTail.Lift = (float)defProp.Value;
                                break;
                        }
                    }
                }
                else
                {
                    foreach (JProperty defProp in spriteDict.Properties())
                    {
                        if (defProp.Name.Equals("color", StringComparison.OrdinalIgnoreCase)
                            && ColorUtility.TryParseHtmlString(defProp.Value.ToString(), out var color))
                        {
                            defaultColors[spriteKey.ToUpperInvariant()] = color;
                        }
                    }
                }
            }
        }

        private static string EncodingUtf8(byte[] bytes)
        {
            return System.Text.Encoding.UTF8.GetString(bytes, 0, bytes.Length);
        }

        public static int WipeAllMemory()
        {
            int released = 0;

            var registeredIds = new List<string>(RegisteredMemorySheets);
            foreach (string renamedId in registeredIds)
            {
                var sheet = DressMySlugcat.Plugin.SpriteSheets.FirstOrDefault(s => s != null && s.ID == renamedId);
                if (sheet != null)
                {
                    UnloadAtlases(sheet.Atlases);
                    DressMySlugcat.Plugin.SpriteSheets.Remove(sheet);
                }
                RegisteredMemorySheets.Remove(renamedId);
                released++;
            }

            int cachedEntries = 0;
            foreach (var bySender in MemorySkinCacheBySender.Values)
            {
                cachedEntries += bySender.Count;
            }
            MemorySkinCacheBySender.Clear();
            MemoryRemapBySender.Clear();

            if (released > 0 || cachedEntries > 0)
            {
                Plugin.Logger.LogDebug($"🧹 Wipe total (MainMenu): {released} hoja(s) de memoria descargadas y {cachedEntries} entrada(s) de caché purgadas.");
            }
            return released + cachedEntries;
        }

        public static int ForgetSender(string senderSteamId)
        {
            if (string.IsNullOrEmpty(senderSteamId)) return 0;

            int released = 0;
            string prefix = $"{DmsxmSkinPrefix}_{SanitizeSteamId(senderSteamId)}_";

            var sheetsToRemove = RegisteredMemorySheets
                .Where(id => id.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();
            foreach (string renamedId in sheetsToRemove)
            {
                var sheet = DressMySlugcat.Plugin.SpriteSheets.FirstOrDefault(s => s != null && s.ID == renamedId);
                if (sheet != null)
                {
                    UnloadAtlases(sheet.Atlases);
                    DressMySlugcat.Plugin.SpriteSheets.Remove(sheet);
                }
                RegisteredMemorySheets.Remove(renamedId);
                released++;
            }

            int cached = 0;
            if (MemorySkinCacheBySender.TryGetValue(senderSteamId, out var skinsBySender))
            {
                cached = skinsBySender.Count;
                MemorySkinCacheBySender.Remove(senderSteamId);
            }
            bool remapRemoved = MemoryRemapBySender.Remove(senderSteamId);

            if (released > 0 || cached > 0 || remapRemoved)
            {
                Plugin.Logger.LogDebug($"🧹 Estado en memoria de {senderSteamId} purgado (ban de skins): {released} hoja(s) descargada(s), {cached} entrada(s) de caché, remaps {(remapRemoved ? "eliminados" : "sin tocar")}.");
            }

            return released + cached;
        }

        private static void UnloadAtlases(List<FAtlas> atlases)
        {
            foreach (FAtlas atlas in atlases)
            {
                if (atlas == null) continue;
                try
                {
                    if (Futile.atlasManager.DoesContainAtlas(atlas.name))
                    {
                        Futile.atlasManager.UnloadAtlas(atlas.name);
                    }
                    else if (atlas.texture != null)
                    {
                        UnityEngine.Object.Destroy(atlas.texture);
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning($"No se pudo descargar el atlas '{atlas.name}': {ex.Message}");
                }
            }
        }

        // ===================================================================
        // LADO EMISOR
        // ===================================================================

        public static Dictionary<string, byte[]> ExportEquippedSkinToDTO(string skinId)
        {
            var files = new Dictionary<string, byte[]>();

            string skinFolder = FindSkinDirectoryOnDisk(skinId);
            if (string.IsNullOrEmpty(skinFolder) || !Directory.Exists(skinFolder))
            {
                Plugin.Logger.LogError($"❌ No se encontró la carpeta física de la skin '{skinId}' en los mods.");
                return files;
            }

            var foundFiles = Directory.GetFiles(skinFolder, "*.*", SearchOption.AllDirectories);
            var pngPaths = new List<string>();

            foreach (string filePath in foundFiles)
            {
                string relativePath = filePath.Substring(skinFolder.Length).TrimStart('\\', '/');
                string fileName = Path.GetFileName(filePath);

                if (!".png".Equals(Path.GetExtension(fileName), StringComparison.OrdinalIgnoreCase)) continue;
                if (IsNonPartFile(fileName)) continue;

                pngPaths.Add(relativePath);
            }

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
                    Plugin.Logger.LogWarning($"Skin '{skinId}': '{pngRelative}' no tiene su .txt compañero. Se transfiere igual (el receptor omitirá esa parte, mirror AtlasHooks.cs:185-189).");
                }
            }

            string metadataRelative = "metadata.json";
            string metadataFull = Path.Combine(skinFolder, metadataRelative);
            if (File.Exists(metadataFull))
            {
                files[metadataRelative] = File.ReadAllBytes(metadataFull);
            }

            Plugin.Logger.LogDebug($"📦 Skin '{skinId}' empaquetada con éxito desde disco ({files.Count} archivos: {pngPaths.Count} partes + txts + metadata).");
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
                Plugin.Logger.LogWarning($"No se pudo leer ModManager.InstalledMods: {ex.Message}");
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
                Plugin.Logger.LogWarning($"No se pudo resolver la ruta directa de Workshop: {ex.Message}");
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

            Plugin.Logger.LogError($"❌ No se encontró la carpeta física para la skin '{skinId}' ni en local ni en Workshop.");
            return null;
        }

        private static string CheckDmsDirectoryForSkin(string dmsPath, string skinId)
        {
            foreach (string skinDir in Directory.GetDirectories(dmsPath, "*", SearchOption.AllDirectories))
            {
                string jsonPath = Path.Combine(skinDir, "metadata.json");
                if (!File.Exists(jsonPath)) continue;

                try
                {
                    string jsonText = File.ReadAllText(jsonPath);

                    foreach (string line in jsonText.Split('\n'))
                    {
                        if (line.Contains("\"id\""))
                        {
                            string[] parts = line.Split(':');
                            if (parts.Length >= 2)
                            {
                                string extractedId = parts[1].Trim('"', ' ', ',', '\r', '\n', '\t');

                                if (string.Equals(extractedId, skinId, StringComparison.Ordinal))
                                {
                                    Plugin.Logger.LogDebug($"🎯 Skin '{skinId}' encontrada con éxito en: {skinDir}");
                                    return skinDir;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogDebug($"Error al leer metadata.json en {skinDir}: {ex.Message}");
                }
            }
            return null;
        }
    }
}