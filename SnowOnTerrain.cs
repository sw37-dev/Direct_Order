using GTA;
using GTA.Native;
using GTA.UI;
using System;
using System.Windows.Forms;

public class SnowTerrainToggle : Script
{
    // Native: _FORCE_GROUND_SNOW_PASS
    private static readonly Hash SnowNative = (Hash)0x6E9EF3A33C8899F8UL;

    // Weather natives
    private static readonly Hash SetWeatherNowPersistNative = Hash.SET_WEATHER_TYPE_NOW_PERSIST;
    private static readonly Hash SetWeatherPersistNative = Hash.SET_WEATHER_TYPE_PERSIST;
    private static readonly Hash SetOverrideWeatherNative = Hash.SET_OVERRIDE_WEATHER;
    private static readonly Hash ClearOverrideWeatherNative = Hash.CLEAR_OVERRIDE_WEATHER;
    private static readonly Hash ClearWeatherTypePersistNative = Hash.CLEAR_WEATHER_TYPE_PERSIST;

    // Trạng thái map tuyết
    private bool _snowPassEnabled = false;

    // Trạng thái khóa thời tiết
    private bool _weatherLocked = false;
    private string _lockedWeather = "";

    // Trạng thái menu chọn weather
    private bool _weatherMenuOpen = false;
    private int _weatherMenuEndTime = 0;
    private int _weatherIndex = 0;

    // Phím
    private readonly Keys KeyWeatherMenu = Keys.Multiply;  // Numpad *

    // Chặn bấm quá nhanh
    private int _lastKeyGameTime = -99999;
    private const int KeyCooldownMs = 180;

    // Thời gian tồn tại menu
    private const int WeatherMenuDurationMs = 30000;

    // Tên weather native tương ứng
    private readonly string[] _weatherNativeNames =
    {
        "BLIZZARD",
        "CLEAR",
        "CLEARING",
        "CLOUDS",
        "EXTRASUNNY",
        "FOGGY",
        "NEUTRAL",
        "OVERCAST",
        "RAIN",
        "SMOG",
        "SNOW",
        "SNOWLIGHT",
        "THUNDER"
    };

    // Key trong file ngôn ngữ cho label hiển thị
    private readonly string[] _weatherDisplayKeys =
    {
        "WeatherName_Blizzard",
        "WeatherName_Clear",
        "WeatherName_Clearing",
        "WeatherName_Clouds",
        "WeatherName_Extrasunny",
        "WeatherName_Foggy",
        "WeatherName_Neutral",
        "WeatherName_Overcast",
        "WeatherName_Rain",
        "WeatherName_Smog",
        "WeatherName_Snow",
        "WeatherName_Snowlight",
        "WeatherName_Thunder"
    };

    // Fallback nếu file ngôn ngữ không có key
    private readonly string[] _weatherDisplayFallbackNames =
    {
        "Blizzard",
        "Clear",
        "Clearing",
        "Clouds",
        "Extrasunny",
        "Foggy",
        "Neutral",
        "Overcast",
        "Rain",
        "Smog",
        "Snow",
        "Snowlight",
        "Thunder"
    };

    // Weather ngẫu nhiên dành riêng cho map tuyết
    private readonly string[] _snowWeatherPool =
    {
        "BLIZZARD",
        "OVERCAST",
        "SNOWLIGHT",
        "FOGGY",
        "SNOW"
    };

    private readonly Random _rng = new Random();

    public SnowTerrainToggle()
    {
        Language.Initialize();

        KeyDown += OnKeyDown;
        Tick += OnTick;
    }

    private void OnTick(object sender, EventArgs e)
    {
        if (Game.IsLoading || Game.IsCutsceneActive)
            return;

        if (_weatherMenuOpen)
        {
            if (Game.GameTime >= _weatherMenuEndTime)
            {
                CloseWeatherMenu();
                return;
            }

            GTA.UI.Screen.ShowHelpTextThisFrame(BuildWeatherMenuHelpText());
        }

        // Nếu đang bật snow pass thì ép giữ
        if (_snowPassEnabled)
        {
            try
            {
                Function.Call(SnowNative, true);
            }
            catch
            {
                // Bỏ qua lỗi native
            }
        }

        // Nếu đang khóa weather thì ép giữ
        if (_weatherLocked && !string.IsNullOrWhiteSpace(_lockedWeather))
        {
            try
            {
                Function.Call(SetWeatherNowPersistNative, _lockedWeather);
                Function.Call(SetWeatherPersistNative, _lockedWeather);
                Function.Call(SetOverrideWeatherNative, _lockedWeather);
            }
            catch
            {
                // Bỏ qua lỗi native
            }
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Game.IsLoading || Game.IsCutsceneActive)
            return;

        if (Game.GameTime - _lastKeyGameTime < KeyCooldownMs)
            return;

        _lastKeyGameTime = Game.GameTime;

        // Numpad *
        if (e.KeyCode == KeyWeatherMenu)
        {
            ToggleWeatherMenu();
            return;
        }

        // Nếu menu đang mở thì chỉ xử lý điều hướng menu
        if (_weatherMenuOpen)
        {
            HandleWeatherMenuInput(e.KeyCode);
        }
    }

    private void ToggleWeatherMenu()
    {
        if (_weatherMenuOpen)
            CloseWeatherMenu();
        else
            OpenWeatherMenu();
    }

    private void OpenWeatherMenu()
    {
        _weatherMenuOpen = true;
        _weatherMenuEndTime = Game.GameTime + WeatherMenuDurationMs;

        if (_weatherIndex < 0 || _weatherIndex >= GetMenuItemCount())
            _weatherIndex = 0;

        // Nếu đang khóa weather rồi thì cố gắng đưa menu về đúng weather hiện tại
        if (_weatherLocked && !string.IsNullOrWhiteSpace(_lockedWeather))
        {
            int idx = Array.IndexOf(_weatherNativeNames, _lockedWeather);
            if (idx >= 0)
                _weatherIndex = idx;
        }

        PlayFrontendSound("NAV_UP_DOWN", "HUD_FRONTEND_DEFAULT_SOUNDSET");
    }

    private void CloseWeatherMenu()
    {
        _weatherMenuOpen = false;
        PlayFrontendSound("BACK", "HUD_FRONTEND_DEFAULT_SOUNDSET");
    }

    private void HandleWeatherMenuInput(Keys keyCode)
    {
        if (!_weatherMenuOpen)
            return;

        if (keyCode == Keys.Left)
        {
            _weatherIndex = (_weatherIndex - 1 + GetMenuItemCount()) % GetMenuItemCount();
            _weatherMenuEndTime = Game.GameTime + WeatherMenuDurationMs;
            PlayFrontendSound("NAV_LEFT_RIGHT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        }
        else if (keyCode == Keys.Right)
        {
            _weatherIndex = (_weatherIndex + 1) % GetMenuItemCount();
            _weatherMenuEndTime = Game.GameTime + WeatherMenuDurationMs;
            PlayFrontendSound("NAV_LEFT_RIGHT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        }
        else if (keyCode == Keys.Enter)
        {
            if (ExecuteSelectedMenuItem())
                CloseWeatherMenu();
        }
        else if (keyCode == Keys.Back || keyCode == Keys.Escape)
        {
            CloseWeatherMenu();
        }
    }

    private int GetMenuItemCount()
    {
        // weather list + Reset Weather + Snow ON + Snow OFF
        return _weatherNativeNames.Length + 3;
    }

    private bool IsWeatherIndex(int index)
    {
        return index >= 0 && index < _weatherNativeNames.Length;
    }

    private string GetWeatherDisplayName(int index)
    {
        if (index < 0 || index >= _weatherDisplayFallbackNames.Length)
            return string.Empty;

        return Language.Get(_weatherDisplayKeys[index], _weatherDisplayFallbackNames[index]);
    }

    private string GetMenuItemLabel(int index)
    {
        if (IsWeatherIndex(index))
            return GetWeatherDisplayName(index);

        if (index == _weatherNativeNames.Length)
            return Language.Get("MenuResetWeather", "Reset Weather");

        if (index == _weatherNativeNames.Length + 1)
            return Language.Get("MenuSnowOnTerrainEnabled", "Snow On Terrain (ON)");

        return Language.Get("MenuSnowOnTerrainDisabled", "Snow On Terrain (OFF)");
    }

    private bool ExecuteSelectedMenuItem()
    {
        try
        {
            if (IsWeatherIndex(_weatherIndex))
                return ApplySelectedWeather();

            if (_weatherIndex == _weatherNativeNames.Length)
                return ResetWeatherToGame();

            if (_weatherIndex == _weatherNativeNames.Length + 1)
                return ToggleSnowAndWeather(true);

            return ToggleSnowAndWeather(false);
        }
        catch (Exception ex)
        {
            Notification.Show(Language.Get("ErrorExecutePrefix", "Lỗi khi thực thi: ") + ex.Message);
            PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
            return false;
        }
    }

    private bool ApplySelectedWeather()
    {
        if (_snowPassEnabled)
        {
            GTA.UI.Screen.ShowSubtitle(
                "~HUD_COLOUR_YELLOW~" + Language.Get("ErrorDisableSnowBeforeWeather", "Hãy tắt Snow On Terrain trước khi đổi weather."),
                3000
            );
            PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
            return false;
        }

        try
        {
            string weather = _weatherNativeNames[_weatherIndex];
            ApplyWeatherNative(weather);

            _weatherLocked = true;
            _lockedWeather = weather;

            Notification.Show("~b~" + Language.Get("LabelWeather", "Weather") + ": ~h~" + weather);
            PlayFrontendSound("PURCHASE", "HUD_FRONTEND_DEFAULT_SOUNDSET");
            return true;
        }
        catch (Exception ex)
        {
            Notification.Show(Language.Get("ErrorLockWeatherPrefix", "Lỗi khi khóa thời tiết: ") + ex.Message);
            PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
            return false;
        }
    }

    private bool ResetWeatherToGame()
    {
        try
        {
            RestoreNormalGameWeatherState();
            PlayFrontendSound("BACK", "HUD_FRONTEND_DEFAULT_SOUNDSET");
            return true;
        }
        catch (Exception ex)
        {
            Notification.Show(Language.Get("ErrorResetWeatherPrefix", "Lỗi khi reset weather: ") + ex.Message);
            PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
            return false;
        }
    }

    private bool ToggleSnowAndWeather(bool enable)
    {
        try
        {
            if (enable)
            {
                string weather = PickRandomWeather();

                Function.Call(SnowNative, true);
                ApplyWeatherNative(weather);

                _snowPassEnabled = true;
                _weatherLocked = true;
                _lockedWeather = weather;

                Notification.Show(
                    "~g~" + Language.Get("MessageSnowOnTerrainEnabled", "Snow On Terrain: ON") +
                    "~n~~b~" + Language.Get("LabelWeather", "Weather") + ": ~h~" + weather
                );

                PlayFrontendSound("PURCHASE", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return true;
            }
            else
            {
                Function.Call(SnowNative, false);
                ClearWeatherLockOnly();

                _snowPassEnabled = false;

                Notification.Show(
                    "~r~" + Language.Get("MessageSnowOnTerrainDisabled", "Snow On Terrain: OFF") +
                    "~n~~b~" + Language.Get("LabelWeather", "Weather") + ": ~h~" +
                    Language.Get("StateUnlocked", "ĐÃ MỞ KHÓA")
                );

                PlayFrontendSound("BACK", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return true;
            }
        }
        catch (Exception ex)
        {
            Notification.Show(Language.Get("ErrorNativeCallPrefix", "Lỗi khi gọi native: ") + ex.Message);
            PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
            return false;
        }
    }

    private void RestoreNormalGameWeatherState()
    {
        CloseWeatherMenu();

        try
        {
            Function.Call(SnowNative, false);
        }
        catch
        {
            // Bỏ qua lỗi native
        }

        try
        {
            Function.Call(ClearOverrideWeatherNative);
            Function.Call(ClearWeatherTypePersistNative);
        }
        catch
        {
            // Bỏ qua lỗi native
        }

        _snowPassEnabled = false;
        _weatherLocked = false;
        _lockedWeather = "";
    }

    private void ApplyWeatherNative(string weather)
    {
        Function.Call(ClearOverrideWeatherNative);
        Function.Call(ClearWeatherTypePersistNative);

        Function.Call(SetWeatherNowPersistNative, weather);
        Function.Call(SetWeatherPersistNative, weather);
        Function.Call(SetOverrideWeatherNative, weather);
    }

    private void ClearWeatherLockOnly()
    {
        try
        {
            Function.Call(ClearOverrideWeatherNative);
            Function.Call(ClearWeatherTypePersistNative);

            _weatherLocked = false;
            _lockedWeather = "";
        }
        catch
        {
            // Bỏ qua lỗi native
        }
    }

    private string BuildWeatherMenuHelpText()
    {
        string current = GetMenuItemLabel(_weatherIndex);
        string next = GetMenuItemLabel((_weatherIndex + 1) % GetMenuItemCount());

        string text = Language.ReplaceTokens(
            Language.Get("WeatherMenuHelpText", "~h~Bạn đang muốn sử dụng loại nào: ~y~(hiện tại)~s~ ~HUD_COLOUR_DEGEN_GREEN~{current}~s~ ~o~>~s~ {next}"),
            "{current}", current,
            "{next}", next
        );

        return "~h~" + text;
    }

    private string PickRandomWeather()
    {
        return _snowWeatherPool[_rng.Next(_snowWeatherPool.Length)];
    }

    private void PlayFrontendSound(string soundName, string soundSet)
    {
        try
        {
            Audio.PlaySoundFrontend(soundName, soundSet);
        }
        catch
        {
            // Bỏ qua lỗi âm thanh
        }
    }
}