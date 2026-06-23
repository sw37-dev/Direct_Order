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
using static AssetLeaking;

public partial class InstantRefill : Script
{
    // Davey contact
    private CustomiFruit _daveyPhoneInstance = null;
    private bool _daveyContactAdded = false;

    private iFruitContact _daveyContact = null;

    private static string DaveyContactName => T("RewardDaveyContactName", "Dave Norton");

    // Davey bribe menu UI
    private NativeMenu _luiRewardBribeMenu = null;
    private NativeItem _luiRewardBribeServiceItem = null;
    private NativeItem _luiRewardBribeLevelItem = null;
    private NativeItem _luiRewardBribeConfirmItem = null;

    private bool _rewardBribeLevelFocused = false;
    private int _rewardBribeStars = 0;
    private bool _rewardBribeAssetLeakingMode = false;

    // Normal mode: 1,000,000 reward points per wanted star
    private const long RewardBribeCostPerStar = 1_000_000L;
    private const int RewardBribeMaxStars = 5;
    private const long AssetLeakingRewardBribeCostPerStar = 5_000_000L;
    private const int AssetLeakingRewardBribeMaxStars = 4;

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
                _daveyContact = null;
            }

            if (_daveyContactAdded)
            {
                SyncDaveyContactState();
                return;
            }

            _daveyContact = phone.Contacts.FirstOrDefault(c =>
                c != null &&
                string.Equals(c.Name, DaveyContactName, StringComparison.OrdinalIgnoreCase));

            if (_daveyContact != null)
            {
                _daveyContactAdded = true;
                SyncDaveyContactState();
                return;
            }

            var contact = new iFruitContact(DaveyContactName)
            {
                Active = !IsDaveyBlockedByFleecaDebt(),
                DialTimeout = DaveyContactDialTimeoutMs,
                Bold = false,
                Icon = ContactIcon.Dave
            };

            contact.Answered += OnDaveyContactAnswered;
            phone.Contacts.Add(contact);

            _daveyContact = contact;
            _daveyContactAdded = true;
            SyncDaveyContactState();
        }
        catch
        {
        }
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

    private bool IsRewardBribeAssetLeakingMode()
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

    private bool IsDaveyBlockedByFleecaDebt()
    {
        try
        {
            FleecaDebtWantedState.ClearIfWantedLevelZero();
            return FleecaDebtWantedState.IsLockedByFleecaDebt;
        }
        catch
        {
            return false;
        }
    }

    private void SyncDaveyContactState()
    {
        try
        {
            if (_daveyContact == null)
                return;

            _daveyContact.Active = !IsDaveyBlockedByFleecaDebt();
        }
        catch
        {
        }
    }

    private int GetRewardBribeMaxStars()
    {
        return _rewardBribeAssetLeakingMode
            ? AssetLeakingRewardBribeMaxStars
            : RewardBribeMaxStars;
    }

    private long GetRewardBribeCostPerStar()
    {
        return _rewardBribeAssetLeakingMode
            ? AssetLeakingRewardBribeCostPerStar
            : RewardBribeCostPerStar;
    }

    private string GetRewardBribeServiceDescription()
    {
        return _rewardBribeAssetLeakingMode
            ? T(
                "RewardBribeServiceDescAssetLeaking",
                "Trạng thái hiện tại đến từ việc mua chuộc phương tiện từ Fleeca. Dave Norton chỉ hỗ trợ tối đa 4 sao, giá 5.000.000 điểm mỗi sao.")
            : T(
                "RewardBribeServiceDescNormal",
                "Trạng thái hiện tại là truy nã bình thường. Dave Norton hỗ trợ tối đa 5 sao, giá 1.000.000 điểm mỗi sao.");
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

            // Chặn riêng cho trạng thái nợ Fleeca
            if (IsDaveyBlockedByFleecaDebt())
            {
                GTA.UI.Screen.ShowSubtitle(
                    T("RewardDaveyBlockedByFleecaDebt",
                      "~HUD_COLOUR_DEGEN_RED~Dave Norton từ chối cuộc gọi vì truy nã này đến từ nhánh thu nợ/tất toán Fleeca."),
                    3000);
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
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
        _rewardBribeAssetLeakingMode = IsRewardBribeAssetLeakingMode();
        _rewardBribeStars = Math.Min(GetRewardBribeMaxStars(), wantedLevel);

        var infoPoints = new NativeItem(
            string.Format(CultureInfo.InvariantCulture,
                T("RewardBribeCurrentPointsLabel", "Điểm thưởng hiện có: {0}"),
                FormatML(points)));
        _luiRewardBribeMenu.Add(infoPoints);

        _luiRewardBribeServiceItem = new NativeItem(
            T("RewardBribeServiceLabel", "Dịch vụ: Hối lộ giảm tội"));
        _luiRewardBribeServiceItem.Description = GetRewardBribeServiceDescription();
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
                int maxStars = GetRewardBribeMaxStars();
                long costPerStar = GetRewardBribeCostPerStar();

                _luiRewardBribeLevelItem.AltTitle = string.Format(
                    CultureInfo.InvariantCulture,
                    "\u2190 {0} \u2192",
                    Math.Max(0, Math.Min(_rewardBribeStars, maxStars)));

                _luiRewardBribeLevelItem.Description = string.Format(
                    CultureInfo.InvariantCulture,
                    T("RewardBribeLevelDesc",
                      "Số sao truy nã muốn giảm là {0}. Giới hạn hiện tại là {1} sao. Chi phí thanh toán là {2} điểm/sao."),
                    Math.Max(0, _rewardBribeStars),
                    maxStars,
                    FormatML(costPerStar));
            }

            if (_luiRewardBribeConfirmItem != null)
            {
                long cost = Math.Max(0, _rewardBribeStars) * GetRewardBribeCostPerStar();
                _luiRewardBribeConfirmItem.Description = string.Format(
                    CultureInfo.InvariantCulture,
                    T("RewardBribeConfirmDesc",
                      "Chi phí thanh toán là {0} điểm. Có chắc không?"),
                    FormatML(cost));
            }

            if (_luiRewardBribeServiceItem != null)
            {
                _luiRewardBribeServiceItem.Description = GetRewardBribeServiceDescription();
            }
        }
        catch { }
    }

    private void AdjustRewardBribeStars(int delta)
    {
        try
        {
            int maxStars = GetRewardBribeMaxStars();
            _rewardBribeStars += delta;
            if (_rewardBribeStars < 0) _rewardBribeStars = 0;
            if (_rewardBribeStars > maxStars) _rewardBribeStars = maxStars;

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
            int maxStars = GetRewardBribeMaxStars();
            int reduceStars = Math.Min(currentWanted, Math.Min(maxStars, Math.Max(0, _rewardBribeStars)));

            if (reduceStars <= 0)
            {
                GTA.UI.Screen.ShowSubtitle(
                    T("RewardBribeNoWantedLevel", "~HUD_COLOUR_DEGEN_YELLOW~Hiện không có sao truy nã để giảm."),
                    2500);
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            ReloadSpendingAccumulatorFromDisk();

            long costPerStar = GetRewardBribeCostPerStar();
            long cost = (long)reduceStars * costPerStar;
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

    public static class FleecaDebtWantedState
    {
        private static readonly object _sync = new object();
        private static bool _lockedByFleecaDebt = false;

        public static bool IsLockedByFleecaDebt
        {
            get
            {
                lock (_sync)
                    return _lockedByFleecaDebt;
            }
        }

        public static void MarkFleecaDebtWantedActive()
        {
            lock (_sync)
                _lockedByFleecaDebt = true;
        }

        public static void ClearIfWantedLevelZero()
        {
            try
            {
                if (Game.Player == null)
                    return;

                if (Game.Player.WantedLevel <= 0)
                {
                    lock (_sync)
                        _lockedByFleecaDebt = false;
                }
            }
            catch
            {
            }
        }
    }
}