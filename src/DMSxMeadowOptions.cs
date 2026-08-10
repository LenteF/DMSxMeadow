using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Menu;
using Menu.Remix.MixedUI;
using UnityEngine;

namespace DMSxMeadow
{
    public class DMSxMeadowOptions : OptionInterface
    {
        public static DMSxMeadowOptions Instance = new DMSxMeadowOptions();

        private OpScrollBox _scrollBox;
        private OpTextBox _searchBox;
        private OpSimpleButton _modeButton;
        private bool _searchBySteamId = true;
        private List<ProfileRow> _currentRows = new List<ProfileRow>();

        private const string ONLINE_TAB_NAME = "Online";
        private const string LOCAL_TAB_NAME = "Local";

        private OpTab _onlineTab;
        private OpTab _profilesTab;

        private Configurable<string> _searchConfig;

        // ============================================================
        // FLAG DE CONSENTIMIENTO (RF-7 / Capa 0) — default OFF
        // ============================================================
        private static Configurable<bool> _shareSkinConfig;

        /// <summary>Estado global en memoria del flag de compartir. Se carga UNA vez al
        /// arranque desde el archivo ModConfigs/dmsxmeadow.txt y se actualiza en vivo cuando
        /// el usuario togglea el checkbox del OI (cuyo valor es el que Remix persistirá al
        /// cerrar el menú). Así no hay I/O de disco por petición y no existe la ventana de
        /// arranque donde el bind de Remix devolvía el default.</summary>
        private static bool _shareSkinMemory;
        private static bool _shareSkinMemoryInitialized;

        public static bool ShareSkinEnabled
        {
            get
            {
                if (!_shareSkinMemoryInitialized)
                {
                    _shareSkinMemory = ReadShareSkinFromFile();
                    _shareSkinMemoryInitialized = true;
                }
                return _shareSkinMemory;
            }
        }

        private static bool ReadShareSkinFromFile()
        {
            try
            {
                string path = Path.Combine(Application.persistentDataPath, "ModConfigs", "dmsxmeadow.txt");
                if (!File.Exists(path)) return false;

                foreach (string rawLine in File.ReadAllLines(path))
                {
                    string line = rawLine.Trim();
                    if (line.StartsWith("shareSkin", StringComparison.OrdinalIgnoreCase) && line.Contains("="))
                    {
                        string value = line.Substring(line.IndexOf('=') + 1).Trim();
                        if (bool.TryParse(value, out bool parsed))
                        {
                            return parsed;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[DMSxMeadow] Error leyendo el flag de compartir del archivo de config: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Bindeo temprano del flag para el checkbox del OI (defensa en profundidad). El
        /// valor QUE MANDÁ en el comportamiento es siempre el del archivo en disco
        /// (ShareSkinEnabled); el bind solo informa al checkbox, que Remix persiste al
        /// cerrar el menú. Se llama desde OnModsInit.
        /// </summary>
        public void EnsureConfigBound()
        {
            try
            {
                if (_shareSkinConfig == null && this.config != null)
                {
                    _shareSkinConfig = this.config.Bind<bool>("shareSkin", false, new ConfigurableInfo(
                        "Allow other players to request the skins you have equipped. OFF by default: your skins never leave your machine."));
                }

                // Carga inicial del estado en memoria desde el archivo (una sola vez).
                if (!_shareSkinMemoryInitialized)
                {
                    _shareSkinMemory = ReadShareSkinFromFile();
                    _shareSkinMemoryInitialized = true;
                }

                Plugin.Logger.LogInfo($"[DMSxMeadow] Flag de compartir skin (fuente: ModConfigs/dmsxmeadow.txt): {_shareSkinMemory}");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[DMSxMeadow] No se pudo bindear temprano el flag de compartir: {ex.Message}");
            }
        }

        // ============================================================
        // CACHE PARA EL MÉTODO Unload (Reflection)
        // ============================================================
        private static MethodInfo _unloadMethod;

        private class ProfileRow
        {
            public int ProfileNumber;
            public string SteamId;
            public OpLabel Label;
            public OpSimpleButton DeleteButton;
        }

        public override void Initialize()
        {
            base.Initialize();

            // ============================================================
            // TABS: "Online" (first, shown by default) and "Local"
            // ============================================================
            _onlineTab = new OpTab(this, ONLINE_TAB_NAME);
            _profilesTab = new OpTab(this, LOCAL_TAB_NAME);
            Tabs = new[] { _onlineTab, _profilesTab };

            // Shared layout anchors for both tabs (visual consistency)
            float row1Y = 515f;
            float searchRowOffsetX = -21f;
            float titleY = row1Y + 55f;

            if (_shareSkinConfig == null)
            {
                _shareSkinConfig = this.config.Bind<bool>("shareSkin", false, new ConfigurableInfo(
                    "Allow other players to request the skins you have equipped. OFF by default: your skins never leave your machine."));
            }
            var onlineTitle = new OpLabel(
                new Vector2(300f, titleY),
                new Vector2(),
                "ONLINE OPTIONS",
                FLabelAlignment.Center,
                false
            );
            _onlineTab.AddItems(onlineTitle);

            var shareCheckbox = new OpCheckBox(_shareSkinConfig, new Vector2(20f, 440f));
            // El checkbox togglea en vivo el estado en memoria: ese valor es exactamente el
            // que Remix persistirá al archivo al cerrar el menú, así que no hace falta
            // releer el disco (que aún tendría el valor viejo hasta el guardado).
            shareCheckbox.OnValueChanged += (_, _, newValue) =>
            {
                if (bool.TryParse(newValue, out bool parsed))
                {
                    _shareSkinMemory = parsed;
                }
            };
            _onlineTab.AddItems(shareCheckbox);

            var shareDescription = new OpLabel(
                new Vector2(52f, 415f),
                new Vector2(520f, 60f),
                "Allow other players to see your skin",
                FLabelAlignment.Left,
                false
            );
            shareDescription.color = new Color(0.65f, 0.65f, 0.65f);
            _onlineTab.AddItems(shareDescription);

            // ============================================================
            // LOCAL TAB — LOCAL PROFILE MANAGEMENT
            // ============================================================
            if (_searchConfig == null)
            {
                _searchConfig = this.config.Bind<string>("searchQuery", "", new ConfigurableInfo("Search query"));
            }

            // SECTION TITLE
            var titleLabel = new OpLabel(
                new Vector2(20f + searchRowOffsetX + 124f, titleY),
                new Vector2(350f, 20f),
                "PROFILE MANAGER",
                FLabelAlignment.Center,
                false
            );
            _profilesTab.AddItems(titleLabel);

            // TOP ROW: SEARCH FIELD + MODE
            _searchBox = new OpTextBox(_searchConfig, new Vector2(20f + searchRowOffsetX, row1Y), 200f);
            _searchBox.OnValueChanged += (sender, oldV, newV) => RefreshList();
            _profilesTab.AddItems(_searchBox);

            _modeButton = new OpSimpleButton(
                new Vector2(230f + searchRowOffsetX, row1Y),
                new Vector2(140f, 24f),
                _searchBySteamId ? "Player ID" : "Profile #"
            );
            _modeButton.OnClick += (_) =>
            {
                _searchBySteamId = !_searchBySteamId;
                _modeButton.text = _searchBySteamId ? "Player ID" : "Profile #";
                RefreshList();
            };
            _profilesTab.AddItems(_modeButton);

            // SCROLL BOX DE PERFILES
            float scrollY = 30f;
            float scrollHeight = 460f;
            float contentHeight = 100f;

            _scrollBox = new OpScrollBox(
                new Vector2(0f, scrollY),
                new Vector2(600f, scrollHeight),
                contentHeight,
                false,
                true,
                true
            )
            {
                colorEdge = MenuColorEffect.rgbMediumGrey,
                colorFill = MenuColorEffect.rgbBlack,
                fillAlpha = 0.3f
            };
            _profilesTab.AddItems(_scrollBox);

            if (_unloadMethod == null)
            {
                _unloadMethod = typeof(UIelement).GetMethod("Unload",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }

            // LIMPIEZA AUTOMÁTICA DE HUÉRFANOS
            MeadowProfileManager.DeleteOrphanProfiles();
            RefreshList();
        }

        // ============================================================
        // HELPER: UNLOAD ELEMENT (elimina gráficos de pantalla)
        // ============================================================
        private void UnloadElement(UIelement element)
        {
            if (element == null) return;
            try
            {
                _unloadMethod?.Invoke(element, null);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error unloading element: {ex.Message}");
            }
        }

        private void RefreshList()
        {
            try
            {
                // ============================================================
                // 1. ELIMINAR FILAS ACTUALES
                // ============================================================
                foreach (var row in _currentRows)
                {
                    OpScrollBox.RemoveItemsFromScrollBox(row.Label, row.DeleteButton);
                    UnloadElement(row.Label);
                    UnloadElement(row.DeleteButton);
                }
                _currentRows.Clear();

                // ============================================================
                // 2. OBTENER TODOS LOS PERFILES
                // ============================================================
                var allProfiles = MeadowProfileManager.GetAllProfileNumbers();

                string query = _searchBox.value?.Trim() ?? "";
                bool hasQuery = !string.IsNullOrEmpty(query);

                const float ROW_HEIGHT = 26f;
                const float VISIBLE_HEIGHT = 460f;

                // ============================================================
                // 3. FILTRAR Y CONTAR
                // ============================================================
                var matchingProfiles = new List<(int num, string steamId, bool orphan)>();

                foreach (int p in allProfiles)
                {
                    string sid = MeadowProfileManager.GetSteamID(p);
                    bool isOrphan = string.IsNullOrEmpty(sid);

                    bool matches = !hasQuery || (_searchBySteamId
                        ? sid.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                        : p.ToString().Contains(query));

                    if (matches) matchingProfiles.Add((p, sid, isOrphan));
                }

                // ============================================================
                // 4. ORDENAR DE MENOR A MAYOR
                // ============================================================
                matchingProfiles = matchingProfiles.OrderBy(x => x.num).ToList();

                // ============================================================
                // 5. CALCULAR ALTURA TOTAL DEL CONTENIDO
                // ============================================================
                float contentHeight = Math.Max(matchingProfiles.Count * ROW_HEIGHT + 20f, VISIBLE_HEIGHT);

                // ============================================================
                // 6. COLOCAR FILAS
                // ============================================================
                float y = contentHeight - ROW_HEIGHT - 10f;

                foreach (var (profileNum, steamId, isOrphan) in matchingProfiles)
                {
                    string display = isOrphan
                        ? $"Profile {profileNum}  [orphan]"
                        : $"Profile {profileNum}  {steamId}";

                    var label = new OpLabel(10f, y, display, false);
                    if (isOrphan)
                    {
                        label.color = Color.gray;
                    }

                    var deleteBtn = new OpSimpleButton(
                        new Vector2(350f, y - 3f),
                        new Vector2(70f, 22f),
                        "Delete"
                    );

                    int capturedNum = profileNum;
                    deleteBtn.OnClick += (_) => DeleteProfile(capturedNum);

                    _scrollBox.AddItems(label, deleteBtn);
                    _currentRows.Add(new ProfileRow
                    {
                        ProfileNumber = profileNum,
                        SteamId = steamId,
                        Label = label,
                        DeleteButton = deleteBtn
                    });

                    y -= ROW_HEIGHT;
                }

                // ============================================================
                // 7. ACTUALIZAR SCROLL
                // ============================================================
                _scrollBox.SetContentSize(contentHeight, true);
                _scrollBox.MarkDirty();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error refreshing profile list: {ex.Message}");
                Plugin.Logger.LogError(ex.StackTrace);
            }
        }

        private void DeleteProfile(int profileNumber)
        {
            try
            {
                MeadowProfileManager.DeleteProfile(profileNumber);
                MeadowProfileManager.RemoveAssignment(profileNumber);
                RefreshList();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error deleting profile {profileNumber}: {ex.Message}");
            }
        }
    }
}
