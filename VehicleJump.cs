using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using GTA;
using GTA.Native;
using GTA.Math;
using GTA.UI;
using System.Text.RegularExpressions;

public class VehicleJump : Script
{
    // ---------------- CONFIG ----------------
    private const Keys JumpKey = Keys.Space;
    private const Keys CycleModeKey = Keys.F6;

    private const int IdleIntervalMs = 1000;
    private const int ActiveIntervalMs = 50;
    private const int DefaultCooldownMs = 600;
    private const int HoldApplyIntervalMs = 120;
    private const float DefaultJumpStrength = 6.5f;
    private const float DefaultForwardBoost = 1.5f;
    private const string IniFileName = "scripts\\VehicleJump.ini";

    private const int StartupDelayMs = 3000; // Game.GameTime units (ms)
    // smoothing config (0..1, lớn = nhanh theo target, nhỏ = mượt hơn)
    private const float HoldSmoothingFactor = 0.22f;
    // ----------------------------------------

    private enum JumpMode { Off = 0, Press = 1, Hold = 2 }

    private class JumpEntry
    {
        public uint ModelHash;
        public string Name;
        public JumpMode Mode;
        public float JumpStrength;
        public float ForwardBoost;
        public int CooldownMs;
        public int HoldIntervalMs;
        public int LastJumpTimeMs;

        public JumpEntry(uint hash, string name)
        {
            ModelHash = hash;
            Name = name;
            Mode = JumpMode.Press;
            JumpStrength = DefaultJumpStrength;
            ForwardBoost = DefaultForwardBoost;
            CooldownMs = DefaultCooldownMs;
            HoldIntervalMs = HoldApplyIntervalMs;
            LastJumpTimeMs = 0;
        }
    }

    private readonly Dictionary<uint, JumpEntry> _entries = new Dictionary<uint, JumpEntry>(16);

    // runtime state
    private JumpEntry _activeEntry = null;
    private bool _featureActive = false;         // true when player sits in a registered vehicle (driver)
    private bool _holdKeyDown = false;           // true while keyboard key is pressed (KeyDown/KeyUp)
    private bool _isHoldActive = false;          // true while we are actively applying Hold-periodic impulses
    private int _lastHoldApplied = 0;            // Game.GameTime of last hold application
    private int _currentVehicleHandle = 0;

    // smoothing state: current applied force (world-relative but built from vehicle forward/up)
    private Vector3 _currentAppliedForce = new Vector3(0f, 0f, 0f);

    private bool _startupUnlocked = false;
    private int _startupUnlockTime = 0;

    public VehicleJump()
    {
        Interval = IdleIntervalMs;
        SetupDefaults();
        LoadIni();

        // schedule startup unlock (use Game.GameTime)
        _startupUnlockTime = Game.GameTime + StartupDelayMs;

        Tick += OnTick;
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;

        // quiet startup
    }

    private void SetupDefaults()
    {
        // Default configured vehicle: Future Shock Deathbike (hex 0x93F09558)
        uint deathbikeHash = 0x93F09558u;
        var e = new JumpEntry(deathbikeHash, "Future Shock Deathbike");
        e.Mode = JumpMode.Press;
        e.JumpStrength = 7.0f;
        e.ForwardBoost = 1.5f;
        e.CooldownMs = 700;
        e.HoldIntervalMs = 120;
        _entries[deathbikeHash] = e;
    }

    private void LoadIni()
    {
        try
        {
            if (!File.Exists(IniFileName)) return;

            // Load ScriptSettings (dùng để đọc giá trị)
            var cfg = ScriptSettings.Load(IniFileName);

            // 1) Quét file .ini để tìm tất cả section dạng [vehicle_0xXXXXXXXX]
            //    và thêm JumpEntry mặc định cho các vehicle chưa có trong _entries.
            try
            {
                string[] lines = File.ReadAllLines(IniFileName);
                var sectionRegex = new Regex(@"^\s*\[vehicle_0x([0-9A-Fa-f]{1,8})\]\s*$", RegexOptions.Compiled);

                foreach (var line in lines)
                {
                    var m = sectionRegex.Match(line);
                    if (m.Success)
                    {
                        string hexStr = m.Groups[1].Value;
                        uint hash;
                        try
                        {
                            hash = Convert.ToUInt32(hexStr, 16);
                        }
                        catch
                        {
                            continue; // skip invalid
                        }

                        if (!_entries.ContainsKey(hash))
                        {
                            string name = $"Vehicle 0x{hash.ToString("X8")}";
                            _entries[hash] = new JumpEntry(hash, name);
                        }
                    }
                }
            }
            catch
            {
                // nếu việc quét thất bại thì vẫn tiếp tục (để không phá vỡ chức năng)
            }

            // 2) Bây giờ load các giá trị cấu hình cho mọi entry có trong _entries
            foreach (var kv in _entries)
            {
                // chuẩn hoá tên section dạng vehicle_0xXXXXXXXX (hex viết hoa)
                string hex = "0x" + kv.Key.ToString("X8");
                string sectionUpperHex = $"vehicle_{hex}";
                string sectionLowerHex = $"vehicle_0x{kv.Key.ToString("x8")}";
                string sectionDecimal = $"vehicle_{kv.Key}";

                // Thử đọc dùng section viết hoa, nếu không có thì thử section viết thường rồi decimal
                // (ScriptSettings.GetValue trả về default nếu không tìm thấy, nên không ném ngoại lệ)
                try
                {
                    // Prefer uppercase-hex section first
                    kv.Value.Mode = (JumpMode)Math.Max(0, Math.Min(2,
                        cfg.GetValue(sectionUpperHex, "Mode", (int)kv.Value.Mode)));

                    kv.Value.JumpStrength =
                        cfg.GetValue(sectionUpperHex, "JumpStrength", kv.Value.JumpStrength);

                    kv.Value.ForwardBoost =
                        cfg.GetValue(sectionUpperHex, "ForwardBoost", kv.Value.ForwardBoost);

                    kv.Value.CooldownMs =
                        cfg.GetValue(sectionUpperHex, "CooldownMs", kv.Value.CooldownMs);

                    kv.Value.HoldIntervalMs =
                        cfg.GetValue(sectionUpperHex, "HoldIntervalMs", kv.Value.HoldIntervalMs);

                    // If the file used lowercase hex section names, try to read them as well to override
                    // (only needed if values remain equal to defaults — safe to call anyway)
                    kv.Value.Mode = (JumpMode)Math.Max(0, Math.Min(2,
                        cfg.GetValue(sectionLowerHex, "Mode", (int)kv.Value.Mode)));

                    kv.Value.JumpStrength =
                        cfg.GetValue(sectionLowerHex, "JumpStrength", kv.Value.JumpStrength);

                    kv.Value.ForwardBoost =
                        cfg.GetValue(sectionLowerHex, "ForwardBoost", kv.Value.ForwardBoost);

                    kv.Value.CooldownMs =
                        cfg.GetValue(sectionLowerHex, "CooldownMs", kv.Value.CooldownMs);

                    kv.Value.HoldIntervalMs =
                        cfg.GetValue(sectionLowerHex, "HoldIntervalMs", kv.Value.HoldIntervalMs);
                }
                catch
                {
                    // as a last resort try decimal-named section
                    try
                    {
                        kv.Value.Mode = (JumpMode)Math.Max(0, Math.Min(2,
                            cfg.GetValue(sectionDecimal, "Mode", (int)kv.Value.Mode)));

                        kv.Value.JumpStrength =
                            cfg.GetValue(sectionDecimal, "JumpStrength", kv.Value.JumpStrength);

                        kv.Value.ForwardBoost =
                            cfg.GetValue(sectionDecimal, "ForwardBoost", kv.Value.ForwardBoost);

                        kv.Value.CooldownMs =
                            cfg.GetValue(sectionDecimal, "CooldownMs", kv.Value.CooldownMs);

                        kv.Value.HoldIntervalMs =
                            cfg.GetValue(sectionDecimal, "HoldIntervalMs", kv.Value.HoldIntervalMs);
                    }
                    catch
                    {
                        // keep defaults
                    }
                }
            }
        }
        catch
        {
            // keep defaults if anything fails
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!_startupUnlocked) return;

        try
        {
            if (e.KeyCode == CycleModeKey)
            {
                CycleModeForCurrentVehicle();
                return;
            }

            if (e.KeyCode == JumpKey)
            {
                // mark key down
                _holdKeyDown = true;

                // only respond if feature active and entry present
                if (!_featureActive || _activeEntry == null) return;

                // PRESS mode: apply on KeyDown (respect cooldown)
                if (_activeEntry.Mode == JumpMode.Press)
                {
                    TryApplyJump(true);
                }
                else if (_activeEntry.Mode == JumpMode.Hold)
                {
                    // For Hold: start a fresh hold sequence only when key is pressed
                    if (!_isHoldActive)
                    {
                        _isHoldActive = true;

                        // --- REPLACED CALL: apply an immediate hold impulse safely ---
                        var v = Game.Player?.Character?.CurrentVehicle;
                        ApplyHoldImmediate(v, _activeEntry);

                        _lastHoldApplied = Game.GameTime;
                    }
                }
            }
        }
        catch { /* swallow to avoid crash */ }
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (!_startupUnlocked) return;

        try
        {
            if (e.KeyCode == JumpKey)
            {
                // mark key release
                _holdKeyDown = false;

                // For hold mode, stop the active hold sequence immediately when released
                _isHoldActive = false;

                // do not instantly zero _currentAppliedForce; let smoothing decay it to zero over a few ticks
                // but reset hold apply timer so next hold applies immediate nudge
                _lastHoldApplied = 0;
            }
        }
        catch { }
    }

    private void OnTick(object sender, EventArgs e)
    {
        try
        {
            if (!_startupUnlocked)
            {
                if (Game.GameTime >= _startupUnlockTime)
                {
                    _startupUnlocked = true;
                    // quiet ready
                }
                return;
            }

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead)
            {
                DeactivateFeatureIfNeeded();
                return;
            }

            // If feature is not active, do cheap detection to activate only when player sits in registered vehicle (driver)
            if (!_featureActive)
            {
                if (!player.IsInVehicle()) return;

                Vehicle v = player.CurrentVehicle;
                if (v == null || !v.Exists()) return;

                int driverHandle = Function.Call<int>(Hash.GET_PED_IN_VEHICLE_SEAT, v.Handle, (int)VehicleSeat.Driver);
                if (driverHandle != player.Handle) return;

                uint hash = (uint)v.Model.Hash;
                if (!_entries.TryGetValue(hash, out JumpEntry entry)) return;

                ActivateFeatureForVehicle(entry, v);
                return;
            }

            // Feature active: minimal per-tick checks and handle hold periodic smoothing and force application
            Vehicle currVeh = player.CurrentVehicle;
            if (currVeh == null || !currVeh.Exists())
            {
                DeactivateFeatureIfNeeded();
                return;
            }

            if (currVeh.Handle != _currentVehicleHandle)
            {
                DeactivateFeatureIfNeeded();
                return;
            }

            int driverNow = Function.Call<int>(Hash.GET_PED_IN_VEHICLE_SEAT, currVeh.Handle, (int)VehicleSeat.Driver);
            if (driverNow != player.Handle)
            {
                DeactivateFeatureIfNeeded();
                return;
            }

            // Build target force depending on hold state (target force in world-space)
            Vector3 targetForce = new Vector3(0f, 0f, 0f);

            if (_activeEntry != null && _activeEntry.Mode == JumpMode.Hold && _isHoldActive)
            {
                // compute target force (absolute per-application magnitude chosen experimentally)
                // we slightly increase hold multipliers for more lift while smoothing prevents jerks
                float upBase = _activeEntry.JumpStrength;
                float fwdBase = _activeEntry.ForwardBoost;

                // tuned multipliers:
                float upMulHoldTarget = 0.26f;   // giảm mạnh lift
                float fwdMulHoldTarget = 0.14f;  // forward nhẹ lại

                Vector3 forward = currVeh.ForwardVector;
                float fx = forward.X * fwdBase * fwdMulHoldTarget;
                float fy = forward.Y * fwdBase * fwdMulHoldTarget;
                float fz = upBase * upMulHoldTarget;

                // clamp
                const float MAX_SINGLE_FORCE = 80f;
                fx = Math.Max(Math.Min(fx, MAX_SINGLE_FORCE), -MAX_SINGLE_FORCE);
                fy = Math.Max(Math.Min(fy, MAX_SINGLE_FORCE), -MAX_SINGLE_FORCE);
                fz = Math.Max(Math.Min(fz, MAX_SINGLE_FORCE), -MAX_SINGLE_FORCE);

                targetForce = new Vector3(fx, fy, fz);

                // If it's time for a periodic apply (respect HoldIntervalMs), we will nudge currentAppliedForce toward target
                // and call APPLY_FORCE using the smoothed value below.
                if (Game.GameTime - _lastHoldApplied >= _activeEntry.HoldIntervalMs)
                {
                    // update last tick for periodic schedule
                    _lastHoldApplied = Game.GameTime;
                    // (actual force applied below uses _currentAppliedForce after smoothing)
                }
                else
                {
                    // Even if not at interval boundary, continue smoothing towards target so we have continuous movement.
                    // (We still call APPLY with smoothed force below every tick to avoid stepped behavior.)
                }
            }
            else
            {
                // No hold target: targetForce remains zero so smoothing will decay currentAppliedForce -> 0
                targetForce = new Vector3(0f, 0f, 0f);
            }

            // Smooth currentAppliedForce toward targetForce
            _currentAppliedForce = Lerp(_currentAppliedForce, targetForce, HoldSmoothingFactor);

            // If smoothed force magnitude is very small, skip calling native to save cost
            if (_currentAppliedForce.Length() > 0.0005f)
            {
                // apply the smoothed force each tick (APPLY_FORCE_TO_ENTITY accepts instantaneous force; small repeated forces emulate continuous thrust)
                ApplyForceToVehicle(currVeh, _currentAppliedForce);
            }
            else
            {
                // small enough -> zero
                _currentAppliedForce = new Vector3(0f, 0f, 0f);
            }
        }
        catch { /* swallow to avoid crash */ }
    }

    private void ActivateFeatureForVehicle(JumpEntry entry, Vehicle v)
    {
        _featureActive = true;
        _activeEntry = entry;
        _currentVehicleHandle = v.Handle;
        _holdKeyDown = false;
        _isHoldActive = false;
        _lastHoldApplied = 0;
        _currentAppliedForce = new Vector3(0f, 0f, 0f);
        Interval = ActiveIntervalMs;
        // quiet activation
    }

    private void DeactivateFeatureIfNeeded()
    {
        if (!_featureActive) return;

        _featureActive = false;
        _activeEntry = null;
        _holdKeyDown = false;
        _isHoldActive = false;
        _currentVehicleHandle = 0;
        _lastHoldApplied = 0;
        _currentAppliedForce = new Vector3(0f, 0f, 0f);
        Interval = IdleIntervalMs;
        // quiet deactivation
    }

    // TryApplyJump called only for Press mode (immediate via KeyDown)
    private void TryApplyJump(bool isKeyDownEvent)
    {
        if (!_featureActive || _activeEntry == null) return;

        Ped player = Game.Player.Character;
        if (player == null || !player.Exists() || player.IsDead) return;

        Vehicle v = player.CurrentVehicle;
        if (v == null || !v.Exists()) return;
        if (v.Handle != _currentVehicleHandle) return;

        var entry = _activeEntry;
        if (entry.Mode == JumpMode.Off) return;

        if (entry.Mode == JumpMode.Press)
        {
            if (!isKeyDownEvent) return;

            if (Game.GameTime - entry.LastJumpTimeMs < entry.CooldownMs) return;

            // Press: stronger single impulse (immediate)
            ApplyPressImpulse(v, entry);
            entry.LastJumpTimeMs = Game.GameTime;
        }
    }

    private void ApplyPressImpulse(Vehicle v, JumpEntry entry)
    {
        try
        {
            if (v == null || !v.Exists() || v.IsDead) return;
            if (v.IsInWater) return;

            // Press uses a stronger one-shot force than hold target
            float upBase = entry.JumpStrength;
            float fwdBase = entry.ForwardBoost;

            float upMulPress = 1.0f;   // full lift for press
            float fwdMulPress = 1.0f;

            Vector3 forward = v.ForwardVector;
            float fx = forward.X * fwdBase * fwdMulPress;
            float fy = forward.Y * fwdBase * fwdMulPress;
            float fz = upBase * upMulPress;

            const float MAX_SINGLE_FORCE = 120f;
            fx = Math.Max(Math.Min(fx, MAX_SINGLE_FORCE), -MAX_SINGLE_FORCE);
            fy = Math.Max(Math.Min(fy, MAX_SINGLE_FORCE), -MAX_SINGLE_FORCE);
            fz = Math.Max(Math.Min(fz, MAX_SINGLE_FORCE), -MAX_SINGLE_FORCE);

            Vector3 force = new Vector3(fx, fy, fz);

            ApplyForceToVehicle(v, force);
            PlaySoundAt("AVENGER_LAUNCH", "VEHICLE_GENERAL");
        }
        catch { }
    }

    /// <summary>
    /// ApplyForceToVehicle: wrapper to call APPLY_FORCE_TO_ENTITY with safety checks and fallback.
    /// </summary>
    private void ApplyForceToVehicle(Vehicle v, Vector3 force)
    {
        try
        {
            if (v == null || !v.Exists()) return;
            if (v.IsDead) return;
            if (v.IsInWater) return;

            // cap forces again (defensive)
            const float CAP = 200f;
            float fx = Math.Max(Math.Min(force.X, CAP), -CAP);
            float fy = Math.Max(Math.Min(force.Y, CAP), -CAP);
            float fz = Math.Max(Math.Min(force.Z, CAP), -CAP);

            // APPLY_FORCE_TO_ENTITY parameters:
            // (entity, forceType, x, y, z, offX, offY, offZ, boneIndex, isDirectionRel, isForceRel, p11, p12, p13)
            // We use relative direction/force and apply at center (offset 0).
            try
            {
                Function.Call(Hash.APPLY_FORCE_TO_ENTITY, v.Handle,
                    1,           // forceType (relative)
                    fx, fy, fz,  // force vector
                    0.0f, 0.0f, 0.0f, // offset
                    0,           // boneIndex
                    true,        // isDirectionRel
                    true,        // isForceRel
                    true,        // p11
                    false,       // p12
                    true         // p13
                );
            }
            catch
            {
                // fallback gentle velocity tweak (rare)
                Vector3 vel = v.Velocity;
                Vector3 newVel = new Vector3(vel.X + fx * 0.02f, vel.Y + fy * 0.02f, vel.Z + fz * 0.02f);
                if (newVel.Z > 30f) newVel.Z = 30f;
                Function.Call(Hash.SET_ENTITY_VELOCITY, v.Handle, newVel.X, newVel.Y, newVel.Z);
            }
        }
        catch { /* swallow */ }
    }

    private void CycleModeForCurrentVehicle()
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || !player.IsInVehicle()) return;

            Vehicle v = player.CurrentVehicle;
            if (v == null || !v.Exists()) return;

            uint hash = (uint)v.Model.Hash;
            if (!_entries.TryGetValue(hash, out JumpEntry entry)) return;

            // Cycle the mode
            entry.Mode = (JumpMode)(((int)entry.Mode + 1) % 3);

            // Short Vietnamese messages (exact 3 states)
            string msg;
            switch (entry.Mode)
            {
                case JumpMode.Off:
                    msg = "Jump Mode: OFF";
                    break;
                case JumpMode.Press:
                    msg = "Jump Mode: Press Space";
                    break;
                case JumpMode.Hold:
                    msg = "Jump Mode: Hold Space";
                    break;
                default:
                    msg = "Jump Mode: N/A";
                    break;
            }

            GTA.UI.Notification.Show(msg);

            // Reset hold states when changing mode for current vehicle
            if (_featureActive && _currentVehicleHandle == v.Handle)
            {
                _holdKeyDown = false;
                _isHoldActive = false;
                _lastHoldApplied = 0;
                _currentAppliedForce = new Vector3(0f, 0f, 0f);
            }
        }
        catch { }
    }

    private void PlaySoundAt(string name, string set)
    {
        try
        {
            Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, name, set, true);
        }
        catch { }
    }

    // small helper: linear interpolation for Vector3
    private void ApplyHoldImmediate(Vehicle v, JumpEntry entry)
    {
        try
        {
            if (v == null || !v.Exists() || v.IsDead) return;
            if (v.IsInWater) return;

            // immediate hold multipliers (a bit stronger than periodic to feel responsive)
            float upBase = entry.JumpStrength;
            float fwdBase = entry.ForwardBoost;

            float upMulImmediate = 0.30f;
            float fwdMulImmediate = 0.16f;

            Vector3 forward = v.ForwardVector;
            float fx = forward.X * fwdBase * fwdMulImmediate;
            float fy = forward.Y * fwdBase * fwdMulImmediate;
            float fz = upBase * upMulImmediate;

            // clamp defensively
            const float MAX_SINGLE_FORCE = 120f;
            fx = Math.Max(Math.Min(fx, MAX_SINGLE_FORCE), -MAX_SINGLE_FORCE);
            fy = Math.Max(Math.Min(fy, MAX_SINGLE_FORCE), -MAX_SINGLE_FORCE);
            fz = Math.Max(Math.Min(fz, MAX_SINGLE_FORCE), -MAX_SINGLE_FORCE);

            Vector3 immediateForce = new Vector3(fx, fy, fz);

            // set currentAppliedForce near the immediate target (so smoothing doesn't jerk)
            // we lerp with a high t so currentAppliedForce moves quickly toward immediateForce
            _currentAppliedForce = Lerp(_currentAppliedForce, immediateForce, 0.8f);

            // call central force applier
            ApplyForceToVehicle(v, immediateForce);

            // gentle audio feedback (non-spam)
            PlaySoundAt("AVENGER_LAUNCH", "VEHICLE_GENERAL");
        }
        catch { /* swallow */ }
    }
    private static Vector3 Lerp(Vector3 a, Vector3 b, float t)
    {
        t = Math.Max(0f, Math.Min(1f, t));
        return new Vector3(
            a.X + (b.X - a.X) * t,
            a.Y + (b.Y - a.Y) * t,
            a.Z + (b.Z - a.Z) * t
        );
    }
}
