using GTA;
using GTA.Native;
using GTA.UI;
using LemonUI;
using LemonUI.Menus;
using System;
using System.IO;

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
    private NativeMenu _luiRewardBribeMenu = null;

    // Insurance UI
    private NativeCheckboxItem _luiRewardInsuranceTicketItem = null;
    private NativeItem _luiRewardInsuranceValueItem = null;
    private NativeItem _luiRewardInsuranceConfirmItem = null;

    // Bribe UI
    private NativeItem _luiRewardBribeServiceItem = null;
    private NativeItem _luiRewardBribeLevelItem = null;
    private NativeItem _luiRewardBribeConfirmItem = null;

    // Reward state
    private bool _rewardInsuranceChecked = false;
    private bool _rewardBribeLevelFocused = false;
    private int _rewardBribeStars = 0;

    private int _rewardMenuInputBlockUntil = 0;

    // Reward costs
    private const long RewardInsuranceCost = 25000000L;
    private const int RewardInsuranceBaseCompensationPercent = 18;
    private const int RewardInsuranceReducedCompensationPercent = 5;

    private const long RewardBribeCostPerStar = 10000000L;
    private const int RewardBribeMaxStars = 5;

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
              T("RewardRootMenuDescription", "Chọn mục muốn đổi thưởng"));

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

    private void EnsureRewardBribeMenu()
    {
        try
        {
            if (_luiRewardBribeMenu != null)
                return;

            _luiRewardBribeMenu = new NativeMenu(
              T("RewardBribeMenuTitle", "Secret bribe"),
              T("RewardBribeMenuDescription", "Chi tiết hối lộ truy nã"));

            _luiPool.Add(_luiRewardBribeMenu);
            ConfigureKeyboardOnlyVehicleMenu(_luiRewardBribeMenu);
            _luiRewardBribeMenu.Visible = false;
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

        var bribeItem = new NativeItem(T("RewardBribeMenuLabel", "3. Hối lộ sao truy nã"));
        bribeItem.AltTitle = "~HUD_COLOUR_YELLOWLIGHT~>~s~";
        bribeItem.Description = T("RewardBribeMenuDescription", "Hối lộ cảnh sát.");
        bribeItem.Activated += (s, e) =>
        {
            OpenRewardBribeMenu();
        };
        _luiRewardRootMenu.Add(bribeItem);

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

    private void OpenRewardBribeMenu()
    {
        try
        {
            EnsureRewardBribeMenu();
            BuildRewardBribeMenu();

            if (_luiRewardRootMenu != null) _luiRewardRootMenu.Visible = false;
            if (_luiRewardInsuranceMenu != null) _luiRewardInsuranceMenu.Visible = false;
            if (_luiRewardDetailMenu != null) _luiRewardDetailMenu.Visible = false;

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

        _rewardBribeStars = 0;
        _rewardBribeLevelFocused = false;

        _luiRewardBribeServiceItem = new NativeItem(T("RewardBribeServiceLabel", "Dịch vụ: Hối lộ giảm tội"));
        _luiRewardBribeServiceItem.Description = T("RewardBribeServiceDescription", "Dùng điểm thưởng để giảm sao truy nã.");
        _luiRewardBribeServiceItem.Selected += (s, e) =>
        {
            _rewardBribeLevelFocused = false;
        };
        _luiRewardBribeMenu.Add(_luiRewardBribeServiceItem);

        _luiRewardBribeLevelItem = new NativeItem(T("RewardBribeLevelLabel", "Chọn mức độ giảm tội"));
        _luiRewardBribeLevelItem.Selected += (s, e) =>
        {
            _rewardBribeLevelFocused = true;
            UpdateRewardBribeMenuVisuals();
        };
        _luiRewardBribeMenu.Add(_luiRewardBribeLevelItem);

        _luiRewardBribeConfirmItem = new NativeItem(T("RewardBribeConfirmLabel", "Xác nhận giảm sao truy nã"));
        _luiRewardBribeConfirmItem.Selected += (s, e) =>
        {
            _rewardBribeLevelFocused = false;
        };
        _luiRewardBribeConfirmItem.Activated += (s, e) =>
        {
            ConfirmWantedBribeFromMenu();
        };
        _luiRewardBribeMenu.Add(_luiRewardBribeConfirmItem);

        var back = new NativeItem(T("RewardBribeBackLabel", "Từ chối sử dụng dịch vụ"));
        back.Activated += (s, e) =>
        {
            CloseRewardBribeMenu(false);
            ShowRewardRootMenu();
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
                _luiRewardBribeLevelItem.AltTitle = $"\u2190 {_rewardBribeStars} \u2192";
            }

            if (_luiRewardBribeConfirmItem != null)
            {
                long cost = (long)_rewardBribeStars * RewardBribeCostPerStar;
                _luiRewardBribeConfirmItem.Description = T(
                    "RewardBribeConfirmDescription",
                    "Chi phí chi trả dự kiến: ${cost} điểm thưởng.",
                    "{cost}", FormatML(cost)
                    );
            }
        }
        catch
        {
        }
    }

    private void CloseRewardBribeMenu(bool setCooldown)
    {
        try
        {
            if (_luiRewardBribeMenu != null)
                _luiRewardBribeMenu.Visible = false;
        }
        catch
        {
        }

        _rewardBribeLevelFocused = false;
        _rewardBribeStars = 0;

        if (setCooldown)
            EnsureHelpBoxCooldownSet();
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
            if (stars > 5) stars = 5;

            int playerId = Function.Call<int>(Hash.PLAYER_ID);
            Function.Call(Hash.SET_PLAYER_WANTED_LEVEL, playerId, stars, false);
            Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, playerId, true);
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
            int reduceStars = Math.Min(currentWanted, _rewardBribeStars);

            if (reduceStars <= 0)
            {
                GTA.UI.Screen.ShowSubtitle(
                  T("RewardBribeNoWantedLevel", "~HUD_COLOUR_DEGEN_YELLOW~Hiện không có sao truy nã để giảm."),
                   2500
                );
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            long cost = (long)reduceStars * RewardBribeCostPerStar;

            ReloadSpendingAccumulatorFromDisk();

            if (_spendingAccumulator.Total < cost)
            {
                GTA.UI.Screen.ShowSubtitle(
                  T("RewardBribeNotEnoughPoints", "~HUD_COLOUR_DEGEN_RED~Không đủ điểm để hối lộ cho mức sao này!!!"),
                   3000
                );
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            _spendingAccumulator.SubtractFromSpendingAccumulator(cost);

            int newWanted = Math.Max(0, currentWanted - reduceStars);
            SetWantedLevel(newWanted);

            PlayFrontendSound("PURCHASE", "HUD_FRONTEND_DEFAULT_SOUNDSET");

            CloseRewardBribeMenu(false);
            ShowRewardRootMenu();
        }
        catch
        {
            CloseRewardBribeMenu(false);
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