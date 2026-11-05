using System;
using Godot;

namespace STS2_FollowThrough;

// Skipping a long replay. The skip arms only once the replay has run for skip_after_seconds AND more than
// SkipMinRemainingSec is still left; short replays never add anything and never touch input.
//
// While armed (and for a short grace after a skip) ONE node of ours sits on top: SkipCatcher, a CanvasLayer
// added as the root's last child. Godot sends each event to _Input in reverse tree order, so the root's last
// child sees it before every game node (the game's NButtons act in _Input). On a mouse button / key /
// joypad button event it marks the event handled (Viewport.SetInputAsHandled), so no game node, GUI control
// or hotkey handler sees it, and a press triggers the skip. A full-rect Control with MouseFilter=Stop inside
// it also accepts any GUI-stage click (AcceptEvent), as a second line. Motion and wheel pass through.
// The node is removed when the replay ends, or 0.35 s after a skip (so the release half of the click and a
// double-click are swallowed too).
public static partial class FollowThroughMod
{
    private static bool IsButtonLike(InputEvent? ev, out bool pressed)
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

    // Our single input-eating node. Only exists while the skip is armed (plus the post-skip grace).
    private sealed class SkipCatcher : CanvasLayer
    {
        private bool _logged;

        public override void _Input(InputEvent @event)
        {
            try
            {
                if (!IsButtonLike(@event, out bool pressed)) return;
                if (!_logged) { _logged = true; Dbg($"skip catcher: _Input received {@event.GetType().Name} (consumed)"); }
                GetViewport()?.SetInputAsHandled();
                if (pressed) Skip.Request(@event);
            }
            catch { }
        }
    }

    private static class Skip
    {
        private static bool _running, _armed, _requested;
        private static ulong _t0;
        private static float _total;
        private static SkipCatcher? _catcher;
        private static Label? _hint;

        public static bool Requested => _requested;

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
            AddCatcher();
        }

        public static void Request(InputEvent ev)
        {
            if (!_running || !_armed || _requested) return;
            _requested = true;
            Dbg($"skip requested by {ev.GetType().Name} at {(Time.GetTicksMsec() - _t0) / 1000f:0.00}s; input consumed");
            HideHint();
            RemoveCatcherAfter(0.35f);
        }

        public static void End()
        {
            bool wasSkipped = _requested;
            _running = false; _armed = false;
            HideHint();
            if (!wasSkipped) RemoveCatcherNow();   // a skip removes it itself after the grace
        }

        private static void AddCatcher()
        {
            try
            {
                if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null) return;
                RemoveCatcherNow();
                var catcher = new SkipCatcher { Layer = 1024, Name = "FollowThroughSkipCatcher" };
                var blocker = new Control { MouseFilter = Control.MouseFilterEnum.Stop, Name = "Blocker" };
                blocker.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                blocker.GuiInput += ev =>
                {
                    try
                    {
                        if (!IsButtonLike(ev, out bool pressed)) return;
                        blocker.AcceptEvent();
                        Dbg($"skip catcher: GUI-stage {ev.GetType().Name} accepted");
                        if (pressed) Request(ev);
                    }
                    catch { }
                };
                catcher.AddChild(blocker);
                if (_showSkipHint) { _hint = MakeHint(tree.Root); if (_hint != null) catcher.AddChild(_hint); }
                tree.Root.AddChild(catcher);   // last child of the root: first to receive _Input
                catcher.SetProcessInput(true);
                _catcher = catcher;
                if (_hint != null) _hint.CreateTween().TweenProperty(_hint, "modulate:a", 0.85f, 0.3f);
            }
            catch (Exception ex) { Dbg($"skip catcher failed (skip unavailable): {ex.Message}"); RemoveCatcherNow(); }
        }

        private static void RemoveCatcherAfter(float sec)
        {
            var c = _catcher;
            if (c == null || !GodotObject.IsInstanceValid(c)) return;
            try { c.GetTree().CreateTimer(sec).Timeout += () => { try { if (GodotObject.IsInstanceValid(c)) c.QueueFree(); } catch { } if (ReferenceEquals(_catcher, c)) _catcher = null; }; }
            catch { RemoveCatcherNow(); }
        }

        private static void RemoveCatcherNow()
        {
            try { if (_catcher != null && GodotObject.IsInstanceValid(_catcher)) _catcher.QueueFree(); } catch { }
            _catcher = null; _hint = null;
        }

        private static void HideHint()
        {
            try { if (_hint != null && GodotObject.IsInstanceValid(_hint)) _hint.QueueFree(); } catch { }
            _hint = null;
        }

        // Small "Click to skip" at bottom-centre: game font, cream text, dark outline; fades in over 0.3 s.
        private static Label? MakeHint(Window root)
        {
            try
            {
                var hint = new Label
                {
                    Text = "Click to skip",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    Modulate = new Color(1, 1, 1, 0),
                };
                var font = GameFont(root);
                if (font != null) hint.AddThemeFontOverride("font", font);
                var vp = root.GetVisibleRect().Size;
                int size = Math.Max(18, (int)(vp.Y * 0.024f));
                hint.AddThemeFontSizeOverride("font_size", size);
                hint.AddThemeColorOverride("font_color", new Color(0.96f, 0.91f, 0.80f));
                hint.AddThemeColorOverride("font_outline_color", new Color(0.10f, 0.08f, 0.06f, 0.9f));
                hint.AddThemeConstantOverride("outline_size", Math.Max(4, size / 4));
                hint.Size = new Vector2(vp.X, size * 2f);
                hint.Position = new Vector2(0, vp.Y * 0.86f);
                return hint;
            }
            catch (Exception ex) { Dbg($"hint failed (ignored): {ex.Message}"); return null; }
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
