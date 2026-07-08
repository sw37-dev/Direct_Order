using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using iFruitAddon2;
using LemonUI;
using LemonUI.Menus;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

public class DevinWeston : Script
{
    private static string L(string key, string fallback)
    {
        return Language.Get(key, fallback);
    }

    private static string DevinContactName => L("Devin_ContactName", "Devin Weston");
    private static string DevinMenuTitle => L("Devin_MenuTitle", "Sell Supercars");
    private static string DevinMenuSubtitle => L("Devin_MenuSubtitle", "THÔNG TIN THANH LÝ SIÊU XE");
    private static string DevinNoItemsText => L("Devin_NoItems", "Không có dữ liệu.");
    private static string DevinNoDataText => L("Devin_NoData", "Không có dữ liệu");
    private static string DevinNoDataDescText => L("Devin_NoDataDesc", "Chiếc xe mục tiêu không còn tồn tại.");
    private static string DevinSellPromptText => L("Devin_SellPrompt", "Mày đang muốn bán chiếc xe nào cho tao vậy????");
    private static string DevinRejectTrashText => L("Devin_RejectTrash", "Con xe này cùi quá, tao không chấp nhận con này!!!!");
    private static string DevinAcceptHintText => L("Devin_AcceptHint", "Con này có vẻ được đấy!! Mang đến cho tao xem nàooooo???");
    private static string DevinMenuLineVehicleName => L("Devin_MenuLineVehicleName", "1. Tên phương tiện: {0}");
    private static string DevinMenuLineVehiclePrice => L("Devin_MenuLineVehiclePrice", "Giá bán dự kiến: {0}");
    private static string DevinMenuLineVehicleClass => L("Devin_MenuLineVehicleClass", "2. Loại phương tiện: {0}");
    private static string DevinMenuLineVehicleClassDesc => L("Devin_MenuLineVehicleClassDesc", "Devin chỉ quan tâm các chiếc Siêu Xe.");
    private static string DevinMenuLinePlate => L("Devin_MenuLinePlate", "3. Biển số xe: {0}");
    private static string DevinMenuLinePlateDesc => L("Devin_MenuLinePlateDesc", "Biển số hiện tại của chiếc xe đang được xem xét.");
    private static string DevinMenuLineOwner => L("Devin_MenuLineOwner", "4. Chủ sở hữu: {0}");
    private static string DevinMenuLineOwnerDesc => L("Devin_MenuLineOwnerDesc", "Tên chủ xe theo nhân vật hiện tại.");
    private static string DevinMenuLineBuyback => L("Devin_MenuLineBuyback", "5. Giá Devin mua lại: {0}");
    private static string DevinMenuLineBuybackDesc => L("Devin_MenuLineBuybackDesc", "Hãy xác nhận để hoàn tất giao dịch.");
    private static string DevinConfirmSell => L("Devin_ConfirmSell", "Xác nhận bán phương tiện cho Devin");
    private static string DevinConfirmSellDesc => L("Devin_ConfirmSellDesc", "Nhấn Enter để bán chiếc Siêu Xe này.");
    private static string DevinBack => L("Devin_Back", "Quay lại");
    private static string DevinBackDesc => L("Devin_BackDesc", "Đóng giao dịch.");
    private static string DevinSaleCompletedTitle => L("Devin_SaleCompletedTitle", "Thanh lý hoàn tất");
    private static string DevinSaleCompletedBody => L("Devin_SaleCompletedBody", "Đã thanh lý ~y~{0}~s~ và nhận lại ~g~+{1}~s~{2}");
    private static string DevinSaleCompletedSuffixRemoved => L("Devin_SaleCompletedSuffixRemoved", " (đã xóa dữ liệu xe). ");
    private static string DevinSaleCompletedSuffixPlain => L("Devin_SaleCompletedSuffixPlain", ".");
    private static string DevinNoVehicleSelectedText => L("Devin_NoVehicleSelected", "Mày đang muốn bán chiếc xe nào cho tao vậy????");
    private static string DevinShowNotInVehicleText => L("Devin_ShowNotInVehicle", "Mày đang muốn bán chiếc xe nào cho tao vậy????");
    private static string DevinShowTooWeakText => L("Devin_ShowTooWeak", "Con xe này cùi quá, tao không chấp nhận con này!!!!");
    private static string DevinShowInterestingText => L("Devin_ShowInteresting", "Con này có vẻ được đấy!! Mang đến cho tao xem nàooooo???");
    private static string DevinTargetVehicleMissingText => L("Devin_TargetVehicleMissing", "Mày đang muốn bán chiếc xe nào cho tao vậy????");
    private static string DevinStatsTopSpeed => L("Devin_StatTopSpeed", "Tốc độ cao nhất");
    private static string DevinStatsAcceleration => L("Devin_StatAcceleration", "Gia tốc");
    private static string DevinStatsBraking => L("Devin_StatBraking", "Phanh");
    private static string DevinStatsTraction => L("Devin_StatTraction", "Độ bám đường");
    private static string DevinUnknownVehicleText => L("Devin_UnknownVehicle", "Phương tiện");
    private static string DevinSuperClassText => L("Devin_SuperClass", "Super");
    private static string DevinCharaUnknownText => L("Devin_CharacterUnknown", "Khách hàng");
    private static string DevinNoPlateText => L("Devin_NoPlate", "Chưa có");
    private static string DevinCurrentPlateDesc => L("Devin_CurrentPlateDesc", "Biển số hiện tại của chiếc xe đang được xem xét.");
    private static string DevinCurrentOwnerDesc => L("Devin_CurrentOwnerDesc", "Tên chủ xe theo nhân vật hiện tại.");
    private static string DevinSellHintText => L("Devin_SellHint", "Nhấn Enter để bán chiếc Siêu Xe này.");
    private static string DevinCloseCleanupFailureText => L("Devin_CloseCleanupFailure", "Không thể xử lý yêu cầu.");
    private static string DevinFeedErrorText => L("Devin_FeedError", "Không thể thông báo giao dịch.");

    private const int GameReadyDelayMs = 2500;
    private const int CallDelayMs = 1000;
    private const int CleanupDelayMs = 25000;

    private const float DevinHeading = 172.3377f;
    private static readonly Vector3 DevinPosition = new Vector3(-2551.277f, 1910.798f, 169.0171f);
    private const float DevinInteractRadius = 4.0f;

    private const int SpawnWarmupMs = 800;

    private const int MinSalePrice = 300000;
    private const int MaxSalePrice = 600000;

    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "PersistentManager");

    private static readonly string PersistentVehiclesFile = Path.Combine(DataFolder, "persistent_vehicles.txt");

    private readonly Random _rng = new Random();
    private readonly ObjectPool _uiPool = new ObjectPool();
    private readonly Queue<Action> _uiActions = new Queue<Action>();

    private NativeMenu _menu;
    private bool _menuReady = false;

    private NativeStatsPanel _statsPanel;
    private NativeStatsInfo _statTopSpeed;
    private NativeStatsInfo _statAcceleration;
    private NativeStatsInfo _statBraking;
    private NativeStatsInfo _statTraction;

    private bool _contactAdded = false;

    private Ped _devinNpc = null;
    private Blip _devinBlip = null;

    private bool _callPending = false;
    private int _callDueTime = 0;
    private int _spawnRequestedAt = -1;

    private bool _spawnSuppressed = false;
    private bool _cleanupArmed = false;
    private int _cleanupDueTime = -1;
    private bool _menuAutoOpened = false;

    private Vehicle _targetVehicle = null;
    private object _targetPersistentRecord = null;
    private bool _targetIsPersistent = false;
    private int _targetSalePrice = 0;
    private int _targetOwnerHash = 0;
    private string _targetVehicleName = "";
    private string _targetPlate = "";

    private int _gameReadySince = -1;
    private bool _ready = false;

    private bool _transactionClosed = true;

    private readonly HashSet<NativeItem> _lockedBadgeItems = new HashSet<NativeItem>();

    private bool _menuVisible => _menu != null && _menu.Visible;

    public DevinWeston()
    {
        Tick += OnTick;
        KeyDown += OnKeyDown;
        Aborted += OnAborted;
        Interval = 1000;

        try { _gameReadySince = Game.GameTime; }
        catch { _gameReadySince = 0; }
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

            if (!_ready)
            {
                if (_gameReadySince < 0)
                    _gameReadySince = Game.GameTime;

                if (Game.GameTime - _gameReadySince < GameReadyDelayMs)
                    return;

                _ready = true;
                EnsureDevinContactRegistered();
            }

            ProcessPendingCleanup();
            EnsureDevinNpcSpawned();
            ProcessPendingCall();

            if (_menuVisible && !ValidateTargetVehicleStillValid())
            {
                CloseAllMenus(true);
            }

            if (!_menuVisible)
            {
                TryOpenDevinMenuFromProximity();
            }

            if (_menuVisible)
            {
                _uiPool.Process();
                FlushUiActions();
                UpdateMenuMouseState();
                Interval = 0;
            }
            else
            {
                FlushUiActions();
                UpdateMenuMouseState();

                if (_callPending)
                    Interval = 100;
                else
                    Interval = 1000;
            }
        }
        catch
        {
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            if (_menuVisible && (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Back))
            {
                CloseAllMenus(true);
            }
        }
        catch
        {
        }
    }

    private void OnAborted(object sender, EventArgs e)
    {
        try
        {
            CloseAllMenus(false);
            RemoveDevinBlip();
            RemoveDevinNpc();
        }
        catch
        {
        }
    }

    private void RequestDevinCollision()
    {
        try
        {
            Function.Call(Hash.REQUEST_COLLISION_AT_COORD, DevinPosition.X, DevinPosition.Y, DevinPosition.Z);
            Function.Call(Hash.REQUEST_ADDITIONAL_COLLISION_AT_COORD, DevinPosition.X, DevinPosition.Y, DevinPosition.Z);
        }
        catch
        {
        }
    }

    private bool IsPlayerNearDevinNpc()
    {
        try
        {
            Ped p = Game.Player.Character;
            if (p == null || !p.Exists() || p.IsDead)
                return false;

            if (_devinNpc != null && _devinNpc.Exists())
                return p.Position.DistanceTo(_devinNpc.Position) <= DevinInteractRadius;

            return p.Position.DistanceTo(DevinPosition) <= DevinInteractRadius;
        }
        catch
        {
            return false;
        }
    }

    private bool TryOpenDevinMenuFromProximity()
    {
        try
        {
            if (_menuVisible)
                return true;

            if (_transactionClosed)
                return false;

            if (!IsPlayerNearDevinNpc())
                return false;

            if (!ValidateTargetVehicleStillValid())
                return false;

            OpenDevinMenu();
            return _menuVisible;
        }
        catch
        {
            return false;
        }
    }

    private void EnsureDevinContactRegistered()
    {
        try
        {
            if (_contactAdded)
                return;

            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            if (phone.Contacts.Any(c => string.Equals(c.Name, DevinContactName, StringComparison.OrdinalIgnoreCase)))
            {
                _contactAdded = true;
                return;
            }

            var contact = new iFruitContact(DevinContactName)
            {
                Active = true,
                DialTimeout = 2000,
                Bold = false,
                Icon = ContactIcon.Devin
            };

            contact.Answered += OnDevinContactAnswered;
            phone.Contacts.Add(contact);
            _contactAdded = true;
        }
        catch
        {
        }
    }

    private void OnDevinContactAnswered(iFruitContact sender)
    {
        try
        {
            HandleDevinRequest();
        }
        finally
        {
            try { Game.Player.Character?.Task.PutAwayMobilePhone(); } catch { }
            try { CustomiFruit.GetCurrentInstance()?.Close(0); } catch { }
        }
    }

    private void HandleDevinRequest()
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead)
                return;

            if (!player.IsInVehicle())
            {
                ShowDevinMessage(DevinContactName, DevinShowNotInVehicleText);
                return;
            }

            Vehicle veh = player.CurrentVehicle;
            if (veh == null || !veh.Exists())
            {
                ShowDevinMessage(DevinContactName, DevinShowNotInVehicleText);
                return;
            }

            if (!IsSuperVehicle(veh))
            {
                ShowDevinMessage(DevinContactName, DevinRejectTrashText);
                return;
            }

            CaptureTargetVehicleFromCurrentVehicle(veh);
            BeginDevinTransaction();

            _callPending = true;
            _callDueTime = Game.GameTime + CallDelayMs;
            _spawnRequestedAt = Game.GameTime;
            _spawnSuppressed = false;
            _menuAutoOpened = false;
            CancelCleanup();

            RequestDevinCollision();

            ShowDevinMessage(DevinContactName, DevinAcceptHintText);
            EnsureDevinNpcSpawned();
            EnsureDevinBlip();
        }
        catch
        {
        }
    }

    private void ProcessPendingCall()
    {
        try
        {
            if (!_callPending)
                return;

            EnsureDevinNpcSpawned();
            EnsureDevinBlip();

            if (Game.GameTime < _callDueTime)
                return;

            if (_menuVisible)
                return;

            if (IsPlayerNearDevinNpc() && ValidateTargetVehicleStillValid())
            {
                OpenDevinMenu();
                _callPending = false;
                _menuAutoOpened = true;
                RemoveDevinBlip();
            }
        }
        catch
        {
        }
    }

    private bool IsSuperVehicle(Vehicle veh)
    {
        try
        {
            if (veh == null || !veh.Exists())
                return false;

            int cls = Function.Call<int>(Hash.GET_VEHICLE_CLASS_FROM_NAME, veh.Model.Hash);
            return cls == 7;
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
            int modelHash = GetCurrentPlayerModelHash();
            switch (modelHash)
            {
                case -1692214353: return "Franklin Clinton";
                case 225514697: return "Michael De Santa";
                case -1686040670: return "Trevor Philips";
            }
        }
        catch
        {
        }

        return DevinCharaUnknownText;
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

    private void BeginDevinTransaction()
    {
        _transactionClosed = false;
        _spawnSuppressed = false;
    }

    private void ClearTargetSelection()
    {
        _targetVehicle = null;
        _targetPersistentRecord = null;
        _targetIsPersistent = false;
        _targetSalePrice = 0;
        _targetOwnerHash = 0;
        _targetVehicleName = "";
        _targetPlate = "";
    }

    private void EndDevinTransactionState(bool scheduleCleanup)
    {
        try
        {
            if (_menu != null)
                _menu.Visible = false;
        }
        catch { }

        _callPending = false;
        _callDueTime = 0;
        _menuAutoOpened = false;
        _spawnRequestedAt = -1;
        _spawnSuppressed = true;
        _transactionClosed = true;

        ClearTargetSelection();

        if (scheduleCleanup)
            ScheduleCleanup();
        else
            CancelCleanup();

        Interval = 1000;
    }

    private void LockAndDisableSoldVehicle(Vehicle veh)
    {
        try
        {
            if (veh == null || !veh.Exists())
                return;

            try
            {
                Ped player = Game.Player.Character;
                if (player != null && player.Exists() &&
                    player.CurrentVehicle != null &&
                    player.CurrentVehicle.Exists() &&
                    player.CurrentVehicle.Handle == veh.Handle)
                {
                    try { Function.Call(Hash.TASK_LEAVE_VEHICLE, player.Handle, veh.Handle, 0); } catch { }
                }
            }
            catch { }

            try { Function.Call(Hash.SET_VEHICLE_ENGINE_ON, veh.Handle, false, true, true); } catch { }
            try { Function.Call(Hash.SET_VEHICLE_UNDRIVEABLE, veh.Handle, true); } catch { }
            try { Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED, veh.Handle, 2); } catch { }
            try { Function.Call(Hash.FREEZE_ENTITY_POSITION, veh.Handle, true); } catch { }
            try { veh.LockStatus = VehicleLockStatus.Locked; } catch { }
        }
        catch { }
    }

    private bool ValidateTargetVehicleStillValid()
    {
        try
        {
            if (_targetVehicle == null || !_targetVehicle.Exists())
                return false;

            return IsSuperVehicle(_targetVehicle);
        }
        catch
        {
            return false;
        }
    }

    private void CaptureTargetVehicleFromCurrentVehicle(Vehicle veh)
    {
        try
        {
            _targetVehicle = veh;
            _targetOwnerHash = GetCurrentPlayerModelHash();
            _targetVehicleName = PersistentManager.GetVehicleDisplayNamePublic((uint)veh.Model.Hash) ?? DevinUnknownVehicleText;
            _targetPlate = SafeGetPlate(veh);
            _targetSalePrice = _rng.Next(MinSalePrice, MaxSalePrice + 1);
            _targetIsPersistent = TryResolvePersistentRecordForCurrentVehicle(veh, out _targetPersistentRecord);
        }
        catch
        {
        }
    }

    private void EnsureDevinNpcSpawned()
    {
        try
        {
            if ((_spawnSuppressed && !_callPending && !_menuVisible) || (!_callPending && !_menuVisible))
                return;

            RequestDevinCollision();

            if (_devinNpc != null && _devinNpc.Exists())
                return;

            if (_spawnRequestedAt < 0)
            {
                _spawnRequestedAt = Game.GameTime;
                RequestDevinCollision();
                return;
            }

            if (Game.GameTime - _spawnRequestedAt < SpawnWarmupMs)
            {
                RequestDevinCollision();
                return;
            }

            Model model = new Model("ig_devin");
            if (!model.IsLoaded)
                model.Request(250);

            if (!model.IsLoaded)
                return;

            Ped ped = World.CreatePed(model, DevinPosition, DevinHeading);
            if (ped == null || !ped.Exists())
                return;

            try { ped.IsPersistent = true; } catch { }
            try { ped.BlockPermanentEvents = true; } catch { }
            try { ped.IsInvincible = true; } catch { }
            try { ped.CanRagdoll = false; } catch { }
            try { Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, ped.Handle, true, true); } catch { }
            try { Function.Call(Hash.SET_ENTITY_HEADING, ped.Handle, DevinHeading); } catch { }
            try { Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET, ped.Handle, DevinPosition.X, DevinPosition.Y, DevinPosition.Z, false, false, false); } catch { }
            try { Function.Call(Hash.FREEZE_ENTITY_POSITION, ped.Handle, true); } catch { }
            try { ped.Task.ClearAllImmediately(); } catch { }
            try { ped.Task.StandStill(-1); } catch { }

            _devinNpc = ped;
            _spawnRequestedAt = -1;

            RemoveDevinBlip();
            EnsureDevinBlip();
        }
        catch
        {
        }
    }

    private void EnsureDevinBlip()
    {
        try
        {
            if (!_callPending && (_devinNpc == null || !_devinNpc.Exists()) && !IsAnyMenuVisible())
                return;

            if (_devinNpc != null && _devinNpc.Exists())
            {
                if (_devinBlip != null && _devinBlip.Exists())
                {
                    try { _devinBlip.Position = _devinNpc.Position; } catch { }
                    try { _devinBlip.Color = BlipColor.Yellow; } catch { }
                    try { _devinBlip.Name = DevinContactName; } catch { }
                    return;
                }

                _devinBlip = _devinNpc.AddBlip();
                if (_devinBlip != null && _devinBlip.Exists())
                {
                    _devinBlip.IsShortRange = false;
                    _devinBlip.Sprite = BlipSprite.Standard;
                    _devinBlip.Color = BlipColor.Yellow;
                    _devinBlip.Name = DevinContactName;
                }

                return;
            }

            if (_devinBlip != null && _devinBlip.Exists())
            {
                try { _devinBlip.Position = DevinPosition; } catch { }
                try { _devinBlip.Color = BlipColor.Yellow; } catch { }
                try { _devinBlip.Name = DevinContactName; } catch { }
                return;
            }

            _devinBlip = World.CreateBlip(DevinPosition);
            if (_devinBlip != null && _devinBlip.Exists())
            {
                _devinBlip.IsShortRange = false;
                _devinBlip.Sprite = BlipSprite.Standard;
                _devinBlip.Color = BlipColor.Yellow;
                _devinBlip.Name = DevinContactName;
            }
        }
        catch
        {
        }
    }

    private void RemoveDevinBlip()
    {
        try
        {
            if (_devinBlip != null)
            {
                try
                {
                    if (_devinBlip.Exists())
                        _devinBlip.Delete();
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
            _devinBlip = null;
        }
    }

    private void RemoveDevinNpc()
    {
        try
        {
            if (_devinNpc != null && _devinNpc.Exists())
            {
                try { _devinNpc.MarkAsNoLongerNeeded(); } catch { }
                try { _devinNpc.Delete(); } catch { }
            }
        }
        catch
        {
        }
        finally
        {
            _devinNpc = null;
            _spawnRequestedAt = -1;
        }
    }

    private void OpenDevinMenu()
    {
        try
        {
            CancelCleanup();

            if (_targetVehicle == null || !_targetVehicle.Exists())
            {
                ShowDevinMessage(DevinContactName, DevinTargetVehicleMissingText);
                CloseAllMenus(true);
                return;
            }

            if (!ValidateTargetVehicleStillValid())
            {
                ShowDevinMessage(DevinContactName, DevinTargetVehicleMissingText);
                CloseAllMenus(true);
                return;
            }

            EnsureMenuCreated();
            BuildMenu();

            if (_menu != null)
            {
                ConfigureKeyboardOnlyMenu(_menu);
                _menu.Visible = true;
                Interval = 0;
            }
        }
        catch
        {
        }
    }

    private void EnsureMenuCreated()
    {
        try
        {
            if (_menuReady)
                return;

            _menu = new NativeMenu(
                DevinMenuTitle,
                DevinMenuSubtitle
            );

            _menu.KeepNameCasing = true;
            _menu.MaxItems = 8;
            _menu.NoItemsText = DevinNoItemsText;
            _uiPool.Add(_menu);
            _menu.Visible = false;
            _menuReady = true;
        }
        catch
        {
        }
    }

    private void ConfigureKeyboardOnlyMenu(NativeMenu menu)
    {
        if (menu == null)
            return;

        try
        {
            menu.MouseBehavior = MenuMouseBehavior.Disabled;
            menu.ResetCursorWhenOpened = false;
            menu.CloseOnInvalidClick = false;
            menu.RotateCamera = true;
        }
        catch
        {
        }
    }

    private void EnsureStatsPanel()
    {
        if (_statsPanel != null)
            return;

        _statTopSpeed = new NativeStatsInfo(DevinStatsTopSpeed, 0);
        _statAcceleration = new NativeStatsInfo(DevinStatsAcceleration, 0);
        _statBraking = new NativeStatsInfo(DevinStatsBraking, 0);
        _statTraction = new NativeStatsInfo(DevinStatsTraction, 0);

        _statsPanel = new NativeStatsPanel(_statTopSpeed, _statAcceleration, _statBraking, _statTraction)
        {
            BackgroundColor = Color.FromArgb(180, 28, 28, 28),
            ForegroundColor = Color.FromArgb(255, 255, 255, 255),
            Visible = true
        };
    }

    private static float ClampStat(float value)
    {
        if (value < 0f) return 0f;
        if (value > 100f) return 100f;
        return value;
    }

    private static float NormalizeStat(float raw, float cap)
    {
        if (cap <= 0f) return 0f;
        return ClampStat((raw / cap) * 100f);
    }

    private void UpdateStatsPanel(uint modelHash)
    {
        try
        {
            EnsureStatsPanel();

            float topSpeed = SafeCall(() => Function.Call<float>(Hash.GET_VEHICLE_MODEL_ESTIMATED_MAX_SPEED, modelHash), 0f);
            float acceleration = SafeCall(() => Function.Call<float>(Hash.GET_VEHICLE_MODEL_ACCELERATION, modelHash), 0f);
            float braking = SafeCall(() => Function.Call<float>(Hash.GET_VEHICLE_MODEL_MAX_BRAKING, modelHash), 0f);
            float traction = SafeCall(() => Function.Call<float>(Hash.GET_VEHICLE_MODEL_MAX_TRACTION, modelHash), 0f);

            _statTopSpeed.Name = DevinStatsTopSpeed;
            _statAcceleration.Name = DevinStatsAcceleration;
            _statBraking.Name = DevinStatsBraking;
            _statTraction.Name = DevinStatsTraction;

            _statTopSpeed.Value = NormalizeStat(topSpeed, 60f);
            _statAcceleration.Value = NormalizeStat(acceleration, 1.20f);
            _statBraking.Value = NormalizeStat(braking, 1.50f);
            _statTraction.Value = NormalizeStat(traction, 3.50f);
        }
        catch
        {
        }
    }

    private void BuildMenu()
    {
        try
        {
            if (_menu == null)
                return;

            _menu.Clear();
            _lockedBadgeItems.Clear();

            if (_targetVehicle == null || !_targetVehicle.Exists())
            {
                _menu.Add(new NativeItem(DevinNoDataText, DevinNoDataDescText));
                return;
            }

            uint modelHash = (uint)_targetVehicle.Model.Hash;
            string vehicleName = PersistentManager.GetVehicleDisplayNamePublic(modelHash) ?? DevinUnknownVehicleText;
            string plateText = string.IsNullOrWhiteSpace(_targetPlate) ? SafeGetPlate(_targetVehicle) : _targetPlate;
            string ownerName = GetCurrentCharacterDisplayName();
            string className = DevinSuperClassText;

            UpdateStatsPanel(modelHash);

            var line1 = new NativeItem(
                string.Format(CultureInfo.InvariantCulture, DevinMenuLineVehicleName, vehicleName),
                string.Format(CultureInfo.InvariantCulture, DevinMenuLineVehiclePrice, FormatMoney(_targetSalePrice)))
            {
                Panel = _statsPanel
            };
            _menu.Add(line1);

            var line2 = new NativeItem(
                string.Format(CultureInfo.InvariantCulture, DevinMenuLineVehicleClass, className),
                DevinMenuLineVehicleClassDesc);
            _menu.Add(line2);

            var line3 = new NativeItem(
                string.Format(CultureInfo.InvariantCulture, DevinMenuLinePlate, string.IsNullOrWhiteSpace(plateText) ? DevinNoPlateText : plateText.Trim()),
                DevinCurrentPlateDesc);
            _menu.Add(line3);

            var line4 = new NativeItem(
                string.Format(CultureInfo.InvariantCulture, DevinMenuLineOwner, ownerName),
                DevinCurrentOwnerDesc);
            _menu.Add(line4);

            var line5 = new NativeItem(
                string.Format(CultureInfo.InvariantCulture, DevinMenuLineBuyback, FormatMoney(_targetSalePrice)),
                DevinMenuLineBuybackDesc);
            _menu.Add(line5);

            var confirm = new NativeItem(DevinConfirmSell, DevinConfirmSellDesc);
            confirm.Activated += (s, e) => SellTargetVehicleToDevin();
            _menu.Add(confirm);

            var back = new NativeItem(DevinBack, DevinBackDesc);
            back.Activated += (s, e) =>
            {
                CloseAllMenus(true);
            };
            _menu.Add(back);
        }
        catch { }
    }

    private void SellTargetVehicleToDevin()
    {
        try
        {
            if (!ValidateTargetVehicleStillValid())
            {
                ShowDevinMessage(DevinContactName, DevinTargetVehicleMissingText);
                CloseAllMenus(true);
                return;
            }

            Vehicle soldVeh = _targetVehicle;

            Game.Player.Money += _targetSalePrice;

            // NEW: xóa blip/icon của chiếc xe vừa bán, nhưng không đụng logic khóa xe
            try
            {
                RemovePersistentVehicleBlipForSoldVehicle(soldVeh, _targetPersistentRecord);
            }
            catch { }

            bool removedPersistent = false;

            if (_targetPersistentRecord != null)
            {
                try
                {
                    removedPersistent = RemovePersistentVehicleEntry(_targetPersistentRecord) || removedPersistent;
                }
                catch { }
            }

            try
            {
                removedPersistent = DeletePersistentVehicleLineFallback(soldVeh, _targetOwnerHash, _targetPlate) || removedPersistent;
            }
            catch { }

            try { MarkVehiclesDirty(); } catch { }

            // Khóa xe dù có dữ liệu persistent hay không
            try
            {
                LockAndDisableSoldVehicle(soldVeh);
            }
            catch { }

            ShowFeedMessage(
                DevinContactName,
                DevinSaleCompletedTitle,
                string.Format(
                    CultureInfo.InvariantCulture,
                    DevinSaleCompletedBody,
                    _targetVehicleName,
                    FormatMoney(_targetSalePrice),
                    removedPersistent ? DevinSaleCompletedSuffixRemoved : DevinSaleCompletedSuffixPlain));

            // Đóng giao dịch hoàn toàn
            CloseAllMenus(false);

            RemoveDevinBlip();
            RemoveDevinNpc();

            _spawnSuppressed = true;
            _callPending = false;
            _menuAutoOpened = false;
            CancelCleanup();
        }
        catch
        {
        }
    }

    private void CloseAllMenus(bool scheduleCleanup)
    {
        EndDevinTransactionState(scheduleCleanup);
    }

    private void ScheduleCleanup()
    {
        try
        {
            _cleanupArmed = true;
            _cleanupDueTime = Game.GameTime + CleanupDelayMs;
        }
        catch
        {
        }
    }

    private void CancelCleanup()
    {
        try
        {
            _cleanupArmed = false;
            _cleanupDueTime = -1;
        }
        catch
        {
        }
    }

    private void ProcessPendingCleanup()
    {
        try
        {
            if (!_cleanupArmed)
                return;

            if (Game.GameTime < _cleanupDueTime)
                return;

            _cleanupArmed = false;
            _cleanupDueTime = -1;

            CloseAllMenus(false);
            RemoveDevinBlip();
            RemoveDevinNpc();
            _spawnSuppressed = true;
            _callPending = false;
            _menuAutoOpened = false;
        }
        catch { }
    }

    private static void ShowFeedMessage(string sender, string subject, string body)
    {
        try
        {
            MethodInfo mi = typeof(PersistentManager).GetMethod("ShowFeedMessage", BindingFlags.NonPublic | BindingFlags.Static);
            if (mi != null)
            {
                mi.Invoke(null, new object[] { sender, subject, body });
                return;
            }
        }
        catch
        {
        }

        try
        {
            Notification.Show(body);
        }
        catch
        {
            try { GTA.UI.Screen.ShowSubtitle(body, 3000); } catch { }
        }
    }

    private static void ShowDevinMessage(string subject, string body)
    {
        try
        {
            PlayFrontendSound("Text_Arrive_Tone", "Phone_SoundSet_Default");

            Notification.Show(NotificationIcon.Devin, DevinContactName, subject, body);
            return;
        }
        catch
        {
        }

        try
        {
            Notification.Show(body);
        }
        catch
        {
            try { GTA.UI.Screen.ShowSubtitle(body, 3000); } catch { }
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

    private static T SafeCall<T>(Func<T> fn, T fallback = default(T))
    {
        try
        {
            if (fn == null)
                return fallback;
            return fn();
        }
        catch
        {
            return fallback;
        }
    }

    private static void EnsureDataFolderExists()
    {
        try { Directory.CreateDirectory(DataFolder); }
        catch { }
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
        try
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
        catch
        {
            try
            {
                string tmp = path + ".tmp";
                if (File.Exists(tmp))
                    File.Delete(tmp);
            }
            catch
            {
            }
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

    private static bool TryParseModelHash(string modelField, out uint hash)
    {
        hash = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(modelField))
                return false;

            string s = modelField.Trim();

            int idx = s.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && idx + 2 < s.Length)
            {
                string after = s.Substring(idx + 2);
                var sb = new StringBuilder();
                for (int i = 0; i < after.Length; i++)
                {
                    char c = after[i];
                    bool isHex =
                        (c >= '0' && c <= '9') ||
                        (c >= 'A' && c <= 'F') ||
                        (c >= 'a' && c <= 'f');
                    if (isHex) sb.Append(c);
                    else break;
                }

                if (sb.Length > 0 && uint.TryParse(sb.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hash))
                    return true;
            }

            string raw = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s.Substring(2).Trim() : s;
            bool allHex = raw.Length > 0 && raw.All(Uri.IsHexDigit);
            if (allHex && uint.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hash))
                return true;
        }
        catch
        {
        }

        return false;
    }

    private static bool TryParsePersistentVehicleLine(string line, out uint modelHash, out string plate, out int ownerHash, out Vector3 pos)
    {
        modelHash = 0;
        plate = string.Empty;
        ownerHash = 0;
        pos = Vector3.Zero;

        try
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            string[] p = line.Split('|');
            if (p.Length < 7)
                return false;

            if (!TryParseModelHash(p[0].Trim(), out modelHash))
                return false;

            float x = 0f, y = 0f, z = 0f;
            float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x);
            float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out y);
            float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out z);

            plate = p[5] ?? string.Empty;
            int.TryParse(p[6], out ownerHash);
            pos = new Vector3(x, y, z);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool LineMatchesVehicle(string line, Vehicle veh, int ownerHash, string plate)
    {
        try
        {
            if (veh == null || !veh.Exists() || string.IsNullOrWhiteSpace(line))
                return false;

            if (!TryParsePersistentVehicleLine(line, out uint modelHash, out string linePlate, out int lineOwner, out Vector3 linePos))
                return false;

            if (modelHash != (uint)veh.Model.Hash)
                return false;

            if (ownerHash != 0 && lineOwner != ownerHash)
                return false;

            string a = NormalizePlate(linePlate);
            string b = NormalizePlate(plate);

            if (!string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b))
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

            if (linePos.DistanceTo2D(veh.Position) <= 8f)
                return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryResolvePersistentRecordForCurrentVehicle(Vehicle veh, out object record)
    {
        record = null;

        try
        {
            IList list = GetPersistVehicleList();
            if (list == null || veh == null || !veh.Exists())
                return false;

            int ownerHash = GetCurrentPlayerModelHash();
            string plate = SafeGetPlate(veh);
            Vector3 pos = veh.Position;
            uint model = (uint)veh.Model.Hash;

            object best = null;
            int bestScore = int.MinValue;

            lock (list)
            {
                foreach (var item in list)
                {
                    try
                    {
                        if (item == null)
                            continue;

                        int itemOwner = GetIntField(item, "OwnerModelHash", 0);
                        uint itemModel = GetUIntField(item, "ModelHash", 0);

                        if (itemModel != model)
                            continue;

                        if (itemOwner != ownerHash)
                            continue;

                        string itemPlate = GetStringField(item, "Plate", string.Empty);
                        Vector3 itemPos = GetVector3Field(item, "Position");

                        int score = 0;
                        if (!string.IsNullOrWhiteSpace(plate) &&
                            !string.IsNullOrWhiteSpace(itemPlate) &&
                            string.Equals(NormalizePlate(plate), NormalizePlate(itemPlate), StringComparison.OrdinalIgnoreCase))
                        {
                            score += 1000;
                        }

                        float dist = pos.DistanceTo2D(itemPos);
                        if (dist <= 5f) score += 300;
                        else if (dist <= 15f) score += 120;
                        else if (dist <= 40f) score += 30;

                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = item;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            if (best != null)
            {
                record = best;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static IList GetPersistVehicleList()
    {
        try
        {
            FieldInfo f = typeof(PersistentManager).GetField("_persistVehicles", BindingFlags.NonPublic | BindingFlags.Static);
            if (f == null)
                return null;

            return f.GetValue(null) as IList;
        }
        catch
        {
            return null;
        }
    }

    private static void MarkVehiclesDirty()
    {
        try
        {
            FieldInfo f = typeof(PersistentManager).GetField("_vehiclesDirty", BindingFlags.NonPublic | BindingFlags.Static);
            if (f == null)
                return;

            f.SetValue(null, true);
        }
        catch
        {
        }
    }

    private static int GetIntField(object target, string fieldName, int fallback = 0)
    {
        try
        {
            object value = GetFieldValue(target, fieldName);
            if (value == null)
                return fallback;

            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return fallback;
        }
    }

    private static uint GetUIntField(object target, string fieldName, uint fallback = 0)
    {
        try
        {
            object value = GetFieldValue(target, fieldName);
            if (value == null)
                return fallback;

            return Convert.ToUInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return fallback;
        }
    }

    private static string GetStringField(object target, string fieldName, string fallback = "")
    {
        try
        {
            object value = GetFieldValue(target, fieldName);
            if (value == null)
                return fallback;

            return value as string ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static Vector3 GetVector3Field(object target, string fieldName)
    {
        try
        {
            object value = GetFieldValue(target, fieldName);
            if (value is Vector3)
                return (Vector3)value;
        }
        catch
        {
        }

        return Vector3.Zero;
    }

    private static object GetFieldValue(object target, string fieldName)
    {
        try
        {
            if (target == null || string.IsNullOrWhiteSpace(fieldName))
                return null;

            FieldInfo f = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null)
                return null;

            return f.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    private static bool RemovePersistentVehicleEntry(object record)
    {
        try
        {
            IList list = GetPersistVehicleList();
            if (list == null || record == null)
                return false;

            lock (list)
            {
                try
                {
                    bool existed = list.Contains(record);
                    if (existed)
                        list.Remove(record);

                    return existed;
                }
                catch
                {
                    return false;
                }
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool RemovePersistentVehicleBlipForSoldVehicle(Vehicle veh, object recordHint = null)
    {
        try
        {
            bool changed = false;

            if (recordHint != null)
                changed |= RemovePersistentVehicleBlipFromRecord(recordHint);

            IList list = GetPersistVehicleList();
            if (list == null)
                return changed;

            if (veh == null || !veh.Exists())
                return changed;

            int ownerHash = GetCurrentPlayerModelHash();
            string plate = SafeGetPlate(veh);
            uint model = (uint)veh.Model.Hash;
            Vector3 pos = veh.Position;

            lock (list)
            {
                foreach (var item in list)
                {
                    try
                    {
                        if (item == null)
                            continue;

                        uint itemModel = GetUIntField(item, "ModelHash", 0);
                        if (itemModel != model)
                            continue;

                        int itemOwner = GetIntField(item, "OwnerModelHash", 0);
                        if (ownerHash != 0 && itemOwner != ownerHash)
                            continue;

                        string itemPlate = GetStringField(item, "Plate", string.Empty);
                        Vector3 itemPos = GetVector3Field(item, "Position");

                        bool matchByPlate =
                            !string.IsNullOrWhiteSpace(plate) &&
                            !string.IsNullOrWhiteSpace(itemPlate) &&
                            string.Equals(NormalizePlate(plate), NormalizePlate(itemPlate), StringComparison.OrdinalIgnoreCase);

                        bool matchByPos = itemPos.DistanceTo2D(pos) <= 8f;

                        if (matchByPlate || matchByPos)
                        {
                            changed |= RemovePersistentVehicleBlipFromRecord(item);
                            break;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            return changed;
        }
        catch
        {
            return false;
        }
    }

    // NEW: delete blip object gắn trong record persistent
    private static bool RemovePersistentVehicleBlipFromRecord(object record)
    {
        try
        {
            if (record == null)
                return false;

            FieldInfo blipField = record.GetType().GetField(
                "MapBlip",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (blipField == null)
                return false;

            try
            {
                Blip blip = blipField.GetValue(record) as Blip;
                if (blip != null)
                {
                    try
                    {
                        if (blip.Exists())
                            blip.Delete();
                    }
                    catch
                    {
                    }
                }
            }
            finally
            {
                try { blipField.SetValue(record, null); } catch { }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool DeletePersistentVehicleLineFallback(Vehicle veh, int ownerHash, string plate)
    {
        try
        {
            EnsureDataFolderExists();

            string[] lines = ReadAllLinesSafe(PersistentVehiclesFile);
            if (lines == null || lines.Length == 0)
                return false;

            var kept = new List<string>();
            bool removedAny = false;

            foreach (string line in lines)
            {
                try
                {
                    if (LineMatchesVehicle(line, veh, ownerHash, plate))
                    {
                        removedAny = true;
                        continue;
                    }

                    kept.Add(line);
                }
                catch
                {
                    kept.Add(line);
                }
            }

            if (removedAny)
                WriteAllLinesAtomic(PersistentVehiclesFile, kept.ToArray());

            return removedAny;
        }
        catch
        {
            return false;
        }
    }

    private void UpdateMenuMouseState()
    {
        try
        {
            if (_menu == null || !_menu.Visible)
                return;

            _menu.MouseBehavior = MenuMouseBehavior.Disabled;
            _menu.ResetCursorWhenOpened = false;
            _menu.CloseOnInvalidClick = false;
            _menu.RotateCamera = true;
        }
        catch
        {
        }
    }

    private void FlushUiActions()
    {
        try
        {
            while (true)
            {
                Action next = null;

                lock (_uiActions)
                {
                    if (_uiActions.Count > 0)
                        next = _uiActions.Dequeue();
                }

                if (next == null)
                    break;

                try { next(); } catch { }
            }
        }
        catch
        {
        }
    }

    private static string FormatMoney(int value)
    {
        if (value < 0)
            value = 0;

        return string.Format(CultureInfo.InvariantCulture, "${0:N0}", value);
    }

    private bool IsAnyMenuVisible()
    {
        return _menu != null && _menu.Visible;
    }
}