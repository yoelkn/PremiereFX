# PremiereFX

Quick effect search for Adobe Premiere Pro (Windows).
Press **Ctrl+Space** in Premiere, type an effect name, and press **Enter**. The effect goes on the selected clips.

- Search matches parts of words and initials (`gb` → Gaussian Blur). Recently used effects rank first.
- Works with a Hebrew keyboard: letters are read as the English letter on the same key.
- Video effects go on the selected video clips, audio effects on the selected audio clips. One Ctrl+Z undoes it.
- ↑/↓ to move, Enter to apply, Esc to close.

## Install
Needs Windows and Premiere Pro 25.6 or newer.

1. Download this repo (Code → Download ZIP) and unzip it.
2. Double-click **`install.cmd`**.
3. In Premiere: **Window → UXP Plugins → PremiereFX**, and dock the small panel somewhere in your workspace. Leave it there; it has to be open to work.

The installer builds a small helper app with the .NET compiler that comes with Windows, installs the Premiere plugin through Adobe's plugin installer, and adds the helper to Windows startup. Nothing needs to be downloaded.

## How it works
- `plugin/`: Premiere UXP plugin. It sends the list of installed effects to the helper and applies the chosen effect.
- `helper/PremiereFX.cs`: Windows helper. It catches Ctrl+Space only while Premiere is the active window and shows the search box.
- The two talk over a local WebSocket on `127.0.0.1:8791`.

## Uninstall
1. Right-click the "fx" tray icon → Exit.
2. Delete `%LOCALAPPDATA%\PremiereFX` and the `PremiereFX` shortcut in `shell:startup`.
3. Remove the plugin in the Creative Cloud app (Stock & Marketplace → Plugins → Manage plugins).
