// ParaWorld-style RTS camera: pan, zoom (pitch follows zoom), rotate, camera shakes - and its controls
// (GameCameraModule.usl as the remake has them):
//   arrows / screen edge (4 px)   pan, at dist x 1.1 m/s
//   wheel                         zoom (x 0.88 / 1.12 per step), PageUp / PageDown zoom continuously (60 m/s)
//   middle drag                   pan;  middle + right drag or Alt + left drag: rotate;  Alt + right drag: pan
//   End                           default view (yaw 0, distance 70)
// yaw 0 looks north (-z); the eye stands at (x + sin(yaw) cos(pitch) d, h + sin(pitch) d, z + cos(yaw) cos(pitch) d).
//
// Ported from remake/src/engine/renderer.js RTSCamera and the camera parts of remake/src/ui/input.js (onDown,
// onMove, onUp, onWheel, update, the PageUp / PageDown / End keys) (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using Godot;

namespace PwRemake.View.World;

public sealed class RtsCamera
{
    public readonly Camera3D Camera;
    public Func<double, double, double> HeightAt;
    /// <summary>|x|, |z| at most this (the JS: size / 2 - 8)</summary>
    public double Bounds;
    public double X, Z, Yaw;
    public double Dist = 70, TDist = 70;
    public double Min = 25, Max = 150;
    /// <summary>the point the camera looks at (after Update)</summary>
    public Vector3 Target;
    readonly List<(double t, double dur, double s, double fade)> _shakes = new();
    bool _shakeOn;
    readonly Random _rnd = new();

    public RtsCamera(Camera3D camera, Func<double, double, double> heightAt, double bounds)
    {
        Camera = camera; HeightAt = heightAt; Bounds = bounds;
    }

    /// <summary>
    /// a camera shake (GameCameraModule.usl ShakeCameraTheGundlachStyle): duration s, strength, fade-out % of the
    /// duration, the event's position, its range (60). Dropped beyond the range from the point the camera looks at;
    /// full strength within 15 m, then less down to nothing at 60 m.
    /// </summary>
    public void AddShake(double dur, double strength, double fade, Vector3? pos, double range = 60)
    {
        var c = Target;
        double d = pos is Vector3 p ? (p - c).Length() : 0;
        if (d > range) return;
        double f = d < 15 ? 1 : Math.Max(0, 1 - (d - 15) / 45);
        if (f > 0 && dur > 0) _shakes.Add((0, dur, strength * f, Math.Min(100, Math.Max(1, fade)) / 100));
    }

    // the shakes' strength now (full until the last fade-out share of their time, then down to 0), and their ageing
    double ShakeStrength(double dt)
    {
        double S = 0;
        for (int i = 0; i < _shakes.Count; i++)
        {
            var k = _shakes[i];
            k.t += dt;
            _shakes[i] = k;
            double q = Math.Min(1, k.t / k.dur);
            S += q < 1 - k.fade ? k.s : k.s * Math.Max(0, (1 - q) / k.fade);
        }
        _shakes.RemoveAll(k => k.t >= k.dur);
        return S;
    }

    public void Pan(double dx, double dz)
    {
        double s = Math.Sin(Yaw), c = Math.Cos(Yaw);
        X += dx * c - dz * s;
        Z += -dx * s - dz * c;
    }

    public void Update(double dt)
    {
        double b = Bounds;
        X = Math.Max(-b, Math.Min(b, X));
        Z = Math.Max(-b, Math.Min(b, Z));
        Dist += (TDist - Dist) * Math.Min(1, dt * 10);
        double t = (Dist - Min) / (Max - Min);
        double pitch = 0.72 + 0.36 * t;
        double h = HeightAt(X, Z);
        double s = Math.Sin(Yaw), c = Math.Cos(Yaw);
        var eye = new Vector3((float)(X + s * Math.Cos(pitch) * Dist), (float)(h + Math.Sin(pitch) * Dist), (float)(Z + c * Math.Cos(pitch) * Dist));
        // shaking: every other frame the eye and the point it looks at move together by a random step - up and down by
        // up to 0.03 m per strength, sideways half of that - and back the next frame (GameCameraModule.usl:714-749)
        double S = _shakes.Count > 0 ? ShakeStrength(dt) : 0;
        double ox = 0, oy = 0, oz = 0;
        _shakeOn = !_shakeOn;
        if (S > 0 && _shakeOn)
        {
            double a = 0.03 * S;
            double R() => _rnd.NextDouble() * 2 - 1;
            ox = R() * a / 2; oz = R() * a / 2; oy = R() * a;
        }
        var look = new Vector3((float)(X + ox), (float)(h + oy), (float)(Z + oz));
        eye += new Vector3((float)ox, (float)oy, (float)oz);
        Camera.GlobalTransform = new Transform3D(Basis.Identity, eye).LookingAt(look, Vector3.Up);
        Target = new Vector3((float)X, (float)h, (float)Z);
    }
}

/// <summary>The camera's mouse and keyboard controls (input.js), as a node that feeds an RtsCamera.</summary>
public partial class RtsCameraInput : Node
{
    public RtsCamera Cam = null!;
    public bool EdgeScroll = true;
    bool _pan, _rot, _rdown, _inside;
    Vector2 _p, _mouse;
    readonly HashSet<Key> _keys = new();

    /// <summary>the mouse position for the screen edge from every motion, also those an interface control takes: a
    /// position kept from the last unhandled motion stayed at the edge when the mouse went onto a panel from there, and
    /// the camera scrolled on for good</summary>
    public override void _Input(InputEvent e)
    {
        if (e is InputEventMouse m) { _mouse = m.Position; _inside = true; }
        // a release over an interface control still ends a drag (else the camera turned / panned on with every motion)
        if (e is InputEventMouseButton { Pressed: false } r)
        {
            if (r.ButtonIndex == MouseButton.Middle) { _rot = false; _pan = false; }
            else if (r.ButtonIndex == MouseButton.Right) { _rdown = false; _rot = false; _pan = false; }
            else if (r.ButtonIndex == MouseButton.Left && _rot) _rot = false;
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        var cam = Cam;
        if (cam == null) return;
        switch (e)
        {
            case InputEventMouseButton mb:
                _mouse = mb.Position; _inside = true;
                if (mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
                {
                    if (!mb.Pressed) break;
                    double sign = mb.ButtonIndex == MouseButton.WheelDown ? 1 : -1;
                    cam.TDist = Math.Max(cam.Min, Math.Min(cam.Max, cam.TDist * (1 + sign * 0.12)));
                    break;
                }
                if (mb.Pressed)
                {
                    // camera (GameCameraModule.usl): middle drag pans, middle + right or Alt + left drag turns, Alt + right drag pans
                    if (mb.ButtonIndex == MouseButton.Middle) { if (_rdown) { _rdown = false; _rot = true; } else _pan = true; _p = mb.Position; }
                    else if (mb.ButtonIndex == MouseButton.Right)
                    {
                        if (_pan) { _pan = false; _rot = true; _p = mb.Position; }
                        else if (mb.AltPressed) { _pan = true; _p = mb.Position; }
                        else _rdown = true;
                    }
                    else if (mb.ButtonIndex == MouseButton.Left && mb.AltPressed) { _rot = true; _p = mb.Position; }
                }
                else
                {
                    if (mb.ButtonIndex == MouseButton.Middle) { _rot = false; _pan = false; }
                    else if (mb.ButtonIndex == MouseButton.Right) { _rdown = false; _rot = false; _pan = false; }
                    else if (mb.ButtonIndex == MouseButton.Left && _rot) _rot = false;
                }
                break;
            case InputEventMouseMotion mm:
                _mouse = mm.Position; _inside = true;
                if (_rot) { cam.Yaw -= (mm.Position.X - _p.X) * 0.006; _p = mm.Position; }
                else if (_pan) { double k = cam.Dist * 0.0016; cam.Pan(-(mm.Position.X - _p.X) * k, (mm.Position.Y - _p.Y) * k); _p = mm.Position; }
                break;
            case InputEventKey k:
                if (k.Pressed) _keys.Add(k.Keycode); else _keys.Remove(k.Keycode);
                if (k.Pressed && !k.Echo && k.Keycode == Key.End) { cam.Yaw = 0; cam.TDist = 70; }     // ResetCameraToDefaultView
                break;
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMMouseExit) _inside = false;
        else if (what == NotificationWMMouseEnter) _inside = true;
        else if (what == NotificationApplicationFocusOut) _keys.Clear();
    }

    /// <summary>per frame: keys and the screen edge (input.js update)</summary>
    public void Step(double dt)
    {
        var cam = Cam;
        double sp = cam.Dist * 1.1 * dt;
        double dx = 0, dz = 0;
        if (_keys.Contains(Key.Left)) dx -= 1;
        if (_keys.Contains(Key.Right)) dx += 1;
        if (_keys.Contains(Key.Up)) dz += 1;
        if (_keys.Contains(Key.Down)) dz -= 1;
        var vp = GetViewport();
        if (EdgeScroll && _inside && !_rot && !_pan && vp != null && DisplayServer.WindowIsFocused())
        {
            const float E = 4;
            var size = vp.GetVisibleRect().Size;
            if (_mouse.X <= E) dx -= 1;
            if (_mouse.X >= size.X - E - 1) dx += 1;
            if (_mouse.Y <= E) dz += 1;
            if (_mouse.Y >= size.Y - E - 1) dz -= 1;
        }
        if (dx != 0 || dz != 0) cam.Pan(dx * sp, dz * sp);
        if (_keys.Contains(Key.Pageup)) cam.TDist = Math.Max(cam.Min, cam.TDist - 60 * dt);
        if (_keys.Contains(Key.Pagedown)) cam.TDist = Math.Min(cam.Max, cam.TDist + 60 * dt);
    }
}
