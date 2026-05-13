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

public class CityBlackout : Script
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
    private const int PAIGE_BLACKOUT_DELAY_MS = 5000;
    private const int PAIGE_WANTED_DELAY_MS = 25000;

    private int _paigeBlackoutStartGameTime = -1;   // sau 5 giây mới cúp điện
    private int _paigeWantedGrantGameTime = -1;     // sau 25 giây mới gán sao
    private bool _paigeWantedGranted = false;       // đã gán sao chưa

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

    // Constructor
    public CityBlackout()
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

    // Thêm vào trong class CityBlackout
    private void ShowPaigeHarrisNotification(string title, string message, int timeout = PaigeNotificationTimeout)
    {
        try
        {
            Function.Call(Hash.BEGIN_TEXT_COMMAND_THEFEED_POST, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, message);

            Function.Call(Hash.END_TEXT_COMMAND_THEFEED_POST_MESSAGETEXT,
                "CHAR_PAIGE",
                "CHAR_PAIGE",
                false,
                0,
                PaigeHarrisNotificationBrand,
                title);

            Function.Call(Hash.END_TEXT_COMMAND_THEFEED_POST_TICKER, false, true);
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
        }
        catch (Exception ex)
        {
            GTA.UI.Notification.Show(T("CityBlackout_RuntimeError", "CityBlackout error: ") + ex.Message);
        }
    }

    private void HandlePaigeDeferredActions()
    {
        int now = Game.GameTime;

        // 3 giây sau khi gọi Paige mới bắt đầu blackout
        if (_paigeBlackoutStartGameTime > 0 && now >= _paigeBlackoutStartGameTime)
        {
            _paigeBlackoutStartGameTime = -1;
            StartBlackout(BlackoutSource.Paige);
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

    private void StartBlackout(BlackoutSource source)
    {
        try
        {
            _blackoutSource = source;
            _blackoutActive = true;
            _blackoutEndGameTime = Game.GameTime + _cfgBlackoutDurationMs;
            _usedAffectsNative = false;
            _forcedVehicleHandles.Clear();
            _smoothingActive = false;

            // Chỉ blackout từ Paige mới được phép gán sao, nhưng gán sau 10 giây
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

                Interval = IDLE_INTERVAL_MS;
                return;
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
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.Show(T("CityBlackout_StartBlackoutFailed", "StartBlackout failed: ") + ex.Message);
                _blackoutActive = false;
            }

            Interval = SMOOTH_INTERVAL_MS;
        }
        catch (Exception ex)
        {
            GTA.UI.Notification.Show(T("CityBlackout_StartBlackoutFailed", "StartBlackout failed: ") + ex.Message);
            _blackoutActive = false;
        }
    }

    private void StartBlackoutAndLock()
    {
        int dayKey = GetCurrentGameDayKey();
        if (dayKey == -1)
            dayKey = _lastObservedDayKey;

        if (IsBlackoutLockedToday(dayKey))
            return;

        _blackoutLockedDayKey = dayKey;
        StartBlackout(BlackoutSource.Auto);
    }

    private void StartPaigeBlackoutSequence()
    {
        int dayKey = GetCurrentGameDayKey();
        if (dayKey == -1)
            dayKey = _lastObservedDayKey;

        if (IsBlackoutLockedToday(dayKey))
            return;

        // khóa ngày ngay khi bấm gọi Paige để không có blackout tự động chen vào
        _blackoutLockedDayKey = dayKey;

        _paigeBlackoutStartGameTime = Game.GameTime + PAIGE_BLACKOUT_DELAY_MS;
        _paigeWantedGranted = false;
        _paigeWantedGrantGameTime = -1;

        ShowPaigeHarrisNotification(
            T("CityBlackout_Title", "Cúp điện"),
            T("CityBlackout_Message", "Tôi sẽ truy cập hệ thống điện thành phố và cắt điện nhưng cảnh sát sẽ truy ra anh rất nhanh đấy!")
        );
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
                DialTimeout = 10000,
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
            // Nếu blackout hiện tại là do Paige tạo ra thì gọi lại Paige = bật điện lại
            if (_blackoutActive && _blackoutSource == BlackoutSource.Paige)
            {
                _paigeBlackoutStartGameTime = -1;
                _paigeWantedGrantGameTime = -1;
                _paigeWantedGranted = true;

                StartSmoothingSequence();
            }
            else
            {
                // Còn lại: gọi Paige để chuẩn bị cúp điện sau 3 giây
                StartPaigeBlackoutSequence();
            }
        }
        finally
        {
            try
            {
                CustomiFruit.GetCurrentInstance()?.Close(0);
            }
            catch { }
        }
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
            }
            catch { }
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