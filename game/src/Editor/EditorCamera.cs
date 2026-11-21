// The map editor's camera: the game's RTS camera (it looks at a point on the ground from a distance, north up at yaw 0,
// docs/TERRAIN.md "Camera") with a free zoom (8 .. 600 m instead of 25 .. 150), a free tilt (the game ties the pitch to
// the zoom) and the editor's keys:
//   W A S D / arrows / screen edge   pan (Shift: three times as fast)
//   wheel, PageUp / PageDown         zoom
//   Q / E                            turn left / right
//   R / F                            tilt up (towards the horizon) / down (towards straight down)
//   middle drag                      pan;  right drag: turn (sideways) and tilt (up / down);  Alt + left drag: turn
//   Home                             the default view (north up, 45 degrees, 120 m)
// The keys work only while no text field has the focus (the dialogs' name fields).
using System;
using System.Collections.Generic;
using Godot;

namespace PwRemake.Editor;

public sealed class EditorCamera
{
    public readonly Camera3D Camera;
    public Func<double, double, double> HeightAt;
    /// <summary>|x|, |z| of the point looked at at most this</summary>
    public double Bounds;
    public double X, Z, Yaw, Pitch = 0.8, Dist = 120, TDist = 120;
    public const double MinDist = 8, MaxDist = 600, MinPitch = 0.15, MaxPitch = 1.55;
    /// <summary>the point looked at (after Update)</summary>
    public Vector3 Target;

    public EditorCamera(Camera3D camera, Func<double, double, double> heightAt, double bounds) { Camera = camera; HeightAt = heightAt; Bounds = bounds; }

    /// <summary>moves the point looked at by (dx right, dz forward) metres of the screen's directions</summary>
    public void Pan(double dx, double dz)
    {
        double s = Math.Sin(Yaw), c = Math.Cos(Yaw);
        X += dx * c - dz * s;
        Z += -dx * s - dz * c;
    }

    public void Update(double dt)
    {
        X = Math.Clamp(X, -Bounds, Bounds); Z = Math.Clamp(Z, -Bounds, Bounds);
        Pitch = Math.Clamp(Pitch, MinPitch, MaxPitch);
        TDist = Math.Clamp(TDist, MinDist, MaxDist);
        Dist += (TDist - Dist) * Math.Min(1, dt * 10);
        double h = HeightAt(X, Z);
        double s = Math.Sin(Yaw), c = Math.Cos(Yaw);
        var eye = new Vector3((float)(X + s * Math.Cos(Pitch) * Dist), (float)(h + Math.Sin(Pitch) * Dist), (float)(Z + c * Math.Cos(Pitch) * Dist));
        // (not below the ground under the eye)
        double gh = HeightAt(eye.X, eye.Z) + 2;
        if (eye.Y < gh) eye.Y = (float)gh;
        Target = new Vector3((float)X, (float)h, (float)Z);
        Camera.GlobalTransform = new Transform3D(Basis.Identity, eye).LookingAt(Target, Math.Abs(Pitch - Math.PI / 2) < 1e-3 ? new Vector3((float)-s, 0, (float)-c) : Vector3.Up);
    }

    public void Reset() { Yaw = 0; Pitch = 0.8; TDist = 120; }
}

/// <summary>the editor camera's mouse and keys (a node: it sees the input the interface did not take)</summary>
public partial class EditorCameraInput : Node
{
    public EditorCamera Cam = null!;
    public bool EdgeScroll = true;
    bool _pan, _rot, _inside;
    Vector2 _p, _mouse;
    readonly HashSet<Key> _keys = new();

    /// <summary>a text field has the focus: the letters are typed, not camera keys</summary>
    bool Typing => GetViewport()?.GuiGetFocusOwner() is LineEdit or TextEdit or SpinBox;

    public override void _Input(InputEvent e)
    {
        if (e is InputEventMouse m) { _mouse = m.Position; _inside = true; }
        // a release over a panel still ends a drag
        if (e is InputEventMouseButton { Pressed: false } r && r.ButtonIndex is MouseButton.Middle or MouseButton.Right or MouseButton.Left) { _rot = false; _pan = false; }
        if (e is InputEventKey { Pressed: false } k) _keys.Remove(k.Keycode);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        var cam = Cam;
        if (cam == null) return;
        switch (e)
        {
            case InputEventMouseButton mb:
                if (mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
                {
                    if (mb.Pressed) cam.TDist *= mb.ButtonIndex == MouseButton.WheelDown ? 1.15 : 1 / 1.15;
                    break;
                }
                if (!mb.Pressed) break;
                if (mb.ButtonIndex == MouseButton.Middle) { _pan = true; _p = mb.Position; }
                else if (mb.ButtonIndex == MouseButton.Right || (mb.ButtonIndex == MouseButton.Left && mb.AltPressed)) { _rot = true; _p = mb.Position; }
                break;
            case InputEventMouseMotion mm:
                if (_rot)
                {
                    cam.Yaw -= (mm.Position.X - _p.X) * 0.006;
                    cam.Pitch += (mm.Position.Y - _p.Y) * 0.004;
                    _p = mm.Position;
                }
                else if (_pan)
                {
                    double k = cam.Dist * 0.0016;
                    cam.Pan(-(mm.Position.X - _p.X) * k, (mm.Position.Y - _p.Y) * k);
                    _p = mm.Position;
                }
                break;
            case InputEventKey k when !Typing:
                if (k.Pressed && !k.CtrlPressed) _keys.Add(k.Keycode);
                if (k.Pressed && !k.Echo && k.Keycode == Key.Home) cam.Reset();
                break;
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMMouseExit) _inside = false;
        else if (what == NotificationWMMouseEnter) _inside = true;
        else if (what == NotificationApplicationFocusOut) _keys.Clear();
    }

    /// <summary>is the mouse inside the window (for the screen edge)</summary>
    public bool MouseInside => _inside;

    /// <summary>per frame: the held keys and the screen edge</summary>
    public void Step(double dt)
    {
        var cam = Cam;
        if (Typing) _keys.Clear();
        bool fast = Input.IsKeyPressed(Key.Shift);
        double sp = Math.Max(20, cam.Dist) * 1.1 * dt * (fast ? 3 : 1);
        double dx = 0, dz = 0;
        if (_keys.Contains(Key.Left) || _keys.Contains(Key.A)) dx -= 1;
        if (_keys.Contains(Key.Right) || _keys.Contains(Key.D)) dx += 1;
        if (_keys.Contains(Key.Up) || _keys.Contains(Key.W)) dz += 1;
        if (_keys.Contains(Key.Down) || _keys.Contains(Key.S)) dz -= 1;
        var vp = GetViewport();
        if (EdgeScroll && _inside && !_rot && !_pan && vp != null && DisplayServer.WindowIsFocused())
        {
            const float E = 3;
            var size = vp.GetVisibleRect().Size;
            if (_mouse.X <= E) dx -= 1;
            if (_mouse.X >= size.X - E - 1) dx += 1;
            if (_mouse.Y <= E) dz += 1;
            if (_mouse.Y >= size.Y - E - 1) dz -= 1;
        }
        if (dx != 0 || dz != 0) cam.Pan(dx * sp, dz * sp);
        if (_keys.Contains(Key.Q)) cam.Yaw += 1.5 * dt;
        if (_keys.Contains(Key.E)) cam.Yaw -= 1.5 * dt;
        if (_keys.Contains(Key.R)) cam.Pitch -= 0.8 * dt;
        if (_keys.Contains(Key.F)) cam.Pitch += 0.8 * dt;
        if (_keys.Contains(Key.Pageup)) cam.TDist /= 1 + 1.5 * dt;
        if (_keys.Contains(Key.Pagedown)) cam.TDist *= 1 + 1.5 * dt;
    }

    /// <summary>a camera drag is going on (the brush waits)</summary>
    public bool Dragging => _rot || _pan;
}
