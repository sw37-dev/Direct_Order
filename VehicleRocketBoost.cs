using System;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using GTAControl = GTA.Control;

public class RaceBoostOnHorn : Script
{
    // ===== TUNING =====
    private const float BoostAccelPerSecond = 48.0f;
    private const float MaxForwardSpeed = 60.0f;
    private const int BoostHoldMs = 1500;
    private const int BoostFadeMs = 900;
    private const int CooldownMs = 5000;
    private const int MinApplyIntervalMs = 15;

    // ===== STATE =====

    // Mặc định tắt
    private bool _enabled = false;

    private bool _boostActive = false;
    private int _boostStartMs = 0;
    private int _cooldownUntilMs = 0;

    private int _lastGameTimeMs = 0;
    private int _lastApplyMs = 0;

    public RaceBoostOnHorn()
    {
        Tick += OnTick;
        KeyDown += OnKeyDown;
        Interval = 0;
        _lastGameTimeMs = Game.GameTime;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.F7)
            return;

        _enabled = !_enabled;

        if (!_enabled)
        {
            _boostActive = false;
        }

        Notification.Show(_enabled ? "~HUD_COLOUR_DEGEN_GREEN~Horn Boost: ON" : "~HUD_COLOUR_REDLIGHT~Horn Boost: OFF");
    }

    private void OnTick(object sender, EventArgs e)
    {
        try
        {
            int now = Game.GameTime;
            float dt = (now - _lastGameTimeMs) / 1000.0f;
            _lastGameTimeMs = now;

            if (dt <= 0.0f)
                dt = 0.016f;
            else if (dt > 0.05f)
                dt = 0.05f;

            Ped ped = Game.Player.Character;
            if (ped == null || !ped.Exists() || !ped.IsAlive || !ped.IsInVehicle())
                return;

            Vehicle veh = ped.CurrentVehicle;
            if (veh == null || !veh.Exists() || veh.Driver != ped)
                return;

            // Chỉ khóa còi khi mod đang bật
            if (!_enabled)
                return;

            Game.DisableControlThisFrame(GTAControl.VehicleHorn);

            bool hornJustPressed = Function.Call<bool>(
                Hash.IS_DISABLED_CONTROL_JUST_PRESSED,
                0,
                (int)GTAControl.VehicleHorn
            );

            if (hornJustPressed)
                TryStartBoost(now);

            if (!_boostActive)
                return;

            float boostBlend = GetBoostBlend(now);
            if (boostBlend <= 0.0f)
            {
                _boostActive = false;
                return;
            }

            if (now - _lastApplyMs < MinApplyIntervalMs)
                return;
            _lastApplyMs = now;

            if (!Function.Call<bool>(Hash.IS_VEHICLE_ON_ALL_WHEELS, veh))
                return;

            Vector3 velocity = veh.Velocity;
            Vector3 forwardFlat = GetFlatForward(veh.ForwardVector);

            Vector3 horizontalVel = new Vector3(velocity.X, velocity.Y, 0.0f);
            float currentForwardSpeed = Vector3.Dot(horizontalVel, forwardFlat);

            if (currentForwardSpeed < -2.0f)
                return;

            float power = boostBlend * boostBlend;
            float addedSpeed = BoostAccelPerSecond * power * dt;
            float targetForwardSpeed = currentForwardSpeed + addedSpeed;

            if (targetForwardSpeed > MaxForwardSpeed)
                targetForwardSpeed = MaxForwardSpeed;
            if (targetForwardSpeed < 0.0f)
                targetForwardSpeed = 0.0f;

            Vector3 newHorizontalVel = horizontalVel + (forwardFlat * addedSpeed);

            float newForwardSpeed = Vector3.Dot(newHorizontalVel, forwardFlat);

            if (newForwardSpeed > MaxForwardSpeed)
            {
                float clampAdd = MaxForwardSpeed - currentForwardSpeed;
                if (clampAdd < 0.0f)
                    clampAdd = 0.0f;

                newHorizontalVel = horizontalVel + (forwardFlat * clampAdd);
            }

            Vector3 finalVel = new Vector3(newHorizontalVel.X, newHorizontalVel.Y, velocity.Z);

            Function.Call(Hash.SET_ENTITY_VELOCITY, veh, finalVel.X, finalVel.Y, finalVel.Z);
        }
        catch
        {
        }
    }

    private void TryStartBoost(int now)
    {
        if (_boostActive)
            return;

        if (now < _cooldownUntilMs)
        {
            int remainMs = _cooldownUntilMs - now;
            int remainSec = (int)Math.Ceiling(remainMs / 1000.0f);
            GTA.UI.Screen.ShowSubtitle("~h~~HUD_COLOUR_CONTROLLER_FRANKLIN~Waiting for energy to regen:~s~ " + remainSec + "s", 1200);
            return;
        }

        _boostActive = true;
        _boostStartMs = now;
        _cooldownUntilMs = now + CooldownMs;

        PlayTriggerExplosionSound();
    }

    private void PlayTriggerExplosionSound()
    {
        // Âm thanh báo hiệu thay cho subtitle "BOOST!"
        // Nếu bạn có native/hash riêng đúng tên TriggerExplosionSound trong project của bạn,
        // có thể thay dòng này bằng native đó.
        Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "TIMER_STOP", "HUD_MINI_GAME_SOUNDSET", false);
    }

    private float GetBoostBlend(int now)
    {
        int elapsed = now - _boostStartMs;
        if (elapsed < 0)
            return 0.0f;

        if (elapsed <= BoostHoldMs)
            return 1.0f;

        int fadeElapsed = elapsed - BoostHoldMs;
        if (fadeElapsed >= BoostFadeMs)
            return 0.0f;

        float t = fadeElapsed / (float)BoostFadeMs;
        float smooth = t * t * (3.0f - 2.0f * t);
        return 1.0f - smooth;
    }

    private static Vector3 GetFlatForward(Vector3 forward)
    {
        float len = (float)Math.Sqrt(forward.X * forward.X + forward.Y * forward.Y);
        if (len < 0.001f)
            return new Vector3(0.0f, 1.0f, 0.0f);

        return new Vector3(forward.X / len, forward.Y / len, 0.0f);
    }
}