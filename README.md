# DMSxMeadow

A **BepInEx code mod** for *Rain World* that resolves the skin-cloning issue between **Dress My Slugcat (DMS)** and **Rain Meadow** by introducing an isolated client-side profile storage and assignment manager.


## Overview

The core problem stems from how `DressMySlugcat.Customization.For(Player, bool)` handles player indices in an online environment. Rain Meadow introduces custom network clones that confuse the native `PlayerNumber` allocation, forcing DMS to fallback to local player states (cloning Player 1's skin onto everyone else) or rendering them incorrectly.

**DMSxMeadow** addresses this by decoupling DMS profiles from native local player slots. It introduces an internal database system (`MeadowProfileManager`) that manages, persists, and assigns individual DMS customizations to specific Rain Meadow players without modifying the core DMS installation.

## 🛠️ Technical Architecture

### 1. Persistent Profile Manager (`MeadowProfileManager`)
The core of the mod relies on a binary-serialized persistent database (`meadowcustom.dat`) and an assignment mapping file (`dmsxmeadow.txt`).

* **Internal Offsets & Slot Ranges:** The system uses a fixed offset (`PROFILE_OFFSET = 4`) to isolate custom Meadow profiles from local game options, ensuring profile mappings remain consistent across sessions.
* **Database & Profile Data:** Customizations are wrapped inside `MeadowProfileData` containers within a central `MedowDatabase` dictionary, tracking updating timestamps, profile numbers, and visual metadata.

### 2. Execution & Detour Flow
1. **Hook Injection:** Intercepts `DressMySlugcat.Customization.For(Player, bool)` using a `MonoMod` runtime detour.
2. **Entity Validation:** Queries `RainMeadow.OnlinePhysicalObject.map` using the player's `abstractCreature` reference to verify if the entity belongs to any client (`owner != null`).
3. **Database Lookups:** Retrieves the player's Steam ID (`owner.id.ToString()`) and queries `MeadowProfileManager` for its corresponding internal profile slot via `dmsxmeadow.txt`.
4. **Data Redirection:** If ssigned, returns the specific `Customization` stored in `MeadowProfileManager.Database.Profiles`. Forces `PlayerNumber = 0` on the returning instance to bypass local gamepad polling, preventing `NullReferenceException` crashes during realization.
5. **Fallback:** Unmatched or unassigned clients are safely delegated back to the original method (`orig`).

## ⚙️ Compilation Notes
Target framework: **.NET Framework 4.8**
Dependencies required for compilation:
* `0Harmony.dll`
* `BepInEx.dll`
* `com.rlabrecque.steamworks.net.dll`
* `DressMySlugcat.dll`
* `HOOKS-Assembly-CSharp.dll`
* `Mono.Cecil.dll`
* `MonoMod.RuntimeDetour.dll`
* `MonoMod.Utils.dll`
* `PUBLIC-Assembly-CSharp.dll`
* `RainMeadow.dll`
* `UnityEngine.dll`
* `UnityEngine.CoreModule.dll`
* `UnityEngine.IMGUIModule.dll`
* `UnityEngine.InputLegacyModule`
