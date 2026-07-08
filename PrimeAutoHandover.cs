using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using iFruitAddon2;
using LemonUI;
using LemonUI.Menus;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

public class PrimeAutoHandover : Script
{
    private const long BUSINESS_PRICE = 1_750_000L;
    private const float MARKER_INTERACT_RADIUS = 2.5f;
    private const int CONTACT_SIGNAL_DURATION_MS = 8000;
    private const int CONTACT_SIGNAL_PULSE_MS = 1200;
    private const int CONTACT_DIAL_TIMEOUT = 2500;
    private const int GAME_READY_DELAY_MS = 3000;

    private static string NotificationSenderName => L("NotificationSenderName", "BOMW");
    private static string ContactName => L("ContactName_PAH", "D2D Shipping");

    private static readonly Vector3 BusinessMarkerPosition = new Vector3(-167.915600f, 916.191600f, 235.655700f);
    private static string BusinessName => L("BusinessName", "Prime Auto Handover");
    private static string BrandName => L("BrandName", "PAH");
    private static string BusinessField => L("BusinessField", "Vận chuyển phương tiện");

    private static string MainMenuTitle => L("MainMenuTitle", "Prime Handover");
    private static string MainMenuHeader => L("MainMenuHeader", "CHI TIẾT DOANH NGHIỆP");
    private static string MainMenuFooter => L("MainMenuFooter", "D2D SHIPPING");
    private static string NoItemsText => L("NoItemsText", "Không có dữ liệu");

    private static string DoorToDoorTitle => L("DoorToDoorTitle", "Giao xe tận nơi");
    private static string DoorToDoorDescription => L("DoorToDoorDescription", "Dịch vụ bắt buộc của Prime Auto Handover.");
    private static string OnSiteTitle => L("OnSiteTitle", "Bàn giao tại vị trí");
    private static string OnSiteDescription => L("OnSiteDescription", "Dịch vụ bắt buộc của Prime Auto Handover.");
    private static string DeclineTradeTitle => L("DeclineTradeTitle", "Từ chối giao dịch này");
    private static string DeclineTradeDescription => L("DeclineTradeDescription", "Đóng menu và quay lại.");

    private static string BusinessNameLabel => L("BusinessNameLabel", "Tên doanh nghiệp: ");
    private static string BrandNameLabel => L("BrandNameLabel", "Tên thương hiệu: ");
    private static string BusinessFieldLabel => L("BusinessFieldLabel", "Lĩnh vực: ");
    private static string BusinessPriceLabel => L("BusinessPriceLabel", "Giá mua: ");
    private static string StatusPurchasedLabel => L("StatusPurchasedLabel", "Trạng thái: Đã mua doanh nghiệp");
    private static string StatusNotPurchasedLabel => L("StatusNotPurchasedLabel", "Trạng thái: Chưa mua doanh nghiệp");

    private static string PurchasedTitle => L("PurchasedTitle", "Doanh nghiệp đã được mua");
    private static string ConfirmPurchaseTitle => L("ConfirmPurchaseTitle", "Xác nhận mua doanh nghiệp");
    private static string PurchasedDescription => L("PurchasedDescription", "Doanh nghiệp này đã được mua trước đó. Các nhiệm vụ giao hàng sẽ được mở khóa nếu bạn tiếp tục dùng doanh nghiệp.");
    private static string ConfirmPurchaseDescription => L("ConfirmPurchaseDescription", "Thanh toán để sở hữu Prime Auto Handover và mở khóa 3 nhiệm vụ giao hàng.");
    private static string CloseTitle => L("CloseTitle", "Hủy bỏ");
    private static string CloseDescription => L("CloseDescription", "Đóng menu.");
    private static string DeclineTitleAlt => L("DeclineTitleAlt", "Từ chối giao dịch này");
    private static string DeclineDescriptionAlt => L("DeclineDescriptionAlt", "Từ chối giao dịch này và đóng menu.");

    private static string StatusNotificationTitle => L("StatusNotificationTitle", "Prime Handover");
    private static string PurchasedAlreadyStatus => L("PurchasedAlreadyStatus", "Doanh nghiệp này đã được mua. Các nhiệm vụ giao xe, giao máy bay và giao tàu thuyền hiện đã sẵn sàng.");
    private static string InsufficientMoneyStatus => L("InsufficientMoneyStatus", "Bạn không đủ tiền để mua doanh nghiệp này.");

    private static string PurchaseSuccessSubject => L("PurchaseSuccessSubject", "Mua doanh nghiệp thành công");
    private static string PurchaseSuccessBody => L("PurchaseSuccessBody", "Bạn đã sở hữu Prime Auto Handover. Các nhiệm vụ giao hàng sẽ bắt đầu kích hoạt từ bây giờ.");

    private readonly ObjectPool _uiPool = new ObjectPool();
    private NativeMenu _mainMenu;
    private bool _menuInitialized;

    private NativeItem _businessNameItem;
    private NativeItem _brandItem;
    private NativeItem _fieldItem;
    private NativeItem _priceItem;
    private NativeItem _statusItem;
    private NativeCheckboxItem _doorToDoorServiceItem;
    private NativeCheckboxItem _onSiteHandoverItem;
    private NativeItem _confirmPurchaseItem;
    private NativeItem _declineItem;

    private CustomiFruit _phoneInstance;
    private iFruitContact _handoverContact;
    private bool _contactAnsweredBound;

    private bool _ready;
    private int _gameReadySince = -1;
    private bool _signalActive;
    private bool _signalVisible;
    private int _signalEndTime;
    private int _signalLastPulseTime;
    private Blip _signalBlip;

    private int _lastCharacterHash;

    private readonly Queue<Action> _pendingUiActions = new Queue<Action>();

    public PrimeAutoHandover()
    {
        Tick += OnTick;
        KeyDown += OnKeyDown;
        Interval = 0;
    }

    private void SetBoolPropertyIfExists(object target, bool value, params string[] propertyNames)
    {
        try
        {
            if (target == null || propertyNames == null || propertyNames.Length == 0)
                return;

            Type t = target.GetType();

            foreach (string name in propertyNames)
            {
                PropertyInfo prop = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null && prop.CanWrite && prop.PropertyType == typeof(bool))
                {
                    prop.SetValue(target, value, null);
                    return;
                }
            }
        }
        catch
        {
        }
    }

    private void UpdateLemonUiMouseState()
    {
        try
        {
            bool anyMenuVisible = _mainMenu != null && _mainMenu.Visible;
            if (!anyMenuVisible)
                return;

            SetBoolPropertyIfExists(_mainMenu, false,
                "MouseControlsEnabled", "MouseControls", "EnableMouseControls", "UseMouse",
                "MouseEdgeEnabled", "MouseEdgesEnabled", "AllowMouseControls");

            SetBoolPropertyIfExists(_uiPool, false,
                "MouseControlsEnabled", "MouseControls", "EnableMouseControls", "UseMouse",
                "MouseEdgeEnabled", "MouseEdgesEnabled", "AllowMouseControls");
        }
        catch { }
    }

    private void OnTick(object sender, EventArgs e)
    {
        try
        {
            if (Game.IsLoading || Game.IsCutsceneActive)
            {
                ResetRuntimeState();
                return;
            }

            if (!_ready)
            {
                if (_gameReadySince < 0)
                    _gameReadySince = Game.GameTime;

                if (Game.GameTime - _gameReadySince < GAME_READY_DELAY_MS)
                    return;

                _ready = true;
            }

            SyncCharacterState();
            EnsureContactRegistered();
            UpdateSignalState();
            DrawBusinessMarker();
            FlushUiActions();
            UpdateLemonUiMouseState();

            if (_uiPool != null && _uiPool.AreAnyVisible)
            {
                _uiPool.Process();
                UpdateLemonUiMouseState();
                Interval = 0;
            }
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

            if (_uiPool != null && _uiPool.AreAnyVisible)
            {
                if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
                    CloseMainMenu();
            }
        }
        catch (Exception ex)
        {
            Log("OnKeyDown failed: " + ex);
        }
    }

    private void SyncCharacterState()
    {
        try
        {
            int hash = GetCurrentCharacterHash();
            if (hash == 0 || _lastCharacterHash == hash)
                return;

            _lastCharacterHash = hash;
            RefreshMainMenu();
        }
        catch (Exception ex)
        {
            Log("SyncCharacterState failed: " + ex);
        }
    }

    private void EnsureContactRegistered()
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            if (!ReferenceEquals(_phoneInstance, phone))
            {
                _phoneInstance = phone;
                _handoverContact = null;
                _contactAnsweredBound = false;
            }

            if (_handoverContact == null)
            {
                _handoverContact = phone.Contacts.FirstOrDefault(c =>
                    string.Equals(c.Name, ContactName, StringComparison.OrdinalIgnoreCase));
            }

            if (_handoverContact == null)
            {
                _handoverContact = new iFruitContact(ContactName)
                {
                    Active = true,
                    DialTimeout = CONTACT_DIAL_TIMEOUT,
                    Bold = false,
                    Icon = new ContactIcon("CHAR_CARSITE3")
                };
                phone.Contacts.Add(_handoverContact);
            }
            else
            {
                _handoverContact.Active = true;
            }

            if (!_contactAnsweredBound)
            {
                _handoverContact.Answered += OnHandoverAnswered;
                _contactAnsweredBound = true;
            }
        }
        catch (Exception ex)
        {
            Log("EnsureContactRegistered failed: " + ex);
        }
    }

    private void OnHandoverAnswered(iFruitContact sender)
    {
        try
        {
            if (IsPlayerNearBusinessMarker())
            {
                OpenBusinessMenu();
                TryClosePhone();
                return;
            }

            StartHandoverSignal();
            TryClosePhone();
        }
        catch (Exception ex)
        {
            Log("OnHandoverAnswered failed: " + ex);
        }
    }

    private void StartHandoverSignal()
    {
        try
        {
            _signalActive = true;
            _signalVisible = true;
            _signalEndTime = Game.GameTime + CONTACT_SIGNAL_DURATION_MS;
            _signalLastPulseTime = Game.GameTime;

            EnsureSignalBlip();
            ApplySignalVisibility(true);
        }
        catch (Exception ex)
        {
            Log("StartHandoverSignal failed: " + ex);
        }
    }

    private void StopHandoverSignal()
    {
        try
        {
            _signalActive = false;
            _signalVisible = false;
            _signalEndTime = 0;
            _signalLastPulseTime = 0;
            ClearSignalBlip();
        }
        catch (Exception ex)
        {
            Log("StopHandoverSignal failed: " + ex);
        }
    }

    private void UpdateSignalState()
    {
        try
        {
            if (!_signalActive)
                return;

            int now = Game.GameTime;
            if (now >= _signalEndTime)
            {
                StopHandoverSignal();
                return;
            }

            if (now - _signalLastPulseTime >= CONTACT_SIGNAL_PULSE_MS)
            {
                _signalVisible = !_signalVisible;
                ApplySignalVisibility(_signalVisible);
                _signalLastPulseTime = now;
            }
        }
        catch (Exception ex)
        {
            Log("UpdateSignalState failed: " + ex);
        }
    }

    private void EnsureSignalBlip()
    {
        try
        {
            if (_signalBlip != null && _signalBlip.Exists())
                return;

            _signalBlip = World.CreateBlip(BusinessMarkerPosition);
            if (_signalBlip != null)
            {
                _signalBlip.Sprite = BlipSprite.Standard;
                _signalBlip.Color = BlipColor.Yellow;
                _signalBlip.Scale = 0.9f;
                _signalBlip.IsShortRange = false;
                _signalBlip.Name = ContactName;
                _signalBlip.ShowRoute = false;
                _signalBlip.IsFlashing = true;
                _signalBlip.Alpha = 255;
            }
        }
        catch (Exception ex)
        {
            Log("EnsureSignalBlip failed: " + ex);
            _signalBlip = null;
        }
    }

    private void ApplySignalVisibility(bool visible)
    {
        try
        {
            if (_signalBlip == null || !_signalBlip.Exists())
                return;

            _signalBlip.Alpha = visible ? 255 : 0;
            _signalBlip.IsFlashing = true;
        }
        catch (Exception ex)
        {
            Log("ApplySignalVisibility failed: " + ex);
        }
    }

    private void ClearSignalBlip()
    {
        try
        {
            if (_signalBlip != null && _signalBlip.Exists())
                _signalBlip.Delete();
        }
        catch { }

        _signalBlip = null;
    }

    private void DrawBusinessMarker()
    {
        try
        {
            World.DrawMarker(
                MarkerType.VerticalCylinder,
                BusinessMarkerPosition + new Vector3(0.0f, 0.0f, -1.0f),
                Vector3.Zero,
                Vector3.Zero,
                new Vector3(1.0f, 1.0f, 1.0f),
                Color.FromArgb(215, 255, 235, 120));
        }
        catch { }
    }

    private bool IsPlayerNearBusinessMarker()
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead)
                return false;

            return player.Position.DistanceToSquared(BusinessMarkerPosition) <= MARKER_INTERACT_RADIUS * MARKER_INTERACT_RADIUS;
        }
        catch
        {
            return false;
        }
    }

    private void OpenBusinessMenu()
    {
        try
        {
            EnsureMainMenuCreated();
            RefreshMainMenu();

            if (_mainMenu != null)
            {
                _mainMenu.Visible = true;
                Interval = 0;
                UpdateLemonUiMouseState();
            }
        }
        catch (Exception ex)
        {
            Log("OpenBusinessMenu failed: " + ex);
        }
    }

    private void CloseMainMenu()
    {
        try
        {
            if (_mainMenu != null)
                _mainMenu.Visible = false;

            UpdateLemonUiMouseState();
        }
        catch (Exception ex)
        {
            Log("CloseMainMenu failed: " + ex);
        }
    }

    private void EnsureMainMenuCreated()
    {
        try
        {
            if (_menuInitialized)
                return;

            _mainMenu = new NativeMenu(MainMenuTitle, MainMenuHeader, MainMenuFooter);
            _mainMenu.KeepNameCasing = true;
            _mainMenu.MaxItems = 8;
            _mainMenu.NoItemsText = NoItemsText;
            _uiPool.Add(_mainMenu);

            _businessNameItem = new NativeItem("");
            _brandItem = new NativeItem("");
            _fieldItem = new NativeItem("");
            _priceItem = new NativeItem("");
            _statusItem = new NativeItem("");
            _doorToDoorServiceItem = CreateLockedCheckboxItem(DoorToDoorTitle, DoorToDoorDescription);
            _onSiteHandoverItem = CreateLockedCheckboxItem(OnSiteTitle, OnSiteDescription);
            _confirmPurchaseItem = new NativeItem("");
            _declineItem = new NativeItem(DeclineTradeTitle, DeclineTradeDescription);

            _confirmPurchaseItem.Activated += (s, e) => QueueUiAction(HandlePurchaseConfirm);
            _declineItem.Activated += (s, e) => QueueUiAction(CloseMainMenu);

            _mainMenu.Add(_businessNameItem);
            _mainMenu.Add(_brandItem);
            _mainMenu.Add(_fieldItem);
            _mainMenu.Add(_priceItem);
            _mainMenu.Add(_statusItem);
            _mainMenu.Add(_doorToDoorServiceItem);
            _mainMenu.Add(_onSiteHandoverItem);
            _mainMenu.Add(_confirmPurchaseItem);
            _mainMenu.Add(_declineItem);

            _menuInitialized = true;
        }
        catch (Exception ex)
        {
            Log("EnsureMainMenuCreated failed: " + ex);
        }
    }

    private NativeCheckboxItem CreateLockedCheckboxItem(string title, string description)
    {
        bool suppress = false;
        var item = new NativeCheckboxItem(title, description, true);

        item.CheckboxChanged += (s, e) =>
        {
            if (suppress)
                return;

            try
            {
                if (!item.Checked)
                {
                    suppress = true;
                    item.Checked = true;
                    suppress = false;
                }
            }
            catch
            {
                suppress = false;
            }
        };

        return item;
    }

    private void RefreshMainMenu()
    {
        try
        {
            if (_mainMenu == null)
                return;

            bool purchased = PrimeAutoHandoverBridge.IsBusinessPurchasedForCurrentCharacter();
            _businessNameItem.Title = BusinessNameLabel + BusinessName;
            _brandItem.Title = BrandNameLabel + BrandName;
            _fieldItem.Title = BusinessFieldLabel + BusinessField;
            _priceItem.Title = BusinessPriceLabel + FormatMoney(BUSINESS_PRICE);
            _statusItem.Title = purchased ? StatusPurchasedLabel : StatusNotPurchasedLabel;

            _confirmPurchaseItem.Title = purchased ? PurchasedTitle : ConfirmPurchaseTitle;
            _confirmPurchaseItem.Description = purchased
                ? PurchasedDescription
                : ConfirmPurchaseDescription;

            _declineItem.Title = purchased ? CloseTitle : DeclineTradeTitle;
            _declineItem.Description = purchased ? CloseDescription : DeclineDescriptionAlt;
        }
        catch (Exception ex)
        {
            Log("RefreshMainMenu failed: " + ex);
        }
    }

    private void HandlePurchaseConfirm()
    {
        try
        {
            if (PrimeAutoHandoverBridge.IsBusinessPurchasedForCurrentCharacter())
            {
                ShowStatusNotification(StatusNotificationTitle, PurchasedAlreadyStatus);
                CloseMainMenu();
                return;
            }

            if (Game.Player.Money < BUSINESS_PRICE)
            {
                ShowStatusNotification(StatusNotificationTitle, InsufficientMoneyStatus);
                return;
            }

            Game.Player.Money -= (int)BUSINESS_PRICE;
            PrimeAutoHandoverBridge.SetBusinessPurchasedForCurrentCharacter(true);
            RefreshMainMenu();

            ShowFeedMessage(ContactName, PurchaseSuccessSubject, PurchaseSuccessBody);
            CloseMainMenu();
        }
        catch (Exception ex)
        {
            Log("HandlePurchaseConfirm failed: " + ex);
        }
    }

    private void ShowStatusNotification(string title, string message)
    {
        try
        {
            PlayFrontendSound("Text_Arrive_Tone", "Phone_SoundSet_Default");
            Notification.Show(
                NotificationIcon.Carsite2,
                NotificationSenderName,
                title,
                message);
        }
        catch
        {
            try
            {
                GTA.UI.Notification.Show(message);
            }
            catch
            {
                GTA.UI.Screen.ShowSubtitle(title + ": " + message, 2500);
            }
        }
    }

    private void ShowFeedMessage(string sender, string subject, string body)
    {
        try
        {
            PlayFrontendSound("Text_Arrive_Tone", "Phone_SoundSet_Default");
            Notification.Show(
                NotificationIcon.Carsite2,
                sender,
                subject,
                body);
        }
        catch
        {
            try
            {
                GTA.UI.Notification.Show(body);
            }
            catch { }
        }
    }

    private void PlayFrontendSound(string soundName, string soundSet)
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

    private void TryClosePhone()
    {
        try
        {
            CustomiFruit.GetCurrentInstance()?.Close(0);
        }
        catch { }
    }

    private static int GetCurrentCharacterHash()
    {
        try
        {
            Ped p = Game.Player.Character;
            if (p == null || !p.Exists())
                return 0;

            int hash = p.Model.Hash;
            if (hash == PrimeAutoHandoverBridge.HashMichael ||
                hash == PrimeAutoHandoverBridge.HashFranklin ||
                hash == PrimeAutoHandoverBridge.HashTrevor)
            {
                return hash;
            }
        }
        catch { }

        return 0;
    }

    private void FlushUiActions()
    {
        while (true)
        {
            Action next = null;
            lock (_pendingUiActions)
            {
                if (_pendingUiActions.Count > 0)
                    next = _pendingUiActions.Dequeue();
            }

            if (next == null)
                break;

            try
            {
                next();
            }
            catch (Exception ex)
            {
                Log("FlushUiActions action failed: " + ex);
            }
        }
    }

    private void QueueUiAction(Action action)
    {
        if (action == null)
            return;

        lock (_pendingUiActions)
        {
            _pendingUiActions.Enqueue(action);
        }
    }

    private void ResetRuntimeState()
    {
        try
        {
            CloseMainMenu();
            ClearSignalBlip();
            _signalActive = false;
            _signalVisible = false;
            _signalEndTime = 0;
            _signalLastPulseTime = 0;
            _ready = false;
            _gameReadySince = -1;
            _menuInitialized = false;
            _lastCharacterHash = 0;
            _contactAnsweredBound = false;
        }
        catch { }
    }

    private void Log(string message)
    {
        try
        {
            // File.AppendAllText("PrimeAutoHandover.log", DateTime.Now.ToString("s") + " " + message + Environment.NewLine);
        }
        catch { }
    }

    private static string FormatMoney(long value)
    {
        if (value < 0)
            value = 0;

        return string.Format(CultureInfo.InvariantCulture, "${0:N0}", value);
    }

    private static string L(string key, string fallback)
    {
        return Language.Get(key, fallback);
    }
}

internal static class PrimeAutoHandoverBridge
{
    public const int HashMichael = 225514697;
    public const int HashFranklin = -1692214353;
    public const int HashTrevor = -1686040670;

    private static readonly object SyncRoot = new object();
    private static readonly string StateRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods",
        "Prime Auto Handover");

    private static bool _loaded;
    private static bool _purchased;

    public static bool CanTriggerDeliveryContractsForCurrentCharacter()
    {
        return IsBusinessPurchasedForCurrentCharacter();
    }

    public static bool IsBusinessPurchasedForCurrentCharacter()
    {
        lock (SyncRoot)
        {
            EnsureLoadedForCurrentCharacter_NoLock();
            return _purchased;
        }
    }

    public static void SetBusinessPurchasedForCurrentCharacter(bool purchased)
    {
        lock (SyncRoot)
        {
            EnsureLoadedForCurrentCharacter_NoLock();
            _purchased = purchased;
            SaveCurrentState_NoLock();
        }
    }

    private static void EnsureLoadedForCurrentCharacter_NoLock()
    {
        if (_loaded)
            return;

        _loaded = true;
        _purchased = LoadPurchasedState();
        if (_purchased)
            SaveCurrentState_NoLock();
    }

    private static string GetStateFile()
    {
        Directory.CreateDirectory(StateRoot);
        return Path.Combine(StateRoot, "prime_auto_handover_global.dat");
    }

    private static bool LoadPurchasedState()
    {
        try
        {
            string file = GetStateFile();
            if (File.Exists(file))
            {
                string text = File.ReadAllText(file);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        int idx = line.IndexOf('=');
                        if (idx <= 0)
                            continue;

                        string key = line.Substring(0, idx).Trim();
                        string value = line.Substring(idx + 1).Trim();

                        if (string.Equals(key, "purchased", StringComparison.OrdinalIgnoreCase))
                            return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    }
                }
            }

            foreach (int legacyHash in new[] { HashMichael, HashFranklin, HashTrevor })
            {
                string legacyFile = Path.Combine(StateRoot, $"prime_auto_handover_{legacyHash}.dat");
                if (!File.Exists(legacyFile))
                    continue;

                string text = File.ReadAllText(legacyFile);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int idx = line.IndexOf('=');
                    if (idx <= 0)
                        continue;

                    string key = line.Substring(0, idx).Trim();
                    string value = line.Substring(idx + 1).Trim();

                    if (string.Equals(key, "purchased", StringComparison.OrdinalIgnoreCase) &&
                        (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
            }
        }
        catch { }

        return false;
    }

    private static void SaveCurrentState_NoLock()
    {
        try
        {
            Directory.CreateDirectory(StateRoot);
            string file = GetStateFile();

            var sb = new StringBuilder();
            sb.AppendLine("version=1");
            sb.AppendLine("purchased=" + (_purchased ? "1" : "0"));

            File.WriteAllText(file, sb.ToString());
        }
        catch { }
    }
}