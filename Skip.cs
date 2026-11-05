using System;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace STS2_FollowThrough;

// Skipping a long replay. The skip arms only once the replay has run for skip_after_seconds AND at least
// SkipMinRemainingSec is still left; short replays never show a hint and never touch input.
// While armed, the first mouse click / key press / controller button press skips: no further hits spawn and
// the held rewards are released at once (the replay's task completes and ReleaseLater runs OnCombatWon).
//
// Consuming the input: Godot delivers each event to every node's _Input first (deepest nodes first, NGame,
// the root game node, last), then to the GUI (Control._GuiInput), then to _UnhandledInput. The game's own
// buttons (NButton) act in _Input. While armed we prefix every _Input/_GuiInput/_UnhandledInput/
// _UnhandledKeyInput/_ShortcutInput declared in sts2.dll: the first one reached calls
// Viewport.SetInputAsHandled() and skips its original, so no later node, no GUI control and no hotkey
// handler sees the event. Releases (and presses for ~0.35 s after the skip) are swallowed too, so the
// release half of the click cannot activate anything either. Mouse motion is never touched.
public static partial class FollowThroughMod
{
    private static int PatchInputHandlers(Harmony h)
    {
        var prefix = new HarmonyMethod(typeof(FollowThroughMod).GetMethod(nameof(InputPrefix), BindingFlags.NonPublic | BindingFlags.Static));
        string[] names = { "_Input", "_GuiInput", "_UnhandledInput", "_UnhandledKeyInput", "_ShortcutInput" };
        int n = 0;
        Type[] types;
        try { types = typeof(Creature).Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
        foreach (var t in types)
        {
            if (t == null || t.ContainsGenericParameters || !typeof(Node).IsAssignableFrom(t)) continue;
            foreach (var name in names)
            {
                var m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    null, new[] { typeof(InputEvent) }, null);
                if (m == null || m.IsAbstract || m.GetMethodBody() == null) continue;
                try { h.Patch(m, prefix: prefix); n++; }
                catch (Exception ex) { Dbg($"input patch skipped {t.Name}.{name}: {ex.Message}"); }
            }
        }
        return n;
    }

    // Returns false (skip the original) only while a skip is armed or just happened, for button/key events.
    private static bool InputPrefix(object __instance, object[] __args)
    {
        try
        {
            if (!Skip.Consuming || __args.Length == 0) return true;
            var ev = __args[0] as InputEvent;
            if (!Skip.IsButtonLike(ev, out bool pressed)) return true;
            if (pressed) Skip.Request(ev!);
            if (__instance is Node node && GodotObject.IsInstanceValid(node)) node.GetViewport()?.SetInputAsHandled();
            return false;
        }
        catch { return true; }
    }

    private static class Skip
    {
        private static bool _running, _armed, _requested;
        private static ulong _t0, _swallowUntil;
        private static float _total;
        private static CanvasLayer? _hintLayer;
        private static Label? _hint;

        public static bool Requested => _requested;
        public static bool Consuming => (_running && _armed) || Time.GetTicksMsec() < _swallowUntil;

        public static void Begin(ulong t0, float totalSec)
        {
            End();
            _running = true; _armed = false; _requested = false;
            _t0 = t0; _total = totalSec;
        }

        // Called every frame by the replay's wait loop.
        public static void Tick()
        {
            if (!_running || _armed || _requested) return;
            float elapsed = (Time.GetTicksMsec() - _t0) / 1000f;
            float left = _total - elapsed;
            if (elapsed < _skipAfterSec || left <= SkipMinRemainingSec) return;
            _armed = true;
            Dbg($"skip ARMED after {elapsed:0.00}s ({left:0.00}s left); hint={_showSkipHint}");
            if (_showSkipHint) ShowHint();
        }

        public static void Request(InputEvent ev)
        {
            if (!_running || !_armed || _requested) return;
            _requested = true;
            _swallowUntil = Time.GetTicksMsec() + 350;   // the rest of the click/keypress, and a double-click
            Dbg($"skip requested by {ev.GetType().Name} at {(Time.GetTicksMsec() - _t0) / 1000f:0.00}s; input consumed");
            HideHint();
        }

        public static void End()
        {
            if (_running && _armed && !_requested) _swallowUntil = 0;
            _running = false; _armed = false;
            HideHint();
        }

        public static bool IsButtonLike(InputEvent? ev, out bool pressed)
        {
            pressed = false;
            switch (ev)
            {
                case InputEventMouseButton mb:
                    if (mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown or MouseButton.WheelLeft or MouseButton.WheelRight) return false;
                    pressed = mb.Pressed; return true;
                case InputEventKey k:
                    pressed = k.Pressed && !k.Echo; return true;
                case InputEventJoypadButton jb:
                    pressed = jb.Pressed; return true;
                case InputEventScreenTouch st:
                    pressed = st.Pressed; return true;
                default:
                    return false;
            }
        }

        // ===== Hint: small "Click to skip" at bottom-centre, game font, cream text with a dark outline =====

        private static void ShowHint()
        {
            try
            {
                if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null) return;
                _hintLayer = new CanvasLayer { Layer = 120, Name = "FollowThroughSkipHint" };
                _hint = new Label
                {
                    Text = "Click to skip",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    Modulate = new Color(1, 1, 1, 0),
                };
                var font = GameFont(tree.Root);
                if (font != null) _hint.AddThemeFontOverride("font", font);
                var vp = tree.Root.GetVisibleRect().Size;
                int size = Math.Max(18, (int)(vp.Y * 0.024f));
                _hint.AddThemeFontSizeOverride("font_size", size);
                _hint.AddThemeColorOverride("font_color", new Color(0.96f, 0.91f, 0.80f));          // game cream
                _hint.AddThemeColorOverride("font_outline_color", new Color(0.10f, 0.08f, 0.06f, 0.9f));
                _hint.AddThemeConstantOverride("outline_size", Math.Max(4, size / 4));
                _hint.Size = new Vector2(vp.X, size * 2f);
                _hint.Position = new Vector2(0, vp.Y * 0.86f);
                _hintLayer.AddChild(_hint);
                tree.Root.AddChild(_hintLayer);
                var tween = _hint.CreateTween();
                tween.TweenProperty(_hint, "modulate:a", 0.85f, 0.3f);
            }
            catch (Exception ex) { Dbg($"hint failed (ignored): {ex.Message}"); HideHint(); }
        }

        private static void HideHint()
        {
            try { if (_hintLayer != null && GodotObject.IsInstanceValid(_hintLayer)) _hintLayer.QueueFree(); } catch { }
            _hintLayer = null; _hint = null;
        }

        // Borrow the font of a visible game Label so the hint matches the game's typography.
        private static Font? GameFont(Node root)
        {
            try
            {
                var stack = new System.Collections.Generic.Stack<Node>();
                stack.Push(root);
                int visited = 0;
                while (stack.Count > 0 && visited++ < 4000)
                {
                    var n = stack.Pop();
                    if (n is Label l && l.IsVisibleInTree() && l.HasThemeFontOverride("font")) return l.GetThemeFont("font");
                    foreach (var c in n.GetChildren()) stack.Push(c);
                }
            }
            catch { }
            return null;
        }
    }
}
