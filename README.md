# Libre's Multi Window

<img src="logo.png" alt="Libre's Multi Window logo" width="128">

A [BepInEx 5](https://github.com/BepInEx/BepInEx) plugin for the Pokémon TCG Online 2.95 client.

In full screen, the game minimizes itself as soon as another window gets the focus. With two
monitors (or two game windows side by side), clicking one window hides the other. This mod
keeps the window on screen and running while another window has the focus, so you can
keep two game windows open side by side, one on each monitor.

- ID: `libre.multiwindow`
- Needs: BepInEx 5 only (it doesn't use Libre's Support Mod).
- Windows and Wine. Nothing changes on other platforms.

## How it works

The game's own code never minimizes the window: the Unity player does, with Windows'
`ShowWindow`, when it hears that the app lost the focus. The mod:

1. redirects the player's `ShowWindow` import and skips a minimize the player asks for on its
   own, so the window never leaves the screen (no minimize/restore animation, and it stays on
   its monitor), and
2. if the window gets minimized anyway, puts it back without taking the focus (at most 5
   times in 5 seconds, so it never fights something else).

Minimizing the window yourself (the minimize button, the taskbar, a shortcut) still works.

## Settings

`BepInEx/config/libre.multiwindow.cfg`, created on the first run:

| Setting | Default | What it does |
|---|---|---|
| `BlockMinimize` | `true` | Stop the game from minimizing itself when another window gets the focus. |
| `RestoreWhenMinimized` | `true` | Put the window back if it gets minimized anyway (without you asking). |
| `RunInBackground` | `true` | Keep the game running and drawing while another window has the focus. |

## Building

```
dotnet build -c Release -p:GameDir="<game folder with BepInEx installed>"
```

Copy `bin/Release/net471/LibreTcgMultiWindow.dll` to `BepInEx/plugins/`.

## License

GPL-3.0-or-later. See [LICENSE](LICENSE).
