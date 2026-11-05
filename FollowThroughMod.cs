using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Modding;

namespace STS2_FollowThrough;

// "Follow Through": when a multi-hit attack kills the last enemy partway through, the hits the game skipped
// still play out FOR SHOW ONLY (hit SFX, hit VFX at the corpse's saved position, floating damage numbers
// including overkill), and the local rewards screen waits for them.
//
// Why it is cosmetic and co-op safe (verified statically against sts2.dll v0.107.1):
//   - AttackCommand.<Execute>d__86.MoveNext stops hitting when no target passes IsAlive (IL_018f..01a7),
//     then calls Hook.AfterAttack. We only OBSERVE that point (prefix, never skips) and the hit count the game
//     computed (Hook.ModifyAttackHitCount postfix, never changes __result).
//   - The replay is pure Godot: SfxCmd.Play / NDebugAudioManager.Play (sound), VfxCmd.PlayVfx (position based,
//     because the dead creature's node already left NCombatRoom.CreatureNodes via RemoveCreatureNode, so the
//     creature-based VfxCmd helpers bail), NDamageNumVfx.Create(Vector2, int). It never calls Damage, any
//     Hook.* (other than reading CombatManager.IsEnding, the game's own read-only query), the CombatTargets RNG,
//     the attack's _beforeDamage/_afterAttackerAnim/custom VFX closures, and never creates GameActions.
//   - The only flow change is local: NCombatUi.OnCombatWon (a fire-and-forget local UI handler that starts
//     ShowRewards) is deferred until the replay finishes (timeout-capped). Combat has already ended in the
//     simulation; no peer waits on this.
//
// Every game member is resolved by name at startup. Any lookup failure logs once and disables the mod
// (no patches applied). Any runtime exception in our code logs once and disables it (pass-through).
[ModInitializer("Initialize")]
public static partial class FollowThroughMod
{
    private const string Tag = "[FollowThrough]";
    private const string BuildTag = "2026-11-05-skip";
    private const string ConfigFileName = "STS2_FollowThrough.conf";
    private const string HarmonyId = "com.sts2followthrough";

    // Config
    private static bool _debug = true;            // log each replay decision (spike default: on)
    private static float _maxReplaySec = 0f;       // 0 = no cap (natural rhythm); >0 compresses spacing to fit
    private static bool _showSkipHint = true;
    private static float _skipAfterSec = 2.0f;     // skip (hint + input) only arms after this much replay...
    private const float SkipMinRemainingSec = 0.75f; // ...and only if at least this much is still left
    private static bool _fallbackHitSfx = true;    // no HitSfx on the attack => the corpse's own TakeDamageSfx
    private static float _defaultHitGapSec = 0.3f; // per-hit spacing when the real attack's cadence is unknown
    private const float MinGapSec = 0.05f;

    private static bool _disabled;

    // ===== State =====
    // Dying creature -> its last on-screen VFX anchor (saved before its node leaves CreatureNodes).
    private static readonly ConditionalWeakTable<Creature, StrongBox<Vector2>> _deathPos = new();
    // AttackCommand -> the real hit count the game computed (after ModifyAttackHitCount).
    private static readonly ConditionalWeakTable<object, StrongBox<int>> _hitCounts = new();
    // AttackCommand -> time (ms) each real hit's results were recorded: the attack's natural rhythm.
    private static readonly ConditionalWeakTable<object, List<ulong>> _hitTimes = new();
    // Last measured per-hit gap by attack style (anim|vfx), for attacks that died on hit 1.
    private static readonly Dictionary<string, float> _gapByStyle = new();
    // Pending replay (completed by the main-thread replay); null when nothing is playing.
    private static TaskCompletionSource<bool>? _replay;
    private static int _generation;              // bumped on room exit: cancels a running replay
    private static bool _passThrough;           // reentrancy guard while we run the original OnCombatWon
    private static bool _holding;
    private static readonly Random _jitter = new();

    // ===== Resolved game members =====
    private static Type? _tAttackCommand, _tNCombatRoom, _tNCreature, _tVfxCmd, _tSfxCmd, _tDamageNum, _tDebugAudio, _tNCombatUi, _tHook;
    private static FieldInfo? _fResults;
    private static PropertyInfo? _pAttacker, _pHitSfx, _pTmpHitSfx, _pHitVfx;
    private static PropertyInfo? _pRoomInstance, _pVfxContainer, _pVfxSpawnPos, _pNCreatureCreature, _pDebugAudioInstance;
    // Attacker swing (per hit when the attack plays its anim on every hit). Read-only fields of the attack.
    private static FieldInfo? _fAnimName, _fAnimDelay, _fVisualAttacker, _fShouldPlayAnim, _fPlayOnEveryHit;
    private static MethodInfo? _mSetAnimTrigger;
    private static PropertyInfo? _pCreaturePlayer, _pPlayerCharacter, _pCharAttackSfx;
    private static MethodInfo? _mGetCreatureNode, _mPlayVfx, _mSfxPlay, _mDebugAudioPlay, _mDamageNumCreate;
    private static MethodInfo? _mRemoveCreatureNode, _mModifyHitCount, _mAfterAttack, _mOnCombatWon, _mRoomExitTree, _mAddResults;
    private static PropertyInfo? _pCreatureMonster, _pTakeDamageSfx;
    private static PropertyInfo? _pResReceiver, _pResBlocked, _pResUnblocked, _pResOverkill;

    public static void Initialize()
    {
        try
        {
            if (!LoadConfig()) { GD.Print($"{Tag} disabled via config"); return; }
            if (!Resolve(out string? missing))
            {
                GD.PrintErr($"{Tag} DISABLED: game member not found: {missing} (game updated?)");
                _disabled = true;
                return;
            }
            ApplyPatches();
            GD.Print($"{Tag} initialized (build {BuildTag}, debug={_debug}, max_replay_seconds={_maxReplaySec}, show_skip_hint={_showSkipHint}, skip_after_seconds={_skipAfterSec}, fallback_hit_sfx={_fallbackHitSfx})");
            Dbg($"PlayVfx={Sig(_mPlayVfx)} SfxPlay={Sig(_mSfxPlay)} DebugAudioPlay={Sig(_mDebugAudioPlay)} ModifyAttackHitCount={Sig(_mModifyHitCount)} AfterAttack={Sig(_mAfterAttack)} OnCombatWon={Sig(_mOnCombatWon)} RemoveCreatureNode={Sig(_mRemoveCreatureNode)} NCreature.creature={_pNCreatureCreature?.Name}");
        }
        catch (Exception ex)
        {
            Disable("init", ex);
        }
    }

    // ===== Config =====

    private static bool LoadConfig()
    {
        try
        {
            string? dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (dir == null) return true;
            string path = Path.Combine(dir, ConfigFileName);
            var known = new Dictionary<string, object>();
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                if (root.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False) return false;
                if (root.TryGetProperty("debug", out var db)) _debug = db.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("max_replay_seconds", out var mr) && mr.TryGetSingle(out var mrv)) _maxReplaySec = Math.Clamp(mrv, 0f, 60f);
                if (root.TryGetProperty("show_skip_hint", out var sh)) _showSkipHint = sh.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("skip_after_seconds", out var sa) && sa.TryGetSingle(out var sav)) _skipAfterSec = Math.Clamp(sav, 0f, 60f);
                if (root.TryGetProperty("fallback_hit_sfx", out var fb)) _fallbackHitSfx = fb.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("default_hit_gap_sec", out var dg) && dg.TryGetSingle(out var dgv)) _defaultHitGapSec = Math.Clamp(dgv, MinGapSec, 2f);
                foreach (var prop in root.EnumerateObject()) known[prop.Name] = prop.Value.Clone();
            }
            // Write back with every current key present (additive: unknown/old keys are kept as-is).
            known["enabled"] = true;
            known["debug"] = _debug;
            known["max_replay_seconds"] = _maxReplaySec;
            known["show_skip_hint"] = _showSkipHint;
            known["skip_after_seconds"] = _skipAfterSec;
            known["fallback_hit_sfx"] = _fallbackHitSfx;
            known["default_hit_gap_sec"] = _defaultHitGapSec;
            try { File.WriteAllText(path, JsonSerializer.Serialize(known, new JsonSerializerOptions { WriteIndented = true }) + "\n"); } catch { }
        }
        catch (Exception ex) { GD.PrintErr($"{Tag} config parse failed, using defaults: {ex.Message}"); }
        return true;
    }

    // ===== Resolution (by name; any miss => disabled) =====

    private static bool Resolve(out string? missing)
    {
        missing = null;
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        _tAttackCommand = AccessTools.TypeByName("MegaCrit.Sts2.Core.Commands.Builders.AttackCommand");
        _tNCombatRoom = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom");
        _tNCreature = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NCreature");
        _tVfxCmd = AccessTools.TypeByName("MegaCrit.Sts2.Core.Commands.VfxCmd");
        _tSfxCmd = AccessTools.TypeByName("MegaCrit.Sts2.Core.Commands.SfxCmd");
        _tDamageNum = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Vfx.NDamageNumVfx");
        _tDebugAudio = AccessTools.TypeByName("MegaCrit.Sts2.Core.Audio.Debug.NDebugAudioManager");
        _tNCombatUi = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NCombatUi");
        _tHook = AccessTools.TypeByName("MegaCrit.Sts2.Core.Hooks.Hook");
        if (_tAttackCommand == null) { missing = "AttackCommand"; return false; }
        if (_tNCombatRoom == null) { missing = "NCombatRoom"; return false; }
        if (_tNCreature == null) { missing = "NCreature"; return false; }
        if (_tVfxCmd == null) { missing = "VfxCmd"; return false; }
        if (_tSfxCmd == null) { missing = "SfxCmd"; return false; }
        if (_tDamageNum == null) { missing = "NDamageNumVfx"; return false; }
        if (_tNCombatUi == null) { missing = "NCombatUi"; return false; }
        if (_tHook == null) { missing = "Hook"; return false; }

        _fResults = AccessTools.Field(_tAttackCommand, "_results");
        _pAttacker = AccessTools.Property(_tAttackCommand, "Attacker");
        _pHitSfx = AccessTools.Property(_tAttackCommand, "HitSfx");
        _pTmpHitSfx = AccessTools.Property(_tAttackCommand, "TmpHitSfx");   // optional
        _pHitVfx = AccessTools.Property(_tAttackCommand, "HitVfx");
        // Optional (no anim replay if missing): the attacker animation settings the real hits used.
        _fAnimName = AccessTools.Field(_tAttackCommand, "_attackerAnimName");
        _fAnimDelay = AccessTools.Field(_tAttackCommand, "_attackerAnimDelay");
        _fVisualAttacker = AccessTools.Field(_tAttackCommand, "_visualAttacker");
        _fShouldPlayAnim = AccessTools.Field(_tAttackCommand, "_shouldPlayAnimation");
        _fPlayOnEveryHit = AccessTools.Field(_tAttackCommand, "_playOnEveryHit");
        _mAddResults = AccessTools.DeclaredMethod(_tAttackCommand, "AddResultsInternal");   // optional: rhythm
        if (_fResults == null) { missing = "AttackCommand._results"; return false; }
        if (_pAttacker == null) { missing = "AttackCommand.Attacker"; return false; }
        if (_pHitSfx == null) { missing = "AttackCommand.HitSfx"; return false; }
        if (_pHitVfx == null) { missing = "AttackCommand.HitVfx"; return false; }

        _pRoomInstance = AccessTools.Property(_tNCombatRoom, "Instance");
        _pVfxContainer = AccessTools.Property(_tNCombatRoom, "CombatVfxContainer");
        _mGetCreatureNode = AccessTools.Method(_tNCombatRoom, "GetCreatureNode", new[] { typeof(Creature) });
        _mRemoveCreatureNode = AccessTools.DeclaredMethod(_tNCombatRoom, "RemoveCreatureNode");
        _mRoomExitTree = AccessTools.DeclaredMethod(_tNCombatRoom, "_ExitTree");
        if (_pRoomInstance == null) { missing = "NCombatRoom.Instance"; return false; }
        if (_pVfxContainer == null) { missing = "NCombatRoom.CombatVfxContainer"; return false; }
        if (_mGetCreatureNode == null) { missing = "NCombatRoom.GetCreatureNode"; return false; }
        if (_mRemoveCreatureNode == null) { missing = "NCombatRoom.RemoveCreatureNode"; return false; }

        _pVfxSpawnPos = AccessTools.Property(_tNCreature, "VfxSpawnPosition");
        // Optional: the dead monster's own impact sound (fallback when the attack has no HitSfx).
        var tMonster = AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.MonsterModel");
        if (tMonster != null)
        {
            _pCreatureMonster = typeof(Creature).GetProperties(All & ~BindingFlags.Static).FirstOrDefault(p => p.PropertyType == tMonster && p.GetIndexParameters().Length == 0);
            _pTakeDamageSfx = AccessTools.Property(tMonster, "TakeDamageSfx");
            if (_pTakeDamageSfx?.PropertyType != typeof(string)) _pTakeDamageSfx = null;
        }
        _mSetAnimTrigger = AccessTools.Method(_tNCreature, "SetAnimationTrigger", new[] { typeof(string) });   // optional
        _pCreaturePlayer = AccessTools.Property(typeof(Creature), "Player");                                  // optional
        _pPlayerCharacter = _pCreaturePlayer != null ? AccessTools.Property(_pCreaturePlayer.PropertyType, "Character") : null;
        _pCharAttackSfx = _pPlayerCharacter != null ? AccessTools.Property(_pPlayerCharacter.PropertyType, "AttackSfx") : null;
        if (_pVfxSpawnPos == null || _pVfxSpawnPos.PropertyType != typeof(Vector2)) { missing = "NCreature.VfxSpawnPosition"; return false; }
        // The NCreature -> Creature link: the (single) Creature-typed instance property.
        _pNCreatureCreature = _tNCreature.GetProperties(All & ~BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(Creature) && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.Name == "Entity" ? 0 : p.Name == "Creature" ? 1 : 2).FirstOrDefault();
        if (_pNCreatureCreature == null) { missing = "NCreature.<Creature property>"; return false; }

        // VfxCmd.PlayVfx(Vector2 globalPosition, string path, Node container, ...)
        _mPlayVfx = _tVfxCmd.GetMethods(All).Where(m => m.IsStatic && m.Name == "PlayVfx")
            .FirstOrDefault(m => ParamsStartWith(m, typeof(Vector2), typeof(string)) && m.GetParameters().Any(p => typeof(Node).IsAssignableFrom(p.ParameterType)));
        if (_mPlayVfx == null) { missing = "VfxCmd.PlayVfx(Vector2, string, Node)"; return false; }
        _mSfxPlay = _tSfxCmd.GetMethods(All).Where(m => m.IsStatic && m.Name == "Play")
            .FirstOrDefault(m => ParamsStartWith(m, typeof(string)) && m.GetParameters().Skip(1).All(p => p.HasDefaultValue));
        if (_mSfxPlay == null) { missing = "SfxCmd.Play(string)"; return false; }
        if (_tDebugAudio != null)   // optional (TmpHitSfx fallback)
        {
            _pDebugAudioInstance = AccessTools.Property(_tDebugAudio, "Instance");
            _mDebugAudioPlay = _tDebugAudio.GetMethods(All).Where(m => !m.IsStatic && m.Name == "Play")
                .FirstOrDefault(m => ParamsStartWith(m, typeof(string)) && m.GetParameters().Skip(1).All(p => p.HasDefaultValue));
        }
        _mDamageNumCreate = AccessTools.Method(_tDamageNum, "Create", new[] { typeof(Vector2), typeof(int) });
        if (_mDamageNumCreate == null) { missing = "NDamageNumVfx.Create(Vector2, int)"; return false; }

        _mModifyHitCount = AccessTools.DeclaredMethod(_tHook, "ModifyAttackHitCount");
        if (_mModifyHitCount == null || !_mModifyHitCount.GetParameters().Any(p => p.ParameterType == _tAttackCommand)
            || (_mModifyHitCount.ReturnType != typeof(decimal) && _mModifyHitCount.ReturnType != typeof(int)))
        { missing = "Hook.ModifyAttackHitCount(.., AttackCommand, ..) -> decimal|int"; return false; }
        _mAfterAttack = AccessTools.DeclaredMethod(_tHook, "AfterAttack");
        if (_mAfterAttack == null || !_mAfterAttack.GetParameters().Any(p => p.ParameterType == _tAttackCommand))
        { missing = "Hook.AfterAttack(.., AttackCommand)"; return false; }
        _mOnCombatWon = AccessTools.DeclaredMethod(_tNCombatUi, "OnCombatWon");
        if (_mOnCombatWon == null || _mOnCombatWon.ReturnType != typeof(void)) { missing = "NCombatUi.OnCombatWon (void)"; return false; }

        var tRes = typeof(DamageResult);
        _pResReceiver = AccessTools.Property(tRes, "Receiver");
        _pResBlocked = AccessTools.Property(tRes, "BlockedDamage");
        _pResUnblocked = AccessTools.Property(tRes, "UnblockedDamage");
        _pResOverkill = AccessTools.Property(tRes, "OverkillDamage");
        if (_pResReceiver == null || _pResUnblocked == null || _pResOverkill == null) { missing = "DamageResult.{Receiver,UnblockedDamage,OverkillDamage}"; return false; }
        return true;
    }

    private static bool ParamsStartWith(MethodInfo m, params Type[] types)
    {
        var ps = m.GetParameters();
        if (ps.Length < types.Length) return false;
        for (int i = 0; i < types.Length; i++) if (ps[i].ParameterType != types[i]) return false;
        return true;
    }

    private static string Sig(MethodInfo? m) =>
        m == null ? "null" : $"{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})->{m.ReturnType.Name}";

    private static void ApplyPatches()
    {
        var h = new Harmony(HarmonyId);
        HarmonyMethod M(string name) => new(typeof(FollowThroughMod).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));
        h.Patch(_mRemoveCreatureNode, prefix: M(nameof(RemoveCreatureNodePrefix)));
        h.Patch(_mModifyHitCount, postfix: M(_mModifyHitCount!.ReturnType == typeof(decimal) ? nameof(HitCountPostfixDecimal) : nameof(HitCountPostfixInt)));
        h.Patch(_mAfterAttack, prefix: M(nameof(AfterAttackPrefix)));
        h.Patch(_mOnCombatWon, prefix: M(nameof(OnCombatWonPrefix)));
        if (_mRoomExitTree != null) h.Patch(_mRoomExitTree, prefix: M(nameof(RoomExitTreePrefix)));
        if (_mAddResults != null) h.Patch(_mAddResults, postfix: M(nameof(AddResultsPostfix)));
        GD.Print($"{Tag} patches applied: RemoveCreatureNode, ModifyAttackHitCount, AfterAttack, OnCombatWon{(_mRoomExitTree != null ? ", NCombatRoom._ExitTree" : "")}{(_mAddResults != null ? ", AddResultsInternal" : "")}");
    }

    // Kill switch: log once, stop doing anything. Patches stay but every one of them is a pass-through.
    private static void Disable(string where, Exception ex)
    {
        if (_disabled) return;
        _disabled = true;
        GD.PrintErr($"{Tag} DISABLED after error in {where}: {ex.GetType().Name}: {ex.Message}");
        try { new Harmony(HarmonyId).UnpatchAll(HarmonyId); } catch { }
        // A held rewards screen is released by its own waiter (it checks completion/timeout, not _disabled).
        try { _replay?.TrySetResult(false); } catch { }
    }

    private static void Dbg(string msg)
    {
        if (_debug) GD.Print($"{Tag} {msg}");
    }

    // ===== Patch 1: remember where a dying creature was =====

    private static void RemoveCreatureNodePrefix(object __instance, object[] __args)
    {
        if (_disabled) return;
        try
        {
            foreach (var a in __args)
            {
                Node? node = null;
                Creature? creature = null;
                if (a is Node n && _tNCreature!.IsInstanceOfType(n)) { node = n; creature = _pNCreatureCreature!.GetValue(n) as Creature; }
                else if (a is Creature c) { creature = c; node = _mGetCreatureNode!.Invoke(__instance, new object[] { c }) as Node; }
                if (node == null || creature == null || !GodotObject.IsInstanceValid(node)) continue;
                Vector2 pos = _pVfxSpawnPos!.GetValue(node) is Vector2 v ? v : (node as Node2D)?.GlobalPosition ?? (node as Control)?.GlobalPosition ?? Vector2.Zero;
                _deathPos.AddOrUpdate(creature, new StrongBox<Vector2>(pos));
                Dbg($"saved corpse position {pos} for {CreatureName(creature)}");
            }
        }
        catch (Exception ex) { Disable("RemoveCreatureNode prefix", ex); }
    }

    // ===== Patch 2: remember the real hit count of each attack =====

    private static void HitCountPostfixDecimal(object[] __args, decimal __result) => RecordHitCount(__args, (int)Math.Min(__result, 1000m));
    private static void HitCountPostfixInt(object[] __args, int __result) => RecordHitCount(__args, Math.Min(__result, 1000));

    private static void RecordHitCount(object[] args, int count)
    {
        if (_disabled) return;
        try
        {
            var cmd = args.FirstOrDefault(a => a != null && _tAttackCommand!.IsInstanceOfType(a));
            if (cmd != null) _hitCounts.AddOrUpdate(cmd, new StrongBox<int>(count));
        }
        catch (Exception ex) { Disable("ModifyAttackHitCount postfix", ex); }
    }

    // ===== Patch 3: decide on and start the replay =====

    private sealed record Plan(int Remaining, string? HitSfx, string? TmpHitSfx, string? HitVfx, List<(Creature Target, Vector2 Pos, int Amount, string? ImpactSfx)> Targets, string Attacker,
        Creature? AnimCreature, string? AnimName, float AnimDelay, string? AttackSfx, float Gap);

    // Patch: stamp each real hit of an attack (AddResultsInternal runs once per hit).
    private static void AddResultsPostfix(object __instance)
    {
        if (_disabled) return;
        try { _hitTimes.GetOrCreateValue(__instance).Add(Time.GetTicksMsec()); }
        catch (Exception ex) { Disable("AddResultsInternal postfix", ex); }
    }

    private static void AfterAttackPrefix(object[] __args)
    {
        if (_disabled) return;
        try
        {
            var cmd = __args.FirstOrDefault(a => a != null && _tAttackCommand!.IsInstanceOfType(a));
            if (cmd == null) return;
            var plan = Decide(cmd);
            if (plan == null) return;

            // Store the pending task NOW (synchronously, before CombatWon can fire); the Godot-side replay
            // starts on the next idle frame on the main thread and completes it.
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _replay?.TrySetResult(false);
            _replay = tcs;
            int gen = _generation;
            _holdTimeoutForPending = Math.Max(6f, plan.Remaining * plan.Gap + 3f);   // scales with the replay
            Callable.From(() => { _ = RunReplay(plan, tcs, gen); }).CallDeferred();
        }
        catch (Exception ex) { Disable("AfterAttack prefix", ex); }
    }

    private static Plan? Decide(object cmd)
    {
        if (!_hitCounts.TryGetValue(cmd, out var countBox)) { Dbg("skip: no recorded hit count for this attack"); return null; }
        int attackCount = countBox.Value;
        int done = _fResults!.GetValue(cmd) is ICollection results ? results.Count : -1;
        int remaining = attackCount - done;
        var attacker = _pAttacker!.GetValue(cmd) as Creature;
        string who = CreatureName(attacker);
        if (done < 0) { Dbg($"skip: _results unreadable (attacker={who})"); return null; }
        if (remaining <= 0)
        {
            if (attackCount > 1) Dbg($"skip: all {attackCount} hits landed (attacker={who})");
            return null;
        }
        if (attacker == null || attacker.IsDead) { Dbg($"skip: attacker dead/missing ({who}), hits={done}/{attackCount}"); return null; }
        if (attacker.IsMonster) { Dbg($"skip: attacker is a monster ({who}), hits={done}/{attackCount}"); return null; }
        var cm = CombatManager.Instance;
        bool inProgress = cm?.IsInProgress ?? false;
        bool ending = cm?.IsEnding ?? false;   // all enemies dead, nothing stopping the end, no pending loss
        if (!inProgress || !ending) { Dbg($"skip: combat not ending (inProgress={inProgress}, isEnding={ending}), hits={done}/{attackCount}, attacker={who}"); return null; }

        // Targets of the final real hit (the corpses), with that hit's pre-block per-hit damage.
        var targets = new List<(Creature, Vector2, int, string?)>();
        if (_fResults.GetValue(cmd) is IList hits && hits.Count > 0 && hits[hits.Count - 1] is IEnumerable last)
        {
            foreach (var r in last)
            {
                if (r == null || _pResReceiver!.GetValue(r) is not Creature recv) continue;
                if (targets.Any(t => ReferenceEquals(t.Item1, recv))) continue;
                int unblocked = ToInt(_pResUnblocked!.GetValue(r));
                int overkill = ToInt(_pResOverkill!.GetValue(r));
                int blocked = _pResBlocked != null ? ToInt(_pResBlocked.GetValue(r)) : 0;
                int amount = blocked + unblocked + overkill;   // what the hit would have dealt to an unblocked target
                Vector2? pos = _deathPos.TryGetValue(recv, out var pb) ? pb.Value : LivePos(recv);
                if (pos == null) { Dbg($"no position for {CreatureName(recv)}; skipped that target"); continue; }
                string? impact = null;
                try { if (_pCreatureMonster?.GetValue(recv) is { } mm) impact = _pTakeDamageSfx?.GetValue(mm) as string; } catch { }
                targets.Add((recv, pos.Value, Math.Max(0, amount), impact));
                Dbg($"target {CreatureName(recv)} lastHit blocked={blocked} unblocked={unblocked} overkill={overkill} -> show {amount} at {pos.Value}");
            }
        }
        if (targets.Count == 0) { Dbg($"skip: no corpse to hit, hits={done}/{attackCount}, attacker={who}"); return null; }

        string? sfx = _pHitSfx!.GetValue(cmd) as string;
        string? tmpSfx = _pTmpHitSfx?.GetValue(cmd) as string;
        string? vfx = _pHitVfx!.GetValue(cmd) as string;
        // The game swings the attacker before every hit only when _playOnEveryHit (else just before hit 1).
        string? animName = null; float animDelay = 0f; Creature? animCreature = null; string? attackSfx = null;
        try
        {
            bool should = _fShouldPlayAnim?.GetValue(cmd) is true;
            bool everyHit = _fPlayOnEveryHit?.GetValue(cmd) is true;
            if (should && everyHit && _mSetAnimTrigger != null && _fAnimName?.GetValue(cmd) is string an)
            {
                animName = an;
                animDelay = _fAnimDelay?.GetValue(cmd) is float d ? d : 0f;
                animCreature = (_fVisualAttacker?.GetValue(cmd) as Creature) ?? attacker;
                // TriggerAnim plays the character's attack swoosh with the "Attack" trigger; replay that sound too.
                var player = _pCreaturePlayer?.GetValue(animCreature);
                var character = player != null ? _pPlayerCharacter?.GetValue(player) : null;
                attackSfx = character != null ? _pCharAttackSfx?.GetValue(character) as string : null;
            }
            Dbg($"anim: shouldPlay={should} everyHit={everyHit} name={_fAnimName?.GetValue(cmd) ?? "none"} delay={_fAnimDelay?.GetValue(cmd)} -> {(animName != null ? "replay swing" : "no swing")}");
        }
        catch (Exception ex) { Dbg($"anim lookup failed (no swing): {ex.Message}"); animName = null; }
        // Natural rhythm: the real attack's own measured per-hit gap; else the last one measured for the same
        // style of attack; else the configured default.
        string style = $"{animName}|{vfx}";
        float gap; string gapSource;
        if (_hitTimes.TryGetValue(cmd, out var times) && times.Count >= 2)
        {
            gap = Math.Max(MinGapSec, (times[^1] - times[0]) / 1000f / (times.Count - 1));
            _gapByStyle[style] = gap; gapSource = $"measured over {times.Count} real hits";
        }
        else if (_gapByStyle.TryGetValue(style, out var g)) { gap = g; gapSource = "last measured for this attack style"; }
        else { gap = Math.Max(_defaultHitGapSec, animDelay + 0.1f); gapSource = "default"; }
        if (_maxReplaySec > 0f && gap * remaining > _maxReplaySec)
        {
            float capped = Math.Max(MinGapSec, _maxReplaySec / remaining);
            gapSource += $", compressed from {gap:0.000}s by max_replay_seconds={_maxReplaySec}";
            gap = capped;
        }
        string impactNote = sfx != null ? sfx : tmpSfx != null ? $"tmp:{tmpSfx}" : _fallbackHitSfx ? $"fallback:{targets[0].Item4 ?? "none"}" : "none";
        Dbg($"REPLAY: attacker={who} hits landed={done}/{attackCount} remaining={remaining} gap={gap:0.000}s ({gapSource}) est={gap * remaining:0.00}s sfx={impactNote} vfx={vfx ?? "none"} targets={targets.Count}");
        return new Plan(remaining, sfx, tmpSfx, vfx, targets, who, animCreature, animName, animDelay, attackSfx, gap);
    }

    private static async Task RunReplay(Plan plan, TaskCompletionSource<bool> tcs, int gen)
    {
        int played = 0;
        ulong t0 = Time.GetTicksMsec();
        bool skipped = false;
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return;
            // Absolute schedule: hit k lands at t0 + k*gap (k = 1..n), so the opening delay is one gap and the
            // total is exactly n*gap (timer jitter does not accumulate). The swing starts swingLead before its hit.
            float gap = plan.Gap;
            bool swing = plan.AnimName != null;
            float swingLead = swing ? Math.Min(plan.AnimDelay, gap * 0.7f) : 0f;
            float total = gap * plan.Remaining;
            Skip.Begin(t0, total);
            for (int k = 1; k <= plan.Remaining; k++)
            {
                ulong hitAt = t0 + (ulong)(k * gap * 1000f);
                if (swing)
                {
                    if (!await WaitUntil(tree, hitAt - (ulong)(swingLead * 1000f), gen)) break;
                    if (Skip.Requested) { skipped = true; break; }
                    Swing(plan);
                }
                if (!await WaitUntil(tree, hitAt, gen)) break;
                if (Skip.Requested) { skipped = true; break; }
                var container = VfxContainer();
                if (container == null) { Dbg($"replay stopped after {played}/{plan.Remaining}: combat VFX container gone"); break; }
                foreach (var (target, pos, amount, impact) in plan.Targets)
                {
                    PlaySfx(plan, impact);
                    if (plan.HitVfx != null) _mPlayVfx!.Invoke(null, BuildArgs(_mPlayVfx, pos, plan.HitVfx, container));
                    var jitter = new Vector2(_jitter.Next(-30, 31), _jitter.Next(-20, 11));   // own RNG: never the game's
                    if (_mDamageNumCreate!.Invoke(null, new object[] { pos + new Vector2(0f, -100f) + jitter, amount }) is Node num
                        && GodotObject.IsInstanceValid(container))
                        container.AddChild(num);
                }
                played++;
            }
        }
        catch (Exception ex) { Disable("replay", ex); }
        finally
        {
            Skip.End();
            Dbg($"replay {(skipped ? "SKIPPED by player" : "done")}: played {played}/{plan.Remaining} hits for {plan.Attacker} in {Time.GetTicksMsec() - t0} ms (planned {plan.Gap * plan.Remaining * 1000f:0} ms)");
            tcs.TrySetResult(true);
            if (ReferenceEquals(_replay, tcs)) _replay = null;
        }
    }

    // Waits (frame by frame) until the absolute tick; false if the replay was cancelled meanwhile.
    private static async Task<bool> WaitUntil(SceneTree tree, ulong atMs, int gen)
    {
        while (Time.GetTicksMsec() < atMs)
        {
            Skip.Tick();
            if (Skip.Requested) return true;
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            if (_disabled || gen != _generation) { Dbg("replay cancelled (room exit or disabled)"); return false; }
        }
        return !_disabled && gen == _generation;
    }

    // Godot-only swing: NCreature.SetAnimationTrigger on the (living) attacker's node + the attack swoosh.
    // Same visible effect as CreatureCmd.TriggerAnim, without going through the command.
    private static bool Swing(Plan plan)
    {
        try
        {
            if (plan.AnimCreature == null || plan.AnimCreature.IsDead) return false;
            var room = _pRoomInstance!.GetValue(null);
            if (room == null || _mGetCreatureNode!.Invoke(room, new object[] { plan.AnimCreature }) is not Node node || !GodotObject.IsInstanceValid(node)) return false;
            _mSetAnimTrigger!.Invoke(node, new object[] { plan.AnimName! });
            if (plan.AttackSfx != null && plan.AnimName == "Attack") _mSfxPlay!.Invoke(null, BuildArgs(_mSfxPlay, plan.AttackSfx));
            return true;
        }
        catch (Exception ex) { Dbg($"swing failed (ignored): {ex.InnerException?.Message ?? ex.Message}"); return false; }
    }

    private static void PlaySfx(Plan plan, string? impactSfx)
    {
        try
        {
            if (plan.HitSfx != null) _mSfxPlay!.Invoke(null, BuildArgs(_mSfxPlay, plan.HitSfx));
            else if (plan.TmpHitSfx != null && _mDebugAudioPlay != null && _pDebugAudioInstance?.GetValue(null) is { } mgr)
                _mDebugAudioPlay.Invoke(mgr, BuildArgs(_mDebugAudioPlay, plan.TmpHitSfx));
            else if (_fallbackHitSfx && impactSfx != null)
                _mSfxPlay!.Invoke(null, BuildArgs(_mSfxPlay, impactSfx));   // the corpse's own "takes damage" impact
        }
        catch (Exception ex) { Dbg($"sfx failed (ignored): {ex.InnerException?.Message ?? ex.Message}"); }
    }

    // Leading args as given; the rest from the method's defaults (Node args from the given ones, by type).
    private static object?[] BuildArgs(MethodInfo m, params object[] lead)
    {
        var ps = m.GetParameters();
        var args = new object?[ps.Length];
        int li = 0;
        for (int i = 0; i < ps.Length; i++)
        {
            if (li < lead.Length && ps[i].ParameterType.IsInstanceOfType(lead[li])) { args[i] = lead[li++]; continue; }
            if (ps[i].HasDefaultValue) { args[i] = ps[i].DefaultValue; continue; }
            args[i] = ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
        }
        return args;
    }

    // ===== Patch 4: hold the local rewards screen until the replay finishes =====

    private static bool OnCombatWonPrefix(object __instance, object[] __args)
    {
        if (_passThrough) return true;
        try
        {
            var pending = _replay;
            if (_disabled || _holding || pending == null || pending.Task.IsCompleted) return true;
            _holding = true;
            Dbg("rewards HELD: replay in progress");
            _ = ReleaseLater(__instance, (object[])__args.Clone(), pending.Task, _holdTimeoutForPending);
            return false;
        }
        catch (Exception ex) { _holding = false; Disable("OnCombatWon prefix", ex); return true; }
    }

    private static float _holdTimeoutForPending = 6f;

    private static async Task ReleaseLater(object ui, object[] args, Task replay, float timeoutSec)
    {
        ulong t0 = Time.GetTicksMsec();
        bool timedOut = false;
        try
        {
            if (Engine.GetMainLoop() is SceneTree tree)
            {
                while (!replay.IsCompleted)
                {
                    if (Time.GetTicksMsec() - t0 > (ulong)(timeoutSec * 1000)) { timedOut = true; break; }
                    await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                }
            }
        }
        catch { }
        finally
        {
            _holding = false;
            if (timedOut) GD.Print($"{Tag} rewards RELEASED by safety timeout after {Time.GetTicksMsec() - t0} ms (limit {timeoutSec:0.0}s)");
            else Dbg($"rewards RELEASED after {Time.GetTicksMsec() - t0} ms");
            try
            {
                if (ui is GodotObject go && !GodotObject.IsInstanceValid(go)) Dbg("rewards: NCombatUi gone; original not invoked");
                else
                {
                    _passThrough = true;
                    try { _mOnCombatWon!.Invoke(ui, args); }
                    finally { _passThrough = false; }
                }
            }
            catch (Exception ex) { GD.PrintErr($"{Tag} running original OnCombatWon failed: {ex.InnerException?.Message ?? ex.Message}"); }
        }
    }

    // ===== Patch 5: room exit clears state =====

    private static void RoomExitTreePrefix()
    {
        try
        {
            _generation++;
            Skip.End();
            _replay?.TrySetResult(false);
            _replay = null;
        }
        catch { }
    }

    // ===== Helpers =====

    private static Node? VfxContainer()
    {
        try
        {
            var room = _pRoomInstance!.GetValue(null);
            if (room == null || _pVfxContainer!.GetValue(room) is not Node n) return null;
            return GodotObject.IsInstanceValid(n) && n.IsInsideTree() ? n : null;
        }
        catch { return null; }
    }

    private static Vector2? LivePos(Creature c)
    {
        try
        {
            var room = _pRoomInstance!.GetValue(null);
            if (room == null || _mGetCreatureNode!.Invoke(room, new object[] { c }) is not Node node || !GodotObject.IsInstanceValid(node)) return null;
            return _pVfxSpawnPos!.GetValue(node) is Vector2 v ? v : null;
        }
        catch { return null; }
    }

    private static int ToInt(object? v)
    {
        try { return v == null ? 0 : Convert.ToInt32(v); } catch { return 0; }
    }

    private static string CreatureName(Creature? c)
    {
        if (c == null) return "null";
        try { return c.ToString() ?? "creature"; } catch { return "creature"; }
    }
}
