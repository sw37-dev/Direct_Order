using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using GTA;
using GTA.Native;
using GTA.Math;
using GTA.UI;

public class Bodyguards : Script
{
    // Controls
    private Keys RemoveKey = Keys.OemMinus;   // top-row '-' key

    private const int NormalIntervalMs = 150;
    private const int RemoveModeIntervalMs = 0;

    // Delete-select mode
    private bool _removeMode = false;
    private int _selectedCloneIndex = -1;
    private int _lastRemoveNavMs = -100000;
    private int RemoveNavThrottleMs = 150;

    // Tuning
    private int MaxClones = 3;
    private float FollowDistance = 2.5f;
    private float FollowOffsetBaseY = -1.0f;
    private float FollowOffsetSpacing = 0.5f;
    private float AggroRange = 50f;

    // Throttles / cooldowns (ms)
    private int FollowThrottleMs = 1200;
    private int EngageThrottleMs = 500;
    private int VehicleSyncThrottleMs = 700;
    private int CombatClearMs = 2500;
    private int PlayerReactionWindowMs = 5000;
    private int StateBlockTimeoutMs = 8000;

    // Weapon pool
    private WeaponHash[] WeaponPool = new WeaponHash[]
    {
        WeaponHash.AssaultRifle,
        WeaponHash.Pistol,
        (WeaponHash)0x88374054u, // SNS Pistol Mk 2
        (WeaponHash)0xBD248B55u, // Mini SMG
        (WeaponHash)0x969C3D67u, // Special Carbine Mk2
        (WeaponHash)0xD1D5F52Bu, // Service Carbine
        (WeaponHash)0x7F229F94u, // Bullpup Rifle (non-Mk2)
        (WeaponHash)0xAF3696A1u, // Up-n-Atomizer
        (WeaponHash)0xC1B3C3D1u, // Heavy Revolver (non-Mk2)
        (WeaponHash)0xC0A3098Du, // Special Carbine (non-Mk2)
        (WeaponHash)0x1BC4FDB9u, // WM 29 Pistol
        (WeaponHash)0x476BF155u, // Unholy Hellbringer
        (WeaponHash)0x0A3D4D34u, // Combat PDW
        (WeaponHash)0x22D8FE39u, // AP Pistol
        (WeaponHash)0x624FE830u, // Compact Rifle
        (WeaponHash)0xDBBD7280u, // Combat MG Mk2
        (WeaponHash)0x2B5EF5ECu, // Ceramic Pistol
        (WeaponHash)0x61012683u, // Gusenberg Sweeper
        (WeaponHash)0xDB26713Au, // Compact EMP Launcher
        (WeaponHash)0xEF951FBBu, // Double Barrel Shotgun
        (WeaponHash)0x7846A318u  // Sawn Off Shotgun
    };

    // Weapon attachments by weapon hash
    private readonly Dictionary<uint, uint[]> _weaponAttachments = new Dictionary<uint, uint[]>
    {
        {
            0x969C3D67u, // Special Carbine Mk2
            new uint[]
            {
                0x503DEA90u, // SpecialCarbineMk2ClipFMJ
                0x7BC4CDDCu, // AtArFish
                0xC6686542u, // AtScopeMediumMk2
                0x4DB62ABEu, // AtMuzzle07
                0x9D65907Au, // AtArAfGrip02
                0xF97F783Bu, // AtScBarrel02
                0x5218C819u  // SpecialCarbineMk2CamoIndependence01
            }
        },
        {
            0xBD248B55u, // Mini SMG
            new uint[]
            {
                0x84C8B2D3u,
                0x937ED0B7u
            }
        },
        {
            0xAF3696A1u, // Up-n-Atomizer
            new uint[]
            {
                0xD7DBF707u
            }
        },
        {
            0x88374054u, // SNS Pistol Mk 2
            new uint[]
            {
                0x01466CE6u, // 0x1466CE6 (scan contains 0x1466CE6 — preserve)
                0xCE8C0772u,
                0x902DA26Eu,
                0xE6AD5F79u,
                0x8D107402u,
                0xC111EB26u,
                0x4A4965F3u,
                0x47DE9258u,
                0x65EA7EBBu,
                0xAA8283BFu
            }
        },
        {
            0xD1D5F52Bu, // Service Carbine
            new uint[]
            {
                0x3749B8BBu,
                0x8594554Fu,
                0x9DB1E023u,
                0xA73D4664u,
                0x0C164F53u
            }
        },
        {
            0x7F229F94u, // Bullpup Rifle (non-Mk2)
            new uint[]
            {
                0xC5A12F80u,
                0xB3688B0Fu,
                0x7BC4CDDCu,
                0xAA2C45B4u,
                0x837445AAu,
                0x0C164F53u,
                0xA857BC78u,
                0x60BD749Cu
            }
        },
        {
            0xC1B3C3D1u, // Heavy Revolver (non-Mk2)
            new uint[]
            {
                0xE9867CE3u,
                0x16EE3040u,
                0x9493B80Du,
                0x60BD749Cu
            }
        },
        {
            0xC0A3098Du, // Special Carbine (non-Mk2)
            new uint[]
            {
                0xC6C7E581u,
                0x7C8BD10Eu,
                0x6B59AEAAu,
                0x7BC4CDDCu,
                0xA0D89C42u,
                0xA73D4664u,
                0x0C164F53u,
                0x730154F2u,
                0x120A66F8u,
                0x60BD749Cu
            }
        },
        {
            0x1BC4FDB9u, // WM 29 Pistol
            new uint[]
            {
                0x1663E75Eu,
                0x1E02B7E0u
            }
        },
        {
            0x0A3D4D34u, // Combat PDW
            new uint[]
            {
                0x4317F19Eu,
                0x334A5203u,
                0x6EB8C8DBu,
                0x7BC4CDDCu,
                0x0C164F53u,
                0xAA2C45B4u
            }
        },
        {
            0x22D8FE39u, // AP Pistol
            new uint[]
            {
                0x31C4B22Au,
                0x249A17D5u,
                0x359B7AAEu,
                0xC304849Au,
                0x9B76C72Cu,
                0x62CF4F46u
            }
        },
        {
            0x624FE830u, // Compact Rifle
            new uint[]
            {
                0x513F0A63u,
                0x59FF9BF8u,
                0xC607740Eu
            }
        },
        {
            0xDBBD7280u,  // Combat MG Mk2
            new uint[]
            {
                0x57EF1CC8u, // CombatMGMk2ClipFMJ
                0xC66B6542u, // AtScopeMediumMk2 (variant)
                0x4DB62ABEu, // AtMuzzle07
                0x9D65907Au, // AtArAfGrip02
                0xB5E2575Bu, // AtMGBarrel02
                0xD703C94Du  // CombatMGMk2CamoIndependence01
            }
        },
        {
            0x2B5EF5ECu, // Ceramic Pistol
            new uint[]
            {
                0x54D41361u,
                0x81786CA9u,
                0x9307D6FAu
            }
        },
        {
            0x61012683u, // Gusenberg Sweeper
            new uint[]
            {
                0x1CE5A6A5u,
                0xEAC8C270u
            }
        },
        {
            0xDB26713Au, // Compact EMP Launcher
            new uint[]
            {
                0xD28F58F1u // CompactEMPLauncherClip01
            }
        },
        {
            0xEF951FBBu, // Double Barrel Shotgun
            new uint[]
            {
                0x29EA741Eu
            }
        },
        {
            0x7846A318u,  // Sawn Off Shotgun
            new uint[]
            {
                0xC7D62225u, // SawnoffShotgunClip01
                0x85A64DF9u  // SawnoffShotgunVarmodLuxe
            }
        }
    };

    private Random _rng = new Random();

    // --- added: track last player vehicle handle for reliable vehicle-exit sync ---
    private int _lastPlayerVehicleHandle = 0;
    private int VehicleExitThrottleMs = 700; // throttle for re-issuing leave-vehicle

    // Relationship
    private RelationshipGroup CompanionRelGroup = null;

    // Player damage tracking
    private float _lastPlayerHealth = -1f;
    private int _lastPlayerDamagedAtMs = -100000;

    // Clone storage
    private enum CloneState
    {
        Idle,
        Following,
        EnteringVehicle,
        ExitingVehicle,   // NEW: dedicated state while leaving a vehicle
        InVehicle,
        Combat,
        SeekingCover,
        Removing
    }

    private class CloneData
    {
        public Ped Ped;
        public CloneState State = CloneState.Idle;
        public CloneState PrevState = CloneState.Idle;
        public int LastStateChangeMs = -1;

        // throttles
        public int LastFollowIssuedMs = -1;
        public int LastCombatIssuedMs = -1;
        public int LastVehicleSyncMs = -1;
        public int LastSeenEnemyMs = -1;

        // --- NEW: exit throttle ---
        public int LastExitIssuedMs = -1;

        // flags
        public bool StateBlocked = false;
        public int StateBlockedStartMs = -1;
        public string StateBlockedReason = null;

        public bool CombatLocked = false;
        public int CombatLockStartMs = -1;
        public string CombatLockReason = null;

        // health tracking
        public float LastHealth = -1f;

        // last known damage source (best-effort)
        public Entity LastDamageSource = null;

        public Blip MapBlip = null;

        public WeaponHash AssignedWeapon = WeaponHash.Unarmed;
        public bool WeaponAssigned = false;

        // NEW: chống xử lý lặp khi clone đã chết
        public bool WeaponCleanupDone = false;
    }

    private List<CloneData> _clones = new List<CloneData>();

    public static Bodyguards Instance = null;

    public Bodyguards()
    {
        Instance = this; // <-- set singleton instance for external callers
        Interval = NormalIntervalMs;
        Tick += OnTick;
        KeyDown += OnKeyDown;
        Aborted += OnAborted;
    }

    private void OnAborted(object sender, EventArgs e)
    {
        RemoveAllClones();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // Toggle delete-select mode bằng phím "-"
        if (e.KeyCode == RemoveKey)
        {
            ToggleRemoveMode();
            return;
        }

        // Chỉ xử lý các phím chọn khi đang bật chế độ xóa
        if (!_removeMode)
            return;

        if (e.KeyCode == Keys.Left)
        {
            MoveRemoveSelection(-1);
            return;
        }

        if (e.KeyCode == Keys.Right)
        {
            MoveRemoveSelection(1);
            return;
        }

        if (e.KeyCode == Keys.Enter)
        {
            RemoveSelectedClone();
            return;
        }
    }

    private void OnTick(object sender, EventArgs e)
    {
        try
        {
            var player = Game.Player.Character;
            if (player == null || !player.Exists()) return;

            // Bảo hiểm: Tự động điều chỉnh Interval nếu vô tình bị thay đổi
            if (_removeMode && Interval != RemoveModeIntervalMs)
                Interval = RemoveModeIntervalMs;
            else if (!_removeMode && Interval != NormalIntervalMs)
                Interval = NormalIntervalMs;

            // --- added: detect player vehicle transition to force clones to leave immediately when player exits ---
            try
            {
                var currentVeh = player.CurrentVehicle;
                int curVehHandle = (currentVeh != null && currentVeh.Exists()) ? currentVeh.Handle : 0;

                if (_lastPlayerVehicleHandle != curVehHandle)
                {
                    // player exited vehicle (was in a vehicle, now not)
                    if (_lastPlayerVehicleHandle != 0 && curVehHandle == 0)
                    {
                        for (int k = _clones.Count - 1; k >= 0; k--)
                        {
                            var cdExit = _clones[k];
                            try
                            {
                                if (cdExit?.Ped != null && cdExit.Ped.Exists() && cdExit.Ped.IsInVehicle())
                                {
                                    var cloneVeh = cdExit.Ped.CurrentVehicle;
                                    if (cloneVeh != null && cloneVeh.Exists() && cloneVeh.Handle == _lastPlayerVehicleHandle)
                                    {
                                        try
                                        {
                                            // Use dedicated state + block so other tasks won't override the leave
                                            SetStateBlock(cdExit, "leaving_vehicle");
                                            Function.Call(Hash.TASK_LEAVE_ANY_VEHICLE, cdExit.Ped.Handle, 0, 0);
                                            cdExit.LastExitIssuedMs = Game.GameTime;
                                            cdExit.LastVehicleSyncMs = Game.GameTime;
                                            cdExit.State = CloneState.ExitingVehicle;
                                            try { cdExit.Ped.BlockPermanentEvents = true; cdExit.Ped.AlwaysKeepTask = true; } catch { }
                                        }
                                        catch { }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    _lastPlayerVehicleHandle = curVehHandle;
                }
            }
            catch { }

            TrackPlayerDamage(player);

            if (CompanionRelGroup == null) CreateCompanionRelationshipGroup();

            // iterate copies in reverse for safe removal
            for (int i = _clones.Count - 1; i >= 0; i--)
            {
                var cd = _clones[i];

                // basic sanity
                if (cd.Ped == null || !cd.Ped.Exists() || cd.Ped.IsDead || cd.Ped.Health <= 0)
                {
                    // NEW: xóa vũ khí của clone ngay khi phát hiện chết / sắp chết
                    CleanupCloneWeaponOnDeath(cd);

                    RemoveCloneBlip(cd);
                    _clones.RemoveAt(i);
                    continue;
                }

                // keep blip alive
                EnsureCloneBlip(cd);

                // ensure mission entity so engine doesn't GC them
                try { Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, cd.Ped.Handle, true, true); } catch { }

                // maintain state times
                if (cd.LastStateChangeMs < 0) cd.LastStateChangeMs = Game.GameTime;
                if (cd.LastHealth < 0f) cd.LastHealth = cd.Ped.Health;

                // update last damage tracking for clone
                DetectCloneDamage(cd);

                // vehicle sync check (throttled)
                if ((Game.GameTime - cd.LastVehicleSyncMs) > 100)
                {
                    HandleVehicleSync(cd, player, i);
                }

                // Evaluate Conditions -> decide desired state (based on priority)
                var desired = EvaluateState(cd, player, i);

                // Apply only when state changed **or** when we explicitly need to re-issue same state after timeout
                if (desired != cd.State)
                {
                    cd.PrevState = cd.State;
                    cd.State = desired;
                    cd.LastStateChangeMs = Game.GameTime;
                    ApplyStateChange(cd, player, i);
                }
                else
                {
                    // Same state: occasionally refresh/maintain (but do not spam)
                    MaintainState(cd, player, i);
                }

                // Clear combat lock if conditions met
                TryClearCombatLock(cd, player, i);
            }

            if (_removeMode)
            {
                DrawRemoveSelectionMarker();
            }
        }
        catch (Exception ex)
        {
            Notification.Show("CompanionCloner error: " + ex.Message);
        }
    }

    private WeaponHash PickRandomWeapon()
    {
        return WeaponPool[_rng.Next(WeaponPool.Length)];
    }

    private void UpdateScriptInterval()
    {
        try
        {
            Interval = _removeMode ? RemoveModeIntervalMs : NormalIntervalMs;
        }
        catch { }
    }
    private void GiveWeaponWithAttachments(Ped clone, WeaponHash weaponHash)
    {
        try
        {
            if (clone == null || !clone.Exists()) return;

            uint weapon = (uint)weaponHash;

            // Give weapon first
            try
            {
                clone.Weapons.Give(weaponHash, 9999, true, true);
            }
            catch
            {
                try
                {
                    Function.Call(Hash.GIVE_WEAPON_TO_PED, clone.Handle, weapon, 9999, false, true);
                }
                catch { }
            }

            // Apply attachments if configured for this weapon
            uint[] attachments;
            if (_weaponAttachments != null &&
                _weaponAttachments.TryGetValue(weapon, out attachments) &&
                attachments != null)
            {
                foreach (uint component in attachments)
                {
                    try
                    {
                        // If native exists, only add missing components
                        bool hasComponent = false;
                        try
                        {
                            hasComponent = Function.Call<bool>(Hash.HAS_PED_GOT_WEAPON_COMPONENT, clone.Handle, weapon, component);
                        }
                        catch
                        {
                            hasComponent = false;
                        }

                        if (!hasComponent)
                        {
                            Function.Call(Hash.GIVE_WEAPON_COMPONENT_TO_PED, clone.Handle, weapon, component);
                        }
                    }
                    catch { }
                }
            }

            // Force equip after everything is applied
            try { clone.Weapons.Select(weaponHash, true); } catch { }
            try { Function.Call(Hash.SET_CURRENT_PED_WEAPON, clone.Handle, weapon, true); } catch { }
        }
        catch { }
    }

    // Tạo clone dựa trên chính nhân vật hiện tại.
    // Ưu tiên Ped.Clone() vì SHVDN mô tả đây là "identical clone".
    // Nếu vì lý do nào đó Clone() lỗi, fallback về CreatePed(model, pos).
    private Ped CreateCloneFromPlayer(Ped player, Vector3 spawnPos)
    {
        Ped clone = null;

        try
        {
            // true = copy cả appearance / outfit / trạng thái cần thiết theo overload mới
            clone = player.Clone(true);
        }
        catch
        {
            clone = null;
        }

        if (clone == null || !clone.Exists())
        {
            try
            {
                var model = player.Model;
                int waited = 0;
                if (!model.IsLoaded)
                {
                    model.Request(500);
                    while (!model.IsLoaded && waited < 2000)
                    {
                        Script.Wait(50);
                        waited += 50;
                    }
                }

                clone = World.CreatePed(model, spawnPos);
            }
            catch
            {
                clone = null;
            }
        }

        if (clone == null || !clone.Exists())
            return null;

        try { clone.Position = spawnPos; } catch { }
        try { clone.Heading = player.Heading; } catch { }

        try { clone.Task.ClearAll(); } catch { }
        try { clone.ClearBloodDamage(); } catch { }
        try { clone.ClearVisibleDamage(); } catch { }

        CopyPedAppearance(player, clone);

        return clone;
    }

    private Ped CreateCompanionFromModelHash(Ped player, Vector3 spawnPos, uint modelHash)
    {
        Ped clone = null;

        try
        {
            var model = new Model((int)modelHash);
            if (!model.IsValid || !model.IsInCdImage)
                return null;

            int waited = 0;
            if (!model.IsLoaded)
            {
                model.Request(500);
                while (!model.IsLoaded && waited < 2000)
                {
                    Script.Wait(50);
                    waited += 50;
                }
            }

            clone = World.CreatePed(model, spawnPos);
        }
        catch
        {
            clone = null;
        }

        if (clone == null || !clone.Exists())
            return null;

        try { clone.Position = spawnPos; } catch { }
        try { clone.Heading = player.Heading; } catch { }

        try { clone.Task.ClearAll(); } catch { }
        try { clone.ClearBloodDamage(); clone.ClearVisibleDamage(); } catch { }

        return clone;
    }

    private void CopyPedAppearance(Ped source, Ped target)
    {
        try
        {
            if (source == null || target == null || !source.Exists() || !target.Exists())
                return;

            // Components: áo, quần, giày, tay áo, phụ kiện...
            for (int component = 0; component < 12; component++)
            {
                try
                {
                    int drawable = Function.Call<int>(Hash.GET_PED_DRAWABLE_VARIATION, source.Handle, component);
                    int texture = Function.Call<int>(Hash.GET_PED_TEXTURE_VARIATION, source.Handle, component);
                    Function.Call(Hash.SET_PED_COMPONENT_VARIATION, target.Handle, component, drawable, texture, 0);
                }
                catch { }
            }

            // Props: mũ, kính, tai nghe, mặt nạ gắn kiểu prop...
            for (int prop = 0; prop < 8; prop++)
            {
                try
                {
                    int propIndex = Function.Call<int>(Hash.GET_PED_PROP_INDEX, source.Handle, prop);
                    if (propIndex >= 0)
                    {
                        int propTexture = Function.Call<int>(Hash.GET_PED_PROP_TEXTURE_INDEX, source.Handle, prop);
                        Function.Call(Hash.SET_PED_PROP_INDEX, target.Handle, prop, propIndex, propTexture, true);
                    }
                    else
                    {
                        Function.Call(Hash.CLEAR_PED_PROP, target.Handle, prop);
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private void AssignRandomWeaponOnce(CloneData cd)
    {
        try
        {
            var clone = cd.Ped;
            if (clone == null || !clone.Exists()) return;

            if (!cd.WeaponAssigned || cd.AssignedWeapon == WeaponHash.Unarmed)
            {
                cd.AssignedWeapon = PickRandomWeapon();
                cd.WeaponAssigned = true;
                cd.WeaponCleanupDone = false; // NEW
            }

            try { Function.Call(Hash.REMOVE_ALL_PED_WEAPONS, clone.Handle, true); } catch { }
            GiveWeaponWithAttachments(clone, cd.AssignedWeapon);
        }
        catch { }
    }

    // -------------------------
    // State evaluation (Condition System + Priority)
    // Priority: Combat > EnteringVehicle (player in vehicle) > Following > Idle
    // -------------------------
    // existing method signature unchanged
    private CloneState EvaluateState(CloneData cd, Ped player, int index)
    {
        try
        {
            var clone = cd.Ped;

            // if blocked in critical state, keep it unless timeout
            if (cd.StateBlocked)
            {
                if ((Game.GameTime - cd.StateBlockedStartMs) > StateBlockTimeoutMs)
                {
                    // expired block
                    ClearStateBlock(cd, "timeout");
                }
                else
                {
                    // preserve entering vehicle flow if engine indicates getting in
                    if (cd.StateBlockedReason == "entering_vehicle")
                    {
                        if (Function.Call<bool>(Hash.IS_PED_GETTING_INTO_A_VEHICLE, clone.Handle))
                        {
                            return CloneState.EnteringVehicle;
                        }
                    }

                    // preserve leaving vehicle flow if still in vehicle
                    if (cd.StateBlockedReason == "leaving_vehicle")
                    {
                        // if still in vehicle, keep ExitingVehicle
                        if (clone.IsInVehicle()) return CloneState.ExitingVehicle;

                        // if not in vehicle anymore, clear the block and continue evaluation
                        ClearStateBlock(cd, "left_vehicle");
                    }

                    // preserve seeking cover flow while engine is doing cover
                    if (cd.StateBlockedReason == "seek_cover")
                    {
                        // we attempt to let native cover tasks run — keep SeekingCover for a while
                        return CloneState.SeekingCover;
                    }

                    // otherwise fall-through to normal evaluation (if block reason isn't preventing)
                }
            }

            // (rest of EvaluateState unchanged)
            // 1) Combat triggers (highest priority)
            if (IsHostileNearbyFor(cd, player) || clone.IsInCombat || PlayerWasAttackedRecently() || cd.LastDamageSource != null)
            {
                var target = DetectBestTarget(cd, player);
                if (target != null && target.Exists())
                {
                    return CloneState.Combat;
                }
                if (clone.IsInCombat || cd.LastDamageSource != null) return CloneState.Combat;
            }

            // 2) Enter vehicle trigger
            if (player.IsInVehicle() && !clone.IsInVehicle())
            {
                var playerVeh = player.CurrentVehicle;
                if (playerVeh != null && playerVeh.Exists() && IsVehicleDriveable(playerVeh))
                {
                    return CloneState.EnteringVehicle;
                }
            }

            // 3) Follow trigger
            if (!clone.IsInVehicle())
            {
                Vector3 desiredOffset = GetFollowOffset(index);
                Vector3 desiredWorld = player.GetOffsetPosition(desiredOffset);
                float d = clone.Position.DistanceTo(desiredWorld);
                if (d > 2.0f) // threshold to start following
                {
                    return CloneState.Following;
                }
            }

            // default Idle
            return CloneState.Idle;
        }
        catch
        {
            return CloneState.Idle;
        }
    }

    // -------------------------
    // Apply state change: Task Execution ONLY WHEN STATE CHANGED
    // -------------------------
    private void ApplyStateChange(CloneData cd, Ped player, int index)
    {
        try
        {
            var clone = cd.Ped;
            // clear minor tasks on state change to avoid task conflicts
            try { clone.Task.ClearAll(); } catch { }

            switch (cd.State)
            {
                case CloneState.Combat:
                    // prepare combat: configure attributes & equip if needed, then issue TASK_COMBAT_PED
                    ConfigureCombatBehavior(clone);
                    // NOTE: do NOT pre-set LastCombatIssuedMs here — IssueCombat will set it after issuing.
                    cd.LastSeenEnemyMs = Game.GameTime;
                    cd.CombatLocked = true;
                    cd.CombatLockStartMs = Game.GameTime;
                    cd.CombatLockReason = "state_combat";
                    IssueCombat(cd, player);
                    break;

                case CloneState.EnteringVehicle:
                    // issue enter vehicle once (throttled)
                    if (!cd.StateBlocked && (Game.GameTime - cd.LastVehicleSyncMs) > VehicleSyncThrottleMs)
                    {
                        TryEnterPlayerVehicle(cd, player, index);
                    }
                    break;

                case CloneState.Following:
                    // ensure follow task applied (throttle inside)
                    EnsureFollowOnce(cd, player, index);
                    break;

                case CloneState.Idle:
                    // stand still or wander minimal
                    ApplyIdle(cd);
                    break;

                case CloneState.ExitingVehicle:
                    // ensure leave command is issued and state is blocked so nothing overrides until engine finishes
                    try
                    {
                        if (clone != null && clone.Exists() && clone.IsInVehicle())
                        {
                            // Throttle re-issuing leave
                            if ((Game.GameTime - cd.LastExitIssuedMs) > VehicleExitThrottleMs)
                            {
                                SetStateBlock(cd, "leaving_vehicle");
                                Function.Call(Hash.TASK_LEAVE_ANY_VEHICLE, clone.Handle, 0, 0);
                                cd.LastExitIssuedMs = Game.GameTime;
                                try { clone.BlockPermanentEvents = true; clone.AlwaysKeepTask = true; } catch { }
                            }
                        }
                        else
                        {
                            // not in vehicle; clear block and transition to following
                            ClearStateBlock(cd, "left_vehicle");
                            cd.State = CloneState.Following;
                            cd.LastStateChangeMs = Game.GameTime;
                            try { clone.BlockPermanentEvents = false; clone.AlwaysKeepTask = false; } catch { }
                            EnsureFollowOnce(cd, player, index);
                        }
                    }
                    catch { }
                    break;

                case CloneState.InVehicle:
                    // nothing to apply; block events to let vehicle logic persist
                    try
                    {
                        clone.BlockPermanentEvents = true;
                        clone.AlwaysKeepTask = true;
                    }
                    catch { }
                    break;

                case CloneState.SeekingCover:
                    TrySeekCover(cd);
                    break;

                case CloneState.Removing:
                    // fallback: remove
                    RemoveClone(cd);
                    break;
            }
        }
        catch (Exception ex)
        {
            Notification.Show("ApplyStateChange error: " + ex.Message);
        }
    }

    // Maintain same state occasionally (do not spam)
    private void MaintainState(CloneData cd, Ped player, int index)
    {
        try
        {
            var clone = cd.Ped;
            switch (cd.State)
            {
                case CloneState.Following:
                    // reissue follow only if throttle elapsed or distance big changed
                    EnsureFollowOnce(cd, player, index);
                    break;

                case CloneState.EnteringVehicle:
                    // check if got in / reissue enter if stalled
                    if (!clone.IsInVehicle() && (Game.GameTime - cd.LastVehicleSyncMs) > VehicleSyncThrottleMs)
                    {
                        TryEnterPlayerVehicle(cd, player, index);
                    }
                    break;

                case CloneState.Combat:
                    // periodically ensure combat still active (throttled)
                    if ((Game.GameTime - cd.LastCombatIssuedMs) > EngageThrottleMs)
                    {
                        IssueCombat(cd, player);
                    }
                    break;

                case CloneState.ExitingVehicle:
                    // if still in vehicle after some time, re-issue leave (throttled)
                    if (clone.IsInVehicle() && (Game.GameTime - cd.LastExitIssuedMs) > VehicleExitThrottleMs)
                    {
                        try
                        {
                            SetStateBlock(cd, "leaving_vehicle");
                            Function.Call(Hash.TASK_LEAVE_ANY_VEHICLE, clone.Handle, 0, 0);
                            cd.LastExitIssuedMs = Game.GameTime;
                            try { clone.BlockPermanentEvents = true; clone.AlwaysKeepTask = true; } catch { }
                        }
                        catch { }
                    }
                    else if (!clone.IsInVehicle())
                    {
                        // we've exited: let follow resume
                        ClearStateBlock(cd, "left_vehicle");
                        cd.State = CloneState.Following;
                        cd.LastStateChangeMs = Game.GameTime;
                        try { clone.BlockPermanentEvents = false; clone.AlwaysKeepTask = false; } catch { }
                        EnsureFollowOnce(cd, player, index);
                    }
                    break;

                case CloneState.Idle:
                    // maybe re-apply standstill rarely
                    if ((Game.GameTime - cd.LastStateChangeMs) > 3000)
                    {
                        ApplyIdle(cd);
                    }
                    break;
            }
        }
        catch { }
    }

    // -------------------------
    // State-specific helpers
    // -------------------------
    private void EnsureFollowOnce(CloneData cd, Ped player, int index)
    {
        try
        {
            var clone = cd.Ped;
            if (clone == null || !clone.Exists()) return;

            // Do not issue follow during combat or when blocked
            if (cd.CombatLocked || clone.IsInCombat || cd.StateBlocked) return;

            Vector3 offset = GetFollowOffset(index);
            Vector3 desired = player.GetOffsetPosition(offset);
            float dist = clone.Position.DistanceTo(desired);

            bool shouldIssue = false;
            int sinceFollow = Game.GameTime - cd.LastFollowIssuedMs;

            // 1) If too far from desired formation, issue immediately (no throttle)
            if (dist > (FollowDistance + 0.5f))
            {
                shouldIssue = true;
            }
            // 2) Medium distance: allow quick re-issue if a short time passed (avoid 0ms spam)
            else if (dist > 4.0f && sinceFollow > 200)
            {
                shouldIssue = true;
            }
            // 3) Regular throttle fallback (periodic refresh)
            else if (sinceFollow > FollowThrottleMs)
            {
                shouldIssue = true;
            }

            if (shouldIssue)
            {
                try
                {
                    clone.BlockPermanentEvents = false;
                    clone.AlwaysKeepTask = false;

                    // Issue follow-to-offset (timeout large so it persists)
                    Function.Call(Hash.TASK_FOLLOW_TO_OFFSET_OF_ENTITY, clone.Handle, player.Handle,
                        offset.X, offset.Y, offset.Z, 10000, -1, FollowDistance, true);

                    cd.LastFollowIssuedMs = Game.GameTime;
                    // set LastStateChangeMs if state just applied
                    cd.LastStateChangeMs = Game.GameTime;
                }
                catch { }
            }
        }
        catch { }
    }

    private void CleanupCloneWeaponOnDeath(CloneData cd)
    {
        try
        {
            if (cd == null) return;

            var clone = cd.Ped;
            if (clone == null || !clone.Exists()) return;

            // Chặn thêm lần nữa để ped không tự thả vũ khí
            try { Function.Call(Hash.SET_PED_DROPS_WEAPONS_WHEN_DEAD, clone.Handle, false); } catch { }

            if (cd.WeaponCleanupDone)
                return;

            // Gỡ đúng khẩu mà mod đã cấp
            if (cd.WeaponAssigned && cd.AssignedWeapon != WeaponHash.Unarmed)
            {
                try { Function.Call(Hash.REMOVE_WEAPON_FROM_PED, clone.Handle, (uint)cd.AssignedWeapon); } catch { }
            }

            // Dọn sạch toàn bộ inventory vũ khí còn sót
            try { Function.Call(Hash.REMOVE_ALL_PED_WEAPONS, clone.Handle, true); } catch { }

            // Đẩy về unarmed để tránh game giữ current weapon state
            try { Function.Call(Hash.SET_CURRENT_PED_WEAPON, clone.Handle, (uint)WeaponHash.Unarmed, true); } catch { }

            cd.WeaponCleanupDone = true;
        }
        catch { }
    }

    private Vector3 GetFollowOffset(int index)
    {
        // basic formation: behind player with slight spacing
        return new Vector3(0f, FollowOffsetBaseY - (index * FollowOffsetSpacing), 0f);
    }

    private void ApplyIdle(CloneData cd)
    {
        try
        {
            var clone = cd.Ped;
            try
            {
                clone.Task.ClearAll();
                Function.Call(Hash.TASK_STAND_STILL, clone.Handle, 1000);
                clone.BlockPermanentEvents = false;
                clone.AlwaysKeepTask = false;
            }
            catch { }
        }
        catch { }
    }

    private void TrySeekCover(CloneData cd)
    {
        try
        {
            var clone = cd.Ped;
            if (clone == null || !clone.Exists()) return;

            cd.StateBlocked = true;
            cd.StateBlockedStartMs = Game.GameTime;
            cd.StateBlockedReason = "seek_cover";

            try
            {
                Vector3 pos = clone.Position;
                Function.Call(Hash.TASK_SEEK_COVER_FROM_POS, clone.Handle, pos.X, pos.Y, pos.Z, -1);
            }
            catch
            {
                // fallback: combat hated targets around or standstill
                try { Function.Call(Hash.TASK_COMBAT_HATED_TARGETS_AROUND_PED, clone.Handle, 50f, 0); } catch { }
            }
        }
        catch { }
    }

    // -------------------------
    // Combat helpers
    // -------------------------
    private void ConfigureCombatBehavior(Ped clone)
    {
        try
        {
            // make them more accurate / capable so they actually shoot reliably in combat
            Function.Call(Hash.SET_PED_ACCURACY, clone.Handle, 85);
            Function.Call(Hash.SET_PED_COMBAT_ABILITY, clone.Handle, 3);
            Function.Call(Hash.SET_PED_COMBAT_MOVEMENT, clone.Handle, 2);
            Function.Call(Hash.SET_PED_COMBAT_RANGE, clone.Handle, 3);

            int[] enableAttrs = new int[] { 3, 5, 11, 46, 1 };
            foreach (var a in enableAttrs)
            {
                try { Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, clone.Handle, a, true); } catch { }
            }

            Function.Call(Hash.SET_PED_FLEE_ATTRIBUTES, clone.Handle, 0, false);
            Function.Call(Hash.SET_PED_CAN_RAGDOLL, clone.Handle, false);
            Function.Call(Hash.SET_PED_DROPS_WEAPONS_WHEN_DEAD, clone.Handle, false);

            // KHÔNG cấp toàn bộ vũ khí ở đây nữa.
            // Vũ khí sẽ được random 1 lần cho từng clone và cấp riêng bằng AssignRandomWeaponOnce().
        }
        catch { }
    }

    private void IssueCombat(CloneData cd, Ped player)
    {
        try
        {
            var clone = cd.Ped;
            if (clone == null || !clone.Exists()) return;

            // If entering vehicle or blocked, skip
            if (cd.StateBlocked) return;

            // Find best target
            var target = DetectBestTarget(cd, player);
            if (target == null || !target.Exists()) return;

            // Throttle combat issuing
            if ((Game.GameTime - cd.LastCombatIssuedMs) < EngageThrottleMs) return;

            // Ensure equipped
            EnsureHasEquippedWeapon(cd);

            try
            {
                Function.Call(Hash.TASK_COMBAT_PED, clone.Handle, target.Handle, 0, 16);
                cd.LastCombatIssuedMs = Game.GameTime;
                cd.LastSeenEnemyMs = Game.GameTime;
                cd.CombatLocked = true;
                cd.CombatLockStartMs = Game.GameTime;
                cd.CombatLockReason = "issued_combat";
                // keep tasks
                clone.BlockPermanentEvents = true;
                clone.AlwaysKeepTask = true;
            }
            catch (Exception ex)
            {
                Notification.Show("IssueCombat fail: " + ex.Message);
            }
        }
        catch { }
    }

    private void EnsureHasEquippedWeapon(CloneData cd)
    {
        try
        {
            var clone = cd.Ped;
            if (clone == null || !clone.Exists()) return;

            // Nếu chưa có weapon được gán thì gán ngay
            if (!cd.WeaponAssigned || cd.AssignedWeapon == WeaponHash.Unarmed)
            {
                AssignRandomWeaponOnce(cd);
            }

            // Nếu đã mất vũ khí thì cấp lại đúng món đã random
            bool hasAssigned = false;
            try
            {
                hasAssigned = clone.Weapons.HasWeapon(cd.AssignedWeapon);
            }
            catch
            {
                hasAssigned = false;
            }

            if (!hasAssigned)
            {
                try { Function.Call(Hash.REMOVE_ALL_PED_WEAPONS, clone.Handle, true); } catch { }
                GiveWeaponWithAttachments(clone, cd.AssignedWeapon);
                return;
            }

            // Nếu weapon đã có sẵn, vẫn thử bơm lại component bị thiếu
            uint weapon = (uint)cd.AssignedWeapon;
            uint[] attachments;
            if (_weaponAttachments != null &&
                _weaponAttachments.TryGetValue(weapon, out attachments) &&
                attachments != null)
            {
                foreach (uint component in attachments)
                {
                    try
                    {
                        bool hasComponent = false;
                        try
                        {
                            hasComponent = Function.Call<bool>(Hash.HAS_PED_GOT_WEAPON_COMPONENT, clone.Handle, weapon, component);
                        }
                        catch
                        {
                            hasComponent = false;
                        }

                        if (!hasComponent)
                        {
                            Function.Call(Hash.GIVE_WEAPON_COMPONENT_TO_PED, clone.Handle, weapon, component);
                        }
                    }
                    catch { }
                }
            }

            // Ép equip đúng món đó
            try { clone.Weapons.Select(cd.AssignedWeapon, true); } catch { }
            try { Function.Call(Hash.SET_CURRENT_PED_WEAPON, clone.Handle, weapon, true); } catch { }

            // Bơm lại đạn cho đúng món đã chọn
            try
            {
                clone.Weapons.Give(cd.AssignedWeapon, 9999, false, false);
            }
            catch { }
        }
        catch { }
    }

    // Try clear combat lock when no hostiles for enough time or engine says not in combat
    private void TryClearCombatLock(CloneData cd, Ped player, int index)
    {
        try
        {
            var clone = cd.Ped;
            if (!cd.CombatLocked) return;

            bool engineSaysCombat = false;
            try { engineSaysCombat = clone.IsInCombat; } catch { engineSaysCombat = false; }

            if (!engineSaysCombat && !IsHostileNearbyFor(cd, player))
            {
                if ((Game.GameTime - cd.LastSeenEnemyMs) > CombatClearMs)
                {
                    // clear
                    cd.CombatLocked = false;
                    cd.CombatLockStartMs = -1;
                    cd.CombatLockReason = null;

                    // If blocked for leaving vehicle, keep that flow instead of forcing Following immediately
                    if (cd.StateBlocked && cd.StateBlockedReason == "leaving_vehicle")
                    {
                        cd.State = CloneState.ExitingVehicle;
                        cd.LastStateChangeMs = Game.GameTime;
                        // Ensure ApplyStateChange will maintain leave flow
                    }
                    else
                    {
                        cd.State = CloneState.Following;
                        cd.LastStateChangeMs = Game.GameTime;
                        try { clone.BlockPermanentEvents = false; clone.AlwaysKeepTask = false; } catch { }
                        EnsureFollowOnce(cd, player, index);
                    }
                }
            }
        }
        catch { }
    }

    // -------------------------
    // Detection helpers (hostiles)
    // -------------------------
    private Ped DetectBestTarget(CloneData cd, Ped player)
    {
        try
        {
            var clone = cd.Ped;
            // search around clone first then player
            var center = clone.Position;
            var nearby = World.GetNearbyPeds(center, AggroRange)
                              .Where(p => p != null && p.Exists() && p.IsAlive && !p.IsInjured && p != clone && p != player)
                              .ToList();

            Ped best = null;
            float bestScore = -999f;
            foreach (var p in nearby)
            {
                if (!IsValidTargetForClone(p, clone, player)) continue;

                float score = 0f;
                if (Function.Call<bool>(Hash.IS_PED_SHOOTING, p.Handle)) score += 80f;
                if (Function.Call<bool>(Hash.IS_PED_IN_COMBAT, p.Handle)) score += 50f;
                try
                {
                    if (Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY, player.Handle, p.Handle, true)) score += 80f;
                    if (Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY, clone.Handle, p.Handle, true)) score += 90f;
                }
                catch { }

                float d = p.Position.DistanceTo(center);
                score -= d * 0.2f;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = p;
                }
            }

            return best;
        }
        catch { return null; }
    }

    private bool IsValidTargetForClone(Ped p, Ped clone, Ped player)
    {
        try
        {
            if (p == null || !p.Exists() || !p.IsAlive || p.IsInjured) return false;

            if (p.RelationshipGroup != null && clone.RelationshipGroup != null)
            {
                if (p.RelationshipGroup == clone.RelationshipGroup || p.RelationshipGroup == player.RelationshipGroup) return false;
            }
            return true;
        }
        catch { return true; }
    }

    private bool IsHostileNearbyFor(CloneData cd, Ped player)
    {
        try
        {
            var clone = cd.Ped;
            if (clone == null || !clone.Exists()) return false;

            var nearby = World.GetNearbyPeds(clone.Position, AggroRange)
                              .Where(p => p != null && p.Exists() && p.IsAlive && !p.IsInjured && p != clone && p != player);

            foreach (var p in nearby)
            {
                if (Function.Call<bool>(Hash.IS_PED_SHOOTING, p.Handle)) return true;
                if (Function.Call<bool>(Hash.IS_PED_IN_COMBAT, p.Handle)) return true;
                try
                {
                    if (Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY, player.Handle, p.Handle, true)) return true;
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    // -------------------------
    // Vehicle helpers
    // -------------------------
    private void HandleVehicleSync(CloneData cd, Ped player, int index)
    {
        try
        {
            var clone = cd.Ped;
            if (clone == null || !clone.Exists() || player == null || !player.Exists()) return;

            // If clone already in same vehicle as player -> in vehicle state
            if (player.IsInVehicle() && clone.IsInVehicle() && clone.CurrentVehicle == player.CurrentVehicle)
            {
                cd.State = CloneState.InVehicle;
                cd.LastVehicleSyncMs = Game.GameTime;
                cd.StateBlocked = false;
                cd.StateBlockedReason = null;
                try { clone.BlockPermanentEvents = true; clone.AlwaysKeepTask = true; } catch { }
                return;
            }

            // If player in vehicle and clone not in player's vehicle -> try to enter (but respect throttles)
            if (player.IsInVehicle() && !clone.IsInVehicle())
            {
                if ((Game.GameTime - cd.LastVehicleSyncMs) > VehicleSyncThrottleMs)
                {
                    TryEnterPlayerVehicle(cd, player, index);
                }
                return;
            }

            // --- improved: when player not in vehicle but clone still is, trigger controlled exit ---
            if (!player.IsInVehicle() && clone.IsInVehicle())
            {
                try
                {
                    // don't spam leave requests
                    if ((Game.GameTime - cd.LastExitIssuedMs) < VehicleExitThrottleMs)
                    {
                        return;
                    }

                    // Use dedicated state and block so other logic won't override while native does its work.
                    try
                    {
                        // mark blocked for leaving
                        SetStateBlock(cd, "leaving_vehicle");

                        // issue leave task
                        Function.Call(Hash.TASK_LEAVE_ANY_VEHICLE, clone.Handle, 0, 0);

                        // keep task persistent so it's less likely to be interrupted
                        try { clone.BlockPermanentEvents = true; clone.AlwaysKeepTask = true; } catch { }

                        cd.LastExitIssuedMs = Game.GameTime;
                        cd.LastVehicleSyncMs = Game.GameTime;
                        cd.State = CloneState.ExitingVehicle;
                        // DO NOT immediately set to Following here; wait until engine reports exited and block cleared
                    }
                    catch { cd.LastVehicleSyncMs = Game.GameTime; }
                }
                catch { }
            }
        }
        catch { }
    }

    private void TryEnterPlayerVehicle(CloneData cd, Ped player, int index)
    {
        try
        {
            var clone = cd.Ped;
            if (clone == null || !clone.Exists() || player == null || !player.Exists()) return;

            // if currently blocked because leaving vehicle, skip any enter attempts
            if (cd.StateBlocked && cd.StateBlockedReason == "leaving_vehicle") return;

            var veh = player.CurrentVehicle;
            if (veh == null || !veh.Exists() || !IsVehicleDriveable(veh)) return;

            // find seat
            var seat = FindSafePassengerSeat(veh);
            if (!seat.HasValue)
            {
                // if no seat free, move near vehicle and wait
                try
                {
                    Vector3 followPoint = veh.GetOffsetPosition(new Vector3(1.0f + (index * 0.5f), -2.0f, 0));
                    clone.Task.ClearAll();
                    clone.Task.GoTo(followPoint);
                }
                catch { }
                cd.LastVehicleSyncMs = Game.GameTime;
                return;
            }

            // safety checks: do not issue if clone is ragdoll or in combat
            if (clone.IsRagdoll || clone.IsInCombat || Function.Call<bool>(Hash.IS_PED_GETTING_INTO_A_VEHICLE, clone.Handle))
            {
                cd.LastVehicleSyncMs = Game.GameTime;
                return;
            }

            // prepare to enter
            try
            {
                clone.BlockPermanentEvents = false;
                clone.AlwaysKeepTask = false;
                clone.Task.ClearAll();

                Function.Call(Hash.TASK_ENTER_VEHICLE, clone.Handle, veh.Handle, -1, (int)seat.Value, 2.0f, 1, 0);
                SetStateBlock(cd, "entering_vehicle");
                cd.State = CloneState.EnteringVehicle;
                cd.LastVehicleSyncMs = Game.GameTime;
            }
            catch { cd.LastVehicleSyncMs = Game.GameTime; }
        }
        catch { }
    }

    private VehicleSeat? FindSafePassengerSeat(Vehicle veh)
    {
        try
        {
            VehicleSeat[] prefer = new VehicleSeat[] { VehicleSeat.Passenger, VehicleSeat.LeftRear, VehicleSeat.RightRear };
            foreach (var s in prefer) if (veh.IsSeatFree(s)) return s;
            foreach (VehicleSeat s in Enum.GetValues(typeof(VehicleSeat))) if (veh.IsSeatFree(s)) return s;
        }
        catch { }
        return null;
    }

    private bool IsVehicleDriveable(Vehicle v)
    {
        try
        {
            if (v == null || !v.Exists()) return false;
            if (v.EngineHealth <= 0f) return false;
            try { if (!v.IsOnAllWheels) return false; } catch { }
            return true;
        }
        catch { return false; }
    }

    // -------------------------
    // Damage detection helpers
    // -------------------------
    private void DetectCloneDamage(CloneData cd)
    {
        try
        {
            var clone = cd.Ped;
            if (clone == null || !clone.Exists()) return;

            float cur = clone.Health;
            if (cd.LastHealth < 0f) cd.LastHealth = cur;

            if (cur < cd.LastHealth)
            {
                // NEW: nếu clone vừa rơi xuống 0 máu thì dọn vũ khí ngay lập tức
                if (cur <= 0f)
                {
                    CleanupCloneWeaponOnDeath(cd);
                    cd.LastHealth = cur;
                    return;
                }

                cd.LastStateChangeMs = Game.GameTime;
                cd.LastSeenEnemyMs = Game.GameTime;

                var attacker = FindLastDamageSource(cd);
                if (attacker != null) cd.LastDamageSource = attacker;

                try
                {
                    if (cd.LastDamageSource != null && cd.LastDamageSource is Ped attackerPed && !cd.StateBlocked)
                    {
                        ConfigureCombatBehavior(clone);
                        cd.CombatLocked = true;
                        cd.CombatLockStartMs = Game.GameTime;
                        cd.CombatLockReason = "took_damage";
                        IssueCombat(cd, Game.Player.Character);
                        cd.State = CloneState.Combat;
                        cd.LastStateChangeMs = Game.GameTime;
                    }
                    else
                    {
                        cd.State = CloneState.SeekingCover;
                        cd.LastStateChangeMs = Game.GameTime;
                        TrySeekCover(cd);
                    }
                }
                catch { }
            }

            cd.LastHealth = cur;
        }
        catch { }
    }

    private Entity FindLastDamageSource(CloneData cd)
    {
        try
        {
            var clone = cd.Ped;
            if (clone == null || !clone.Exists()) return null;

            var nearby = World.GetNearbyPeds(clone.Position, 20f)
                              .Where(p => p != null && p.Exists() && p.IsAlive).ToList();

            foreach (var p in nearby)
            {
                try
                {
                    if (Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY, clone.Handle, p.Handle, true))
                    {
                        return p;
                    }
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private void TrackPlayerDamage(Ped player)
    {
        try
        {
            if (_lastPlayerHealth < 0f) _lastPlayerHealth = player.Health;

            if (player.Health < _lastPlayerHealth)
            {
                _lastPlayerDamagedAtMs = Game.GameTime;
            }

            _lastPlayerHealth = player.Health;
        }
        catch { }
    }

    private bool PlayerWasAttackedRecently()
    {
        return (Game.GameTime - _lastPlayerDamagedAtMs) < PlayerReactionWindowMs;
    }

    private Ped FindPlayerAttacker(Ped player)
    {
        try
        {
            if (player == null || !player.Exists()) return null;
            var nearby = World.GetNearbyPeds(player.Position, AggroRange)
                              .Where(p => p != null && p.Exists() && p.IsAlive && !p.IsInjured && p != player).ToList();

            foreach (var p in nearby)
            {
                try
                {
                    if (Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY, player.Handle, p.Handle, true))
                    {
                        return p;
                    }
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    // -------------------------
    // State-block helpers
    // -------------------------
    private void SetStateBlock(CloneData cd, string reason)
    {
        cd.StateBlocked = true;
        cd.StateBlockedStartMs = Game.GameTime;
        cd.StateBlockedReason = reason;
    }

    private void ClearStateBlock(CloneData cd, string reason)
    {
        try
        {
            cd.StateBlocked = false;
            cd.StateBlockedStartMs = -1;
            cd.StateBlockedReason = null;
            cd.LastStateChangeMs = Game.GameTime;

            // release any forced task persistence so native tasks can be re-issued normally
            try
            {
                if (cd.Ped != null && cd.Ped.Exists())
                {
                    cd.Ped.BlockPermanentEvents = false;
                    cd.Ped.AlwaysKeepTask = false;
                }
            }
            catch { }
        }
        catch { }
    }

    // -------------------------
    // Relationship Group
    // -------------------------
    private void CreateCompanionRelationshipGroup()
    {
        try
        {
            CompanionRelGroup = World.AddRelationshipGroup("shvdn_companion_group");
            var playerGroup = Game.Player.Character.RelationshipGroup;
            try
            {
                CompanionRelGroup.SetRelationshipBetweenGroups(playerGroup, Relationship.Companion, true);
            }
            catch
            {
                int compHash = (int)CompanionRelGroup.NativeValue;
                int playerHash = (int)playerGroup.NativeValue;
                int relFriendly = 1;
                Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, relFriendly, compHash, playerHash);
                Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, relFriendly, playerHash, compHash);
            }
        }
        catch { }
    }

    // -------------------------
    // Spawning / Removal
    // -------------------------
    public bool TrySpawnClone(uint selectedModelHash = 0u)
    {
        try
        {
            var player = Game.Player.Character;
            if (player == null || !player.Exists()) return false;

            if (_clones.Count >= MaxClones)
            {
                Notification.Show("Max clones reached (" + MaxClones + ")");
                return false;
            }

            Vector3 spawnPos = player.GetOffsetPosition(new Vector3((_clones.Count + 1) * 0.6f, -1.2f - (_clones.Count * 0.6f), 0f));

            Ped clone = null;

            if (selectedModelHash != 0u)
            {
                clone = CreateCompanionFromModelHash(player, spawnPos, selectedModelHash);
            }
            else
            {
                clone = CreateCloneFromPlayer(player, spawnPos);
            }

            if (clone == null || !clone.Exists())
            {
                Notification.Show("Failed to create clone");
                return false;
            }

            try
            {
                Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, clone.Handle, true, true);
                Function.Call(Hash.SET_PED_MAX_HEALTH, clone.Handle, 250);
                Function.Call(Hash.SET_ENTITY_HEALTH, clone.Handle, Math.Min(player.Health, 250));
                Function.Call(Hash.SET_PED_ARMOUR, clone.Handle, 450);
                if (CompanionRelGroup != null)
                    Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, clone.Handle, (int)CompanionRelGroup.NativeValue);
                else
                    Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, clone.Handle, (int)Game.Player.Character.RelationshipGroup.NativeValue);
            }
            catch { }

            clone.BlockPermanentEvents = false;
            clone.AlwaysKeepTask = false;

            ConfigureCombatBehavior(clone);

            var cd = new CloneData()
            {
                Ped = clone,
                State = CloneState.Following,
                PrevState = CloneState.Idle,
                LastStateChangeMs = Game.GameTime,
                LastFollowIssuedMs = Game.GameTime,
                LastVehicleSyncMs = Game.GameTime,
                LastHealth = clone.Health,
                LastSeenEnemyMs = -1
            };

            AssignRandomWeaponOnce(cd);

            Vector3 offset = GetFollowOffset(_clones.Count);
            try
            {
                Function.Call(Hash.TASK_FOLLOW_TO_OFFSET_OF_ENTITY, clone.Handle, player.Handle,
                    offset.X, offset.Y, offset.Z, 10000, -1, FollowDistance, true);
            }
            catch { }

            _clones.Add(cd);
            EnsureCloneBlip(cd);
            Notification.Show("Your cloned as companion. (" + _clones.Count + "/" + MaxClones + ")");
            return true;
        }
        catch (Exception ex)
        {
            Notification.Show("Spawn error: " + ex.Message);
            return false;
        }
    }

    private bool IsSelectableClone(CloneData cd)
    {
        try
        {
            return cd != null &&
                   cd.Ped != null &&
                   cd.Ped.Exists() &&
                   !cd.Ped.IsDead &&
                   cd.Ped.Health > 0;
        }
        catch
        {
            return false;
        }
    }

    private int GetFirstSelectableCloneIndex()
    {
        for (int i = 0; i < _clones.Count; i++)
        {
            if (IsSelectableClone(_clones[i]))
                return i;
        }
        return -1;
    }

    private int GetNextSelectableCloneIndex(int startIndex, int direction)
    {
        if (_clones.Count == 0)
            return -1;

        if (direction != 1 && direction != -1)
            direction = 1;

        int idx = startIndex;

        for (int step = 0; step < _clones.Count; step++)
        {
            idx += direction;

            if (idx < 0) idx = _clones.Count - 1;
            if (idx >= _clones.Count) idx = 0;

            if (IsSelectableClone(_clones[idx]))
                return idx;
        }

        return -1;
    }

    private void NormalizeRemoveSelection()
    {
        if (!_removeMode)
        {
            _selectedCloneIndex = -1;
            return;
        }

        if (_clones.Count == 0)
        {
            _selectedCloneIndex = -1;
            _removeMode = false;
            return;
        }

        if (_selectedCloneIndex < 0 ||
            _selectedCloneIndex >= _clones.Count ||
            !IsSelectableClone(_clones[_selectedCloneIndex]))
        {
            _selectedCloneIndex = GetFirstSelectableCloneIndex();
        }
    }

    private void ToggleRemoveMode()
    {
        _removeMode = !_removeMode;

        if (_removeMode)
        {
            NormalizeRemoveSelection();

            if (_selectedCloneIndex < 0)
            {
                _removeMode = false;
                UpdateScriptInterval();
                Notification.Show("No companion to delete.");
                return;
            }

            _lastRemoveNavMs = Game.GameTime;
            UpdateScriptInterval();
        }
        else
        {
            _selectedCloneIndex = -1;
            UpdateScriptInterval();
        }
    }

    private void MoveRemoveSelection(int direction)
    {
        try
        {
            if (!_removeMode)
                return;

            if ((Game.GameTime - _lastRemoveNavMs) < RemoveNavThrottleMs)
                return;

            NormalizeRemoveSelection();
            if (_selectedCloneIndex < 0)
                return;

            int next = GetNextSelectableCloneIndex(_selectedCloneIndex, direction);
            if (next >= 0)
            {
                _selectedCloneIndex = next;
                _lastRemoveNavMs = Game.GameTime;
            }
        }
        catch { }
    }

    private void RemoveSelectedClone()
    {
        try
        {
            if (!_removeMode)
                return;

            NormalizeRemoveSelection();

            if (_selectedCloneIndex < 0 || _selectedCloneIndex >= _clones.Count)
            {
                Notification.Show("No companion selected.");
                return;
            }

            int removedIndex = _selectedCloneIndex;
            var cd = _clones[_selectedCloneIndex];

            RemoveClone(cd);

            if (_clones.Count == 0)
            {
                _removeMode = false;
                _selectedCloneIndex = -1;
                return;
            }

            if (removedIndex >= _clones.Count)
                removedIndex = _clones.Count - 1;

            _selectedCloneIndex = removedIndex;
            NormalizeRemoveSelection();
        }
        catch { }
    }

    private void DrawRemoveSelectionMarker()
    {
        try
        {
            if (!_removeMode)
                return;

            NormalizeRemoveSelection();

            if (_selectedCloneIndex < 0 || _selectedCloneIndex >= _clones.Count)
                return;

            var cd = _clones[_selectedCloneIndex];
            if (!IsSelectableClone(cd))
                return;

            var ped = cd.Ped;
            if (ped == null || !ped.Exists())
                return;

            Vector3 pos = ped.Position + new Vector3(0f, 0f, 1.45f);

            // Marker nổi phía trên đầu, chĩa xuống mục tiêu
            Function.Call(Hash.DRAW_MARKER,
                2,                      // marker type (nếu muốn kiểu khác, đổi số này)
                pos.X, pos.Y, pos.Z,
                0f, 0f, 0f,             // direction
                0f, 180f, 0f,           // rotation
                0.30f, 0.30f, 0.75f,    // scale
                255, 60, 60, 220,       // color
                false, true, 2, false,  // bob, faceCamera, p19, rotate
                null, null, false);     // textureDict, textureName, drawOnEnts
        }
        catch { }
    }

    private void EnsureCloneBlip(CloneData cd)
    {
        try
        {
            if (cd == null) return;

            var ped = cd.Ped;
            if (ped == null || !ped.Exists() || ped.IsDead)
            {
                RemoveCloneBlip(cd);
                return;
            }

            // already exists
            if (cd.MapBlip != null && cd.MapBlip.Exists())
                return;

            cd.MapBlip = ped.AddBlip();
            if (cd.MapBlip == null || !cd.MapBlip.Exists())
                return;

            // green round blip
            try { Function.Call(Hash.SET_BLIP_SPRITE, cd.MapBlip.Handle, 1); } catch { }
            try { Function.Call(Hash.SET_BLIP_COLOUR, cd.MapBlip.Handle, 2); } catch { } // green
            try { Function.Call(Hash.SET_BLIP_SCALE, cd.MapBlip.Handle, 0.85f); } catch { }
            try { Function.Call(Hash.SET_BLIP_AS_SHORT_RANGE, cd.MapBlip.Handle, false); } catch { }
        }
        catch { }
    }

    private void RemoveCloneBlip(CloneData cd)
    {
        try
        {
            if (cd == null) return;

            if (cd.MapBlip != null && cd.MapBlip.Exists())
            {
                try
                {
                    cd.MapBlip.Delete();
                }
                catch { }
            }

            cd.MapBlip = null;
        }
        catch { }
    }

    private void RemoveAllClones()
    {
        foreach (var cd in _clones)
        {
            try
            {
                CleanupCloneWeaponOnDeath(cd); // NEW
                RemoveCloneBlip(cd);

                var c = cd.Ped;
                if (c != null && c.Exists())
                {
                    Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, c.Handle, true, true);
                    c.MarkAsNoLongerNeeded();
                    c.Delete();
                }
            }
            catch { }
        }
        _clones.Clear();
        Notification.Show("All clones removed.");
    }

    private void RemoveClone(CloneData cd)
    {
        try
        {
            if (cd == null) return;

            CleanupCloneWeaponOnDeath(cd); // NEW
            RemoveCloneBlip(cd);

            var c = cd.Ped;
            if (c != null && c.Exists())
            {
                try
                {
                    Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, c.Handle, true, true);
                    c.MarkAsNoLongerNeeded();
                    c.Delete();
                }
                catch { }
            }
            _clones.Remove(cd);
        }
        catch { }
    }
}