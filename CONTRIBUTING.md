# Contributing / development

## Build

Requires the .NET 9 SDK (or newer) and a local copy of the game. The build references the game's own
`sts2.dll`, `GodotSharp.dll` and `0Harmony.dll` from your install; nothing proprietary is committed.

```bash
./scripts/build.sh            # dotnet build -c Release -o out
```

The csproj defaults `STS2GameDir` to the standard Steam path per OS. For another location, copy
`local.props.example` to `local.props` (git-ignored) or pass `-p:STS2GameDir="..."` / set `STS2GameDir`.

## Install locally

```bash
./scripts/install.sh          # macOS layout; refuses while the game runs
```

Mods load at startup, so restart the game after installing. Never overwrite the DLL while the game runs (it
is memory-mapped and the game crashes); the script stages the files and moves them into place.

## How it works

- `NCombatRoom.RemoveCreatureNode` prefix: saves each dying creature's on-screen position.
- `Hook.ModifyAttackHitCount` postfix: records the real hit count of each `AttackCommand`.
  `AttackCommand.AddResultsInternal` postfix: timestamps each real hit (the attack's rhythm).
- `Hook.AfterAttack` prefix: if hits remain, the attacker is alive and `CombatManager.IsEnding`, starts a
  Godot-only replay: `NCreature.SetAnimationTrigger`, `SfxCmd.Play`, `VfxCmd.PlayVfx(Vector2, string,
  Control)`, `NDamageNumVfx.Create(Vector2, int)`. Hits follow an absolute schedule (hit k at k x gap).
- `NCombatUi.OnCombatWon` prefix: holds the local rewards screen until the replay ends. The safety timeout is
  remaining x gap + 3 s, never under 6 s.
- Skip (`Skip.cs`): while armed, a temporary CanvasLayer with a full-rect, focused `MouseFilter=Stop`
  Control receives clicks/keys/buttons through its `GuiInput` signal and `AcceptEvent()`s them. Signals are
  used because mod-defined Node subclasses are not registered Godot scripts, so `_Input` overrides never run.
- `NCombatRoom._ExitTree` prefix: cancels a running replay.

Never call `CreatureCmd.Damage`, game hooks, the run's RNG or create GameActions from the replay. Game
members are resolved by name; a missing one disables the mod with one log line.

## Testing

There are no unit tests; behaviour is verified in-game with `"debug": true` in the conf, reading
`~/Library/Application Support/SlayTheSpire2/logs/godot.log` (`[FollowThrough] ...` lines). A multi-hit
kill can be set up with the game's dev console: leave one enemy at low HP and add `SWORD_BOOMERANG` (short
chain) or `TEAR_ASUNDER` after losing HP several times (long chain).

To test skipping without touching the real mouse or keyboard, add these debug-only keys to the conf by hand
(the mod never writes them; remove them afterwards):

| key | effect |
| --- | --- |
| `debug_inject_skip_after_seconds` | > 0: injects a click on the rewards area (via `Input.ParseInputEvent`) that long after the skip arms |
| `debug_inject_key` | `true`: inject an Enter key press instead of a click |
| `debug_inject_prearm_seconds` | > 0: injects a click at that replay time before arming (should pass through) |

Expect `skip catcher ADDED`, `skip catcher got InputEventMouseButton pressed=True (accepted)`,
`replay SKIPPED by player: played N/M hits`, `rewards RELEASED`, `skip catcher REMOVED`, and no
`obtained ... gold from reward` line afterwards.

## Publishing (Steam Workshop)

`scripts/package-workshop.sh` builds Release and stages `dist/workshop/` (git-ignored): `workshop.json`,
`image.png` (from `workshop/preview.jpg`) and `content/`. It only uploads with `--upload`, using MegaCrit's
[sts2-mod-uploader](https://github.com/megacrit/sts2-mod-uploader) (`./tools/ModUploader` or `$MODUPLOADER`).
The upload runs locally with the Steam client open and logged in, never in CI. On Apple Silicon, ad-hoc sign
the downloaded binary once: `codesign --force --sign - tools/ModUploader`.

The first upload creates a new Workshop item and writes `dist/workshop/mod_id.txt`. The script copies it to
`workshop/mod_id.txt`; commit that file so later uploads update the same item. Bump `version` in
`mod_manifest.json` and the csproj, and `changeNote` in `workshop/workshop.json`, for each release.
