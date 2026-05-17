using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using iFruitAddon2;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using LemonUI;
using LemonUI.Menus;
using System.Reflection;
using System.Text;

public class Hacker : Script
{
    private static string T(string key, string fallback)
    {
        return Language.Get(key, fallback);
    }

    private Random _rng = new Random();

    private static string PaigeContactName => T("CityBlackout_ContactName", "Paige Harris");

    // --- Configurable (được load 1 lần khi mod khởi tạo) ---
    private int _cfgStartHour = 21;
    private int _cfgStartMinute = 0;
    private int _cfgEndHour = 22;
    private int _cfgEndMinute = 59;
    private int _cfgBlackoutDurationMs = 330 * 1000;
    private double _cfgTriggerChance = 0.20;
    private string _configPath;

    // --- Explosion sound config ---
    private bool _cfgPlayExplosion = true;
    private string _cfgExplosionSound = "EMP_Blast";
    private string _cfgExplosionSoundSet = "DLC_HEISTS_BIOLAB_FINALE_SOUNDS";
    private bool _cfgExplosionUseFrontend = true;

    // --- Window roll state ---
    private int _targetHour = -1;
    private int _targetMinute = -1;
    private bool _rolledThisWindow = false;

    // Thêm gần đầu class, cùng khu vực hằng số
    private const string PaigeHarrisNotificationBrand = "Paige Harris";
    private const int PaigeNotificationTimeout = 7000;

    private enum BlackoutSource
    {
        None,
        Auto,
        Paige
    }

    private BlackoutSource _blackoutSource = BlackoutSource.None;

    // Paige flow
    private const int PAIGE_CONFIRMATION_PROMPT_DELAY_MS = 5000;
    private const int PAIGE_BLACKOUT_DELAY_MS = 5000;
    private const int PAIGE_WANTED_DELAY_MS = 25000;

    private const int HelpBoxCooldownMs = 5000;
    private int _helpBoxCooldownExpiry = 0;

    // Trạng thái xác nhận của Paige:
    private bool _paigeSequenceActive = false;
    private bool _paigeHelpBoxVisible = false;
    private int _paigeConfirmationPromptGameTime = -1; // thời điểm bắt đầu hiện help-box
    private int _paigeBlackoutStartGameTime = -1;       // sau khi đồng ý, chờ thêm 5 giây mới cúp điện
    private int _paigeWantedGrantGameTime = -1;         // sau khi blackout bắt đầu mới gán sao
    private bool _paigeWantedGranted = false;           // đã gán sao chưa

    // --- Blackout state ---
    private bool _blackoutActive = false;
    private int _blackoutEndGameTime = 0; // ms

    // --- Daily lock state ---
    private int _lastObservedDayKey = -1;
    private int _blackoutLockedDayKey = -1;

    // --- Phone contact state ---
    private CustomiFruit _paigePhoneInstance = null;
    private bool _paigeContactAdded = false;

    // --- Smoothing / flicker ---
    private bool _smoothingActive = false;
    private int _smoothingStartTime = 0;
    private int _lastSmoothingToggleTime = 0;
    private int _smoothingStep = 0;
    private const int SMOOTH_STEP_MS = 280;
    private const int SMOOTH_STEPS = 12;

    // --- Native hashes ---
    private readonly Hash HASH_SET_ARTIFICIAL_LIGHTS_STATE = (Hash)0x1268615ACE24D504UL;
    private readonly Hash HASH_SET_ARTIFICIAL_VEHICLE_LIGHTS_STATE = (Hash)0xE2B187C0939B3D32UL;
    private readonly Hash HASH_SET_VEHICLE_LIGHTS = Hash.SET_VEHICLE_LIGHTS;

    private readonly ObjectPool _uiPool = new ObjectPool();

    private NativeMenu _hackerMenu;
    private NativeCheckboxItem _hackerCityBlackoutItem;
    private NativeCheckboxItem _hackerAtmItem;
    private NativeItem _hackerConfirmItem;
    private NativeItem _hackerCancelItem;
    private bool _hackerMenuInitialized = false;
    private bool _hackerUiSync = false;

    private CityBlackoutHackerState.HackerTarget _selectedHackerTarget =
        CityBlackoutHackerState.HackerTarget.None;

    // Wanted-level logic
    private const double ONE_STAR_PROB = 0.90;

    // Fallback vehicle handle list & native flag
    private List<int> _forcedVehicleHandles = new List<int>();
    private bool _usedAffectsNative = false;

    // Precomputed window minutes for faster roll logic
    private int _cfgStartTotalMinutes;
    private int _cfgEndTotalMinutes;
    private int _windowSpanMinutes;

    // Intervals (ms) tuned for performance:
    private const int IDLE_INTERVAL_MS = 1000;
    private const int WINDOW_INTERVAL_MS = 1000;
    private const int SMOOTH_INTERVAL_MS = 100;

    // Fallback vehicle search radius squared (to avoid scanning every vehicle far away)
    private const float VEHICLE_EFFECT_RADIUS = 200.0f;
    private const float VEHICLE_EFFECT_RADIUS_SQ = VEHICLE_EFFECT_RADIUS * VEHICLE_EFFECT_RADIUS;

    private void SetBoolPropertyIfExists(object target, bool value, params string[] propertyNames)
    {
        try
        {
            if (target == null || propertyNames == null || propertyNames.Length == 0)
                return;

            Type t = target.GetType();

            foreach (string name in propertyNames)
            {
                PropertyInfo prop = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null && prop.CanWrite && prop.PropertyType == typeof(bool))
                {
                    prop.SetValue(target, value, null);
                    return;
                }
            }
        }
        catch
        {
        }
    }

    private void UpdateLemonUiMouseState()
    {
        try
        {
            bool anyMenuVisible = _hackerMenu != null && _hackerMenu.Visible;
            if (!anyMenuVisible)
                return;

            SetBoolPropertyIfExists(_hackerMenu, false,
                "MouseControlsEnabled", "MouseControls", "EnableMouseControls", "UseMouse",
                "MouseEdgeEnabled", "MouseEdgesEnabled", "AllowMouseControls");

            SetBoolPropertyIfExists(_uiPool, false,
                "MouseControlsEnabled", "MouseControls", "EnableMouseControls", "UseMouse",
                "MouseEdgeEnabled", "MouseEdgesEnabled", "AllowMouseControls");
        }
        catch { }
    }

    // Constructor
    public Hacker()
    {
        Language.Initialize();
        _configPath = Path.Combine("scripts", "DirectOrder.ini");
        LoadConfig();
        Tick += OnTick;
        Aborted += OnAborted;

        // Default to idle interval (less CPU)
        Interval = IDLE_INTERVAL_MS;
    }

    private void LoadConfig()
    {
        try
        {
            // pre-initialize window values with defaults first
            UpdateWindowPrecomputations();

            if (!File.Exists(_configPath)) return;

            try
            {
                var settings = ScriptSettings.Load(_configPath);

                string sStartHour = settings.GetValue<string>("CityBlackout", "StartHour", _cfgStartHour.ToString());
                string sStartMin = settings.GetValue<string>("CityBlackout", "StartMinute", _cfgStartMinute.ToString());
                string sEndHour = settings.GetValue<string>("CityBlackout", "EndHour", _cfgEndHour.ToString());
                string sEndMin = settings.GetValue<string>("CityBlackout", "EndMinute", _cfgEndMinute.ToString());
                string sDuration = settings.GetValue<string>("CityBlackout", "BlackoutDurationSeconds", (_cfgBlackoutDurationMs / 1000).ToString());
                string sChance = settings.GetValue<string>("CityBlackout", "TriggerChance", _cfgTriggerChance.ToString(CultureInfo.InvariantCulture));

                string sPlayExplosion = settings.GetValue<string>("CityBlackout", "PlayExplosion", _cfgPlayExplosion ? "true" : "false");
                string sExplosionSound = settings.GetValue<string>("CityBlackout", "ExplosionSound", _cfgExplosionSound);
                string sExplosionSoundSet = settings.GetValue<string>("CityBlackout", "ExplosionSoundSet", _cfgExplosionSoundSet);
                string sExplosionUseFrontend = settings.GetValue<string>("CityBlackout", "ExplosionUseFrontend", _cfgExplosionUseFrontend ? "true" : "false");

                int tmp;
                if (int.TryParse(sStartHour, out tmp)) _cfgStartHour = Clamp(tmp, 0, 23);
                if (int.TryParse(sStartMin, out tmp)) _cfgStartMinute = Clamp(tmp, 0, 59);
                if (int.TryParse(sEndHour, out tmp)) _cfgEndHour = Clamp(tmp, 0, 23);
                if (int.TryParse(sEndMin, out tmp)) _cfgEndMinute = Clamp(tmp, 0, 59);

                int durSeconds;
                if (int.TryParse(sDuration, out durSeconds) && durSeconds >= 1) _cfgBlackoutDurationMs = durSeconds * 1000;

                double tmpd;
                if (double.TryParse(sChance, NumberStyles.Float, CultureInfo.InvariantCulture, out tmpd)) _cfgTriggerChance = Math.Max(0.0, Math.Min(1.0, tmpd));

                bool btmp;
                if (bool.TryParse(sPlayExplosion, out btmp)) _cfgPlayExplosion = btmp;
                if (!string.IsNullOrEmpty(sExplosionSound)) _cfgExplosionSound = sExplosionSound;
                if (!string.IsNullOrEmpty(sExplosionSoundSet)) _cfgExplosionSoundSet = sExplosionSoundSet;
                if (bool.TryParse(sExplosionUseFrontend, out btmp)) _cfgExplosionUseFrontend = btmp;
            }
            catch
            {
                ParseConfigFallback();
            }

            UpdateWindowPrecomputations();
        }
        catch (Exception ex)
        {
            GTA.UI.Notification.Show(T("CityBlackout_LoadConfigError", "CityBlackout LoadConfig error: ") + ex.Message);
        }
    }

    private void UpdateWindowPrecomputations()
    {
        _cfgStartTotalMinutes = _cfgStartHour * 60 + _cfgStartMinute;
        _cfgEndTotalMinutes = _cfgEndHour * 60 + _cfgEndMinute;
        if (_cfgStartTotalMinutes <= _cfgEndTotalMinutes)
        {
            _windowSpanMinutes = _cfgEndTotalMinutes - _cfgStartTotalMinutes + 1;
        }
        else
        {
            _windowSpanMinutes = (_cfgEndTotalMinutes + 24 * 60) - _cfgStartTotalMinutes + 1;
        }
    }

    private void ShowPaigeHarrisNotification(
        string title,
        string message,
        int timeout = PaigeNotificationTimeout)
    {
        try
        {
            Notification.Show(
                NotificationIcon.HumanDefault,
                PaigeHarrisNotificationBrand,
                title,
                message
            );
        }
        catch
        {
            GTA.UI.Screen.ShowSubtitle(message, timeout);
        }
    }

    private void ParseConfigFallback()
    {
        try
        {
            string[] lines = File.ReadAllLines(_configPath);
            bool inSection = false;
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith(";") || line.StartsWith("#")) continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    string sec = line.Substring(1, line.Length - 2).Trim();
                    inSection = sec.Equals("CityBlackout", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inSection) continue;

                int idx = line.IndexOf('=');
                if (idx < 0) continue;
                string key = line.Substring(0, idx).Trim();
                string val = line.Substring(idx + 1).Trim();

                int tmp;
                double tmpd;
                bool tmpb;
                switch (key.ToLowerInvariant())
                {
                    case "starthour":
                        if (int.TryParse(val, out tmp)) _cfgStartHour = Clamp(tmp, 0, 23);
                        break;
                    case "startminute":
                        if (int.TryParse(val, out tmp)) _cfgStartMinute = Clamp(tmp, 0, 59);
                        break;
                    case "endhour":
                        if (int.TryParse(val, out tmp)) _cfgEndHour = Clamp(tmp, 0, 23);
                        break;
                    case "endminute":
                        if (int.TryParse(val, out tmp)) _cfgEndMinute = Clamp(tmp, 0, 59);
                        break;
                    case "blackoutdurationseconds":
                        if (int.TryParse(val, out tmp) && tmp >= 1) _cfgBlackoutDurationMs = tmp * 1000;
                        break;
                    case "triggerchance":
                        if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out tmpd)) _cfgTriggerChance = Math.Max(0.0, Math.Min(1.0, tmpd));
                        break;
                    case "playexplosion":
                        if (bool.TryParse(val, out tmpb)) _cfgPlayExplosion = tmpb;
                        break;
                    case "explosionsound":
                        if (!string.IsNullOrEmpty(val)) _cfgExplosionSound = val;
                        break;
                    case "explosionsoundset":
                        if (!string.IsNullOrEmpty(val)) _cfgExplosionSoundSet = val;
                        break;
                    case "explosionusefrontend":
                        if (bool.TryParse(val, out tmpb)) _cfgExplosionUseFrontend = tmpb;
                        break;
                }
            }
        }
        catch { /* ignore parsing errors silently */ }
    }

    private void OnTick(object sender, EventArgs e)
    {
        try
        {
            EnsurePaigeHarrisContactRegistered();

            int currentDayKey = GetCurrentGameDayKey();
            if (currentDayKey != -1 && currentDayKey != _lastObservedDayKey)
            {
                _lastObservedDayKey = currentDayKey;
                _targetHour = -1;
                _targetMinute = -1;
                _rolledThisWindow = false;
            }

            HandlePaigeDeferredActions();
            CityBlackoutHackerState.ProcessPendingAtmHackRollsForCurrentCharacter();
            CityBlackoutHackerState.ProcessBankAccessTimersForCurrentCharacter();

            if (_uiPool != null && _uiPool.AreAnyVisible)
            {
                UpdateLemonUiMouseState();
                Interval = 0;
                _uiPool.Process();
                UpdateLemonUiMouseState();
                return;
            }

            // Nếu đang blackout: xử lý smoothing / kết thúc
            if (_blackoutActive)
            {
                if (!_smoothingActive && Game.GameTime >= _blackoutEndGameTime)
                {
                    StartSmoothingSequence();
                }

                if (_smoothingActive)
                {
                    HandleSmoothingTick();
                }

                // while blackout active we keep a lower interval (but not too frequent)
                Interval = SMOOTH_INTERVAL_MS;
                return;
            }

            // Lấy giờ/phút in-game chỉ 1 lần mỗi tick
            int hour = Function.Call<int>(Hash.GET_CLOCK_HOURS);
            int minute = Function.Call<int>(Hash.GET_CLOCK_MINUTES);

            // Kiểm tra nhanh xem có đang trong cửa sổ cấu hình không
            if (IsWithinWindow(hour, minute))
            {
                // ở trong cửa sổ -> tăng tần suất kiểm tra để không bỏ lỡ phút mục tiêu
                Interval = WINDOW_INTERVAL_MS;

                // Nếu chưa chọn target trong cửa sổ này -> chọn một total-minute ngẫu nhiên
                if (_targetHour == -1)
                {
                    // chọn một offset trong span (0..span-1)
                    int offset = _rng.Next(0, Math.Max(1, _windowSpanMinutes));
                    int selTotal = (_cfgStartTotalMinutes + offset) % (24 * 60);
                    _targetHour = selTotal / 60;
                    _targetMinute = selTotal % 60;
                }

                // Nếu chưa roll lần này và giờ==targetHour & minute==targetMinute thì roll
                if (!_rolledThisWindow && hour == _targetHour && minute == _targetMinute)
                {
                    _rolledThisWindow = true;
                    if (!IsBlackoutLockedToday(currentDayKey))
                    {
                        double roll = _rng.NextDouble();
                        if (roll <= _cfgTriggerChance)
                        {
                            StartBlackoutAndLock();
                        }
                    }
                }
            }
            else
            {
                // Ra khỏi cửa sổ -> reset để sẵn sàng cho lần sau, và hạ tần suất tick
                if (_targetHour != -1 || _targetMinute != -1 || _rolledThisWindow)
                {
                    _targetHour = -1;
                    _targetMinute = -1;
                    _rolledThisWindow = false;
                }
                Interval = IDLE_INTERVAL_MS;
            }

            // Khi Paige đang chờ hiện help-box / chờ người chơi đồng ý / chờ blackout sau khi đồng ý,
            // ép tick = 0ms để help-box không bị nhấp nháy và timing luôn chính xác.
            if (_paigeSequenceActive && !_blackoutActive)
            {
                Interval = 0;
            }
        }
        catch (Exception ex)
        {
            GTA.UI.Notification.Show(T("CityBlackout_RuntimeError", "CityBlackout error: ") + ex.Message);
        }
    }

    private void HandlePaigeDeferredActions()
    {
        int now = Game.GameTime;

        if (_paigeSequenceActive)
        {
            // Nếu vì lý do nào đó blackout khác đã bắt đầu trong lúc chờ Paige,
            // thì hủy toàn bộ lớp xác nhận để tránh logic treo.
            if (_blackoutActive && _blackoutSource != BlackoutSource.Paige)
            {
                ResetPaigeSequenceState(true);
            }
            else
            {
                // 5 giây sau khi Paige gửi tin nhắn mới hiện help-box
                if (!_paigeHelpBoxVisible &&
                    _paigeConfirmationPromptGameTime > 0 &&
                    now >= _paigeConfirmationPromptGameTime)
                {
                    _paigeHelpBoxVisible = true;
                }

                // Help-box phải được vẽ mỗi frame cho tới khi người chơi chọn đồng ý / từ chối
                if (_paigeHelpBoxVisible)
                {
                    ShowPaigeConfirmationHelpBox();

                    if (Function.Call<bool>(Hash.IS_CONTROL_JUST_PRESSED, 0, (int)Control.FrontendAccept))
                    {
                        // Đồng ý: tắt help-box, chờ thêm 5 giây rồi mới bắt đầu blackout
                        _paigeHelpBoxVisible = false;
                        _paigeConfirmationPromptGameTime = -1;
                        _paigeBlackoutStartGameTime = now + PAIGE_BLACKOUT_DELAY_MS;
                    }
                    else if (Function.Call<bool>(Hash.IS_CONTROL_JUST_PRESSED, 0, (int)Control.FrontendCancel))
                    {
                        // Từ chối: hủy hoàn toàn, không khóa ngày
                        ResetPaigeSequenceState(true);
                        return;
                    }
                }

                // Sau khi đồng ý, chờ thêm 5 giây rồi mới thực sự kích hoạt blackout
                if (!_paigeHelpBoxVisible &&
                    _paigeBlackoutStartGameTime > 0 &&
                    now >= _paigeBlackoutStartGameTime)
                {
                    _paigeBlackoutStartGameTime = -1;

                    // Chỉ khóa ngày khi blackout kích hoạt thành công
                    if (!StartBlackout(BlackoutSource.Paige))
                    {
                        ResetPaigeSequenceState(true);
                    }
                    else
                    {
                        _paigeSequenceActive = false;
                        _paigeHelpBoxVisible = false;
                        _paigeConfirmationPromptGameTime = -1;
                    }
                }
            }
        }

        // 10 giây sau khi blackout bắt đầu mới gán sao
        if (_blackoutActive &&
            _blackoutSource == BlackoutSource.Paige &&
            !_paigeWantedGranted &&
            _paigeWantedGrantGameTime > 0 &&
            now >= _paigeWantedGrantGameTime)
        {
            _paigeWantedGranted = true;
            TryGivePlayerWantedIncrement();
        }
    }

    private void ShowPaigeConfirmationHelpBox()
    {
        const string helpText =
            "Anh có chắc muốn tôi hack lưới điện thành phố chứ, nhưng anh sẽ bị truy nã đấy?\n~INPUT_FRONTEND_ACCEPT~ để đồng ý\n~INPUT_FRONTEND_LEFT~ để hủy";

        try
        {
            GTA.UI.Screen.ShowHelpTextThisFrame(helpText);
        }
        catch
        {
            try
            {
                Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_HELP, "STRING");
                Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, helpText);
                Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_HELP, 0, false, true, -1);
            }
            catch
            {
            }
        }
    }

    private bool IsWithinWindow(int hour, int minute)
    {
        int total = hour * 60 + minute;
        if (_cfgStartTotalMinutes <= _cfgEndTotalMinutes)
        {
            return (total >= _cfgStartTotalMinutes && total <= _cfgEndTotalMinutes);
        }
        else
        {
            // wrapped window (ví dụ 23:00 -> 02:00)
            return (total >= _cfgStartTotalMinutes || total <= _cfgEndTotalMinutes);
        }
    }

    private bool StartBlackout(BlackoutSource source)
    {
        try
        {
            if (_blackoutActive || _smoothingActive)
                return false;

            int dayKey = GetCurrentGameDayKey();
            if (dayKey == -1)
                dayKey = _lastObservedDayKey;

            // Với Paige: chỉ được khóa ngày khi blackout thực sự bắt đầu thành công.
            if (source == BlackoutSource.Paige && IsBlackoutLockedToday(dayKey))
            {
                ResetPaigeSequenceState(true);
                return false;
            }

            _blackoutSource = source;
            _blackoutActive = true;
            _blackoutEndGameTime = Game.GameTime + _cfgBlackoutDurationMs;
            _usedAffectsNative = false;
            _forcedVehicleHandles.Clear();
            _smoothingActive = false;

            // Chỉ blackout từ Paige mới được phép gán sao, nhưng gán sau khi blackout đã chạy xong phần khởi tạo
            if (source == BlackoutSource.Paige)
            {
                _paigeWantedGranted = false;
                _paigeWantedGrantGameTime = Game.GameTime + PAIGE_WANTED_DELAY_MS;
            }
            else
            {
                _paigeWantedGranted = true;
                _paigeWantedGrantGameTime = -1;
            }

            // play sound nếu bật
            if (_cfgPlayExplosion)
            {
                TriggerExplosionSound();
            }

            try
            {
                Function.Call(HASH_SET_ARTIFICIAL_VEHICLE_LIGHTS_STATE, false);
                Function.Call(HASH_SET_ARTIFICIAL_LIGHTS_STATE, true);
                _usedAffectsNative = true;

                GTA.UI.Screen.ShowSubtitle(
                    T("CityBlackout_BlackoutStarted", "~HUD_COLOUR_DEGEN_RED~Thành phố đã bị cúp điện"),
                    5000
                );

                // Blackout của Paige chỉ được khóa ngày sau khi đã khởi tạo thành công
                if (source == BlackoutSource.Paige)
                {
                    _blackoutLockedDayKey = dayKey;
                    ResetPaigeSequenceState(false);
                }

                Interval = IDLE_INTERVAL_MS;
                return true;
            }
            catch
            {
                _usedAffectsNative = false;
            }

            try
            {
                Function.Call(HASH_SET_ARTIFICIAL_LIGHTS_STATE, true);

                Ped playerPed = Game.Player.Character;
                Vector3 playerPos = Vector3.Zero;
                if (playerPed != null && playerPed.Exists()) playerPos = playerPed.Position;

                var vehicles = World.GetAllVehicles();
                try
                {
                    foreach (Vehicle v in vehicles)
                    {
                        if (v == null || !v.Exists()) continue;

                        float dx = v.Position.X - playerPos.X;
                        float dy = v.Position.Y - playerPos.Y;
                        float dz = v.Position.Z - playerPos.Z;
                        float distSq = dx * dx + dy * dy + dz * dz;
                        if (distSq > VEHICLE_EFFECT_RADIUS_SQ) continue;

                        Function.Call(HASH_SET_VEHICLE_LIGHTS, v.Handle, 2);
                        _forcedVehicleHandles.Add(v.Handle);
                    }
                }
                catch { }

                GTA.UI.Screen.ShowSubtitle(
                    T("CityBlackout_BlackoutStartedAlt", "~HUD_COLOUR_DEGEN_RED~~h~Cúp điện toàn thành phố"),
                    5000
                );

                if (source == BlackoutSource.Paige)
                {
                    _blackoutLockedDayKey = dayKey;
                    ResetPaigeSequenceState(false);
                }

                Interval = SMOOTH_INTERVAL_MS;
                return true;
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.Show(T("CityBlackout_StartBlackoutFailed", "StartBlackout failed: ") + ex.Message);
                _blackoutActive = false;
                _blackoutSource = BlackoutSource.None;

                if (source == BlackoutSource.Paige)
                {
                    ResetPaigeSequenceState(true);
                }

                return false;
            }
        }
        catch (Exception ex)
        {
            GTA.UI.Notification.Show(T("CityBlackout_StartBlackoutFailed", "StartBlackout failed: ") + ex.Message);
            _blackoutActive = false;
            _blackoutSource = BlackoutSource.None;

            if (source == BlackoutSource.Paige)
            {
                ResetPaigeSequenceState(true);
            }

            return false;
        }
    }

    private void StartBlackoutAndLock()
    {
        int dayKey = GetCurrentGameDayKey();
        if (dayKey == -1)
            dayKey = _lastObservedDayKey;

        if (IsBlackoutLockedToday(dayKey))
            return;

        if (StartBlackout(BlackoutSource.Auto))
        {
            _blackoutLockedDayKey = dayKey;

            // Nếu đang có một lớp xác nhận Paige chờ sẵn thì phải hủy nó,
            // vì blackout tự động đã chạy rồi.
            if (_paigeSequenceActive)
            {
                ResetPaigeSequenceState(true);
            }
        }
    }

    private void StartPaigeBlackoutSequence()
    {
        int dayKey = GetCurrentGameDayKey();
        if (dayKey == -1)
            dayKey = _lastObservedDayKey;

        if (IsBlackoutLockedToday(dayKey))
            return;

        // Không tạo chồng nhiều lớp xác nhận
        if (_paigeSequenceActive)
            return;

        _paigeSequenceActive = true;
        _paigeHelpBoxVisible = false;
        _paigeConfirmationPromptGameTime = Game.GameTime + PAIGE_CONFIRMATION_PROMPT_DELAY_MS;
        _paigeBlackoutStartGameTime = -1;

        // reset wanted state cho an toàn
        _paigeWantedGranted = false;
        _paigeWantedGrantGameTime = -1;

        ShowPaigeHarrisNotification(
            T("CityBlackout_Title", "Cúp điện"),
            T("CityBlackout_Message", "Tôi sẽ truy cập hệ thống điện thành phố và cắt điện nhưng cảnh sát sẽ truy ra anh rất nhanh đấy!")
        );

        // tick nhanh để help-box không bị nhấp nháy và thời điểm 5 giây luôn chính xác
        Interval = 0;
    }

    private bool IsBlackoutLockedToday(int dayKey)
    {
        return dayKey != -1 && _blackoutLockedDayKey == dayKey;
    }

    private int GetCurrentGameDayKey()
    {
        try
        {
            int year = Function.Call<int>(Hash.GET_CLOCK_YEAR);
            int month = Function.Call<int>(Hash.GET_CLOCK_MONTH);
            int day = Function.Call<int>(Hash.GET_CLOCK_DAY_OF_MONTH);
            return (year * 10000) + (month * 100) + day;
        }
        catch
        {
            return -1;
        }
    }

    private void EnsurePaigeHarrisContactRegistered()
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            if (!ReferenceEquals(_paigePhoneInstance, phone))
            {
                _paigePhoneInstance = phone;
                _paigeContactAdded = false;
            }

            if (_paigeContactAdded)
                return;

            if (phone.Contacts.Any(c =>
                string.Equals(c.Name, PaigeContactName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Name, "Paige Harris", StringComparison.OrdinalIgnoreCase)))
            {
                _paigeContactAdded = true;
                return;
            }

            var contact = new iFruitContact(PaigeContactName)
            {
                Active = true,
                DialTimeout = 6000,
                Bold = false,
                Icon = ContactIcon.PaigeHarris
            };

            contact.Answered += OnPaigeHarrisContactAnswered;
            phone.Contacts.Add(contact);

            _paigeContactAdded = true;
        }
        catch
        {
        }
    }

    private void OnPaigeHarrisContactAnswered(iFruitContact sender)
    {
        try
        {
            // Nếu đang blackout do Paige tạo ra thì gọi Paige lần nữa = bật điện lại như cũ
            if (_blackoutActive && _blackoutSource == BlackoutSource.Paige)
            {
                _paigeBlackoutStartGameTime = -1;
                _paigeWantedGrantGameTime = -1;
                _paigeWantedGranted = true;

                StartSmoothingSequence();
            }
            else
            {
                // Mặc định: mở menu Hacker
                if (!_blackoutActive && !_smoothingActive && !_paigeSequenceActive)
                {
                    OpenHackerMenu();
                }
            }
        }
        finally
        {
            try { CustomiFruit.GetCurrentInstance()?.Close(0); } catch { }
        }
    }

    private void EnsureHackerMenuCreated()
    {
        try
        {
            if (_hackerMenuInitialized)
                return;

            _hackerMenu = new NativeMenu(
                T("CityBlackout_HackerTitle", "Hacker"),
                T("CityBlackout_HackerSubtitle", "CHI TIẾT DANH MỤC"));

            _uiPool.Add(_hackerMenu);
            _hackerMenu.Visible = false;

            _hackerMenuInitialized = true;
        }
        catch { }
    }

    private void BuildHackerMenu()
    {
        if (_hackerMenu == null)
            return;

        _hackerMenu.Clear();
        _selectedHackerTarget = CityBlackoutHackerState.HackerTarget.None;

        _hackerCityBlackoutItem = new NativeCheckboxItem(
            T("CityBlackout_Hacker_Target1", "1. Cúp điện thành phố"),
            false);

        _hackerAtmItem = new NativeCheckboxItem(
            T("CityBlackout_Hacker_Target2", "2. Hack ATM của Lom Bank"),
            false);

        _hackerConfirmItem = new NativeItem(
            T("CityBlackout_HackerConfirm", "Xác nhận giao dịch"));

        _hackerCancelItem = new NativeItem(
            T("CityBlackout_HackerCancel", "Hủy dịch vụ hacker"));

        _hackerConfirmItem.Activated += (s, e) =>
        {
            ConfirmHackerTarget();
        };

        _hackerCancelItem.Activated += (s, e) =>
        {
            CloseHackerMenu(true);
        };

        _hackerMenu.Add(_hackerCityBlackoutItem);
        _hackerMenu.Add(_hackerAtmItem);
        _hackerMenu.Add(_hackerConfirmItem);
        _hackerMenu.Add(_hackerCancelItem);

        UpdateLemonUiMouseState();
    }

    private void OpenHackerMenu()
    {
        try
        {
            if (IsHelpBoxCooldownActive())
            {
                ShowHelpCooldownMessage();
                return;
            }

            if (_paigeSequenceActive || _blackoutActive || _smoothingActive)
                return;

            EnsureHackerMenuCreated();
            BuildHackerMenu();

            if (_hackerMenu != null)
                _hackerMenu.Visible = true;

            UpdateLemonUiMouseState();
            Interval = 0;
        }
        catch { }
    }

    private void CloseHackerMenu(bool setCooldown)
    {
        try
        {
            if (_hackerMenu != null)
            {
                _hackerMenu.Visible = false;
                _hackerMenu.Clear();
            }

            _selectedHackerTarget = CityBlackoutHackerState.HackerTarget.None;
        }
        catch { }

        if (setCooldown)
            EnsureHelpBoxCooldownSet();

        UpdateLemonUiMouseState();
        Interval = 1000;
    }

    private bool IsHelpBoxCooldownActive()
    {
        try
        {
            return Game.GameTime < _helpBoxCooldownExpiry;
        }
        catch
        {
            return false;
        }
    }

    private void EnsureHelpBoxCooldownSet()
    {
        try
        {
            _helpBoxCooldownExpiry = Game.GameTime + HelpBoxCooldownMs;
        }
        catch
        {
            _helpBoxCooldownExpiry = Game.GameTime;
        }
    }

    private void ShowHelpCooldownMessage()
    {
        try
        {
            int remainMs = Math.Max(0, _helpBoxCooldownExpiry - Game.GameTime);
            int sec = (int)Math.Ceiling(remainMs / 1000.0);

            GTA.UI.Screen.ShowSubtitle(
                string.Format(
                    T("CityBlackout_HelpCooldown", "~y~Hãy đợi ~b~{0}s~s~ trước khi mở lại menu."),
                    sec),
                2500);
        }
        catch { }
    }

    private void ConfirmHackerTarget()
    {
        try
        {
            bool blackoutSelected = _hackerCityBlackoutItem != null && _hackerCityBlackoutItem.Checked;
            bool atmSelected = _hackerAtmItem != null && _hackerAtmItem.Checked;

            if (!blackoutSelected && !atmSelected)
            {
                GTA.UI.Screen.ShowSubtitle(
                    T("CityBlackout_HackerNoTarget", "~HUD_COLOUR_DEGEN_YELLOW~Hãy chọn ít nhất một mục tiêu."),
                    2500);
                return;
            }

            bool atmArmed = false;

            if (atmSelected)
            {
                long cash = Math.Max(0, Game.Player.Money);

                if (cash < 10000)
                {
                    GTA.UI.Screen.ShowSubtitle(
                        T("CityBlackout_HackerAtmTooPoor", "~r~Bạn phải có ít nhất $10,000 để kích hoạt Hack ATM."),
                        3000);
                }
                else
                {
                    int fee = CityBlackoutHackerState.ComputeAtmHackFee(cash);
                    if (Game.Player.Money < fee)
                    {
                        GTA.UI.Screen.ShowSubtitle(
                            T("CityBlackout_HackerAtmNoFee", "~r~Không đủ tiền thuê hacker."),
                            3000);
                    }
                    else
                    {
                        Game.Player.Money -= fee;

                        // Arm 1 lần duy nhất cho lần rút tiền tiếp theo
                        CityBlackoutHackerState.ArmAtmHackForCurrentCharacter();
                        atmArmed = true;
                    }
                }
            }

            CloseHackerMenu(false);

            if (atmArmed)
            {
                ShowPaigeHarrisNotification(
                    T("CityBlackout_HackerAtmTitle", "Hack ATM"),
                    T("CityBlackout_HackerAtmActiveOnce",
                        "Đợi tôi một chút. ATM của Lom Bank sẽ rơi vào tay tôi do tôi kiểm soát nhưng sẽ có rủi ro nếu ATM của Lom Bank có trục trặc nhé."));
            }

            if (blackoutSelected)
            {
                StartPaigeBlackoutSequence();
            }
        }
        catch { }
    }

    private void TriggerExplosionSound()
    {
        try
        {
            if (!_cfgPlayExplosion) return;

            // Lấy vị trí player để có fallback phát 3D nếu cần
            Vector3 pos = Vector3.Zero;
            try
            {
                var playerPed = Game.Player?.Character;
                if (playerPed != null && playerPed.Exists()) pos = playerPed.Position;
            }
            catch { pos = Vector3.Zero; }

            if (_cfgExplosionUseFrontend)
            {
                // ưu tiên wrapper an toàn
                try
                {
                    GTA.Audio.PlaySoundFrontend(_cfgExplosionSound, _cfgExplosionSoundSet);
                }
                catch
                {
                    try
                    {
                        Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, _cfgExplosionSound, _cfgExplosionSoundSet, true);
                    }
                    catch { }
                }
            }
            else
            {
                try
                {
                    GTA.Audio.PlaySoundAt(pos, _cfgExplosionSound, _cfgExplosionSoundSet);
                }
                catch
                {
                    try
                    {
                        Function.Call(Hash.PLAY_SOUND_FROM_COORD, -1, _cfgExplosionSound, pos.X, pos.Y, pos.Z, _cfgExplosionSoundSet, false, 0, 0);
                    }
                    catch { }
                }
            }
        }
        catch { /* im lặng nếu lỗi âm thanh */ }
    }

    private void TriggerPowerOnSound()
    {
        // Sử dụng âm thanh "tạch" của cầu dao khi có điện lại
        GTA.Audio.PlaySoundFrontend("Breaker_01", "DLC_HALLOWEEN_FVJ_Sounds");
    }

    private void StartSmoothingSequence()
    {
        try
        {
            _paigeWantedGranted = true;
            _paigeWantedGrantGameTime = -1;

            _smoothingActive = true;
            _smoothingStartTime = Game.GameTime;
            _lastSmoothingToggleTime = Game.GameTime;
            _smoothingStep = 0;
            // smoothing cần tick nhanh hơn -> set interval nhỏ (nhưng không quá nhỏ)
            Interval = SMOOTH_INTERVAL_MS;
        }
        catch (Exception ex)
        {
            GTA.UI.Notification.Show(T("CityBlackout_StartSmoothingSequenceFailed", "StartSmoothingSequence failed: ") + ex.Message);
            FinishBlackout();
        }
    }

    private void HandleSmoothingTick()
    {
        try
        {
            int now = Game.GameTime;
            if (now - _lastSmoothingToggleTime < SMOOTH_STEP_MS) return;

            bool desiredArtificialLightsState = (_smoothingStep % 2 == 1);

            try
            {
                Function.Call(HASH_SET_ARTIFICIAL_LIGHTS_STATE, desiredArtificialLightsState);
            }
            catch
            {
                // nếu native lỗi trong smoothing -> kết thúc blackout an toàn
                _smoothingActive = false;
                Interval = IDLE_INTERVAL_MS;
                FinishBlackout();
                return;
            }

            _smoothingStep++;
            _lastSmoothingToggleTime = now;

            if (_smoothingStep >= SMOOTH_STEPS)
            {
                try { Function.Call(HASH_SET_ARTIFICIAL_LIGHTS_STATE, false); } catch { }

                _smoothingActive = false;
                _blackoutActive = false;
                Interval = IDLE_INTERVAL_MS;

                if (_usedAffectsNative)
                {
                    try { Function.Call(HASH_SET_ARTIFICIAL_VEHICLE_LIGHTS_STATE, true); } catch { }
                    _usedAffectsNative = false;
                }
                else
                {
                    // restore forced vehicle lights (kiểm tra tồn tại trước khi gọi native)
                    foreach (int handle in _forcedVehicleHandles)
                    {
                        try { Function.Call(HASH_SET_VEHICLE_LIGHTS, handle, 0); } catch { }
                    }
                    _forcedVehicleHandles.Clear();
                }

                GTA.UI.Screen.ShowSubtitle(T("CityBlackout_PowerRestored", "~HUD_COLOUR_CONTROLLER_MICHAEL~~h~Đã có điện trở lại."), 3000);
                TriggerPowerOnSound();
            }
        }
        catch (Exception ex)
        {
            GTA.UI.Notification.Show(T("CityBlackout_SmoothingError", "Smoothing error: ") + ex.Message);
            _smoothingActive = false;
            _blackoutActive = false;
            Interval = IDLE_INTERVAL_MS;
            try { Function.Call(HASH_SET_ARTIFICIAL_LIGHTS_STATE, false); } catch { }
        }
    }

    private void FinishBlackout()
    {
        try
        {
            try { Function.Call(HASH_SET_ARTIFICIAL_LIGHTS_STATE, false); } catch { }

            if (_usedAffectsNative)
            {
                try { Function.Call(HASH_SET_ARTIFICIAL_VEHICLE_LIGHTS_STATE, true); } catch { }
                _usedAffectsNative = false;
            }
            else
            {
                foreach (int handle in _forcedVehicleHandles)
                {
                    try { Function.Call(HASH_SET_VEHICLE_LIGHTS, handle, 0); } catch { }
                }
                _forcedVehicleHandles.Clear();
            }

            _blackoutActive = false;
            _smoothingActive = false;
            _blackoutSource = BlackoutSource.None;
            _paigeBlackoutStartGameTime = -1;
            _paigeWantedGrantGameTime = -1;
            _paigeWantedGranted = true;

            // an toàn: xóa luôn bất kỳ lớp xác nhận Paige nào còn sót
            ResetPaigeSequenceState(true);

            Interval = IDLE_INTERVAL_MS;
            GTA.UI.Screen.ShowSubtitle(T("CityBlackout_PowerRestored", "~HUD_COLOUR_CONTROLLER_MICHAEL~~h~Đã có điện trở lại."), 3000);
            TriggerPowerOnSound();
        }
        catch (Exception ex)
        {
            GTA.UI.Notification.Show(T("CityBlackout_FinishBlackoutFailed", "FinishBlackout failed: ") + ex.Message);
            _blackoutActive = false;
            _smoothingActive = false;
            _forcedVehicleHandles.Clear();
            Interval = IDLE_INTERVAL_MS;
        }
    }

    private void OnAborted(object sender, EventArgs e)
    {
        if (_blackoutActive || _smoothingActive)
        {
            try
            {
                Function.Call(HASH_SET_ARTIFICIAL_LIGHTS_STATE, false);

                if (_usedAffectsNative)
                {
                    try { Function.Call(HASH_SET_ARTIFICIAL_VEHICLE_LIGHTS_STATE, true); } catch { }
                    _usedAffectsNative = false;
                }
                else
                {
                    foreach (int handle in _forcedVehicleHandles)
                    {
                        try { Function.Call(HASH_SET_VEHICLE_LIGHTS, handle, 0); } catch { }
                    }
                    _forcedVehicleHandles.Clear();
                }

                _blackoutActive = false;
                _smoothingActive = false;
                _blackoutSource = BlackoutSource.None;
                _paigeBlackoutStartGameTime = -1;
                _paigeWantedGrantGameTime = -1;
                _paigeWantedGranted = true;

                ResetPaigeSequenceState(true);
            }
            catch { }
        }
        else
        {
            ResetPaigeSequenceState(true);
        }
    }

    private void ResetPaigeSequenceState(bool clearWantedTimers)
    {
        _paigeSequenceActive = false;
        _paigeHelpBoxVisible = false;
        _paigeConfirmationPromptGameTime = -1;
        _paigeBlackoutStartGameTime = -1;

        if (clearWantedTimers)
        {
            _paigeWantedGranted = false;
            _paigeWantedGrantGameTime = -1;
        }
    }

    private void TryGivePlayerWantedIncrement()
    {
        try
        {
            int current = Game.Player.WantedLevel;
            double roll = _rng.NextDouble();
            int delta = (roll <= ONE_STAR_PROB) ? 1 : 2;
            int newLevel = Math.Min(5, current + delta);

            if (newLevel != current)
            {
                Game.Player.WantedLevel = newLevel;
                try
                {
                    int playerId = Function.Call<int>(Hash.PLAYER_ID);
                    Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, playerId, false);
                }
                catch
                {
                    try { Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, Function.Call<int>(Hash.PLAYER_ID)); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            GTA.UI.Notification.Show(T("CityBlackout_WantedLevelError", "Không thể cấp sao truy nã: ") + ex.Message);
        }
    }

    // --- Helpers ---
    private static int Clamp(int v, int lo, int hi) { return (v < lo) ? lo : (v > hi) ? hi : v; }
}

internal static class CityBlackoutHackerState
{
    public enum HackerTarget
    {
        None = 0,
        CityBlackout = 1,
        HackAtm = 2
    }

    private sealed class PendingAtmRiskEntry
    {
        public int DueGameTimeMs;
        public DateTime DueAtInGameTime;
        public long DirtyMoneyAmount;
    }

    private sealed class StateData
    {
        public long DirtyMoney = 0;

        public DateTime? AtmLockedUntil = null;
        public DateTime? FleecaLockedUntil = null;

        public bool AtmUnlockNoticeShown = false;
        public bool FleecaUnlockNoticeShown = false;

        public List<PendingAtmRiskEntry> PendingRisks = new List<PendingAtmRiskEntry>();
    }

    private static void ApplyDetectionLocks(StateData s, DateTime detectedAt)
    {
        if (s == null)
            return;

        // Lom Bank: khóa 4 ngày
        s.AtmLockedUntil = detectedAt.AddDays(4);
        s.AtmUnlockNoticeShown = false;

        // Fleeca Bank: khóa 2 ngày
        s.FleecaLockedUntil = detectedAt.AddDays(2);
        s.FleecaUnlockNoticeShown = false;
    }

    private static readonly object _sync = new object();
    private static readonly Random _rng = new Random();
    private static readonly Dictionary<int, HackerTarget> _sessionTargets = new Dictionary<int, HackerTarget>();

    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "Hacker");

    private static string T(string key, string fallback)
    {
        return Language.Get(key, fallback);
    }

    private static int GetCurrentCharacterHash()
    {
        try
        {
            Ped p = Game.Player.Character;
            if (p != null && p.Exists())
                return p.Model.Hash;
        }
        catch { }
        return 0;
    }

    private static DateTime GetCurrentInGameDateTime()
    {
        try
        {
            int year = Function.Call<int>(Hash.GET_CLOCK_YEAR);
            int month = Function.Call<int>(Hash.GET_CLOCK_MONTH);
            int day = Function.Call<int>(Hash.GET_CLOCK_DAY_OF_MONTH);
            int hour = Function.Call<int>(Hash.GET_CLOCK_HOURS);
            int minute = Function.Call<int>(Hash.GET_CLOCK_MINUTES);

            if (month < 1 || month > 12)
                month += 1;

            if (year < 1) year = 1;
            if (month < 1) month = 1;
            if (month > 12) month = 12;

            int maxDay = DateTime.DaysInMonth(year, month);
            if (day < 1) day = 1;
            if (day > maxDay) day = maxDay;

            if (hour < 0) hour = 0;
            if (hour > 23) hour = 23;

            if (minute < 0) minute = 0;
            if (minute > 59) minute = 59;

            return new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        }
        catch
        {
            return new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        }
    }

    private static DateTime? ParseNullableDateTime(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (DateTime.TryParseExact(
            value,
            "o",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTime parsed))
        {
            return parsed;
        }

        if (DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out parsed))
        {
            return parsed;
        }

        return null;
    }

    public static decimal ComputeAtmHackDetectionRate(long lombankTotalLimit)
    {
        long limit = Math.Max(1_000_000L, Math.Min(15_000_000L, lombankTotalLimit));

        if (limit <= 3_000_000L)
            return 0.0600m;

        if (limit <= 5_000_000L)
            return 0.0712m;

        if (limit <= 8_000_000L)
            return 0.0820m;

        if (limit <= 10_000_000L)
            return 0.0912m;

        if (limit <= 12_000_000L)
            return 0.0940m;

        if (limit <= 14_000_000L)
            return 0.0708m;

        return 0.0704m;
    }

    public static bool IsAtmTransactionLockedForCurrentCharacter()
    {
        int ownerHash = GetCurrentCharacterHash();
        if (ownerHash == 0)
            return false;

        lock (_sync)
        {
            StateData s = Load();
            if (!s.AtmLockedUntil.HasValue)
                return false;

            return GetCurrentInGameDateTime() < s.AtmLockedUntil.Value;
        }
    }

    public static bool IsAtmTransactionContactEnabledForCurrentCharacter()
    {
        return !IsAtmTransactionLockedForCurrentCharacter();
    }

    public static bool IsFleecaBankLockedForCurrentCharacter()
    {
        int ownerHash = GetCurrentCharacterHash();
        if (ownerHash == 0)
            return false;

        lock (_sync)
        {
            StateData s = Load();
            if (!s.FleecaLockedUntil.HasValue)
                return false;

            return GetCurrentInGameDateTime() < s.FleecaLockedUntil.Value;
        }
    }

    public static bool IsFleecaBankContactEnabledForCurrentCharacter()
    {
        return !IsFleecaBankLockedForCurrentCharacter();
    }

    public static string GetAtmTransactionContactStateTextForCurrentCharacter()
    {
        if (!IsAtmTransactionLockedForCurrentCharacter())
            return string.Empty;

        TimeSpan remain = GetAtmTransactionLockRemainingForCurrentCharacter();
        int days = Math.Max(0, (int)Math.Ceiling(remain.TotalDays));
        if (days <= 0)
            return "ATM tạm khóa";

        return string.Format(
            CultureInfo.InvariantCulture,
            "ATM tạm khóa còn {0} ngày",
            days);
    }

    public static TimeSpan GetAtmTransactionLockRemainingForCurrentCharacter()
    {
        int ownerHash = GetCurrentCharacterHash();
        if (ownerHash == 0)
            return TimeSpan.Zero;

        lock (_sync)
        {
            StateData s = Load();
            if (!s.AtmLockedUntil.HasValue)
                return TimeSpan.Zero;

            TimeSpan remain = s.AtmLockedUntil.Value - GetCurrentInGameDateTime();
            return remain > TimeSpan.Zero ? remain : TimeSpan.Zero;
        }
    }

    private static string GetFilePath(int ownerHash)
    {
        Directory.CreateDirectory(Root);
        return Path.Combine(Root, $"hacker_state_{ownerHash}.dat");
    }

    private static StateData Load()
    {
        var s = new StateData();
        int ownerHash = GetCurrentCharacterHash();
        if (ownerHash == 0)
            return s;

        try
        {
            string path = GetFilePath(ownerHash);
            if (!File.Exists(path))
                return s;

            foreach (var raw in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                int idx = raw.IndexOf('=');
                if (idx <= 0)
                    continue;

                string key = raw.Substring(0, idx).Trim().ToLowerInvariant();
                string val = raw.Substring(idx + 1).Trim();

                if (key == "dirtymoney")
                {
                    if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out long dm))
                        s.DirtyMoney = Math.Max(0, dm);
                    continue;
                }

                if (key == "atmlockeduntil")
                {
                    s.AtmLockedUntil = ParseNullableDateTime(val);
                    continue;
                }

                if (key == "atmunlocknoticeshown")
                {
                    s.AtmUnlockNoticeShown = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (key == "fleecalockeduntil")
                {
                    s.FleecaLockedUntil = ParseNullableDateTime(val);
                    continue;
                }

                if (key == "fleecaunlocknoticeshown")
                {
                    s.FleecaUnlockNoticeShown = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (key == "pending" || key == "pendingrisk")
                {
                    string[] parts = val.Split('|');
                    if (parts.Length < 3)
                        continue;

                    if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int dueGameTimeMs))
                        continue;

                    if (!DateTime.TryParseExact(
                        parts[1].Trim(),
                        "o",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out DateTime dueInGameTime))
                    {
                        continue;
                    }

                    if (!long.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long amount))
                        continue;

                    s.PendingRisks.Add(new PendingAtmRiskEntry
                    {
                        DueGameTimeMs = dueGameTimeMs,
                        DueAtInGameTime = DateTime.SpecifyKind(dueInGameTime, DateTimeKind.Unspecified),
                        DirtyMoneyAmount = Math.Max(0, amount)
                    });
                }
            }
        }
        catch { }

        return s;
    }

    private static void Save(StateData s)
    {
        int ownerHash = GetCurrentCharacterHash();
        if (ownerHash == 0)
            return;

        try
        {
            Directory.CreateDirectory(Root);
            string path = GetFilePath(ownerHash);

            var lines = new List<string>
        {
            $"dirtyMoney={Math.Max(0, s.DirtyMoney)}"
        };

            lines.Add("atmLockedUntil=" + (s.AtmLockedUntil.HasValue
                ? s.AtmLockedUntil.Value.ToString("o", CultureInfo.InvariantCulture)
                : ""));
            lines.Add("fleecaLockedUntil=" + (s.FleecaLockedUntil.HasValue
                ? s.FleecaLockedUntil.Value.ToString("o", CultureInfo.InvariantCulture)
                : ""));
            lines.Add("atmUnlockNoticeShown=" + (s.AtmUnlockNoticeShown ? "1" : "0"));
            lines.Add("fleecaUnlockNoticeShown=" + (s.FleecaUnlockNoticeShown ? "1" : "0"));

            foreach (var p in s.PendingRisks)
            {
                if (p == null)
                    continue;

                lines.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "pending={0}|{1}|{2}",
                    p.DueGameTimeMs,
                    p.DueAtInGameTime.ToString("o", CultureInfo.InvariantCulture),
                    Math.Max(0, p.DirtyMoneyAmount)));
            }

            File.WriteAllLines(path, lines.ToArray());
        }
        catch { }
    }

    public static HackerTarget GetTargetForCurrentCharacter()
    {
        int ownerHash = GetCurrentCharacterHash();
        if (ownerHash == 0)
            return HackerTarget.None;

        lock (_sync)
        {
            HackerTarget target;
            return _sessionTargets.TryGetValue(ownerHash, out target) ? target : HackerTarget.None;
        }
    }

    public static void SetTargetForCurrentCharacter(HackerTarget target)
    {
        int ownerHash = GetCurrentCharacterHash();
        if (ownerHash == 0)
            return;

        lock (_sync)
        {
            if (target == HackerTarget.None)
                _sessionTargets.Remove(ownerHash);
            else
                _sessionTargets[ownerHash] = target;
        }
    }

    public static void ArmAtmHackForCurrentCharacter()
    {
        int ownerHash = GetCurrentCharacterHash();
        if (ownerHash == 0)
            return;

        lock (_sync)
        {
            _sessionTargets[ownerHash] = HackerTarget.HackAtm;
        }
    }

    public static bool IsAtmHackActiveForCurrentCharacter()
    {
        return GetTargetForCurrentCharacter() == HackerTarget.HackAtm;
    }

    public static bool ConsumeAtmHackSessionForCurrentCharacter()
    {
        int ownerHash = GetCurrentCharacterHash();
        if (ownerHash == 0)
            return false;

        lock (_sync)
        {
            HackerTarget target;
            if (_sessionTargets.TryGetValue(ownerHash, out target) && target == HackerTarget.HackAtm)
            {
                _sessionTargets.Remove(ownerHash);
                return true;
            }
        }

        return false;
    }

    private static long GetLombankTotalLimitForCurrentCharacter()
    {
        const long fallbackLimit = 1_000_000L;

        int ownerHash = GetCurrentCharacterHash();
        if (ownerHash == 0)
            return fallbackLimit;

        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GTA V Mods", "Lom Bank",
                $"lombank_state_{ownerHash}.dat");

            if (!File.Exists(path))
                return fallbackLimit;

            foreach (string raw in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                int idx = raw.IndexOf('=');
                if (idx <= 0)
                    continue;

                string key = raw.Substring(0, idx).Trim();
                if (!key.Equals("limit", StringComparison.OrdinalIgnoreCase))
                    continue;

                string val = raw.Substring(idx + 1).Trim();
                if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out long limit))
                    return Math.Max(fallbackLimit, Math.Min(15_000_000L, limit));

                break;
            }
        }
        catch { }

        return fallbackLimit;
    }

    public static int ComputeAtmHackFee()
    {
        return ComputeAtmHackFee(GetLombankTotalLimitForCurrentCharacter(), Math.Max(0, Game.Player.Money));
    }

    public static int ComputeAtmHackFee(long currentCash)
    {
        return ComputeAtmHackFee(GetLombankTotalLimitForCurrentCharacter(), currentCash);
    }

    // Gọi hàm này ngay sau khi ATM rút tiền thành công
    // Nó vừa ghi dirty money, vừa tạo timer 7 giây, vừa consume trạng thái hack ATM
    public static bool RecordAtmHackWithdrawalForCurrentCharacter(long amount)
    {
        if (amount <= 0)
            return false;

        int ownerHash = GetCurrentCharacterHash();
        if (ownerHash == 0)
            return false;

        lock (_sync)
        {
            HackerTarget target;
            if (!_sessionTargets.TryGetValue(ownerHash, out target) || target != HackerTarget.HackAtm)
                return false;

            StateData s = Load();

            s.DirtyMoney = Math.Max(0, s.DirtyMoney + amount);

            // 7 giây in-game: dùng Game.GameTime cho timer live,
            // đồng thời lưu thêm in-game clock để còn an toàn khi reload state.
            s.PendingRisks.Add(new PendingAtmRiskEntry
            {
                DueGameTimeMs = Game.GameTime + 7000,
                DueAtInGameTime = GetCurrentInGameDateTime().AddSeconds(7),
                DirtyMoneyAmount = amount
            });

            Save(s);
            _sessionTargets.Remove(ownerHash);
            return true;
        }
    }

    public static long GetDirtyMoneyBalanceForCurrentCharacter()
    {
        return Load().DirtyMoney;
    }

    public static void AddDirtyMoneyForCurrentCharacter(long amount)
    {
        if (amount <= 0)
            return;

        lock (_sync)
        {
            StateData s = Load();
            long next = s.DirtyMoney + amount;
            if (next < 0) next = long.MaxValue;
            s.DirtyMoney = next;
            Save(s);
        }
    }

    public static bool TrySpendDirtyMoneyForCurrentCharacter(long amount)
    {
        if (amount <= 0)
            return true;

        lock (_sync)
        {
            StateData s = Load();
            if (s.DirtyMoney < amount)
                return false;

            s.DirtyMoney -= amount;
            if (s.DirtyMoney < 0) s.DirtyMoney = 0;
            Save(s);
            return true;
        }
    }

    // Roll 1 lần khi đã quá 7 giây:
    // 6% => cộng 3 sao, tối đa 5 sao, đồng thời tịch thu đúng số tiền bẩn của phiên đó
    public static void ProcessPendingAtmHackRollsForCurrentCharacter()
    {
        int ownerHash = GetCurrentCharacterHash();
        if (ownerHash == 0)
            return;

        lock (_sync)
        {
            StateData s = Load();
            if (s.PendingRisks.Count == 0)
                return;

            int nowGameTime = Game.GameTime;
            DateTime nowInGame = GetCurrentInGameDateTime();

            var dueEntries = s.PendingRisks
                .Where(x =>
                    x != null &&
                    (
                        (x.DueGameTimeMs > 0 && nowGameTime >= x.DueGameTimeMs) ||
                        (x.DueAtInGameTime != default(DateTime) && nowInGame >= x.DueAtInGameTime)
                    ))
                .ToList();

            if (dueEntries.Count == 0)
                return;

            long confiscatedTotal = 0;
            bool anyHit = false;

            int wanted = 0;
            try { wanted = Math.Max(0, Game.Player.WantedLevel); } catch { wanted = 0; }

            decimal detectionRate = ComputeAtmHackDetectionRate(GetLombankTotalLimitForCurrentCharacter());

            foreach (var entry in dueEntries)
            {
                if (_rng.NextDouble() <= (double)detectionRate)
                {
                    anyHit = true;
                    confiscatedTotal += Math.Max(0, entry.DirtyMoneyAmount);
                    wanted = Math.Min(5, wanted + 3);
                }
            }

            s.PendingRisks.RemoveAll(x =>
                x != null &&
                (
                    (x.DueGameTimeMs > 0 && nowGameTime >= x.DueGameTimeMs) ||
                    (x.DueAtInGameTime != default(DateTime) && nowInGame >= x.DueAtInGameTime)
                ));

            if (confiscatedTotal > 0)
            {
                s.DirtyMoney = Math.Max(0, s.DirtyMoney - confiscatedTotal);
            }

            if (anyHit)
            {
                ApplyDetectionLocks(s, nowInGame);
            }

            Save(s);

            if (!anyHit)
                return;

            try
            {
                Game.Player.WantedLevel = wanted;
                try
                {
                    int playerId = Function.Call<int>(Hash.PLAYER_ID);
                    Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, playerId, false);
                }
                catch
                {
                    try { Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, Function.Call<int>(Hash.PLAYER_ID)); } catch { }
                }
            }
            catch { }

            try
            {
                ShowLombankCaughtNotification(confiscatedTotal);
            }
            catch
            {
                try
                {
                    GTA.UI.Screen.ShowSubtitle(
                        T("CityBlackout_AtmHackCaughtFallback",
                            "Lom Bank đã phát hiện giao dịch ATM. Tiền bất hợp pháp đã bị tịch thu."),
                        3500);
                }
                catch { }
            }
        }
    }

    private static void ShowLombankCaughtNotification(long confiscatedTotal)
    {
        try
        {
            Function.Call(Hash.BEGIN_TEXT_COMMAND_THEFEED_POST, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Lom Bank đã phát hiện {0} tiền bẩn từ ATM. Tiền đã bị thu lại.",
                    string.Format(CultureInfo.InvariantCulture, "${0:N0}", confiscatedTotal)));

            Function.Call(Hash.END_TEXT_COMMAND_THEFEED_POST_MESSAGETEXT,
                "WEB_LOMBANK",
                "WEB_LOMBANK",
                false,
                0,
                "Lom Bank",
                T("CityBlackout_AtmHackCaughtTitle", "Hack ATM"));

            Function.Call(Hash.END_TEXT_COMMAND_THEFEED_POST_TICKER, false, true);
        }
        catch
        {
            try
            {
                Notification.Show(
                    NotificationIcon.BankFleeca,
                    "Lom Bank",
                    T("CityBlackout_AtmHackCaughtTitle", "Hack ATM"),
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Lom Bank đã phát hiện {0} tiền bẩn từ ATM. Tiền đã bị thu lại.",
                        string.Format(CultureInfo.InvariantCulture, "${0:N0}", confiscatedTotal)));
            }
            catch
            {
            }
        }
    }

    public static void ProcessBankAccessTimersForCurrentCharacter()
    {
        int ownerHash = GetCurrentCharacterHash();
        if (ownerHash == 0)
            return;

        lock (_sync)
        {
            StateData s = Load();
            DateTime now = GetCurrentInGameDateTime();
            bool changed = false;

            if (s.AtmLockedUntil.HasValue && now >= s.AtmLockedUntil.Value)
            {
                if (!s.AtmUnlockNoticeShown)
                {
                    s.AtmUnlockNoticeShown = true;

                    try
                    {
                        ShowLombankUnlockedNotification();
                    }
                    catch
                    {
                        try { GTA.UI.Screen.ShowSubtitle("Lom Bank: Ngân hàng đã mở khóa lại giao dịch của anh rồi!", 3000); } catch { }
                    }

                    changed = true;
                }

                s.AtmLockedUntil = null;
                changed = true;
            }

            if (s.FleecaLockedUntil.HasValue && now >= s.FleecaLockedUntil.Value)
            {
                if (!s.FleecaUnlockNoticeShown)
                {
                    s.FleecaUnlockNoticeShown = true;

                    try
                    {
                        ShowFleecaUnlockedNotification();
                    }
                    catch
                    {
                        try { GTA.UI.Screen.ShowSubtitle("Fleeca Bank: Ngân hàng đã mở khóa lại giao dịch của anh rồi!", 3000); } catch { }
                    }

                    changed = true;
                }

                s.FleecaLockedUntil = null;
                changed = true;
            }

            if (changed)
                Save(s);
        }
    }

    private static void ShowLombankUnlockedNotification()
    {
        try
        {
            Function.Call(Hash.BEGIN_TEXT_COMMAND_THEFEED_POST, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, "Ngân hàng đã mở khóa lại giao dịch của anh rồi!");

            Function.Call(Hash.END_TEXT_COMMAND_THEFEED_POST_MESSAGETEXT,
                "WEB_LOMBANK",
                "WEB_LOMBANK",
                false,
                0,
                "Lom Bank",
                "Thông báo");

            Function.Call(Hash.END_TEXT_COMMAND_THEFEED_POST_TICKER, false, true);
        }
        catch
        {
            try { GTA.UI.Screen.ShowSubtitle("Lom Bank: Ngân hàng đã mở khóa lại giao dịch của anh rồi!", 3000); } catch { }
        }
    }

    private static void ShowFleecaUnlockedNotification()
    {
        try
        {
            Notification.Show(
                NotificationIcon.BankFleeca,
                "Fleeca Bank",
                "Thông báo",
                "Ngân hàng đã mở khóa lại giao dịch của anh rồi!");
        }
        catch
        {
            try { GTA.UI.Screen.ShowSubtitle("Fleeca Bank: Ngân hàng đã mở khóa lại giao dịch của anh rồi!", 3000); } catch { }
        }
    }

    public static int ComputeAtmHackFee(long lombankTotalLimit, long currentCash)
    {
        long limit = Math.Max(1_000_000L, lombankTotalLimit);
        decimal rate;
        long cap;

        if (limit <= 3_000_000L)
        {
            rate = 0.035m;
            cap = 1_000_000L;
        }
        else if (limit <= 5_000_000L)
        {
            rate = 0.0379m;
            cap = 1_700_000L;
        }
        else if (limit <= 8_000_000L)
        {
            rate = 0.047m;
            cap = 2_400_000L;
        }
        else if (limit <= 10_000_000L)
        {
            rate = 0.0561m;
            cap = 3_100_000L;
        }
        else if (limit <= 12_000_000L)
        {
            rate = 0.059m;
            cap = 3_800_000L;
        }
        else if (limit <= 14_000_000L)
        {
            rate = 0.0667m;
            cap = 4_500_000L;
        }
        else
        {
            rate = 0.0718m;
            cap = 4_850_000L;
        }

        long baseCash = Math.Max(0L, currentCash);
        long fee = (long)Math.Round(baseCash * rate, MidpointRounding.AwayFromZero);
        if (fee < 10000L) fee = 10000L;
        if (fee > cap) fee = cap;
        return (int)Math.Min(int.MaxValue, fee);
    }

    public static bool IsDirtyOnlyVehicle(uint modelHash, string displayName, string vehicleClass)
    {
        try
        {
            string text = ((displayName ?? string.Empty) + " " + (vehicleClass ?? string.Empty))
                .ToLowerInvariant();

            int cls = -1;
            try { cls = Function.Call<int>(Hash.GET_VEHICLE_CLASS_FROM_NAME, modelHash); }
            catch { }

            if (cls == 18 || cls == 19)
                return true;

            string[] keys =
            {
                "police","sheriff","swat","fbi","riot","pranger","patrol",
                "military","weaponized","combat","armed","tank","apc","khanjali",
                "chernobog","hydra","lazer","pyro","raiju","akula","hunter",
                "strikeforce","avenger","rogue","bombushka","alkonost","volatol",
                "menacer","half-track","vigilante","oppressor","deluxo","stromberg",
                "toreador","scramjet","ruiner 2000","thruster","predator","dinghy5",
                "tampa3","technical2","police4","police5","police3","policeb",
                "policeold1","policeold2","policeo1","coqutt4"
            };

            foreach (var k in keys)
            {
                if (text.Contains(k))
                    return true;
            }

            if (cls == 14 || cls == 15 || cls == 16)
            {
                return text.Contains("weaponized") ||
                       text.Contains("combat") ||
                       text.Contains("military") ||
                       text.Contains("attack") ||
                       text.Contains("bomber") ||
                       text.Contains("gunship") ||
                       text.Contains("patrol") ||
                       text.Contains("predator") ||
                       text.Contains("tank");
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}