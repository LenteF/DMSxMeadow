using System;
using System.Collections.Generic;
using System.Reflection;
using Menu;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace DMSxMeadow
{
    public static class FancyMenuHookHandler
    {
        private static Hook updateHook;
        private static Hook signalHook;
        private static Hook getSelectedHook;
        private static Hook setSelectedHook;
        private static Hook shutdownHook;
        private static Hook customizationFor3ArgHook;
        private static Dictionary<DressMySlugcat.FancyMenu, MeadowProfileUI> _uiInstances = new Dictionary<DressMySlugcat.FancyMenu, MeadowProfileUI>();

        private static DressMySlugcat.FancyMenu _currentFancyMenu;
        private static Dictionary<string, DressMySlugcat.Customization> _liveMeadowCustomizations = new Dictionary<string, DressMySlugcat.Customization>();
        private static DressMySlugcat.Customization _copiedMeadowCustomization;
        private static System.Reflection.FieldInfo _dmsCopiedCustomizationField;

        public static void Initialize()
        {
            try
            {
                Type fancyMenuType = typeof(DressMySlugcat.FancyMenu);

                MethodInfo updateMethod = typeof(ProcessManager).GetMethod("Update");
                if (updateMethod != null)
                {
                    MethodInfo hookMethod = typeof(FancyMenuHookHandler)
                        .GetMethod("Update_Hook",
                            BindingFlags.NonPublic | BindingFlags.Static);

                    if (hookMethod != null)
                    {
                        updateHook = new Hook(updateMethod, hookMethod);
                    }
                }

                MethodInfo signalMethod = fancyMenuType.GetMethod("Singal");
                if (signalMethod != null)
                {
                    MethodInfo hookSignal = typeof(FancyMenuHookHandler)
                        .GetMethod("Singal_Hook",
                            BindingFlags.NonPublic | BindingFlags.Static);

                    if (hookSignal != null)
                    {
                        signalHook = new Hook(signalMethod, hookSignal);
                    }
                }

                MethodInfo getSelMethod = fancyMenuType.GetMethod("GetCurrentlySelectedOfSeries");
                if (getSelMethod != null)
                {
                    MethodInfo hookGetSel = typeof(FancyMenuHookHandler)
                        .GetMethod("GetSelected_Hook",
                            BindingFlags.NonPublic | BindingFlags.Static);

                    if (hookGetSel != null)
                    {
                        getSelectedHook = new Hook(getSelMethod, hookGetSel);
                    }
                }

                MethodInfo setSelMethod = fancyMenuType.GetMethod("SetCurrentlySelectedOfSeries");
                if (setSelMethod != null)
                {
                    MethodInfo hookSetSel = typeof(FancyMenuHookHandler)
                        .GetMethod("SetSelected_Hook",
                            BindingFlags.NonPublic | BindingFlags.Static);

                    if (hookSetSel != null)
                    {
                        setSelectedHook = new Hook(setSelMethod, hookSetSel);
                    }
                }

                MethodInfo shutdownMethod = fancyMenuType.GetMethod("ShutDownProcess");
                if (shutdownMethod != null)
                {
                    MethodInfo hookShutdown = typeof(FancyMenuHookHandler)
                        .GetMethod("ShutDownProcess_Hook",
                            BindingFlags.NonPublic | BindingFlags.Static);

                    if (hookShutdown != null)
                    {
                        shutdownHook = new Hook(shutdownMethod, hookShutdown);
                    }
                }

                MethodInfo for3Arg = typeof(DressMySlugcat.Customization)
                    .GetMethod("For", new Type[] { typeof(string), typeof(int), typeof(bool) });

                if (for3Arg != null)
                {
                    MethodInfo hookFor3Arg = typeof(FancyMenuHookHandler)
                        .GetMethod("Customization_For3Arg_Hook",
                            BindingFlags.NonPublic | BindingFlags.Static);

                    if (hookFor3Arg != null)
                    {
                        customizationFor3ArgHook = new Hook(for3Arg, hookFor3Arg);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error initializing FancyMenu hooks: {ex.Message}");
            }
        }

        private static DressMySlugcat.Customization Customization_For3Arg_Hook(
            Func<string, int, bool, DressMySlugcat.Customization> orig,
            string slugcatName,
            int playerNumber,
            bool mergeDefaults)
        {
            if (MeadowProfileManager.IsMeadowModeActive
                && _currentFancyMenu != null
                && slugcatName == _currentFancyMenu.selectedSlugcat
                && playerNumber == _currentFancyMenu.selectedPlayerIndex)
            {
                return GetLiveMeadowCustomization(slugcatName, playerNumber);
            }

            return orig(slugcatName, playerNumber, mergeDefaults);
        }

        private static DressMySlugcat.Customization GetLiveMeadowCustomization(string slugcatName, int playerNumber)
        {
            if (!_liveMeadowCustomizations.TryGetValue(slugcatName, out var live))
            {
                live = new DressMySlugcat.Customization
                {
                    Slugcat = slugcatName,
                    PlayerNumber = playerNumber
                };
                _liveMeadowCustomizations[slugcatName] = live;
            }

            if (live.Slugcat != slugcatName)
                live.Slugcat = slugcatName;
            if (live.PlayerNumber != playerNumber)
                live.PlayerNumber = playerNumber;

            return live;
        }

        private static void Update_Hook(
            Action<ProcessManager, float> orig,
            ProcessManager self,
            float deltaTime)
        {
            orig(self, deltaTime);

            try
            {
                if (self.currentMainLoop is DressMySlugcat.FancyMenu fancyMenu)
                {
                    _currentFancyMenu = fancyMenu;
                    if (!_uiInstances.ContainsKey(fancyMenu))
                    {
                        var ui = new MeadowProfileUI(fancyMenu);
                        ui.Initialize();
                        _uiInstances[fancyMenu] = ui;
                    }
                    else
                    {
                        if (_uiInstances.TryGetValue(fancyMenu, out var ui))
                        {
                            ui.CheckFieldFocusLoss();
                            ui.CheckSlugcatChange();
                            ui.CheckPasteInput();
                        }
                    }
                }

                if (self.dialog is DressMySlugcat.FancyMenu fancyMenuDialog)
                {
                    _currentFancyMenu = fancyMenuDialog;
                    if (!_uiInstances.ContainsKey(fancyMenuDialog))
                    {
                        var ui = new MeadowProfileUI(fancyMenuDialog);
                        ui.Initialize();
                        _uiInstances[fancyMenuDialog] = ui;
                    }
                    else
                    {
                        if (_uiInstances.TryGetValue(fancyMenuDialog, out var ui))
                        {
                            ui.CheckFieldFocusLoss();
                            ui.CheckSlugcatChange();
                            ui.CheckPasteInput();
                        }
                    }
                }

                foreach (var process in self.sideProcesses)
                {
                    if (process is DressMySlugcat.FancyMenu fancyMenuSide)
                    {
                        _currentFancyMenu = fancyMenuSide;
                        if (!_uiInstances.ContainsKey(fancyMenuSide))
                        {
                            var ui = new MeadowProfileUI(fancyMenuSide);
                            ui.Initialize();
                            _uiInstances[fancyMenuSide] = ui;
                        }
                        else
                        {
                            if (_uiInstances.TryGetValue(fancyMenuSide, out var ui))
                            {
                                ui.CheckFieldFocusLoss();
                                ui.CheckSlugcatChange();
                                ui.CheckPasteInput();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error in Update hook: {ex.Message}");
            }
        }

        private static int GetSelected_Hook(
            Func<DressMySlugcat.FancyMenu, string, int> orig,
            DressMySlugcat.FancyMenu self,
            string series)
        {
            if (series == "MEADOW_SERIES")
            {
                return MeadowProfileManager.IsMeadowModeActive ? 0 : -1;
            }

            if (MeadowProfileManager.IsMeadowModeActive && series.StartsWith("PLAYER_"))
            {
                return -1;
            }

            return orig(self, series);
        }

        private static void SetSelected_Hook(
            Action<DressMySlugcat.FancyMenu, string, int> orig,
            DressMySlugcat.FancyMenu self,
            string series,
            int to)
        {
            if (series == "MEADOW_SERIES")
            {
                if (_uiInstances.TryGetValue(self, out var ui))
                {
                    ui.ToggleMeadowMode();
                }
                return;
            }

            if (series.StartsWith("PLAYER_") && MeadowProfileManager.IsMeadowModeActive)
            {
                if (_uiInstances.TryGetValue(self, out var ui))
                {
                    ui.DeactivateMeadowMode();
                }
            }

            orig(self, series, to);
        }

        private static void ShutDownProcess_Hook(
            Action<DressMySlugcat.FancyMenu> orig,
            DressMySlugcat.FancyMenu self)
        {
            try
            {
                if (_uiInstances.TryGetValue(self, out var ui))
                {
                    if (MeadowProfileManager.IsMeadowModeActive)
                    {
                        ui.ForceDeactivateMeadowMode();
                    }

                    _uiInstances.Remove(self);
                    _currentFancyMenu = null;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error in ShutDownProcess hook: {ex.Message}");
            }

            orig(self);
        }

        private static void Singal_Hook(
            Action<DressMySlugcat.FancyMenu, MenuObject, string> orig,
            DressMySlugcat.FancyMenu fancyMenu,
            MenuObject sender,
            string message)
        {
            if (message == "MEADOW_TOGGLE" || message == "PROFILE_SET")
            {
                try
                {
                    if (_uiInstances.TryGetValue(fancyMenu, out var ui))
                    {
                        ui.HandleSignal(message);
                    }
                    return;
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"Error handling meadow signal: {ex.Message}");
                }
            }

            if (!MeadowProfileManager.IsMeadowModeActive && message == "CUST_COPY")
            {
                try
                {
                    orig(fancyMenu, sender, message);
                    _copiedMeadowCustomization = GetDmsCopiedCustomization(fancyMenu)?.Copy();
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"Error syncing meadow copy buffer: {ex.Message}");
                }
                return;
            }

            if (MeadowProfileManager.IsMeadowModeActive &&
                (message == "CUST_COPY" || message == "CUST_PASTE" || message == "CUST_DEFAULTS"))
            {
                try
                {
                    HandleMeadowCopyPasteDefaults(fancyMenu, message);
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"Error handling meadow copy/paste/defaults: {ex.Message}");
                }
                return;
            }

            if (MeadowProfileManager.IsMeadowModeActive &&
                (message.StartsWith("SPRITE_SELECTOR_") ||
                 message.StartsWith("SPRITE_CUSTOMIZER_") ||
                 message == "TAIL_CUSTOMIZER" ||
                 message == "CUST_PASTE" ||
                 message == "CUST_DEFAULTS"))
            {
                try
                {
                    if (_uiInstances.TryGetValue(fancyMenu, out var ui))
                    {
                        ui.SaveCurrentProfile();
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"Error auto-saving: {ex.Message}");
                }
            }

            orig(fancyMenu, sender, message);
        }

        private static void HandleMeadowCopyPasteDefaults(DressMySlugcat.FancyMenu fancyMenu, string message)
        {
            string slugcat = fancyMenu.selectedSlugcat;
            int playerNumber = fancyMenu.selectedPlayerIndex;
            var live = GetLiveMeadowCustomization(slugcat, playerNumber);

            if (message == "CUST_COPY")
            {
                _copiedMeadowCustomization = live.Copy();
                SetDmsCopiedCustomization(fancyMenu, _copiedMeadowCustomization.Copy());
                fancyMenu.pasteButton.inactive = false;
                fancyMenu.PlaySound(SoundID.MENY_Already_Selected_MultipleChoice_Clicked);
                return;
            }

            if (message == "CUST_PASTE")
            {
                var source = _copiedMeadowCustomization ?? GetDmsCopiedCustomization(fancyMenu);
                if (source == null) return;

                bool keepTargetTailColor = source.CustomTail.Color == DressMySlugcat.Utils.DefaultColorForSprite(source.Slugcat, "TAIL");

                live.CustomTail.Length = source.CustomTail.Length;
                live.CustomTail.Wideness = source.CustomTail.Wideness;
                live.CustomTail.Roundness = source.CustomTail.Roundness;
                live.CustomTail.Lift = source.CustomTail.Lift;
                live.CustomTail.CustTailShape = source.CustomTail.CustTailShape;
                live.CustomTail.AsymTail = source.CustomTail.AsymTail;
                if (!keepTargetTailColor)
                {
                    live.CustomTail.Color = source.CustomTail.Color;
                }

                live.CustomSprites.Clear();
                foreach (var s in source.CustomSprites)
                {
                    live.CustomSprites.Add(new DressMySlugcat.CustomSprite
                    {
                        Sprite = s.Sprite,
                        SpriteSheetID = s.SpriteSheetID,
                        ColorHex = s.ColorHex,
                        Enforce = s.Enforce
                    });
                }

                fancyMenu.PlaySound(SoundID.MENU_Switch_Page_Out);
            }

            if (message == "CUST_DEFAULTS")
            {
                var defaults = DressMySlugcat.SpriteDefinitions.GetSlugcatDefault(slugcat, playerNumber)?.Copy();
                live.CustomSprites.Clear();

                if (defaults == null)
                {
                    live.CustomTail.AsymTail = false;
                    live.CustomTail.CustTailShape = false;
                    live.CustomTail.Color = DressMySlugcat.Utils.DefaultBodyColor(slugcat);
                }
                else
                {
                    live.CustomTail.Length = defaults.CustomTail.Length;
                    live.CustomTail.Wideness = defaults.CustomTail.Wideness;
                    live.CustomTail.Roundness = defaults.CustomTail.Roundness;
                    live.CustomTail.Lift = defaults.CustomTail.Lift;
                    live.CustomTail.AsymTail = defaults.CustomTail.AsymTail;
                    live.CustomTail.CustTailShape = defaults.CustomTail.IsCustom;
                    live.CustomTail.Color = defaults.CustomTail.Color;
                }

                fancyMenu.PlaySound(SoundID.MENY_Already_Selected_MultipleChoice_Clicked);
            }

            try
            {
                if (_uiInstances.TryGetValue(fancyMenu, out var ui))
                {
                    ui.SaveCurrentProfile();
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error auto-saving after meadow customization change: {ex.Message}");
            }

            RefreshDummyAndControls(fancyMenu);
        }

        private static DressMySlugcat.Customization GetDmsCopiedCustomization(DressMySlugcat.FancyMenu fancyMenu)
        {
            EnsureDmsCopiedCustomizationField();
            return _dmsCopiedCustomizationField?.GetValue(fancyMenu) as DressMySlugcat.Customization;
        }

        private static void SetDmsCopiedCustomization(DressMySlugcat.FancyMenu fancyMenu, DressMySlugcat.Customization customization)
        {
            EnsureDmsCopiedCustomizationField();
            _dmsCopiedCustomizationField?.SetValue(fancyMenu, customization);
        }

        private static void EnsureDmsCopiedCustomizationField()
        {
            if (_dmsCopiedCustomizationField != null) return;

            try
            {
                _dmsCopiedCustomizationField = typeof(DressMySlugcat.FancyMenu)
                    .GetField("copiedCustomization",
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error getting DMS copiedCustomization field: {ex.Message}");
            }
        }

        private static void RefreshDummyAndControls(DressMySlugcat.FancyMenu fancyMenu)
        {
            try
            {
                var dummyField = fancyMenu.GetType()
                    .GetField("slugcatDummy",
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Instance);

                var dummy = dummyField?.GetValue(fancyMenu);
                if (dummy != null)
                {
                    var updateMethod = dummy.GetType().GetMethod("UpdateSprites",
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);

                    updateMethod?.Invoke(dummy, null);
                }

                var updateControlsMethod = fancyMenu.GetType()
                    .GetMethod("UpdateControls",
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Instance);
                updateControlsMethod?.Invoke(fancyMenu, null);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error refreshing dummy: {ex.Message}");
            }
        }

        public static void Dispose()
        {
            updateHook?.Dispose();
            signalHook?.Dispose();
            getSelectedHook?.Dispose();
            setSelectedHook?.Dispose();
            shutdownHook?.Dispose();
            customizationFor3ArgHook?.Dispose();
            _uiInstances.Clear();
            _currentFancyMenu = null;
            _liveMeadowCustomizations.Clear();
            _copiedMeadowCustomization = null;
        }
    }
}
