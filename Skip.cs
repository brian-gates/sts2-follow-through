using System;
using Godot;

namespace STS2_FollowThrough;

// Skipping a long replay. The skip arms only once the replay has run for skip_after_seconds AND more than
// SkipMinRemainingSec is still left; short replays add nothing and never touch input.
//
// While armed (and for a 0.35 s grace after a skip) one temporary catcher sits on top: a CanvasLayer with a
// very high Layer holding a full-rect Control (MouseFilter=Stop, FocusMode=All) that has keyboard focus.
// Input reaches it through the Control's GuiInput SIGNAL (a plain C# event subscription). Mod-defined
// Node subclasses are not registered as Godot scripts, so virtual overrides such as _Input are never called;
// signals work on plain engine nodes.
//   - Mouse: the Stop control is the topmost GUI control everywhere, so every click's GUI stage lands on it;
//     AcceptEvent() marks it handled and nothing under it (and no _UnhandledInput hotkey) sees it. While it
//     covers the screen nothing below is hovered, so buttons that act on hover+release cannot fire either.
//   - Keys / controller buttons: GUI key and joypad events go to the focused control; the catcher grabs focus,
//     so they arrive on the same GuiInput signal and are accepted there.
//   - Fallback (documented, not consuming): if a key/button press never reaches the signal (e.g. a game node
//     handles it in _Input first), the replay loop also polls Input.IsAnythingPressed() on a rising edge while
//     armed and skips; that path cannot consume the event.
// Motion and the mouse wheel pass through untouched.
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
            case InputEventAction a:
                pressed = a.Pressed; return true;
            default:
                return false;
        }
    }

    private static class Skip
    {
        private static bool _running, _armed, _requested, _anyPressedAtArm, _injected, _prearmInjected;
        private static ulong _t0, _armedAt;
        private static float _total;
        private static CanvasLayer? _catcher;
        private static Control? _blocker;
        private static Label? _hint;

        public static bool Requested => _requested;

        public static void Begin(ulong t0, float totalSec)
        {
            End();
            _running = true; _armed = false; _requested = false; _injected = false; _prearmInjected = false;
            _t0 = t0; _total = totalSec;
        }

        // Called every frame by the replay's wait loop.
        public static void Tick()
        {
            if (!_running || _requested) return;
            float elapsed = (Time.GetTicksMsec() - _t0) / 1000f;
            if (!_armed)
            {
                if (_dbgInjectPrearmSec > 0f && !_prearmInjected && elapsed >= _dbgInjectPrearmSec)
                {
                    _prearmInjected = true;
                    Dbg($"DEBUG inject (pre-arm) click at {elapsed:0.00}s; catcher present={_catcher != null}");
                    InjectClick();
                }
                float left = _total - elapsed;
                if (elapsed < _skipAfterSec || left <= SkipMinRemainingSec) return;
                _armed = true;
                _armedAt = Time.GetTicksMsec();
                _anyPressedAtArm = SafeAnyPressed();
                Dbg($"skip ARMED after {elapsed:0.00}s ({left:0.00}s left); hint={_showSkipHint}");
                AddCatcher();
                return;
            }
            // Debug-only injection, shortly after arming.
            if (_dbgInjectAfterArmSec > 0f && !_injected && (Time.GetTicksMsec() - _armedAt) / 1000f >= _dbgInjectAfterArmSec)
            {
                _injected = true;
                if (_dbgInjectKey) { Dbg("DEBUG inject key Enter (armed)"); InjectKey(); }
                else { Dbg("DEBUG inject click at the rewards area (armed)"); InjectClick(); }
                return;
            }
            // Polling fallback: a fresh press that never reached the signal still skips (not consumed).
            bool any = SafeAnyPressed();
            if (any && !_anyPressedAtArm) Request("poll:Input.IsAnythingPressed");
            _anyPressedAtArm = any && _anyPressedAtArm;
        }

        private static bool SafeAnyPressed()
        {
            try { return Input.IsAnythingPressed(); } catch { return false; }
        }

        public static void Request(string via)
        {
            if (!_running || !_armed || _requested) return;
            _requested = true;
            Dbg($"skip requested via {via} at {(Time.GetTicksMsec() - _t0) / 1000f:0.00}s");
            HideHint();
            RemoveCatcherAfter(0.35f);
        }

        public static void End()
        {
            bool wasSkipped = _requested;
            _running = false; _armed = false;
            HideHint();
            if (!wasSkipped) RemoveCatcherNow("replay ended");   // a skip removes it itself after the grace
        }

        private static void OnGuiInput(InputEvent ev)
        {
            try
            {
                if (!IsButtonLike(ev, out bool pressed)) return;
                _blocker?.AcceptEvent();
                Dbg($"skip catcher got {ev.GetType().Name} pressed={pressed} (accepted)");
                if (pressed) Request($"signal:{ev.GetType().Name}");
            }
            catch { }
        }

        private static void AddCatcher()
        {
            try
            {
                if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null) return;
                RemoveCatcherNow("re-arm");
                var layer = new CanvasLayer { Layer = 1024, Name = "FollowThroughSkipCatcher" };
                var blocker = new Control
                {
                    Name = "Blocker",
                    MouseFilter = Control.MouseFilterEnum.Stop,
                    FocusMode = Control.FocusModeEnum.All,
                };
                blocker.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                blocker.GuiInput += OnGuiInput;
                layer.AddChild(blocker);
                if (_showSkipHint) { _hint = MakeHint(tree.Root); if (_hint != null) layer.AddChild(_hint); }
                tree.Root.AddChild(layer);
                blocker.GrabFocus();
                _catcher = layer; _blocker = blocker;
                if (_hint != null) _hint.CreateTween().TweenProperty(_hint, "modulate:a", 0.85f, 0.3f);
                Dbg($"skip catcher ADDED (layer {layer.Layer}, rect {blocker.GetGlobalRect()}, focus={blocker.HasFocus()})");
            }
            catch (Exception ex) { Dbg($"skip catcher failed (skip unavailable): {ex.Message}"); RemoveCatcherNow("error"); }
        }

        private static void RemoveCatcherAfter(float sec)
        {
            var c = _catcher;
            if (c == null || !GodotObject.IsInstanceValid(c)) return;
            try
            {
                c.GetTree().CreateTimer(sec).Timeout += () =>
                {
                    if (ReferenceEquals(_catcher, c)) RemoveCatcherNow($"{sec:0.00}s after skip");
                    else try { if (GodotObject.IsInstanceValid(c)) c.QueueFree(); } catch { }
                };
            }
            catch { RemoveCatcherNow("timer error"); }
        }

        private static void RemoveCatcherNow(string why)
        {
            if (_catcher == null) return;
            try
            {
                if (_blocker != null && GodotObject.IsInstanceValid(_blocker)) _blocker.GuiInput -= OnGuiInput;
                if (GodotObject.IsInstanceValid(_catcher)) _catcher.QueueFree();
            }
            catch { }
            _catcher = null; _blocker = null; _hint = null;
            Dbg($"skip catcher REMOVED ({why})");
        }

        private static void HideHint()
        {
            try { if (_hint != null && GodotObject.IsInstanceValid(_hint)) _hint.QueueFree(); } catch { }
            _hint = null;
        }

        // ===== Debug-only injection (Input.ParseInputEvent: goes through the game's normal input path) =====

        private static Vector2 RewardsAreaPoint()
        {
            // Where the first reward row (gold) appears on the rewards screen.
            var size = (Engine.GetMainLoop() as SceneTree)?.Root?.GetVisibleRect().Size ?? new Vector2(1920, 1080);
            return new Vector2(size.X * 0.5f, size.Y * 0.43f);
        }

        private static void InjectClick()
        {
            var p = RewardsAreaPoint();
            foreach (bool down in new[] { true, false })
                Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = down, Position = p, GlobalPosition = p, ButtonMask = down ? MouseButtonMask.Left : 0 });
        }

        private static void InjectKey()
        {
            foreach (bool down in new[] { true, false })
                Input.ParseInputEvent(new InputEventKey { Keycode = Key.Enter, PhysicalKeycode = Key.Enter, Pressed = down });
        }

        // ===== Hint: small "Click to skip" at bottom-centre, game font, cream text, dark outline =====

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
