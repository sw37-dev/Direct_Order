using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using iFruitAddon2;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

public class Chop : Script
{
    private static string L(string key, string fallback)
    {
        return Language.Get(key, fallback);
    }

    private static string ChopContactName => L("Chop_ContactName", "Chop the Pup");
    private static string ChopNotificationTitle => L("Chop_NotificationTitle", "Thông báo");
    private static string ChopBarkDeniedText => L("Chop_BarkDeniedText", "Gâu gâu, gâu gâu!!");
    private static string ChopBarkMissText => L("Chop_BarkMissText", "Gâu gâu :(");
    private static string ChopBarkFoundText => L("Chop_BarkFoundText", "Gâu gâu, GÂUUU!!");

    private const int TickIntervalMs = 250;
    private const float ReachClearRadius = 8.0f;
    private const double TargetRevealChance = 0.33;

    private static Chop _instance;

    private CustomiFruit _phoneInstance;
    private iFruitContact _chopContact;
    private bool _chopContactBound;

    private bool _contactEnabledCached;

    private int _lastSeenVioletStartGameTime = -1;
    private bool _violetEventActive = false;
    private bool _violetIconsEnabled = false;
    private bool _usedThisVioletEvent = false;

    private static readonly string ChopDataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "Chop");

    private static readonly string ChopDailyStateFilePrefix = "chop_daily_state_";

    private int _chopDailyStateOwnerHash = 0;
    private int _chopDailyStateDayKey = -1;
    private bool _usedToday = false;

    private Vehicle _revealedTargetVehicle = null;
    private Blip _revealedTargetBlip = null;
    private bool _routeClearedNearTarget = false;

    private readonly Random _rng = new Random();

    private static readonly Type VioletType = typeof(Violet);

    private static readonly FieldInfo VioletSingletonField =
        VioletType.GetField("_singleton", BindingFlags.Static | BindingFlags.NonPublic);

    private static readonly FieldInfo VioletTrialActiveField =
        VioletType.GetField("_trialActive", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo VioletTrialIconsEnabledField =
        VioletType.GetField("_violetTrialIconsEnabled", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo VioletTrialStartGameTimeField =
        VioletType.GetField("_trialStartGameTime", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo VioletTrialVehiclesField =
        VioletType.GetField("_trialVehicles", BindingFlags.Instance | BindingFlags.NonPublic);

    public Chop()
    {
        if (_instance != null)
            return;

        _instance = this;

        Interval = TickIntervalMs;
        Tick += OnTick;

        EnsureChopContactRegistered();
    }

    private void OnTick(object sender, EventArgs e)
    {
        try
        {
            if (Game.IsLoading || Game.IsCutsceneActive)
                return;

            EnsureChopContactRegistered();
            SyncChopDailyState();
            SyncVioletEventState();
            UpdateContactAvailability();
            MonitorRevealedTargetProximity();
        }
        catch
        {
        }
    }

    private void EnsureChopContactRegistered()
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            if (!ReferenceEquals(_phoneInstance, phone))
            {
                _phoneInstance = phone;
                _chopContact = null;
                _chopContactBound = false;
            }

            if (_chopContact == null)
            {
                _chopContact = phone.Contacts.FirstOrDefault(c =>
                    c != null &&
                    string.Equals(c.Name, ChopContactName, StringComparison.OrdinalIgnoreCase));
            }

            if (_chopContact == null)
            {
                _chopContact = new iFruitContact(ChopContactName)
                {
                    Active = false,
                    DialTimeout = 2000,
                    Bold = false,
                    Icon = ContactIcon.Chop
                };

                _chopContact.Answered += OnChopAnswered;
                phone.Contacts.Add(_chopContact);
                _chopContactBound = true;
                return;
            }

            if (!_chopContactBound)
            {
                _chopContact.Answered += OnChopAnswered;
                _chopContactBound = true;
            }
        }
        catch
        {
        }
    }

    private void SyncVioletEventState()
    {
        try
        {
            object violetInstance = GetVioletInstance();
            bool active = violetInstance != null && GetBoolField(violetInstance, VioletTrialActiveField, false);

            if (!active)
            {
                if (_violetEventActive)
                    ResetChopState(deleteBlip: true);

                _violetEventActive = false;
                _violetIconsEnabled = false;
                _lastSeenVioletStartGameTime = -1;
                return;
            }

            int startGameTime = GetIntField(violetInstance, VioletTrialStartGameTimeField, -1);
            bool iconsEnabled = GetBoolField(violetInstance, VioletTrialIconsEnabledField, false);

            if (!_violetEventActive || _lastSeenVioletStartGameTime != startGameTime)
            {
                ResetChopState(deleteBlip: true);
                _usedThisVioletEvent = false;
                _lastSeenVioletStartGameTime = startGameTime;
            }

            _violetEventActive = true;
            _violetIconsEnabled = iconsEnabled;
        }
        catch
        {
            _violetEventActive = false;
            _violetIconsEnabled = false;
        }
    }

    private void UpdateContactAvailability()
    {
        try
        {
            bool shouldBeActive = CanUseChopNow();

            if (_contactEnabledCached == shouldBeActive)
                return;

            _contactEnabledCached = shouldBeActive;
            SetContactActive(shouldBeActive);
        }
        catch
        {
        }
    }

    private void OnChopAnswered(iFruitContact sender)
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone != null)
            {
                try { phone.Close(0); } catch { }
            }

            SyncChopDailyState();
            SyncVioletEventState();

            if (!CanUseChopNow())
            {
                SetContactActive(false);
                return;
            }

            SetContactActive(false);

            if (_violetIconsEnabled)
            {
                ShowChopMessage(ChopBarkDeniedText);
                return;
            }

            if (_rng.NextDouble() > TargetRevealChance)
            {
                ShowChopMessage(ChopBarkMissText);
                return;
            }

            _usedThisVioletEvent = true;
            _usedToday = true;
            SaveChopDailyStateForCurrentCharacter();

            if (!TryRevealRandomHiddenVioletVehicle())
            {
                ShowChopMessage(ChopBarkMissText);
                return;
            }

            ShowChopMessage(ChopBarkFoundText);
        }
        catch
        {
            try { SetContactActive(false); } catch { }
        }
    }

    private static string GetChopDailyStateFile(int ownerHash)
    {
        return Path.Combine(ChopDataRoot, $"{ChopDailyStateFilePrefix}{ownerHash}.dat");
    }

    private int GetCurrentCharacterHash()
    {
        try
        {
            Ped p = Game.Player.Character;
            if (p == null || !p.Exists())
                return 0;

            return p.Model.Hash;
        }
        catch
        {
            return 0;
        }
    }

    private static int GetCurrentGameDayKey()
    {
        try
        {
            int year = Function.Call<int>(Hash.GET_CLOCK_YEAR);
            int month = Function.Call<int>(Hash.GET_CLOCK_MONTH);
            int day = Function.Call<int>(Hash.GET_CLOCK_DAY_OF_MONTH);

            if (month < 1 || month > 12) month += 1;
            if (year < 1) year = 1;
            month = Math.Max(1, Math.Min(12, month));

            int maxDay = DateTime.DaysInMonth(year, month);
            day = Math.Max(1, Math.Min(maxDay, day));

            return (year * 10000) + (month * 100) + day;
        }
        catch
        {
            return -1;
        }
    }

    private void SyncChopDailyState()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            int dayKey = GetCurrentGameDayKey();

            if (ownerHash == 0 || dayKey == -1)
                return;

            if (_chopDailyStateOwnerHash != ownerHash)
            {
                _chopDailyStateOwnerHash = ownerHash;
                _chopDailyStateDayKey = -1;
                _usedToday = false;
                LoadChopDailyStateForCurrentCharacter();
                return;
            }

            if (_chopDailyStateDayKey != dayKey)
            {
                _chopDailyStateDayKey = dayKey;
                _usedToday = false;
                LoadChopDailyStateForCurrentCharacter();
            }
        }
        catch
        {
        }
    }

    private void LoadChopDailyStateForCurrentCharacter()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            int dayKey = GetCurrentGameDayKey();

            _usedToday = false;

            if (ownerHash == 0 || dayKey == -1)
                return;

            _chopDailyStateOwnerHash = ownerHash;
            _chopDailyStateDayKey = dayKey;

            string file = GetChopDailyStateFile(ownerHash);
            if (!File.Exists(file))
                return;

            foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                int idx = raw.IndexOf('=');
                if (idx <= 0)
                    continue;

                string key = raw.Substring(0, idx).Trim().ToLowerInvariant();
                string val = raw.Substring(idx + 1).Trim();

                if (key == "lastuseddaykey")
                {
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int storedDayKey))
                    {
                        _usedToday = (storedDayKey == dayKey);
                    }
                }
            }
        }
        catch
        {
            _usedToday = false;
        }
    }

    private void SaveChopDailyStateForCurrentCharacter()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            int dayKey = GetCurrentGameDayKey();

            if (ownerHash == 0 || dayKey == -1)
                return;

            _chopDailyStateOwnerHash = ownerHash;
            _chopDailyStateDayKey = dayKey;

            Directory.CreateDirectory(ChopDataRoot);

            string file = GetChopDailyStateFile(ownerHash);
            string text =
                "version=1" + Environment.NewLine +
                "lastUsedDayKey=" + dayKey.ToString(CultureInfo.InvariantCulture) + Environment.NewLine;

            File.WriteAllText(file, text, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private bool CanUseChopNow()
    {
        try
        {
            return _violetEventActive
                && !_violetIconsEnabled
                && !_usedThisVioletEvent
                && !_usedToday;
        }
        catch
        {
            return false;
        }
    }

    private bool TryRevealRandomHiddenVioletVehicle()
    {
        try
        {
            var vehicles = GetCurrentVioletTrialVehicles();
            if (vehicles == null || vehicles.Count == 0)
                return false;

            var liveVehicles = vehicles.Where(v => v != null && v.Exists()).ToList();
            if (liveVehicles.Count == 0)
                return false;

            Vehicle target = liveVehicles[_rng.Next(liveVehicles.Count)];
            if (target == null || !target.Exists())
                return false;

            _revealedTargetVehicle = target;
            _routeClearedNearTarget = false;

            CreateRevealBlipForTarget(target);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private List<Vehicle> GetCurrentVioletTrialVehicles()
    {
        var result = new List<Vehicle>();

        try
        {
            object violetInstance = GetVioletInstance();
            if (violetInstance == null)
                return result;

            IEnumerable<object> rawRecords = GetEnumerableField(violetInstance, VioletTrialVehiclesField);
            if (rawRecords == null)
                return result;

            foreach (object record in rawRecords)
            {
                try
                {
                    Vehicle veh = GetVehicleFromTrialRecord(record);
                    if (veh != null && veh.Exists())
                        result.Add(veh);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return result;
    }

    private Vehicle GetVehicleFromTrialRecord(object record)
    {
        if (record == null)
            return null;

        try
        {
            Type t = record.GetType();
            FieldInfo field = t.GetField("RuntimeVehicle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null && typeof(Vehicle).IsAssignableFrom(field.FieldType))
                return field.GetValue(record) as Vehicle;
        }
        catch
        {
        }

        return null;
    }

    private void CreateRevealBlipForTarget(Vehicle target)
    {
        try
        {
            ClearRevealBlip();

            if (target == null || !target.Exists())
                return;

            Blip blip = target.AddBlip();
            if (blip == null || !blip.Exists())
                return;

            blip.IsShortRange = false;
            blip.Sprite = GetVehicleBlipSprite(target);
            blip.Color = BlipColor.Yellow;

            try
            {
                Function.Call(Hash.SET_BLIP_DISPLAY, blip.Handle, 2);
            }
            catch { }

            try
            {
                Function.Call(Hash.SET_BLIP_ROUTE, blip.Handle, true);
            }
            catch { }

            try
            {
                blip.ShowRoute = true;
            }
            catch { }

            try
            {
                Function.Call(Hash.SET_BLIP_ROUTE_COLOUR, blip.Handle, 3);
            }
            catch { }

            _revealedTargetBlip = blip;
        }
        catch
        {
        }
    }

    private static BlipSprite GetVehicleBlipSprite(Vehicle v)
    {
        try
        {
            if (v != null && v.Exists())
            {
                if (v.IsBlimp) return BlipSprite.Blimp;
                if (v.IsSubmarine) return BlipSprite.Sub;
                if (v.IsBoat) return BlipSprite.Boat;
                if (v.IsHelicopter) return BlipSprite.Helicopter;
                if (v.IsPlane) return BlipSprite.Plane;
                if (v.IsBike || v.IsBicycle || v.IsMotorcycle) return BlipSprite.PersonalVehicleBike;
                if (v.IsTrain) return BlipSprite.PersonalVehicleCar;
                return BlipSprite.PersonalVehicleCar;
            }
        }
        catch
        {
        }

        return BlipSprite.PersonalVehicleCar;
    }

    private void MonitorRevealedTargetProximity()
    {
        try
        {
            if (!_violetEventActive)
                return;

            if (_revealedTargetVehicle == null || !_revealedTargetVehicle.Exists())
            {
                if (_revealedTargetBlip != null)
                {
                    ClearRevealBlip();
                }
                return;
            }

            if (_routeClearedNearTarget)
                return;

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead)
                return;

            float dist = player.Position.DistanceTo(_revealedTargetVehicle.Position);
            if (dist <= ReachClearRadius)
            {
                ClearRevealRouteOnly();
                _routeClearedNearTarget = true;
            }
        }
        catch
        {
        }
    }

    private void ClearRevealRouteOnly()
    {
        try
        {
            if (_revealedTargetBlip != null && _revealedTargetBlip.Exists())
            {
                try
                {
                    Function.Call(Hash.SET_BLIP_ROUTE, _revealedTargetBlip.Handle, false);
                }
                catch
                {
                }

                try
                {
                    _revealedTargetBlip.ShowRoute = false;
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

    private void ClearRevealBlip()
    {
        try
        {
            if (_revealedTargetBlip != null && _revealedTargetBlip.Exists())
                _revealedTargetBlip.Delete();
        }
        catch
        {
        }

        _revealedTargetBlip = null;
    }

    private void ResetChopState(bool deleteBlip)
    {
        try
        {
            if (deleteBlip)
                ClearRevealBlip();
            else
                ClearRevealRouteOnly();
        }
        catch
        {
        }

        _revealedTargetVehicle = null;
        _routeClearedNearTarget = false;
        _contactEnabledCached = false;
    }

    private void ShowChopMessage(string msg)
    {
        try
        {
            Notification.Show(NotificationIcon.Chop, ChopContactName, ChopNotificationTitle, msg);
            return;
        }
        catch
        {
        }

        try
        {
            Screen.ShowSubtitle(msg, 2500);
        }
        catch { }
    }

    private void SetContactActive(bool active)
    {
        try
        {
            if (_chopContact != null)
                _chopContact.Active = active;

            var phone = CustomiFruit.GetCurrentInstance();
            if (phone != null && phone.Contacts != null)
            {
                var c = phone.Contacts.FirstOrDefault(x =>
                    x != null &&
                    string.Equals(x.Name, ChopContactName, StringComparison.OrdinalIgnoreCase));

                if (c != null)
                    c.Active = active;
            }
        }
        catch
        {
        }
    }

    private object GetVioletInstance()
    {
        try
        {
            return VioletSingletonField?.GetValue(null);
        }
        catch
        {
            return null;
        }
    }

    private static bool GetBoolField(object obj, FieldInfo field, bool fallback)
    {
        try
        {
            if (obj == null || field == null)
                return fallback;

            object v = field.GetValue(obj);
            if (v is bool b)
                return b;
        }
        catch
        {
        }

        return fallback;
    }

    private static int GetIntField(object obj, FieldInfo field, int fallback)
    {
        try
        {
            if (obj == null || field == null)
                return fallback;

            object v = field.GetValue(obj);
            if (v is int i)
                return i;
        }
        catch
        {
        }

        return fallback;
    }

    private static IEnumerable<object> GetEnumerableField(object obj, FieldInfo field)
    {
        try
        {
            if (obj == null || field == null)
                return null;

            object v = field.GetValue(obj);
            if (v is System.Collections.IEnumerable enumerable)
            {
                var list = new List<object>();
                foreach (object item in enumerable)
                    list.Add(item);
                return list;
            }
        }
        catch
        {
        }

        return null;
    }
}