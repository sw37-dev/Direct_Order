using GTA;
using GTA.Native;
using GTA.UI;
using iFruitAddon2;
using LemonUI;
using LemonUI.Menus;
using System;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

public partial class InstantRefill : Script
{
    // Davey contact
    private CustomiFruit _daveyPhoneInstance = null;
    private bool _daveyContactAdded = false;

    private static string DaveyContactName => T("RewardDaveyContactName", "Dave Norton");

    // Davey bribe menu UI
    private NativeMenu _luiRewardBribeMenu = null;
    private NativeItem _luiRewardBribeServiceItem = null;
    private NativeItem _luiRewardBribeLevelItem = null;
    private NativeItem _luiRewardBribeConfirmItem = null;

    private bool _rewardBribeLevelFocused = false;
    private int _rewardBribeStars = 0;

    // 1,000,000 reward points per wanted star
    private const long RewardBribeCostPerStar = 1_000_000L;
    private const int RewardBribeMaxStars = 5;

    private const int DaveyContactDialTimeoutMs = 2000;

    private void EnsureDaveyContactRegistered()
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            if (!ReferenceEquals(_daveyPhoneInstance, phone))
            {
                _daveyPhoneInstance = phone;
                _daveyContactAdded = false;
            }

            if (_daveyContactAdded)
                return;

            if (phone.Contacts.Any(c =>
                c != null &&
                string.Equals(c.Name, DaveyContactName, StringComparison.OrdinalIgnoreCase)))
            {
                _daveyContactAdded = true;
                return;
            }

            var contact = new iFruitContact(DaveyContactName)
            {
                Active = true,
                DialTimeout = DaveyContactDialTimeoutMs,
                Bold = false,
                Icon = new ContactIcon("CHAR_DAVE")
            };

            contact.Answered += OnDaveyContactAnswered;
            phone.Contacts.Add(contact);

            _daveyContactAdded = true;
        }
        catch { }
    }

    private void OnDaveyContactAnswered(iFruitContact sender)
    {
        try
        {
            OpenRewardBribeMenu();
        }
        finally
        {
            try { CustomiFruit.GetCurrentInstance()?.Close(0); } catch { }
        }
    }

    private void EnsureRewardBribeMenu()
    {
        try
        {
            if (_luiRewardBribeMenu != null)
                return;

            _luiRewardBribeMenu = new NativeMenu(
                T("RewardBribeMenuTitle", "Secret Bribe"),
                T("RewardBribeMenuDescription", "CHI TIẾT HỐI LỘ TRUY NÃ"));

            // Hide/disable mouse cursor like the Hacker menu
            _luiRewardBribeMenu.MouseBehavior = MenuMouseBehavior.Disabled;
            _luiRewardBribeMenu.ResetCursorWhenOpened = false;
            _luiRewardBribeMenu.CloseOnInvalidClick = false;
            _luiRewardBribeMenu.RotateCamera = true;

            _luiPool.Add(_luiRewardBribeMenu);
            ConfigureKeyboardOnlyVehicleMenu(_luiRewardBribeMenu);
            _luiRewardBribeMenu.Visible = false;
        }
        catch { }
    }

    private bool HandleAnyMenuInput(KeyEventArgs e)
    {
        if (HandleDaveyBribeMenuInput(e))
            return true;

        if (HandleSteveHainesBribeMenuInput(e))
            return true;

        return false;
    }

    private void OpenRewardBribeMenu()
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

            // Close other UI first so only this menu is shown
            CloseRewardMenus(false);

            EnsureRewardBribeMenu();
            BuildRewardBribeMenu();

            if (_luiRewardRootMenu != null) _luiRewardRootMenu.Visible = false;
            if (_luiRewardInsuranceMenu != null) _luiRewardInsuranceMenu.Visible = false;
            if (_luiRewardDetailMenu != null) _luiRewardDetailMenu.Visible = false;
            if (_luiRewardBribeMenu != null) _luiRewardBribeMenu.Visible = false;
            if (_luiIllegalMoneyMethodMenu != null) _luiIllegalMoneyMethodMenu.Visible = false;
            if (_luiIllegalMoneyDetailMenu != null) _luiIllegalMoneyDetailMenu.Visible = false;
            if (_luiIllegalMoneyTradeMenu != null) _luiIllegalMoneyTradeMenu.Visible = false;
            if (_luiSmugglerDetailMenu != null) _luiSmugglerDetailMenu.Visible = false;
            if (_luiSmugglerTradeMenu != null) _luiSmugglerTradeMenu.Visible = false;

            _luiRewardBribeMenu.Visible = true;
            Interval = 0;
            PlayFrontendSound("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        }
        catch
        {
        }
    }

    private void BuildRewardBribeMenu()
    {
        if (_luiRewardBribeMenu == null)
            return;

        _luiRewardBribeMenu.Clear();

        ReloadSpendingAccumulatorFromDisk();

        long points = Math.Max(0L, _spendingAccumulator.Total);
        int wantedLevel = Math.Max(0, Game.Player.WantedLevel);

        _rewardBribeLevelFocused = false;
        _rewardBribeStars = Math.Min(RewardBribeMaxStars, wantedLevel);

        var infoPoints = new NativeItem(
            string.Format(CultureInfo.InvariantCulture,
                T("RewardBribeCurrentPointsLabel", "Điểm thưởng hiện có: {0}"),
                FormatML(points)));
        _luiRewardBribeMenu.Add(infoPoints);

        _luiRewardBribeServiceItem = new NativeItem(
            T("RewardBribeServiceLabel", "Dịch vụ: Hối lộ giảm tội"));
        _luiRewardBribeMenu.Add(_luiRewardBribeServiceItem);

        var infoWanted = new NativeItem(
            string.Format(CultureInfo.InvariantCulture,
                T("RewardBribeCurrentWantedLabel", "Sao truy nã hiện tại: {0}"),
                wantedLevel));
        _luiRewardBribeMenu.Add(infoWanted);

        _luiRewardBribeLevelItem = new NativeItem(
            T("RewardBribeLevelLabel", "Mức độ giảm tội"));
        _luiRewardBribeLevelItem.Selected += (s, e) =>
        {
            _rewardBribeLevelFocused = true;
            UpdateRewardBribeMenuVisuals();
        };
        _luiRewardBribeMenu.Add(_luiRewardBribeLevelItem);

        _luiRewardBribeConfirmItem = new NativeItem(
            T("RewardBribeConfirmLabel", "Xác nhận giảm sao truy nã"));
        _luiRewardBribeConfirmItem.Activated += (s, e) =>
        {
            ConfirmWantedBribeFromMenu();
        };
        _luiRewardBribeMenu.Add(_luiRewardBribeConfirmItem);

        var back = new NativeItem(T("RewardBribeBackLabel", "Từ chối sử dụng dịch vụ"));
        back.Activated += (s, e) =>
        {
            CloseRewardBribeMenu(false);
        };
        _luiRewardBribeMenu.Add(back);

        UpdateRewardBribeMenuVisuals();
    }

    private void UpdateRewardBribeMenuVisuals()
    {
        try
        {
            if (_luiRewardBribeLevelItem != null)
            {
                _luiRewardBribeLevelItem.AltTitle = string.Format(
                    CultureInfo.InvariantCulture,
                    "\u2190 {0} \u2192",
                    Math.Max(0, _rewardBribeStars));

                _luiRewardBribeLevelItem.Description = string.Format(
                    CultureInfo.InvariantCulture,
                    T("RewardBribeLevelDesc",
                      "Số sao truy nã muốn giảm là {0} và chi phí thanh toán là {1} điểm."),
                    Math.Max(0, _rewardBribeStars),
                    FormatML(Math.Max(0, _rewardBribeStars) * RewardBribeCostPerStar));
            }

            if (_luiRewardBribeConfirmItem != null)
            {
                long cost = Math.Max(0, _rewardBribeStars) * RewardBribeCostPerStar;
                _luiRewardBribeConfirmItem.Description = string.Format(
                    CultureInfo.InvariantCulture,
                    T("RewardBribeConfirmDesc",
                      "Chi phí thanh toán là {0} điểm. Có chắc không?"),
                    FormatML(cost));
            }
        }
        catch { }
    }

    private void AdjustRewardBribeStars(int delta)
    {
        try
        {
            _rewardBribeStars += delta;
            if (_rewardBribeStars < 0) _rewardBribeStars = 0;
            if (_rewardBribeStars > RewardBribeMaxStars) _rewardBribeStars = RewardBribeMaxStars;

            UpdateRewardBribeMenuVisuals();
            PlayFrontendSound("NAV_UP_DOWN", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        }
        catch
        {
        }
    }

    private int GetCurrentWantedLevel()
    {
        try
        {
            int playerId = Function.Call<int>(Hash.PLAYER_ID);
            return Function.Call<int>(Hash.GET_PLAYER_WANTED_LEVEL, playerId);
        }
        catch
        {
            return 0;
        }
    }

    private void SetWantedLevel(int stars)
    {
        try
        {
            if (stars < 0) stars = 0;
            if (stars > SteveBribeWantedRestoreCap) stars = SteveBribeWantedRestoreCap;

            int playerId = Function.Call<int>(Hash.PLAYER_ID);
            Function.Call(Hash.SET_PLAYER_WANTED_LEVEL, playerId, stars, false);
            Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, playerId, true);

            // NEW: cập nhật trạng thái gọi được của Steve
            SyncSteveHainesContactState();
        }
        catch
        {
        }
    }

    private void ConfirmWantedBribeFromMenu()
    {
        try
        {
            int currentWanted = GetCurrentWantedLevel();
            int reduceStars = Math.Min(currentWanted, Math.Max(0, _rewardBribeStars));

            if (reduceStars <= 0)
            {
                GTA.UI.Screen.ShowSubtitle(
                    T("RewardBribeNoWantedLevel", "~HUD_COLOUR_DEGEN_YELLOW~Hiện không có sao truy nã để giảm."),
                    2500);
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            ReloadSpendingAccumulatorFromDisk();

            long cost = (long)reduceStars * RewardBribeCostPerStar;
            if (_spendingAccumulator.Total < cost)
            {
                GTA.UI.Screen.ShowSubtitle(
                    T("RewardBribeNotEnoughPoints", "~HUD_COLOUR_DEGEN_RED~Không đủ điểm để hối lộ cho mức sao này!!!"),
                    3000);
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            _spendingAccumulator.SubtractFromSpendingAccumulator(cost);

            int newWanted = Math.Max(0, currentWanted - reduceStars);
            SetWantedLevel(newWanted);

            PlayFrontendSound("PURCHASE", "HUD_FRONTEND_DEFAULT_SOUNDSET");
            CloseRewardBribeMenu(false);
        }
        catch
        {
            CloseRewardBribeMenu(false);
        }
    }

    private void CloseRewardBribeMenu(bool setCooldown)
    {
        try
        {
            if (_luiRewardBribeMenu != null)
            {
                _luiRewardBribeMenu.Visible = false;
                _luiRewardBribeMenu.Clear();
            }
        }
        catch
        {
        }

        _rewardBribeLevelFocused = false;
        _rewardBribeStars = 0;

        if (setCooldown)
            EnsureHelpBoxCooldownSet();

        Interval = 1000;
    }

    private bool HandleDaveyBribeMenuInput(KeyEventArgs e)
    {
        try
        {
            if (_luiRewardBribeMenu == null || !_luiRewardBribeMenu.Visible)
                return false;

            if ((e.KeyCode == Keys.Left || e.KeyCode == Keys.Right) && _rewardBribeLevelFocused)
            {
                AdjustRewardBribeStars(e.KeyCode == Keys.Left ? -1 : 1);
                return true;
            }

            if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
            {
                CloseRewardBribeMenu(false);
                return true;
            }
        }
        catch { }

        return false;
    }
}