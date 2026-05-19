# FateWalker

By **RichardLH**.


Open-world FATE grinder for FFXIV (Shadowbringers / Endwalker / Dawntrail). Dalamud plugin.

Walks between FATEs, mounts, flies, engages, and chains across zones — including Shared FATE rank tracking, gemstone auto-trading, durability auto-repair, and safety stops.

## Install

Add this Custom Plugin Repository in Dalamud:

```
https://raw.githubusercontent.com/richardlh023/FateWalker/main/repo.json
```

Steps:

1. In-game: `/xlsettings` → **Experimental** tab
2. Under **Custom Plugin Repositories** → paste the URL above → **Save and Close**
3. Open `/xlplugins` → search **"FateWalker"** → **Install**
4. (First time) Accept the "Third Party Plugins" disclaimer if Dalamud prompts

## Required dependencies

Install these from the puni.sh custom repo before using FateWalker:

- **vnavmesh** — pathfinding
- **BossmodReborn** — combat AI + FateUtils
- **Lifestream** — cross-zone teleport

Recommended:

- **RotationSolverReborn** — combat backend option (set in `/fwalk` → Combat tab)
- **TextAdvance** — auto-advances NPC FATE-start dialogs

## Usage

- `/fwalk` — open the main window
- `/fwalk start` / `stop` / `toggle` / `status`
- **Dry-run** mode logs every action without actually doing anything — turn it on first to verify behaviour

## Disclaimer

Bot plugins violate FFXIV ToS. Use at your own risk on an account you can afford to lose. Player reports are the dominant ban vector — see `reference_bot_safety_detection.md`. Use on a fresh / alt account, not your main.

## Building from source

```powershell
dotnet build -c Release
```

Output lands in `dist/`. Repackage `dist/*` as `plugins/FateWalker/latest.zip` to publish via the Custom Repo.
