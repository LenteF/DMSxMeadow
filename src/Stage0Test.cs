using System;
using System.Collections.Generic;
using UnityEngine;

namespace DMSxMeadow
{
    public class Stage0Test : MonoBehaviour
    {
        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F5))
            {
                RunFullStage0Test();
            }
        }

        private void RunFullStage0Test()
        {
            try
            {
                Plugin.Logger.LogInfo("[DMSxMeadow] === INICIANDO PRUEBA COMPLETA DE ETAPA 0 (F5) ===");

                Player localPlayer = GetLocalPlayer();
                if (localPlayer == null) 
                {
                    Plugin.Logger.LogWarning("[DMSxMeadow] ⚠️ Debes estar dentro de una partida activa para ejecutar la prueba.");
                    return;
                }

                HashSet<string> equippedSkinIds = SkinRegistration.GetEquippedSkinIds(localPlayer);

                if (equippedSkinIds.Count == 0)
                {
                    Plugin.Logger.LogWarning("[DMSxMeadow] ⚠️ El jugador local no tiene ninguna skin custom de DMS equipada (usa la apariencia vanilla).");
                    return;
                }

                foreach (string skinId in equippedSkinIds)
                {
                    Plugin.Logger.LogInfo($"[DMSxMeadow] 🔍 Skin detectada en el jugador local: '{skinId}'");
                    Dictionary<string, byte[]> dtoFiles = SkinRegistration.ExportEquippedSkinToDTO(skinId);

                    if (dtoFiles.Count == 0)
                    {
                        Plugin.Logger.LogError($"[DMSxMeadow] ❌ No se pudieron leer los archivos para la skin '{skinId}'.");
                        continue;
                    }

                    bool success = SkinRegistration.SaveAndRegisterCacheSkin(skinId, dtoFiles);

                    if (success)
                    {
                        Plugin.Logger.LogInfo($"[DMSxMeadow] 🎉 ¡ÉXITO! La skin '{skinId}' ha sido leída.");
                    }
                }

                Plugin.Logger.LogInfo("[DMSxMeadow] === PRUEBA FINALIZADA CON ÉXITO ===");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[DMSxMeadow] ❌ Error en el test de la Etapa 0: {ex}");
            }
        }

        private Player GetLocalPlayer()
        {
            var rainWorldGame = RWCustom.Custom.rainWorld.processManager.currentMainLoop as RainWorldGame;
            if (rainWorldGame?.Players == null)
            {
                return null;
            }

            foreach (var abstractPlayer in rainWorldGame.Players)
            {
                if (abstractPlayer?.realizedCreature is Player player)
                {
                    return player;
                }
            }

            return null;
        }
    }
}
