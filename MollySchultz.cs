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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

public class MollySchultz : Script
{
    private static string L(string key, string fallback)
    {
        return Language.Get(key, fallback);
    }

    private const float MOLLY_INTERACT_RADIUS = 5.0f;
    private const int MOLLY_CONTACT_TIMEOUT_MS = 2000;
    private const int MOLLY_CALL_DELAY_MS = 1000;
    private const double MOLLY_REFUND_RATE_MIN = 0.48;
    private const double MOLLY_REFUND_RATE_MAX = 0.62;

    private static readonly Random _mollyRefundRandom = new Random();
    private readonly Dictionary<object, double> _mollyRefundRateCache =
        new Dictionary<object, double>(ReferenceEqualityComparer.Instance);

    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "PersistentManager"
        );

    private static readonly string PersistentVehiclesFile = Path.Combine(DataFolder, "persistent_vehicles.txt");

    private const int MOLLY_CLEANUP_DELAY_MS = 25000;
    private bool _mollyCleanupArmed = false;
    private int _mollyCleanupDueTime = -1;

    private bool _mollySpawnSuppressed = false;
    private bool _mollyRefuseCallsUntilCleanup = false;

    private static readonly Vector3 MollyPosition = new Vector3(-2319.292000f, 311.796400f, 169.602100f);
    private const float MollyHeading = 172.3377f;

    private static readonly BadgeSet LockBadge = new BadgeSet
    {
        NormalDictionary = "commonmenu",
        NormalTexture = "shop_lock",
        HoveredDictionary = "commonmenu",
        HoveredTexture = "shop_lock_b"
    };

    private readonly ObjectPool _menuPool = new ObjectPool();
    private readonly Queue<Action> _uiActions = new Queue<Action>();

    private NativeMenu _mainMenu;
    private NativeMenu _detailMenu;
    private NativeMenu _bulkMenu;

    private bool _mainMenuBuilt = false;
    private bool _detailMenuBuilt = false;
    private bool _bulkMenuBuilt = false;

    private NativeStatsPanel _statsPanel;
    private NativeStatsInfo _statTopSpeed;
    private NativeStatsInfo _statAcceleration;
    private NativeStatsInfo _statBraking;
    private NativeStatsInfo _statTraction;

    private readonly HashSet<NativeItem> _mainLockedBadgeItems = new HashSet<NativeItem>();
    private readonly HashSet<NativeItem> _bulkLockedBadgeItems = new HashSet<NativeItem>();
    private readonly HashSet<NativeItem> _detailLockedBadgeItems = new HashSet<NativeItem>();

    private readonly HashSet<object> _bulkSelected = new HashSet<object>();
    private readonly List<object> _bulkEntries = new List<object>();
    private readonly List<NativeCheckboxItem> _bulkCheckboxItems = new List<NativeCheckboxItem>();

    private object _selectedVehicle = null;

    private bool _bulkUiSync = false;
    private bool _detailUiSync = false;

    private Blip _mollyBlip = null;
    private Ped _mollyNpc = null;
    private int _mollySpawnRequestedAt = -1;
    private const int MOLLY_SPAWN_WARMUP_MS = 800;
    private bool _mollyContactAdded = false;
    private bool _mollyCallPending = false;
    private int _mollyCallDueTime = 0;
    private bool _menuAutoOpened = false;

    private static readonly string MollyContactName = L("MollySchultz_ContactName", "Molly Schultz");
    private static readonly string MollyMenuTitle = L("MollySchultz_MenuTitle", "Molly Schultz");
    private static readonly string MollyMenuSubtitle = L("MollySchultz_MenuSubtitle", "THANH LÝ XE SUPER");
    private static readonly string MollyDetailSubtitle = L("MollySchultz_DetailSubtitle", "THÔNG TIN THANH LÝ CHI TIẾT");
    private static readonly string MollyBulkSubtitle = L("MollySchultz_BulkSubtitle", "CHỌN NHIỀU PHƯƠNG TIỆN ĐỂ THANH LÝ");

    private static readonly string MollyNoSuperVehicles = L("MollySchultz_NoSuperVehicles", "Không có phương tiện Super nào để thanh lý.");
    private static readonly string MollyNoSelection = L("MollySchultz_NoSelection", "Bạn chưa chọn phương tiện nào.");
    private static readonly string MollyNoCharacter = L("MollySchultz_NoCharacter", "Không có nhân vật.");
    private static readonly string MollySellConfirm = L("MollySchultz_SellConfirm", "Xác nhận bán phương tiện cho Molly");
    private static readonly string MollyBulkConfirm = L("MollySchultz_BulkConfirm", "Xác nhận thanh lý");
    private static readonly string MollyBulkBack = L("MollySchultz_BulkBack", "Quay lại danh sách phương tiện");
    private static readonly string MollyBackToList = L("MollySchultz_BackToList", "Quay lại danh sách phương tiện");
    private static readonly string MollyMainQuickSell = L("MollySchultz_QuickSell", "Thanh lý nhanh phương tiện");
    private static readonly string MollyMainQuickSellDesc = L("MollySchultz_QuickSellDesc", "Chọn nhiều xe Super mà bạn muốn thanh lý cùng lúc.");
    private static readonly string MollyNoVehiclesOwned = L("MollySchultz_NoVehiclesOwned", "Danh sách đang trống.");
    private static readonly string MollyNoCharacterDesc = L("MollySchultz_NoCharacterDesc", "Không thể xem danh sách.");
    private static readonly string MollySuperOnlyDesc = L("MollySchultz_SuperOnlyDesc", "Chỉ các phương tiện thuộc class Super mới được Molly thanh lý.");
    private static readonly string MollySellSuccessSubject = L("MollySchultz_SuccessSubject", "Thanh lý hoàn tất");
    private static readonly string MollySellSuccessBody = L("MollySchultz_SuccessBody", "Đã thanh lý ~y~{0}~s~ phương tiện và nhận lại ~g~+{1}~s~.");
    private static readonly string MollyVehicleRefundBody = L("MollySchultz_VehicleRefundBody", "Molly sẽ trả lại ~HUD_COLOUR_DEGEN_GREEN~{0}~s~ cho chiếc xe này.");
    private static readonly string MollyCollateralLockedDesc = L("MollySchultz_CollateralLockedDesc", "Bạn đang thế chấp chiếc phương tiện này cho hoạt động vay của bạn tại ngân hàng Fleeca nên không thể thanh lý");
    private static readonly string MollyVehicleItemDesc = L("MollySchultz_VehicleItemDesc", "Nhấn Enter để xem thông tin phương tiện này trước khi thanh lý.");
    private static readonly string MollyDetailPriceLabel = L("MollySchultz_DetailPriceLabel", "Mức thanh lý dự kiến: {0}");
    private static readonly string MollyDetailLossLabel = L("MollySchultz_DetailLossLabel", "Giá trị hao hụt: {0}");
    private static readonly string MollyDetailOwnerLabel = L("MollySchultz_OwnerLabel", "Chủ sở hữu: {0}");
    private static readonly string MollyDetailPlateLabel = L("MollySchultz_PlateLabel", "Biển số xe: {0}");
    private static readonly string MollyDetailVehicleNameLabel = L("MollySchultz_NameLabel", "Tên phương tiện: {0}");
    private static readonly string MollyDetailClassLabel = L("MollySchultz_ClassLabel", "Loại phương tiện: {0}");
    private static readonly string MollyDetailSellDesc = L("MollySchultz_DetailSellDesc", "Nhận số tiền hoàn trả về và phương tiện sẽ được hủy bỏ khỏi danh sách thanh lý.");

    private static readonly string MollyUnspecifiedText = L("MollySchultz_UnspecifiedText", "Chưa có");
    private static readonly string MollyVehicleClassSuper = L("MollySchultz_ClassSuper", "Super");

    private static readonly string MollyCharacterFranklin = L("MollySchultz_CharacterFranklin", "Franklin");
    private static readonly string MollyCharacterMichael = L("MollySchultz_CharacterMichael", "Michael");
    private static readonly string MollyCharacterTrevor = L("MollySchultz_CharacterTrevor", "Trevor");
    private static readonly string MollyCharacterUnknown = L("MollySchultz_CharacterUnknown", "Không xác định");

    private static readonly FieldInfo PersistVehiclesField = typeof(PersistentManager).GetField("_persistVehicles", BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly FieldInfo VehiclesDirtyField = typeof(PersistentManager).GetField("_vehiclesDirty", BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly MethodInfo ShowFeedMessageMethod = typeof(PersistentManager).GetMethod("ShowFeedMessage", BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly MethodInfo GetVehicleDisplayNameMethod = typeof(PersistentManager).GetMethod("GetVehicleDisplayNamePublic", BindingFlags.Public | BindingFlags.Static);
    private static readonly MethodInfo SafeRemoveBlipMethod = typeof(PersistentManager).GetMethod("SafeRemoveBlip", BindingFlags.NonPublic | BindingFlags.Static);

    public MollySchultz()
    {
        Tick += OnTick;
        KeyDown += OnKeyDown;
        Aborted += OnAborted;
        Interval = 1000;
    }

    private void OnTick(object sender, EventArgs e)
    {
        try
        {
            EnsureMollyContactRegistered();
            SyncMollyContactAvailability();
            ProcessPendingMollyCleanup();
            ProcessPendingMollyCall();

            if (IsAnyMenuVisible() && !IsPlayerNearMolly())
            {
                CloseAllMollyMenus(false);
            }

            bool anyMenuVisible = (_mainMenu != null && _mainMenu.Visible) ||
                                  (_detailMenu != null && _detailMenu.Visible) ||
                                  (_bulkMenu != null && _bulkMenu.Visible);

            if (anyMenuVisible)
            {
                _menuPool.Process();
                FlushUiActions();
                Interval = 0;
                return;
            }

            FlushUiActions();

            if (_mollyCallPending)
            {
                Interval = 100;
                return;
            }

            if (_menuAutoOpened)
            {
                _menuAutoOpened = false;
            }

            Interval = 1000;
        }
        catch { }
    }

    private double GetMollyRefundRate(object vehicleEntry)
    {
        try
        {
            if (vehicleEntry == null)
                return MOLLY_REFUND_RATE_MIN;

            lock (_mollyRefundRateCache)
            {
                double rate;
                if (_mollyRefundRateCache.TryGetValue(vehicleEntry, out rate))
                    return rate;

                rate = MOLLY_REFUND_RATE_MIN +
                       (_mollyRefundRandom.NextDouble() * (MOLLY_REFUND_RATE_MAX - MOLLY_REFUND_RATE_MIN));

                _mollyRefundRateCache[vehicleEntry] = rate;
                return rate;
            }
        }
        catch
        {
            return MOLLY_REFUND_RATE_MIN;
        }
    }

    private static int ComputeRefundByRate(int purchasePrice, double refundRate)
    {
        try
        {
            if (purchasePrice <= 0)
                return 0;

            if (refundRate < MOLLY_REFUND_RATE_MIN)
                refundRate = MOLLY_REFUND_RATE_MIN;

            if (refundRate > MOLLY_REFUND_RATE_MAX)
                refundRate = MOLLY_REFUND_RATE_MAX;

            return (int)Math.Round(purchasePrice * refundRate, MidpointRounding.AwayFromZero);
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatRefundRate(double rate)
    {
        return (rate * 100.0).ToString("0", CultureInfo.InvariantCulture);
    }

    private void ForgetMollyRefundRate(object vehicleEntry)
    {
        try
        {
            if (vehicleEntry == null)
                return;

            lock (_mollyRefundRateCache)
            {
                _mollyRefundRateCache.Remove(vehicleEntry);
            }
        }
        catch
        {
        }
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

        public new bool Equals(object x, object y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(object obj)
        {
            return obj == null ? 0 : RuntimeHelpers.GetHashCode(obj);
        }
    }

    private void OnKeyDown(object sender, System.Windows.Forms.KeyEventArgs e)
    {
        try
        {
            if (IsAnyMenuVisible() && (e.KeyCode == System.Windows.Forms.Keys.Escape || e.KeyCode == System.Windows.Forms.Keys.Back))
            {
                CloseAllMollyMenus(true);
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
            CloseAllMollyMenus(false);
            RemoveMollyBlip();
            RemoveMollyNpc();
        }
        catch
        {
        }
    }

    private void ScheduleMollyCleanup()
    {
        try
        {
            _mollyCleanupArmed = true;
            _mollyCleanupDueTime = Game.GameTime + MOLLY_CLEANUP_DELAY_MS;
        }
        catch
        {
        }
    }

    private void CancelMollyCleanup()
    {
        try
        {
            _mollyCleanupArmed = false;
            _mollyCleanupDueTime = -1;
        }
        catch
        {
        }
    }

    private void ProcessPendingMollyCleanup()
    {
        try
        {
            if (!_mollyCleanupArmed)
                return;

            if (Game.GameTime < _mollyCleanupDueTime)
                return;

            _mollyCleanupArmed = false;
            _mollyCleanupDueTime = -1;

            CloseAllMollyMenus(false);
            RemoveMollyBlip();
            RemoveMollyNpc();

            _mollySpawnSuppressed = true;
            _mollyCallPending = false;
            _menuAutoOpened = false;

            if (_mollyRefuseCallsUntilCleanup)
            {
                _mollyRefuseCallsUntilCleanup = false;
                SyncMollyContactAvailability();
            }
        }
        catch
        {
        }
    }

    private void SetMollyContactActive(bool active)
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            var contact = phone.Contacts.FirstOrDefault(c =>
                string.Equals(c.Name, MollyContactName, StringComparison.OrdinalIgnoreCase));

            if (contact != null)
                contact.Active = active;
        }
        catch
        {
        }
    }

    private void SyncMollyContactAvailability()
    {
        try
        {
            SetMollyContactActive(!_mollyRefuseCallsUntilCleanup);
        }
        catch
        {
        }
    }

    private void BeginPostSaleMollyCleanup()
    {
        try
        {
            // giữ Molly tồn tại thêm một khoảng, nhưng khóa contact để không gọi lại được
            CloseAllMollyMenus(false);

            _mollyRefuseCallsUntilCleanup = true;
            _mollyCleanupArmed = true;
            _mollyCleanupDueTime = Game.GameTime + MOLLY_CLEANUP_DELAY_MS;

            _mollyCallPending = false;
            _menuAutoOpened = false;

            SyncMollyContactAvailability();
        }
        catch
        {
        }
    }

    private void RequestMollyCollision()
    {
        try
        {
            Function.Call(Hash.REQUEST_COLLISION_AT_COORD, MollyPosition.X, MollyPosition.Y, MollyPosition.Z);
            Function.Call(Hash.REQUEST_ADDITIONAL_COLLISION_AT_COORD, MollyPosition.X, MollyPosition.Y, MollyPosition.Z);
        }
        catch
        {
        }
    }

    private void DismissMollyImmediately()
    {
        try
        {
            CloseAllMollyMenus(false);
            RemoveMollyBlip();
            RemoveMollyNpc();

            _mollySpawnSuppressed = true;
            _mollyCallPending = false;
            _menuAutoOpened = false;

            CancelMollyCleanup();
        }
        catch
        {
        }
    }

    private bool IsAnyMenuVisible()
    {
        return (_mainMenu != null && _mainMenu.Visible) ||
               (_detailMenu != null && _detailMenu.Visible) ||
               (_bulkMenu != null && _bulkMenu.Visible);
    }

    private void EnsureMollyContactRegistered()
    {
        try
        {
            if (_mollyContactAdded)
            {
                SyncMollyContactAvailability();
                return;
            }

            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            if (phone.Contacts.Any(c => string.Equals(c.Name, MollyContactName, StringComparison.OrdinalIgnoreCase)))
            {
                _mollyContactAdded = true;
                return;
            }

            var contact = new iFruitContact(MollyContactName)
            {
                Active = true,
                DialTimeout = MOLLY_CONTACT_TIMEOUT_MS,
                Bold = false,
                Icon = ContactIcon.Molly
            };

            contact.Answered += OnMollyContactAnswered;
            phone.Contacts.Add(contact);
            _mollyContactAdded = true;
        }
        catch
        {
        }
    }

    private void OnMollyContactAnswered(iFruitContact sender)
    {
        try
        {
            if (_mollyRefuseCallsUntilCleanup)
            {
                try { sender.Active = false; } catch { }
                try { CustomiFruit.GetCurrentInstance()?.Close(0); } catch { }
                return;
            }

            _mollyCallPending = true;
            _mollySpawnSuppressed = false;
            _mollyCallDueTime = Game.GameTime + MOLLY_CALL_DELAY_MS;
            _mollySpawnRequestedAt = Game.GameTime;
            EnsureMollyBlip();
        }
        finally
        {
            try { Game.Player.Character.Task.PutAwayMobilePhone(); } catch { }
            try { CustomiFruit.GetCurrentInstance()?.Close(0); } catch { }
        }
    }

    private void ProcessPendingMollyCall()
    {
        try
        {
            if (!_mollyCallPending)
                return;

            EnsureMollyNpcSpawned();
            EnsureMollyBlip();

            if (Game.GameTime < _mollyCallDueTime)
                return;

            if (IsPlayerNearMolly())
            {
                OpenMollyMenu();
                _mollyCallPending = false;
                RemoveMollyBlip();
            }
        }
        catch
        {
        }
    }

    private bool IsPlayerNearMolly()
    {
        try
        {
            Ped p = Game.Player.Character;
            if (p == null || !p.Exists() || p.IsDead)
                return false;

            return p.Position.DistanceTo(MollyPosition) <= MOLLY_INTERACT_RADIUS;
        }
        catch
        {
            return false;
        }
    }

    private void EnsureMollyBlip()
    {
        try
        {
            if (!_mollyCallPending && (_mollyNpc == null || !_mollyNpc.Exists()) && !IsAnyMenuVisible())
                return;

            if (_mollyNpc != null && _mollyNpc.Exists())
            {
                if (_mollyBlip != null && _mollyBlip.Exists())
                {
                    try { _mollyBlip.Position = _mollyNpc.Position; } catch { }
                    try { _mollyBlip.Color = BlipColor.Yellow; } catch { }
                    try { _mollyBlip.Name = MollyContactName; } catch { }
                    return;
                }

                _mollyBlip = _mollyNpc.AddBlip();
                if (_mollyBlip != null && _mollyBlip.Exists())
                {
                    _mollyBlip.IsShortRange = false;
                    _mollyBlip.Sprite = BlipSprite.Standard;
                    _mollyBlip.Color = BlipColor.Yellow;
                    _mollyBlip.Name = MollyContactName;
                }
                return;
            }

            if (_mollyBlip != null && _mollyBlip.Exists())
            {
                try { _mollyBlip.Position = MollyPosition; } catch { }
                try { _mollyBlip.Color = BlipColor.Yellow; } catch { }
                try { _mollyBlip.Name = MollyContactName; } catch { }
                return;
            }

            _mollyBlip = World.CreateBlip(MollyPosition);
            if (_mollyBlip != null && _mollyBlip.Exists())
            {
                _mollyBlip.IsShortRange = false;
                _mollyBlip.Sprite = BlipSprite.Standard;
                _mollyBlip.Color = BlipColor.Yellow;
                _mollyBlip.Name = MollyContactName;
            }
        }
        catch
        {
        }
    }

    private void RemoveMollyBlip()
    {
        try
        {
            if (_mollyBlip == null)
                return;

            if (SafeRemoveBlipMethod != null)
            {
                try { SafeRemoveBlipMethod.Invoke(null, new object[] { _mollyBlip }); } catch { }
            }
            else if (_mollyBlip.Exists())
            {
                _mollyBlip.Delete();
            }
        }
        catch
        {
        }
        finally
        {
            _mollyBlip = null;
        }
    }

    private void RemoveMollyNpc()
    {
        try
        {
            if (_mollyNpc != null && _mollyNpc.Exists())
            {
                try { _mollyNpc.MarkAsNoLongerNeeded(); } catch { }
                try { _mollyNpc.Delete(); } catch { }
            }
        }
        catch
        {
        }
        finally
        {
            _mollyNpc = null;
            _mollySpawnRequestedAt = -1;
        }
    }

    private void EnsureMollyNpcSpawned()
    {
        try
        {
            if (!_mollyCallPending && !_menuAutoOpened)
                return;

            if (_mollySpawnSuppressed && !_mollyCallPending && !IsAnyMenuVisible())
                return;

            if (_mollyNpc != null && _mollyNpc.Exists())
                return;

            if (_mollySpawnRequestedAt < 0)
            {
                _mollySpawnRequestedAt = Game.GameTime;
                RequestMollyCollision();
                return;
            }

            if (Game.GameTime - _mollySpawnRequestedAt < MOLLY_SPAWN_WARMUP_MS)
            {
                RequestMollyCollision();
                return;
            }

            RequestMollyCollision();

            var model = new Model("ig_molly");
            if (!model.IsLoaded)
                model.Request(250);

            if (!model.IsLoaded)
                return;

            Ped ped = World.CreatePed(model, MollyPosition, MollyHeading);
            if (ped == null || !ped.Exists())
                return;

            try { ped.IsPersistent = true; } catch { }
            try { ped.BlockPermanentEvents = true; } catch { }
            try { ped.IsInvincible = true; } catch { }
            try { ped.CanRagdoll = false; } catch { }
            try { Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, ped.Handle, true, true); } catch { }
            try { Function.Call(Hash.SET_ENTITY_HEADING, ped.Handle, MollyHeading); } catch { }
            try { Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET, ped.Handle, MollyPosition.X, MollyPosition.Y, MollyPosition.Z, false, false, false); } catch { }
            try { Function.Call(Hash.FREEZE_ENTITY_POSITION, ped.Handle, true); } catch { }
            try { ped.Task.ClearAllImmediately(); } catch { }
            try { ped.Task.StandStill(-1); } catch { }

            _mollyNpc = ped;
            _mollySpawnRequestedAt = -1;

            RemoveMollyBlip();
            EnsureMollyBlip();
        }
        catch
        {
        }
    }

    private static IList GetPersistVehicleList()
    {
        try
        {
            if (PersistVehiclesField == null)
                return null;

            return PersistVehiclesField.GetValue(null) as IList;
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
            if (VehiclesDirtyField == null)
                return;

            VehiclesDirtyField.SetValue(null, true);
        }
        catch
        {
        }
    }

    private static string GetVehicleName(uint modelHash)
    {
        try
        {
            if (GetVehicleDisplayNameMethod != null)
            {
                var name = GetVehicleDisplayNameMethod.Invoke(null, new object[] { modelHash }) as string;
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }
        }
        catch { }

        return string.Format(CultureInfo.InvariantCulture, "0x{0:X}", modelHash);
    }

    private static object GetFieldValue(object target, string fieldName)
    {
        try
        {
            if (target == null || string.IsNullOrWhiteSpace(fieldName))
                return null;

            var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null)
                return null;

            return field.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    private static void SetFieldValue(object target, string fieldName, object value)
    {
        try
        {
            if (target == null || string.IsNullOrWhiteSpace(fieldName))
                return;

            var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null)
                return;

            field.SetValue(target, value);
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

    private static bool GetBoolField(object target, string fieldName, bool fallback = false)
    {
        try
        {
            object value = GetFieldValue(target, fieldName);
            if (value == null)
                return fallback;

            return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
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

    private static int? GetNullableIntField(object target, string fieldName)
    {
        try
        {
            object value = GetFieldValue(target, fieldName);
            if (value == null)
                return null;

            if (value is int)
                return (int)value;

            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static int[] GetIntArrayField(object target, string fieldName)
    {
        try
        {
            object value = GetFieldValue(target, fieldName);
            if (value is int[])
                return (int[])value;
        }
        catch
        {
        }

        return null;
    }

    private static Blip GetBlipField(object target, string fieldName)
    {
        try
        {
            object value = GetFieldValue(target, fieldName);
            if (value is Blip)
                return (Blip)value;
        }
        catch
        {
        }

        return null;
    }

    private void RemovePersistentVehicleEntry(object vehicleEntry)
    {
        try
        {
            if (vehicleEntry == null)
                return;

            try
            {
                Vehicle veh = GetFieldValue(vehicleEntry, "RuntimeVehicle") as Vehicle;
                if (veh != null)
                {
                    bool exists = false;
                    try { exists = veh.Exists(); } catch { exists = false; }

                    if (exists)
                    {
                        try { veh.LockStatus = VehicleLockStatus.Locked; } catch { }
                        try { Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED, veh.Handle, 2); } catch { }
                        try { Function.Call(Hash.SET_VEHICLE_UNDRIVEABLE, veh.Handle, true); } catch { }
                        try { Function.Call(Hash.SET_VEHICLE_ENGINE_ON, veh.Handle, false, true, true); } catch { }
                    }
                }
            }
            catch
            {
            }

            try
            {
                Blip blip = GetBlipField(vehicleEntry, "MapBlip");
                if (blip != null)
                {
                    if (SafeRemoveBlipMethod != null)
                    {
                        try { SafeRemoveBlipMethod.Invoke(null, new object[] { blip }); } catch { }
                    }
                    else
                    {
                        try
                        {
                            if (blip.Exists())
                                blip.Delete();
                        }
                        catch { }
                    }
                }
            }
            catch
            {
            }

            try { SetFieldValue(vehicleEntry, "MapBlip", null); } catch { }
            try { SetFieldValue(vehicleEntry, "RuntimeVehicle", null); } catch { }

            try
            {
                IList list = GetPersistVehicleList();
                if (list != null)
                {
                    lock (list)
                    {
                        try { list.Remove(vehicleEntry); } catch { }
                    }
                }
            }
            catch
            {
            }

            try
            {
                DeletePersistentVehicleRecordFallback(vehicleEntry);
            }
            catch
            {
            }

            MarkVehiclesDirty();
        }
        catch
        {
        }
    }

    private static bool LineMatchesPersistentVehicleEntry(string line, object vehicleEntry)
    {
        try
        {
            if (vehicleEntry == null || string.IsNullOrWhiteSpace(line))
                return false;

            if (!TryParsePersistentVehicleLine(line, out uint lineModelHash, out string linePlate, out int lineOwnerHash, out Vector3 linePos))
                return false;

            uint modelHash = GetUIntField(vehicleEntry, "ModelHash", 0);
            int ownerHash = GetIntField(vehicleEntry, "OwnerModelHash", 0);
            string plate = GetStringField(vehicleEntry, "Plate", string.Empty);
            Vector3 pos = GetVector3Field(vehicleEntry, "Position");

            if (lineModelHash != modelHash || lineOwnerHash != ownerHash)
                return false;

            string a = NormalizePlate(linePlate);
            string b = NormalizePlate(plate);

            if (!string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b))
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

            return linePos.DistanceTo2D(pos) <= 8f;
        }
        catch
        {
            return false;
        }
    }

    private static void DeletePersistentVehicleRecordFallback(object vehicleEntry)
    {
        try
        {
            EnsureDataFolderExists();

            string[] lines = ReadAllLinesSafe(PersistentVehiclesFile);
            if (lines == null || lines.Length == 0)
                return;

            var kept = new List<string>();

            foreach (string line in lines)
            {
                try
                {
                    if (LineMatchesPersistentVehicleEntry(line, vehicleEntry))
                        continue;

                    kept.Add(line);
                }
                catch
                {
                    kept.Add(line);
                }
            }

            WriteAllLinesAtomic(PersistentVehiclesFile, kept.ToArray());
        }
        catch
        {
        }
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

    private static string[] ReadAllLinesSafe(string path)
    {
        try
        {
            if (!File.Exists(path))
                return Array.Empty<string>();

            return File.ReadAllLines(path, Encoding.UTF8);
        }
        catch
        {
            return Array.Empty<string>();
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

            string modelField = p[0].Trim();

            bool parsed = false;

            int idx = modelField.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                string hexPart = modelField.Substring(idx + 2).Trim();
                if (uint.TryParse(hexPart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out modelHash))
                    parsed = true;
            }

            if (!parsed && uint.TryParse(modelField, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out modelHash))
                parsed = true;

            if (!parsed)
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

    private static bool IsSuperVehicle(uint modelHash)
    {
        try
        {
            int cls = Function.Call<int>(Hash.GET_VEHICLE_CLASS_FROM_NAME, modelHash);
            return cls == 7;
        }
        catch
        {
            return false;
        }
    }

    private List<object> GetOwnedSuperVehiclesForCurrentPlayer()
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return new List<object>();

            int ownerHash = player.Model.Hash;
            IList list = GetPersistVehicleList();
            if (list == null)
                return new List<object>();

            var result = new List<object>();
            lock (list)
            {
                foreach (var item in list)
                {
                    try
                    {
                        if (item == null)
                            continue;

                        int itemOwnerHash = GetIntField(item, "OwnerModelHash", 0);
                        uint modelHash = GetUIntField(item, "ModelHash", 0);

                        if (itemOwnerHash != ownerHash)
                            continue;

                        if (!IsSuperVehicle(modelHash))
                            continue;

                        result.Add(item);
                    }
                    catch
                    {
                    }
                }
            }

            return result;
        }
        catch
        {
            return new List<object>();
        }
    }

    private static string FormatMoney(int value)
    {
        if (value < 0)
            value = 0;

        return string.Format(CultureInfo.InvariantCulture, "${0:N0}", value);
    }

    private static string GetCharacterDisplayNameFromHash(int modelHash)
    {
        switch (modelHash)
        {
            case -1692214353: return MollyCharacterFranklin;
            case 225514697: return MollyCharacterMichael;
            case -1686040670: return MollyCharacterTrevor;
            default:
                return MollyCharacterUnknown;
        }
    }

    private static void SetRightBadgeSetIfExists(NativeItem item, BadgeSet badge)
    {
        try
        {
            if (item == null)
                return;

            Type t = item.GetType();

            string[] propertyNames = { "RightBadgeSet", "BadgeRightSet", "RightBadge" };
            foreach (string name in propertyNames)
            {
                PropertyInfo prop = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null && prop.CanWrite && prop.PropertyType == typeof(BadgeSet))
                {
                    prop.SetValue(item, badge, null);
                    return;
                }
            }

            string[] fieldNames = { "badgeRight", "rightBadge", "_rightBadge", "m_rightBadge" };
            foreach (string name in fieldNames)
            {
                FieldInfo field = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null && field.FieldType == typeof(BadgeSet))
                {
                    field.SetValue(item, badge);
                    return;
                }
            }
        }
        catch
        {
        }
    }

    private void EnsureStatsPanel()
    {
        if (_statsPanel != null)
            return;

        _statTopSpeed = new NativeStatsInfo(L("MollyStat_TopSpeed", "Tốc độ cao nhất"), 0);
        _statAcceleration = new NativeStatsInfo(L("MollyStat_Acceleration", "Gia tốc"), 0);
        _statBraking = new NativeStatsInfo(L("MollyStat_Braking", "Phanh"), 0);
        _statTraction = new NativeStatsInfo(L("MollyStat_Traction", "Độ bám đường"), 0);

        _statsPanel = new NativeStatsPanel(_statTopSpeed, _statAcceleration, _statBraking, _statTraction)
        {
            BackgroundColor = System.Drawing.Color.FromArgb(180, 28, 28, 28),
            ForegroundColor = System.Drawing.Color.FromArgb(255, 255, 255, 255),
            Visible = true
        };
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

            _statTopSpeed.Name = L("MollyStat_TopSpeed", "Tốc độ cao nhất");
            _statAcceleration.Name = L("MollyStat_Acceleration", "Gia tốc");
            _statBraking.Name = L("MollyStat_Braking", "Phanh");
            _statTraction.Name = L("MollyStat_Traction", "Độ bám đường");

            _statTopSpeed.Value = NormalizeStat(topSpeed, 60f);
            _statAcceleration.Value = NormalizeStat(acceleration, 1.20f);
            _statBraking.Value = NormalizeStat(braking, 1.50f);
            _statTraction.Value = NormalizeStat(traction, 3.50f);
        }
        catch
        {
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

    private void ConfigureMenu(NativeMenu menu)
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

    private void OpenMollyMenu()
    {
        try
        {
            CancelMollyCleanup();
            _mollySpawnSuppressed = false;

            EnsureMollyNpcSpawned();
            EnsureMollyBlip();
            BuildMainMenu(true);
            if (_detailMenu != null) _detailMenu.Visible = false;
            if (_bulkMenu != null) _bulkMenu.Visible = false;

            ConfigureMenu(_mainMenu);
            if (_mainMenu != null)
            {
                _mainMenu.Visible = true;
                Interval = 0;
            }
        }
        catch
        {
        }
    }

    private void OpenDetailMenu(object vehicleEntry)
    {
        try
        {
            if (vehicleEntry == null)
                return;

            _selectedVehicle = vehicleEntry;
            BuildDetailMenu();
            RefreshDetailMenu();

            if (_mainMenu != null) _mainMenu.Visible = false;
            if (_bulkMenu != null) _bulkMenu.Visible = false;

            ConfigureMenu(_detailMenu);
            if (_detailMenu != null)
            {
                _detailMenu.Visible = true;
                Interval = 0;
            }
        }
        catch
        {
        }
    }

    private void OpenBulkSellMenu()
    {
        try
        {
            _bulkSelected.Clear();
            BuildBulkMenu(true);
            RefreshBulkMenu();

            if (_mainMenu != null) _mainMenu.Visible = false;
            if (_detailMenu != null) _detailMenu.Visible = false;

            ConfigureMenu(_bulkMenu);
            if (_bulkMenu != null)
            {
                _bulkMenu.Visible = true;
                Interval = 0;
            }
        }
        catch { }
    }

    private void CloseAllMollyMenus(bool scheduleCleanup = true)
    {
        try
        {
            if (_mainMenu != null) _mainMenu.Visible = false;
            if (_detailMenu != null) _detailMenu.Visible = false;
            if (_bulkMenu != null) _bulkMenu.Visible = false;

            _selectedVehicle = null;
            _bulkEntries.Clear();
            _bulkCheckboxItems.Clear();
            _bulkSelected.Clear();
            _menuAutoOpened = false;

            if (scheduleCleanup)
                ScheduleMollyCleanup();
        }
        catch { }
        finally
        {
            Interval = 1000;
        }
    }

    private void BuildMainMenu(bool forceRebuild = false)
    {
        try
        {
            if (!forceRebuild && _mainMenuBuilt && _mainMenu != null)
                return;

            _mainMenu = new NativeMenu(MollyMenuTitle, MollyMenuSubtitle);
            _mainMenu.KeepNameCasing = true;
            _mainMenu.MaxItems = 8;
            _mainMenu.NoItemsText = MollyNoSuperVehicles;
            _menuPool.Add(_mainMenu);
            _mainMenuBuilt = true;

            RefreshMainMenu();
        }
        catch { }
    }

    private void RefreshMainMenu()
    {
        try
        {
            if (_mainMenu == null)
                return;

            _mainLockedBadgeItems.Clear();
            _mainMenu.Clear();

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
            {
                _mainMenu.Add(new NativeItem(MollyNoCharacter, MollyNoCharacterDesc));
                return;
            }

            List<object> owned = GetOwnedSuperVehiclesForCurrentPlayer();
            if (owned.Count == 0)
            {
                _mainMenu.Add(new NativeItem(MollyNoVehiclesOwned, MollyNoSuperVehicles));
            }
            else
            {
                int index = 1;
                foreach (var pv in owned)
                {
                    try
                    {
                        uint modelHash = GetUIntField(pv, "ModelHash", 0);
                        bool locked = GetBoolField(pv, "IsCollateralLocked", false);
                        int purchasePrice = GetIntField(pv, "PurchasePrice", 0);
                        string modelName = GetVehicleName(modelHash);
                        string priceText = locked ? string.Empty : FormatMoney(purchasePrice);
                        string desc = locked ? MollyCollateralLockedDesc : MollyVehicleItemDesc;

                        var item = new NativeItem(
                            string.Format(CultureInfo.InvariantCulture, "{0}. {1}", index, modelName),
                            desc,
                            priceText);

                        if (locked)
                        {
                            _mainLockedBadgeItems.Add(item);
                            SetRightBadgeSetIfExists(item, LockBadge);
                        }

                        object captured = pv;
                        item.Activated += (s, e) =>
                        {
                            QueueUiAction(() => OpenDetailMenu(captured));
                        };

                        _mainMenu.Add(item);
                        index++;
                    }
                    catch { }
                }
            }

            var quickSell = new NativeItem(MollyMainQuickSell, MollyMainQuickSellDesc);
            quickSell.Activated += (s, e) => QueueUiAction(OpenBulkSellMenu);
            _mainMenu.Add(quickSell);

            if (_mainMenu.Items.Count > 0)
            {
                try { _mainMenu.SelectedIndex = 0; } catch { }
            }

            UpdateConditionalLockBadges(_mainMenu, _mainLockedBadgeItems);
        }
        catch { }
    }

    private void BuildDetailMenu()
    {
        try
        {
            if (_detailMenuBuilt && _detailMenu != null)
                return;

            _detailMenu = new NativeMenu(MollyMenuTitle, MollyDetailSubtitle);
            _detailMenu.KeepNameCasing = true;
            _detailMenu.MaxItems = 8;
            _detailMenu.NoItemsText = MollyNoSuperVehicles;
            _menuPool.Add(_detailMenu);
            _detailMenuBuilt = true;
        }
        catch
        {
        }
    }

    private void RefreshDetailMenu()
    {
        try
        {
            if (_detailMenu == null)
                return;

            _detailMenu.Clear();
            _detailLockedBadgeItems.Clear();

            var pv = _selectedVehicle;
            if (pv == null)
            {
                _detailMenu.Add(new NativeItem(MollyNoCharacter, MollyNoCharacterDesc));
                return;
            }

            uint modelHash = GetUIntField(pv, "ModelHash", 0);
            string vehicleName = GetVehicleName(modelHash);
            int purchasePrice = GetIntField(pv, "PurchasePrice", 0);
            double refundRate = GetMollyRefundRate(pv);
            int refund = ComputeRefundByRate(purchasePrice, refundRate);
            int loss = Math.Max(0, purchasePrice - refund);
            string plateText = GetStringField(pv, "Plate", string.Empty);
            string ownerName = GetCharacterDisplayNameFromHash(GetIntField(pv, "OwnerModelHash", 0));
            string className = MollyVehicleClassSuper;
            bool locked = GetBoolField(pv, "IsCollateralLocked", false);

            UpdateStatsPanel(modelHash);

            var line1 = new NativeItem(
                string.Format(CultureInfo.InvariantCulture, MollyDetailVehicleNameLabel, vehicleName),
                string.Format(CultureInfo.InvariantCulture, MollyDetailPriceLabel, FormatMoney(purchasePrice)))
            {
                Panel = _statsPanel
            };

            if (locked)
            {
                _detailLockedBadgeItems.Add(line1);
                SetRightBadgeSetIfExists(line1, LockBadge);
            }
            _detailMenu.Add(line1);

            var line2 = new NativeItem(
                string.Format(CultureInfo.InvariantCulture, MollyDetailClassLabel, className),
                MollySuperOnlyDesc);
            _detailMenu.Add(line2);

            var line3 = new NativeItem(
                string.Format(CultureInfo.InvariantCulture, MollyDetailPlateLabel, string.IsNullOrWhiteSpace(plateText) ? MollyUnspecifiedText : plateText.Trim()),
                L("MollySchultz_PlateDesc", "Biển số xe hiện tại của phương tiện."));
            _detailMenu.Add(line3);

            var line4 = new NativeItem(
                string.Format(CultureInfo.InvariantCulture, MollyDetailOwnerLabel, ownerName),
                L("MollySchultz_OwnerDesc", "Chủ sở hữu của chiếc xe này trong danh sách persistent."));
            _detailMenu.Add(line4);

            var line5 = new NativeItem(
                string.Format(CultureInfo.InvariantCulture, MollyDetailPriceLabel, FormatMoney(refund)),
                string.Format(
                    CultureInfo.InvariantCulture,
                    L("MollySchultz_RefundRateDesc", "Molly đang áp dụng mức ~HUD_COLOUR_DEGEN_GREEN~{0}%~s~ giá gốc của xe."),
                    FormatRefundRate(refundRate)));
            _detailMenu.Add(line5);

            var line6 = new NativeItem(
                string.Format(CultureInfo.InvariantCulture, MollyDetailLossLabel, FormatMoney(loss)),
                L("MollySchultz_LossDesc", "Phần giá trị bị trừ đi so với giá gốc ban đầu."));
            _detailMenu.Add(line6);

            var sellDesc = locked ? MollyCollateralLockedDesc : MollyDetailSellDesc;
            var sellItem = new NativeItem(MollySellConfirm, sellDesc);
            sellItem.Activated += (s, e) =>
            {
                QueueUiAction(() =>
                {
                    SellVehicleWithRandomRefund(pv);
                    RefreshMainMenu();
                    if (_mainMenu != null) _mainMenu.Visible = true;
                    if (_detailMenu != null) _detailMenu.Visible = false;
                });
            };
            _detailMenu.Add(sellItem);

            var backItem = new NativeItem(MollyBackToList, L("MollySchultz_BackDesc", "Trở về danh sách phương tiện đã sở hữu"));
            backItem.Activated += (s, e) =>
            {
                QueueUiAction(() =>
                {
                    if (_detailMenu != null) _detailMenu.Visible = false;
                    if (_mainMenu != null) _mainMenu.Visible = true;
                });
            };
            _detailMenu.Add(backItem);

            UpdateConditionalLockBadges(_detailMenu, _detailLockedBadgeItems);
        }
        catch
        {
        }
    }

    private void BuildBulkMenu(bool forceRebuild = false)
    {
        try
        {
            if (!forceRebuild && _bulkMenuBuilt && _bulkMenu != null)
                return;

            _bulkMenu = new NativeMenu(MollyMenuTitle, MollyBulkSubtitle);
            _bulkMenu.KeepNameCasing = true;
            _bulkMenu.MaxItems = 8;
            _bulkMenu.NoItemsText = MollyNoSuperVehicles;
            _menuPool.Add(_bulkMenu);
            _bulkMenuBuilt = true;
        }
        catch
        {
        }
    }

    private void RefreshBulkMenu()
    {
        try
        {
            if (_bulkMenu == null)
                return;

            _bulkLockedBadgeItems.Clear();
            _bulkEntries.Clear();
            _bulkCheckboxItems.Clear();

            _bulkMenu.Clear();

            var owned = GetOwnedSuperVehiclesForCurrentPlayer();
            if (owned.Count == 0)
            {
                _bulkMenu.Add(new NativeItem(MollyNoVehiclesOwned, MollyNoSuperVehicles));
            }
            else
            {
                int index = 1;
                foreach (var pv in owned)
                {
                    bool locked = GetBoolField(pv, "IsCollateralLocked", false);
                    uint modelHash = GetUIntField(pv, "ModelHash", 0);
                    string modelName = GetVehicleName(modelHash);

                    if (locked)
                    {
                        var lockedItem = new NativeItem(modelName, MollyCollateralLockedDesc);
                        _bulkLockedBadgeItems.Add(lockedItem);
                        SetRightBadgeSetIfExists(lockedItem, LockBadge);
                        lockedItem.Activated += (s, e) => { };
                        _bulkMenu.Add(lockedItem);
                    }
                    else
                    {
                        int purchasePrice = GetIntField(pv, "PurchasePrice", 0);
                        double refundRate = GetMollyRefundRate(pv);
                        int refund = ComputeRefundByRate(purchasePrice, refundRate);
                        string desc = string.Format(CultureInfo.InvariantCulture,
                            L("MollySchultz_BulkVehicleDesc", "Chọn hoặc bỏ chọn chiếc xe này.\nGiá thanh lý dự kiến: {0}"),
                            FormatMoney(refund));

                        var item = new NativeCheckboxItem(modelName, desc, _bulkSelected.Contains(pv));
                        object captured = pv;
                        item.CheckboxChanged += (s, e) =>
                        {
                            if (_bulkUiSync)
                                return;

                            try
                            {
                                _bulkUiSync = true;

                                if (item.Checked)
                                    _bulkSelected.Add(captured);
                                else
                                    _bulkSelected.Remove(captured);

                                RecalculateBulkTotal();
                                RefreshBulkMenu();
                            }
                            finally
                            {
                                _bulkUiSync = false;
                            }
                        };

                        _bulkEntries.Add(pv);
                        _bulkCheckboxItems.Add(item);
                        _bulkMenu.Add(item);
                    }

                    index++;
                }
            }

            int total = RecalculateBulkTotal();

            var confirm = new NativeItem(MollyBulkConfirm, string.Format(CultureInfo.InvariantCulture,
                L("MollySchultz_BulkConfirmDesc", "Tổng số tiền thanh lý cho các xe đã chọn là ~HUD_COLOUR_DEGEN_GREEN~{0}~s~"),
                FormatMoney(total)));
            confirm.Activated += (s, e) => QueueUiAction(SellSelectedVehiclesQuickly);
            _bulkMenu.Add(confirm);

            var backItem = new NativeItem(MollyBulkBack, L("MollySchultz_BulkBackDesc", "Quay về danh sách phương tiện đã sở hữu"));
            backItem.Activated += (s, e) =>
            {
                QueueUiAction(() =>
                {
                    if (_bulkMenu != null) _bulkMenu.Visible = false;
                    if (_mainMenu != null) _mainMenu.Visible = true;
                });
            };
            _bulkMenu.Add(backItem);

            UpdateConditionalLockBadges(_bulkMenu, _bulkLockedBadgeItems);
        }
        catch
        {
        }
    }

    private int RecalculateBulkTotal()
    {
        try
        {
            int total = 0;
            foreach (var pv in _bulkSelected.ToList())
            {
                int purchasePrice = GetIntField(pv, "PurchasePrice", 0);
                double refundRate = GetMollyRefundRate(pv);
                total += ComputeRefundByRate(purchasePrice, refundRate);
            }
            return total;
        }
        catch
        {
            return 0;
        }
    }

    private void SellVehicleWithRandomRefund(object vehicleEntry)
    {
        try
        {
            if (vehicleEntry == null)
                return;

            bool locked = GetBoolField(vehicleEntry, "IsCollateralLocked", false);
            if (locked)
            {
                Notification.Show(MollyCollateralLockedDesc);
                return;
            }

            uint modelHash = GetUIntField(vehicleEntry, "ModelHash", 0);
            int purchasePrice = GetIntField(vehicleEntry, "PurchasePrice", 0);
            double refundRate = GetMollyRefundRate(vehicleEntry);
            int refund = ComputeRefundByRate(purchasePrice, refundRate);

            if (refund > 0)
                Game.Player.Money += refund;

            string vehicleName = GetVehicleName(modelHash);
            ShowMollyFeed(
                MollyMenuTitle,
                L("MollySchultz_FeedSubject", "Hoàn tiền phương tiện"),
                string.Format(CultureInfo.InvariantCulture,
                    L("MollySchultz_FeedBody", "Đã thanh lý ~HUD_COLOUR_DEGEN_YELLOW~{0}~s~ và nhận lại ~HUD_COLOUR_DEGEN_GREEN~+{1}~s~."),
                    vehicleName,
                    FormatMoney(refund)));

            RemovePersistentVehicleEntry(vehicleEntry);
            ForgetMollyRefundRate(vehicleEntry);
            MarkVehiclesDirty();

            BeginPostSaleMollyCleanup();
        }
        catch
        {
        }
    }

    private void SellSelectedVehiclesQuickly()
    {
        try
        {
            var selected = _bulkSelected.Where(v => v != null).ToList();
            if (selected.Count == 0)
            {
                Notification.Show(MollyNoSelection);
                return;
            }

            int totalRefund = 0;
            int soldCount = 0;

            foreach (var pv in selected)
            {
                try
                {
                    if (GetBoolField(pv, "IsCollateralLocked", false))
                        continue;

                    int purchasePrice = GetIntField(pv, "PurchasePrice", 0);
                    double refundRate = GetMollyRefundRate(pv);
                    totalRefund += ComputeRefundByRate(purchasePrice, refundRate);
                    soldCount++;
                }
                catch
                {
                }
            }

            if (totalRefund > 0)
                Game.Player.Money += totalRefund;

            foreach (var pv in selected)
            {
                try
                {
                    if (GetBoolField(pv, "IsCollateralLocked", false))
                        continue;

                    RemovePersistentVehicleEntry(pv);
                    ForgetMollyRefundRate(pv);
                }
                catch
                {
                }
            }

            _bulkSelected.Clear();
            MarkVehiclesDirty();

            ShowMollyFeed(
                MollyMenuTitle,
                MollySellSuccessSubject,
                string.Format(CultureInfo.InvariantCulture, MollySellSuccessBody, soldCount, FormatMoney(totalRefund)));

            BeginPostSaleMollyCleanup();
        }
        catch
        {
        }
    }

    private static void ShowMollyFeed(string sender, string subject, string body)
    {
        try
        {
            if (ShowFeedMessageMethod != null)
            {
                ShowFeedMessageMethod.Invoke(null, new object[] { sender, subject, body });
                return;
            }
        }
        catch
        {
        }

        try
        {
            Notification.Show(NotificationIcon.Molly, sender, subject, body);
        }
        catch
        {
            try
            {
                GTA.UI.Notification.Show(body);
            }
            catch
            {
            }
        }
    }

    private void QueueUiAction(Action action)
    {
        if (action == null)
            return;

        lock (_uiActions)
        {
            _uiActions.Enqueue(action);
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

                try
                {
                    next();
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private void UpdateConditionalLockBadges(NativeMenu menu, HashSet<NativeItem> lockedItems)
    {
        try
        {
            if (menu == null || lockedItems == null || lockedItems.Count == 0)
                return;

            int selectedIndex = -1;
            try { selectedIndex = menu.SelectedIndex; } catch { }

            for (int i = 0; i < menu.Items.Count; i++)
            {
                var item = menu.Items[i];
                if (item == null || !lockedItems.Contains(item))
                    continue;

                SetRightBadgeSetIfExists(item, i == selectedIndex ? null : LockBadge);
            }
        }
        catch
        {
        }
    }
}