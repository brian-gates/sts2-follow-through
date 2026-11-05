# Follow Through (Slay the Spire 2 mod, spike)

When a multi-hit attack kills the last enemy partway through, the hits the game skipped still play out
for show: the attacker's swing (when the attack swings on every hit), hit VFX at the corpse's last
position, and floating damage numbers (including overkill). The local rewards screen waits for them.

Cosmetic only. It deals no damage, changes no game state and starts no game actions, so it is safe in
co-op. Each player sees the replay on their own screen.

## How it works

- `NCombatRoom.RemoveCreatureNode` prefix: saves each dying creature's `NCreature.VfxSpawnPosition`.
- `Hook.ModifyAttackHitCount` postfix: records the real hit count of each `AttackCommand`;
  `AttackCommand.AddResultsInternal` postfix: timestamps each real hit (the attack's natural rhythm).
- `Hook.AfterAttack` prefix: if hits remain (`attackCount - _results.Count`), the attacker is alive and
  `CombatManager.IsEnding`, starts a Godot-only replay at the attack's own per-hit spacing (measured, else
  the last measured for that attack style, else `default_hit_gap_sec`). It uses `NCreature.SetAnimationTrigger`,
  `SfxCmd.Play`, `VfxCmd.PlayVfx(Vector2, string, Control)` and `NDamageNumVfx.Create(Vector2, int)`. When the
  attack has no hit sound, the corpse's own `MonsterModel.TakeDamageSfx` impact is used.
- `NCombatUi.OnCombatWon` prefix: holds the local rewards screen until the replay ends. The safety
  timeout scales with the replay: remaining x gap + 3 s, never under 6 s.
- Skip: once a replay has run `skip_after_seconds` with more than 0.75 s left, a "Click to skip" hint
  fades in. The first click, key or controller button then ends the replay and releases the rewards.
  While armed, one node of the mod's own sits on top (a CanvasLayer added as the root's last child, so its
  `_Input` runs before any game node's, plus a full-rect `MouseFilter=Stop` Control). It marks button,
  key and controller events handled, so nothing underneath reacts. Motion and the mouse wheel pass
  through. The node is removed when the replay ends, or 0.35 s after a skip. Short replays never add it.
- `NCombatRoom._ExitTree` prefix: cancels any replay.

Game members are resolved by name at startup. If any lookup fails, the mod logs once and disables
itself. If a patch throws at runtime, the mod logs once and unpatches.

## Config

`STS2_FollowThrough.conf` sits next to the DLL in `mods/`. It is created on first run, and missing keys are
added on each launch:

| key | default | meaning |
| --- | --- | --- |
| `enabled` | true | |
| `debug` | true | log each replay decision as `[FollowThrough] ...` in godot.log |
| `max_replay_seconds` | 0 | 0 = no cap; >0 compresses the spacing so the replay fits |
| `show_skip_hint` | true | show the "Click to skip" hint when the skip arms |
| `skip_after_seconds` | 2.0 | the skip (hint and input) arms only after this long |
| `fallback_hit_sfx` | true | play the corpse's impact sound when the attack has no hit sound |
| `default_hit_gap_sec` | 0.3 | per-hit spacing when the real rhythm is unknown |

## Build & install (macOS)

```bash
./scripts/build.sh      # dotnet build -> out/ (STS2GameDir overridable)
./scripts/install.sh    # refuses while the game runs; stages, then mv into mods/
```

Requires the .NET 9 SDK (or newer) and a local game install (the game's assemblies are referenced and
never bundled).
