# Follow Through (Slay the Spire 2 mod, spike)

When a multi-hit attack kills the last enemy partway through, the hits the game skipped still play out
for show: the attacker's swing (when the attack swings on every hit), hit VFX at the corpse's last
position, and floating damage numbers (including overkill). The local rewards screen waits for them.

Cosmetic only. It deals no damage, changes no game state and starts no game actions, so it is safe in
co-op. Each player sees the replay on their own screen.

## How it works

- `NCombatRoom.RemoveCreatureNode` prefix: saves each dying creature's `NCreature.VfxSpawnPosition`.
- `Hook.ModifyAttackHitCount` postfix: records the real hit count for each `AttackCommand`.
- `Hook.AfterAttack` prefix: if hits remain (`attackCount - _results.Count`), the attacker is alive and
  `CombatManager.IsEnding`, it starts a Godot-only replay. The replay uses `NCreature.SetAnimationTrigger`,
  `SfxCmd.Play`, `VfxCmd.PlayVfx(Vector2, string, Control)` and `NDamageNumVfx.Create(Vector2, int)`.
  Long chains are sped up to fit within `max_total_sec`.
- `NCombatUi.OnCombatWon` prefix: while a replay is running, holds the call and runs the original
  handler once the replay ends (`hold_timeout_sec` cap).
- `NCombatRoom._ExitTree` prefix: cancels any replay.

Game members are resolved by name at startup. If any lookup fails, the mod logs once and disables
itself. If a patch throws at runtime, the mod logs once and unpatches.

## Config

`STS2_FollowThrough.conf` sits next to the DLL in `mods/` and is created on first run:
`enabled`, `debug` (logs each replay decision as `[FollowThrough] ...` in godot.log), `max_total_sec`,
`hit_interval_sec`, `hold_timeout_sec`.

## Build & install (macOS)

```bash
./scripts/build.sh      # dotnet build -> out/ (STS2GameDir overridable)
./scripts/install.sh    # refuses while the game runs; stages, then mv into mods/
```

Requires the .NET 9 SDK (or newer) and a local game install (the game's assemblies are referenced and
never bundled).
