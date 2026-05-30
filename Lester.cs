using GTA;
using GTA.Native;
using GTA.UI;
using iFruitAddon2;
using LemonUI;
using LemonUI.Menus;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

public class Lester : Script
{
    private const string LesterContactNameFallback = "Lester";
    private const string LesterContactIconName = "HC_N_LESTER";
    private const int LesterDialTimeoutMs = 2500;

    private const int MenuCooldownMs = 5000;
    private const int InputBlockMs = 180;

    private const int DefaultMinDelta = -25;
    private const int DefaultMaxDelta = 25;

    // User requested thresholds:
    private const int XCapWhenIncreasing = -8;
    private const int YCapWhenDecreasing = 8;

    private const long BaseFee = 750000L;
    private const long FeePerStep = 25000L;
    private const long MinFee = 450000L;
    private const long MaxFee = 3500000L;

    private readonly ObjectPool _pool = new ObjectPool();
    private NativeMenu _menu = null;

    private NativeItem _serviceItem = null;
    private NativeItem _manipulationItem = null;
    private NativeItem _feeItem = null;
    private NativeItem _confirmItem = null;
    private NativeItem _cancelItem = null;

    private CustomiFruit _phoneInstance = null;
    private bool _contactAdded = false;

    private bool _manipulationFocused = true;
    private int _inputBlockUntil = 0;
    private int _cooldownExpiry = 0;

    private int _currentMinDelta = DefaultMinDelta;
    private int _currentMaxDelta = DefaultMaxDelta;

    public Lester()
    {
        Tick += OnTick;
        KeyDown += OnKeyDown;
        Interval = 1000;
    }

    private static string T(string key, string fallback)
    {
        try
        {
            return Language.Get(key, fallback);
        }
        catch
        {
            return fallback;
        }
    }

    private static string GetLesterContactName()
    {
        return T("Lester_ContactName", LesterContactNameFallback);
    }

    private static string FormatCash(long value)
    {
        if (value < 0)
            value = 0;

        return string.Format(CultureInfo.InvariantCulture, "${0:N0}", value);
    }

    private static string FormatSignedPercent(int value)
    {
        return value >= 0
            ? "+" + value.ToString(CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);
    }

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
        catch { }
    }

    private void UpdateLemonUiMouseState()
    {
        try
        {
            bool anyMenuVisible = (_menu != null && _menu.Visible);

            if (!anyMenuVisible)
                return;

            SetBoolPropertyIfExists(_menu, false,
                "MouseControlsEnabled", "MouseControls", "EnableMouseControls", "UseMouse",
                "MouseEdgeEnabled", "MouseEdgesEnabled", "AllowMouseControls");

            SetBoolPropertyIfExists(_pool, false,
                "MouseControlsEnabled", "MouseControls", "EnableMouseControls", "UseMouse",
                "MouseEdgeEnabled", "MouseEdgesEnabled", "AllowMouseControls");
        }
        catch { }
    }

    private void OnTick(object sender, EventArgs e)
    {
        try
        {
            if (Game.IsLoading || Game.IsCutsceneActive)
                return;

            EnsureLesterContactRegistered();

            if (_menu != null && _menu.Visible)
            {
                UpdateLemonUiMouseState();
                Interval = 0;
                _pool.Process();
                UpdateLemonUiMouseState();
                return;
            }

            Interval = 1000;
        }
        catch
        {
            Interval = 1000;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            if (Game.IsLoading || Game.IsCutsceneActive)
                return;

            if (_menu == null || !_menu.Visible)
                return;

            if (Game.GameTime < _inputBlockUntil)
                return;

            if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
            {
                CloseMenu(true);
                return;
            }

            if (e.KeyCode == Keys.Left)
            {
                if (_manipulationFocused)
                {
                    AdjustManipulationRange(-1);
                    BlockInput(InputBlockMs);
                }
                return;
            }

            if (e.KeyCode == Keys.Right)
            {
                if (_manipulationFocused)
                {
                    AdjustManipulationRange(+1);
                    BlockInput(InputBlockMs);
                }
                return;
            }
        }
        catch { }
    }

    private void BlockInput(int ms)
    {
        try
        {
            _inputBlockUntil = Game.GameTime + Math.Max(1, ms);
        }
        catch
        {
            _inputBlockUntil = 0;
        }
    }

    private void EnsureCooldownSet()
    {
        try
        {
            _cooldownExpiry = Game.GameTime + MenuCooldownMs;
        }
        catch
        {
            _cooldownExpiry = Game.GameTime;
        }
    }

    private bool IsCooldownActive()
    {
        try
        {
            return Game.GameTime < _cooldownExpiry;
        }
        catch
        {
            return false;
        }
    }

    private void ShowCooldownMessage()
    {
        try
        {
            int remainMs = Math.Max(0, _cooldownExpiry - Game.GameTime);
            int sec = (int)Math.Ceiling(remainMs / 1000.0);

            GTA.UI.Screen.ShowSubtitle(
                string.Format(
                    T("LesterAuctionManipulation_Cooldown",
                        "~y~Hãy đợi ~b~{0}s~s~ trước khi mở lại menu Lester."),
                    sec),
                2500);
        }
        catch { }
    }

    private void EnsureLesterContactRegistered()
    {
        try
        {
            CustomiFruit phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            string contactName = GetLesterContactName();

            if (!ReferenceEquals(_phoneInstance, phone))
            {
                _phoneInstance = phone;
                _contactAdded = false;
            }

            if (_contactAdded)
                return;

            if (phone.Contacts.Any(c =>
                c != null && string.Equals(c.Name, contactName, StringComparison.OrdinalIgnoreCase)))
            {
                _contactAdded = true;
                return;
            }

            ContactIcon icon;
            try
            {
                icon = new ContactIcon(LesterContactIconName);
            }
            catch
            {
                icon = null;
            }

            iFruitContact contact = new iFruitContact(contactName)
            {
                Active = true,
                DialTimeout = LesterDialTimeoutMs,
                Bold = false,
                Icon = icon
            };

            contact.Answered += OnLesterContactAnswered;
            phone.Contacts.Add(contact);
            _contactAdded = true;
        }
        catch { }
    }

    private void OnLesterContactAnswered(iFruitContact sender)
    {
        try
        {
            OpenLesterManipulationMenu();
        }
        finally
        {
            try { CustomiFruit.GetCurrentInstance()?.Close(0); } catch { }
        }
    }

    private void EnsureMenu()
    {
        try
        {
            if (_menu != null)
                return;

            _menu = new NativeMenu(
                T("LesterAuctionManipulation_MenuTitle", "Lester"),
                T("LesterAuctionManipulation_MenuSubtitle", "CHI TIẾT THAO TÚNG ĐẤU GIÁ"));

            _pool.Add(_menu);
            _menu.Visible = false;
        }
        catch { }
    }

    private void SyncMenuSelectionFromSavedState()
    {
        try
        {
            int minDelta;
            int maxDelta;
            int fee;

            if (LesterAuctionManipulationState.TryPeekPendingManipulationForCurrentCharacter(out minDelta, out maxDelta, out fee))
            {
                _currentMinDelta = minDelta;
                _currentMaxDelta = maxDelta;
            }
            else
            {
                _currentMinDelta = DefaultMinDelta;
                _currentMaxDelta = DefaultMaxDelta;
            }
        }
        catch
        {
            _currentMinDelta = DefaultMinDelta;
            _currentMaxDelta = DefaultMaxDelta;
        }
    }

    private void BuildMenu()
    {
        if (_menu == null)
            return;

        _menu.Clear();

        _serviceItem = new NativeItem(
            T("LesterAuctionManipulation_Service", "Dịch vụ: Thao túng thị trường đấu giá"));
        _serviceItem.Description = T(
            "LesterAuctionManipulation_ServiceDesc",
            "Điều chỉnh biên độ dao động của phiên đấu giá kế tiếp.");
        _serviceItem.Selected += (s, e) =>
        {
            _manipulationFocused = false;
        };
        _menu.Add(_serviceItem);

        _manipulationItem = new NativeItem(
            T("LesterAuctionManipulation_Level", "Mức thao túng"));
        _manipulationItem.Description = T(
            "LesterAuctionManipulation_LevelDesc",
            "Có thể thay đổi mức độ dao động. Biên độ dao động này chỉ được áp dụng một lần cho phiên đấu giá kế tiếp.");
        _manipulationItem.Selected += (s, e) =>
        {
            _manipulationFocused = true;
        };
        _menu.Add(_manipulationItem);

        _feeItem = new NativeItem(
            T("LesterAuctionManipulation_Fee", "Phí thao túng"));
        _feeItem.Description = T(
            "LesterAuctionManipulation_FeeDesc",
            "Phí sẽ được trừ ngay khi xác nhận.");
        _feeItem.Selected += (s, e) =>
        {
            _manipulationFocused = false;
        };
        _menu.Add(_feeItem);

        _confirmItem = new NativeItem(
            T("LesterAuctionManipulation_Confirm", "Xác nhận giao dịch"));
        _confirmItem.Description = T(
            "LesterAuctionManipulation_ConfirmDesc",
            "Áp dụng biên độ đã chọn cho phiên đấu giá kế tiếp một lần.");
        _confirmItem.Activated += (s, e) =>
        {
            ConfirmManipulation();
        };
        _confirmItem.Selected += (s, e) =>
        {
            _manipulationFocused = false;
        };
        _menu.Add(_confirmItem);

        _cancelItem = new NativeItem(
            T("LesterAuctionManipulation_Cancel", "Hủy bỏ giao dịch"));
        _cancelItem.Description = T(
            "LesterAuctionManipulation_CancelDesc",
            "Đóng menu.");
        _cancelItem.Activated += (s, e) =>
        {
            CloseMenu(true);
        };
        _cancelItem.Selected += (s, e) =>
        {
            _manipulationFocused = false;
        };
        _menu.Add(_cancelItem);

        UpdateMenuVisuals();
    }

    private void UpdateMenuVisuals()
    {
        try
        {
            if (_manipulationItem != null)
            {
                _manipulationItem.AltTitle = string.Format(
                    CultureInfo.InvariantCulture,
                    "\u2190{0}%\u2192 : \u2190{1}%\u2192",
                    FormatSignedPercent(_currentMinDelta),
                    FormatSignedPercent(_currentMaxDelta));
            }

            if (_feeItem != null)
            {
                _feeItem.AltTitle = FormatCash(ComputeFee(_currentMinDelta, _currentMaxDelta));
            }

            if (_confirmItem != null)
            {
                _confirmItem.Description = string.Format(
                    CultureInfo.InvariantCulture,
                    T("LesterAuctionManipulation_ConfirmDesc2",
                        "Phí hiện tại: {0}. Biên độ này chỉ áp dụng một lần cho phiên đấu giá kế tiếp."),
                    FormatCash(ComputeFee(_currentMinDelta, _currentMaxDelta)));
            }
        }
        catch { }
    }

    private void OpenLesterManipulationMenu()
    {
        try
        {
            if (Game.IsLoading || Game.IsCutsceneActive)
                return;

            if (IsCooldownActive())
            {
                ShowCooldownMessage();
                return;
            }

            EnsureMenu();
            SyncMenuSelectionFromSavedState();
            BuildMenu();

            _manipulationFocused = true;
            _menu.Visible = true;
            UpdateLemonUiMouseState();
            Interval = 0;
            BlockInput(250);
            PlayFrontendSound("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        }
        catch { }
    }

    private void CloseMenu(bool setCooldown)
    {
        try
        {
            if (_menu != null)
            {
                _menu.Visible = false;
                _menu.Clear();
            }
        }
        catch { }

        _manipulationFocused = true;

        if (setCooldown)
            EnsureCooldownSet();

        Interval = 1000;
    }

    private void AdjustManipulationRange(int delta)
    {
        try
        {
            if (delta == 0)
                return;

            int nextMin = _currentMinDelta;
            int nextMax = _currentMaxDelta;

            // Keep moving both ends together, exactly like the example:
            if (delta > 0)
            {
                if (_currentMinDelta >= XCapWhenIncreasing)
                    return;

                nextMin = _currentMinDelta + 1;
                nextMax = _currentMaxDelta + 1;

                if (nextMin > XCapWhenIncreasing)
                    nextMin = XCapWhenIncreasing;
            }
            else
            {
                if (_currentMaxDelta <= YCapWhenDecreasing)
                    return;

                nextMin = _currentMinDelta - 1;
                nextMax = _currentMaxDelta - 1;

                if (nextMax < YCapWhenDecreasing)
                    nextMax = YCapWhenDecreasing;
            }

            _currentMinDelta = nextMin;
            _currentMaxDelta = nextMax;

            UpdateMenuVisuals();
            PlayFrontendSound("NAV_UP_DOWN", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        }
        catch { }
    }

    private int ComputeFee(int minDelta, int maxDelta)
    {
        try
        {
            long distance =
                Math.Abs(minDelta - DefaultMinDelta) +
                Math.Abs(maxDelta - DefaultMaxDelta);

            long fee = BaseFee + (distance * FeePerStep);
            if (fee < MinFee) fee = MinFee;
            if (fee > MaxFee) fee = MaxFee;

            return (int)Math.Min(int.MaxValue, fee);
        }
        catch
        {
            return (int)BaseFee;
        }
    }

    private void ConfirmManipulation()
    {
        try
        {
            int fee = ComputeFee(_currentMinDelta, _currentMaxDelta);
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead)
            {
                CloseMenu(false);
                return;
            }

            if (Game.Player.Money < fee)
            {
                GTA.UI.Screen.ShowSubtitle(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        T("LesterAuctionManipulation_NotEnough",
                            "~r~Không đủ tiền~s~ để thao túng đấu giá."),
                        FormatCash(fee)),
                    3000);
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            Game.Player.Money -= fee;
            LesterAuctionManipulationState.ArmForCurrentCharacter(_currentMinDelta, _currentMaxDelta, fee);

            GTA.UI.Screen.ShowSubtitle(
                string.Format(
                    CultureInfo.InvariantCulture,
                    T("LesterAuctionManipulation_Success",
                        "~HUD_COLOUR_DEGEN_GREEN~Đã kích hoạt thao túng đấu giá"),
                    string.Format(CultureInfo.InvariantCulture, "\u2190{0}%\u2192", FormatSignedPercent(_currentMinDelta)),
                    string.Format(CultureInfo.InvariantCulture, "\u2190{0}%\u2192", FormatSignedPercent(_currentMaxDelta))),
                3500);

            PlayFrontendSound("PURCHASE", "HUD_FRONTEND_DEFAULT_SOUNDSET");
            CloseMenu(false);
        }
        catch
        {
            CloseMenu(false);
        }
    }

    private void PlayFrontendSound(string soundName, string soundSet)
    {
        try
        {
            Audio.PlaySoundFrontend(soundName, soundSet);
        }
        catch
        {
            try
            {
                Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, soundName, soundSet, true);
            }
            catch { }
        }
    }
}

internal static class LesterAuctionManipulationState
{
    private sealed class Session
    {
        public int MinDelta;
        public int MaxDelta;
        public int Fee;
        public bool Armed;
    }

    private static readonly object _sync = new object();
    private static readonly Dictionary<int, Session> _sessions = new Dictionary<int, Session>();

    private static int GetCurrentCharacterHash()
    {
        try
        {
            Ped p = Game.Player.Character;
            if (p != null && p.Exists())
                return p.Model.Hash;
        }
        catch
        {
        }

        return 0;
    }

    public static void ArmForCurrentCharacter(int minDelta, int maxDelta, int fee)
    {
        int ownerHash = GetCurrentCharacterHash();
        if (ownerHash == 0)
            return;

        lock (_sync)
        {
            _sessions[ownerHash] = new Session
            {
                MinDelta = minDelta,
                MaxDelta = maxDelta,
                Fee = Math.Max(0, fee),
                Armed = true
            };
        }
    }

    public static bool TryPeekPendingManipulationForCurrentCharacter(out int minDelta, out int maxDelta, out int fee)
    {
        minDelta = -25;
        maxDelta = 25;
        fee = 0;

        int ownerHash = GetCurrentCharacterHash();
        if (ownerHash == 0)
            return false;

        lock (_sync)
        {
            Session s;
            if (_sessions.TryGetValue(ownerHash, out s) && s != null && s.Armed)
            {
                minDelta = s.MinDelta;
                maxDelta = s.MaxDelta;
                fee = s.Fee;
                return true;
            }
        }

        return false;
    }

    public static bool HasPendingManipulationForCurrentCharacter()
    {
        int ownerHash = GetCurrentCharacterHash();
        if (ownerHash == 0)
            return false;

        lock (_sync)
        {
            Session s;
            return _sessions.TryGetValue(ownerHash, out s) && s != null && s.Armed;
        }
    }

    public static bool TryTakePendingManipulationForCurrentCharacter(out int minDelta, out int maxDelta, out int fee)
    {
        minDelta = -25;
        maxDelta = 25;
        fee = 0;

        int ownerHash = GetCurrentCharacterHash();
        if (ownerHash == 0)
            return false;

        lock (_sync)
        {
            Session s;
            if (!_sessions.TryGetValue(ownerHash, out s) || s == null || !s.Armed)
                return false;

            minDelta = s.MinDelta;
            maxDelta = s.MaxDelta;
            fee = s.Fee;

            _sessions.Remove(ownerHash);
            return true;
        }
    }

    public static void ClearForCurrentCharacter()
    {
        int ownerHash = GetCurrentCharacterHash();
        if (ownerHash == 0)
            return;

        lock (_sync)
        {
            _sessions.Remove(ownerHash);
        }
    }
}
