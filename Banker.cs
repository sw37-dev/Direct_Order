using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using iFruitAddon2;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

public partial class FleecaBankLoanScript : Script
{
    private const float ChairSearchRadius = 12.0f;
    private const string ChairScenarioName = "PROP_HUMAN_SEAT_CHAIR";

    private const float DefaultInteractionRadius = 5.0f;
    private const int ContactDialTimeoutMs = 3000;
    private const int SpawnDelayMs = 2000;

    private static readonly Random _random = new Random();

    private sealed class BankerSpawnPoint
    {
        public Vector3 Position { get; }
        public bool UseChairLogic { get; }
        public float InteractionRadius { get; }

        public BankerSpawnPoint(Vector3 position, bool useChairLogic, float interactionRadius)
        {
            Position = position;
            UseChairLogic = useChairLogic;
            InteractionRadius = interactionRadius;
        }
    }

    private static readonly BankerSpawnPoint[] BankerSpawnPoints = new[]
    {
        new BankerSpawnPoint(new Vector3(-1573.982000f, -612.021700f, 31.282150f), true, 3.0f),
        new BankerSpawnPoint(new Vector3(313.393500f, -280.319500f, 54.164710f), false, 5.0f),
        new BankerSpawnPoint(new Vector3(148.038100f, -1041.643000f, 29.367920f), false, 5.0f),
        new BankerSpawnPoint(new Vector3(-1211.274000f, -330.062000f, 37.787090f), false, 5.0f),
        new BankerSpawnPoint(new Vector3(-351.272400f, -51.300290f, 49.036500f), false, 5.0f),
        new BankerSpawnPoint(new Vector3(-813.587800f, -1114.504000f, 11.181200f), false, 5.0f),
        new BankerSpawnPoint(new Vector3(-2961.064000f, 483.074200f, 15.697020f), false, 5.0f),
        new BankerSpawnPoint(new Vector3(1175.745000f, 2708.250000f, 38.087920f), false, 5.0f)
    };

    private Ped _bankerNpc = null;
    private Blip _bankerBlip = null;
    private BankerSpawnPoint _activeBankerSpawnPoint = null;

    private readonly List<Ped> _bankerNpcs = new List<Ped>();
    private readonly List<Blip> _bankerBlips = new List<Blip>();

    private bool _spawnRequested = false;
    private int _spawnExecuteAtGameTime = 0;

    private Ped _npcMovingToChair = null;
    private Prop _targetChairProp = null;
    private int _chairMoveDeadline = 0;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            if (IsAnyLoanMenuVisible())
            {
                if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Back)
                    CloseAllLoanMenus();

                return;
            }

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead)
                return;

            Ped nearestNpc = GetNearestBankerNpc(player.Position, GetCurrentInteractionRadius());
            if (nearestNpc == null)
                return;

            _bankerNpc = nearestNpc;

            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Return)
            {
                if (_activeBankerServiceMode == FleecaServiceMode.Savings)
                    BeginSavingsDialog();
                else
                    BeginLoanDialog();

                return;
            }

            if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
            {
                DespawnBankerNpc();
                return;
            }
        }
        catch (Exception ex)
        {
            Log("OnKeyDown failed: " + ex);
        }
    }

    private void HandleNpcSpawnTimer()
    {
        try
        {
            if (!_spawnRequested)
                return;

            if (Game.GameTime < _spawnExecuteAtGameTime)
                return;

            _spawnRequested = false;
            SpawnBankerNpc();
        }
        catch (Exception ex)
        {
            Log("HandleNpcSpawnTimer failed: " + ex);
        }
    }

    private void SpawnBankerNpc()
    {
        try
        {
            // Giữ lại mode dịch vụ hiện tại (Loan / Savings) trong lúc respawn NPC.
            // Nếu gọi DespawnBankerNpc() kiểu cũ ở đây thì nó sẽ reset mode về None,
            // làm prompt + Enter luôn rơi về logic vay tiền.
            FleecaServiceMode serviceMode = _activeBankerServiceMode;
            DespawnBankerNpc(false);
            _activeBankerServiceMode = serviceMode;

            if (BankerSpawnPoints == null || BankerSpawnPoints.Length == 0)
            {
                Log("SpawnBankerNpc failed: no spawn points.");
                return;
            }

            BankerSpawnPoint spawnPoint = BankerSpawnPoints[_random.Next(BankerSpawnPoints.Length)];
            _activeBankerSpawnPoint = spawnPoint;

            string[] modelsToTry = new[]
            {
            "a_m_y_business_01",
            "s_m_m_bankman_01",
            "s_m_y_cop_01"
        };

            Ped spawnedNpc = null;

            foreach (string modelName in modelsToTry)
            {
                spawnedNpc = TryCreatePed(modelName, spawnPoint.Position);
                if (spawnedNpc != null && spawnedNpc.Exists())
                    break;
            }

            if (spawnedNpc == null || !spawnedNpc.Exists())
            {
                _activeBankerSpawnPoint = null;
                Log("SpawnBankerNpc failed: all models failed.");
                return;
            }

            _bankerNpc = spawnedNpc;
            _bankerNpcs.Add(spawnedNpc);

            try
            {
                Blip blip = spawnedNpc.AddBlip();
                if (blip != null && blip.Exists())
                {
                    blip.IsShortRange = false;
                    blip.Sprite = BlipSprite.Standard;
                    blip.Color = BlipColor.Green;
                    Function.Call(Hash.SET_BLIP_COLOUR, blip.Handle, (int)BlipColor.Green);
                    SetBlipName(blip, BankerDisplayName);
                    _bankerBlips.Add(blip);

                    if (_bankerBlip == null)
                        _bankerBlip = blip;
                }
            }
            catch (Exception ex)
            {
                Notification.Show(L("Credit_ErrorCreateBlip", "Tạo blip lỗi."));
                Log("Create banker blip failed: " + ex);
            }

            if (spawnPoint.UseChairLogic)
            {
                TrySeatNpcOnNearestChair(spawnedNpc);
            }
            else
            {
                try { spawnedNpc.Task.ClearAllImmediately(); } catch { }
                try { spawnedNpc.Task.StandStill(-1); } catch { }
            }

            ShowFleecaNotification(
                L("Credit_RequestTitle", "Yêu cầu"),
                L("Credit_StaffWaiting", "Nhân viên ngân hàng đang đợi sẵn bạn rồi. Ra đó gặp để thực hiện giao dịch nhé!")
            );
        }
        catch (Exception ex)
        {
            Notification.Show(L("Credit_ErrorSpawnNpc", "Spawn NPC lỗi."));
            Log("SpawnBankerNpc failed: " + ex);
        }
    }

    private Ped GetNearestBankerNpc(Vector3 origin, float maxDistance)
    {
        try
        {
            Ped nearest = null;
            float bestDistance = maxDistance;

            for (int i = 0; i < _bankerNpcs.Count; i++)
            {
                Ped npc = _bankerNpcs[i];
                if (npc == null || !npc.Exists() || npc.IsDead)
                    continue;

                float distance = origin.DistanceTo(npc.Position);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    nearest = npc;
                }
            }

            return nearest;
        }
        catch (Exception ex)
        {
            Log("GetNearestBankerNpc failed: " + ex);
            return null;
        }
    }

    private float GetCurrentInteractionRadius()
    {
        try
        {
            if (_activeBankerSpawnPoint != null)
                return _activeBankerSpawnPoint.InteractionRadius;
        }
        catch { }

        return DefaultInteractionRadius;
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

            try
            {
                Function.Call(Hash.CLEAR_AREA, position.X, position.Y, position.Z, 3.5f, true, false, false, false);
            }
            catch { }

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

    private Prop FindNearestChairProp(Vector3 origin, float radius)
    {
        try
        {
            Prop nearest = null;
            float bestDistance = radius;

            foreach (Prop prop in World.GetAllProps())
            {
                if (prop == null || !prop.Exists())
                    continue;

                if (!IsChairModel(prop))
                    continue;

                float distance = prop.Position.DistanceTo(origin);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    nearest = prop;
                }
            }

            return nearest;
        }
        catch (Exception ex)
        {
            Log("FindNearestChairProp failed: " + ex);
            return null;
        }
    }

    private bool IsChairModel(Prop prop)
    {
        if (prop == null || !prop.Exists())
            return false;

        string modelName = prop.Model.ToString().ToLower();

        return modelName.Contains("chair")
            || modelName.Contains("seat")
            || modelName.Contains("bench")
            || modelName.Contains("stool")
            || modelName.Contains("sofa");
    }

    private void TrySeatNpcOnNearestChair(Ped npc)
    {
        try
        {
            if (npc == null || !npc.Exists() || npc.IsDead)
                return;

            Prop chair = FindNearestChairProp(npc.Position, ChairSearchRadius);
            if (chair == null || !chair.Exists())
            {
                try { npc.Task.WanderAround(); } catch { }
                return;
            }

            Vector3 chairPos = chair.Position;

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

            try
            {
                Function.Call(
                    Hash.TASK_FOLLOW_NAV_MESH_TO_COORD,
                    npc.Handle,
                    chairPos.X, chairPos.Y, chairPos.Z,
                    1.0f,
                    30000,
                    1.0f,
                    false,
                    0.0f);
            }
            catch
            {
                try { npc.Task.GoTo(chairPos); } catch { }
            }

            _npcMovingToChair = npc;
            _targetChairProp = chair;
            _chairMoveDeadline = Game.GameTime + 30000;
        }
        catch (Exception ex)
        {
            Log("TrySeatNpcOnNearestChair failed: " + ex);
        }
    }

    private void UpdateNpcSeatSequence()
    {
        try
        {
            if (_npcMovingToChair == null || !_npcMovingToChair.Exists())
            {
                _npcMovingToChair = null;
                _targetChairProp = null;
                return;
            }

            if (_targetChairProp == null || !_targetChairProp.Exists())
            {
                _npcMovingToChair = null;
                _targetChairProp = null;
                return;
            }

            if (Game.GameTime > _chairMoveDeadline)
            {
                try { _npcMovingToChair.Task.ClearAllImmediately(); } catch { }
                try { _npcMovingToChair.Task.StandStill(-1); } catch { }

                _npcMovingToChair = null;
                _targetChairProp = null;
                return;
            }

            float dist = _npcMovingToChair.Position.DistanceTo(_targetChairProp.Position);
            if (dist > 1.25f)
                return;

            Vector3 chairPos = _targetChairProp.Position;
            float chairHeading = _targetChairProp.Heading;

            try { _npcMovingToChair.Task.ClearAllImmediately(); } catch { }
            try { _npcMovingToChair.Position = chairPos; } catch { }
            try { _npcMovingToChair.Heading = chairHeading; } catch { }

            try
            {
                Function.Call(
                    Hash.TASK_START_SCENARIO_AT_POSITION,
                    _npcMovingToChair.Handle,
                    ChairScenarioName,
                    chairPos.X, chairPos.Y, chairPos.Z,
                    chairHeading,
                    -1,
                    true,
                    true);
            }
            catch
            {
                try { _npcMovingToChair.Task.StartScenarioInPlace(ChairScenarioName, -1, true); } catch { }
            }

            try { Function.Call(Hash.SET_PED_KEEP_TASK, _npcMovingToChair.Handle, true); } catch { }
            try { Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, _npcMovingToChair.Handle, true); } catch { }

            _npcMovingToChair = null;
            _targetChairProp = null;
        }
        catch (Exception ex)
        {
            Log("UpdateNpcSeatSequence failed: " + ex);
        }
    }

    private void UpdateNpcInteractionPrompt()
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead)
                return;

            Ped nearestNpc = GetNearestBankerNpc(player.Position, GetCurrentInteractionRadius());
            if (nearestNpc == null)
                return;

            _bankerNpc = nearestNpc;

            string help;

            if (_activeBankerServiceMode == FleecaServiceMode.Savings)
            {
                help = L(
                    "Credit_Help_Savings",
                    "~b~~h~GỬI TIỀN TIẾT KIỆM~h~~s~\nBạn hãy nhập số tiền cần gửi vào Ngân hàng Fleeca để sinh lời mỗi ngày nhé!\n~INPUT_FRONTEND_ACCEPT~ để nhập\n~INPUT_FRONTEND_LEFT~ để hủy");
            }
            else
            {
                if (_loanPackageFlowPending && _selectedLoanPackageAmount > 0)
                {
                    help = string.Format(
                        L("Credit_Help_PackageLoan",
                        "~b~~h~VAY NGÂN HÀNG~h~~s~\nBạn đã chọn gói vay {0} và có thực sự muốn vay khoản này không?\n{1} để vay\n{2} để hủy"),
                        FormatMoney(_selectedLoanPackageAmount),
                        "~INPUT_FRONTEND_ACCEPT~",
                        "~INPUT_FRONTEND_LEFT~");
                }
                else
                {
                    long creditPoints = GetCreditPoints();
                    long maxLoan = GetLoanLimitFromPoints(creditPoints);

                    if (maxLoan <= 0)
                    {
                        help = string.Format(
                            L("Credit_Help_NoLoan",
                            "Bạn chưa đủ điều kiện vay.\nCần chi tầm {0}.\n{1} để đóng thông báo\n{2} để hủy"),
                            FormatMoney(1000000),
                            "~INPUT_FRONTEND_ACCEPT~",
                            "~INPUT_FRONTEND_LEFT~");
                    }
                    else
                    {
                        help = string.Format(
                            L("Credit_Help_CanLoan",
                            "Bạn muốn vay tiền ngân hàng à?\nHạn mức vay tiền là {0} nha!!!\n{1} để vay tiền\n{2} để hủy"),
                            FormatMoney(maxLoan),
                            "~INPUT_FRONTEND_ACCEPT~",
                            "~INPUT_FRONTEND_LEFT~");
                    }
                }
            }

            GTA.UI.Screen.ShowHelpTextThisFrame(help);
        }
        catch (Exception ex)
        {
            Log("UpdateNpcInteractionPrompt failed: " + ex);
        }
    }

    private void BeginSavingsDialog()
    {
        try
        {
            if (!EnsureFleecaBankAccessAllowed())
                return;

            if (_savingsDialogActive)
                return;

            if (_loanActive && _loanRemainingDebt > 0)
            {
                ShowFleecaNotification(
                    L("Credit_MessageTitle", "Tin nhắn ngân hàng"),
                    L("Credit_ActiveLoanDebt", "Bạn đang có khoản vay chưa thanh toán...."));
                return;
            }

            _savingsDialogActive = true;

            string input = Game.GetUserInput(string.Empty);
            if (string.IsNullOrWhiteSpace(input))
                return;

            var sb = new StringBuilder();
            foreach (char ch in input)
            {
                if (char.IsDigit(ch))
                    sb.Append(ch);
            }

            string digitsOnly = sb.ToString();
            if (string.IsNullOrWhiteSpace(digitsOnly) ||
                !long.TryParse(digitsOnly, NumberStyles.Integer, CultureInfo.InvariantCulture, out long amount))
            {
                GTA.UI.Screen.ShowSubtitle(L("Credit_InvalidAmount", "Số tiền không hợp lệ."), 2500);
                return;
            }

            if (amount < SAVINGS_MIN_DEPOSIT)
            {
                GTA.UI.Screen.ShowSubtitle(L("Credit_MinSavingsDeposit", "Số tiền gửi tối thiểu là $1,000,000."), 2500);
                return;
            }

            if (amount > Math.Max(0, Game.Player.Money))
            {
                GTA.UI.Screen.ShowSubtitle(L("Credit_NotEnoughMoneyToDeposit", "Bạn không đủ tiền để gửi khoản này."), 2500);
                return;
            }

            StartSavingsDeposit(amount);
        }
        catch (Exception ex)
        {
            Log("BeginSavingsDialog failed: " + ex);
        }
        finally
        {
            _savingsDialogActive = false;
        }
    }

    private void DespawnBankerNpc(bool resetServiceMode = true)
    {
        try
        {
            for (int i = _bankerBlips.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (_bankerBlips[i] != null && _bankerBlips[i].Exists())
                        _bankerBlips[i].Delete();
                }
                catch { }
            }
            _bankerBlips.Clear();
            _bankerBlip = null;

            for (int i = _bankerNpcs.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (_bankerNpcs[i] != null && _bankerNpcs[i].Exists())
                        _bankerNpcs[i].Delete();
                }
                catch { }
            }
            _bankerNpcs.Clear();
            _bankerNpc = null;
            _activeBankerSpawnPoint = null;

            if (resetServiceMode)
                _activeBankerServiceMode = FleecaServiceMode.None;
        }
        catch (Exception ex)
        {
            Log("DespawnBankerNpc failed: " + ex);
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
        catch (Exception ex)
        {
            Log("SetBlipName failed: " + ex);
        }
    }
}