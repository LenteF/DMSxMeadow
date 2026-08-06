using System;
using System.Collections.Generic;
using System.IO;
using DressMySlugcat.Hooks;
using UnityEngine;

namespace DMSxMeadow
{
    public static class SkinRegistration
    {
        private static string CacheSkinsPath // Para la release habrá que cambiar el path
        {
            get
            {
                string modsPath = Path.Combine(Application.dataPath, "StreamingAssets", "mods");
                string myModPath = Path.Combine(modsPath, "dmsxmeadow", "dressmyslugcat");

                return myModPath;
            }
        }

        public static bool SaveAndRegisterCacheSkin(string skinId, Dictionary<string, byte[]> files)
        {
            try
            {
                string targetFolder = Path.Combine(CacheSkinsPath, skinId);
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

                Debug.Log($"[CacheSkin] Skin '{skinId}' escrita con éxito en: {targetFolder}");

                AtlasHooks.ReloadAtlases();

                Debug.Log($"[CacheSkin] ReloadAtlases completado. Skin '{skinId}' lista para usarse.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CacheSkin] Error al crear la skin caché: {ex}");
                return false;
            }
        }
    }
}
