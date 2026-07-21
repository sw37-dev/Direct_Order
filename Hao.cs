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
using System.Drawing;

public class Hao : Script
{
    private static string L(string key, string fallback)
    {
        return Language.Get(key, fallback);
    }

    private static string LT(string key, string fallback, params string[] tokensAndValues)
    {
        return Language.ReplaceTokens(Language.Get(key, fallback), tokensAndValues);
    }

    private static string HaoContactName => L("Hao_ContactName", "Hao");
    private static string HaoMenuTitle => L("VehicleUpgrade_Title", "Upgrade Mod Kit");
    private static string HaoMenuSubtitle => L("VehicleUpgrade_Subtitle", "CHI TIẾT NÂNG CẤP");
    private static string HaoConfirmTitle => L("VehicleUpgrade_Confirm", "Xác nhận nâng cấp linh kiện");
    private static string HaoCancelTitle => L("VehicleUpgrade_Cancel", "Hủy bỏ nâng cấp");

    private static string HaoNeedVehicleFirstText => L("VehicleUpgrade_NeedVehicleFirst", "~HUD_COLOUR_REDLIGHT~Hãy vào trong xe trước đã~s~");
    private static string HaoFailedTitle => L("VehicleUpgrade_FailedTitle", "Nâng cấp thất bại");
    private static string HaoUnknownVehicleText => L("VehicleUpgrade_UnknownVehicleText", "Chiếc phương tiện này mày lụm ở xó xỉnh nào vậy?");
    private static string HaoAlreadyMaxedText => L("VehicleUpgrade_AlreadyMaxedText", "Chiếc phương tiện này đã thuộc dạng cao cấp rồi!");
    private static string HaoFullVehicleText => L("VehicleUpgrade_FullVehicleText", "Chiếc phương tiện này mày mua ở cửa hàng đúng không? Nó được cửa hàng lắp nhiều linh kiện cao cấp rồi mà mày còn đòi gì nữa chứ?");
    private static string HaoComponentRejectText => L("VehicleUpgrade_ComponentRejectText", "Tao không đủ linh kiện cho mấy chiếc lụm vặt mà mày nhặt kiểu này đâu, thử chiếc khác xem nào!?");
    private static string HaoUpgradeFailedText => L("VehicleUpgrade_UpgradeFailedText", "Không thể nâng cấp phương tiện này.");
    private static string HaoInsufficientMoneyText => L("VehicleUpgrade_InsufficientMoney", "Không đủ tiền để mua linh kiện. Cần ~y~{0}~s~ lận.");

    private static string HaoCustomerNameLabel => L("VehicleUpgrade_CustomerNameLabel", "Tên khách hàng");
    private static string HaoVehicleNameLabel => L("VehicleUpgrade_VehicleNameLabel", "Tên phương tiện");
    private static string HaoPurchasePriceLabel => L("VehicleUpgrade_PurchasePriceLabel", "Giá phương tiện");
    private static string HaoStatusLabel => L("VehicleUpgrade_StatusLabel", "Tình trạng");
    private static string HaoComponentPriceLabel => L("VehicleUpgrade_ComponentPriceLabel", "Giá linh kiện");
    private static string HaoPurchasedUpgradedStatus => L("VehicleUpgrade_StatusUpgraded", "Linh kiện chưa nâng cấp");
    private static string HaoUnknownStatus => L("VehicleUpgrade_StatusUnknown", "Không xác định linh kiện");
    private static string HaoPriceNaText => L("VehicleUpgrade_PriceNa", "N/A");

    private static string HaoDefaultVehicleName => L("VehicleUpgrade_DefaultVehicleName", "Phương tiện");
    private static string HaoStreetLootVehicleName => L("VehicleUpgrade_StreetLootVehicleName", "Phương tiện lụm vặt");
    private static string HaoDefaultCustomerName => L("VehicleUpgrade_DefaultCustomerName", "Khách hàng");

    private const int GameReadyDelayMs = 2500;
    private const int FadeOutMs = 2000;
    private const int PostFadeDelayMs = 1000;

    private const int UpgradeFeePercent = 33;

    private readonly Random _rng = new Random();

    private const int StreetLootRejectChancePercent = 60;
    private const int StreetLootMinComponentFee = 500000;
    private const int StreetLootMaxComponentFee = 1000000;

    private const int HaoPreviewBlipLifetimeMs = 10000;
    private const float HaoPreviewInteractRadius = 4f;

    private static readonly Vector3[] HaoPreviewLocations = new[]
    {
        new Vector3(-962.8352f, -1974.9480f, 13.19158f),
        new Vector3(-946.4146f, -1966.8780f, 13.19158f),
        new Vector3(867.1649f, -1061.4060f, 28.93521f),
        new Vector3(841.5558f, -1161.7150f, 25.26783f),
        new Vector3(36.01333f, -1095.7110f, 29.48212f),
        new Vector3(-363.0708f, -227.2436f, 37.12526f),
        new Vector3(1137.4270f, 2653.9170f, 37.99837f),
        new Vector3(151.0670f, 6607.6260f, 31.87792f),
        new Vector3(2410.6560f, 5023.7310f, 46.19401f),
        new Vector3(-3155.9820f, 1085.1210f, 20.70576f)
    };

    private const string HaoMessageSoundName = "Text_Arrive_Tone";
    private const string HaoMessageSoundSet = "Phone_SoundSet_Default";

    private string _lastUpgradeRejectMessage = null;

    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "PersistentManager");

    private static readonly string PersistentVehiclesFile = Path.Combine(DataFolder, "persistent_vehicles.txt");

    private readonly ObjectPool _uiPool = new ObjectPool();
    private NativeMenu _haoMenu;
    private bool _haoMenuReady = false;

    private iFruitContact _haoContact = null;
    private bool _haoContactAdded = false;

    private bool _haoPreviewActive = false;
    private int _haoPreviewBlipsExpireAt = 0;
    private readonly List<Blip> _haoPreviewBlips = new List<Blip>();

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
    private bool _targetIsFull = false;
    private string _targetCustomerName = string.Empty;
    private string _targetVehicleName = string.Empty;
    private string _targetPlate = string.Empty;

    private int _gameReadySince = -1;
    private bool _modReady = false;

    private int _lastMenuUpdateTime = 0;

    private NativeItem _confirmItem;
    private NativeItem _cancelItem;

    private static readonly Vector3 DefaultSubtitlePos = Vector3.Zero;

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
        public int? HaoUpgradeBranch;
        public bool HaoUpgradeApplied;
    }

    private sealed class UpgradeTarget
    {
        public Vehicle Vehicle;
        public OwnedVehicleRecord Record;
        public bool IsFull;
        public int PurchasePrice;
        public int UpgradeFee;
        public string CustomerName;
        public string VehicleName;
        public string Plate;
        public bool IsPersistentOwnedVehicle;
        public bool IsStreetLootVehicle;
        public bool StreetLootAccepted;
        public int UpgradeMinutes;
        public int CompletionHour;
        public int CompletionMinute;
        public bool TimeShiftApplied;
    }

    private sealed class UpgradeProbeSnapshot
    {
        public bool HasUpgradeableSlot;
        public string CurrentSignature;
        public string MaxSignature;

        public bool IsMaxed
        {
            get
            {
                return !HasUpgradeableSlot ||
                       string.Equals(CurrentSignature, MaxSignature, StringComparison.Ordinal);
            }
        }
    }

    private bool TryBuildUpgradeProbeSnapshot(Vehicle veh, out UpgradeProbeSnapshot snapshot)
    {
        snapshot = null;

        try
        {
            if (veh == null || !veh.Exists())
                return false;

            try { Function.Call(Hash.SET_VEHICLE_MOD_KIT, veh.Handle, 0); } catch { }

            var current = new StringBuilder(256);
            var max = new StringBuilder(256);
            bool hasUpgradeableSlot = false;

            for (int modType = 0; modType <= 49; modType++)
            {
                if (modType == 18)
                    continue;

                int count = SafeCall(() => Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, veh.Handle, modType), 0);
                if (count <= 0)
                    continue;

                hasUpgradeableSlot = true;

                int curIndex = SafeCall(() => Function.Call<int>(Hash.GET_VEHICLE_MOD, veh.Handle, modType), -1);

                current.Append(modType).Append(':').Append(curIndex).Append('|');
                max.Append(modType).Append(':').Append(count - 1).Append('|');
            }

            int turboCount = SafeCall(() => Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, veh.Handle, 18), 0);
            if (turboCount > 0)
            {
                hasUpgradeableSlot = true;

                bool turboOn = SafeCall(() => Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, veh.Handle, 18), false);

                current.Append("18:").Append(turboOn ? 1 : 0).Append('|');
                max.Append("18:1|");
            }

            snapshot = new UpgradeProbeSnapshot
            {
                HasUpgradeableSlot = hasUpgradeableSlot,
                CurrentSignature = current.ToString(),
                MaxSignature = max.ToString()
            };

            return true;
        }
        catch
        {
            snapshot = null;
            return false;
        }
    }

    private bool IsVehicleAlreadyMaxUpgraded(Vehicle veh)
    {
        try
        {
            if (!TryBuildUpgradeProbeSnapshot(veh, out UpgradeProbeSnapshot snap))
                return false;

            return snap != null && snap.IsMaxed;
        }
        catch
        {
            return false;
        }
    }

    private UpgradeTarget _upgradeTarget = null;

    public Hao()
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
                EnsureHaoContactRegistered();
            }

            HandleDeferredUpgradeFlow();
            HandleHaoLocationPreview();

            if (_haoMenu != null && _haoMenu.Visible)
            {
                _uiPool.Process();
                UpdateLemonUiMouseState();
                Interval = 0;
            }
            else
            {
                UpdateLemonUiMouseState();
                Interval = (_flowState == FlowState.Idle && !_haoPreviewActive) ? 1000 : 0;
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
            if (_haoMenu == null)
                return;

            if (!_haoMenu.Visible)
                return;

            _haoMenu.MouseBehavior = MenuMouseBehavior.Disabled;
            _haoMenu.ResetCursorWhenOpened = false;
            _haoMenu.CloseOnInvalidClick = false;
            _haoMenu.RotateCamera = true;
        }
        catch
        {
        }
    }

    private void EnsureHaoContactRegistered()
    {
        try
        {
            if (_haoContactAdded)
                return;

            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            if (phone.Contacts.Any(c => string.Equals(c.Name, HaoContactName, StringComparison.OrdinalIgnoreCase)))
            {
                _haoContactAdded = true;
                return;
            }

            var contact = new iFruitContact(HaoContactName)
            {
                Active = true,
                DialTimeout = 2000,
                Bold = false,
                Icon = ContactIcon.Hao
            };

            contact.Answered += OnHaoContactAnswered;
            phone.Contacts.Add(contact);
            _haoContact = contact;
            _haoContactAdded = true;
        }
        catch (Exception ex)
        {
            Log("EnsureHaoContactRegistered failed: " + ex);
        }
    }

    private void OnHaoContactAnswered(iFruitContact sender)
    {
        try
        {
            HandleHaoRequest();
        }
        finally
        {
            try
            {
                Game.Player.Character?.Task.PutAwayMobilePhone();
            }
            catch
            {
            }

            try
            {
                CustomiFruit.GetCurrentInstance()?.Close(0);
            }
            catch
            {
            }
        }
    }

    private void HandleHaoRequest()
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return;

            if (!player.IsInVehicle())
            {
                GTA.UI.Screen.ShowSubtitle(HaoNeedVehicleFirstText, 2500);
                return;
            }

            Vehicle veh = player.CurrentVehicle;
            if (veh == null || !veh.Exists())
            {
                GTA.UI.Screen.ShowSubtitle(HaoNeedVehicleFirstText, 2500);
                return;
            }

            _lastUpgradeRejectMessage = null;

            if (!TryResolveUpgradeTarget(veh, out UpgradeTarget target))
            {
                if (!string.IsNullOrWhiteSpace(_lastUpgradeRejectMessage))
                {
                    ShowHaoMessage(HaoFailedTitle, _lastUpgradeRejectMessage);
                    _lastUpgradeRejectMessage = null;
                }
                else
                {
                    ShowHaoMessage(HaoFailedTitle, HaoUnknownVehicleText);
                }
                return;
            }

            _upgradeTarget = target;

            if (_upgradeTarget.IsFull)
            {
                ShowHaoMessage(HaoMenuTitle, HaoFullVehicleText);
                ClearTargetState();
                return;
            }

            if (IsVehicleAlreadyMaxUpgraded(veh))
            {
                ShowHaoMessage(HaoMenuTitle, HaoAlreadyMaxedText);
                ClearTargetState();
                return;
            }

            StartHaoLocationPreview();
            Interval = 0;
        }
        catch (Exception ex)
        {
            Log("HandleHaoRequest failed: " + ex);
        }
    }

    private void EnsureHaoMenuCreated()
    {
        try
        {
            if (_haoMenuReady)
                return;

            _haoMenu = new NativeMenu(HaoMenuTitle, HaoMenuSubtitle);

            _uiPool.Add(_haoMenu);
            _haoMenu.Visible = false;

            _haoMenuReady = true;
        }
        catch (Exception ex)
        {
            Log("EnsureHaoMenuCreated failed: " + ex);
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

    private int RollStreetLootComponentFee()
    {
        try
        {
            return _rng.Next(StreetLootMinComponentFee, StreetLootMaxComponentFee + 1);
        }
        catch
        {
            return 500000;
        }
    }

    private void BuildHaoMenu()
    {
        if (_haoMenu == null || _upgradeTarget == null)
            return;

        _haoMenu.Clear();

        _confirmItem = new NativeItem(HaoConfirmTitle);
        _cancelItem = new NativeItem(HaoCancelTitle);

        AddInfoItem(_haoMenu, string.Format(CultureInfo.InvariantCulture,
            "{0}: {1}", HaoCustomerNameLabel, _upgradeTarget.CustomerName));

        AddInfoItem(_haoMenu, string.Format(CultureInfo.InvariantCulture,
            "{0}: {1}", HaoVehicleNameLabel, _upgradeTarget.VehicleName));

        if (_upgradeTarget.IsPersistentOwnedVehicle)
        {
            AddInfoItem(_haoMenu, string.Format(CultureInfo.InvariantCulture,
                "{0}: ${1:N0}", HaoPurchasePriceLabel, _upgradeTarget.PurchasePrice));

            AddInfoItem(_haoMenu, string.Format(CultureInfo.InvariantCulture,
                "{0}: {1}", HaoStatusLabel, HaoPurchasedUpgradedStatus));
        }
        else
        {
            AddInfoItem(_haoMenu, string.Format(CultureInfo.InvariantCulture,
                "{0}: {1}", HaoPurchasePriceLabel, HaoPriceNaText));

            AddInfoItem(_haoMenu, string.Format(CultureInfo.InvariantCulture,
                "{0}: {1}", HaoStatusLabel, HaoUnknownStatus));
        }

        AddInfoItem(_haoMenu, string.Format(CultureInfo.InvariantCulture,
            "{0}: ${1:N0}", HaoComponentPriceLabel, _upgradeTarget.UpgradeFee));

        _confirmItem.Activated += (s, e) =>
        {
            try
            {
                if (!ValidateCurrentUpgradeTarget())
                    return;

                if (Game.Player.Money < _upgradeTarget.UpgradeFee)
                {
                    ShowHaoMessage(HaoFailedTitle, string.Format(CultureInfo.InvariantCulture,
                        HaoInsufficientMoneyText, string.Format(CultureInfo.InvariantCulture, "${0:N0}", _upgradeTarget.UpgradeFee)));
                    CloseHaoMenu(false);
                    return;
                }

                CloseHaoMenu(false); // đóng ngay
                Game.Player.Money -= _upgradeTarget.UpgradeFee;
                StartUpgradeSequence();
            }
            catch (Exception ex)
            {
                Log("Confirm upgrade failed: " + ex);
                CloseHaoMenu(false);
            }
        };

        _cancelItem.Activated += (s, e) =>
        {
            CloseHaoMenu(true);
            ClearHaoLocationPreview();
            ClearTargetState();
        };

        _haoMenu.Add(_confirmItem);
        _haoMenu.Add(_cancelItem);
    }

    private void AddInfoItem(NativeMenu menu, string text)
    {
        if (menu == null) return;

        var item = new NativeItem(text);
        item.Enabled = true;
        item.Activated += (s, e) => { };
        menu.Add(item);
    }

    private void CloseHaoMenu(bool setCooldown)
    {
        try
        {
            if (_haoMenu != null)
            {
                _haoMenu.Visible = false;
                _haoMenu.Clear();
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
        _upgradeTarget = null;
        _lastUpgradeRejectMessage = null;

        _targetVehicle = null;
        _targetRecord = null;
        _targetOwnerHash = 0;
        _targetPurchasePrice = 0;
        _targetUpgradeFee = 0;
        _targetIsFull = false;
        _targetCustomerName = string.Empty;
        _targetVehicleName = string.Empty;
        _targetPlate = string.Empty;
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
                GTA.UI.Screen.ShowSubtitle(HaoNeedVehicleFirstText, 2500);
                return false;
            }

            Vehicle current = player.CurrentVehicle;
            if (current == null || !current.Exists())
            {
                GTA.UI.Screen.ShowSubtitle(HaoNeedVehicleFirstText, 2500);
                return false;
            }

            if (_upgradeTarget == null || _upgradeTarget.Vehicle == null)
                return false;

            if (!ReferenceEquals(current, _upgradeTarget.Vehicle) && current.Handle != _upgradeTarget.Vehicle.Handle)
            {
                ShowHaoMessage(HaoFailedTitle, HaoUnknownVehicleText);
                return false;
            }

            if (_upgradeTarget.IsPersistentOwnedVehicle)
            {
                if (_upgradeTarget.Record == null)
                    return false;

                _upgradeTarget.Record.Plate = SafeGetPlate(current);
                _upgradeTarget.Plate = _upgradeTarget.Record.Plate;
                return true;
            }

            _upgradeTarget.Plate = SafeGetPlate(current);
            _upgradeTarget.VehicleName = PersistentManager.GetVehicleDisplayNamePublic((uint)current.Model.Hash) ?? _upgradeTarget.VehicleName;
            _upgradeTarget.CustomerName = GetCurrentCharacterDisplayName();

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
            CloseHaoMenu(false);
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

                bool ok = ApplyUpgradeToCurrentVehicle();
                if (!ok)
                {
                    GTA.UI.Screen.FadeIn(FadeOutMs);
                    _flowState = FlowState.Idle;
                    ShowHaoMessage(HaoFailedTitle, HaoUpgradeFailedText);
                    return;
                }

                ClearHaoLocationPreview();
                ClearTargetState();

                // Lúc màn hình còn đang fade out, đồng hồ game đã được đẩy tới thời điểm hoàn tất.
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

    private void StartHaoLocationPreview()
    {
        try
        {
            ClearHaoLocationPreview();

            _haoPreviewActive = true;
            _haoPreviewBlipsExpireAt = Game.GameTime + HaoPreviewBlipLifetimeMs;

            foreach (var pos in HaoPreviewLocations)
            {
                try
                {
                    var blip = World.CreateBlip(pos);
                    if (blip == null || !blip.Exists())
                        continue;

                    blip.Sprite = BlipSprite.Standard;
                    blip.Color = BlipColor.Yellow;
                    blip.Scale = 0.9f;
                    blip.IsShortRange = false;
                    blip.Alpha = 255;

                    _haoPreviewBlips.Add(blip);
                }
                catch
                {
                }
            }
        }
        catch (Exception ex)
        {
            Log("StartHaoLocationPreview failed: " + ex);
        }
    }

    private void HandleHaoLocationPreview()
    {
        try
        {
            if (!_haoPreviewActive)
                return;

            if (_flowState != FlowState.Idle)
                return;

            int now = Game.GameTime;
            if (_haoPreviewBlipsExpireAt > 0 && now >= _haoPreviewBlipsExpireAt)
            {
                ClearHaoPreviewBlips();
            }

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return;

            Vector3 checkPos;
            bool inVehicle = player.IsInVehicle() && player.CurrentVehicle != null && player.CurrentVehicle.Exists();

            if (inVehicle)
                checkPos = player.CurrentVehicle.Position;
            else
                checkPos = player.Position;

            foreach (var pos in HaoPreviewLocations)
            {
                try
                {
                    World.DrawMarker(
                        MarkerType.VerticalCylinder,
                        pos + new Vector3(0.0f, 0.0f, -1.42f),
                        Vector3.Zero,
                        Vector3.Zero,
                        new Vector3(0.85f, 0.85f, 0.85f),
                        Color.FromArgb(180, 0, 120, 255));

                    if (inVehicle && checkPos.DistanceTo(pos) <= HaoPreviewInteractRadius)
                    {
                        TryOpenHaoMenuFromPreview();
                        break;
                    }
                }
                catch
                {
                }
            }
        }
        catch (Exception ex)
        {
            Log("HandleHaoLocationPreview failed: " + ex);
        }
    }

    private void TryOpenHaoMenuFromPreview()
    {
        try
        {
            if (_haoMenu != null && _haoMenu.Visible)
                return;

            if (_upgradeTarget == null)
                return;

            if (!ValidateCurrentUpgradeTarget())
                return;

            EnsureHaoMenuCreated();
            BuildHaoMenu();
            ConfigureKeyboardOnlyMenu(_haoMenu);

            if (_haoMenu != null)
            {
                _haoMenu.Visible = true;
                Interval = 0;
            }
        }
        catch (Exception ex)
        {
            Log("TryOpenHaoMenuFromPreview failed: " + ex);
        }
    }

    private void ClearHaoLocationPreview()
    {
        try
        {
            ClearHaoPreviewBlips();
            _haoPreviewActive = false;
            _haoPreviewBlipsExpireAt = 0;
        }
        catch
        {
        }
    }

    private void ClearHaoPreviewBlips()
    {
        try
        {
            for (int i = 0; i < _haoPreviewBlips.Count; i++)
            {
                try
                {
                    var blip = _haoPreviewBlips[i];
                    if (blip != null && blip.Exists())
                        blip.Delete();
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
        finally
        {
            _haoPreviewBlips.Clear();
        }
    }

    private bool ApplyUpgradeToCurrentVehicle()
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || !player.IsInVehicle())
                return false;

            Vehicle veh = player.CurrentVehicle;
            if (veh == null || !veh.Exists())
                return false;

            if (_upgradeTarget == null)
                return false;

            if (!ValidateCurrentUpgradeTarget())
                return false;

            if (!_upgradeTarget.IsPersistentOwnedVehicle)
            {
                if (!_upgradeTarget.StreetLootAccepted)
                    return false;
            }

            ApplyFullUpgradeToVehicle(veh);

            // Nhảy thời gian trong game theo đúng nhánh xe.
            // Xe chính chủ: 60–75 phút.
            // Xe lụm vặt:   35–50 phút.
            ApplyHaoUpgradeTimeShift();

            if (_upgradeTarget.IsPersistentOwnedVehicle && _upgradeTarget.Record != null)
            {
                try
                {
                    PersistentManager.UpdatePersistentFromVehicle(veh);
                }
                catch
                {
                }

                try
                {
                    PersistentManager.UpdateHaoUpgradeState(
                        veh,
                        _upgradeTarget.Record.HaoUpgradeBranch,
                        true);
                }
                catch
                {
                }

                try
                {
                    RewritePersistentVehicleFileAfterUpgrade(veh, _upgradeTarget.Record, true);
                }
                catch (Exception ex)
                {
                    Log("RewritePersistentVehicleFileAfterUpgrade failed: " + ex);
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Log("ApplyUpgradeToCurrentVehicle failed: " + ex);
            return false;
        }
    }

    private bool TryResolveUpgradeTarget(Vehicle veh, out UpgradeTarget target)
    {
        target = null;
        _lastUpgradeRejectMessage = null;

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

                bool branchIsFull = best.HaoUpgradeBranch.HasValue && best.HaoUpgradeBranch.Value == 1;
                bool alreadyUpgraded = best.HaoUpgradeApplied;

                target = new UpgradeTarget
                {
                    Vehicle = veh,
                    Record = best,
                    IsFull = branchIsFull || alreadyUpgraded,
                    IsPersistentOwnedVehicle = true,
                    PurchasePrice = Math.Max(0, best.PurchasePrice),
                    UpgradeFee = ComputeUpgradeFee(Math.Max(0, best.PurchasePrice)),
                    CustomerName = GetCurrentCharacterDisplayName(),
                    VehicleName = PersistentManager.GetVehicleDisplayNamePublic(best.ModelHash) ?? HaoDefaultVehicleName,
                    Plate = SafeGetPlate(veh),
                    IsStreetLootVehicle = false,
                    StreetLootAccepted = false
                };

                return true;
            }

            if (IsVehicleAlreadyMaxUpgraded(veh))
            {
                _lastUpgradeRejectMessage = HaoAlreadyMaxedText;
                return false;
            }

            int roll = _rng.Next(100);
            bool accepted = roll >= StreetLootRejectChancePercent;

            if (!accepted)
            {
                _lastUpgradeRejectMessage = HaoComponentRejectText;
                return false;
            }

            target = new UpgradeTarget
            {
                Vehicle = veh,
                Record = null,
                IsFull = false,
                IsPersistentOwnedVehicle = false,
                IsStreetLootVehicle = true,
                StreetLootAccepted = true,
                PurchasePrice = 0,
                UpgradeFee = RollStreetLootComponentFee(),
                CustomerName = GetCurrentCharacterDisplayName(),
                VehicleName = PersistentManager.GetVehicleDisplayNamePublic(modelHash) ?? HaoStreetLootVehicleName,
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

    private void RewritePersistentVehicleFileAfterUpgrade(Vehicle veh, OwnedVehicleRecord oldRecord, bool haoApplied)
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

        string newLine = SerializeCurrentVehicleAsPersistentLine(veh, oldRecord, haoApplied);
        kept.Add(newLine);

        WriteAllLinesAtomic(PersistentVehiclesFile, kept.ToArray());
    }

    private string SerializeCurrentVehicleAsPersistentLine(Vehicle veh, OwnedVehicleRecord oldRecord, bool haoApplied)
    {
        uint modelHash = (uint)veh.Model.Hash;
        string modelString = PersistentManager.GetVehicleDisplayNamePublic(modelHash) ?? string.Format(CultureInfo.InvariantCulture, "0x{0:X}", modelHash);
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

        string haoBranchStr = oldRecord != null && oldRecord.HaoUpgradeBranch.HasValue
            ? oldRecord.HaoUpgradeBranch.Value.ToString(CultureInfo.InvariantCulture)
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

    private void ApplyFullUpgradeToVehicle(Vehicle veh)
    {
        if (veh == null || !veh.Exists())
            return;

        try
        {
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, veh.Handle, 0);
        }
        catch
        {
        }

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
                    int bestIndex = count - 1;
                    SafeCall(() =>
                    {
                        Function.Call(Hash.SET_VEHICLE_MOD, veh.Handle, modType, bestIndex, false);
                        return 0;
                    }, 0);
                }
            }
            catch
            {
            }
        }

        try
        {
            SafeCall(() =>
            {
                Function.Call(Hash.TOGGLE_VEHICLE_MOD, veh.Handle, 18, true);
                return 0;
            }, 0);
        }
        catch
        {
        }

        try
        {
            int liveryCount = SafeCall(() => Function.Call<int>(Hash.GET_VEHICLE_LIVERY_COUNT, veh.Handle), 0);
            if (liveryCount > 0)
                SafeCall(() =>
                {
                    Function.Call(Hash.SET_VEHICLE_LIVERY, veh.Handle, liveryCount - 1);
                    return 0;
                }, 0);
        }
        catch
        {
        }

        try
        {
            for (int ex = 1; ex <= 20; ex++)
            {
                try
                {
                    if (Function.Call<bool>(Hash.DOES_EXTRA_EXIST, veh.Handle, ex))
                        Function.Call(Hash.SET_VEHICLE_EXTRA, veh.Handle, ex, 0);
                }
                catch
                {
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
            veh.Repair();
            veh.DirtLevel = 0f;
            veh.IsEngineRunning = true;
        }
        catch
        {
        }
    }

    private void ApplyHaoUpgradeTimeShift()
    {
        try
        {
            if (_upgradeTarget == null || _upgradeTarget.TimeShiftApplied)
                return;

            int minMinutes;
            int maxMinutes;

            if (_upgradeTarget.IsPersistentOwnedVehicle)
            {
                minMinutes = 60;
                maxMinutes = 75;
            }
            else if (_upgradeTarget.IsStreetLootVehicle)
            {
                minMinutes = 35;
                maxMinutes = 50;
            }
            else
            {
                return;
            }

            int addMinutes = _rng.Next(minMinutes, maxMinutes + 1);

            int curHour = SafeCall(() => Function.Call<int>(Hash.GET_CLOCK_HOURS), 0);
            int curMinute = SafeCall(() => Function.Call<int>(Hash.GET_CLOCK_MINUTES), 0);

            int totalMinutes = (curHour * 60) + curMinute + addMinutes;
            int newHour = (totalMinutes / 60) % 24;
            int newMinute = totalMinutes % 60;

            _upgradeTarget.UpgradeMinutes = addMinutes;
            _upgradeTarget.CompletionHour = newHour;
            _upgradeTarget.CompletionMinute = newMinute;
            _upgradeTarget.TimeShiftApplied = true;

            try
            {
                Function.Call(Hash.SET_CLOCK_TIME, newHour, newMinute, 0);
            }
            catch
            {
            }
        }
        catch
        {
        }
    }

    private static string SerializeMods(Vehicle veh)
    {
        try
        {
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, veh.Handle, 0);
        }
        catch
        {
        }

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

        return HaoDefaultCustomerName;
    }

    private static string SafeGetPlate(Vehicle v)
    {
        try
        {
            if (v == null || !v.Exists())
                return string.Empty;

            try
            {
                return v.Mods.LicensePlate ?? string.Empty;
            }
            catch
            {
            }

            try
            {
                return Function.Call<string>(Hash.GET_VEHICLE_NUMBER_PLATE_TEXT, v.Handle) ?? string.Empty;
            }
            catch
            {
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static int ComputeUpgradeFee(int purchasePrice)
    {
        try
        {
            if (purchasePrice <= 0)
                return 100000;

            decimal fee = Math.Round((decimal)purchasePrice * UpgradeFeePercent / 100m, MidpointRounding.AwayFromZero);
            if (fee < 0) fee = 0;
            return (int)Math.Min(int.MaxValue, fee);
        }
        catch
        {
            return 100000;
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

            string raw = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? s.Substring(2).Trim()
                : s;

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

    private static void ShowHaoMessage(string subject, string body)
    {
        try
        {
            PlayFrontendSound(HaoMessageSoundName, HaoMessageSoundSet);
            Notification.Show(NotificationIcon.Hao, HaoContactName, subject, body);
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
            catch
            {
            }
        }
    }

    private void EnsureHelpBoxCooldownSet()
    {
        try
        {
            _lastMenuUpdateTime = Game.GameTime + 5000;
        }
        catch
        {
        }
    }

    private static void Log(string msg)
    {
        try
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GTA V Mods", "VehicleUpgrade");
            Directory.CreateDirectory(root);
            File.AppendAllText(Path.Combine(root, "vehicle_upgrade_log.txt"), DateTime.Now.ToString("s") + "  " + msg + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static T SafeCall<T>(Func<T> fn, T fallback)
    {
        try
        {
            return fn == null ? fallback : fn();
        }
        catch
        {
            return fallback;
        }
    }
}