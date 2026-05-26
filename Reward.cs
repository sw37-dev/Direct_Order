using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using iFruitAddon2;
using LemonUI;
using LemonUI.Menus;
using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;

public partial class InstantRefill : Script
{
    // Reward vehicle detail UI
    private NativeMenu _luiRewardDetailMenu = null;
    private VehicleEntry _rewardDetailVehicle = null;
    private long _rewardDetailPrice = 0;
    private string _rewardDetailPlate = "";
    private bool _rewardPlateEdited = false;

    private string RewardPlateHintBeforeEdit => T("RewardPlateHintBeforeEdit", "Nhấn Enter để điều chỉnh biển số theo ý muốn.");
    private string RewardPlateHintAfterEdit => T("RewardPlateHintAfterEdit", "Đây là biển số mới sẽ được dùng, có thể đổi lại.");

    // Reward root / sub menus
    private NativeMenu _luiRewardRootMenu = null;
    private NativeMenu _luiRewardInsuranceMenu = null;

    // Insurance UI
    private NativeCheckboxItem _luiRewardInsuranceTicketItem = null;
    private NativeItem _luiRewardInsuranceValueItem = null;
    private NativeItem _luiRewardInsuranceConfirmItem = null;

    private static readonly BadgeSet _smugglerLockBadge = new BadgeSet
    {
        NormalDictionary = "commonmenu",
        NormalTexture = "shop_lock",
        HoveredDictionary = "commonmenu",
        HoveredTexture = "shop_lock_b"
    };

    private sealed class RewardRightBadgeItem : NativeItem
    {
        private readonly BadgeSet _badge;
        private readonly Func<bool> _shouldShowBadge;

        public RewardRightBadgeItem(string title, string description, BadgeSet badge)
            : this(title, description, badge, null)
        {
        }

        public RewardRightBadgeItem(string title, string description, BadgeSet badge, Func<bool> shouldShowBadge)
            : base(title, description)
        {
            _badge = badge;
            _shouldShowBadge = shouldShowBadge ?? (() => true);
        }

        public override void Recalculate(PointF pos, SizeF size, bool selected)
        {
            base.Recalculate(pos, size, selected);

            SetRightBadgeSetIfExists(this, (!selected && _shouldShowBadge()) ? _badge : null);

            UpdateColors();
        }
    }

    // Reward state
    private bool _rewardInsuranceChecked = false;
    private int _rewardMenuInputBlockUntil = 0;

    // --- Maze Bank ATM (illegal money conversion) ---
    private const int MazeBankAtmSpawnDelayMs = 5000;
    private static readonly Vector3 MazeBankAtmPosition = new Vector3(-243.7571f, -1968.992f, 29.5528f);
    private const float MazeBankAtmHeading = 110.3329f;
    private const float MazeBankAtmRadius = 2.0f;

    private int _mazeBankAtmSpawnReadyTime = 0;
    private bool _mazeBankAtmSpawned = false;

    private NativeMenu _luiIllegalMoneyDetailMenu = null;
    private NativeMenu _luiIllegalMoneyTradeMenu = null;

    private NativeItem _illegalMoneyOwnerItem = null;
    private NativeItem _illegalMoneyBalanceItem = null;
    private NativeItem _illegalMoneyCurrentItem = null;
    private NativeItem _illegalMoneyRatioItem = null;
    private NativeItem _illegalMoneyAmountItem = null;
    private NativeItem _illegalMoneyReceiveItem = null;
    private NativeItem _illegalMoneyConfirmItem = null;

    private bool _illegalMoneyRedeemArmed = false;
    private long _illegalMoneyConvertAmount = -1;
    private long _illegalMoneyExpectedCash = -1;

    // --- Smuggler branch (illegal money conversion 1:100) ---
    private NativeMenu _luiIllegalMoneyMethodMenu = null;
    private NativeMenu _luiSmugglerDetailMenu = null;
    private NativeMenu _luiSmugglerTradeMenu = null;

    private NativeItem _smugglerOwnerItem = null;
    private NativeItem _smugglerBalanceItem = null;
    private NativeItem _smugglerRatioItem = null;
    private NativeItem _smugglerAmountItem = null;
    private NativeItem _smugglerReceiveItem = null;

    private bool _smugglerRedeemArmed = false;
    private long _smugglerConvertAmount = -1;
    private long _smugglerExpectedCash = -1;
    private bool _smugglerContactAdded = false;
    private int _smugglerExpireGameTime = -1;

    private Ped _smugglerNpc = null;
    private Blip _smugglerBlip = null;

    private static readonly Vector3 SmugglerNpcPosition = new Vector3(1558.424000f, -2152.824000f, 77.502410f);
    private const float SmugglerNpcRadius = 2.5f;
    private const int SmugglerContactDialTimeoutMs = 2000;
    private const int SmugglerLifetimeMs = 180000;
    private const long MazeBankConversionRate = 18L;
    private const long SmugglerConversionRate = 82L;

    private int _illegalMoneyWantedTriggerGameTime = -1;
    private bool _illegalMoneyWantedConsumed = false;

    private const int HASH_MICHAEL = 225514697;
    private const int HASH_FRANKLIN = -1692214353;
    private const int HASH_TREVOR = -1686040670;

    // Reward costs
    private const long RewardInsuranceCost = 25000000L;
    private const int RewardInsuranceBaseCompensationPercent = 18;
    private const int RewardInsuranceReducedCompensationPercent = 5;

    private static string T(string key, string fallback = "", params string[] tokensAndValues)
    {
        string text = Language.Get(key, fallback);
        return (tokensAndValues != null && tokensAndValues.Length > 0)
          ? Language.ReplaceTokens(text, tokensAndValues)
          : text;
    }

    private void EnsureRewardDetailMenu()
    {
        try
        {
            if (_luiRewardDetailMenu != null)
                return;

            _luiRewardDetailMenu = new NativeMenu(
              T("RewardMenuBrandTitle", "Legendary Motorsport"),
              T("RewardDetailMenuTitle", "Đổi quà tri ân")
            );
            _luiPool.Add(_luiRewardDetailMenu);
            ConfigureKeyboardOnlyVehicleMenu(_luiRewardDetailMenu);
            _luiRewardDetailMenu.Visible = false;
        }
        catch
        {
        }
    }

    private VehicleEntry PickRandomRewardVehicle()
    {
        try
        {
            if (_vehicles == null || _vehicles.Count == 0)
                return null;

            VehicleEntry chosen = null;
            int attempts = 0;
            int maxAttempts = Math.Max(20, _vehicles.Count);

            while (attempts < maxAttempts)
            {
                var cand = _vehicles[_rng.Next(_vehicles.Count)];
                if (cand == null)
                {
                    attempts++;
                    continue;
                }

                bool modelAvailable = SafeCall(() =>
                {
                    var m = new Model((int)cand.Hash);
                    return m.IsValid && m.IsInCdImage;
                }, false);

                if (modelAvailable)
                {
                    chosen = cand;
                    break;
                }

                attempts++;
            }

            return chosen;
        }
        catch
        {
            return null;
        }
    }

    private void EnsureRewardRootMenu()
    {
        try
        {
            if (_luiRewardRootMenu != null)
                return;

            _luiRewardRootMenu = new NativeMenu(
              T("RewardRootMenuTitle", "Redeem points"),
              T("RewardRootMenuDescription", "Chọn mục đổi thưởng"));

            _luiPool.Add(_luiRewardRootMenu);
            ConfigureKeyboardOnlyVehicleMenu(_luiRewardRootMenu);
            _luiRewardRootMenu.Visible = false;
        }
        catch
        {
        }
    }

    private void BlockRewardMenuInput(int ms = 350)
    {
        try
        {
            _rewardMenuInputBlockUntil = Game.GameTime + Math.Max(1, ms);
        }
        catch
        {
            _rewardMenuInputBlockUntil = 0;
        }
    }

    private bool IsRewardMenuInputBlocked()
    {
        try
        {
            return Game.GameTime < _rewardMenuInputBlockUntil;
        }
        catch
        {
            return false;
        }
    }

    private bool ShouldRunPerFrameTick()
    {
        try
        {
            return _searchActive
                || _pendingType != PendingType.None
                || (_luiPool != null && SafeCall(() => _luiPool.AreAnyVisible, false))
                || (_illegalMoneyRedeemArmed && IsNearMazeBankAtm())
                || _smugglerRedeemArmed
                || _isProcessingAmmo;
        }
        catch
        {
            return false;
        }
    }

    private void CloseIllegalMoneyMethodMenu(bool setCooldown)
    {
        try
        {
            if (_luiIllegalMoneyMethodMenu != null)
            {
                _luiIllegalMoneyMethodMenu.Visible = false;
                _luiIllegalMoneyMethodMenu.Clear();
            }
        }
        catch { }

        if (setCooldown)
            EnsureHelpBoxCooldownSet();
    }

    private void EnsureRewardInsuranceMenu()
    {
        try
        {
            if (_luiRewardInsuranceMenu != null)
                return;

            _luiRewardInsuranceMenu = new NativeMenu(
              T("RewardInsuranceMenuTitle", "Mission Insurance"),
              T("RewardInsuranceMenuDescription", "Chi tiết thẻ bảo hiểm"));

            _luiPool.Add(_luiRewardInsuranceMenu);
            ConfigureKeyboardOnlyVehicleMenu(_luiRewardInsuranceMenu);
            _luiRewardInsuranceMenu.Visible = false;
        }
        catch
        {
        }
    }

    private void CloseRewardMenus(bool setCooldown)
    {
        try
        {
            ClearVehiclePreview();

            if (_luiRewardRootMenu != null) _luiRewardRootMenu.Visible = false;
            if (_luiRewardInsuranceMenu != null) _luiRewardInsuranceMenu.Visible = false;
            if (_luiRewardBribeMenu != null) _luiRewardBribeMenu.Visible = false;
            if (_luiRewardDetailMenu != null) _luiRewardDetailMenu.Visible = false;

            // Thêm 2 dòng hide menu mới tại đây
            if (_luiIllegalMoneyDetailMenu != null) _luiIllegalMoneyDetailMenu.Visible = false;
            if (_luiIllegalMoneyTradeMenu != null) _luiIllegalMoneyTradeMenu.Visible = false;

            if (_luiIllegalMoneyMethodMenu != null) _luiIllegalMoneyMethodMenu.Visible = false;
            if (_luiSmugglerDetailMenu != null) _luiSmugglerDetailMenu.Visible = false;
            if (_luiSmugglerTradeMenu != null) _luiSmugglerTradeMenu.Visible = false;
        }
        catch
        {
        }

        _rewardInsuranceChecked = false;
        _rewardBribeLevelFocused = false;
        _rewardBribeStars = 0;

        if (setCooldown)
            EnsureHelpBoxCooldownSet();

        Interval = 1000;
    }

    private void ShowRewardRootMenu()
    {
        try
        {
            EnsureRewardRootMenu();
            BuildRewardRootMenu();

            if (_luiIllegalMoneyMethodMenu != null) _luiIllegalMoneyMethodMenu.Visible = false;
            if (_luiIllegalMoneyDetailMenu != null) _luiIllegalMoneyDetailMenu.Visible = false;
            if (_luiIllegalMoneyTradeMenu != null) _luiIllegalMoneyTradeMenu.Visible = false;
            if (_luiSmugglerDetailMenu != null) _luiSmugglerDetailMenu.Visible = false;
            if (_luiSmugglerTradeMenu != null) _luiSmugglerTradeMenu.Visible = false;

            if (_luiRewardInsuranceMenu != null) _luiRewardInsuranceMenu.Visible = false;
            if (_luiRewardBribeMenu != null) _luiRewardBribeMenu.Visible = false;
            if (_luiRewardDetailMenu != null) _luiRewardDetailMenu.Visible = false;

            _luiRewardRootMenu.Visible = true;
            Interval = 0;
            PlayFrontendSound("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        }
        catch
        {
        }
    }

    private void BuildRewardRootMenu()
    {
        if (_luiRewardRootMenu == null)
            return;

        _luiRewardRootMenu.Clear();

        ReloadSpendingAccumulatorFromDisk();

        var pointsItem = new NativeItem(T("RewardCurrentPointsLabel", "Điểm thưởng hiện tại:"));
        pointsItem.AltTitle = FormatML(_spendingAccumulator.Total);
        pointsItem.Description = T("RewardCurrentPointsDescription", "Số điểm hiện tại đang có.");
        _luiRewardRootMenu.Add(pointsItem);

        long dirtyBalance = CityBlackoutHackerState.GetDirtyMoneyBalanceForCurrentCharacter();
        _illegalMoneyCurrentItem = new NativeItem(T("RewardCurrentIllegalMoneyLabel", "Tiền bất hợp pháp hiện có:"));
        _illegalMoneyCurrentItem.AltTitle = FormatCash(dirtyBalance);
        _illegalMoneyCurrentItem.Description = T(
            "RewardCurrentIllegalMoneyDescription",
            "Số tiền bất hợp pháp hiện tại mà nhân vật này đang sở hữu.");
        _luiRewardRootMenu.Add(_illegalMoneyCurrentItem);

        var vehicleItem = new NativeItem(T("RewardVehicleMenuLabel", "1. Siêu phẩm phương tiện"));
        vehicleItem.AltTitle = "~HUD_COLOUR_YELLOWLIGHT~>~s~";
        vehicleItem.Description = T("RewardVehicleMenuDescription", "Mở danh sách siêu phẩm.");
        vehicleItem.Activated += (s, e) =>
        {
            RedeemReward();
        };
        _luiRewardRootMenu.Add(vehicleItem);

        var insuranceItem = new NativeItem(T("RewardInsuranceMenuLabel", "2. Thẻ bảo hiểm nhiệm vụ"));
        insuranceItem.AltTitle = "~HUD_COLOUR_YELLOWLIGHT~>~s~";
        insuranceItem.Description = T("RewardInsuranceMenuDescription", "Đổi thẻ bảo hiểm.");
        insuranceItem.Activated += (s, e) =>
        {
            OpenRewardInsuranceMenu();
        };
        _luiRewardRootMenu.Add(insuranceItem);

        var illegalMoneyItem = new NativeItem(T("RewardIllegalMoneyMenuLabel", "3. Quy đổi tiền bất hợp pháp"));
        illegalMoneyItem.AltTitle = "~HUD_COLOUR_YELLOWLIGHT~>~s~";
        illegalMoneyItem.Description = T("RewardIllegalMoneyMenuDescription", "Đổi tiền bất hợp pháp sang tiền mặt qua Maze Bank hoặc Smuggler.");
        illegalMoneyItem.Activated += (s, e) =>
        {
            OpenIllegalMoneyMethodMenu();
        };
        _luiRewardRootMenu.Add(illegalMoneyItem);

        var decline = new NativeItem(T("RewardDeclineServiceLabel", "Từ chối sử dụng dịch vụ"));
        decline.Description = T("RewardDeclineServiceDescription", "Đóng menu.");
        decline.Activated += (s, e) =>
        {
            CloseRewardMenus(true);
        };
        _luiRewardRootMenu.Add(decline);
    }

    private void OpenRewardRedeemMenu()
    {
        try
        {
            if (IsHelpBoxCooldownActive())
            {
                ShowHelpCooldownMessage();
                return;
            }

            if (_pendingType != PendingType.None || SafeCall(() => _luiPool.AreAnyVisible, false))
            {
                GTA.UI.Screen.ShowSubtitle(
                  T("RewardAnotherMenuOpen", "~HUD_COLOUR_DEGEN_YELLOW~Hiện có menu khác đang mở. Hãy đóng nó trước."),
                   3000);
                return;
            }

            ShowRewardRootMenu();
        }
        catch { }
    }

    private static string FormatCash(long value)
    {
        if (value < 0) value = 0;
        return string.Format(CultureInfo.InvariantCulture, "${0:N0}", value);
    }

    private string GetCurrentCharacterDisplayName()
    {
        try
        {
            Ped p = Game.Player.Character;
            if (p == null || !p.Exists())
                return "N/A";

            int hash = p.Model.Hash;

            if (hash == HASH_MICHAEL) return "Michael De Santa";
            if (hash == HASH_FRANKLIN) return "Franklin Clinton";
            if (hash == HASH_TREVOR) return "Trevor Philips";

            return "N/A";
        }
        catch
        {
            return "N/A";
        }
    }

    private string GetSmugglerContactName()
    {
        return T("RewardSmugglerContactName", "Smuggler");
    }

    private void ArmSmugglerDeal()
    {
        _smugglerRedeemArmed = true;
        _smugglerExpireGameTime = Game.GameTime + SmugglerLifetimeMs;
    }

    private void ResetSmugglerDealState()
    {
        _smugglerRedeemArmed = false;
        _smugglerConvertAmount = -1;
        _smugglerExpectedCash = -1;
        _smugglerExpireGameTime = -1;
    }

    private void AbortSmugglerDeal(string subtitleKey = null)
    {
        try
        {
            if (_luiSmugglerTradeMenu != null)
                _luiSmugglerTradeMenu.Visible = false;
        }
        catch { }

        try
        {
            if (_luiSmugglerDetailMenu != null)
                _luiSmugglerDetailMenu.Visible = false;
        }
        catch { }

        ResetSmugglerDealState();
        DespawnSmugglerNpc();

        if (!string.IsNullOrWhiteSpace(subtitleKey))
        {
            GTA.UI.Screen.ShowSubtitle(
                T(subtitleKey, "~y~Smuggler đã rời đi vì quá thời gian chờ giao dịch."),
                3000);
        }
    }

    private void ProcessSmugglerLifetime()
    {
        try
        {
            if (!_smugglerRedeemArmed)
                return;

            if (_smugglerExpireGameTime <= 0)
                return;

            if (Game.GameTime < _smugglerExpireGameTime)
                return;

            AbortSmugglerDeal("RewardSmugglerExpired");
        }
        catch { }
    }

    private bool IsNearMazeBankAtm()
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead)
                return false;

            return player.Position.DistanceTo(MazeBankAtmPosition) <= MazeBankAtmRadius;
        }
        catch
        {
            return false;
        }
    }

    private void EnsureMazeBankAtmSpawned()
    {
        try
        {
            if (_mazeBankAtmSpawned)
                return;

            if (Game.GameTime < _mazeBankAtmSpawnReadyTime)
                return;

            int modelHash = Function.Call<int>(Hash.GET_HASH_KEY, "prop_atm_02");

            int existing = Function.Call<int>(
                Hash.GET_CLOSEST_OBJECT_OF_TYPE,
                MazeBankAtmPosition.X,
                MazeBankAtmPosition.Y,
                MazeBankAtmPosition.Z,
                1.5f,
                modelHash,
                false,
                false,
                false);

            if (existing != 0 && Function.Call<bool>(Hash.DOES_ENTITY_EXIST, existing))
            {
                _mazeBankAtmSpawned = true;
                return;
            }

            Model model = new Model(modelHash);
            model.Request(1000);

            if (!model.IsLoaded)
                return;

            int handle = Function.Call<int>(
                Hash.CREATE_OBJECT_NO_OFFSET,
                modelHash,
                MazeBankAtmPosition.X,
                MazeBankAtmPosition.Y,
                MazeBankAtmPosition.Z,
                true,
                true,
                false);

            if (handle != 0)
            {
                Function.Call(Hash.SET_ENTITY_HEADING, handle, MazeBankAtmHeading);
                Function.Call(Hash.FREEZE_ENTITY_POSITION, handle, true);
                Function.Call(Hash.SET_ENTITY_INVINCIBLE, handle, true);
                Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, handle, true, true);
                _mazeBankAtmSpawned = true;
            }

            model.MarkAsNoLongerNeeded();
        }
        catch
        {
        }
    }

    private void ProcessIllegalMoneyWantedRisk()
    {
        try
        {
            if (_illegalMoneyWantedConsumed || _illegalMoneyWantedTriggerGameTime <= 0)
                return;

            if (Game.GameTime < _illegalMoneyWantedTriggerGameTime)
                return;

            _illegalMoneyWantedConsumed = true;
            _illegalMoneyWantedTriggerGameTime = -1;

            if (_rng.NextDouble() > 0.30)
                return;

            int currentWanted = GetCurrentWantedLevel();
            int nextWanted = Math.Min(5, currentWanted + 3);

            if (nextWanted != currentWanted)
                SetWantedLevel(nextWanted);

            GTA.UI.Screen.ShowSubtitle(
                T("RewardIllegalMoneyWantedHit", "~r~~h~Giao dịch bị phát hiện! Cảnh sát đã tăng mức truy nã."),
                3500);
        }
        catch
        {
        }
    }

    private void ShowMazeBankAtmHelpText()
    {
        try
        {
            if (!_illegalMoneyRedeemArmed)
                return;

            if (_pendingType != PendingType.None)
                return;

            if (_luiPool != null && SafeCall(() => _luiPool.AreAnyVisible, false))
                return;

            if (!IsNearMazeBankAtm())
                return;

            GTA.UI.Screen.ShowHelpTextThisFrame(
                T("RewardIllegalMoneyAtmHelp",
                  "~b~~h~ATM MAZE BANK~h~~s~\nNhấn ~INPUT_FRONTEND_ACCEPT~ để kích hoạt ATM của Maze Bank."));
        }
        catch
        {
        }
    }

    private void EnsureIllegalMoneyDetailMenu()
    {
        try
        {
            if (_luiIllegalMoneyDetailMenu != null)
                return;

            _luiIllegalMoneyDetailMenu = new NativeMenu(
                T("RewardMenuMoneyBrandTitle", "Maze Bank"),
                T("RewardIllegalMoneyDetailTitle", "Quy đổi tiền bất hợp pháp"));

            _luiPool.Add(_luiIllegalMoneyDetailMenu);
            ConfigureKeyboardOnlyVehicleMenu(_luiIllegalMoneyDetailMenu);
            _luiIllegalMoneyDetailMenu.Visible = false;
        }
        catch
        {
        }
    }

    private void EnsureIllegalMoneyMethodMenu()
    {
        try
        {
            if (_luiIllegalMoneyMethodMenu != null)
                return;

            _luiIllegalMoneyMethodMenu = new NativeMenu(
                T("RewardMenu_BrandTitle", "Maze Bank"),
                T("RewardIllegalMoneyMethodTitle", "Chọn phương thức quy đổi"));

            _luiPool.Add(_luiIllegalMoneyMethodMenu);
            ConfigureKeyboardOnlyVehicleMenu(_luiIllegalMoneyMethodMenu);
            _luiIllegalMoneyMethodMenu.Visible = false;
        }
        catch
        {
        }
    }

    private void BuildIllegalMoneyMethodMenu()
    {
        if (_luiIllegalMoneyMethodMenu == null)
            return;

        _luiIllegalMoneyMethodMenu.Clear();

        var mazeBank = new NativeItem(T("RewardIllegalMoneyMethodMazeBank", "1. Maze Bank"));
        mazeBank.AltTitle = "~HUD_COLOUR_YELLOWLIGHT~>~s~";
        mazeBank.Description = T("RewardIllegalMoneyMethodMazeBankDesc", "Khả năng quy đổi là 1:18.");
        mazeBank.Activated += (s, e) => OpenIllegalMoneyDetailMenu();
        _luiIllegalMoneyMethodMenu.Add(mazeBank);

        var smuggler = new RewardRightBadgeItem(
            T("RewardIllegalMoneyMethodSmuggler", "2. Smuggler"),
            T("RewardIllegalMoneyMethodSmugglerDesc", "Liên hệ Smuggler để xử lý giao dịch."),
            _smugglerLockBadge);

        smuggler.AltTitle = "";
        smuggler.Activated += (s, e) =>
        {
            Notification.Show(
                NotificationIcon.BankMaze,
                "Maze Bank",
                T("RewardMazeBankNoticeTitle", "Thông báo"),
                T("RewardMazeBankNoticeBody", "Bạn cần liên hệ cho Smuggler chứ không giao dịch tại Maze Bank."));
        };
        _luiIllegalMoneyMethodMenu.Add(smuggler);

        var back = new NativeItem(T("RewardIllegalMoneyMethodBack", "Quay lại"));
        back.Activated += (s, e) =>
        {
            CloseIllegalMoneyMethodMenu(false);
            ShowRewardRootMenu();
        };
        _luiIllegalMoneyMethodMenu.Add(back);
    }

    private void OpenIllegalMoneyMethodMenu()
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

            EnsureIllegalMoneyMethodMenu();
            BuildIllegalMoneyMethodMenu();

            if (_luiRewardRootMenu != null) _luiRewardRootMenu.Visible = false;
            if (_luiRewardInsuranceMenu != null) _luiRewardInsuranceMenu.Visible = false;
            if (_luiRewardBribeMenu != null) _luiRewardBribeMenu.Visible = false;
            if (_luiRewardDetailMenu != null) _luiRewardDetailMenu.Visible = false;
            if (_luiIllegalMoneyDetailMenu != null) _luiIllegalMoneyDetailMenu.Visible = false;
            if (_luiIllegalMoneyTradeMenu != null) _luiIllegalMoneyTradeMenu.Visible = false;
            if (_luiSmugglerDetailMenu != null) _luiSmugglerDetailMenu.Visible = false;
            if (_luiSmugglerTradeMenu != null) _luiSmugglerTradeMenu.Visible = false;

            _luiIllegalMoneyMethodMenu.Visible = true;
            Interval = 0;
            PlayFrontendSound("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        }
        catch
        {
        }
    }

    private void EnsureIllegalMoneyTradeMenu()
    {
        try
        {
            if (_luiIllegalMoneyTradeMenu != null)
                return;

            _luiIllegalMoneyTradeMenu = new NativeMenu(
                T("RewardMenuBrand_Title", "Maze Bank"),
                T("RewardIllegalMoneyTradeTitle", "Giao dịch quy đổi"));

            _luiPool.Add(_luiIllegalMoneyTradeMenu);
            ConfigureKeyboardOnlyVehicleMenu(_luiIllegalMoneyTradeMenu);
            _luiIllegalMoneyTradeMenu.Visible = false;
        }
        catch
        {
        }
    }

    private void BuildIllegalMoneyDetailMenu()
    {
        if (_luiIllegalMoneyDetailMenu == null)
            return;

        _luiIllegalMoneyDetailMenu.Clear();

        long dirty = CityBlackoutHackerState.GetDirtyMoneyBalanceForCurrentCharacter();

        _illegalMoneyOwnerItem = new NativeItem(
            string.Format(T("RewardIllegalMoneyOwnerLabel", "Tên người sở hữu: {0}"), GetCurrentCharacterDisplayName()));
        _illegalMoneyBalanceItem = new NativeItem(
            string.Format(T("RewardIllegalMoneyBalanceLabel", "Số tiền bất hợp pháp: {0}"), FormatCash(dirty)));
        _illegalMoneyRatioItem = new NativeItem(
            T("RewardIllegalMoneyRatioLabel", "Tỷ lệ quy đổi: 1:18"));

        _illegalMoneyOwnerItem.Description = T("RewardIllegalMoneyOwnerDesc", "Nhân vật hiện tại.");
        _illegalMoneyBalanceItem.Description = T("RewardIllegalMoneyBalanceDesc", "Số tiền bất hợp pháp mà bạn đang sở hữu.");
        _illegalMoneyRatioItem.Description = T("RewardIllegalMoneyRatioDesc", "$18 tiền bất hợp pháp sẽ đổi được $1 tiền mặt.");

        _luiIllegalMoneyDetailMenu.Add(_illegalMoneyOwnerItem);
        _luiIllegalMoneyDetailMenu.Add(_illegalMoneyBalanceItem);
        _luiIllegalMoneyDetailMenu.Add(_illegalMoneyRatioItem);

        var confirm = new NativeItem(T("RewardIllegalMoneyDetailConfirm", "Xác nhận quy đổi phạm pháp"));
        confirm.Activated += (s, e) =>
        {
            _illegalMoneyRedeemArmed = true;
            CloseRewardMenus(false);

            if (_luiIllegalMoneyDetailMenu != null)
                _luiIllegalMoneyDetailMenu.Visible = false;

            Notification.Show(
                    NotificationIcon.BankMaze,
                    "Maze Bank",
                    T("ATMMazeBank_Title", "Yêu cầu"),
                    T("ATMMazeBank_Transaction", "Maze Bank đã duyệt yêu cầu của quý khách rồi! Anh hãy đến ATM của Maze Bank để kích hoạt giao dịch nhé.")
                );
        };
        _luiIllegalMoneyDetailMenu.Add(confirm);

        var cancel = new NativeItem(T("RewardIllegalMoneyDetailCancel", "Hủy bỏ giao dịch"));
        cancel.Activated += (s, e) =>
        {
            if (_luiIllegalMoneyDetailMenu != null)
                _luiIllegalMoneyDetailMenu.Visible = false;

            OpenIllegalMoneyMethodMenu();
        };
        _luiIllegalMoneyDetailMenu.Add(cancel);
    }

    private void OpenIllegalMoneyDetailMenu()
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

            EnsureIllegalMoneyDetailMenu();
            BuildIllegalMoneyDetailMenu();

            if (_luiIllegalMoneyMethodMenu != null) _luiIllegalMoneyMethodMenu.Visible = false;
            if (_luiRewardRootMenu != null) _luiRewardRootMenu.Visible = false;
            if (_luiRewardInsuranceMenu != null) _luiRewardInsuranceMenu.Visible = false;
            if (_luiRewardBribeMenu != null) _luiRewardBribeMenu.Visible = false;
            if (_luiRewardDetailMenu != null) _luiRewardDetailMenu.Visible = false;
            if (_luiIllegalMoneyTradeMenu != null) _luiIllegalMoneyTradeMenu.Visible = false;
            if (_luiSmugglerDetailMenu != null) _luiSmugglerDetailMenu.Visible = false;
            if (_luiSmugglerTradeMenu != null) _luiSmugglerTradeMenu.Visible = false;

            _luiIllegalMoneyDetailMenu.Visible = true;
            Interval = 0;
            PlayFrontendSound("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        }
        catch
        {
        }
    }

    // ----------------------- Smuggler branch -----------------------
    private void EnsureSmugglerDetailMenu()
    {
        try
        {
            if (_luiSmugglerDetailMenu != null)
                return;

            _luiSmugglerDetailMenu = new NativeMenu(
                T("RewardMenuSmugglerBrandTitle", "Smuggler"),
                T("RewardSmugglerDetailTitle", "Quy đổi tiền bất hợp pháp"));

            _luiPool.Add(_luiSmugglerDetailMenu);
            ConfigureKeyboardOnlyVehicleMenu(_luiSmugglerDetailMenu);
            _luiSmugglerDetailMenu.Visible = false;
        }
        catch
        {
        }
    }

    private void EnsureSmugglerTradeMenu()
    {
        try
        {
            if (_luiSmugglerTradeMenu != null)
                return;

            _luiSmugglerTradeMenu = new NativeMenu(
                T("RewardMenu_SmugglerBrandTitle", "Smuggler"),
                T("RewardSmugglerTradeTitle", "Giao dịch quy đổi"));

            _luiPool.Add(_luiSmugglerTradeMenu);
            ConfigureKeyboardOnlyVehicleMenu(_luiSmugglerTradeMenu);
            _luiSmugglerTradeMenu.Visible = false;
        }
        catch
        {
        }
    }

    private void BuildSmugglerDetailMenu()
    {
        if (_luiSmugglerDetailMenu == null)
            return;

        _luiSmugglerDetailMenu.Clear();

        long dirty = CityBlackoutHackerState.GetDirtyMoneyBalanceForCurrentCharacter();

        _smugglerOwnerItem = new NativeItem(
            string.Format(T("RewardSmugglerOwnerLabel", "Tên người giao dịch: {0}"), GetCurrentCharacterDisplayName()));
        _smugglerBalanceItem = new NativeItem(
            string.Format(T("RewardSmugglerBalanceLabel", "Số tiền bất hợp pháp: {0}"), FormatCash(dirty)));
        _smugglerRatioItem = new NativeItem(
            T("RewardSmugglerRatioLabel", "Tỷ lệ quy đổi: 1:82"));

        _smugglerOwnerItem.Description = T("RewardSmugglerOwnerDesc", "Nhân vật hiện tại.");
        _smugglerBalanceItem.Description = T("RewardSmugglerBalanceDesc", "Số tiền bất hợp pháp mà bạn đang sở hữu.");
        _smugglerRatioItem.Description = T("RewardSmugglerRatioDesc", "$82 tiền bất hợp pháp sẽ đổi được $1 tiền mặt.");

        _luiSmugglerDetailMenu.Add(_smugglerOwnerItem);
        _luiSmugglerDetailMenu.Add(_smugglerBalanceItem);
        _luiSmugglerDetailMenu.Add(_smugglerRatioItem);

        var confirm = new NativeItem(T("RewardSmugglerDetailConfirm", "Xác nhận với Smuggler"));
        confirm.Activated += (s, e) =>
        {
            ArmSmugglerDeal();
            SpawnSmugglerNpc();

            CloseRewardMenus(false);

            if (_luiSmugglerDetailMenu != null)
                _luiSmugglerDetailMenu.Visible = false;

            Notification.Show(
                NotificationIcon.Milsite,
                "Notorious Smuggler",
                T("RewardSmuggler_Name", "Smuggler"),
                T("RewardSmugglerGoToNpc", "Tao chấp nhận cuộc giao dịch của mày! Đến đây mà lấy tiền đi!!")
            );
        };
        _luiSmugglerDetailMenu.Add(confirm);

        var cancel = new NativeItem(T("RewardSmugglerDetailCancel", "Hủy bỏ giao dịch"));
        cancel.Activated += (s, e) =>
        {
            if (_luiSmugglerDetailMenu != null)
                _luiSmugglerDetailMenu.Visible = false;

            OpenIllegalMoneyMethodMenu();
        };
        _luiSmugglerDetailMenu.Add(cancel);
    }

    private void OpenSmugglerDetailMenu()
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

            EnsureSmugglerDetailMenu();
            BuildSmugglerDetailMenu();

            if (_luiIllegalMoneyMethodMenu != null) _luiIllegalMoneyMethodMenu.Visible = false;
            if (_luiRewardRootMenu != null) _luiRewardRootMenu.Visible = false;
            if (_luiRewardInsuranceMenu != null) _luiRewardInsuranceMenu.Visible = false;
            if (_luiRewardBribeMenu != null) _luiRewardBribeMenu.Visible = false;
            if (_luiRewardDetailMenu != null) _luiRewardDetailMenu.Visible = false;
            if (_luiIllegalMoneyMethodMenu != null) _luiIllegalMoneyMethodMenu.Visible = false;
            if (_luiIllegalMoneyDetailMenu != null) _luiIllegalMoneyDetailMenu.Visible = false;
            if (_luiIllegalMoneyTradeMenu != null) _luiIllegalMoneyTradeMenu.Visible = false;
            if (_luiSmugglerTradeMenu != null) _luiSmugglerTradeMenu.Visible = false;

            _luiSmugglerDetailMenu.Visible = true;
            Interval = 0;
            PlayFrontendSound("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        }
        catch
        {
        }
    }

    private void BuildSmugglerTradeMenu()
    {
        if (_luiSmugglerTradeMenu == null)
            return;

        _luiSmugglerTradeMenu.Clear();

        long dirty = CityBlackoutHackerState.GetDirtyMoneyBalanceForCurrentCharacter();

        _smugglerOwnerItem = new NativeItem(
            string.Format(T("RewardSmugglerOwnerLabel", "Tên người giao dịch: {0}"), GetCurrentCharacterDisplayName()));
        _smugglerBalanceItem = new NativeItem(
            string.Format(T("RewardSmugglerBalanceLabel", "Số tiền bất hợp pháp: {0}"), FormatCash(dirty)));
        _smugglerRatioItem = new NativeItem(
            T("RewardSmugglerRatioLabel", "Tỷ lệ quy đổi: 1:82"));

        _smugglerAmountItem = new NativeItem(
            string.Format(T("RewardSmugglerAmountLabel", "Số tiền cần đổi: {0}"),
                _smugglerConvertAmount > 0 ? FormatCash(_smugglerConvertAmount) : "N/A"));

        _smugglerReceiveItem = new NativeItem(
            string.Format(T("RewardSmugglerReceiveLabel", "Số tiền nhận được: {0}"),
                _smugglerExpectedCash > 0 ? FormatCash(_smugglerExpectedCash) : "N/A"));

        _smugglerOwnerItem.Description = T("RewardSmugglerOwnerDesc", "Nhân vật hiện tại.");
        _smugglerBalanceItem.Description = T("RewardSmugglerBalanceDesc", "Số tiền bất hợp pháp mà bạn đang sở hữu.");
        _smugglerRatioItem.Description = T("RewardSmugglerRatioDesc", "$82 tiền bất hợp pháp sẽ đổi được $1 tiền mặt.");
        _smugglerAmountItem.Description = T("RewardSmugglerAmountDesc", "Hãy nhập số tiền cần đổi vào đây.");
        _smugglerReceiveItem.Description = T("RewardSmugglerReceiveDesc", "Số tiền đã được quyết định.");

        _smugglerAmountItem.Activated += (s, e) => PromptSmugglerAmount();

        _luiSmugglerTradeMenu.Add(_smugglerOwnerItem);
        _luiSmugglerTradeMenu.Add(_smugglerBalanceItem);
        _luiSmugglerTradeMenu.Add(_smugglerRatioItem);
        _luiSmugglerTradeMenu.Add(_smugglerAmountItem);
        _luiSmugglerTradeMenu.Add(_smugglerReceiveItem);

        var confirm = new NativeItem(T("RewardSmugglerTradeConfirm", "Xác nhận quy đổi"));
        confirm.Activated += (s, e) => ConfirmSmugglerConversion();
        _luiSmugglerTradeMenu.Add(confirm);

        var cancel = new NativeItem(T("RewardSmugglerTradeCancel", "Hủy giao dịch"));
        cancel.Activated += (s, e) =>
        {
            OpenSmugglerDetailMenu();
        };
        _luiSmugglerTradeMenu.Add(cancel);
    }

    private void UpdateSmugglerTradeMenuVisuals()
    {
        try
        {
            if (_luiSmugglerTradeMenu == null)
                return;

            BuildSmugglerTradeMenu();
        }
        catch
        {
        }
    }

    private bool IsNearSmugglerNpc()
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead)
                return false;

            if (_smugglerNpc == null || !_smugglerNpc.Exists())
                return false;

            return player.Position.DistanceTo(_smugglerNpc.Position) <= SmugglerNpcRadius;
        }
        catch
        {
            return false;
        }
    }

    private void SpawnSmugglerNpc()
    {
        try
        {
            if (_smugglerNpc != null && _smugglerNpc.Exists())
            {
                if (_smugglerBlip == null || !_smugglerBlip.Exists())
                {
                    _smugglerBlip = _smugglerNpc.AddBlip();
                    if (_smugglerBlip != null && _smugglerBlip.Exists())
                    {
                        _smugglerBlip.IsShortRange = false;
                        _smugglerBlip.Sprite = BlipSprite.Standard;
                        _smugglerBlip.Color = BlipColor.Red;
                        Function.Call(Hash.SET_BLIP_COLOUR, _smugglerBlip.Handle, (int)BlipColor.Red);
                        SetBlipName(_smugglerBlip, T("RewardSmugglerBlipName", "Smuggler"));
                    }
                }
                return;
            }

            DespawnSmugglerNpc();

            Ped npc = TryCreatePed("s_m_m_dockwork_01", SmugglerNpcPosition);
            if (npc == null || !npc.Exists())
                return;

            _smugglerNpc = npc;
            try { npc.Task.ClearAllImmediately(); } catch { }
            try { npc.Task.StandStill(-1); } catch { }

            _smugglerBlip = npc.AddBlip();
            if (_smugglerBlip != null && _smugglerBlip.Exists())
            {
                _smugglerBlip.IsShortRange = false;
                _smugglerBlip.Sprite = BlipSprite.Standard;
                _smugglerBlip.Color = BlipColor.Red;
                Function.Call(Hash.SET_BLIP_COLOUR, _smugglerBlip.Handle, (int)BlipColor.Red);
                SetBlipName(_smugglerBlip, T("RewardSmugglerBlipName", "Smuggler"));
            }
        }
        catch { }
    }

    private Ped TryCreatePed(string modelName, Vector3 position)
    {
        try
        {
            Model model = new Model(Game.GenerateHash(modelName));
            if (!model.IsValid || !model.IsInCdImage)
                return null;

            if (!model.IsLoaded)
                model.Request(2000);

            int waited = 0;
            while (!model.IsLoaded && waited < 3000)
            {
                Script.Wait(50);
                waited += 50;
            }

            if (!model.IsLoaded)
            {
                model.MarkAsNoLongerNeeded();
                return null;
            }

            Ped npc = World.CreatePed(model, position);
            if (npc == null || !npc.Exists())
            {
                model.MarkAsNoLongerNeeded();
                return null;
            }

            try { npc.IsPersistent = true; } catch { }
            try { Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, npc.Handle, true, true); } catch { }
            try { Function.Call(Hash.SET_ENTITY_INVINCIBLE, npc.Handle, true); } catch { }
            try { Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, npc.Handle, true); } catch { }
            try { Function.Call(Hash.SET_PED_FLEE_ATTRIBUTES, npc.Handle, 0, false); } catch { }
            try { Function.Call(Hash.SET_PED_CAN_RAGDOLL, npc.Handle, false); } catch { }
            try { Function.Call(Hash.SET_PED_CAN_BE_TARGETTED, npc.Handle, false); } catch { }
            try { Function.Call(Hash.SET_PED_DIES_WHEN_INJURED, npc.Handle, false); } catch { }
            try { Function.Call(Hash.SET_PED_KEEP_TASK, npc.Handle, true); } catch { }
            try { npc.Task.ClearAllImmediately(); } catch { }

            model.MarkAsNoLongerNeeded();
            return npc;
        }
        catch
        {
            return null;
        }
    }

    private void SetBlipName(Blip blip, string displayName)
    {
        try
        {
            if (blip == null || !blip.Exists())
                return;

            Function.Call(Hash.BEGIN_TEXT_COMMAND_SET_BLIP_NAME, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, displayName);
            Function.Call(Hash.END_TEXT_COMMAND_SET_BLIP_NAME, blip.Handle);
        }
        catch
        {
        }
    }

    private void DespawnSmugglerNpc()
    {
        try
        {
            if (_smugglerBlip != null)
            {
                try { if (_smugglerBlip.Exists()) _smugglerBlip.Delete(); } catch { }
                _smugglerBlip = null;
            }
        }
        catch { }

        try
        {
            if (_smugglerNpc != null)
            {
                try { if (_smugglerNpc.Exists()) _smugglerNpc.Delete(); } catch { }
                _smugglerNpc = null;
            }
        }
        catch { }

        _smugglerExpireGameTime = -1;
    }

    private void UpdateSmugglerPrompt()
    {
        try
        {
            ProcessSmugglerLifetime();

            if (!_smugglerRedeemArmed)
                return;

            if (_pendingType != PendingType.None)
                return;

            if (_luiPool != null && SafeCall(() => _luiPool.AreAnyVisible, false))
                return;

            if (!IsNearSmugglerNpc())
                return;

            GTA.UI.Screen.ShowHelpTextThisFrame(
                T("RewardSmugglerHelp",
                  "~b~~h~SMUGGLER~h~~s~\nNhấn ~INPUT_FRONTEND_ACCEPT~ để kích hoạt giao dịch đổi tiền."));
        }
        catch { }
    }

    private void OpenSmugglerTradeMenu()
    {
        try
        {
            ProcessSmugglerLifetime();

            if (!_smugglerRedeemArmed)
                return;

            if (!IsNearSmugglerNpc())
                return;

            if (IsHelpBoxCooldownActive())
            {
                ShowHelpCooldownMessage();
                return;
            }

            EnsureSmugglerTradeMenu();
            BuildSmugglerTradeMenu();

            if (_luiIllegalMoneyMethodMenu != null) _luiIllegalMoneyMethodMenu.Visible = false;
            if (_luiRewardRootMenu != null) _luiRewardRootMenu.Visible = false;
            if (_luiRewardInsuranceMenu != null) _luiRewardInsuranceMenu.Visible = false;
            if (_luiRewardBribeMenu != null) _luiRewardBribeMenu.Visible = false;
            if (_luiRewardDetailMenu != null) _luiRewardDetailMenu.Visible = false;
            if (_luiIllegalMoneyMethodMenu != null) _luiIllegalMoneyMethodMenu.Visible = false;
            if (_luiIllegalMoneyDetailMenu != null) _luiIllegalMoneyDetailMenu.Visible = false;
            if (_luiIllegalMoneyTradeMenu != null) _luiIllegalMoneyTradeMenu.Visible = false;
            if (_luiSmugglerDetailMenu != null) _luiSmugglerDetailMenu.Visible = false;

            _luiSmugglerTradeMenu.Visible = true;
            Interval = 0;
            PlayFrontendSound("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        }
        catch
        {
        }
    }

    private void CloseSmugglerTradeMenu(bool setCooldown)
    {
        try
        {
            if (_luiSmugglerTradeMenu != null)
                _luiSmugglerTradeMenu.Visible = false;
        }
        catch
        {
        }

        if (setCooldown)
            EnsureHelpBoxCooldownSet();

        Interval = 1000;
    }

    private void PromptSmugglerAmount()
    {
        try
        {
            BlockRewardMenuInput(400);

            string input = Game.GetUserInput(string.Empty);
            if (string.IsNullOrWhiteSpace(input))
                return;

            var sb = new System.Text.StringBuilder();
            foreach (char ch in input)
            {
                if (char.IsDigit(ch))
                    sb.Append(ch);
            }

            if (sb.Length == 0)
            {
                GTA.UI.Screen.ShowSubtitle(T("RewardSmugglerInvalidAmount", "Số tiền ~HUD_COLOUR_DEGEN_YELLOW~không hợp lệ.~s~"), 2500);
                return;
            }

            if (!long.TryParse(sb.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long amount))
            {
                GTA.UI.Screen.ShowSubtitle(T("RewardSmugglerInvalidAmount", "Số tiền ~HUD_COLOUR_DEGEN_YELLOW~không hợp lệ.~s~"), 2500);
                return;
            }

            if (amount < SmugglerConversionRate)
            {
                GTA.UI.Screen.ShowSubtitle(
                    string.Format(
                        T("RewardSmugglerMinimumAmount", "Số tiền quy đổi ~HUD_COLOUR_DEGEN_YELLOW~tối thiểu~s~ là ~HUD_COLOUR_DEGEN_GREEN~{0}.~s~"),
                        FormatCash(SmugglerConversionRate)),
                    2500);
                return;
            }

            long dirty = CityBlackoutHackerState.GetDirtyMoneyBalanceForCurrentCharacter();
            if (amount > dirty)
            {
                GTA.UI.Screen.ShowSubtitle(
                    string.Format(T("RewardSmugglerExceedBalance", "Bạn chỉ có thể đổi ~HUD_COLOUR_DEGEN_RED~tối đa~s~ {0}."),
                    FormatCash(dirty)),
                    3000);
                return;
            }

            _smugglerConvertAmount = amount;
            _smugglerExpectedCash = Math.Max(1, amount / SmugglerConversionRate);

            UpdateSmugglerTradeMenuVisuals();
            PlayFrontendSound("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        }
        catch
        {
        }
        finally
        {
            BlockRewardMenuInput(350);
        }
    }

    private void ConfirmSmugglerConversion()
    {
        try
        {
            long dirty = CityBlackoutHackerState.GetDirtyMoneyBalanceForCurrentCharacter();

            if (_smugglerConvertAmount < SmugglerConversionRate)
            {
                GTA.UI.Screen.ShowSubtitle(
                    T("RewardSmugglerNeedAmount", "~HUD_COLOUR_DEGEN_YELLOW~Hãy nhập số tiền cần đổi trước.~s~"),
                    2500);
                return;
            }

            if (_smugglerConvertAmount > dirty)
            {
                GTA.UI.Screen.ShowSubtitle(
                    string.Format(T("RewardSmugglerExceedBalance", "Bạn chỉ có thể đổi ~HUD_COLOUR_DEGEN_RED~tối đa~s~ {0}."),
                    FormatCash(dirty)),
                    3000);
                return;
            }

            long receive = Math.Max(1, _smugglerConvertAmount / SmugglerConversionRate);
            if (!CityBlackoutHackerState.TrySpendDirtyMoneyForCurrentCharacter(_smugglerConvertAmount))
            {
                GTA.UI.Screen.ShowSubtitle(
                    T("RewardSmugglerSpendFail", "~r~Không thể trừ tiền bất hợp pháp."),
                    2500);
                return;
            }

            if (receive > int.MaxValue)
                receive = int.MaxValue;

            Game.Player.Money += (int)receive;

            PlayFrontendSound("PURCHASE", "HUD_FRONTEND_DEFAULT_SOUNDSET");

            _smugglerRedeemArmed = false;
            _smugglerConvertAmount = -1;
            _smugglerExpectedCash = -1;

            DespawnSmugglerNpc();
            CloseSmugglerTradeMenu(false);
        }
        catch
        {
            CloseSmugglerTradeMenu(false);
        }
    }

    private void EnsureSmugglerContactRegistered()
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            if (_smugglerContactAdded)
                return;

            string contactName = GetSmugglerContactName();

            foreach (var c in phone.Contacts)
            {
                if (c != null && string.Equals(c.Name, contactName, StringComparison.OrdinalIgnoreCase))
                {
                    _smugglerContactAdded = true;
                    return;
                }
            }

            var contact = new iFruitContact(contactName)
            {
                Active = true,
                DialTimeout = SmugglerContactDialTimeoutMs,
                Bold = false,
                Icon = new ContactIcon("CHAR_LJT")
            };

            contact.Answered += OnSmugglerContactAnswered;
            phone.Contacts.Add(contact);
            _smugglerContactAdded = true;
        }
        catch { }
    }

    private void OnSmugglerContactAnswered(iFruitContact sender)
    {
        try
        {
            ArmSmugglerDeal();
            SpawnSmugglerNpc();

            Notification.Show(
                NotificationIcon.Milsite,
                "Notorious Smuggler",
                T("RewardSmuggler_Name", "Smuggler"),
                T("RewardSmugglerGoToNpc", "Tao chấp nhận cuộc giao dịch của mày! Đến đây mà lấy tiền đi!!")
            );

            try
            {
                CustomiFruit.GetCurrentInstance()?.Close(0);
            }
            catch { }
        }
        catch { }
    }

    private void BuildIllegalMoneyTradeMenu()
    {
        if (_luiIllegalMoneyTradeMenu == null)
            return;

        _luiIllegalMoneyTradeMenu.Clear();

        long dirty = CityBlackoutHackerState.GetDirtyMoneyBalanceForCurrentCharacter();

        _illegalMoneyOwnerItem = new NativeItem(
            string.Format(T("RewardIllegalMoneyOwnerLabel", "Tên người sở hữu: {0}"), GetCurrentCharacterDisplayName()));
        _illegalMoneyBalanceItem = new NativeItem(
            string.Format(T("RewardIllegalMoneyBalanceLabel", "Số tiền bất hợp pháp: {0}"), FormatCash(dirty)));
        _illegalMoneyRatioItem = new NativeItem(
            T("RewardIllegalMoneyRatioLabel", "Tỷ lệ quy đổi: 1:18"));

        _illegalMoneyAmountItem = new NativeItem(
            string.Format(T("RewardIllegalMoneyAmountLabel", "Số tiền cần đổi: {0}"),
            _illegalMoneyConvertAmount > 0 ? FormatCash(_illegalMoneyConvertAmount) : "N/A"));

        _illegalMoneyReceiveItem = new NativeItem(
            string.Format(T("RewardIllegalMoneyReceiveLabel", "Số tiền nhận được: {0}"),
            _illegalMoneyExpectedCash > 0 ? FormatCash(_illegalMoneyExpectedCash) : "N/A"));

        _illegalMoneyOwnerItem.Description = T("RewardIllegalMoneyOwnerDesc", "Nhân vật hiện tại.");
        _illegalMoneyBalanceItem.Description = T("RewardIllegalMoneyBalanceDesc", "Số tiền bất hợp pháp mà bạn đang sở hữu.");
        _illegalMoneyRatioItem.Description = T("RewardIllegalMoneyRatioDesc", "$18 tiền bất hợp pháp sẽ đổi được $1 tiền mặt.");
        _illegalMoneyAmountItem.Description = T("RewardIllegalMoneyAmountDesc", "Hãy nhập số tiền cần đổi vào đây.");
        _illegalMoneyReceiveItem.Description = T("RewardIllegalMoneyReceiveDesc", "Số tiền đã được quyết định.");

        _illegalMoneyAmountItem.Activated += (s, e) =>
        {
            PromptIllegalMoneyAmount();
        };

        _luiIllegalMoneyTradeMenu.Add(_illegalMoneyOwnerItem);
        _luiIllegalMoneyTradeMenu.Add(_illegalMoneyBalanceItem);
        _luiIllegalMoneyTradeMenu.Add(_illegalMoneyRatioItem);
        _luiIllegalMoneyTradeMenu.Add(_illegalMoneyAmountItem);
        _luiIllegalMoneyTradeMenu.Add(_illegalMoneyReceiveItem);

        var confirm = new NativeItem(T("RewardIllegalMoneyTradeConfirm", "Xác nhận quy đổi"));
        confirm.Activated += (s, e) =>
        {
            ConfirmIllegalMoneyConversion();
        };
        _luiIllegalMoneyTradeMenu.Add(confirm);

        var cancel = new NativeItem(T("RewardIllegalMoneyTradeCancel", "Hủy giao dịch"));
        cancel.Activated += (s, e) =>
        {
            OpenIllegalMoneyDetailMenu();
        };
        _luiIllegalMoneyTradeMenu.Add(cancel);
    }

    private void UpdateIllegalMoneyTradeMenuVisuals()
    {
        try
        {
            if (_luiIllegalMoneyTradeMenu == null)
                return;

            BuildIllegalMoneyTradeMenu();
        }
        catch
        {
        }
    }

    private void OpenIllegalMoneyTradeMenu()
    {
        try
        {
            if (!_illegalMoneyRedeemArmed)
                return;

            if (!IsNearMazeBankAtm())
                return;

            if (IsHelpBoxCooldownActive())
            {
                ShowHelpCooldownMessage();
                return;
            }

            EnsureIllegalMoneyTradeMenu();
            BuildIllegalMoneyTradeMenu();

            if (_luiIllegalMoneyMethodMenu != null) _luiIllegalMoneyMethodMenu.Visible = false;
            if (_luiRewardRootMenu != null) _luiRewardRootMenu.Visible = false;
            if (_luiRewardInsuranceMenu != null) _luiRewardInsuranceMenu.Visible = false;
            if (_luiRewardBribeMenu != null) _luiRewardBribeMenu.Visible = false;
            if (_luiRewardDetailMenu != null) _luiRewardDetailMenu.Visible = false;
            if (_luiIllegalMoneyDetailMenu != null) _luiIllegalMoneyDetailMenu.Visible = false;
            if (_luiSmugglerDetailMenu != null) _luiSmugglerDetailMenu.Visible = false;
            if (_luiSmugglerTradeMenu != null) _luiSmugglerTradeMenu.Visible = false;

            _luiIllegalMoneyTradeMenu.Visible = true;
            Interval = 0;
            PlayFrontendSound("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        }
        catch
        {
        }
    }

    private void CloseIllegalMoneyTradeMenu(bool setCooldown)
    {
        try
        {
            if (_luiIllegalMoneyTradeMenu != null)
                _luiIllegalMoneyTradeMenu.Visible = false;
        }
        catch
        {
        }

        if (setCooldown)
            EnsureHelpBoxCooldownSet();

        Interval = 1000;
    }

    private void PromptIllegalMoneyAmount()
    {
        try
        {
            BlockRewardMenuInput(400);

            string input = Game.GetUserInput(string.Empty);
            if (string.IsNullOrWhiteSpace(input))
                return;

            var sb = new System.Text.StringBuilder();
            foreach (char ch in input)
            {
                if (char.IsDigit(ch))
                    sb.Append(ch);
            }

            if (sb.Length == 0)
            {
                GTA.UI.Screen.ShowSubtitle(
                    T("RewardIllegalMoneyInvalidAmount", "~y~Số tiền không hợp lệ."),
                    2500);
                return;
            }

            if (!long.TryParse(sb.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long amount))
            {
                GTA.UI.Screen.ShowSubtitle(
                    T("RewardIllegalMoneyInvalidAmount", "~y~Số tiền không hợp lệ."),
                    2500);
                return;
            }

            if (amount < MazeBankConversionRate)
            {
                GTA.UI.Screen.ShowSubtitle(
                    string.Format(
                        T("RewardIllegalMoneyMinimumAmount", "~y~Số tiền quy đổi tối thiểu là {0}."),
                        FormatCash(MazeBankConversionRate)),
                    2500);
                return;
            }

            long dirty = CityBlackoutHackerState.GetDirtyMoneyBalanceForCurrentCharacter();
            if (amount > dirty)
            {
                GTA.UI.Screen.ShowSubtitle(
                    string.Format(T("RewardIllegalMoneyExceedBalance", "~HUD_COLOUR_DEGEN_RED~Bạn chỉ có thể đổi tối đa {0}."),
                    FormatCash(dirty)),
                    3000);
                return;
            }

            _illegalMoneyConvertAmount = amount;
            _illegalMoneyExpectedCash = (long)Math.Round(amount / (decimal)MazeBankConversionRate, 0, MidpointRounding.AwayFromZero);

            if (_illegalMoneyExpectedCash < 1)
                _illegalMoneyExpectedCash = 1;

            UpdateIllegalMoneyTradeMenuVisuals();
            PlayFrontendSound("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        }
        catch
        {
        }
        finally
        {
            BlockRewardMenuInput(350);
        }
    }

    private void ConfirmIllegalMoneyConversion()
    {
        try
        {
            long dirty = CityBlackoutHackerState.GetDirtyMoneyBalanceForCurrentCharacter();

            if (_illegalMoneyConvertAmount < MazeBankConversionRate)
            {
                GTA.UI.Screen.ShowSubtitle(
                    T("RewardIllegalMoneyNeedAmount", "~y~Hãy nhập số tiền cần đổi trước."),
                    2500);
                return;
            }

            if (_illegalMoneyConvertAmount > dirty)
            {
                GTA.UI.Screen.ShowSubtitle(
                    string.Format(T("RewardIllegalMoneyExceedBalance", "~HUD_COLOUR_DEGEN_RED~Bạn chỉ có thể đổi tối đa {0}."),
                    FormatCash(dirty)),
                    3000);
                return;
            }

            long receive = (long)Math.Round(_illegalMoneyConvertAmount / (decimal)MazeBankConversionRate, 0, MidpointRounding.AwayFromZero);
            if (receive < 1)
                receive = 1;

            if (!CityBlackoutHackerState.TrySpendDirtyMoneyForCurrentCharacter(_illegalMoneyConvertAmount))
            {
                GTA.UI.Screen.ShowSubtitle(
                    T("RewardIllegalMoneySpendFail", "~r~Không thể trừ tiền bất hợp pháp."),
                    2500);
                return;
            }

            if (receive > int.MaxValue)
                receive = int.MaxValue;

            Game.Player.Money += (int)receive;

            _illegalMoneyWantedTriggerGameTime = Game.GameTime + 10000;
            _illegalMoneyWantedConsumed = false;

            PlayFrontendSound("PURCHASE", "HUD_FRONTEND_DEFAULT_SOUNDSET");

            GTA.UI.Screen.ShowSubtitle(
                string.Format(
                    T("RewardIllegalMoneySuccess", "~h~Đã quy đổi thành công {0} -> {1}."),
                    FormatCash(_illegalMoneyConvertAmount),
                    FormatCash(receive)),
                3500);

            _illegalMoneyRedeemArmed = false;
            _illegalMoneyConvertAmount = -1;
            _illegalMoneyExpectedCash = -1;

            CloseIllegalMoneyTradeMenu(false);
        }
        catch
        {
            CloseIllegalMoneyTradeMenu(false);
        }
    }

    // Khi help-box đang mở, Enter sẽ gọi vào đây.
    private void RedeemReward()
    {
        try
        {
            ReloadSpendingAccumulatorFromDisk();

            if (_spendingAccumulator.Total < REWARD_THRESHOLD)
            {
                GTA.UI.Screen.ShowSubtitle(
                  T("RewardNotEnoughPoints", "~HUD_COLOUR_REDLIGHT~~h~Không đủ điểm thưởng để quy đổi phương tiện đặc biệt."),
                  5000
                );
                return;
            }

            var chosen = PickRandomRewardVehicle();
            if (chosen == null)
            {
                GTA.UI.Screen.ShowSubtitle(
                  T("RewardNoValidVehicle", "~HUD_COLOUR_DEGEN_RED~Không tìm thấy phương tiện hợp lệ để tặng. Thử lại sau."),
                  3000
                );
                return;
            }

            CloseRewardMenus(false);
            OpenRewardVehicleDetailMenu(chosen);
        }
        catch
        {
            CloseRewardMenus(false);
        }
    }

    private void OpenRewardVehicleDetailMenu(VehicleEntry chosen)
    {
        try
        {
            if (chosen == null)
                return;

            CloseRewardMenus(false);

            EnsureVehicleStatsPanel();
            EnsureRewardDetailMenu();
            CloseLemonVehicleMenus(false);

            _rewardDetailVehicle = chosen;
            _rewardDetailPrice = REWARD_THRESHOLD;
            _rewardDetailPlate = GenerateRandomPlate();
            _rewardPlateEdited = false;

            ReadVehicleModelStats(
              chosen.Hash,
              out float topSpeed,
              out float acceleration,
              out float braking,
              out float traction
            );

            UpdateVehicleStatsPanel(topSpeed, acceleration, braking, traction);

            BuildRewardVehicleDetailMenu(chosen, _rewardDetailPrice, _rewardDetailPlate);

            ConfigureKeyboardOnlyVehicleMenu(_luiRewardDetailMenu);
            _luiRewardDetailMenu.Visible = true;

            SpawnVehiclePreview(chosen);
            Interval = 0;
        }
        catch
        {
            CloseRewardVehicleDetailMenu(false);
        }
    }

    private void CloseRewardVehicleDetailMenu(bool setCooldown)
    {
        try
        {
            if (_luiRewardDetailMenu != null)
                _luiRewardDetailMenu.Visible = false;
        }
        catch
        {
        }

        ClearVehiclePreview();

        _rewardDetailVehicle = null;
        _rewardDetailPrice = 0;
        _rewardDetailPlate = "";
        _rewardPlateEdited = false;

        if (setCooldown)
            EnsureHelpBoxCooldownSet();
    }

    private string GetRewardPlateDescription()
    {
        return _rewardPlateEdited
            ? RewardPlateHintAfterEdit
            : RewardPlateHintBeforeEdit;
    }

    private void BuildRewardVehicleDetailMenu(VehicleEntry chosen, long price, string plate)
    {
        if (_luiRewardDetailMenu == null || chosen == null)
            return;

        _luiRewardDetailMenu.Clear();

        AddReadOnlyMenuLine(
          _luiRewardDetailMenu,
          T("RewardVehicleNameLabel", "Tên phương tiện: {name}", "{name}", chosen.Name),
          _luiVehicleStatsPanel,
          () => SpawnVehiclePreview(chosen)
        );

        AddReadOnlyMenuLine(
          _luiRewardDetailMenu,
          T("RewardVehicleClassLabel", "Loại: {class}", "{class}", chosen.Class),
          null,
          () => SpawnVehiclePreview(chosen)
        );

        AddReadOnlyMenuLine(
          _luiRewardDetailMenu,
          T("RewardVehiclePriceLabel", "Giá quy đổi: {price}", "{price}", FormatML(price)),
          null,
          () => SpawnVehiclePreview(chosen)
        );

        var plateItem = new NativeItem(
          T("RewardVehiclePlateLabel", "Biển số xe: {plate}", "{plate}", plate)
        );
        plateItem.Description = _rewardPlateEdited
          ? T("RewardPlateHintAfterEdit", "Đây là biển số mới sẽ được dùng, có thể đổi lại.")
          : T("RewardPlateHintBeforeEdit", "Nhấn Enter để điều chỉnh biển số theo ý muốn.");

        plateItem.Selected += (s, e) =>
        {
            SpawnVehiclePreview(chosen);
        };

        plateItem.Activated += (s, e) =>
        {
            try
            {
                // Chặn dội Enter từ bàn phím ảo / LemonUI
                BlockRewardMenuInput(500);

                string currentPlate = string.IsNullOrWhiteSpace(_rewardDetailPlate)
                    ? GenerateRandomPlate()
                    : _rewardDetailPlate;

                string typed = PromptPlateText(currentPlate);
                string normalized = SanitizePlateText(typed);

                if (string.IsNullOrWhiteSpace(normalized))
                {
                    GTA.UI.Screen.ShowSubtitle(
                        T("RewardInvalidPlate", "~y~Biển số không hợp lệ."),
                        2500
                    );
                    BlockRewardMenuInput(350);
                    return;
                }

                _rewardPlateEdited = true;
                _rewardDetailPlate = normalized;

                // Chỉ cập nhật trực tiếp item hiện tại, KHÔNG rebuild cả menu
                plateItem.Title = T("RewardVehiclePlateLabel", "Biển số xe: {plate}", "{plate}", _rewardDetailPlate);
                plateItem.Description = GetRewardPlateDescription();

                _luiRewardDetailMenu.Visible = true;
                SpawnVehiclePreview(chosen);
                PlayFrontendSound("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");

                BlockRewardMenuInput(350);
            }
            catch
            {
                BlockRewardMenuInput(350);
            }
        };

        _luiRewardDetailMenu.Add(plateItem);

        var confirm = new NativeItem(T("RewardConfirmButton", "Xác nhận nhận phần quà"));
        confirm.Activated += (s, e) =>
        {
            ConfirmRewardVehicleFromDetailMenu(chosen, price, _rewardDetailPlate);
        };
        _luiRewardDetailMenu.Add(confirm);

        var back = new NativeItem(T("RewardBackButton", "Quay lại"));
        back.Activated += (s, e) =>
        {
            CloseRewardVehicleDetailMenu(false);
            ShowRewardRootMenu();
            Interval = 0;
        };
        _luiRewardDetailMenu.Add(back);
    }

    private void ConfirmRewardVehicleFromDetailMenu(VehicleEntry chosen, long totalCost, string plateText)
    {
        try
        {
            ReloadSpendingAccumulatorFromDisk();

            if (_spendingAccumulator.Total < REWARD_THRESHOLD)
            {
                GTA.UI.Screen.ShowSubtitle(
                  T("RewardNotEnoughPoints", "~HUD_COLOUR_REDLIGHT~~h~Không đủ điểm thưởng để quy đổi phương tiện đặc biệt."),
                  5000
                );
                CloseRewardVehicleDetailMenu(false);
                return;
            }

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead)
            {
                CloseRewardVehicleDetailMenu(false);
                return;
            }

            if (chosen == null)
            {
                CloseRewardVehicleDetailMenu(false);
                return;
            }

            _spendingAccumulator.SubtractFromSpendingAccumulator(REWARD_THRESHOLD);

            ClearVehiclePreview();

            int purchasePriceForRegister = 0;
            string plateSnapshot = plateText;

            VehicleDelivery.RequestDelivery(chosen.Hash, player.Position, (veh) =>
            {
                try
                {
                    if (veh != null && veh.Exists())
                    {
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(plateSnapshot))
                                Function.Call(Hash.SET_VEHICLE_NUMBER_PLATE_TEXT, veh.Handle, plateSnapshot);
                        }
                        catch
                        {
                        }

                        try
                        {
                            var meta = new PersistentManager.VehicleMeta
                            {
                                PurchasePrice = purchasePriceForRegister
                            };
                            PersistentManager.RegisterVehicle(veh, meta);
                        }
                        catch
                        {
                            try { PersistentManager.RegisterVehicle(veh); } catch { }
                        }
                    }
                }
                catch
                {
                }
            }, 150, 30000);

            try
            {
                chosen.TimesPurchased = Math.Max(0, chosen.TimesPurchased + 1);
            }
            catch
            {
            }

            PlayFrontendSound("PURCHASE", "HUD_FRONTEND_DEFAULT_SOUNDSET");
            GTA.UI.Screen.ShowSubtitle(
              T(
                "RewardSuccessSubtitle",
                "~h~Chúc mừng bạn nhận được một chiếc ~HUD_COLOUR_DEGEN_CYAN~{name}~s~ miễn phí!!",
                "{name}", chosen.Name
              ),
              5000
            );

            _lastInteractionGameTime = Game.GameTime;
            CloseRewardVehicleDetailMenu(true);
        }
        catch
        {
            CloseRewardVehicleDetailMenu(false);
        }
    }

    private void OpenRewardInsuranceMenu()
    {
        try
        {
            EnsureRewardInsuranceMenu();
            BuildRewardInsuranceMenu();

            if (_luiRewardRootMenu != null) _luiRewardRootMenu.Visible = false;
            if (_luiRewardBribeMenu != null) _luiRewardBribeMenu.Visible = false;
            if (_luiRewardDetailMenu != null) _luiRewardDetailMenu.Visible = false;

            _luiRewardInsuranceMenu.Visible = true;
            Interval = 0;
            PlayFrontendSound("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        }
        catch
        {
        }
    }

    private void BuildRewardInsuranceMenu()
    {
        if (_luiRewardInsuranceMenu == null)
            return;

        _luiRewardInsuranceMenu.Clear();

        _rewardInsuranceChecked = false;

        _luiRewardInsuranceTicketItem = new NativeCheckboxItem(T("RewardInsuranceTicketLabel", "Thẻ bảo hiểm"), false);
        _luiRewardInsuranceTicketItem.Description = T("RewardInsuranceTicketDescription", "Tick vào để chọn thẻ bảo hiểm.");
        _luiRewardInsuranceTicketItem.CheckboxChanged += (s, e) =>
        {
            _rewardInsuranceChecked = _luiRewardInsuranceTicketItem.Checked;
            UpdateRewardInsuranceMenuVisuals();
            PlayFrontendSound("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        };
        _luiRewardInsuranceMenu.Add(_luiRewardInsuranceTicketItem);

        _luiRewardInsuranceValueItem = new NativeItem(T("RewardInsuranceValueLabel", "Giá trị đền bù"));
        _luiRewardInsuranceMenu.Add(_luiRewardInsuranceValueItem);

        _luiRewardInsuranceConfirmItem = new NativeItem(T("RewardInsuranceConfirmLabel", "Xác nhận đổi thẻ bảo hiểm"));
        _luiRewardInsuranceConfirmItem.Activated += (s, e) =>
        {
            ConfirmMissionInsuranceFromMenu();
        };
        _luiRewardInsuranceMenu.Add(_luiRewardInsuranceConfirmItem);

        var back = new NativeItem(T("RewardBackToRewardMenuLabel", "Quay lại danh sách đổi thưởng"));
        back.Activated += (s, e) =>
        {
            CloseRewardInsuranceMenu(false);
            ShowRewardRootMenu();
        };
        _luiRewardInsuranceMenu.Add(back);

        UpdateRewardInsuranceMenuVisuals();
    }

    private void UpdateRewardInsuranceMenuVisuals()
    {
        try
        {
            if (_luiRewardInsuranceTicketItem != null)
            {
                _luiRewardInsuranceTicketItem.Checked = _rewardInsuranceChecked;
            }

            if (_luiRewardInsuranceValueItem != null)
            {
                _luiRewardInsuranceValueItem.AltTitle = _rewardInsuranceChecked
                  ? T("RewardInsuranceReducedPercent", "5%")
                  : T("RewardInsuranceBasePercent", "18%");
                _luiRewardInsuranceValueItem.Description = _rewardInsuranceChecked
                  ? T("RewardInsuranceReducedDescription", "Mức đền bù sẽ giảm còn 5%.")
                  : T("RewardInsuranceBaseDescription", "Mức đền bù hiện tại là 18%.");
            }

            if (_luiRewardInsuranceConfirmItem != null)
            {
                _luiRewardInsuranceConfirmItem.Description = T(
                    "RewardInsuranceConfirmDescription",
                    "Chi phí: ${cost} điểm thưởng.",
                    "{cost}", FormatML(RewardInsuranceCost)
                    );
            }
        }
        catch
        {
        }
    }

    private void CloseRewardInsuranceMenu(bool setCooldown)
    {
        try
        {
            if (_luiRewardInsuranceMenu != null)
                _luiRewardInsuranceMenu.Visible = false;
        }
        catch
        {
        }

        _rewardInsuranceChecked = false;

        if (setCooldown)
            EnsureHelpBoxCooldownSet();
    }

    private void ConfirmMissionInsuranceFromMenu()
    {
        try
        {
            if (!_rewardInsuranceChecked)
            {
                GTA.UI.Screen.ShowSubtitle(T("RewardInsuranceNotSelected", "Dịch vụ này chưa được sử dụng!!!"), 2500);
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            ReloadSpendingAccumulatorFromDisk();

            if (_spendingAccumulator.Total < RewardInsuranceCost)
            {
                GTA.UI.Screen.ShowSubtitle(
                  T("RewardInsuranceNotEnoughPoints", "~HUD_COLOUR_DEGEN_RED~Không đủ điểm để đổi thẻ bảo hiểm!!!"),
                   3000
                );
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            _spendingAccumulator.SubtractFromSpendingAccumulator(RewardInsuranceCost);
            MissionInsuranceTicketStore.AddTickets(1);

            PlayFrontendSound("PURCHASE", "HUD_FRONTEND_DEFAULT_SOUNDSET");

            ShowFeedMessage(
              L("FeedSender", "Online Shop"),
              L("FeedPurchaseSubject", "Mua hàng"),
              T("RewardInsuranceSuccessFeed", "Đã đổi thành công thẻ bảo hiểm nhiệm vụ.")
             );

            CloseRewardInsuranceMenu(false);
            ShowRewardRootMenu();
        }
        catch
        {
            CloseRewardInsuranceMenu(false);
        }
    }
}

internal static class MissionInsuranceTicketStore
{
    private static readonly object SyncRoot = new object();

    private static readonly string FilePath = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
      "GTA V Mods",
      "PersistentManager",
      "MoneyTruckEvent_MissionInsuranceTicket.dat");

    private static int _count = -1;

    private static void EnsureLoaded()
    {
        if (_count >= 0)
            return;

        try
        {
            if (!File.Exists(FilePath))
            {
                _count = 0;
                return;
            }

            string raw = File.ReadAllText(FilePath).Trim();
            if (!int.TryParse(raw, out _count))
                _count = 0;

            if (_count < 0)
                _count = 0;
        }
        catch
        {
            _count = 0;
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));

            if (_count < 0)
                _count = 0;

            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, _count.ToString());

            if (File.Exists(FilePath))
                File.Delete(FilePath);

            File.Move(tmp, FilePath);
        }
        catch
        {
        }
    }

    public static int GetCount()
    {
        lock (SyncRoot)
        {
            EnsureLoaded();
            return _count;
        }
    }

    public static void AddTickets(int amount)
    {
        if (amount <= 0)
            return;

        lock (SyncRoot)
        {
            EnsureLoaded();
            _count += amount;
            if (_count < 0)
                _count = 0;
            Save();
        }
    }

    public static bool TryConsumeTicket()
    {
        lock (SyncRoot)
        {
            EnsureLoaded();

            if (_count <= 0)
                return false;

            _count--;
            if (_count < 0)
                _count = 0;

            Save();
            return true;
        }
    }
}