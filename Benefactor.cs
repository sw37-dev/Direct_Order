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
using System.Reflection;
using System.Windows.Forms;

public class BenefactorScript : Script
{
    private static string L(string key, string fallback)
    {
        return Language.Get(key, fallback);
    }

    private static string BenefactorContactName => L("Benefactor_ContactName", "Benefactor");
    private static string BenefactorMenuTitle => L("Benefactor_MenuTitle", "Benefactor");
    private static string BenefactorMenuSubtitle => L("Benefactor_MenuSubtitle", "CHI TIẾT DỊCH VỤ THUÊ XE");
    private static string BenefactorConfirmTitle => L("Benefactor_ConfirmTitle", "Xác nhận thuê phương tiện");
    private static string BenefactorCancelTitle => L("Benefactor_CancelTitle", "Hủy bỏ dịch vụ thuê");
    private static string BenefactorFeeTitle => L("Benefactor_FeeTitle", "Số tiền thuê phương tiện: {0}");
    private static string BenefactorFeeNaText => L("Benefactor_FeeNaText", "N/A");
    private static string BenefactorNoVehiclesText => L("Benefactor_NoVehiclesText", "Không thể tải danh sách phương tiện.");
    private static string BenefactorChooseOneText => L("Benefactor_ChooseOneText", "Hãy chọn một phương tiện trước.");
    private static string BenefactorAlreadyActiveText => L("Benefactor_AlreadyActiveText", "Bạn đang có ~HUD_COLOUR_DEGEN_YELLOW~một chiếc thuê đang hoạt động~s~ rồi.");
    private static string BenefactorNotEnoughMoneyText => L("Benefactor_NotEnoughMoneyText", "Bạn không đủ tiền thuê phương tiện này.");
    private static string BenefactorSpawnFailedText => L("Benefactor_SpawnFailedText", "Không thể giao xe thuê.");
    private static string BenefactorRentalSpawnedText => L("Benefactor_RentalSpawnedText", "Chiếc phương tiện bạn thuê đã được đưa tới rồi nhé!");
    private static string BenefactorRentalExpiredText => L("Benefactor_RentalExpiredText", "Thời gian thuê xe đã kết thúc.");
    private static string BenefactorRentalLostNoticeText => L("Benefactor_RentalLostNoticeText",
         "Chiếc phương tiện mà bạn đã thuê từ công ty đâu rồi, sao không thấy trả lại? Nếu không trả sẽ phải đền bù một khoảng tiền ~HUD_COLOUR_DEGEN_YELLOW~{0}~s~ đấy!");

    private const int CONTACT_DIAL_TIMEOUT_MS = 2000;
    private const int RENT_CHECK_INTERVAL_MS = 30000;
    private const int RENT_DURATION_HOURS = 3;
    private const double RENT_FEE_PERCENT = 0.018;
    private const int RENTAL_SPAWN_BLIP_DURATION_MS = 10000;
    private const int RENT_DECAY_STOP_DELETE_DELAY_MS = 15000;

    private const int DAILY_RENTAL_POOL_SIZE = 12;
    private const int RENT_DECAY_DURATION_MS = 3000;

    private const int RENT_LOST_NOTICE_DELAY_MS = 5000;
    private const int RENT_LOST_PENALTY_DELAY_MS = 15000;
    private const double RENT_LOST_COMPENSATION_PERCENT = 0.75;
    private const int RENT_LOST_WANTED_STAR_GAIN = 2;
    private const int RENT_LOST_WANTED_STAR_MAX = 5;

    private int _rentedBasePrice = 0;
    private bool _rentalLossSequenceActive = false;
    private bool _rentalLossNoticeSent = false;
    private bool _rentalLossPenaltyApplied = false;
    private int _rentalLossNoticeDueGameTime = -1;
    private int _rentalLossPenaltyDueGameTime = -1;

    private static readonly Vector3 RENT_SPAWN_POS = new Vector3(562.4601f, -1437.666f, 29.03143f);
    private const float RENT_SPAWN_HEADING = 234.9772f;

    private readonly ObjectPool _menuPool = new ObjectPool();
    private NativeMenu _mainMenu;
    private bool _menuReady = false;

    private NativeCheckboxItem[] _vehicleItems = new NativeCheckboxItem[0];
    private NativeItem _feeItem;
    private NativeItem _confirmItem;
    private NativeItem _cancelItem;

    private bool _contactAdded = false;
    private bool _callPending = false;
    private int _callDueTime = 0;

    private bool _menuSync = false;

    private readonly Random _rng = new Random();

    private List<RentalOption> _currentOptions = new List<RentalOption>();
    private RentalOption _selectedOption = null;
    private int _selectedBasePrice = 0;
    private int _selectedFee = 0;

    private int _benefactorPoolDaySerial = -1;
    private bool _benefactorPoolBuiltForDay = false;
    private readonly HashSet<uint> _benefactorRentedHashesToday = new HashSet<uint>();

    private iFruitContact _benefactorContact = null;

    private Vector3 _rentalDecayStartVelocity = Vector3.Zero;
    private int _rentalDecayStartedAt = -1;

    private Vehicle _rentedVehicle = null;
    private DateTime? _rentalStartedAt = null;
    private DateTime? _rentalEndsAt = null;
    private bool _rentalExpiryTriggered = false;
    private int _lastRentalTimeCheckGameTime = -RENT_CHECK_INTERVAL_MS;
    private int _rentalCleanupStartGameTime = -1;

    private Blip _rentalSpawnBlip = null;
    private int _rentalSpawnBlipExpireAt = -1;

    private sealed class RentalOption
    {
        public InstantRefill.VehicleEntry Source;
        public int RolledBasePrice;
        public int RentalFee;

        public string DisplayName
        {
            get { return Source != null ? Source.Name : string.Empty; }
        }

        public uint Hash
        {
            get { return Source != null ? Source.Hash : 0u; }
        }
    }

    public BenefactorScript()
    {
        Interval = 1000;
        Tick += OnTick;
        KeyDown += OnKeyDown;
    }

    private void OnTick(object sender, EventArgs e)
    {
        try
        {
            if (Game.IsLoading || Game.IsCutsceneActive)
                return;

            SyncDailyRentalPoolState();
            UpdateBenefactorContactAvailability();

            EnsureContactRegistered();
            ProcessPendingCall();
            UpdateRentalLifecycle();
            UpdateRentalSpawnBlipLifecycle();

            bool menuVisible = _mainMenu != null && _mainMenu.Visible;
            bool needsFastTick = menuVisible || _callPending || _rentalExpiryTriggered;

            if (menuVisible)
                _menuPool.Process();

            Interval = needsFastTick ? 0 : 1000;
        }
        catch (Exception ex)
        {
            Log("OnTick failed: " + ex);
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            if (Game.IsLoading || Game.IsCutsceneActive)
                return;

            if (_mainMenu != null && _mainMenu.Visible)
            {
                if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
                {
                    CloseMenu();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log("OnKeyDown failed: " + ex);
        }
    }

    private void EnsureContactRegistered()
    {
        try
        {
            SyncDailyRentalPoolState();
            UpdateBenefactorContactAvailability();
        }
        catch (Exception ex)
        {
            Log("EnsureContactRegistered failed: " + ex);
        }
    }

    private void OnBenefactorAnswered(iFruitContact sender)
    {
        try
        {
            SyncDailyRentalPoolState();

            if (!HasAvailableRentalOptions())
            {
                UpdateBenefactorContactAvailability();
                return;
            }

            _callPending = true;
            _callDueTime = Game.GameTime + 1;
        }
        catch (Exception ex)
        {
            Log("OnBenefactorAnswered failed: " + ex);
        }
        finally
        {
            try { CustomiFruit.GetCurrentInstance()?.Close(0); } catch { }
        }
    }

    private void ProcessPendingCall()
    {
        try
        {
            if (!_callPending)
                return;

            if (Game.GameTime < _callDueTime)
                return;

            _callPending = false;

            SyncDailyRentalPoolState();

            if (!HasAvailableRentalOptions())
            {
                UpdateBenefactorContactAvailability();
                return;
            }

            OpenMenu();
        }
        catch (Exception ex)
        {
            Log("ProcessPendingCall failed: " + ex);
        }
    }

    private static int ComputeRentalCompensation(int basePrice)
    {
        try
        {
            if (basePrice <= 0)
                return 0;

            long amount = ((long)Math.Max(0, basePrice) * 75L) / 100L;
            return (int)Math.Max(0L, amount);
        }
        catch
        {
            return 0;
        }
    }

    private bool IsRentalVehicleDestroyedOrMissing()
    {
        try
        {
            if (_rentedVehicle == null)
                return true;

            bool exists = false;
            try { exists = _rentedVehicle.Exists(); } catch { exists = false; }
            if (!exists)
                return true;

            try
            {
                if (_rentedVehicle.IsDead)
                    return true;
            }
            catch { }

            try
            {
                if (_rentedVehicle.EngineHealth <= 0f || _rentedVehicle.BodyHealth <= 0f)
                    return true;
            }
            catch { }

            return false;
        }
        catch
        {
            return true;
        }
    }

    private void BeginLostRentalSequence()
    {
        try
        {
            _rentalExpiryTriggered = true;
            _rentalLossSequenceActive = true;
            _rentalLossNoticeSent = false;
            _rentalLossPenaltyApplied = false;
            _rentalLossNoticeDueGameTime = Game.GameTime + RENT_LOST_NOTICE_DELAY_MS;
            _rentalLossPenaltyDueGameTime = -1;
            _rentalCleanupStartGameTime = -1;
        }
        catch (Exception ex)
        {
            Log("BeginLostRentalSequence failed: " + ex);
        }
    }

    private void UpdateLostRentalSequence()
    {
        try
        {
            if (!_rentalLossSequenceActive)
                return;

            if (_rentalLossPenaltyApplied)
                return;

            if (!_rentalLossNoticeSent)
            {
                if (Game.GameTime < _rentalLossNoticeDueGameTime)
                    return;

                int compensation = ComputeRentalCompensation(_rentedBasePrice);

                ShowBenefactorNotification(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        BenefactorRentalLostNoticeText,
                        FormatMoney(compensation)));

                _rentalLossNoticeSent = true;
                _rentalLossPenaltyDueGameTime = Game.GameTime + RENT_LOST_PENALTY_DELAY_MS;
                return;
            }

            if (Game.GameTime < _rentalLossPenaltyDueGameTime)
                return;

            ApplyLostRentalPenalty();
            _rentalLossPenaltyApplied = true;

            ClearRentalState();
        }
        catch (Exception ex)
        {
            Log("UpdateLostRentalSequence failed: " + ex);
        }
    }

    private void ApplyLostRentalPenalty()
    {
        try
        {
            int compensation = ComputeRentalCompensation(_rentedBasePrice);
            if (compensation <= 0)
                return;

            if (Game.Player.Money >= compensation)
            {
                Game.Player.Money -= compensation;
                return;
            }

            try
            {
                int currentMoney = 0;
                try { currentMoney = Math.Max(0, Game.Player.Money); } catch { currentMoney = 0; }

                int moneyToRemove = currentMoney / 2;
                int remainingMoney = currentMoney - moneyToRemove;

                Game.Player.Money = remainingMoney;

                int currentWanted = 0;
                try { currentWanted = Game.Player.WantedLevel; } catch { }

                int newWanted = Math.Min(RENT_LOST_WANTED_STAR_MAX, currentWanted + RENT_LOST_WANTED_STAR_GAIN);

                try { Game.Player.WantedLevel = newWanted; } catch { }

                try
                {
                    Function.Call(Hash.SET_PLAYER_WANTED_LEVEL, Game.Player, newWanted, false);
                    Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, Game.Player, false);
                }
                catch { }
            }
            catch (Exception ex)
            {
                Log("ApplyLostRentalPenalty wanted-level failed: " + ex);
            }
        }
        catch (Exception ex)
        {
            Log("ApplyLostRentalPenalty failed: " + ex);
        }
    }

    private void OpenMenu()
    {
        try
        {
            SyncDailyRentalPoolState();

            if (!HasAvailableRentalOptions())
            {
                UpdateBenefactorContactAvailability();
                GTA.UI.Screen.ShowSubtitle(BenefactorNoVehiclesText, 2500);
                return;
            }

            EnsureMainMenuCreated();
            BuildMenu();

            if (_mainMenu != null)
            {
                _mainMenu.Visible = true;
                Interval = 0;
            }
        }
        catch (Exception ex)
        {
            Log("OpenMenu failed: " + ex);
        }
    }

    private void CloseMenu()
    {
        try
        {
            if (_mainMenu != null)
                _mainMenu.Visible = false;
        }
        catch { }
    }

    private void EnsureMainMenuCreated()
    {
        try
        {
            if (_menuReady)
                return;

            _mainMenu = new NativeMenu(BenefactorMenuTitle, BenefactorMenuSubtitle);
            _menuPool.Add(_mainMenu);
            ConfigureKeyboardOnlyMenu(_mainMenu);
            _menuReady = true;
        }
        catch (Exception ex)
        {
            Log("EnsureMainMenuCreated failed: " + ex);
        }
    }

    private void ConfigureKeyboardOnlyMenu(NativeMenu menu)
    {
        if (menu == null)
            return;

        menu.MouseBehavior = MenuMouseBehavior.Disabled;
        menu.ResetCursorWhenOpened = false;
        menu.CloseOnInvalidClick = false;
        menu.RotateCamera = true;
    }

    private static string FormatMoney(int value)
    {
        if (value < 0)
            value = 0;

        return string.Format(CultureInfo.InvariantCulture, "${0:N0}", value);
    }

    private static int ComputeRentalFee(int basePrice)
    {
        try
        {
            if (basePrice <= 0)
                return 0;

            decimal fee = (decimal)basePrice * (decimal)RENT_FEE_PERCENT;
            return (int)Math.Round(fee, MidpointRounding.AwayFromZero);
        }
        catch
        {
            return 0;
        }
    }

    private static void ShuffleList<T>(IList<T> list, Random rng)
    {
        if (list == null || rng == null)
            return;

        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            if (j == i)
                continue;

            T temp = list[i];
            list[i] = list[j];
            list[j] = temp;
        }
    }

    private int GetCurrentInGameDaySerial()
    {
        try
        {
            DateTime dt = GetCurrentInGameDateTime();
            return dt.Year * 10000 + dt.Month * 100 + dt.Day;
        }
        catch
        {
            return -1;
        }
    }

    private void SyncDailyRentalPoolState()
    {
        try
        {
            int daySerial = GetCurrentInGameDaySerial();
            if (daySerial < 0)
                return;

            if (_benefactorPoolBuiltForDay && _benefactorPoolDaySerial == daySerial)
                return;

            _benefactorPoolDaySerial = daySerial;
            _benefactorPoolBuiltForDay = true;

            _benefactorRentedHashesToday.Clear();
            _currentOptions = BuildDailyRentalOptions(daySerial);

            _selectedOption = null;
            _selectedBasePrice = 0;
            _selectedFee = 0;

            UpdateFeeItem();
        }
        catch (Exception ex)
        {
            Log("SyncDailyRentalPoolState failed: " + ex);
        }
    }

    private List<RentalOption> BuildDailyRentalOptions(int daySerial)
    {
        try
        {
            var source = GetVehicleSourceList();
            if (source == null || source.Count == 0)
                return new List<RentalOption>();

            var pool = source.Where(v => v != null).ToList();

            int seed = unchecked(daySerial * 73856093 ^ 0x6B1D2D47);
            var dailyRng = new Random(seed);

            ShuffleList(pool, dailyRng);

            int count = Math.Min(DAILY_RENTAL_POOL_SIZE, pool.Count);
            var result = new List<RentalOption>(count);

            for (int i = 0; i < count; i++)
            {
                var entry = pool[i];
                if (entry == null)
                    continue;

                result.Add(new RentalOption
                {
                    Source = entry,
                    RolledBasePrice = 0,
                    RentalFee = 0
                });
            }

            return result;
        }
        catch (Exception ex)
        {
            Log("BuildDailyRentalOptions failed: " + ex);
            return new List<RentalOption>();
        }
    }

    private void UpdateBenefactorContactAvailability()
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            bool hasOptions = _currentOptions != null && _currentOptions.Count > 0;
            string contactName = BenefactorContactName;

            var existing = phone.Contacts.FirstOrDefault(c =>
                string.Equals(c.Name, contactName, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                _benefactorContact = existing;
                existing.Active = hasOptions;
                _contactAdded = true;
                return;
            }

            if (!hasOptions)
                return;

            var contact = new iFruitContact(contactName)
            {
                Active = true,
                DialTimeout = CONTACT_DIAL_TIMEOUT_MS,
                Bold = false,
                Icon = ContactIcon.Bikesite
            };

            contact.Answered += OnBenefactorAnswered;
            phone.Contacts.Add(contact);

            _benefactorContact = contact;
            _contactAdded = true;
        }
        catch (Exception ex)
        {
            Log("UpdateBenefactorContactAvailability failed: " + ex);
        }
    }

    private void ConsumeDailyRentalOption(uint hash)
    {
        try
        {
            if (hash == 0)
                return;

            _benefactorRentedHashesToday.Add(hash);

            if (_currentOptions != null && _currentOptions.Count > 0)
                _currentOptions.RemoveAll(o => o != null && o.Hash == hash);

            if (_selectedOption != null && _selectedOption.Hash == hash)
                ClearSelection();

            UpdateBenefactorContactAvailability();
        }
        catch (Exception ex)
        {
            Log("ConsumeDailyRentalOption failed: " + ex);
        }
    }

    private bool HasAvailableRentalOptions()
    {
        SyncDailyRentalPoolState();
        return _currentOptions != null && _currentOptions.Count > 0;
    }

    private List<InstantRefill.VehicleEntry> GetVehicleSourceList()
    {
        try
        {
            var inst = InstantRefill.Instance;
            if (inst == null)
                return new List<InstantRefill.VehicleEntry>();

            FieldInfo field = typeof(InstantRefill).GetField("_vehicles", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
                return new List<InstantRefill.VehicleEntry>();

            object value = field.GetValue(inst);
            var list = value as List<InstantRefill.VehicleEntry>;
            if (list == null)
                return new List<InstantRefill.VehicleEntry>();

            return list.Where(v => v != null).ToList();
        }
        catch (Exception ex)
        {
            Log("GetVehicleSourceList failed: " + ex);
            return new List<InstantRefill.VehicleEntry>();
        }
    }

    private void BuildMenu()
    {
        try
        {
            if (_mainMenu == null)
                return;

            SyncDailyRentalPoolState();

            _mainMenu.Clear();
            _currentOptions = _currentOptions ?? new List<RentalOption>();
            _selectedOption = null;
            _selectedBasePrice = 0;
            _selectedFee = 0;

            if (_currentOptions.Count == 0)
            {
                _mainMenu.Add(new NativeItem(BenefactorNoVehiclesText));
                return;
            }

            _vehicleItems = new NativeCheckboxItem[_currentOptions.Count];

            for (int i = 0; i < _currentOptions.Count; i++)
            {
                int index = i;
                var option = _currentOptions[i];
                string title = string.Format(CultureInfo.InvariantCulture, "{0}. {1}", i + 1, option.DisplayName);
                var item = new NativeCheckboxItem(title, false);
                item.Description = option.Source != null
                    ? string.Format(CultureInfo.InvariantCulture, "{0} | {1}", option.Source.Class ?? string.Empty, option.Source.Label ?? string.Empty)
                    : string.Empty;

                item.CheckboxChanged += (s, e) =>
                {
                    HandleSelectionChanged(index, item);
                };

                _vehicleItems[i] = item;
                _mainMenu.Add(item);
            }

            _feeItem = new NativeItem(string.Format(CultureInfo.InvariantCulture, BenefactorFeeTitle, BenefactorFeeNaText));
            _confirmItem = new NativeItem(BenefactorConfirmTitle);
            _cancelItem = new NativeItem(BenefactorCancelTitle);

            _confirmItem.Activated += (s, e) =>
            {
                ConfirmRental();
            };

            _cancelItem.Activated += (s, e) =>
            {
                CloseMenu();
            };

            _mainMenu.Add(_feeItem);
            _mainMenu.Add(_confirmItem);
            _mainMenu.Add(_cancelItem);
        }
        catch (Exception ex)
        {
            Log("BuildMenu failed: " + ex);
        }
    }

    private void HandleSelectionChanged(int index, NativeCheckboxItem item)
    {
        try
        {
            if (_menuSync)
                return;

            if (_currentOptions == null || index < 0 || index >= _currentOptions.Count)
                return;

            _menuSync = true;

            if (item != null && item.Checked)
            {
                for (int i = 0; i < _vehicleItems.Length; i++)
                {
                    if (i == index)
                        continue;

                    if (_vehicleItems[i] != null && _vehicleItems[i].Checked)
                        _vehicleItems[i].Checked = false;
                }

                var selected = _currentOptions[index];
                if (selected != null && selected.Source != null)
                {
                    _selectedOption = selected;
                    _selectedBasePrice = selected.Source.GetRandomPrice(_rng, false, 0);
                    _selectedFee = ComputeRentalFee(_selectedBasePrice);
                    selected.RolledBasePrice = _selectedBasePrice;
                    selected.RentalFee = _selectedFee;
                    UpdateFeeItem();
                }
                else
                {
                    ClearSelection();
                }
            }
            else
            {
                var selected = _selectedOption;
                if (selected != null && selected.Source != null && selected.Hash == _currentOptions[index].Hash)
                {
                    ClearSelection();
                }
            }
        }
        catch (Exception ex)
        {
            Log("HandleSelectionChanged failed: " + ex);
        }
        finally
        {
            _menuSync = false;
        }
    }

    private void ClearSelection()
    {
        _selectedOption = null;
        _selectedBasePrice = 0;
        _selectedFee = 0;
        UpdateFeeItem();
    }

    private void UpdateFeeItem()
    {
        try
        {
            if (_feeItem == null)
                return;

            if (_selectedOption == null || _selectedFee <= 0)
            {
                _feeItem.Title = string.Format(CultureInfo.InvariantCulture, BenefactorFeeTitle, BenefactorFeeNaText);
                return;
            }

            _feeItem.Title = string.Format(CultureInfo.InvariantCulture, BenefactorFeeTitle, FormatMoney(_selectedFee));
        }
        catch (Exception ex)
        {
            Log("UpdateFeeItem failed: " + ex);
        }
    }

    private void ConfirmRental()
    {
        try
        {
            if (_selectedOption == null || _selectedOption.Source == null)
            {
                GTA.UI.Screen.ShowSubtitle(BenefactorChooseOneText, 2500);
                return;
            }

            if (IsRentalActive())
            {
                GTA.UI.Screen.ShowSubtitle(BenefactorAlreadyActiveText, 2500);
                return;
            }

            if (_selectedFee <= 0)
            {
                GTA.UI.Screen.ShowSubtitle(BenefactorChooseOneText, 2500);
                return;
            }

            if (Game.Player.Money < _selectedFee)
            {
                GTA.UI.Screen.ShowSubtitle(BenefactorNotEnoughMoneyText, 2500);
                return;
            }

            uint rentedHash = _selectedOption.Hash;

            if (!TrySpawnRentalVehicle(_selectedOption.Source, out Vehicle vehicle))
            {
                GTA.UI.Screen.ShowSubtitle(BenefactorSpawnFailedText, 2500);
                return;
            }

            Game.Player.Money -= _selectedFee;
            _rentedVehicle = vehicle;
            _rentedBasePrice = Math.Max(0, _selectedBasePrice);

            _rentalLossSequenceActive = false;
            _rentalLossNoticeSent = false;
            _rentalLossPenaltyApplied = false;
            _rentalLossNoticeDueGameTime = -1;
            _rentalLossPenaltyDueGameTime = -1;

            ConsumeDailyRentalOption(rentedHash);

            _rentalStartedAt = GetCurrentInGameDateTime();
            _rentalEndsAt = _rentalStartedAt.Value.AddHours(RENT_DURATION_HOURS);
            _rentalExpiryTriggered = false;
            _rentalCleanupStartGameTime = -1;
            _lastRentalTimeCheckGameTime = Game.GameTime;

            CloseMenu();
            ShowBenefactorNotification(BenefactorRentalSpawnedText);
            Interval = 0;
        }
        catch (Exception ex)
        {
            Log("ConfirmRental failed: " + ex);
        }
    }

    private bool TrySpawnRentalVehicle(InstantRefill.VehicleEntry entry, out Vehicle spawned)
    {
        spawned = null;

        try
        {
            if (entry == null)
                return false;

            Model model = new Model((int)entry.Hash);
            if (!model.IsValid || !model.IsInCdImage)
                return false;

            if (!model.IsLoaded)
            {
                model.Request(1000);
                int waited = 0;
                while (!model.IsLoaded && waited < 3000)
                {
                    Script.Wait(50);
                    waited += 50;
                }
            }

            if (!model.IsLoaded)
                return false;

            Vehicle veh = World.CreateVehicle(model, RENT_SPAWN_POS, RENT_SPAWN_HEADING);
            if (veh == null || !veh.Exists())
                return false;

            try
            {
                veh.PlaceOnGround();
                veh.Repair();
                veh.DirtLevel = 0f;
                veh.IsEngineRunning = true;
                veh.IsPersistent = false;
                veh.LockStatus = VehicleLockStatus.Unlocked;
            }
            catch { }

            try
            {
                Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, veh.Handle, true, true);
            }
            catch { }

            CreateRentalSpawnBlip(veh);

            spawned = veh;
            return true;
        }
        catch (Exception ex)
        {
            Log("TrySpawnRentalVehicle failed: " + ex);
            spawned = null;
            return false;
        }
    }

    private void CreateRentalSpawnBlip(Vehicle veh)
    {
        try
        {
            ClearRentalSpawnBlip();

            if (veh == null || !veh.Exists())
                return;

            Blip blip = veh.AddBlip();
            if (blip == null || !blip.Exists())
                return;

            blip.Sprite = BlipSprite.Standard;
            blip.Color = BlipColor.Yellow;
            blip.IsShortRange = false;
            blip.Name = L("Benefactor_RentalBlipName", "Xe thuê");

            _rentalSpawnBlip = blip;
            _rentalSpawnBlipExpireAt = Game.GameTime + RENTAL_SPAWN_BLIP_DURATION_MS;
        }
        catch
        {
            ClearRentalSpawnBlip();
        }
    }

    private void UpdateRentalSpawnBlipLifecycle()
    {
        try
        {
            if (_rentalSpawnBlip == null)
                return;

            if (Game.GameTime < _rentalSpawnBlipExpireAt)
            {
                if (_rentedVehicle == null || !_rentedVehicle.Exists())
                    ClearRentalSpawnBlip();

                return;
            }

            ClearRentalSpawnBlip();
        }
        catch
        {
            ClearRentalSpawnBlip();
        }
    }

    private void ClearRentalSpawnBlip()
    {
        try
        {
            if (_rentalSpawnBlip != null)
            {
                try
                {
                    if (_rentalSpawnBlip.Exists())
                        _rentalSpawnBlip.Delete();
                }
                catch { }
            }
        }
        catch { }
        finally
        {
            _rentalSpawnBlip = null;
            _rentalSpawnBlipExpireAt = -1;
        }
    }

    private DateTime GetCurrentInGameDateTime()
    {
        try
        {
            int year = SafeCallInt(() => Function.Call<int>(Hash.GET_CLOCK_YEAR), 2000);
            int month = SafeCallInt(() => Function.Call<int>(Hash.GET_CLOCK_MONTH), 1);
            int day = SafeCallInt(() => Function.Call<int>(Hash.GET_CLOCK_DAY_OF_MONTH), 1);
            int hour = SafeCallInt(() => Function.Call<int>(Hash.GET_CLOCK_HOURS), 0);
            int minute = SafeCallInt(() => Function.Call<int>(Hash.GET_CLOCK_MINUTES), 0);

            if (month < 1 || month > 12)
                month += 1;

            if (year < 1) year = 2000;
            if (month < 1) month = 1;
            if (month > 12) month = 12;

            int maxDay = DateTime.DaysInMonth(year, month);
            if (day < 1) day = 1;
            if (day > maxDay) day = maxDay;
            if (hour < 0) hour = 0;
            if (hour > 23) hour = 23;
            if (minute < 0) minute = 0;
            if (minute > 59) minute = 59;

            return new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        }
        catch
        {
            return new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        }
    }

    private static int SafeCallInt(Func<int> fn, int fallback)
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

    private bool IsRentalActive()
    {
        try
        {
            return _rentalStartedAt.HasValue && _rentalEndsAt.HasValue;
        }
        catch
        {
            return false;
        }
    }

    private void UpdateRentalLifecycle()
    {
        try
        {
            if (!IsRentalActive())
            {
                _rentalExpiryTriggered = false;
                _rentalLossSequenceActive = false;
                _rentalLossNoticeSent = false;
                _rentalLossPenaltyApplied = false;
                _rentalLossNoticeDueGameTime = -1;
                _rentalLossPenaltyDueGameTime = -1;
                _rentalCleanupStartGameTime = -1;
                return;
            }

            if (!_rentalEndsAt.HasValue)
                return;

            if (!_rentalExpiryTriggered)
            {
                if (Game.GameTime - _lastRentalTimeCheckGameTime < RENT_CHECK_INTERVAL_MS)
                    return;

                _lastRentalTimeCheckGameTime = Game.GameTime;

                DateTime now = GetCurrentInGameDateTime();
                if (now >= _rentalEndsAt.Value)
                    TriggerRentalExpiry();
            }

            if (_rentalLossSequenceActive)
            {
                UpdateLostRentalSequence();
                return;
            }

            if (_rentalExpiryTriggered)
            {
                UpdateExpiredVehicleDecay();
            }
        }
        catch (Exception ex)
        {
            Log("UpdateRentalLifecycle failed: " + ex);
        }
    }

    private void TriggerRentalExpiry()
    {
        try
        {
            if (_rentedVehicle == null)
            {
                ClearRentalState();
                return;
            }

            if (IsRentalVehicleDestroyedOrMissing())
            {
                BeginLostRentalSequence();
                return;
            }

            _rentalExpiryTriggered = true;
            _rentalCleanupStartGameTime = -1;

            _rentalDecayStartedAt = Game.GameTime;
            _rentalDecayStartVelocity = _rentedVehicle.Velocity;

            try
            {
                Function.Call(Hash.SET_VEHICLE_UNDRIVEABLE, _rentedVehicle.Handle, true);
            }
            catch { }

            try
            {
                Function.Call(Hash.SET_VEHICLE_ENGINE_ON, _rentedVehicle.Handle, false, true, true);
            }
            catch { }

            try
            {
                Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED, _rentedVehicle.Handle, 2);
            }
            catch { }

            GTA.UI.Screen.ShowSubtitle(BenefactorRentalExpiredText, 3000);
        }
        catch (Exception ex)
        {
            Log("TriggerRentalExpiry failed: " + ex);
        }
    }

    private void UpdateExpiredVehicleDecay()
    {
        try
        {
            if (_rentedVehicle == null)
            {
                ClearRentalState();
                return;
            }

            if (IsRentalVehicleDestroyedOrMissing())
            {
                BeginLostRentalSequence();
                return;
            }

            try
            {
                Function.Call(Hash.SET_VEHICLE_UNDRIVEABLE, _rentedVehicle.Handle, true);
                Function.Call(Hash.SET_VEHICLE_ENGINE_ON, _rentedVehicle.Handle, false, true, true);
                Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED, _rentedVehicle.Handle, 2);
            }
            catch { }

            if (_rentalDecayStartedAt < 0)
            {
                _rentalDecayStartedAt = Game.GameTime;
                _rentalDecayStartVelocity = _rentedVehicle.Velocity;
            }

            float elapsed = Game.GameTime - _rentalDecayStartedAt;
            float t = Math.Max(0f, Math.Min(1f, elapsed / (float)RENT_DECAY_DURATION_MS));

            float factor = 1f - t;
            factor = factor * factor;

            try
            {
                _rentedVehicle.Velocity = _rentalDecayStartVelocity * factor;
            }
            catch { }

            bool playerInsideRental = false;
            try
            {
                Ped player = Game.Player.Character;
                if (player != null && player.Exists() && player.CurrentVehicle != null && player.CurrentVehicle.Exists() && player.CurrentVehicle.Handle == _rentedVehicle.Handle)
                    playerInsideRental = true;
            }
            catch { }

            if (t >= 1f || _rentedVehicle.Velocity.Length() <= 0.03f)
            {
                try
                {
                    _rentedVehicle.Velocity = Vector3.Zero;
                }
                catch { }
            }

            if (!playerInsideRental)
            {
                if (_rentalCleanupStartGameTime < 0)
                    _rentalCleanupStartGameTime = Game.GameTime;

                if (Game.GameTime - _rentalCleanupStartGameTime >= RENT_DECAY_STOP_DELETE_DELAY_MS)
                {
                    CleanupRentedVehicle();
                }
            }
            else
            {
                _rentalCleanupStartGameTime = Game.GameTime;
            }
        }
        catch (Exception ex)
        {
            Log("UpdateExpiredVehicleDecay failed: " + ex);
        }
    }

    private void CleanupRentedVehicle()
    {
        try
        {
            ClearRentalSpawnBlip();

            if (_rentedVehicle != null)
            {
                try
                {
                    if (_rentedVehicle.Exists())
                    {
                        try { _rentedVehicle.MarkAsNoLongerNeeded(); } catch { }
                        try { _rentedVehicle.Delete(); } catch { }
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log("CleanupRentedVehicle failed: " + ex);
        }
        finally
        {
            ClearRentalState();
        }
    }

    private void ClearRentalState()
    {
        ClearRentalSpawnBlip();
        _rentedVehicle = null;
        _rentalStartedAt = null;
        _rentalEndsAt = null;
        _rentalExpiryTriggered = false;
        _rentalCleanupStartGameTime = -1;
        _rentalDecayStartedAt = -1;
        _rentalDecayStartVelocity = Vector3.Zero;

        _rentedBasePrice = 0;
        _rentalLossSequenceActive = false;
        _rentalLossNoticeSent = false;
        _rentalLossPenaltyApplied = false;
        _rentalLossNoticeDueGameTime = -1;
        _rentalLossPenaltyDueGameTime = -1;
    }

    private void ShowBenefactorNotification(string message)
    {
        try
        {
            Notification.Show(NotificationIcon.Bikesite, BenefactorContactName, L("Benefactor_NotificationTitle", "Dịch vụ thuê xe"), message);
        }
        catch
        {
            try { GTA.UI.Screen.ShowSubtitle(message, 3000); } catch { }
        }
    }

    private static void Log(string message)
    {
        try
        {
            // Keep silent by default.
            // File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Benefactor.log"), DateTime.Now.ToString("s") + " " + message + Environment.NewLine);
        }
        catch { }
    }
}