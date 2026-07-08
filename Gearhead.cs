using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using iFruitAddon2;
using LemonUI;
using LemonUI.Menus;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

public class Gearhead : Script
{
    private static string L(string key, string fallback) => Language.Get(key, fallback);
    private static string LT(string key, string fallback, params string[] tokensAndValues)
        => Language.ReplaceTokens(Language.Get(key, fallback), tokensAndValues);

    private static string GearheadContactName => L("Gearhead_ContactName", "Gearhead");
    private static string GearheadMenuTitle => L("VehicleUpgrade_Title", "Upgrade Mod Kit");
    private static string GearheadMenuSubtitle => L("VehicleUpgrade_Subtitle", "CHI TIẾT NÂNG CẤP");
    private static string GearheadConfirmTitlePersistent => L("Gearhead_ConfirmPersistentTitle", "Xác nhận nâng cấp linh kiện ngẫu nhiên");
    private static string GearheadConfirmTitleStreetLoot => L("Gearhead_ConfirmStreetLootTitle", "Xác nhận lắp linh kiện ngẫu nhiên");
    private static string GearheadCancelTitle => L("VehicleUpgrade_Cancel", "Hủy bỏ nâng cấp");

    private static string GearheadNeedVehicleFirstText => L("VehicleUpgrade_NeedVehicleFirst", "~HUD_COLOUR_REDLIGHT~Hãy vào xe trước đã~s~");
    private static string GearheadFailedTitle => L("VehicleUpgrade_FailedTitle", "Nâng cấp thất bại");
    private static string GearheadUnknownVehicleText => L("Gearhead_UnknownVehicleText", "Chiếc xe này không đủ điều kiện để tui xử lý.");
    private static string GearheadAlreadyMaxedText => L("Gearhead_AlreadyMaxedText", "Chiếc xe này đã thuộc dạng cao cấp rồi.");
    private static string GearheadAlreadyProcessedText => L("Gearhead_AlreadyProcessedText", "Chiếc xe này đã được xử lý rồi.");
    private static string GearheadUpgradeFailedText => L("VehicleUpgrade_UpgradeFailedText", "Không thể nâng cấp phương tiện này.");
    private static string GearheadInsufficientMoneyText => L("VehicleUpgrade_InsufficientMoney", "Không đủ tiền để mua linh kiện. Cần ~y~{0}~s~ lận.");
    private static string GearheadCompletedTitle => L("Gearhead_CompletedTitle", "Hoàn thành");
    private static string GearheadCompletedBody => L("Gearhead_CompletedBody", "Tui lắp linh kiện xong rồi đó! Kiểm tra thử đi?");
    private static string GearheadLootCompletedTitle => L("Gearhead_LootCompletedTitle", "Hoàn tất linh kiện");
    private static string GearheadLootCompletedBody => L("Gearhead_LootCompletedBody", "Xong rồi. Con phương tiện này tui đã lắp ráp linh kiện hoàn tất rồi đấy! Thử phương tiện và đánh giá xem nào?");
    private static string GearheadMenuFailureBody => L("Gearhead_MenuFailureBody", "Chiếc xe này không đủ điều kiện để tui xử lý.");
    private static string GearheadStreetLootStatusText => L("Gearhead_StreetLootStatusText", "Không chính chủ");
    private static string GearheadRandomStatusText => L("Gearhead_RandomStatusText", "Linh kiện ngẫu nhiên");
    private static string GearheadAlreadyUpgradedStatusText => L("Gearhead_AlreadyUpgradedStatusText", "Linh kiện cao cấp");
    private static string GearheadUnupgradedStatusText => L("Gearhead_UnupgradedStatusText", "Linh kiện chưa nâng cấp");
    private static string GearheadPriceNaText => L("VehicleUpgrade_PriceNa", "N/A");
    private static string GearheadCustomerLabel => L("VehicleUpgrade_CustomerNameLabel", "Tên khách hàng");
    private static string GearheadVehicleLabel => L("VehicleUpgrade_VehicleNameLabel", "Tên phương tiện");
    private static string GearheadPurchasePriceLabel => L("VehicleUpgrade_PurchasePriceLabel", "Giá phương tiện");
    private static string GearheadStatusLabel => L("VehicleUpgrade_StatusLabel", "Tình trạng");
    private static string GearheadComponentPriceLabel => L("VehicleUpgrade_ComponentPriceLabel", "Giá linh kiện");

    private const int GameReadyDelayMs = 2500;
    private const int FadeOutMs = 1000;
    private const int PostFadeDelayMs = 1000;
    private const int UpgradeFeePercentBranch40 = 21;
    private const int UpgradeFeePercentBranch60 = 27;

    private const int PersistentUpgradeTimeMinMinutes = 80;
    private const int PersistentUpgradeTimeMaxMinutes = 120;
    private const int StreetLootUpgradeTimeMinMinutes = 60;
    private const int StreetLootUpgradeTimeMaxMinutes = 90;

    private readonly Random _rng = new Random();
    private readonly ObjectPool _uiPool = new ObjectPool();

    private const string GearheadMessageSoundName = "Text_Arrive_Tone";
    private const string GearheadMessageSoundSet = "Phone_SoundSet_Default";

    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "PersistentManager");

    private static readonly string PersistentVehiclesFile = Path.Combine(DataFolder, "persistent_vehicles.txt");

    private NativeMenu _gearheadMenu;
    private bool _gearheadMenuReady = false;

    private iFruitContact _gearheadContact = null;
    private bool _gearheadContactAdded = false;

    private enum FlowState
    {
        Idle,
        WaitingFadeApply,
        WaitingFadeIn
    }

    private FlowState _flowState = FlowState.Idle;
    private int _flowStateSince = 0;

    private Vehicle _targetVehicle = null;
    private OwnedVehicleRecord _targetRecord = null;
    private int _targetOwnerHash = 0;
    private int _targetPurchasePrice = 0;
    private int _targetUpgradeFee = 0;
    private int _targetFeePercent = 0;
    private string _targetCustomerName = string.Empty;
    private string _targetVehicleName = string.Empty;
    private string _targetPlate = string.Empty;
    private int _pendingTimeAdvanceMinutes = 0;

    private int _gameReadySince = -1;
    private bool _modReady = false;
    private int _lastMenuUpdateTime = 0;
    private NativeItem _confirmItem;
    private NativeItem _cancelItem;

    private string _lastRejectMessage = null;

    private sealed class OwnedVehicleRecord
    {
        public string RawLine;
        public uint ModelHash;
        public Vector3 Position;
        public float Heading;
        public string Plate;
        public int OwnerModelHash;
        public bool AutoSpawnEnabled;
        public Dictionary<int, int> Mods = new Dictionary<int, int>();
        public bool TurboOn;
        public int PurchasePrice;
        public int? SavedLivery;
        public int? PrimaryColor;
        public int? SecondaryColor;
        public int? PearlColor;
        public int? WheelColor;
        public int? WindowTint;
        public int[] TyreSmokeColor = new int[3] { 0, 0, 0 };
        public List<int> ExtrasExist = new List<int>();
        public int[] NeonColor = null;
        public int? DashboardColor;
        public bool IsCollateralLocked;
        public int? HaoUpgradeBranch;   // 1 = Hao branch, 0 = Gearhead branch
        public bool HaoUpgradeApplied;  // already processed by either Hao or Gearhead
    }

    private sealed class UpgradeTarget
    {
        public Vehicle Vehicle;
        public OwnedVehicleRecord Record;
        public bool IsPersistentOwnedVehicle;
        public int PurchasePrice;
        public int UpgradeFee;
        public int FeePercent;
        public int? HaoUpgradeBranch;
        public string CustomerName;
        public string VehicleName;
        public string Plate;

        public bool IsStreetLootVehicle;
        public bool StreetLootAccepted;
    }

    public Gearhead()
    {
        Tick += OnTick;
        Interval = 1000;

        try
        {
            _gameReadySince = Game.GameTime;
        }
        catch
        {
            _gameReadySince = 0;
        }
    }

    private void OnTick(object sender, EventArgs e)
    {
        try
        {
            if (Game.IsLoading)
            {
                Interval = 1000;
                return;
            }

            if (!_modReady)
            {
                if (_gameReadySince < 0)
                    _gameReadySince = Game.GameTime;

                if (Game.GameTime - _gameReadySince < GameReadyDelayMs)
                    return;

                _modReady = true;
                EnsureGearheadContactRegistered();
            }

            HandleDeferredUpgradeFlow();

            if (_gearheadMenu != null && _gearheadMenu.Visible)
            {
                _uiPool.Process();
                UpdateLemonUiMouseState();
                Interval = 0;
            }
            else
            {
                UpdateLemonUiMouseState();
                Interval = _flowState == FlowState.Idle ? 1000 : 0;
            }
        }
        catch (Exception ex)
        {
            Log("OnTick failed: " + ex);
        }
    }

    private void UpdateLemonUiMouseState()
    {
        try
        {
            if (_gearheadMenu == null || !_gearheadMenu.Visible)
                return;

            _gearheadMenu.MouseBehavior = MenuMouseBehavior.Disabled;
            _gearheadMenu.ResetCursorWhenOpened = false;
            _gearheadMenu.CloseOnInvalidClick = false;
            _gearheadMenu.RotateCamera = true;
        }
        catch
        {
        }
    }

    private void EnsureGearheadContactRegistered()
    {
        try
        {
            if (_gearheadContactAdded)
                return;

            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            if (phone.Contacts.Any(c => string.Equals(c.Name, GearheadContactName, StringComparison.OrdinalIgnoreCase)))
            {
                _gearheadContactAdded = true;
                return;
            }

            var contact = new iFruitContact(GearheadContactName)
            {
                Active = true,
                DialTimeout = 2000,
                Bold = false,
                Icon = ContactIcon.HitcherGirl
            };

            contact.Answered += OnGearheadContactAnswered;
            phone.Contacts.Add(contact);
            _gearheadContact = contact;
            _gearheadContactAdded = true;
        }
        catch (Exception ex)
        {
            Log("EnsureGearheadContactRegistered failed: " + ex);
        }
    }

    private void OnGearheadContactAnswered(iFruitContact sender)
    {
        try
        {
            HandleGearheadRequest();
        }
        finally
        {
            try { Game.Player.Character?.Task.PutAwayMobilePhone(); } catch { }
            try { CustomiFruit.GetCurrentInstance()?.Close(0); } catch { }
        }
    }

    private void HandleGearheadRequest()
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return;

            if (!player.IsInVehicle())
            {
                GTA.UI.Screen.ShowSubtitle(GearheadNeedVehicleFirstText, 2500);
                return;
            }

            Vehicle veh = player.CurrentVehicle;
            if (veh == null || !veh.Exists())
            {
                GTA.UI.Screen.ShowSubtitle(GearheadNeedVehicleFirstText, 2500);
                return;
            }

            _lastRejectMessage = null;

            if (!TryResolveUpgradeTarget(veh, out UpgradeTarget target))
            {
                if (!string.IsNullOrWhiteSpace(_lastRejectMessage))
                {
                    ShowGearheadMessage(GearheadFailedTitle, _lastRejectMessage);
                    _lastRejectMessage = null;
                }
                else
                {
                    ShowGearheadMessage(GearheadFailedTitle, GearheadMenuFailureBody);
                }
                return;
            }

            _targetVehicle = target.Vehicle;
            _targetRecord = target.Record;
            _targetOwnerHash = target.Record != null ? target.Record.OwnerModelHash : 0;
            _targetPurchasePrice = Math.Max(0, target.PurchasePrice);
            _targetUpgradeFee = Math.Max(0, target.UpgradeFee);
            _targetFeePercent = Math.Max(0, target.FeePercent);
            _targetCustomerName = target.CustomerName ?? GetCurrentCharacterDisplayName();
            _targetVehicleName = target.VehicleName ?? L("Gearhead_DefaultVehicleName", "Phương tiện");
            _targetPlate = target.Plate ?? SafeGetPlate(veh);

            EnsureGearheadMenuCreated();
            BuildGearheadMenu();
            ConfigureKeyboardOnlyMenu(_gearheadMenu);

            if (_gearheadMenu != null)
            {
                _gearheadMenu.Visible = true;
                Interval = 0;
            }
        }
        catch (Exception ex)
        {
            Log("HandleGearheadRequest failed: " + ex);
        }
    }

    private void EnsureGearheadMenuCreated()
    {
        try
        {
            if (_gearheadMenuReady)
                return;

            _gearheadMenu = new NativeMenu(GearheadMenuTitle, GearheadMenuSubtitle);
            _uiPool.Add(_gearheadMenu);
            _gearheadMenu.Visible = false;
            _gearheadMenuReady = true;
        }
        catch (Exception ex)
        {
            Log("EnsureGearheadMenuCreated failed: " + ex);
        }
    }

    private void ConfigureKeyboardOnlyMenu(NativeMenu menu)
    {
        if (menu == null) return;

        menu.MouseBehavior = MenuMouseBehavior.Disabled;
        menu.ResetCursorWhenOpened = false;
        menu.CloseOnInvalidClick = false;
        menu.RotateCamera = true;
    }

    private void BuildGearheadMenu()
    {
        if (_gearheadMenu == null || _targetVehicle == null)
            return;

        _gearheadMenu.Clear();

        string confirmText = (_targetRecord != null)
            ? GearheadConfirmTitlePersistent
            : GearheadConfirmTitleStreetLoot;

        _confirmItem = new NativeItem(confirmText);
        _cancelItem = new NativeItem(GearheadCancelTitle);

        AddInfoItem(_gearheadMenu, string.Format(CultureInfo.InvariantCulture,
            "1. {0}: {1}", GearheadCustomerLabel, _targetCustomerName));

        AddInfoItem(_gearheadMenu, string.Format(CultureInfo.InvariantCulture,
            "2. {0}: {1}", GearheadVehicleLabel, _targetVehicleName));

        if (_targetRecord != null)
        {
            AddInfoItem(_gearheadMenu, string.Format(CultureInfo.InvariantCulture,
                "3. {0}: ${1:N0}", GearheadPurchasePriceLabel, _targetPurchasePrice));

            string branchText = _targetRecord.HaoUpgradeBranch.HasValue
                ? (_targetRecord.HaoUpgradeBranch.Value == 0
                    ? GearheadUnupgradedStatusText
                    : GearheadAlreadyUpgradedStatusText)
                : GearheadRandomStatusText;

            AddInfoItem(_gearheadMenu, string.Format(CultureInfo.InvariantCulture,
                "4. {0}: {1}", GearheadStatusLabel, branchText));
        }
        else
        {
            AddInfoItem(_gearheadMenu, string.Format(CultureInfo.InvariantCulture,
                "3. {0}: {1}", GearheadPurchasePriceLabel, GearheadPriceNaText));

            AddInfoItem(_gearheadMenu, string.Format(CultureInfo.InvariantCulture,
                "4. {0}: {1}", GearheadStatusLabel, GearheadStreetLootStatusText));
        }

        AddInfoItem(_gearheadMenu, string.Format(CultureInfo.InvariantCulture,
            "5. {0}: ${1:N0}", GearheadComponentPriceLabel, _targetUpgradeFee));

        _confirmItem.Activated += (s, e) =>
        {
            try
            {
                if (!ValidateCurrentUpgradeTarget())
                    return;

                if (Game.Player.Money < _targetUpgradeFee)
                {
                    ShowGearheadMessage(
                        GearheadFailedTitle,
                        string.Format(CultureInfo.InvariantCulture, GearheadInsufficientMoneyText,
                            string.Format(CultureInfo.InvariantCulture, "${0:N0}", _targetUpgradeFee)));
                    CloseGearheadMenu(false);
                    return;
                }

                Game.Player.Money -= _targetUpgradeFee;
                StartUpgradeSequence();
            }
            catch (Exception ex)
            {
                Log("Confirm upgrade failed: " + ex);
                CloseGearheadMenu(false);
            }
        };

        _cancelItem.Activated += (s, e) =>
        {
            CloseGearheadMenu(true);
            ClearTargetState();
        };

        _gearheadMenu.Add(_confirmItem);
        _gearheadMenu.Add(_cancelItem);
    }

    private void AddInfoItem(NativeMenu menu, string text)
    {
        if (menu == null) return;

        var item = new NativeItem(text);
        item.Enabled = true;
        item.Activated += (s, e) => { };
        menu.Add(item);
    }

    private void CloseGearheadMenu(bool setCooldown)
    {
        try
        {
            if (_gearheadMenu != null)
            {
                _gearheadMenu.Visible = false;
                _gearheadMenu.Clear();
            }
        }
        catch
        {
        }

        if (setCooldown)
            EnsureHelpBoxCooldownSet();

        Interval = 1000;
    }

    private void ClearTargetState()
    {
        _targetVehicle = null;
        _targetRecord = null;
        _targetOwnerHash = 0;
        _targetPurchasePrice = 0;
        _targetUpgradeFee = 0;
        _targetFeePercent = 0;
        _targetCustomerName = string.Empty;
        _targetVehicleName = string.Empty;
        _targetPlate = string.Empty;
        _pendingTimeAdvanceMinutes = 0;
        _lastRejectMessage = null;
    }

    private bool ValidateCurrentUpgradeTarget()
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return false;

            if (!player.IsInVehicle())
            {
                GTA.UI.Screen.ShowSubtitle(GearheadNeedVehicleFirstText, 2500);
                return false;
            }

            Vehicle current = player.CurrentVehicle;
            if (current == null || !current.Exists())
            {
                GTA.UI.Screen.ShowSubtitle(GearheadNeedVehicleFirstText, 2500);
                return false;
            }

            if (_targetVehicle == null)
                return false;

            if (!ReferenceEquals(current, _targetVehicle) && current.Handle != _targetVehicle.Handle)
            {
                ShowGearheadMessage(GearheadFailedTitle, L("Gearhead_WrongVehicleText", "Chiếc phương tiện này mua ở cửa hàng hay sao? Tui không có linh kiện của nó!"));
                return false;
            }

            _targetPlate = SafeGetPlate(current);
            _targetVehicleName = PersistentManager.GetVehicleDisplayNamePublic((uint)current.Model.Hash) ?? _targetVehicleName;
            _targetCustomerName = GetCurrentCharacterDisplayName();
            return true;
        }
        catch (Exception ex)
        {
            Log("ValidateCurrentUpgradeTarget failed: " + ex);
            return false;
        }
    }

    private void StartUpgradeSequence()
    {
        try
        {
            CloseGearheadMenu(false);
            GTA.UI.Screen.FadeOut(FadeOutMs);
            _flowState = FlowState.WaitingFadeApply;
            _flowStateSince = Game.GameTime;
        }
        catch (Exception ex)
        {
            Log("StartUpgradeSequence failed: " + ex);
            _flowState = FlowState.Idle;
            GTA.UI.Screen.FadeIn(FadeOutMs);
        }
    }

    private void HandleDeferredUpgradeFlow()
    {
        try
        {
            if (_flowState == FlowState.Idle)
                return;

            int now = Game.GameTime;

            if (_flowState == FlowState.WaitingFadeApply)
            {
                if (now - _flowStateSince < FadeOutMs + PostFadeDelayMs)
                    return;

                if (!ValidateCurrentUpgradeTarget())
                {
                    GTA.UI.Screen.FadeIn(FadeOutMs);
                    _flowState = FlowState.Idle;
                    ClearTargetState();
                    return;
                }

                bool ok = ApplyGearheadUpgradeToCurrentVehicle();
                if (!ok)
                {
                    GTA.UI.Screen.FadeIn(FadeOutMs);
                    _flowState = FlowState.Idle;
                    ShowGearheadMessage(GearheadFailedTitle, GearheadUpgradeFailedText);
                    ClearTargetState();
                    return;
                }

                ApplyGearheadUpgradeTimeAdvance(_pendingTimeAdvanceMinutes);
                _pendingTimeAdvanceMinutes = 0;

                GTA.UI.Screen.FadeIn(FadeOutMs);
                _flowState = FlowState.WaitingFadeIn;
                _flowStateSince = Game.GameTime;
                return;
            }

            if (_flowState == FlowState.WaitingFadeIn)
            {
                if (now - _flowStateSince < FadeOutMs)
                    return;

                _flowState = FlowState.Idle;
                ClearTargetState();
            }
        }
        catch (Exception ex)
        {
            Log("HandleDeferredUpgradeFlow failed: " + ex);
            try { GTA.UI.Screen.FadeIn(FadeOutMs); } catch { }
            _flowState = FlowState.Idle;
            ClearTargetState();
        }
    }

    private bool ApplyGearheadUpgradeToCurrentVehicle()
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || !player.IsInVehicle())
                return false;

            Vehicle veh = player.CurrentVehicle;
            if (veh == null || !veh.Exists())
                return false;

            if (_targetVehicle == null)
                return false;

            if (!ValidateCurrentUpgradeTarget())
                return false;

            _pendingTimeAdvanceMinutes = RollGearheadUpgradeTimeAdvanceMinutes();

            if (_targetRecord != null)
            {
                if (_targetRecord.HaoUpgradeApplied)
                {
                    ShowGearheadMessage(GearheadFailedTitle, GearheadAlreadyProcessedText);
                    return false;
                }

                ApplyRandomLikeUpgrades(veh);

                int haoBranch = _targetRecord.HaoUpgradeBranch.HasValue
                    ? _targetRecord.HaoUpgradeBranch.Value
                    : (_targetVehicle != null && IsVehicleAlreadyMaxUpgraded(_targetVehicle) ? 1 : 0);

                try { PersistentManager.UpdatePersistentFromVehicle(veh); } catch { }
                try { PersistentManager.UpdateHaoUpgradeState(veh, haoBranch, true); } catch { }

                try
                {
                    RewritePersistentVehicleFileAfterUpgrade(veh, _targetRecord, haoBranch);
                }
                catch (Exception ex)
                {
                    Log("RewritePersistentVehicleFileAfterUpgrade failed: " + ex);
                    return false;
                }

                ShowGearheadMessage(GearheadCompletedTitle, GearheadCompletedBody);
                return true;
            }

            ApplyRandomLikeUpgrades(veh);
            ShowGearheadMessage(GearheadLootCompletedTitle, GearheadLootCompletedBody);
            return true;
        }
        catch (Exception ex)
        {
            Log("ApplyGearheadUpgradeToCurrentVehicle failed: " + ex);
            return false;
        }
    }

    private int RollGearheadUpgradeTimeAdvanceMinutes()
    {
        try
        {
            if (_targetRecord != null)
                return _rng.Next(PersistentUpgradeTimeMinMinutes, PersistentUpgradeTimeMaxMinutes + 1);

            return _rng.Next(StreetLootUpgradeTimeMinMinutes, StreetLootUpgradeTimeMaxMinutes + 1);
        }
        catch
        {
            return _targetRecord != null ? 100 : 75;
        }
    }

    private void ApplyGearheadUpgradeTimeAdvance(int minutes)
    {
        try
        {
            if (minutes <= 0)
                return;

            int hours = Function.Call<int>(Hash.GET_CLOCK_HOURS);
            int mins = Function.Call<int>(Hash.GET_CLOCK_MINUTES);

            int total = (hours * 60) + mins + minutes;
            total %= 24 * 60;
            if (total < 0)
                total += 24 * 60;

            int newHours = total / 60;
            int newMins = total % 60;

            Function.Call(Hash.SET_CLOCK_TIME, newHours, newMins, 0);
        }
        catch (Exception ex)
        {
            Log("ApplyGearheadUpgradeTimeAdvance failed: " + ex);
        }
    }

    private bool TryResolveUpgradeTarget(Vehicle veh, out UpgradeTarget target)
    {
        target = null;
        _lastRejectMessage = null;

        try
        {
            if (veh == null || !veh.Exists())
                return false;

            int ownerHash = GetCurrentPlayerModelHash();
            if (ownerHash == 0)
                return false;

            uint modelHash = (uint)veh.Model.Hash;
            string currentPlate = NormalizePlate(SafeGetPlate(veh));
            Vector3 currentPos = veh.Position;

            var lines = ReadAllLinesSafe(PersistentVehiclesFile);
            var candidates = new List<OwnedVehicleRecord>();

            foreach (var line in lines)
            {
                if (!TryParsePersistentVehicleLine(line, out OwnedVehicleRecord rec))
                    continue;

                if (rec == null)
                    continue;

                if (rec.OwnerModelHash != ownerHash)
                    continue;

                if (rec.ModelHash != modelHash)
                    continue;

                candidates.Add(rec);
            }

            if (candidates.Count > 0)
            {
                OwnedVehicleRecord best = null;
                int bestScore = int.MinValue;

                foreach (var rec in candidates)
                {
                    int score = 0;

                    string recPlate = NormalizePlate(rec.Plate);
                    if (!string.IsNullOrWhiteSpace(currentPlate) &&
                        !string.IsNullOrWhiteSpace(recPlate) &&
                        string.Equals(currentPlate, recPlate, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 1000;
                    }

                    float dist = currentPos.DistanceTo2D(rec.Position);
                    if (dist <= 8f) score += 300;
                    else if (dist <= 20f) score += 120;
                    else if (dist <= 50f) score += 30;

                    if (rec.AutoSpawnEnabled) score += 20;
                    if (rec.IsCollateralLocked) score += 10;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = rec;
                    }
                }

                if (best == null)
                    return false;

                if (best.HaoUpgradeApplied)
                {
                    _lastRejectMessage = GearheadAlreadyProcessedText;
                    return false;
                }

                bool branchIs40 = best.HaoUpgradeBranch.HasValue
                    ? best.HaoUpgradeBranch.Value == 0
                    : !IsVehicleAlreadyMaxUpgraded(veh);

                int feePercent = branchIs40
                    ? UpgradeFeePercentBranch40
                    : UpgradeFeePercentBranch60;

                if (IsVehicleAlreadyMaxUpgraded(veh))
                {
                    _lastRejectMessage = GearheadAlreadyMaxedText;
                    return false;
                }

                target = new UpgradeTarget
                {
                    Vehicle = veh,
                    Record = best,
                    IsPersistentOwnedVehicle = true,
                    IsStreetLootVehicle = false,
                    StreetLootAccepted = false,
                    PurchasePrice = Math.Max(0, best.PurchasePrice),
                    UpgradeFee = ComputeUpgradeFee(Math.Max(0, best.PurchasePrice), feePercent),
                    FeePercent = feePercent,
                    HaoUpgradeBranch = branchIs40 ? 0 : 1,
                    CustomerName = GetCurrentCharacterDisplayName(),
                    VehicleName = PersistentManager.GetVehicleDisplayNamePublic(best.ModelHash) ?? L("Gearhead_DefaultVehicleName", "Phương tiện"),
                    Plate = SafeGetPlate(veh)
                };

                return true;
            }

            target = new UpgradeTarget
            {
                Vehicle = veh,
                Record = null,
                IsPersistentOwnedVehicle = false,
                IsStreetLootVehicle = true,
                StreetLootAccepted = true,
                PurchasePrice = 0,
                UpgradeFee = RollStreetLootComponentFee(),
                FeePercent = 0,
                HaoUpgradeBranch = null,
                CustomerName = GetCurrentCharacterDisplayName(),
                VehicleName = PersistentManager.GetVehicleDisplayNamePublic(modelHash) ?? L("Gearhead_StreetLootVehicleName", "Phương tiện lụm vặt"),
                Plate = SafeGetPlate(veh)
            };

            return true;
        }
        catch (Exception ex)
        {
            Log("TryResolveUpgradeTarget failed: " + ex);
            return false;
        }
    }

    private void RewritePersistentVehicleFileAfterUpgrade(Vehicle veh, OwnedVehicleRecord oldRecord, int? haoBranchOverride)
    {
        EnsureDataFolderExists();

        string[] existingLines = ReadAllLinesSafe(PersistentVehiclesFile);
        var kept = new List<string>();

        foreach (string line in existingLines)
        {
            try
            {
                if (MatchesRecordLine(line, oldRecord, veh))
                    continue;

                kept.Add(line);
            }
            catch
            {
                kept.Add(line);
            }
        }

        string newLine = SerializeCurrentVehicleAsPersistentLine(veh, oldRecord, true, haoBranchOverride);
        kept.Add(newLine);

        WriteAllLinesAtomic(PersistentVehiclesFile, kept.ToArray());
    }

    private string SerializeCurrentVehicleAsPersistentLine(Vehicle veh, OwnedVehicleRecord oldRecord, bool haoApplied, int? haoBranchOverride)
    {
        uint modelHash = (uint)veh.Model.Hash;

        string modelString = PersistentManager.GetVehicleDisplayNamePublic(modelHash)
            ?? string.Format(CultureInfo.InvariantCulture, "0x{0:X}", modelHash);

        modelString = modelString.Replace("|", "");
        string hexSuffix = string.Format(CultureInfo.InvariantCulture, " (0x{0:X})", modelHash);
        if (!modelString.EndsWith(string.Format(CultureInfo.InvariantCulture, "(0x{0:X})", modelHash), StringComparison.OrdinalIgnoreCase))
            modelString += hexSuffix;

        string plate = SafeGetPlate(veh).Replace("|", "");
        string modsSerialized = SerializeMods(veh);
        string tyreRgb = SerializeTyreSmoke(veh);
        string extrasSerialized = SerializeExtras(veh);
        string neonRgb = SerializeNeonColor(veh);

        int dashValue;
        bool hasDash = TryReadDashboardColor(veh, out dashValue);
        string dashStr = hasDash
            ? dashValue.ToString(CultureInfo.InvariantCulture)
            : (oldRecord != null && oldRecord.DashboardColor.HasValue
                ? oldRecord.DashboardColor.Value.ToString(CultureInfo.InvariantCulture)
                : "");

        int ownerHash = oldRecord != null ? oldRecord.OwnerModelHash : GetCurrentPlayerModelHash();
        bool collateralLocked = oldRecord != null && oldRecord.IsCollateralLocked;
        int purchasePrice = oldRecord != null ? oldRecord.PurchasePrice : 0;
        int autoSpawn = oldRecord != null && oldRecord.AutoSpawnEnabled ? 1 : 0;

        int turbo = 0;
        try { turbo = Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, veh.Handle, 18) ? 1 : 0; } catch { turbo = 0; }

        int? savedLivery = null;
        try
        {
            int liv = Function.Call<int>(Hash.GET_VEHICLE_LIVERY, veh.Handle);
            if (liv >= 0) savedLivery = liv;
        }
        catch
        {
            savedLivery = oldRecord != null ? oldRecord.SavedLivery : null;
        }

        int? primary = null, secondary = null, pearl = null, wheel = null, windowTint = null;
        try
        {
            var out1 = new OutputArgument();
            var out2 = new OutputArgument();
            Function.Call(Hash.GET_VEHICLE_COLOURS, veh.Handle, out1, out2);
            primary = out1.GetResult<int>();
            secondary = out2.GetResult<int>();
        }
        catch
        {
            if (oldRecord != null)
            {
                primary = oldRecord.PrimaryColor;
                secondary = oldRecord.SecondaryColor;
            }
        }

        try
        {
            var pearlArg = new OutputArgument();
            var wheelArg = new OutputArgument();
            Function.Call(Hash.GET_VEHICLE_EXTRA_COLOURS, veh.Handle, pearlArg, wheelArg);
            pearl = pearlArg.GetResult<int>();
            wheel = wheelArg.GetResult<int>();
        }
        catch
        {
            if (oldRecord != null)
            {
                pearl = oldRecord.PearlColor;
                wheel = oldRecord.WheelColor;
            }
        }

        try { windowTint = Function.Call<int>(Hash.GET_VEHICLE_WINDOW_TINT, veh.Handle); }
        catch { windowTint = oldRecord != null ? oldRecord.WindowTint : null; }

        int? haoBranchValue = haoBranchOverride ?? (oldRecord != null ? oldRecord.HaoUpgradeBranch : null);
        string haoBranchStr = haoBranchValue.HasValue
            ? haoBranchValue.Value.ToString(CultureInfo.InvariantCulture)
            : "";

        string haoAppliedStr = haoApplied ? "1" : ((oldRecord != null && oldRecord.HaoUpgradeApplied) ? "1" : "0");

        return string.Format(CultureInfo.InvariantCulture,
            "{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}|{9}|{10}|{11}|{12}|{13}|{14}|{15}|{16}|{17}|{18}|{19}|{20}|{21}|{22}|{23}",
            modelString,
            veh.Position.X, veh.Position.Y, veh.Position.Z,
            veh.Heading,
            plate,
            ownerHash,
            autoSpawn,
            modsSerialized,
            turbo,
            purchasePrice,
            savedLivery.HasValue ? savedLivery.Value.ToString(CultureInfo.InvariantCulture) : string.Empty,
            primary.HasValue ? primary.Value.ToString(CultureInfo.InvariantCulture) : string.Empty,
            secondary.HasValue ? secondary.Value.ToString(CultureInfo.InvariantCulture) : string.Empty,
            pearl.HasValue ? pearl.Value.ToString(CultureInfo.InvariantCulture) : string.Empty,
            wheel.HasValue ? wheel.Value.ToString(CultureInfo.InvariantCulture) : string.Empty,
            windowTint.HasValue ? windowTint.Value.ToString(CultureInfo.InvariantCulture) : string.Empty,
            tyreRgb,
            extrasSerialized,
            neonRgb,
            dashStr,
            collateralLocked ? 1 : 0,
            haoBranchStr,
            haoAppliedStr
        );
    }

    private static bool MatchesRecordLine(string line, OwnedVehicleRecord oldRecord, Vehicle veh)
    {
        try
        {
            if (oldRecord == null || string.IsNullOrWhiteSpace(line))
                return false;

            if (!TryParsePersistentVehicleLine(line, out OwnedVehicleRecord rec))
                return false;

            if (rec == null)
                return false;

            if (rec.OwnerModelHash != oldRecord.OwnerModelHash)
                return false;

            if (rec.ModelHash != oldRecord.ModelHash)
                return false;

            string plateA = NormalizePlate(rec.Plate);
            string plateB = NormalizePlate(oldRecord.Plate);

            if (!string.IsNullOrWhiteSpace(plateA) &&
                !string.IsNullOrWhiteSpace(plateB) &&
                string.Equals(plateA, plateB, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (veh != null && veh.Exists())
            {
                if (veh.Position.DistanceTo2D(rec.Position) <= 8f)
                    return true;
            }

            return rec.Position.DistanceTo2D(oldRecord.Position) <= 8f;
        }
        catch
        {
            return false;
        }
    }

    private void ApplyRandomLikeUpgrades(Vehicle veh)
    {
        if (veh == null || !veh.Exists())
            return;

        try { Function.Call(Hash.SET_VEHICLE_MOD_KIT, veh.Handle, 0); } catch { }

        for (int modType = 0; modType <= 49; modType++)
        {
            try
            {
                if (!veh.Exists())
                    break;

                if (modType == 18)
                    continue;

                int count = SafeCall(() => Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, veh.Handle, modType), 0);
                if (count > 0)
                {
                    int chosenIndex = _rng.Next(0, count);
                    SafeCall(() =>
                    {
                        Function.Call(Hash.SET_VEHICLE_MOD, veh.Handle, modType, chosenIndex, false);
                        return 0;
                    }, 0);
                }
            }
            catch
            {
            }
        }

        int[] perfMods = new int[] { 11, 12, 13, 16 };
        foreach (int pm in perfMods)
        {
            try
            {
                if (!veh.Exists())
                    break;

                int count = SafeCall(() => Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, veh.Handle, pm), 0);
                if (count > 0)
                {
                    int chosenIndex = _rng.Next(0, count);
                    SafeCall(() =>
                    {
                        Function.Call(Hash.SET_VEHICLE_MOD, veh.Handle, pm, chosenIndex, false);
                        return 0;
                    }, 0);
                }
            }
            catch
            {
            }
        }

        try { SafeCall(() => { Function.Call(Hash.TOGGLE_VEHICLE_MOD, veh.Handle, 18, true); return 0; }, 0); } catch { }

        try
        {
            int turboCount = SafeCall(() => Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, veh.Handle, 18), 0);
            if (turboCount > 0)
            {
                int chosenIndex = _rng.Next(0, turboCount);
                SafeCall(() =>
                {
                    Function.Call(Hash.SET_VEHICLE_MOD, veh.Handle, 18, chosenIndex, false);
                    return 0;
                }, 0);
            }
        }
        catch
        {
        }

        try
        {
            int liveryCount = SafeCall(() => Function.Call<int>(Hash.GET_VEHICLE_LIVERY_COUNT, veh.Handle), 0);
            if (liveryCount > 0)
            {
                int chosenLivery = _rng.Next(0, liveryCount);
                SafeCall(() =>
                {
                    Function.Call(Hash.SET_VEHICLE_LIVERY, veh.Handle, chosenLivery);
                    return 0;
                }, 0);
            }
        }
        catch
        {
        }

        try { ApplyRandomVehicleColours(veh); } catch { }
        try { Function.Call(Hash.SET_VEHICLE_EXTRA_COLOURS, veh.Handle, 0, 0); } catch { }
        try { Function.Call(Hash.SET_VEHICLE_WINDOW_TINT, veh.Handle, 1); } catch { }
        try { ApplyRandomTyreSmokeColor(veh); } catch { }

        try
        {
            for (int ex = 1; ex <= 20; ex++)
            {
                if (!veh.Exists())
                    break;

                bool exists = SafeCall(() => Function.Call<bool>(Hash.DOES_EXTRA_EXIST, veh.Handle, ex), false);
                if (exists)
                {
                    SafeCall(() =>
                    {
                        Function.Call(Hash.SET_VEHICLE_EXTRA, veh.Handle, ex, 0);
                        return 0;
                    }, 0);
                }
            }
        }
        catch
        {
        }

        try
        {
            for (int i = 0; i <= 3; i++)
            {
                try { Function.Call(Hash.SET_VEHICLE_NEON_ENABLED, veh.Handle, i, true); } catch { }
            }
        }
        catch
        {
        }

        try
        {
            int paintIndex = _rng.Next(0, 178);
            SafeCall(() =>
            {
                Function.Call(Hash.SET_VEHICLE_EXTRA_COLOUR_6, veh.Handle, paintIndex);
                return 0;
            }, 0);
        }
        catch
        {
        }

        try
        {
            veh.Repair();
            veh.DirtLevel = 0f;
            veh.IsEngineRunning = true;
        }
        catch
        {
        }
    }

    private void ApplyRandomTyreSmokeColor(Vehicle v)
    {
        if (!SafeExists(v)) return;

        try
        {
            string[] colors = new string[]
            {
                "PURPLELIGHT","YELLOWLIGHT","ORANGELIGHT","GREENLIGHT","PLATINUM","SAME_CREW","YOGA",
                "FRANKLIN","CONTROLLER_MICHAEL","DEGEN_CYAN","REDLIGHT","SIMPLEBLIP_DEFAULT","FREINDLY",
                "LOCATION","PICKUP","DARTS","WAYPOINT","TREVOR","WAYPOINTLIGHT","CONTROLLER_FRANKLIN",
                "VIDEO_EDITOR_AMBIENT","GB","G","Ice White","RADAR_DAMAGE","RADAR_ARMOUR","NET_PLAYER3",
                "GANG4","PINKLIGHT","PM_MITEM_HIGHLIGHT","TENNIS","NORTH_BLUE","SOCIAL CLUB","PLATFORM_GREEN",
                "B","G5","Blue","Yellow","Schafter Purple","NET_PLAYER4","NET_PLAYER6","NET_PLAYER8",
                "NET_PLAYER10","NET_PLAYER11","NET_PLAYER12","NET_PLAYER13","NET_PLAYER18","NET_PLAYER19",
                "NET_PLAYER21","NET_PLAYER26","NET_PLAYER27","NET_PLAYER28","NET_PLAYER29","NET_PLAYER30",
                "NET_PLAYER31","NET_PLAYER32","Bronze","Silver","ENEMY","INACTIVE_MISSION",
                "GWC and Golfing Society","GOLF_P2","GOLF_P3","PURE_WHITE","PM_WEAPONS_LOCKED",
                "VIDEO_EDITOR_AUDIO","VIDEO_EDITOR_TEXT","HB_YELLOW","LOW_FLOW","GREYLIGHT","G1","G2",
                "G3","G4","G6","G7","G14","DEGEN_RED","DEGEN_YELLOW","DEGEN_GREEN","DEGEN_MAGENTA",
                "VIDEO_EDITOR_VIDEO","HB_BLUE","VIDEO_EDITOR_SCORE","VIDEO_EDITOR_TEXT_FADEOUT",
                "HEIST_BACKGROUND","VIDEO_EDITOR_AMBIENT_FADEOUT","LOW_FLOW_DARK","ADVERSARY","DEGEN_BLUE",
                "STUNT_1","STUNT_2","Gray","Red","GREENDARK","RADAR_HEALTH","SHOOTING_RANGE","FLIGHT_SCHOOL",
                "WAYPOINTDARK","Electric Blue","Mint Green","Lime Green"
            };

            string chosenName = colors[_rng.Next(colors.Length)];
            var rgb = NameToRgb(chosenName);

            Function.Call(Hash.SET_VEHICLE_TYRE_SMOKE_COLOR, v.Handle, rgb.Item1, rgb.Item2, rgb.Item3);
        }
        catch
        {
            try { Function.Call(Hash.SET_VEHICLE_TYRE_SMOKE_COLOR, v.Handle, 200, 50, 255); } catch { }
        }
    }

    private Tuple<int, int, int> NameToRgb(string name)
    {
        if (string.IsNullOrEmpty(name)) return Tuple.Create(255, 255, 255);

        int r = 0, g = 0, b = 0;
        for (int i = 0; i < name.Length; i++)
        {
            int ch = (int)name[i];
            r = (r * 31 + ch + 7) % 256;
            g = (g * 37 + ch + 13) % 256;
            b = (b * 41 + ch + 97) % 256;
        }

        if (r < 30 && g < 30 && b < 30)
        {
            r = (r + 120) % 256;
            g = (g + 120) % 256;
            b = (b + 120) % 256;
        }

        int max = Math.Max(r, Math.Max(g, b));
        if (max > 0 && max < 200)
        {
            float factor = 200f / Math.Max(1, max);
            r = Math.Min(255, (int)(r * factor));
            g = Math.Min(255, (int)(g * factor));
            b = Math.Min(255, (int)(b * factor));
        }

        return Tuple.Create(r, g, b);
    }

    private void ApplyRandomVehicleColours(Vehicle v)
    {
        if (!SafeExists(v)) return;

        try
        {
            int primary = _rng.Next(0, 219);
            int secondary = _rng.Next(0, 219);
            Function.Call(Hash.SET_VEHICLE_COLOURS, v.Handle, primary, secondary);
        }
        catch
        {
            try { Function.Call(Hash.SET_VEHICLE_COLOURS, v.Handle, 0, 0); } catch { }
        }
    }

    private static bool TryReadDashboardColor(Vehicle veh, out int dash)
    {
        dash = 0;
        try
        {
            var outDash = new OutputArgument();
            Function.Call(Hash.GET_VEHICLE_EXTRA_COLOUR_6, veh.Handle, outDash);
            dash = outDash.GetResult<int>();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string SerializeMods(Vehicle veh)
    {
        try { Function.Call(Hash.SET_VEHICLE_MOD_KIT, veh.Handle, 0); } catch { }

        var mods = new Dictionary<int, int>();
        for (int modType = 0; modType <= 49; modType++)
        {
            try
            {
                int idx = Function.Call<int>(Hash.GET_VEHICLE_MOD, veh.Handle, modType);
                if (idx >= 0)
                    mods[modType] = idx;
            }
            catch
            {
            }
        }

        try
        {
            return string.Join(",", mods.Select(kv => string.Format(CultureInfo.InvariantCulture, "{0}:{1}", kv.Key, kv.Value)));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SerializeTyreSmoke(Vehicle veh)
    {
        try
        {
            var r = new OutputArgument();
            var g = new OutputArgument();
            var b = new OutputArgument();
            Function.Call(Hash.GET_VEHICLE_TYRE_SMOKE_COLOR, veh.Handle, r, g, b);
            return string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", r.GetResult<int>(), g.GetResult<int>(), b.GetResult<int>());
        }
        catch
        {
            return "0,0,0";
        }
    }

    private static string SerializeExtras(Vehicle veh)
    {
        try
        {
            var extras = new List<int>();
            for (int ex = 1; ex <= 20; ex++)
            {
                try
                {
                    if (Function.Call<bool>(Hash.DOES_EXTRA_EXIST, veh.Handle, ex))
                        extras.Add(ex);
                }
                catch
                {
                }
            }

            return string.Join(",", extras);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SerializeNeonColor(Vehicle veh)
    {
        try
        {
            var r = new OutputArgument();
            var g = new OutputArgument();
            var b = new OutputArgument();
            Function.Call(Hash.GET_VEHICLE_NEON_COLOUR, veh.Handle, r, g, b);
            return string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", r.GetResult<int>(), g.GetResult<int>(), b.GetResult<int>());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int GetCurrentPlayerModelHash()
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

    private static string GetCurrentCharacterDisplayName()
    {
        try
        {
            uint hash = (uint)GetCurrentPlayerModelHash();
            if (hash == (uint)PedHash.Franklin)
                return "Franklin Clinton";
            if (hash == (uint)PedHash.Michael)
                return "Michael De Santa";
            if (hash == (uint)PedHash.Trevor)
                return "Trevor Philips";
        }
        catch
        {
        }

        return L("Gearhead_DefaultCustomerName", "Khách hàng");
    }

    private static string SafeGetPlate(Vehicle v)
    {
        try
        {
            if (v == null || !v.Exists())
                return string.Empty;

            try { return v.Mods.LicensePlate ?? string.Empty; } catch { }
            try { return Function.Call<string>(Hash.GET_VEHICLE_NUMBER_PLATE_TEXT, v.Handle) ?? string.Empty; } catch { }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static int ComputeUpgradeFee(int purchasePrice, int feePercent)
    {
        try
        {
            if (purchasePrice <= 0)
                return 375000;

            decimal fee = Math.Round((decimal)purchasePrice * Math.Max(0, feePercent) / 100m, MidpointRounding.AwayFromZero);
            if (fee < 0) fee = 0;
            return (int)Math.Min(int.MaxValue, fee);
        }
        catch
        {
            return 100000;
        }
    }

    private int RollStreetLootComponentFee()
    {
        try
        {
            return _rng.Next(300000, 500000 + 1);
        }
        catch
        {
            return 375000;
        }
    }

    private bool IsVehicleAlreadyMaxUpgraded(Vehicle veh)
    {
        try
        {
            if (veh == null || !veh.Exists())
                return false;

            try { Function.Call(Hash.SET_VEHICLE_MOD_KIT, veh.Handle, 0); } catch { }

            for (int modType = 0; modType <= 49; modType++)
            {
                if (modType == 18)
                    continue;

                int count = SafeCall(() => Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, veh.Handle, modType), 0);
                if (count <= 0)
                    continue;

                int current = SafeCall(() => Function.Call<int>(Hash.GET_VEHICLE_MOD, veh.Handle, modType), -1);
                if (current < 0 || current < count - 1)
                    return false;
            }

            int turboCount = SafeCall(() => Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, veh.Handle, 18), 0);
            if (turboCount > 0)
            {
                bool turboOn = SafeCall(() => Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, veh.Handle, 18), false);
                if (!turboOn)
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizePlate(string plate)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(plate))
                return string.Empty;

            return new string(plate.Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToUpperInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool TryParsePersistentVehicleLine(string line, out OwnedVehicleRecord record)
    {
        record = null;

        try
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            var p = line.Split('|');
            if (p.Length < 7)
                return false;

            string modelField = p[0].Trim();
            uint modelHash;
            if (!TryParseModelHash(modelField, out modelHash))
                return false;

            float x = 0f, y = 0f, z = 0f, heading = 0f;
            float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x);
            float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out y);
            float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out z);
            float.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out heading);

            string plate = p.Length > 5 ? p[5] : string.Empty;
            int ownerHash = 0;
            if (p.Length > 6)
                int.TryParse(p[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out ownerHash);

            bool autoSpawn = false;
            if (p.Length > 7)
            {
                int tmp;
                if (int.TryParse(p[7], out tmp))
                    autoSpawn = tmp != 0;
            }

            Dictionary<int, int> mods = new Dictionary<int, int>();
            if (p.Length > 8 && !string.IsNullOrWhiteSpace(p[8]))
            {
                foreach (var part in p[8].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = part.Split(':');
                    if (kv.Length != 2)
                        continue;

                    int mt, mi;
                    if (int.TryParse(kv[0], out mt) && int.TryParse(kv[1], out mi))
                        mods[mt] = mi;
                }
            }

            int turbo = 0;
            if (p.Length > 9)
                int.TryParse(p[9], out turbo);

            int purchasePrice = 0;
            if (p.Length > 10)
                int.TryParse(p[10], out purchasePrice);

            int? savedLivery = null;
            int? primary = null;
            int? secondary = null;
            int? pearl = null;
            int? wheel = null;
            int? windowTint = null;
            int[] tyreRgb = new int[3] { 0, 0, 0 };
            List<int> extrasExist = new List<int>();
            int[] neonRgb = null;
            int? dashColor = null;
            bool collateralLocked = false;
            int? haoUpgradeBranch = null;
            bool haoUpgradeApplied = false;

            try
            {
                if (p.Length > 11 && !string.IsNullOrWhiteSpace(p[11])) { int v; if (int.TryParse(p[11], out v)) savedLivery = v; }
                if (p.Length > 12 && !string.IsNullOrWhiteSpace(p[12])) { int v; if (int.TryParse(p[12], out v)) primary = v; }
                if (p.Length > 13 && !string.IsNullOrWhiteSpace(p[13])) { int v; if (int.TryParse(p[13], out v)) secondary = v; }
                if (p.Length > 14 && !string.IsNullOrWhiteSpace(p[14])) { int v; if (int.TryParse(p[14], out v)) pearl = v; }
                if (p.Length > 15 && !string.IsNullOrWhiteSpace(p[15])) { int v; if (int.TryParse(p[15], out v)) wheel = v; }
                if (p.Length > 16 && !string.IsNullOrWhiteSpace(p[16])) { int v; if (int.TryParse(p[16], out v)) windowTint = v; }

                if (p.Length > 17 && !string.IsNullOrWhiteSpace(p[17]))
                {
                    var rr = p[17].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (rr.Length >= 3)
                    {
                        int.TryParse(rr[0], out tyreRgb[0]);
                        int.TryParse(rr[1], out tyreRgb[1]);
                        int.TryParse(rr[2], out tyreRgb[2]);
                    }
                }

                if (p.Length > 18 && !string.IsNullOrWhiteSpace(p[18]))
                {
                    foreach (var s in p[18].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        int ex;
                        if (int.TryParse(s, out ex))
                            extrasExist.Add(ex);
                    }
                }

                if (p.Length > 19 && !string.IsNullOrWhiteSpace(p[19]))
                {
                    var rr = p[19].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (rr.Length >= 3)
                    {
                        int nr, ng, nb;
                        if (int.TryParse(rr[0], out nr) && int.TryParse(rr[1], out ng) && int.TryParse(rr[2], out nb))
                            neonRgb = new[] { nr, ng, nb };
                    }
                }

                if (p.Length > 20 && !string.IsNullOrWhiteSpace(p[20]))
                {
                    int v;
                    if (int.TryParse(p[20], out v))
                        dashColor = v;
                }

                if (p.Length > 21)
                {
                    int v;
                    if (int.TryParse(p[21], out v))
                        collateralLocked = v != 0;
                }

                if (p.Length > 22 && !string.IsNullOrWhiteSpace(p[22]))
                {
                    int tmp;
                    if (int.TryParse(p[22], out tmp) && (tmp == 0 || tmp == 1))
                        haoUpgradeBranch = tmp;
                }

                if (p.Length > 23 && !string.IsNullOrWhiteSpace(p[23]))
                {
                    int tmp;
                    if (int.TryParse(p[23], out tmp))
                        haoUpgradeApplied = tmp != 0;
                }
            }
            catch
            {
            }

            record = new OwnedVehicleRecord
            {
                RawLine = line,
                ModelHash = modelHash,
                Position = new Vector3(x, y, z),
                Heading = heading,
                Plate = plate,
                OwnerModelHash = ownerHash,
                AutoSpawnEnabled = autoSpawn,
                Mods = mods,
                TurboOn = turbo != 0,
                PurchasePrice = purchasePrice,
                SavedLivery = savedLivery,
                PrimaryColor = primary,
                SecondaryColor = secondary,
                PearlColor = pearl,
                WheelColor = wheel,
                WindowTint = windowTint,
                TyreSmokeColor = tyreRgb,
                ExtrasExist = extrasExist,
                NeonColor = neonRgb,
                DashboardColor = dashColor,
                IsCollateralLocked = collateralLocked,
                HaoUpgradeBranch = haoUpgradeBranch,
                HaoUpgradeApplied = haoUpgradeApplied
            };

            return true;
        }
        catch
        {
            record = null;
            return false;
        }
    }

    private static bool TryParseModelHash(string modelField, out uint hash)
    {
        hash = 0;

        try
        {
            if (string.IsNullOrWhiteSpace(modelField))
                return false;

            string s = modelField.Trim();

            int idx = s.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                int start = idx + 2;
                int end = start;
                while (end < s.Length && Uri.IsHexDigit(s[end]))
                    end++;

                string hexPart = s.Substring(start, end - start);
                if (hexPart.Length > 0 &&
                    uint.TryParse(hexPart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hash))
                {
                    return true;
                }
            }

            string raw = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s.Substring(2).Trim() : s;

            bool allHex = raw.Length > 0;
            for (int i = 0; i < raw.Length; i++)
            {
                if (!Uri.IsHexDigit(raw[i]))
                {
                    allHex = false;
                    break;
                }
            }

            if (allHex &&
                uint.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hash))
            {
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static string[] ReadAllLinesSafe(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new string[0];

            return File.ReadAllLines(path, Encoding.UTF8);
        }
        catch
        {
            return new string[0];
        }
    }

    private static void WriteAllLinesAtomic(string path, string[] lines)
    {
        EnsureDataFolderExists();

        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        using (var sw = new StreamWriter(fs, Encoding.UTF8))
        {
            foreach (var line in lines)
                sw.WriteLine(line);

            sw.Flush();
            fs.Flush(true);
        }

        if (File.Exists(path))
        {
            try { File.Delete(path); } catch { }
        }

        File.Move(tmp, path);
    }

    private static void EnsureDataFolderExists()
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
        }
        catch
        {
        }
    }

    private static void ShowGearheadMessage(string subject, string body)
    {
        try
        {
            PlayFrontendSound(GearheadMessageSoundName, GearheadMessageSoundSet);
            Notification.Show(NotificationIcon.HitcherGirl, GearheadContactName, subject, body);
            return;
        }
        catch
        {
        }

        try
        {
            GTA.UI.Screen.ShowSubtitle(body, 3000);
        }
        catch
        {
        }
    }

    private static void PlayFrontendSound(string soundName, string soundSet)
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

    private void EnsureHelpBoxCooldownSet()
    {
        try
        {
            _lastMenuUpdateTime = Game.GameTime + 5000;
        }
        catch { }
    }

    private bool SafeExists(Entity e)
    {
        try { return e != null && e.Exists(); }
        catch { return false; }
    }

    private T SafeCall<T>(Func<T> fn, T fallback = default(T))
    {
        try { return fn == null ? fallback : fn(); }
        catch { return fallback; }
    }

    private static void Log(string msg)
    {
        try
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GTA V Mods", "Gearhead");
            Directory.CreateDirectory(root);
            File.AppendAllText(Path.Combine(root, "gearhead_log.txt"), DateTime.Now.ToString("s") + "  " + msg + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }
}