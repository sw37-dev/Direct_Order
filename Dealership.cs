using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using LemonUI;
using LemonUI.Menus;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Reflection;

public partial class PersistentManager
{
    private static string GetCollateralLockedDescription()
    {
        return "Bạn đang thế chấp chiếc phương tiện này cho hoạt động vay của bạn tại ngân hàng Fleeca nên không thể thanh lý";
    }

    private static readonly BadgeSet _lockBadge = new BadgeSet
    {
        NormalDictionary = "commonmenu",
        NormalTexture = "shop_lock",
        HoveredDictionary = "commonmenu",
        HoveredTexture = "shop_lock_b"
    };

    private readonly HashSet<NativeItem> _dealershipMainLockedBadgeItems = new HashSet<NativeItem>();
    private readonly HashSet<NativeItem> _dealershipBulkLockedBadgeItems = new HashSet<NativeItem>();
    private readonly HashSet<NativeItem> _dealershipDetailLockedBadgeItems = new HashSet<NativeItem>();

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
            // cố tình bỏ qua
        }
    }

    private bool _dealershipNeedsFastTick = false;

    private const int DEALERSHIP_SIGNAL_DURATION_MS = 8000;
    private const int DEALERSHIP_SIGNAL_PULSE_MS = 1200;

    private bool _dealershipSignalActive = false;
    private bool _dealershipSignalVisible = false;
    private int _dealershipSignalEndTime = 0;
    private int _dealershipSignalLastPulseTime = 0;
    private readonly List<Blip> _dealershipSignalBlips = new List<Blip>();

    private static readonly Vector3[] _dealershipPoints =
    {
        new Vector3(-38.742830f, -1110.089000f, 26.438140f),
        new Vector3(1309.629000f, 4361.937000f, 41.540500f),
        new Vector3(28.243100f, -1769.424000f, 29.560840f),
        new Vector3(-357.386000f, -131.231600f, 39.430670f),
        new Vector3(-283.719700f, -1919.935000f, 29.946030f),
        new Vector3(-1041.252000f, -1474.094000f, 5.377921f),
        new Vector3(285.091900f, 2847.120000f, 43.642400f),
        new Vector3(-421.996000f, -2787.513000f, 6.000384f)
    };

    private NativeStatsPanel _dealershipVehicleStatsPanel;
    private NativeStatsInfo _dealershipStatTopSpeed;
    private NativeStatsInfo _dealershipStatAcceleration;
    private NativeStatsInfo _dealershipStatBraking;
    private NativeStatsInfo _dealershipStatTraction;

    private static readonly string[] _dealershipAliasPool =
    {
        L("DealershipAlias_Dealership", "Dealership"),
        L("DealershipAlias_TheBuyOut", "The Buy-Out"),
        L("DealershipAlias_GlobalLiquidators", "Global Liquidators"),
        L("DealershipAlias_ApexAcquisitions", "Apex Acquisitions"),
        L("DealershipAlias_AssetRecoveryInc", "Asset Recovery Inc"),
        L("DealershipAlias_VehicleScrapper", "Vehicle Scrapper"),
        L("DealershipAlias_EliteExports", "Elite Exports")
    };

    private static readonly object _dealershipAliasLock = new object();
    private static readonly Random _dealershipAliasRng = new Random();

    private string _currentDealershipAlias = "Dealership";
    private string _lastDealershipAlias = null;

    private NativeMenu _dealershipBulkMenu;
    private bool _dealershipBulkMenuBuilt = false;
    private NativeItem _dealershipBulkSellConfirmItem;
    private readonly HashSet<PersistentVehicle> _dealershipBulkSelected = new HashSet<PersistentVehicle>();
    private int _dealershipBulkSellTotal = 0;

    private NativeMenu _dealershipDetailMenu;
    private bool _dealershipDetailMenuBuilt = false;
    private PersistentVehicle _selectedDealershipVehicle = null;

    private const float DEALERSHIP_INTERACT_RADIUS = 2.25f;
    private const float DEALERSHIP_DRAW_RADIUS = 60.0f;

    private readonly ObjectPool _menuPool = new ObjectPool();
    private NativeMenu _dealershipMenu;
    private bool _dealershipMenuBuilt = false;
    private bool _playerInsideDealership = false;

    private readonly Queue<Action> _uiActions = new Queue<Action>();

    private void BuildDealershipMenus(string dealerName, int ownedCount, bool forceRebuild = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dealerName))
                dealerName = "Dealership";

            _currentDealershipAlias = dealerName;

            if (!forceRebuild && _dealershipMenuBuilt && _dealershipMenu != null && _dealershipDetailMenu != null)
                return;

            try { if (_dealershipMenu != null) _dealershipMenu.Visible = false; } catch { }
            try { if (_dealershipDetailMenu != null) _dealershipDetailMenu.Visible = false; } catch { }
            try { if (_dealershipBulkMenu != null) _dealershipBulkMenu.Visible = false; } catch { }

            _selectedDealershipVehicle = null;

            string mainSubtitle = string.Format(
                L("PM_DealershipMenu_Subtitle", "CHỌN PHƯƠNG TIỆN MUỐN BÁN ({0} xe)"),
                ownedCount
            );

            _dealershipMenu = new NativeMenu(
                     dealerName,
                     dealerName,
                     mainSubtitle);
            _dealershipMenu.KeepNameCasing = true;
            _dealershipMenu.MaxItems = 8;
            _dealershipMenu.NoItemsText = L("PM_DealershipMenu_NoItems", "Không có phương tiện nào");
            ConfigureDealershipMenuInput(_dealershipMenu);
            _menuPool.Add(_dealershipMenu);

            _dealershipDetailMenu = new NativeMenu(
                    dealerName,
                    L("PM_DealershipDetailMenu_Title", "THÔNG TIN THANH LÝ CHI TIẾT"),
                    L("PM_DealershipDetailMenu_Subtitle", "Thông tin phương tiện và số tiền"));
            _dealershipDetailMenu.KeepNameCasing = true;
            _dealershipDetailMenu.MaxItems = 8;
            _dealershipDetailMenu.NoItemsText = L("PM_DealershipDetailMenu_NoItems", "Không có dữ liệu");
            ConfigureDealershipMenuInput(_dealershipDetailMenu);
            _menuPool.Add(_dealershipDetailMenu);

            _dealershipMenuBuilt = true;
            _dealershipDetailMenuBuilt = true;

            RefreshDealershipMenu();
        }
        catch (Exception ex)
        {
            Log("BuildDealershipMenus failed: " + ex.ToString());
        }
    }

    private void OpenDealershipMenu(string dealerName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dealerName))
                dealerName = PickRandomDealershipAlias();

            StopDealershipSignal();

            int ownedCount = GetOwnedVehiclesForCurrentPlayer().Count;
            BuildDealershipMenus(dealerName, ownedCount, true);
            RefreshDealershipMenu();
            ConfigureDealershipMenuInput(_dealershipMenu);
            ConfigureDealershipMenuInput(_dealershipDetailMenu);

            if (_dealershipMenu != null)
            {
                _dealershipMenu.Visible = true;
                Interval = 0;
            }
        }
        catch (Exception ex)
        {
            Log("OpenDealershipMenu failed: " + ex.ToString());
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

                // Đang trỏ vào item khóa -> bỏ ổ khóa
                // Không trỏ vào -> hiện lại ổ khóa
                SetRightBadgeSetIfExists(item, i == selectedIndex ? null : _lockBadge);
            }
        }
        catch
        {
            // cố tình bỏ qua
        }
    }

    private void UpdateAllDealershipLockedBadges()
    {
        UpdateConditionalLockBadges(_dealershipMenu, _dealershipMainLockedBadgeItems);
        UpdateConditionalLockBadges(_dealershipBulkMenu, _dealershipBulkLockedBadgeItems);
        UpdateConditionalLockBadges(_dealershipDetailMenu, _dealershipDetailLockedBadgeItems);
    }

    private void RefreshDealershipMenu()
    {
        try
        {
            if (_dealershipMenu == null)
                return;

            _dealershipMainLockedBadgeItems.Clear();
            _dealershipMenu.Clear();

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
            {
                _dealershipMenu.Add(new NativeItem(
                      L("PM_NoCharacter", "Không có nhân vật"),
                      L("PM_NoCharacterDesc", "Không thể xem danh sách.")));
                return;
            }

            int ownerHash = player.Model.Hash;

            List<PersistentVehicle> owned;
            lock (_persistVehicles)
            {
                owned = _persistVehicles
                   .Where(v => v.OwnerModelHash == ownerHash)
                   .ToList();
            }

            if (owned.Count == 0)
            {
                _dealershipMenu.Add(new NativeItem(
                        L("PM_NoVehicles", "Không có phương tiện"),
                        L("PM_NoVehiclesDesc", "Danh sách đang trống.")));
            }
            else
            {
                int index = 1;
                foreach (var pv in owned)
                {
                    bool locked = pv.IsCollateralLocked;
                    string modelName = GetVehicleDisplayName(pv.ModelHash);
                    string priceText = locked ? "" : FormatMoney(pv.PurchasePrice);
                    string desc = locked
                        ? GetCollateralLockedDescription()
                        : L("PM_DealershipVehicleItem_Desc", "Nhấn Enter để xem thông tin phương tiện này trước khi thanh lý và sẽ nhận lại một số tiền nhỏ. Bạn vẫn có thể sử dụng tạm thời nhưng sẽ có thể bị mất hoàn toàn.");

                    var item = new NativeItem(
                        string.Format(
                            L("PM_DealershipVehicleItem_Title", "{0}. {1}"),
                            index,
                            modelName),
                        desc,
                        priceText
                    );

                    if (locked)
                    {
                        _dealershipMainLockedBadgeItems.Add(item);
                        SetRightBadgeSetIfExists(item, _lockBadge);
                    }

                    var captured = pv;
                    item.Activated += (s, e) =>
                    {
                        QueueUiAction(() =>
                        {
                            OpenDealershipDetailMenu(captured);
                        });
                    };

                    _dealershipMenu.Add(item);
                    index++;
                }
            }

            var quickSellItem = new NativeItem(
                L("PM_QuickSell_SelectVehicles", "Thanh lý nhanh phương tiện"),
                L("PM_QuickSell_SelectVehicles_Desc", "Chọn nhiều xe mà bạn muốn cùng lúc để thanh lý chung một lần.")
            );
            quickSellItem.Activated += (s, e) =>
            {
                QueueUiAction(() =>
                {
                    OpenDealershipBulkSellMenu();
                });
            };
            _dealershipMenu.Add(quickSellItem);

            if (_dealershipMenu.Items.Count > 0)
            {
                try { _dealershipMenu.SelectedIndex = 0; } catch { }
            }

            UpdateConditionalLockBadges(_dealershipMenu, _dealershipMainLockedBadgeItems);
        }
        catch (Exception ex)
        {
            Log("RefreshDealershipMenu failed: " + ex.ToString());
        }
    }

    private int GetVehicleQuickSellRefund(PersistentVehicle pv)
    {
        try
        {
            if (pv == null)
                return 0;

            double damagePct;
            bool hasDamage = TryGetVehicleDamagePercent(pv, out damagePct);

            double refundRate = hasDamage ? GetDealershipRefundRateByDamage(damagePct) : REFUND_PERCENT;
            if (refundRate <= 0.0)
                return 0;

            if (pv.PurchasePrice <= 0)
                return 0;

            return (int)Math.Round(pv.PurchasePrice * refundRate);
        }
        catch
        {
            return 0;
        }
    }

    private int RecalculateBulkSellTotal()
    {
        try
        {
            int total = 0;

            foreach (var pv in _dealershipBulkSelected.ToList())
            {
                total += GetVehicleQuickSellRefund(pv);
            }

            _dealershipBulkSellTotal = total;
            return total;
        }
        catch
        {
            _dealershipBulkSellTotal = 0;
            return 0;
        }
    }

    private void OpenDealershipBulkSellMenu()
    {
        try
        {
            List<PersistentVehicle> owned = GetOwnedVehiclesForCurrentPlayer();

            _dealershipBulkSelected.Clear();

            if (_dealershipBulkMenu == null || !_dealershipBulkMenuBuilt)
            {
                BuildDealershipBulkSellMenu(_currentDealershipAlias, owned.Count, true);
            }
            else
            {
                RefreshDealershipBulkSellMenu();
            }

            if (_dealershipMenu != null)
                _dealershipMenu.Visible = false;

            if (_dealershipDetailMenu != null)
                _dealershipDetailMenu.Visible = false;

            if (_dealershipBulkMenu != null)
            {
                ConfigureDealershipMenuInput(_dealershipBulkMenu);
                _dealershipBulkMenu.Visible = true;
                Interval = 0;
            }
        }
        catch (Exception ex)
        {
            Log("OpenDealershipBulkSellMenu failed: " + ex.ToString());
        }
    }

    private void BuildDealershipBulkSellMenu(string dealerName, int ownedCount, bool forceRebuild = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dealerName))
                dealerName = "Dealership";

            if (!forceRebuild && _dealershipBulkMenuBuilt && _dealershipBulkMenu != null)
                return;

            try { if (_dealershipBulkMenu != null) _dealershipBulkMenu.Visible = false; } catch { }

            string subtitle = string.Format(
                L("PM_BulkSell_Subtitle", "CHỌN NHỮNG PHƯƠNG TIỆN MUỐN THANH LÝ ({0} xe)"),
                ownedCount
            );

            _dealershipBulkMenu = new NativeMenu(
                dealerName,
                L("PM_BulkSell_Title", "THANH LÝ NHANH"),
                subtitle
            );
            _dealershipBulkMenu.KeepNameCasing = true;
            _dealershipBulkMenu.MaxItems = 8;
            _dealershipBulkMenu.NoItemsText = L("PM_BulkSell_NoItems", "Không có phương tiện để thanh lý");
            ConfigureDealershipMenuInput(_dealershipBulkMenu);
            _menuPool.Add(_dealershipBulkMenu);

            _dealershipBulkMenuBuilt = true;

            RefreshDealershipBulkSellMenu();
        }
        catch (Exception ex)
        {
            Log("BuildDealershipBulkSellMenu failed: " + ex.ToString());
        }
    }

    private void RefreshDealershipBulkSellMenu()
    {
        try
        {
            if (_dealershipBulkMenu == null)
                return;

            _dealershipBulkLockedBadgeItems.Clear();
            _dealershipBulkSelected.RemoveWhere(v => v == null || v.IsCollateralLocked);

            int oldIndex = 0;
            try { oldIndex = _dealershipBulkMenu.SelectedIndex; } catch { }

            _dealershipBulkMenu.Clear();

            List<PersistentVehicle> owned = GetOwnedVehiclesForCurrentPlayer();

            if (owned.Count == 0)
            {
                _dealershipBulkMenu.Add(new NativeItem(
                    L("PM_BulkSell_NoOwned", "Không có phương tiện"),
                    L("PM_BulkSell_NoOwnedDesc", "Bạn chưa sở hữu phương tiện nào để thanh lý nhanh.")
                ));
            }
            else
            {
                foreach (var pv in owned)
                {
                    bool selected = _dealershipBulkSelected.Contains(pv);
                    string modelName = GetVehicleDisplayName(pv.ModelHash);

                    if (pv.IsCollateralLocked)
                    {
                        var lockedItem = new NativeItem(
                            modelName,
                            GetCollateralLockedDescription()
                        );

                        _dealershipBulkLockedBadgeItems.Add(lockedItem);
                        SetRightBadgeSetIfExists(lockedItem, _lockBadge);

                        lockedItem.Activated += (s, e) => { };

                        _dealershipBulkMenu.Add(lockedItem);
                    }
                    else
                    {
                        int refund = GetVehicleQuickSellRefund(pv);

                        string desc = string.Format(
                            L("PM_BulkSell_VehicleDesc",
                              "Lựa chọn chọn hoặc gỡ chiếc xe này.\n" +
                              "Giá thanh lý dự kiến: {0}"),
                            FormatMoney(refund)
                        );

                        var item = new NativeCheckboxItem(
                            modelName,
                            desc,
                            selected
                        );

                        var captured = pv;
                        item.CheckboxChanged += (s, e) =>
                        {
                            try
                            {
                                if (item.Checked)
                                    _dealershipBulkSelected.Add(captured);
                                else
                                    _dealershipBulkSelected.Remove(captured);

                                RecalculateBulkSellTotal();
                                RefreshDealershipBulkSellMenu();
                            }
                            catch (Exception ex)
                            {
                                Log("Bulk checkbox change failed: " + ex.ToString());
                            }
                        };

                        _dealershipBulkMenu.Add(item);
                    }
                }
            }

            int total = RecalculateBulkSellTotal();

            _dealershipBulkSellConfirmItem = new NativeItem(
                L("PM_BulkSell_Confirm", "Xác nhận thanh lý"),
                string.Format(
                    L("PM_BulkSell_ConfirmDesc", "Tổng số tiền thanh lý cho các xe đã chọn mà bạn sẽ nhận lại là ~HUD_COLOUR_DEGEN_GREEN~{0}~s~"),
                    FormatMoney(total)
                )
            );
            _dealershipBulkSellConfirmItem.Activated += (s, e) =>
            {
                QueueUiAction(() =>
                {
                    SellSelectedVehiclesQuickly();
                });
            };
            _dealershipBulkMenu.Add(_dealershipBulkSellConfirmItem);

            var backItem = new NativeItem(
                L("PM_BulkSell_Back", "Quay lại danh sách phương tiện"),
                L("PM_BulkSell_BackDesc", "Quay về danh sách phương tiện đã sở hữu")
            );
            backItem.Activated += (s, e) =>
            {
                QueueUiAction(() =>
                {
                    if (_dealershipBulkMenu != null)
                        _dealershipBulkMenu.Visible = false;

                    if (_dealershipMenu != null)
                        _dealershipMenu.Visible = true;
                });
            };
            _dealershipBulkMenu.Add(backItem);

            try
            {
                int maxIndex = Math.Max(0, _dealershipBulkMenu.Items.Count - 1);
                _dealershipBulkMenu.SelectedIndex = Math.Min(oldIndex, maxIndex);
            }
            catch { }

            UpdateConditionalLockBadges(_dealershipBulkMenu, _dealershipBulkLockedBadgeItems);
        }
        catch (Exception ex)
        {
            Log("RefreshDealershipBulkSellMenu failed: " + ex.ToString());
        }
    }

    private void SellSelectedVehiclesQuickly()
    {
        try
        {
            List<PersistentVehicle> selected = _dealershipBulkSelected
                .Where(v => v != null)
                .ToList();

            if (selected.Count == 0)
            {
                Notification.Show(L("PM_BulkSell_NoSelection", "Bạn chưa chọn phương tiện nào."));
                return;
            }

            int totalRefund = 0;
            int soldCount = 0;

            foreach (var pv in selected)
            {
                try
                {
                    if (pv.IsCollateralLocked)
                    {
                        continue;
                    }

                    int refund = GetVehicleQuickSellRefund(pv);
                    totalRefund += refund;
                    soldCount++;
                }
                catch { }
            }

            if (totalRefund > 0)
                Game.Player.Money += totalRefund;

            foreach (var pv in selected)
            {
                try
                {
                    if (pv.IsCollateralLocked)
                    {
                        continue;
                    }

                    RemovePersistentVehicleSafe(pv);
                }
                catch { }
            }

            _dealershipBulkSelected.Clear();
            _vehiclesDirty = true;

            ShowFeedMessage(
                _currentDealershipAlias,
                L("PM_BulkSell_Subject", "Thanh lý nhanh hoàn tất"),
                string.Format(
                    L("PM_BulkSell_Body", "Đã thanh lý nhanh ~y~{0}~s~ phương tiện và nhận lại ~g~+{1}~s~."),
                    soldCount,
                    FormatMoney(totalRefund)
                )
            );

            RefreshDealershipMenu();

            if (_dealershipBulkMenu != null)
                _dealershipBulkMenu.Visible = false;

            if (_dealershipMenu != null)
                _dealershipMenu.Visible = true;
        }
        catch (Exception ex)
        {
            Log("SellSelectedVehiclesQuickly failed: " + ex.ToString());
        }
    }

    private void BuildDealershipDetailMenu()
    {
        try
        {
            if (_dealershipDetailMenuBuilt)
                return;

            _dealershipDetailMenu = new NativeMenu(
                   "Dealership",
                   L("PM_DealershipDetailMenu_Title", "THÔNG TIN THANH LÝ CHI TIẾT"),
                   L("PM_DealershipDetailMenu_Subtitle", "Thông tin phương tiện và số tiền"));
            _dealershipDetailMenu.KeepNameCasing = true;
            _dealershipDetailMenu.MaxItems = 8;
            _dealershipDetailMenu.NoItemsText = L("PM_DealershipDetailMenu_NoItems", "Không có dữ liệu");
            ConfigureDealershipMenuInput(_dealershipDetailMenu);
            _menuPool.Add(_dealershipDetailMenu);

            _dealershipDetailMenuBuilt = true;
        }
        catch (Exception ex)
        {
            Log("BuildDealershipDetailMenu failed: " + ex.ToString());
        }
    }

    private void OpenDealershipDetailMenu(PersistentVehicle pv)
    {
        try
        {
            if (pv == null)
                return;

            if (_dealershipDetailMenu == null)
                BuildDealershipDetailMenu();

            _selectedDealershipVehicle = pv;
            RefreshDealershipDetailMenu();

            if (_dealershipMenu != null)
                _dealershipMenu.Visible = false;

            if (_dealershipDetailMenu != null)
            {
                ConfigureDealershipMenuInput(_dealershipMenu);
                ConfigureDealershipMenuInput(_dealershipDetailMenu);
                _dealershipDetailMenu.Visible = true;
                Interval = 0;
            }
        }
        catch (Exception ex)
        {
            Log("OpenDealershipDetailMenu failed: " + ex.ToString());
        }
    }

    private void RefreshDealershipDetailMenu()
    {
        try
        {
            if (_dealershipDetailMenu == null)
                return;

            _dealershipDetailLockedBadgeItems.Clear();
            _dealershipDetailMenu.Clear();

            var pv = _selectedDealershipVehicle;
            if (pv == null)
            {
                _dealershipDetailMenu.Add(new NativeItem(
                       L("PM_NoData", "Không có dữ liệu"),
                       L("PM_SelectedVehicleNotFound", "Không tìm thấy phương tiện được chọn.")));
                return;
            }

            string vehicleName = GetVehicleDisplayName(pv.ModelHash);

            string buyPriceText = FormatMoney(pv.PurchasePrice);
            string plateText = string.IsNullOrWhiteSpace(pv.Plate) ? "Chưa có" : pv.Plate.Trim();
            string ownerName = GetCharacterDisplayNameFromHash(pv.OwnerModelHash);

            double damagePct;
            bool hasDamage = TryGetVehicleDamagePercent(pv, out damagePct);

            double refundRate = hasDamage ? GetDealershipRefundRateByDamage(damagePct) : REFUND_PERCENT;
            int estimatedRefund = (pv.PurchasePrice > 0)
                ? (int)Math.Round(pv.PurchasePrice * refundRate)
                : 0;

            EnsureDealershipVehicleStatsPanel();
            UpdateDealershipVehicleStatsPanel(pv.ModelHash);

            var line1 = new NativeItem(
                   string.Format(L("PM_VehicleName_Label", "Tên phương tiện: {0}"), vehicleName),
                   string.Format(L("PM_MarketPrice_Label", "Giá thị trường: {0}"), buyPriceText)
            )
            {
                Panel = _dealershipVehicleStatsPanel
            };

            if (pv.IsCollateralLocked)
            {
                _dealershipDetailLockedBadgeItems.Add(line1);
                SetRightBadgeSetIfExists(line1, _lockBadge);
            }
            _dealershipDetailMenu.Add(line1);

            string vehicleClassName = GetVehicleClassDisplayName(pv.ModelHash);

            var line1b = new NativeItem(
                  string.Format(L("PM_VehicleClass_Label", "Loại phương tiện: {0}"), vehicleClassName),
                  L("PM_VehicleClass_Desc", "Đây là lớp phương tiện chính thức (chưa bao gồm tính năng) của chiếc phương tiện đang xem xét thanh lý.")
            );
            _dealershipDetailMenu.Add(line1b);

            var line2 = new NativeItem(
                  string.Format(L("PM_Plate_Label", "Biển số xe: {0}"), plateText),
                  L("PM_Plate_Desc", "Biển số xe hiện tại được gắn trên phương tiện hiện đang chuẩn bị thanh lý.")
            );
            _dealershipDetailMenu.Add(line2);

            var line3 = new NativeItem(
                string.Format(L("PM_Owner_Label", "Chủ sở hữu: {0}"), ownerName),
                L("PM_Owner_Desc", "Tên chủ xe của chiếc phương tiện đang được xét giá trước khi thanh lý tại đại lý.")
            );
            _dealershipDetailMenu.Add(line3);

            var line4 = new NativeItem(
                string.Format(
                L("PM_Damage_Label", "Mức khấu hao: {0}"),
                hasDamage ? damagePct.ToString("0") + "%" : L("PM_Unknown", "Không xác định")),
                L("PM_Damage_Desc", "Xét theo tình trạng động cơ và thân phương tiện hiện tại của bạn")
            );
            _dealershipDetailMenu.Add(line4);

            var line5 = new NativeItem(
                 string.Format(L("PM_EstRefund_Label", "Mức thanh lý dự kiến: {0}"), FormatMoney(estimatedRefund)),
                 hasDamage
                 ? string.Format(L("PM_RefundRate_Label", "Số tiền hoàn lại dự tính: {0}%"), (refundRate * 100.0).ToString("0"))
                 : L("PM_EstRefund_Unknown", "Do phương tiện hiện tại không thể xác định được nên dự tính khoảng nhiêu đây.")
            );
            _dealershipDetailMenu.Add(line5);

            int lossValue = Math.Max(0, pv.PurchasePrice - estimatedRefund);

            var line6 = new NativeItem(
                  string.Format(L("PM_LossValue_Label", "Giá trị hao hụt: {0}"), FormatMoney(lossValue)),
                  L("PM_LossValue_Desc", "Đây là phần số tiền bị mất sau khi khấu hao và thanh lý tài sản hoàn tất.")
            );
            _dealershipDetailMenu.Add(line6);

            string sellDesc = pv.IsCollateralLocked
                ? GetCollateralLockedDescription()
                : L("PM_ConfirmSell_Desc", "Nhận số tiền hoàn trả về và phương tiện sẽ được hủy bỏ, nhưng vẫn có thể được sử dụng tạm thời.");

            var sellItem = new NativeItem(
                  L("PM_ConfirmSell", "Xác nhận bán phương tiện cho đại lý"),
                  sellDesc);

            sellItem.Activated += (s, e) =>
            {
                QueueUiAction(() =>
                {
                    SellPersistentVehicleByDamage(pv);
                    RefreshDealershipMenu();

                    if (_dealershipMenu != null && _dealershipMenu.Items.Count > 0)
                    {
                        try { _dealershipMenu.SelectedIndex = 0; } catch { }
                    }

                    if (_dealershipMenu != null)
                        _dealershipMenu.Visible = true;

                    if (_dealershipDetailMenu != null)
                        _dealershipDetailMenu.Visible = false;
                });
            };
            _dealershipDetailMenu.Add(sellItem);

            var backItem = new NativeItem(
                L("PM_BackToVehicleList", "Quay lại danh sách phương tiện"),
                L("PM_BackToVehicleList_Desc", "Trở về danh sách phương tiện đã sở hữu"));
            backItem.Activated += (s, e) =>
            {
                QueueUiAction(() =>
                {
                    if (_dealershipDetailMenu != null)
                        _dealershipDetailMenu.Visible = false;

                    if (_dealershipMenu != null)
                        _dealershipMenu.Visible = true;
                });
            };
            _dealershipDetailMenu.Add(backItem);

            UpdateConditionalLockBadges(_dealershipDetailMenu, _dealershipDetailLockedBadgeItems);
        }
        catch (Exception ex)
        {
            Log("RefreshDealershipDetailMenu failed: " + ex.ToString());
        }
    }

    private static bool TryGetVehicleDamagePercent(PersistentVehicle pv, out double damagePercent)
    {
        damagePercent = 0.0;

        try
        {
            if (pv == null || pv.RuntimeVehicle == null || !pv.RuntimeVehicle.Exists())
                return false;

            Vehicle v = pv.RuntimeVehicle;

            double engine = v.EngineHealth;
            double body = v.BodyHealth;

            if (engine < 0.0) engine = 0.0;
            if (body < 0.0) body = 0.0;
            if (engine > 1000.0) engine = 1000.0;
            if (body > 1000.0) body = 1000.0;

            // 0% = còn nguyên, 100% = hỏng hoàn toàn
            double healthAvg = (engine + body) / 2000.0;
            damagePercent = 100.0 * (1.0 - healthAvg);

            if (damagePercent < 0.0) damagePercent = 0.0;
            if (damagePercent > 100.0) damagePercent = 100.0;

            damagePercent = Math.Round(damagePercent, 0);
            return true;
        }
        catch
        {
            damagePercent = 0.0;
            return false;
        }
    }

    private static double GetDealershipRefundRateByDamage(double damagePercent)
    {
        if (damagePercent <= 0.0) return 0.50;
        if (damagePercent <= 10.0) return 0.48;
        if (damagePercent <= 20.0) return 0.46;
        if (damagePercent <= 30.0) return 0.44;
        if (damagePercent <= 40.0) return 0.42;
        if (damagePercent <= 50.0) return 0.40;
        if (damagePercent <= 60.0) return 0.38;
        if (damagePercent <= 70.0) return 0.36;
        if (damagePercent <= 80.0) return 0.34;
        if (damagePercent <= 90.0) return 0.32;
        if (damagePercent <= 99.0) return 0.30;

        // 100% = hỏng hoàn toàn
        return 0.0;
    }

    private void SellPersistentVehicleByDamage(PersistentVehicle pv)
    {
        try
        {
            if (pv == null)
                return;

            if (pv.IsCollateralLocked)
            {
                Notification.Show(L("PM_CollateralLockedSellBlocked", "~r~Xe này đang được thế chấp, không thể thanh lý."));
                return;
            }

            double damagePct;
            bool hasDamage = TryGetVehicleDamagePercent(pv, out damagePct);

            double refundRate = hasDamage ? GetDealershipRefundRateByDamage(damagePct) : REFUND_PERCENT;

            if (refundRate <= 0.0)
            {
                Notification.Show(L("PM_VehicleFullyDestroyed", "~r~~h~Phương tiện đã hỏng hoàn toàn, không thể bán."));
                return;
            }

            int refund = 0;
            if (pv.PurchasePrice > 0)
                refund = (int)Math.Round(pv.PurchasePrice * refundRate);

            if (refund > 0)
                Game.Player.Money += refund;

            string vehicleName = GetVehicleDisplayName(pv.ModelHash);

            ShowFeedMessage(
                _currentDealershipAlias,
                L("PM_Feed_VehicleRefundSubject", "Hoàn tiền phương tiện"),
                string.Format(
                    L("PM_Feed_VehicleRefundBody",
                    "Hoàn trả ~HUD_COLOUR_DEGEN_YELLOW~{0}~s~ với số tiền ~HUD_COLOUR_DEGEN_GREEN~+${1:N0}~s~. Nếu phương tiện không còn sử dụng hãy đưa đến các đại lý khác nhau trên bản đồ nhé!"),
                    vehicleName,
                    refund)
            );

            RemovePersistentVehicleSafe(pv);
            _vehiclesDirty = true;
        }
        catch (Exception ex)
        {
            Log("SellPersistentVehicleByDamage failed: " + ex.ToString());
        }
    }

    private void QueueUiAction(Action action)
    {
        if (action == null) return;

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
                catch (Exception ex)
                {
                    Log("FlushUiActions action failed: " + ex.ToString());
                }
            }
        }
        catch (Exception ex)
        {
            Log("FlushUiActions failed: " + ex.ToString());
        }
    }

    private bool IsPlayerInsideDealershipZone()
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return false;

            Vector3 pos = player.Position;
            foreach (var point in _dealershipPoints)
            {
                if (pos.DistanceTo(point) <= DEALERSHIP_INTERACT_RADIUS)
                    return true;
            }
        }
        catch { }

        return false;
    }

    private void UpdateDealershipWorldState()
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
            {
                _playerInsideDealership = false;
                _dealershipNeedsFastTick = false;
                return;
            }

            Vector3 pos = player.Position;
            bool inside = false;
            bool nearEnoughToDraw = false;

            foreach (var point in _dealershipPoints)
            {
                float dist = pos.DistanceTo(point);

                if (dist <= DEALERSHIP_DRAW_RADIUS)
                {
                    nearEnoughToDraw = true;
                    DrawDealershipMarker(point);
                }

                if (dist <= DEALERSHIP_INTERACT_RADIUS)
                    inside = true;
            }

            _playerInsideDealership = inside;

            if (inside && _dealershipSignalActive)
                StopDealershipSignal();

            _dealershipNeedsFastTick =
                 nearEnoughToDraw
                 || (_dealershipMenu != null && _dealershipMenu.Visible)
                 || (_dealershipDetailMenu != null && _dealershipDetailMenu.Visible)
                 || (_dealershipBulkMenu != null && _dealershipBulkMenu.Visible)
                 || _dealershipSignalActive;

            UpdateAllDealershipLockedBadges();
        }
        catch (Exception ex)
        {
            Log("UpdateDealershipWorldState failed: " + ex.ToString());
        }
    }

    private void DrawDealershipMarker(Vector3 pos)
    {
        try
        {
            // marker vàng nhỏ
            World.DrawMarker(
                MarkerType.VerticalCylinder,
                new Vector3(pos.X, pos.Y, pos.Z - 1.45f),
                Vector3.Zero,
                Vector3.Zero,
                new Vector3(0.85f, 0.85f, 0.85f),
                Color.FromArgb(220, 0, 255, 0)
            );
        }
        catch { }
    }

    // ===== Patch: helper lấy tên hiển thị cho modelHash với fallback =====
    private void EnsureDealershipVehicleStatsPanel()
    {
        if (_dealershipVehicleStatsPanel != null)
            return;

        _dealershipStatTopSpeed = new NativeStatsInfo(L("PM_Stat_TopSpeed", "Tốc độ cao nhất"), 0);
        _dealershipStatAcceleration = new NativeStatsInfo(L("PM_Stat_Acceleration", "Gia tốc"), 0);
        _dealershipStatBraking = new NativeStatsInfo(L("PM_Stat_Braking", "Phanh"), 0);
        _dealershipStatTraction = new NativeStatsInfo(L("PM_Stat_Traction", "Độ bám đường"), 0);

        _dealershipVehicleStatsPanel = new NativeStatsPanel(
            _dealershipStatTopSpeed,
            _dealershipStatAcceleration,
            _dealershipStatBraking,
            _dealershipStatTraction
        )
        {
            BackgroundColor = Color.FromArgb(180, 28, 28, 28),
            ForegroundColor = Color.FromArgb(255, 255, 255, 255),
            Visible = true
        };
    }

    private void UpdateDealershipVehicleStatsPanel(uint modelHash)
    {
        EnsureDealershipVehicleStatsPanel();

        float topSpeed = SafeCall(() => Function.Call<float>(Hash.GET_VEHICLE_MODEL_ESTIMATED_MAX_SPEED, modelHash), 0f);
        float acceleration = SafeCall(() => Function.Call<float>(Hash.GET_VEHICLE_MODEL_ACCELERATION, modelHash), 0f);
        float braking = SafeCall(() => Function.Call<float>(Hash.GET_VEHICLE_MODEL_MAX_BRAKING, modelHash), 0f);
        float traction = SafeCall(() => Function.Call<float>(Hash.GET_VEHICLE_MODEL_MAX_TRACTION, modelHash), 0f);

        _dealershipStatTopSpeed.Name = L("PM_Stat_TopSpeed", "Tốc độ cao nhất");
        _dealershipStatAcceleration.Name = L("PM_Stat_Acceleration", "Gia tốc");
        _dealershipStatBraking.Name = L("PM_Stat_Braking", "Phanh");
        _dealershipStatTraction.Name = L("PM_Stat_Traction", "Độ bám đường");

        // Bar hiển thị 0..100, chỉ để trực quan hóa
        _dealershipStatTopSpeed.Value = NormalizeStat(topSpeed, 60f);
        _dealershipStatAcceleration.Value = NormalizeStat(acceleration, 1.20f);
        _dealershipStatBraking.Value = NormalizeStat(braking, 1.50f);
        _dealershipStatTraction.Value = NormalizeStat(traction, 3.50f);
    }

    private void ConfigureDealershipMenuInput(NativeMenu menu)
    {
        if (menu == null) return;

        try
        {
            menu.MouseBehavior = MenuMouseBehavior.Disabled;
        }
        catch (Exception ex)
        {
            Log("ConfigureDealershipMenuInput failed: " + ex.Message);
        }
    }

    private static string GetCharacterDisplayNameFromHash(int modelHash)
    {
        switch (modelHash)
        {
            case -1692214353: return "Franklin";
            case 225514697: return "Michael";
            case -1686040670: return "Trevor";
            default:
                return $"0x{unchecked((uint)modelHash):X}";
        }
    }

    private string PickRandomDealershipAlias()
    {
        lock (_dealershipAliasLock)
        {
            if (_dealershipAliasPool == null || _dealershipAliasPool.Length == 0)
                return "Dealership";

            if (_dealershipAliasPool.Length == 1)
                return _dealershipAliasPool[0];

            string picked = _dealershipAliasPool[_dealershipAliasRng.Next(_dealershipAliasPool.Length)];
            int guard = 0;

            while (picked == _lastDealershipAlias && guard < 8)
            {
                picked = _dealershipAliasPool[_dealershipAliasRng.Next(_dealershipAliasPool.Length)];
                guard++;
            }

            _lastDealershipAlias = picked;
            return picked;
        }
    }

    private void StartDealershipSignal()
    {
        try
        {
            _dealershipSignalActive = true;
            _dealershipSignalVisible = true;
            _dealershipSignalEndTime = Game.GameTime + DEALERSHIP_SIGNAL_DURATION_MS;
            _dealershipSignalLastPulseTime = Game.GameTime;

            EnsureDealershipSignalBlips();
            ApplyDealershipSignalVisibility(true);

            _dealershipNeedsFastTick = true;
        }
        catch (Exception ex)
        {
            Log("StartDealershipSignal failed: " + ex.ToString());
        }
    }

    private void StopDealershipSignal()
    {
        try
        {
            _dealershipSignalActive = false;
            _dealershipSignalVisible = false;
            _dealershipSignalEndTime = 0;
            _dealershipSignalLastPulseTime = 0;

            ClearDealershipSignalBlips();
        }
        catch (Exception ex)
        {
            Log("StopDealershipSignal failed: " + ex.ToString());
        }
    }

    private void UpdateDealershipSignalState()
    {
        try
        {
            if (!_dealershipSignalActive)
                return;

            int now = Game.GameTime;

            if (now >= _dealershipSignalEndTime)
            {
                StopDealershipSignal();
                return;
            }

            if (now - _dealershipSignalLastPulseTime >= DEALERSHIP_SIGNAL_PULSE_MS)
            {
                _dealershipSignalVisible = !_dealershipSignalVisible;
                ApplyDealershipSignalVisibility(_dealershipSignalVisible);
                _dealershipSignalLastPulseTime = now;
            }

            _dealershipNeedsFastTick = true;
        }
        catch (Exception ex)
        {
            Log("UpdateDealershipSignalState failed: " + ex.ToString());
        }
    }

    private void EnsureDealershipSignalBlips()
    {
        try
        {
            if (_dealershipSignalBlips.Count == _dealershipPoints.Length)
                return;

            ClearDealershipSignalBlips();

            for (int i = 0; i < _dealershipPoints.Length; i++)
            {
                try
                {
                    var b = World.CreateBlip(_dealershipPoints[i]);
                    if (b != null)
                    {
                        b.IsShortRange = false;
                        b.Sprite = BlipSprite.Standard;
                        b.Color = BlipColor.Yellow;
                        b.Name = L("PM_DealershipPointBlip", "Điểm đại lý");
                        b.Alpha = 255;
                        b.IsFlashing = false;
                        _dealershipSignalBlips.Add(b);
                    }
                }
                catch (Exception ex)
                {
                    Log("EnsureDealershipSignalBlips inner failed: " + ex.ToString());
                }
            }
        }
        catch (Exception ex)
        {
            Log("EnsureDealershipSignalBlips failed: " + ex.ToString());
        }
    }

    private void ApplyDealershipSignalVisibility(bool visible)
    {
        try
        {
            for (int i = _dealershipSignalBlips.Count - 1; i >= 0; i--)
            {
                var b = _dealershipSignalBlips[i];
                try
                {
                    if (b == null || !b.Exists())
                    {
                        _dealershipSignalBlips.RemoveAt(i);
                        continue;
                    }

                    b.Alpha = visible ? 255 : 0;
                    b.IsFlashing = false;
                }
                catch
                {
                    _dealershipSignalBlips.RemoveAt(i);
                }
            }
        }
        catch (Exception ex)
        {
            Log("ApplyDealershipSignalVisibility failed: " + ex.ToString());
        }
    }

    private void ClearDealershipSignalBlips()
    {
        try
        {
            for (int i = _dealershipSignalBlips.Count - 1; i >= 0; i--)
            {
                try { SafeRemoveBlip(_dealershipSignalBlips[i]); } catch { }
            }
            _dealershipSignalBlips.Clear();
        }
        catch (Exception ex)
        {
            Log("ClearDealershipSignalBlips failed: " + ex.ToString());
        }
    }

    private static string GetVehicleDisplayName(uint modelHash)
    {
        try
        {
            // 1) nếu mapping có -> trả tên
            if (_vehicleNameMap != null && _vehicleNameMap.TryGetValue(modelHash, out var knownName) && !string.IsNullOrWhiteSpace(knownName))
                return knownName;

            // 2) thử native GXT / display name (nếu native có sẵn)
            try
            {
                // Một số native có tên tương tự; dùng try/catch cho an toàn
                var name = Function.Call<string>(Hash.GET_DISPLAY_NAME_FROM_VEHICLE_MODEL, modelHash);
                if (!string.IsNullOrEmpty(name))
                {
                    // Nếu name giống mã GXT (ví dụ "HAKUCHOU2") — vẫn trả raw, nhưng tốt hơn có mapping
                    return name;
                }
            }
            catch { /* ignore native lookup errors */ }

            // 3) fallback: trả tên model object nếu có
            try
            {
                var m = new Model((int)modelHash);
                if (m != null && m.IsValid)
                {
                    // Model.ToString() thường trả tên model (không hoàn hảo nhưng hữu dụng)
                    return m.ToString();
                }
            }
            catch { }

            // 4) cuối cùng: trả hex nếu không còn cách nào khác
            return $"0x{modelHash:X}";
        }
        catch { return $"0x{modelHash:X}"; }
    }

    private static string GetVehicleClassDisplayName(uint modelHash)
    {
        try
        {
            int vehicleClass = Function.Call<int>(Hash.GET_VEHICLE_CLASS_FROM_NAME, modelHash);

            switch (vehicleClass)
            {
                case 0: return L("PM_VClass_Compacts", "Compacts");
                case 1: return L("PM_VClass_Sedans", "Sedans");
                case 2: return L("PM_VClass_SUVs", "SUVs");
                case 3: return L("PM_VClass_Coupes", "Coupes");
                case 4: return L("PM_VClass_Muscle", "Muscle");
                case 5: return L("PM_VClass_SportsClassics", "Sports Classics");
                case 6: return L("PM_VClass_Sports", "Sports");
                case 7: return L("PM_VClass_Super", "Super");
                case 8: return L("PM_VClass_Motorcycles", "Motorcycles");
                case 9: return L("PM_VClass_OffRoad", "Off-Road");
                case 10: return L("PM_VClass_Industrial", "Industrial");
                case 11: return L("PM_VClass_Utility", "Utility");
                case 12: return L("PM_VClass_Vans", "Vans");
                case 13: return L("PM_VClass_Cycles", "Cycles");
                case 14: return L("PM_VClass_Boats", "Boats");
                case 15: return L("PM_VClass_Helicopters", "Helicopters");
                case 16: return L("PM_VClass_Planes", "Planes");
                case 17: return L("PM_VClass_Service", "Service");
                case 18: return L("PM_VClass_Emergency", "Emergency");
                case 19: return L("PM_VClass_Military", "Military");
                case 20: return L("PM_VClass_Commercial", "Commercial");
                case 21: return L("PM_VClass_Trains", "Trains");
                case 22: return L("PM_VClass_OpenWheel", "Open Wheel");
                default: return string.Format(L("PM_VClass_Unknown", "Unknown ({0})"), vehicleClass);
            }
        }
        catch
        {
            return L("PM_Unknown", "Không xác định");
        }
    }

    // ===== Patch: helper tìm modelHash từ tên hoặc từ chuỗi có chứa 0xHEX (tương thích ngược) =====
    private static bool TryGetModelHashFromName(string input, out uint modelHash)
    {
        modelHash = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(input)) return false;
            var s = input.Trim();

            // 1) Nếu chuỗi chứa "0x" -> thử extract phần hex ngay sau "0x"
            int idx = s.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && idx + 2 < s.Length)
            {
                string after = s.Substring(idx + 2); // phần sau "0x"
                                                     // Lấy các ký tự hex liên tiếp từ đầu của 'after'
                var sb = new StringBuilder();
                for (int i = 0; i < after.Length; i++)
                {
                    char c = after[i];
                    bool isHexChar =
                        (c >= '0' && c <= '9') ||
                        (c >= 'A' && c <= 'F') ||
                        (c >= 'a' && c <= 'f');
                    if (isHexChar) sb.Append(c);
                    else break;
                }

                if (sb.Length > 0)
                {
                    uint parsed;
                    if (uint.TryParse(sb.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed))
                    {
                        modelHash = parsed;
                        return true;
                    }
                }
            }

            // 2) So khớp chính xác (case-insensitive) theo bảng ánh xạ _vehicleNameMap
            if (_vehicleNameMap != null)
            {
                foreach (var kv in _vehicleNameMap)
                {
                    if (string.Equals(kv.Value, s, StringComparison.OrdinalIgnoreCase))
                    {
                        modelHash = kv.Key;
                        return true;
                    }
                }

                // 3) fallback: tìm entry có chứa chuỗi (partial match)
                foreach (var kv in _vehicleNameMap)
                {
                    if (!string.IsNullOrEmpty(kv.Value) &&
                        kv.Value.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        modelHash = kv.Key;
                        return true;
                    }
                }
            }

            // 4) cuối cùng: thử parse như hex không có prefix (ví dụ file cũ chứa "F0C2A91F")
            uint parsed2;
            if (uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed2))
            {
                modelHash = parsed2;
                return true;
            }
        }
        catch
        {
            // swallow errors intentionally
        }

        return false;
    }

    private static string FormatMoney(int value)
    {
        if (value < 0) value = 0;
        return string.Format(CultureInfo.InvariantCulture, "${0:N0}", value);
    }
}