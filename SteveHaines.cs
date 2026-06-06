using GTA;
using GTA.Native;
using GTA.UI;
using iFruitAddon2;
using LemonUI.Menus;
using System;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using static AssetLeaking;

public partial class InstantRefill
{
    // Steve Haines contact
    private CustomiFruit _stevePhoneInstance = null;
    private bool _steveContactAdded = false;
    private iFruitContact _steveContact = null;

    private bool _steveBribeRestorePending = false;
    private int _steveBribeRestoreDueGameTime = 0;
    private int _steveBribeRestoreStars = 0;

    private string SteveContactName => T("SteveContactName", "Steve Haines");

    // Steve Haines bribe menu UI
    private NativeMenu _luiSteveBribeMenu = null;
    private NativeItem _luiSteveBribeServiceItem = null;
    private NativeItem _luiSteveBribeWantedItem = null;
    private NativeItem _luiSteveBribeLevelItem = null;
    private NativeItem _luiSteveBribeCostItem = null;
    private NativeItem _luiSteveBribeConfirmItem = null;
    private NativeItem _luiSteveBribeBackItem = null;

    // Số sao người chơi nhập để giảm
    private int _steveBribeStars = 0;

    private const int SteveBribeMaxStars = 4;
    private const int SteveBribeFailRestoreDelayMs = 10000;
    private const int SteveBribeWantedRestoreCap = 5;
    private static readonly long[] SteveBribeCosts = { 0L, 5000L, 10000L, 25000L, 50000L };

    private static readonly object _steveRandomLock = new object();
    private static readonly Random _steveRandom = new Random();

    private void EnsureSteveHainesContactRegistered()
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            if (!ReferenceEquals(_stevePhoneInstance, phone))
            {
                _stevePhoneInstance = phone;
                _steveContactAdded = false;
                _steveContact = null;
            }

            SyncSteveHainesContactState();

            if (_steveContactAdded)
                return;

            if (phone.Contacts.Any(c =>
                c != null &&
                string.Equals(c.Name, SteveContactName, StringComparison.OrdinalIgnoreCase)))
            {
                _steveContactAdded = true;
                _steveContact = phone.Contacts.FirstOrDefault(c =>
                    c != null &&
                    string.Equals(c.Name, SteveContactName, StringComparison.OrdinalIgnoreCase));

                SyncSteveHainesContactState();
                return;
            }

            var contact = new iFruitContact(SteveContactName)
            {
                Active = GetCurrentWantedLevel() <= 4 && !IsSteveHainesBlockedByAssetLeaking(),
                DialTimeout = DaveyContactDialTimeoutMs,
                Bold = false,
                Icon = ContactIcon.Steve
            };

            contact.Answered += OnSteveHainesContactAnswered;
            phone.Contacts.Add(contact);

            _steveContact = contact;
            _steveContactAdded = true;
        }
        catch
        {
        }
    }

    private void SyncSteveHainesContactState()
    {
        try
        {
            if (_steveContact == null)
                return;

            AssetLeakingWantedState.ClearIfWantedLevelZero();
            _steveContact.Active = GetCurrentWantedLevel() <= 4 && !AssetLeakingWantedState.IsLockedByAssetLeaking;
        }
        catch
        {
        }
    }

    private void OnSteveHainesContactAnswered(iFruitContact sender)
    {
        try
        {
            OpenSteveHainesBribeMenu();
        }
        finally
        {
            try { CustomiFruit.GetCurrentInstance()?.Close(0); } catch { }
        }
    }

    private static string FormatDollar(long amount)
    {
        return string.Format(CultureInfo.InvariantCulture, "${0:N0}", Math.Max(0L, amount));
    }

    private long GetSteveBribeCost(int stars)
    {
        if (stars < 1 || stars > SteveBribeMaxStars)
            return 0L;

        return SteveBribeCosts[stars];
    }

    private bool TrySpendCurrentPlayerMoney(long amount)
    {
        try
        {
            if (amount <= 0L)
                return true;

            long currentMoney = Game.Player.Money;
            if (currentMoney < amount)
                return false;

            long remaining = currentMoney - amount;
            if (remaining < 0L)
                remaining = 0L;

            Game.Player.Money = (int)remaining;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void EnsureSteveHainesBribeMenu()
    {
        try
        {
            if (_luiSteveBribeMenu != null)
                return;

            _luiSteveBribeMenu = new NativeMenu(
                T("SteveBribeMenuTitle", "Secret Bribe"),
                T("SteveBribeMenuDescription", "CHI TIẾT HỐI LỘ TRUY NÃ"));

            _luiSteveBribeMenu.MouseBehavior = MenuMouseBehavior.Disabled;
            _luiSteveBribeMenu.ResetCursorWhenOpened = false;
            _luiSteveBribeMenu.CloseOnInvalidClick = false;
            _luiSteveBribeMenu.RotateCamera = true;

            _luiPool.Add(_luiSteveBribeMenu);
            ConfigureKeyboardOnlyVehicleMenu(_luiSteveBribeMenu);
            _luiSteveBribeMenu.Visible = false;
        }
        catch
        {
        }
    }

    private void OpenSteveHainesBribeMenu()
    {
        try
        {
            if (IsHelpBoxCooldownActive())
            {
                ShowHelpCooldownMessage();
                return;
            }

            if (_pendingType != PendingType.None)
            {
                GTA.UI.Screen.ShowSubtitle(
                    T("RewardAnotherMenuOpen", "~HUD_COLOUR_DEGEN_YELLOW~Hiện có menu khác đang mở. Hãy đóng nó trước."),
                    3000);
                return;
            }

            // Chèn đoạn mã kiểm tra Asset Leaking trước khi kiểm tra wanted >= 5
            if (IsSteveHainesBlockedByAssetLeaking())
            {
                GTA.UI.Screen.ShowSubtitle(
                    T("SteveBribeBlockedByAssetLeaking", "~HUD_COLOUR_DEGEN_RED~Steve Haines từ chối vì truy nã hiện tại đến từ việc bạn mua chuộc lậu tài sản từ ngân hàng Fleeca."),
                    3000);
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            if (GetCurrentWantedLevel() >= 5)
            {
                return;
            }

            CloseRewardMenus(false);

            EnsureSteveHainesBribeMenu();
            BuildSteveHainesBribeMenu();

            if (_luiRewardRootMenu != null) _luiRewardRootMenu.Visible = false;
            if (_luiRewardInsuranceMenu != null) _luiRewardInsuranceMenu.Visible = false;
            if (_luiRewardDetailMenu != null) _luiRewardDetailMenu.Visible = false;
            if (_luiRewardBribeMenu != null) _luiRewardBribeMenu.Visible = false;
            if (_luiIllegalMoneyMethodMenu != null) _luiIllegalMoneyMethodMenu.Visible = false;
            if (_luiIllegalMoneyDetailMenu != null) _luiIllegalMoneyDetailMenu.Visible = false;
            if (_luiIllegalMoneyTradeMenu != null) _luiIllegalMoneyTradeMenu.Visible = false;
            if (_luiSmugglerDetailMenu != null) _luiSmugglerDetailMenu.Visible = false;
            if (_luiSmugglerTradeMenu != null) _luiSmugglerTradeMenu.Visible = false;

            _luiSteveBribeMenu.Visible = true;
            Interval = 0;
            PlayFrontendSound("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        }
        catch
        {
        }
    }

    private void BuildSteveHainesBribeMenu()
    {
        if (_luiSteveBribeMenu == null)
            return;

        _luiSteveBribeMenu.Clear();

        int wantedLevel = Math.Max(0, Game.Player.WantedLevel);

        // Luôn để mức giảm mặc định là 0 trên menu
        _steveBribeStars = 0;

        _luiSteveBribeServiceItem = new NativeItem(
            T("SteveBribeServiceLabel", "Dịch vụ: Hối lộ truy nã"));
        _luiSteveBribeMenu.Add(_luiSteveBribeServiceItem);

        _luiSteveBribeWantedItem = new NativeItem(
            string.Format(CultureInfo.InvariantCulture,
                T("SteveBribeCurrentWantedLabel", "Sao truy nã hiện tại: {0}"),
                wantedLevel));
        _luiSteveBribeMenu.Add(_luiSteveBribeWantedItem);

        _luiSteveBribeLevelItem = new NativeItem(
            T("SteveBribeLevelLabel", "Mức độ giảm sao"));
        _luiSteveBribeLevelItem.Activated += (s, e) =>
        {
            OpenSteveBribeLevelInputDialog();
        };
        _luiSteveBribeMenu.Add(_luiSteveBribeLevelItem);

        _luiSteveBribeCostItem = new NativeItem(
            T("SteveBribeCostLabel", "Số tiền cần trả"));
        _luiSteveBribeMenu.Add(_luiSteveBribeCostItem);

        _luiSteveBribeConfirmItem = new NativeItem(
            T("SteveBribeConfirmLabel", "Xác nhận giao dịch ngầm"));
        _luiSteveBribeConfirmItem.Activated += (s, e) =>
        {
            ConfirmSteveHainesBribeFromMenu();
        };
        _luiSteveBribeMenu.Add(_luiSteveBribeConfirmItem);

        _luiSteveBribeBackItem = new NativeItem(
            T("SteveBribeBackLabel", "Hủy bỏ giao dịch"));
        _luiSteveBribeBackItem.Activated += (s, e) =>
        {
            CloseSteveHainesBribeMenu(false);
        };
        _luiSteveBribeMenu.Add(_luiSteveBribeBackItem);

        UpdateSteveHainesBribeMenuVisuals();
    }

    private void OpenSteveBribeLevelInputDialog()
    {
        try
        {
            if (_luiSteveBribeMenu == null || !_luiSteveBribeMenu.Visible)
                return;

            if (GetCurrentWantedLevel() >= 5)
            {
                GTA.UI.Screen.ShowSubtitle(
                    T("SteveBribeFiveStarLocked", "~HUD_COLOUR_DEGEN_RED~Steve Haines không thể gọi khi đang 5 sao truy nã."),
                    3000);
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            string input = Game.GetUserInput(string.Empty);
            if (string.IsNullOrWhiteSpace(input))
                return;

            string digits = new string(input.Where(char.IsDigit).ToArray());
            if (string.IsNullOrWhiteSpace(digits) ||
                !int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int stars))
            {
                GTA.UI.Screen.ShowSubtitle(
                    T("SteveBribeInvalidInput", "~HUD_COLOUR_DEGEN_RED~Số sao không hợp lệ."),
                    2500);
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            if (stars < 1 || stars > 4)
            {
                GTA.UI.Screen.ShowSubtitle(
                    T("SteveBribeInputRangeError", "~HUD_COLOUR_DEGEN_RED~Chỉ được nhập số từ 1 đến 4."),
                    2500);
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            _steveBribeStars = stars;
            UpdateSteveHainesBribeMenuVisuals();
            PlayFrontendSound("NAV_UP_DOWN", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        }
        catch
        {
        }
    }

    private void UpdateSteveHainesBribeMenuVisuals()
    {
        try
        {
            if (_luiSteveBribeLevelItem != null)
            {
                // Hiển thị đúng số sao đã nhập gần nhất
                _luiSteveBribeLevelItem.AltTitle = string.Format(
                    CultureInfo.InvariantCulture, "\u2190 {0} \u2192", Math.Max(0, _steveBribeStars));
                _luiSteveBribeLevelItem.Description = T(
                    "SteveBribeLevelDesc",
                    "Hãy nhập số sao muốn giảm. Chỉ chấp nhận từ 1 đến 4.");
            }

            if (_luiSteveBribeCostItem != null)
            {
                long cost = GetSteveBribeCost(Math.Max(0, _steveBribeStars));
                _luiSteveBribeCostItem.AltTitle = FormatDollar(cost);

                if (_steveBribeStars <= 0)
                {
                    _luiSteveBribeCostItem.Description = T(
                        "SteveBribeCostDescEmpty",
                        "Chưa chọn số sao cần giảm.");
                }
                else
                {
                    _luiSteveBribeCostItem.Description = string.Format(
                        CultureInfo.InvariantCulture,
                        T("SteveBribeCostDesc",
                          "Mức giá hiện tại cho {0} sao là {1}."),
                        _steveBribeStars,
                        FormatDollar(cost));
                }
            }

            if (_luiSteveBribeConfirmItem != null)
            {
                if (_steveBribeStars <= 0)
                {
                    _luiSteveBribeConfirmItem.Description = T(
                        "SteveBribeConfirmDescEmpty",
                        "Hãy nhập số sao muốn giảm trước khi xác nhận.");
                }
                else
                {
                    long cost = GetSteveBribeCost(Math.Max(0, _steveBribeStars));
                    _luiSteveBribeConfirmItem.Description = string.Format(
                        CultureInfo.InvariantCulture,
                        T("SteveBribeConfirmDesc",
                          "Xác nhận giao dịch ngầm để giảm {0} sao với chi phí {1}."),
                        _steveBribeStars,
                        FormatDollar(cost));
                }
            }
        }
        catch
        {
        }
    }

    private void ConfirmSteveHainesBribeFromMenu()
    {
        try
        {
            int currentWanted = GetCurrentWantedLevel();

            if (currentWanted >= 5)
            {
                GTA.UI.Screen.ShowSubtitle(
                    T("SteveBribeFiveStarLocked", "~HUD_COLOUR_DEGEN_RED~Steve Haines không thể gọi khi đang 5 sao truy nã."),
                    3000);
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            if (_steveBribeStars < 1 || _steveBribeStars > 4)
            {
                GTA.UI.Screen.ShowSubtitle(
                    T("SteveBribeNoSelectedLevel", "~HUD_COLOUR_DEGEN_YELLOW~Hãy chọn số sao cần giảm từ 1 đến 4 trước."),
                    3000);
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            int reduceStars = Math.Min(currentWanted, _steveBribeStars);

            if (reduceStars <= 0)
            {
                GTA.UI.Screen.ShowSubtitle(
                    T("SteveBribeNoWantedLevel", "~HUD_COLOUR_DEGEN_YELLOW~Hiện không có sao truy nã để giảm."),
                    2500);
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            long cost = GetSteveBribeCost(reduceStars);
            if (cost <= 0)
            {
                GTA.UI.Screen.ShowSubtitle(
                    T("SteveBribeInvalidCost", "~HUD_COLOUR_DEGEN_RED~Mức sao này không hợp lệ."),
                    2500);
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            if (!TrySpendCurrentPlayerMoney(cost))
            {
                GTA.UI.Screen.ShowSubtitle(
                    T("SteveBribeNotEnoughMoney", "~HUD_COLOUR_DEGEN_RED~Không đủ tiền để hối lộ."),
                    3000);
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            int newWanted = Math.Max(0, currentWanted - reduceStars);
            SetWantedLevel(newWanted);

            bool success;
            lock (_steveRandomLock)
            {
                success = _steveRandom.NextDouble() < 0.60d;
            }

            if (success)
            {
                PlayFrontendSound("PURCHASE", "HUD_FRONTEND_DEFAULT_SOUNDSET");
            }
            else
            {
                PlayFrontendSound("PURCHASE", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                ScheduleSteveBribeWantedRestore(reduceStars);
            }

            CloseSteveHainesBribeMenu(false);
        }
        catch
        {
            CloseSteveHainesBribeMenu(false);
        }
    }

    private bool IsSteveHainesBlockedByAssetLeaking()
    {
        try
        {
            AssetLeakingWantedState.ClearIfWantedLevelZero();
            return AssetLeakingWantedState.IsLockedByAssetLeaking;
        }
        catch
        {
            return false;
        }
    }

    private void ScheduleSteveBribeWantedRestore(int restoreStars)
    {
        try
        {
            if (restoreStars <= 0)
                return;

            // Gộp các lần thất bại gần nhau
            _steveBribeRestoreStars += restoreStars;
            _steveBribeRestoreDueGameTime = Game.GameTime + SteveBribeFailRestoreDelayMs;
            _steveBribeRestorePending = true;
        }
        catch
        {
        }
    }

    private void ProcessSteveBribeWantedRestore()
    {
        try
        {
            if (!_steveBribeRestorePending)
                return;

            if (Game.GameTime < _steveBribeRestoreDueGameTime)
                return;

            _steveBribeRestorePending = false;

            int restoreStars = _steveBribeRestoreStars;
            _steveBribeRestoreStars = 0;

            if (restoreStars <= 0)
                return;

            int current = GetCurrentWantedLevel();

            // Không vượt quá 5 sao
            SetWantedLevel(Math.Min(SteveBribeWantedRestoreCap, current + restoreStars));
        }
        catch
        {
        }
    }

    private void CloseSteveHainesBribeMenu(bool setCooldown)
    {
        try
        {
            if (_luiSteveBribeMenu != null)
            {
                _luiSteveBribeMenu.Visible = false;
                _luiSteveBribeMenu.Clear();
            }
        }
        catch
        {
        }

        _steveBribeStars = 0;

        if (setCooldown)
            EnsureHelpBoxCooldownSet();

        Interval = 1000;
    }

    private bool HandleSteveHainesBribeMenuInput(KeyEventArgs e)
    {
        try
        {
            if (_luiSteveBribeMenu == null || !_luiSteveBribeMenu.Visible)
                return false;

            if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
            {
                CloseSteveHainesBribeMenu(false);
                return true;
            }
        }
        catch
        {
        }

        return false;
    }
}