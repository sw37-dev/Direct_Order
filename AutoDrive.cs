using System;
using System.Globalization;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using iFruitAddon2;

public class WaypointAutoDrive : Script
{
    private const int WaypointSpriteId = 8;

    private static string L(string key, string fallback = "")
    {
        return Language.Get(key, fallback);
    }

    private static string LifeinvaderContactName => L("Lifeinvader_ContactName", "Lifeinvader Enterprise");

    // Tốc độ khởi tạo ngẫu nhiên
    private const float RandomMinSpeedKph = 125.0f;
    private const float RandomMaxSpeedKph = 150.0f;

    // Tốc độ nhập tay mới
    private const float PromptMinSpeedKph = 70.0f;
    private const float PromptMaxSpeedKph = 210.0f;

    private const float StopRadiusMeters = 8.0f;

    private const int MainTickIntervalMs = 1500;
    private const int CameraTickIntervalMs = 250;

    private const int StyleHelpDurationMs = 6000;
    private const int SpeedHelpDurationMs = 10000;

    // 0,1,2,4 là các view mode chính thức của vehicle cam.
    // -1 = cinematic mode (native riêng).
    // Tăng nhẹ tần suất cinematic để ưu tiên kiểu điện ảnh hơn.
    private static readonly int[] CameraPool = { 0, 1, 2, 4, -1, -1 };

    // Thời lượng giữ camera thường
    private const int CameraMinHoldMs = 9000;
    private const int CameraMaxHoldMs = 15000;

    // Thời lượng giữ cinematic camera
    private const int CinematicCameraMinHoldMs = 18000;
    private const int CinematicCameraMaxHoldMs = 22000;

    private static readonly DrivingStyle[] DrivingStyles =
    {
        DrivingStyle.Normal,
        DrivingStyle.IgnoreLights,
        DrivingStyle.SometimesOvertakeTraffic,
        DrivingStyle.Rushed,
        DrivingStyle.AvoidTraffic,
        DrivingStyle.AvoidTrafficExtremely
    };

    private readonly Random _rng = new Random();

    private bool _active;

    private bool _styleHelpVisible;
    private bool _speedHelpVisible;

    private int _styleHelpOpenedAt;
    private int _speedHelpOpenedAt;
    private int _lastSpeedReminderAt;
    private int _lastMainUpdateAt;

    private Vehicle _vehicle;
    private Vector3 _destination;

    private int _appliedStyleIndex;
    private int _pendingStyleIndex;

    private float _targetSpeedKph;
    private float _targetSpeedNative;

    private string _styleHelpText = string.Empty;
    private string _speedInput = string.Empty;
    private string _speedHelpText = string.Empty;

    // Camera randomizer
    private bool _cameraRandomizerEnabled;
    private bool _cameraStateCaptured;
    private int _originalVehicleCamViewMode;
    private bool _originalCinematicRendering;
    private int _currentCameraMode = int.MinValue;
    private int _nextCameraSwitchAt;

    // Phone contact replacement for F7 toggle
    private CustomiFruit _lifeinvaderPhoneInstance = null;
    private bool _lifeinvaderContactAdded = false;

    public WaypointAutoDrive()
    {
        Interval = MainTickIntervalMs;
        Tick += OnTick;
    }

    private static string T(string key, string fallback, params string[] tokensAndValues)
    {
        string text = Language.Get(key, fallback);
        return Language.ReplaceTokens(text, tokensAndValues);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!_active)
            return;

        if (_speedHelpVisible)
        {
            HandleSpeedPromptKey(e.KeyCode);
            return;
        }

        if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right)
        {
            if (!_styleHelpVisible)
                OpenStyleHelp();

            ShiftPendingStyle(e.KeyCode == Keys.Right ? 1 : -1);
            RefreshStyleHelpText();
            return;
        }

        if (e.KeyCode == Keys.Enter && _styleHelpVisible)
        {
            OpenSpeedPrompt();
            return;
        }
    }

    private int GetCameraHoldMs(int cameraMode)
    {
        if (cameraMode == -1)
            return _rng.Next(CinematicCameraMinHoldMs, CinematicCameraMaxHoldMs + 1);

        return _rng.Next(CameraMinHoldMs, CameraMaxHoldMs + 1);
    }

    private void StartAutoDrive()
    {
        Ped ped = Game.Player.Character;
        if (ped == null || !ped.Exists() || !ped.IsInVehicle())
        {
            GTA.UI.Screen.ShowSubtitle(T("AUTODRIVE_NEED_VEHICLE", "~HUD_COLOUR_DEGEN_YELLOW~Bạn phải ngồi trong xe trước."), 3000);
            return;
        }

        Vehicle vehicle = ped.CurrentVehicle;
        if (vehicle == null || !vehicle.Exists())
        {
            GTA.UI.Screen.ShowSubtitle(T("AUTODRIVE_NO_CURRENT_VEHICLE", "~HUD_COLOUR_DEGEN_RED~Không tìm thấy xe hiện tại."), 3000);
            return;
        }

        Ped driver = Function.Call<Ped>(Hash.GET_PED_IN_VEHICLE_SEAT, vehicle, -1);
        if (driver == null || !driver.Exists() || driver.Handle != ped.Handle)
        {
            GTA.UI.Screen.ShowSubtitle(T("AUTODRIVE_NEED_DRIVER", "~HUD_COLOUR_YELLOWLIGHT~Bạn phải là tài xế của xe."), 3000);
            return;
        }

        if (!Function.Call<bool>(Hash.IS_WAYPOINT_ACTIVE))
        {
            GTA.UI.Screen.ShowSubtitle(T("AUTODRIVE_NEED_WAYPOINT", "~h~~HUD_COLOUR_WAYPOINTLIGHT~Hãy đặt waypoint trước."), 3000);
            return;
        }

        int waypointBlip = Function.Call<int>(Hash.GET_FIRST_BLIP_INFO_ID, WaypointSpriteId);
        if (waypointBlip == 0 || !Function.Call<bool>(Hash.DOES_BLIP_EXIST, waypointBlip))
        {
            GTA.UI.Screen.ShowSubtitle(T("AUTODRIVE_NO_WAYPOINT", "~HUD_COLOUR_DEGEN_RED~Không đọc được waypoint."), 3000);
            return;
        }

        _destination = Function.Call<Vector3>(Hash.GET_BLIP_INFO_ID_COORD, waypointBlip);
        _vehicle = vehicle;
        _active = true;

        _styleHelpVisible = false;
        _speedHelpVisible = false;
        _styleHelpText = string.Empty;
        _speedHelpText = string.Empty;
        _speedInput = string.Empty;

        _appliedStyleIndex = _rng.Next(0, DrivingStyles.Length);
        _pendingStyleIndex = _appliedStyleIndex;

        int randomKph = _rng.Next((int)RandomMinSpeedKph, (int)RandomMaxSpeedKph + 1);
        _targetSpeedKph = randomKph;
        _targetSpeedNative = _targetSpeedKph / 3.6f;

        _lastMainUpdateAt = Game.GameTime;

        CaptureCameraState();
        _cameraRandomizerEnabled = true;
        _currentCameraMode = int.MinValue;
        _nextCameraSwitchAt = Game.GameTime;
        ApplyRandomCameraMode();

        if (!TryApplyDriveTask(_appliedStyleIndex, true))
            return;

        GTA.UI.Screen.ShowSubtitle(
            T("AUTODRIVE_STARTED",
              "~HUD_COLOUR_GREENLIGHT~Auto Drive bật~s~ | {STYLE} | {SPEED} km/h",
              "{STYLE}", GetStyleName(_appliedStyleIndex),
              "{SPEED}", _targetSpeedKph.ToString("0.0", CultureInfo.InvariantCulture)),
            3000);
    }

    private void OnTick(object sender, EventArgs e)
    {
        EnsureLifeinvaderContactRegistered();

        if (!_active)
        {
            EnsureTickInterval(MainTickIntervalMs);
            return;
        }

        if (_styleHelpVisible)
        {
            if (Game.GameTime - _styleHelpOpenedAt >= StyleHelpDurationMs)
            {
                CloseStyleHelp();
                return;
            }

            GTA.UI.Screen.ShowHelpTextThisFrame(_styleHelpText);
            EnsureTickInterval(0);
        }
        else if (_speedHelpVisible)
        {
            if (Game.GameTime - _speedHelpOpenedAt >= SpeedHelpDurationMs)
            {
                ApplyPendingStyleOnly(T("AUTODRIVE_STYLE_CHANGED_SHORT", "~HUD_COLOUR_DEGEN_CYAN~Đã chuyển chế độ lái mới."));
                CloseSpeedPrompt();
                return;
            }

            UpdateSpeedHelpText();
            GTA.UI.Screen.ShowHelpTextThisFrame(_speedHelpText);
            EnsureTickInterval(0);

            if (string.IsNullOrWhiteSpace(_speedInput) && Game.GameTime - _lastSpeedReminderAt >= 1000)
            {
                GTA.UI.Screen.ShowSubtitle(T("AUTODRIVE_ENTER_SPEED_REMINDER", "~HUD_COLOUR_DEGEN_YELLOW~Hãy nhập giá trị tốc độ mới !!!"), 1000);
                _lastSpeedReminderAt = Game.GameTime;
            }
        }
        else
        {
            EnsureTickInterval(CameraTickIntervalMs);
            UpdateRandomCameraMode();
        }

        if (Game.GameTime - _lastMainUpdateAt < MainTickIntervalMs)
            return;

        _lastMainUpdateAt = Game.GameTime;
        ValidateDriveState();
    }

    private void CaptureCameraState()
    {
        if (_cameraStateCaptured)
            return;

        try
        {
            _originalVehicleCamViewMode = Function.Call<int>(Hash.GET_FOLLOW_VEHICLE_CAM_VIEW_MODE);
        }
        catch
        {
            _originalVehicleCamViewMode = 0;
        }

        try
        {
            _originalCinematicRendering = Function.Call<bool>(Hash.IS_CINEMATIC_CAM_RENDERING);
        }
        catch
        {
            _originalCinematicRendering = false;
        }

        _cameraStateCaptured = true;
    }

    private void UpdateRandomCameraMode()
    {
        if (!_cameraRandomizerEnabled)
            return;

        Ped ped = Game.Player.Character;
        if (ped == null || !ped.Exists() || !ped.IsInVehicle())
            return;

        if (_vehicle == null || !_vehicle.Exists())
            return;

        try
        {
            Function.Call(Hash.INVALIDATE_IDLE_CAM);
        }
        catch
        {
            // Không bắt buộc, chỉ để tránh AFK cam chen vào.
        }

        if (Game.GameTime < _nextCameraSwitchAt)
            return;

        ApplyRandomCameraMode();
    }

    private void ApplyRandomCameraMode()
    {
        int nextMode;

        do
        {
            nextMode = CameraPool[_rng.Next(CameraPool.Length)];
        }
        while (CameraPool.Length > 1 && nextMode == _currentCameraMode);

        try
        {
            if (nextMode == -1)
            {
                Function.Call(Hash.SET_CINEMATIC_BUTTON_ACTIVE, true);
                Function.Call(Hash.SET_CINEMATIC_MODE_ACTIVE, true);
            }
            else
            {
                Function.Call(Hash.SET_CINEMATIC_BUTTON_ACTIVE, false);
                Function.Call(Hash.SET_CINEMATIC_MODE_ACTIVE, false);
                Function.Call(Hash.SET_FOLLOW_VEHICLE_CAM_VIEW_MODE, nextMode);
            }

            _currentCameraMode = nextMode;
            _nextCameraSwitchAt = Game.GameTime + GetCameraHoldMs(nextMode);
        }
        catch
        {
            // Bỏ qua lỗi camera để không ảnh hưởng luồng auto drive.
        }
    }

    private void RestoreCameraState()
    {
        if (!_cameraStateCaptured)
            return;

        try
        {
            Function.Call(Hash.SET_CINEMATIC_BUTTON_ACTIVE, false);
            Function.Call(Hash.SET_CINEMATIC_MODE_ACTIVE, false);
            Function.Call(Hash.SET_FOLLOW_VEHICLE_CAM_VIEW_MODE, _originalVehicleCamViewMode);

            if (_originalCinematicRendering)
            {
                Function.Call(Hash.SET_CINEMATIC_BUTTON_ACTIVE, true);
                Function.Call(Hash.SET_CINEMATIC_MODE_ACTIVE, true);
            }
        }
        catch
        {
            // Bỏ qua lỗi phụ.
        }
        finally
        {
            _cameraRandomizerEnabled = false;
            _cameraStateCaptured = false;
            _currentCameraMode = int.MinValue;
        }
    }

    private void OpenStyleHelp()
    {
        _styleHelpVisible = true;
        _styleHelpOpenedAt = Game.GameTime;
        _pendingStyleIndex = _appliedStyleIndex;
        RefreshStyleHelpText();
        EnsureTickInterval(0);
    }

    private void CloseStyleHelp()
    {
        _styleHelpVisible = false;
        _styleHelpText = string.Empty;
        EnsureTickInterval(CameraTickIntervalMs);
    }

    private void RefreshStyleHelpText()
    {
        _styleHelpText = T(
             "AUTODRIVE_STYLE_HELP",
             "Chế độ lái hiện tại: {CURRENT} > ~h~{NEXT}~h~",
             "{CURRENT}", GetStyleName(_appliedStyleIndex),
             "{NEXT}", GetStyleName(_pendingStyleIndex));
    }

    private void ShiftPendingStyle(int delta)
    {
        int count = DrivingStyles.Length;
        _pendingStyleIndex = (_pendingStyleIndex + delta) % count;
        if (_pendingStyleIndex < 0)
            _pendingStyleIndex += count;
    }

    private void OpenSpeedPrompt()
    {
        _styleHelpVisible = false;
        _styleHelpText = string.Empty;

        _speedHelpVisible = true;
        _speedHelpOpenedAt = Game.GameTime;
        _lastSpeedReminderAt = Game.GameTime - 1000;
        _speedInput = string.Empty;

        UpdateSpeedHelpText();
        EnsureTickInterval(0);
    }

    private void CloseSpeedPrompt()
    {
        _speedHelpVisible = false;
        _speedHelpText = string.Empty;
        _speedInput = string.Empty;
        EnsureTickInterval(CameraTickIntervalMs);
    }

    private void UpdateSpeedHelpText()
    {
        string displayValue = _speedInput ?? string.Empty;

        _speedHelpText = T(
            "AUTODRIVE_SPEED_HELP",
            "~h~Bạn có muốn thay đổi tốc độ điều khiển không?\n" +
            "Nhập giá trị tốc độ: ~HUD_COLOUR_YOGA~[{VALUE}] km/h",
            "{VALUE}", displayValue);
    }

    private void HandleSpeedPromptKey(Keys keyCode)
    {
        if (keyCode == Keys.Back)
        {
            ApplyPendingStyleOnly(T("AUTODRIVE_STYLE_CHANGED", "~HUD_COLOUR_DEGEN_CYAN~Đã chuyển chế độ lái."));
            CloseSpeedPrompt();
            return;
        }

        if (keyCode == Keys.Enter)
        {
            if (string.IsNullOrWhiteSpace(_speedInput))
            {
                GTA.UI.Screen.ShowSubtitle(T("AUTODRIVE_ENTER_SPEED_REQUIRED", "~HUD_COLOUR_YELLOWLIGHT~Hãy nhập giá trị tốc độ mới !!!"), 1500);
                return;
            }

            if (!TryParseAndClampSpeed(_speedInput, out float speedKph))
            {
                GTA.UI.Screen.ShowSubtitle(T("AUTODRIVE_INVALID_SPEED", "~HUD_COLOUR_DEGEN_RED~Giá trị vận tốc không hợp lệ."), 1500);
                return;
            }

            ApplyPendingStyleAndSpeed(speedKph);
            CloseSpeedPrompt();
            return;
        }

        if (TryGetInputChar(keyCode, out char ch))
        {
            if (_speedInput.Length >= 7)
                return;

            if (char.IsDigit(ch))
            {
                _speedInput += ch;
                UpdateSpeedHelpText();
                return;
            }

            if (ch == '.')
            {
                if (_speedInput.Contains("."))
                    return;

                if (_speedInput.Length == 0)
                    _speedInput = "0.";
                else
                    _speedInput += '.';

                UpdateSpeedHelpText();
            }
        }
    }

    private void ApplyPendingStyleAndSpeed(float speedKph)
    {
        _targetSpeedKph = Clamp(speedKph, PromptMinSpeedKph, PromptMaxSpeedKph);
        _targetSpeedNative = _targetSpeedKph / 3.6f;

        if (TryApplyDriveTask(_pendingStyleIndex, false))
        {
            _appliedStyleIndex = _pendingStyleIndex;
            GTA.UI.Screen.ShowSubtitle(
                T("AUTODRIVE_SWITCHED_TO_STYLE",
                  "~HUD_COLOUR_DEGEN_CYAN~Chuyển sang chế độ: {STYLE} | {SPEED} km/h.",
                  "{STYLE}", GetStyleName(_appliedStyleIndex),
                  "{SPEED}", _targetSpeedKph.ToString("0.0", CultureInfo.InvariantCulture)),
                2500);
        }
        else
        {
            GTA.UI.Screen.ShowSubtitle(T("AUTODRIVE_CANNOT_APPLY_STYLE", "~HUD_COLOUR_DEGEN_RED~Không thể áp dụng chế độ lái mới."), 2000);
        }
    }

    private void ApplyPendingStyleOnly(string subtitleMessage)
    {
        if (TryApplyDriveTask(_pendingStyleIndex, false))
        {
            _appliedStyleIndex = _pendingStyleIndex;
            GTA.UI.Screen.ShowSubtitle(
                T("AUTODRIVE_STYLE_ONLY_APPLIED",
                  "{MESSAGE} ~s~| {STYLE} | {SPEED} km/h",
                  "{MESSAGE}", subtitleMessage,
                  "{STYLE}", GetStyleName(_appliedStyleIndex),
                  "{SPEED}", _targetSpeedKph.ToString("0.0", CultureInfo.InvariantCulture)),
                2500);
        }
        else
        {
            GTA.UI.Screen.ShowSubtitle(T("AUTODRIVE_CANNOT_APPLY_STYLE", "~HUD_COLOUR_DEGEN_RED~Không thể áp dụng chế độ lái mới."), 2000);
        }
    }

    private bool TryApplyDriveTask(int styleIndex, bool stopOnFailure)
    {
        try
        {
            if (!_active || _vehicle == null || !_vehicle.Exists())
                return false;

            Ped ped = Game.Player.Character;
            if (ped == null || !ped.Exists())
                return false;

            DrivingStyle style = DrivingStyles[styleIndex];

            Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                ped,
                _vehicle,
                _destination.X, _destination.Y, _destination.Z,
                _targetSpeedNative,
                (int)style,
                StopRadiusMeters);

            Function.Call(Hash.SET_DRIVE_TASK_DRIVING_STYLE, ped, (int)style);
            Function.Call(Hash.SET_DRIVE_TASK_CRUISE_SPEED, ped, _targetSpeedNative);

            return true;
        }
        catch
        {
            if (stopOnFailure)
                StopAutoDrive(T("AUTODRIVE_CANNOT_START", "~HUD_COLOUR_DEGEN_RED~Không thể bắt đầu Auto Drive."));

            return false;
        }
    }

    private void ValidateDriveState()
    {
        Ped ped = Game.Player.Character;
        if (ped == null || !ped.Exists() || !ped.IsInVehicle())
        {
            StopAutoDrive(T("AUTODRIVE_STOPPED", "~HUD_COLOUR_DEGEN_YELLOW~Auto Drive đã tắt."));
            return;
        }

        Vehicle vehicle = ped.CurrentVehicle;
        if (vehicle == null || !vehicle.Exists())
        {
            StopAutoDrive(T("AUTODRIVE_STOPPED", "~HUD_COLOUR_DEGEN_YELLOW~Auto Drive đã tắt."));
            return;
        }

        if (_vehicle != null && vehicle.Handle != _vehicle.Handle)
        {
            StopAutoDrive(T("AUTODRIVE_STOPPED", "~HUD_COLOUR_DEGEN_YELLOW~Auto Drive đã tắt."));
            return;
        }

        float dist2 = DistanceSquared(vehicle.Position, _destination);
        if (dist2 <= StopRadiusMeters * StopRadiusMeters)
        {
            StopAutoDrive(T("AUTODRIVE_ARRIVED", "~HUD_COLOUR_PM_MITEM_HIGHLIGHT~Đã tới điểm đến."));
        }
    }

    private void StopAutoDrive(string message)
    {
        Ped ped = Game.Player.Character;

        try
        {
            if (ped != null && ped.Exists())
                Function.Call(Hash.CLEAR_PED_TASKS, ped);
        }
        catch
        {
        }

        RestoreCameraState();

        _active = false;
        _styleHelpVisible = false;
        _speedHelpVisible = false;
        _vehicle = null;
        _styleHelpText = string.Empty;
        _speedHelpText = string.Empty;
        _speedInput = string.Empty;

        EnsureTickInterval(MainTickIntervalMs);
        GTA.UI.Screen.ShowSubtitle(message, 3000);
    }

    private void ToggleAutoDrive()
    {
        try
        {
            if (_active)
                StopAutoDrive(T("AUTODRIVE_STOPPED", "~HUD_COLOUR_DEGEN_YELLOW~Auto Drive đã tắt."));
            else
                StartAutoDrive();
        }
        catch
        {
            // Xử lý lỗi nếu cần thiết
        }
    }

    private void EnsureTickInterval(int intervalMs)
    {
        if (Interval != intervalMs)
            Interval = intervalMs;
    }

    private static string GetStyleName(int index)
    {
        switch (index)
        {
            case 0: return Language.Get("AUTODRIVE_STYLE_NORMAL", "Normal");
            case 1: return Language.Get("AUTODRIVE_STYLE_IGNORE_LIGHTS", "Ignore Lights");
            case 2: return Language.Get("AUTODRIVE_STYLE_SOMETIMES_OVERTAKE", "Sometimes Overtake Traffic");
            case 3: return Language.Get("AUTODRIVE_STYLE_RUSHED", "Rushed");
            case 4: return Language.Get("AUTODRIVE_STYLE_AVOID_TRAFFIC", "Avoid Traffic");
            case 5: return Language.Get("AUTODRIVE_STYLE_AVOID_TRAFFIC_A_LOT", "Avoid Traffic A Lot");
            default: return Language.Get("AUTODRIVE_UNKNOWN", "Unknown");
        }
    }

    private static float DistanceSquared(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private static bool TryParseAndClampSpeed(string input, out float speedKph)
    {
        speedKph = 0.0f;

        string cleaned = input.Trim().Replace(',', '.');

        if (!float.TryParse(
                cleaned,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float parsed))
        {
            return false;
        }

        speedKph = Clamp(parsed, PromptMinSpeedKph, PromptMaxSpeedKph);
        return true;
    }

    private static float Clamp(float value, float min, float max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private static bool TryGetInputChar(Keys keyCode, out char ch)
    {
        ch = '\0';

        if (keyCode >= Keys.D0 && keyCode <= Keys.D9)
        {
            ch = (char)('0' + (keyCode - Keys.D0));
            return true;
        }

        if (keyCode >= Keys.NumPad0 && keyCode <= Keys.NumPad9)
        {
            ch = (char)('0' + (keyCode - Keys.NumPad0));
            return true;
        }

        if (keyCode == Keys.OemPeriod || keyCode == Keys.Decimal)
        {
            ch = '.';
            return true;
        }

        return false;
    }

    // ----------------------- Lifeinvader Enterprise phone contact -----------------------
    private void EnsureLifeinvaderContactRegistered()
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            if (!ReferenceEquals(_lifeinvaderPhoneInstance, phone))
            {
                _lifeinvaderPhoneInstance = phone;
                _lifeinvaderContactAdded = false;
            }

            if (_lifeinvaderContactAdded)
                return;

            string contactName = LifeinvaderContactName;

            foreach (var c in phone.Contacts)
            {
                if (c != null && string.Equals(c.Name, contactName, StringComparison.OrdinalIgnoreCase))
                {
                    _lifeinvaderContactAdded = true;
                    return;
                }
            }

            var contact = new iFruitContact(contactName)
            {
                Active = true,
                DialTimeout = 2000,
                Bold = false,
                Icon = ContactIcon.Lifeinvader
            };

            contact.Answered += OnLifeinvaderEnterpriseAnswered;
            phone.Contacts.Add(contact);
            _lifeinvaderContactAdded = true;
        }
        catch
        {
            // Xử lý ngoại lệ nếu cần thiết
        }
    }

    private void OnLifeinvaderEnterpriseAnswered(iFruitContact sender)
    {
        try
        {
            ToggleAutoDrive();

            try
            {
                CustomiFruit.GetCurrentInstance()?.Close(0);
            }
            catch
            {
                // Xử lý ngoại lệ khi đóng điện thoại
            }
        }
        catch
        {
            // Xử lý ngoại lệ khi thực hiện ToggleAutoDrive
        }
    }
}