using GTA;
using GTA.Math;
using GTA.Native;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

public class Collateral : Script
{
    private static string L(string key, string fallback = "")
    {
        return Language.Get(key, fallback);
    }

    private const int FRANKLIN_HASH = -1692214353;
    private const int MICHAEL_HASH = 225514697;
    private const int TREVOR_HASH = -1686040670;

    private static readonly HashSet<int> EligibleVehicleClasses = new HashSet<int>
    {
        0, 1, 2, 3, 5, 6, 7, 8, 13, 15, 16
    };

    private enum ClusterPoolKind
    {
        Standard = 0,
        Aircraft = 1
    }

    private static readonly string ClusterStateRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "Fleeca Bank Loan"
    );

    private static string GetClusterFilePrefix(ClusterPoolKind pool)
    {
        return pool == ClusterPoolKind.Aircraft
            ? "collateral_cluster_state_air_"
            : "collateral_cluster_state_std_";
    }

    private static string GetCollateralClusterStateFileForOwner(int ownerHash, ClusterPoolKind pool)
    {
        Directory.CreateDirectory(ClusterStateRoot);
        return Path.Combine(ClusterStateRoot, $"{GetClusterFilePrefix(pool)}{ownerHash}.dat");
    }

    private static void SaveCollateralClusterIndexForOwner(int ownerHash, int clusterIndex, ClusterPoolKind pool)
    {
        try
        {
            if (ownerHash == 0 || clusterIndex < 0)
                return;

            File.WriteAllText(
                GetCollateralClusterStateFileForOwner(ownerHash, pool),
                clusterIndex.ToString(CultureInfo.InvariantCulture));
        }
        catch { }
    }

    private static int LoadCollateralClusterIndexForOwner(int ownerHash, ClusterPoolKind pool)
    {
        try
        {
            if (ownerHash == 0)
                return -1;

            string file = GetCollateralClusterStateFileForOwner(ownerHash, pool);
            if (!File.Exists(file))
                return -1;

            string text = File.ReadAllText(file).Trim();
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx) &&
                idx >= 0 && idx < GetClustersForPool(pool).Count)
            {
                return idx;
            }
        }
        catch { }

        return -1;
    }

    public static void ClearCollateralClusterStateForOwner(int ownerHash)
    {
        try
        {
            if (ownerHash == 0)
                return;

            foreach (ClusterPoolKind pool in Enum.GetValues(typeof(ClusterPoolKind)))
            {
                string file = GetCollateralClusterStateFileForOwner(ownerHash, pool);
                if (File.Exists(file))
                    File.Delete(file);
            }
        }
        catch { }
    }

    private static bool TryParseOwnerHashFromClusterStateFile(string path, ClusterPoolKind pool, out int ownerHash)
    {
        ownerHash = 0;

        try
        {
            string fileName = Path.GetFileNameWithoutExtension(path);
            string prefix = GetClusterFilePrefix(pool);
            if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            string raw = fileName.Substring(prefix.Length);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out ownerHash);
        }
        catch
        {
            ownerHash = 0;
            return false;
        }
    }

    private static HashSet<int> LoadOccupiedClusterIndices(int ignoreOwnerHash, ClusterPoolKind pool)
    {
        var occupied = new HashSet<int>();

        try
        {
            if (!Directory.Exists(ClusterStateRoot))
                return occupied;

            string pattern = GetClusterFilePrefix(pool) + "*.dat";
            foreach (string file in Directory.GetFiles(ClusterStateRoot, pattern))
            {
                try
                {
                    if (!TryParseOwnerHashFromClusterStateFile(file, pool, out int ownerHash))
                        continue;

                    if (ownerHash == 0 || ownerHash == ignoreOwnerHash)
                        continue;

                    int idx = LoadCollateralClusterIndexFromFile(file, GetClustersForPool(pool).Count);
                    if (idx >= 0 && idx < GetClustersForPool(pool).Count)
                        occupied.Add(idx);
                }
                catch { }
            }
        }
        catch { }

        return occupied;
    }

    private static int LoadCollateralClusterIndexFromFile(string file, int clusterCount)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                return -1;

            string text = File.ReadAllText(file).Trim();
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx) &&
                idx >= 0 && idx < clusterCount)
            {
                return idx;
            }
        }
        catch { }

        return -1;
    }

    private int PickClusterIndexExcluding(HashSet<int> occupied, List<ClusterDefinition> clusters)
    {
        try
        {
            var candidates = Enumerable.Range(0, clusters.Count)
                .Where(i => occupied == null || !occupied.Contains(i))
                .ToList();

            if (candidates.Count == 0)
                candidates = Enumerable.Range(0, clusters.Count).ToList();

            if (candidates.Count == 0)
                return -1;

            return candidates[_rng.Next(candidates.Count)];
        }
        catch
        {
            return -1;
        }
    }

    private static readonly object _signalLock = new object();
    private static readonly Queue<int> _pendingOwnerSignals = new Queue<int>();
    private static readonly HashSet<int> _pendingOwnerSignalSet = new HashSet<int>();

    private const int SCAN_INTERVAL_MS = 800;
    private const int IDLE_INTERVAL_MS = 1500;
    private const float CLUSTER_MATCH_TOLERANCE = 8.0f;

    private readonly Random _rng = new Random();
    private int _lastOwnerHash = 0;
    private string _lastProcessedSignature = string.Empty;

    private const int CLUSTER19_AIRCRAFT_CLUSTER_INDEX = 3; // Cluster 16=0, 17=1, 18=2, 19=3
    private const int CLUSTER19_FADE_MS = 800;
    private const float CLUSTER19_INTERACTION_RADIUS = 2.0f;
    private const float CLUSTER19_SLOT_MATCH_TOLERANCE = 2.0f;

    private static readonly Vector3 CLUSTER19_DOOR_SLOT_POSITION = new Vector3(-1999.764f, 3195.267f, 34.03099f);
    private static readonly Vector3 CLUSTER19_MARKER_POSITION = new Vector3(-2021.011000f, 3157.749000f, 32.810300f);
    private static readonly Vector3 CLUSTER19_INTERIOR_SPAWN_POSITION = new Vector3(-2018.309000f, 3163.423000f, 32.810300f);

    private static readonly Vector3 CLUSTER19_EXIT_SPAWN_POSITION = new Vector3(-1933.308f, 2966.785f, 34.03104f);
    private const float CLUSTER19_EXIT_SPAWN_HEADING = 59.45889f;

    private static readonly Vector3 CLUSTER19_EXIT_PED_POSITION = new Vector3(-2023.213000f, 3154.612000f, 32.810290f);
    private const float CLUSTER19_EXIT_PED_HEADING = 148.9726f;

    private static readonly Vector3 CLUSTER19_INTERIOR_EXIT_MARKER_POSITION = new Vector3(-2019.522000f, 3161.121000f, 32.810320f);
    private const float CLUSTER19_INTERIOR_EXIT_RADIUS = 1.5f;

    // === NEW: khu quân sự / khóa sao truy nã ===
    private static readonly Vector3 MILITARY_GATE_POSITION = new Vector3(-1589.334000f, 2794.669000f, 17.030620f);
    private const float MILITARY_GATE_RADIUS = 10.0f;
    private const int MILITARY_WANTED_LOCK_DURATION_MS = 150000;
    private const int MILITARY_WANTED_SYNC_INTERVAL_MS = 250;

    private bool _militaryWantedLockActive = false;
    private int _militaryWantedLockEndGameTime = 0;
    private int _militaryWantedLockWantedLevel = 0;
    private int _militaryWantedLockLastSyncGameTime = 0;
    private int _militaryWantedLockLastAppliedLevel = -1;

    // Trạng thái đang ở bên trong hay không
    private bool _cluster19InsideInterior = false;

    private bool _cluster19TransitionBusy = false;
    private bool _cluster19TransitionToInterior = false;
    private int _cluster19TransitionStartGameTime = 0;
    private bool _cluster19TeleportDone = false;

    private static readonly BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private sealed class ClusterSlot
    {
        public readonly Vector3 Position;
        public readonly float Heading;

        public ClusterSlot(float x, float y, float z, float heading)
        {
            Position = new Vector3(x, y, z);
            Heading = heading;
        }
    }

    public static void NotifyCollateralSeized(int ownerHash)
    {
        try
        {
            if (ownerHash == 0)
                return;

            lock (_signalLock)
            {
                // Tránh enqueue trùng liên tục cùng một owner
                if (_pendingOwnerSignalSet.Add(ownerHash))
                    _pendingOwnerSignals.Enqueue(ownerHash);
            }
        }
        catch
        {
        }
    }

    private static bool TryPeekPendingSignal(out int ownerHash)
    {
        ownerHash = 0;

        try
        {
            lock (_signalLock)
            {
                if (_pendingOwnerSignals.Count == 0)
                    return false;

                ownerHash = _pendingOwnerSignals.Peek();
                return ownerHash != 0;
            }
        }
        catch
        {
            ownerHash = 0;
            return false;
        }
    }

    private static bool TryDequeuePendingSignal(out int ownerHash)
    {
        ownerHash = 0;

        try
        {
            lock (_signalLock)
            {
                if (_pendingOwnerSignals.Count == 0)
                    return false;

                ownerHash = _pendingOwnerSignals.Dequeue();
                _pendingOwnerSignalSet.Remove(ownerHash);
                return ownerHash != 0;
            }
        }
        catch
        {
            ownerHash = 0;
            return false;
        }
    }

    private sealed class ClusterDefinition
    {
        public readonly string Name;
        public readonly ClusterSlot[] Slots;

        public ClusterDefinition(string name, ClusterSlot[] slots)
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Cluster" : name;
            Slots = slots ?? Array.Empty<ClusterSlot>();
        }
    }

    private sealed class LockedVehicleInfo
    {
        public object Source;
        public int OwnerHash;
        public uint ModelHash;
        public int VehicleClass;
        public string Plate;
        public Vector3 Position;
        public float Heading;
        public Vehicle RuntimeVehicle;
        public int ListIndex;

        public string BuildIdentity()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1:X}|{2}|{3}|{4}",
                OwnerHash,
                ModelHash,
                VehicleClass,
                NormalizePlate(Plate),
                ListIndex);
        }
    }

    private static readonly List<ClusterDefinition> StandardClusters = new List<ClusterDefinition>
    {
        new ClusterDefinition("Cluster 1", new[]
        {
            new ClusterSlot(-2953.07f, 462.1911f, 14.59304f, 210.177f),
            new ClusterSlot(-2956.898f, 462.4006f, 14.59767f, 209.0458f),
            new ClusterSlot(-2960.456f, 463.029f, 14.74753f, 203.8263f)
        }),

        new ClusterDefinition("Cluster 2", new[]
        {
            new ClusterSlot(-365.823f, -99.72431f, 45.22906f, 161.1238f),
            new ClusterSlot(-369.2896f, -98.64935f, 44.89526f, 160.7236f),
            new ClusterSlot(-362.3854f, -100.8392f, 45.01766f, 160.1295f)
        }),

        new ClusterDefinition("Cluster 3", new[]
        {
            new ClusterSlot(295.4066f, -343.0847f, 44.30229f, 249.6728f),
            new ClusterSlot(281.6292f, -326.9451f, 44.29925f, 249.2739f),
            new ClusterSlot(268.7345f, -325.6446f, 44.45128f, 249.1309f)
        }),

        new ClusterDefinition("Cluster 4", new[]
        {
            new ClusterSlot(117.5844f, -1081.743f, 28.76846f, 0.5188924f),
            new ClusterSlot(125.0213f, -1082.211f, 28.7247f, 358.5828f),
            new ClusterSlot(121.288f, -1081.002f, 28.53819f, 357.639f)
        }),

        new ClusterDefinition("Cluster 5", new[]
        {
            new ClusterSlot(138.8753f, -1069.623f, 28.76171f, 181.0983f),
            new ClusterSlot(132.2722f, -1069.478f, 28.71862f, 178.5303f),
            new ClusterSlot(135.483f, -1069.603f, 28.70829f, 180.4034f)
        }),

        new ClusterDefinition("Cluster 6", new[]
        {
            new ClusterSlot(110.5954f, -1052.875f, 28.73533f, 63.70826f),
            new ClusterSlot(107.462f, -1059.721f, 28.62478f, 66.17732f),
            new ClusterSlot(109.3774f, -1056.427f, 28.556f, 65.92625f)
        }),

        new ClusterDefinition("Cluster 7", new[]
        {
            new ClusterSlot(1250.557f, 2712.031f, 37.53669f, 85.01753f),
            new ClusterSlot(1250.209f, 2707.867f, 37.38773f, 85.78497f),
            new ClusterSlot(1250.645f, 2716.127f, 37.43118f, 77.6014f)
        }),

        new ClusterDefinition("Cluster 8", new[]
        {
            new ClusterSlot(1220.96f, 2712.357f, 37.42118f, 168.0205f),
            new ClusterSlot(1224.84f, 2711.481f, 37.38919f, 169.2858f),
            new ClusterSlot(1217.419f, 2713.197f, 37.36173f, 169.7593f)
        }),

        new ClusterDefinition("Cluster 9", new[]
        {
            new ClusterSlot(-1215.965f, -378.969f, 42.04406f, 207.4669f),
            new ClusterSlot(-1219.729f, -380.8848f, 42.13305f, 208.048f),
            new ClusterSlot(-1212.4f, -377.5823f, 42.21961f, 208.0606f)
        }),

        new ClusterDefinition("Cluster 10", new[]
        {
            new ClusterSlot(-342.9441f, -50.9661f, 53.67411f, 70.62902f),
            new ClusterSlot(-343.8848f, -54.31155f, 53.73024f, 69.93724f),
            new ClusterSlot(-345.088f, -57.47368f, 53.99776f, 70.78049f)
        }),

        new ClusterDefinition("Cluster 11", new[]
        {
            new ClusterSlot(-377.9294f, -72.7196f, 53.78831f, 249.7068f),
            new ClusterSlot(-376.7171f, -69.43301f, 53.83642f, 250.6965f),
            new ClusterSlot(-379.2896f, -75.96471f, 53.87246f, 250.0562f)
        }),

        new ClusterDefinition("Cluster 12", new[]
        {
            new ClusterSlot(-2939.077f, 477.0901f, 14.74993f, 299.3415f),
            new ClusterSlot(-2939.08f, 470.4199f, 14.62232f, 298.7917f),
            new ClusterSlot(-2938.99f, 473.8595f, 14.55894f, 299.0621f)
        }),

        new ClusterDefinition("Cluster 13", new[]
        {
            new ClusterSlot(266.4434f, -332.2866f, 44.34423f, 250.757f),
            new ClusterSlot(289.6369f, -326.2482f, 44.271f, 68.89964f),
            new ClusterSlot(294.033f, -346.2031f, 44.41407f, 250.6427f)
        }),

        new ClusterDefinition("Cluster 14", new[]
        {
            new ClusterSlot(282.9337f, -323.8428f, 44.18139f, 250.1244f),
            new ClusterSlot(278.0288f, -336.6967f, 44.3151f, 249.5966f),
            new ClusterSlot(283.5606f, -342.4449f, 44.50755f, 69.94807f)
        }),

        new ClusterDefinition("Cluster 15", new[]
        {
            new ClusterSlot(-815.2587f, -1097.992f, 10.38337f, 118.4872f),
            new ClusterSlot(-811.5187f, -1104.522f, 10.16898f, 120.0901f),
            new ClusterSlot(-813.5706f, -1101.4f, 10.19627f, 120.2896f)
        }),
    };

    private static readonly List<ClusterDefinition> AircraftClusters = new List<ClusterDefinition>
    {
        new ClusterDefinition("Cluster 16", new[]
        {
            new ClusterSlot(-1050.652f, -3497.26f, 15.36408f, 327.9553f),
            new ClusterSlot(-861.9043f, -3221.533f, 15.67424f, 60.60456f),
            new ClusterSlot(-977.2495f, -3298.377f, 14.88389f, 61.00814f)
        }),

        new ClusterDefinition("Cluster 17", new[]
        {
            new ClusterSlot(-1861.405f, -3141.485f, 22.73563f, 239.8156f),
            new ClusterSlot(-1475.816f, -2726.83f, 14.92513f, 240.5456f),
            new ClusterSlot(-1313.248f, -2468.216f, 22.72782f, 239.7324f)
        }),

        new ClusterDefinition("Cluster 18", new[]
        {
            new ClusterSlot(2088.68f, 5098.111f, 53.55127f, 126.0993f),
            new ClusterSlot(2133.531f, 5172.811f, 55.55068f, 221.6425f),
            new ClusterSlot(2150.128f, 5038.62f, 51.54543f, 316.3975f)
        }),

        new ClusterDefinition("Cluster 19", new[]
        {
            new ClusterSlot(-2454.125f, 3173.197f, 41.75894f, 239.4178f),
            new ClusterSlot(-2253.737f, 3161.327f, 34.03071f, 238.9708f),
            new ClusterSlot(-1999.764f, 3195.267f, 34.03099f, 148.9726f)
        }),
    };

    private static List<ClusterDefinition> GetClustersForPool(ClusterPoolKind pool)
    {
        return pool == ClusterPoolKind.Aircraft ? AircraftClusters : StandardClusters;
    }

    private static bool IsAircraftCollateralClass(int vehicleClass)
    {
        return vehicleClass == 15 || vehicleClass == 16;
    }

    public Collateral()
    {
        Interval = SCAN_INTERVAL_MS;
        Tick += OnTick;
    }

    private void OnTick(object sender, EventArgs e)
    {
        try
        {
            if (Game.IsLoading)
            {
                Interval = IDLE_INTERVAL_MS;
                return;
            }

            // NEW: khối khóa sao truy nã / help-box khu quân sự
            if (UpdateMilitaryWantedLockInteraction())
            {
                Interval = 0;
                return;
            }

            int currentOwnerHash = GetCurrentCharacterHash();
            if (currentOwnerHash == 0)
            {
                Interval = IDLE_INTERVAL_MS;
                return;
            }

            // Chỉ xử lý khi FleecaBank bắn tín hiệu đúng owner
            if (TryPeekPendingSignal(out int pendingOwnerHash) && pendingOwnerHash == currentOwnerHash)
            {
                TryDequeuePendingSignal(out _);
                ProcessCollateralRelocationForOwner(currentOwnerHash);

                _lastOwnerHash = currentOwnerHash;
                _lastProcessedSignature = string.Empty;
            }

            // Tính năng cluster 19 / slot -1999.764... chỉ chạy khi đúng cluster 19 đang active
            if (UpdateCluster19Interaction(currentOwnerHash))
            {
                Interval = 0;
                return;
            }

            Interval = IDLE_INTERVAL_MS;
        }
        catch
        {
            Interval = IDLE_INTERVAL_MS;
        }
    }

    // === NEW: khu quân sự / khóa sao truy nã ===
    private bool UpdateMilitaryWantedLockInteraction()
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
            {
                if (_militaryWantedLockActive)
                    UpdateMilitaryWantedLockTick(true);

                return _militaryWantedLockActive;
            }

            int now = Game.GameTime;

            // Nếu đang khóa thì giữ trạng thái cho tới khi hết 150 giây
            if (_militaryWantedLockActive)
            {
                if (now >= _militaryWantedLockEndGameTime)
                {
                    ClearMilitaryWantedLock();
                }
                else
                {
                    UpdateMilitaryWantedLockTick(false);
                }

                // Đang khóa thì không hiện help-box
                return true;
            }

            // Chưa khóa: chỉ hiện help-box khi đứng gần vị trí
            if (Distance3D(player.Position, MILITARY_GATE_POSITION) <= MILITARY_GATE_RADIUS)
            {
                // Nếu nhấn Enter thì kích hoạt ngay, không vẽ help-box trong frame này
                if (Game.IsControlJustPressed(Control.FrontendAccept))
                {
                    BeginMilitaryWantedLock();
                    GTA.UI.Screen.ShowSubtitle("~HUD_COLOUR_CONTROLLER_MICHAEL~Access granted~s~", 2500);
                    return true;
                }

                GTA.UI.Screen.ShowHelpTextThisFrame(
                    L("HELP_ENTER_MILITARY_BASE",
                    "Nhấn ~INPUT_FRONTEND_ACCEPT~ để vượt rào khu quân sự trong 150 giây (ưu tiên nên đang không có sao truy nã nào)")
                );

                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private void BeginMilitaryWantedLock()
    {
        try
        {
            int wantedLevel = GetCurrentWantedLevel();
            wantedLevel = Math.Max(0, Math.Min(5, wantedLevel));

            _militaryWantedLockActive = true;
            _militaryWantedLockWantedLevel = wantedLevel;
            _militaryWantedLockEndGameTime = Game.GameTime + MILITARY_WANTED_LOCK_DURATION_MS;
            _militaryWantedLockLastSyncGameTime = 0;
            _militaryWantedLockLastAppliedLevel = -1;

            ApplyMilitaryWantedLock(true);

            GTA.UI.Screen.ShowSubtitle("Access granted", 2000);
        }
        catch
        {
        }
    }

    private void UpdateMilitaryWantedLockTick(bool forceImmediate)
    {
        try
        {
            if (!_militaryWantedLockActive)
                return;

            int now = Game.GameTime;
            if (now >= _militaryWantedLockEndGameTime)
            {
                ClearMilitaryWantedLock();
                return;
            }

            if (forceImmediate || (now - _militaryWantedLockLastSyncGameTime) >= MILITARY_WANTED_SYNC_INTERVAL_MS)
            {
                ApplyMilitaryWantedLock(false);
                _militaryWantedLockLastSyncGameTime = now;
            }
        }
        catch
        {
        }
    }

    private void ApplyMilitaryWantedLock(bool force)
    {
        try
        {
            if (!_militaryWantedLockActive)
                return;

            int wantedLevel = Math.Max(0, Math.Min(5, _militaryWantedLockWantedLevel));

            // Giới hạn mức sao tối đa theo level hiện tại
            Function.Call(Hash.SET_MAX_WANTED_LEVEL, wantedLevel);

            // Ghìm lại wanted level hiện tại. Không ép liên tục mỗi frame, chỉ sync khi cần.
            int currentWanted = GetCurrentWantedLevel();
            if (force || currentWanted != wantedLevel || _militaryWantedLockLastAppliedLevel != wantedLevel)
            {
                Function.Call(Hash.SET_PLAYER_WANTED_LEVEL, Game.Player, wantedLevel, false);
                Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, Game.Player, false);
                _militaryWantedLockLastAppliedLevel = wantedLevel;
            }
        }
        catch
        {
        }
    }

    private void ClearMilitaryWantedLock()
    {
        try
        {
            _militaryWantedLockActive = false;
            _militaryWantedLockEndGameTime = 0;
            _militaryWantedLockWantedLevel = 0;
            _militaryWantedLockLastSyncGameTime = 0;
            _militaryWantedLockLastAppliedLevel = -1;

            // Trả giới hạn wanted về mặc định
            Function.Call(Hash.SET_MAX_WANTED_LEVEL, 5);
        }
        catch
        {
        }
    }

    private static int GetCurrentWantedLevel()
    {
        try
        {
            int level = Function.Call<int>(Hash.GET_PLAYER_WANTED_LEVEL, Game.Player);
            if (level < 0) return 0;
            if (level > 5) return 5;
            return level;
        }
        catch
        {
            return 0;
        }
    }

    private void ProcessCollateralRelocationForOwner(int ownerHash)
    {
        try
        {
            if (ownerHash == 0)
                return;

            List<LockedVehicleInfo> eligibleLockedVehicles = CollectEligibleLockedVehicles(ownerHash);
            if (eligibleLockedVehicles.Count == 0)
                return;

            List<LockedVehicleInfo> standardVehicles = eligibleLockedVehicles
                .Where(v => !IsAircraftCollateralClass(v.VehicleClass))
                .ToList();

            List<LockedVehicleInfo> aircraftVehicles = eligibleLockedVehicles
                .Where(v => IsAircraftCollateralClass(v.VehicleClass))
                .ToList();

            ProcessCollateralRelocationBatchForOwner(ownerHash, standardVehicles, ClusterPoolKind.Standard);
            ProcessCollateralRelocationBatchForOwner(ownerHash, aircraftVehicles, ClusterPoolKind.Aircraft);
        }
        catch
        {
        }
    }

    private void ProcessCollateralRelocationBatchForOwner(int ownerHash, List<LockedVehicleInfo> vehicles, ClusterPoolKind pool)
    {
        try
        {
            if (ownerHash == 0 || vehicles == null || vehicles.Count == 0)
                return;

            List<ClusterDefinition> clusters = GetClustersForPool(pool);
            if (clusters == null || clusters.Count == 0)
                return;

            string currentSignature = BuildBatchSignature(ownerHash, vehicles);

            int savedClusterIndex = LoadCollateralClusterIndexForOwner(ownerHash, pool);
            HashSet<int> occupied = LoadOccupiedClusterIndices(ownerHash, pool);

            int chosenClusterIndex = -1;

            // 1) Ưu tiên cluster đã lưu của chính nhân vật này
            if (savedClusterIndex >= 0 &&
                savedClusterIndex < clusters.Count &&
                !occupied.Contains(savedClusterIndex))
            {
                chosenClusterIndex = savedClusterIndex;
            }
            else
            {
                // 2) Nếu cluster đang bị chiếm thì thử tìm cluster hiện tại từ vị trí xe
                if (TryGetExistingClusterIndex(vehicles, clusters, out int existingClusterIndex) &&
                    existingClusterIndex >= 0 &&
                    existingClusterIndex < clusters.Count &&
                    !occupied.Contains(existingClusterIndex))
                {
                    chosenClusterIndex = existingClusterIndex;
                }
                else
                {
                    // 3) Nếu không có cluster cũ hợp lệ thì roll một cluster chưa bị chiếm
                    chosenClusterIndex = PickClusterIndexExcluding(occupied, clusters);
                }
            }

            if (chosenClusterIndex < 0 || chosenClusterIndex >= clusters.Count)
                return;

            SaveCollateralClusterIndexForOwner(ownerHash, chosenClusterIndex, pool);
            RelocateBatchToCluster(vehicles, clusters[chosenClusterIndex], ownerHash, chosenClusterIndex, pool);

            _lastOwnerHash = ownerHash;
            _lastProcessedSignature = currentSignature;
        }
        catch
        {
        }
    }

    private List<LockedVehicleInfo> CollectEligibleLockedVehicles(int ownerHash)
    {
        List<LockedVehicleInfo> result = new List<LockedVehicleInfo>();

        try
        {
            List<object> snapshot = GetPersistentVehicleSnapshot();
            if (snapshot.Count == 0)
                return result;

            for (int i = 0; i < snapshot.Count; i++)
            {
                object entry = snapshot[i];
                if (entry == null)
                    continue;

                int entryOwnerHash = ReadField<int>(entry, "OwnerModelHash", 0);
                if (entryOwnerHash != ownerHash)
                    continue;

                bool isLocked = ReadField<bool>(entry, "IsCollateralLocked", false);
                if (!isLocked)
                    continue;

                uint modelHash = ReadField<uint>(entry, "ModelHash", 0u);
                if (modelHash == 0u)
                    continue;

                int vehicleClass = GetVehicleClassFromModelHash(modelHash);
                if (!EligibleVehicleClasses.Contains(vehicleClass))
                    continue;

                string plate = ReadField<string>(entry, "Plate", string.Empty) ?? string.Empty;
                Vector3 pos = ReadField<Vector3>(entry, "Position", Vector3.Zero);
                float heading = ReadField<float>(entry, "Heading", 0f);
                Vehicle runtime = ReadField<Vehicle>(entry, "RuntimeVehicle", null);

                result.Add(new LockedVehicleInfo
                {
                    Source = entry,
                    OwnerHash = entryOwnerHash,
                    ModelHash = modelHash,
                    VehicleClass = vehicleClass,
                    Plate = plate,
                    Position = pos,
                    Heading = heading,
                    RuntimeVehicle = runtime,
                    ListIndex = i
                });
            }
        }
        catch
        {
        }

        return result;
    }

    private void RelocateBatchToCluster(List<LockedVehicleInfo> vehicles, ClusterDefinition cluster, int ownerHash, int clusterIndex, ClusterPoolKind pool)
    {
        try
        {
            if (vehicles == null || vehicles.Count == 0 || cluster == null || cluster.Slots == null || cluster.Slots.Length == 0)
                return;

            int count = Math.Min(vehicles.Count, cluster.Slots.Length);
            if (count <= 0)
                return;

            List<LockedVehicleInfo> vehicleOrder = vehicles.OrderBy(v => v.ListIndex).ToList();
            List<ClusterSlot> slotOrder = cluster.Slots.ToList();

            Shuffle(vehicleOrder);
            Shuffle(slotOrder);

            for (int i = 0; i < count; i++)
            {
                MoveVehicleToSlot(vehicleOrder[i], slotOrder[i]);
            }

            SaveCollateralClusterIndexForOwner(ownerHash, clusterIndex, pool);
            SavePersistentVehiclesNow();

            try
            {
                PersistentManager.RefreshVehicleBlipsForCurrentCharacter();
            }
            catch { }
        }
        catch { }
    }

    private void MoveVehicleToSlot(LockedVehicleInfo info, ClusterSlot slot)
    {
        try
        {
            if (info == null || slot == null)
                return;

            if (info.RuntimeVehicle != null && info.RuntimeVehicle.Exists())
            {
                Vehicle v = info.RuntimeVehicle;

                try
                {
                    Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET,
                        v.Handle,
                        slot.Position.X,
                        slot.Position.Y,
                        slot.Position.Z,
                        false, false, false);
                }
                catch
                {
                    try { v.Position = slot.Position; } catch { }
                }

                try
                {
                    Function.Call(Hash.SET_ENTITY_HEADING, v.Handle, slot.Heading);
                }
                catch
                {
                    try { v.Heading = slot.Heading; } catch { }
                }

                try { v.Velocity = Vector3.Zero; } catch { }
                try { v.Position = slot.Position; } catch { }
                try { v.Heading = slot.Heading; } catch { }

                try
                {
                    info.Position = slot.Position;
                    info.Heading = slot.Heading;
                    WriteField(info.Source, "Position", slot.Position);
                    WriteField(info.Source, "Heading", slot.Heading);
                    PersistentManager.UpdatePersistentFromVehicle(v);
                }
                catch
                {
                }

                return;
            }

            try { WriteField(info.Source, "Position", slot.Position); } catch { }
            try { WriteField(info.Source, "Heading", slot.Heading); } catch { }
        }
        catch
        {
        }
    }

    private bool TryGetExistingClusterIndex(List<LockedVehicleInfo> vehicles, List<ClusterDefinition> clusters, out int clusterIndex)
    {
        clusterIndex = -1;

        try
        {
            if (vehicles == null || vehicles.Count == 0)
                return false;

            for (int i = 0; i < clusters.Count; i++)
            {
                if (TryMatchVehiclesToCluster(vehicles, clusters[i]))
                {
                    clusterIndex = i;
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private bool TryMatchVehiclesToCluster(List<LockedVehicleInfo> vehicles, ClusterDefinition cluster)
    {
        try
        {
            if (vehicles == null || cluster == null || cluster.Slots == null)
                return false;

            if (vehicles.Count == 0 || vehicles.Count > cluster.Slots.Length)
                return false;

            bool[] usedSlots = new bool[cluster.Slots.Length];
            return MatchVehicleRecursive(vehicles, cluster, 0, usedSlots);
        }
        catch
        {
            return false;
        }
    }

    private bool MatchVehicleRecursive(List<LockedVehicleInfo> vehicles, ClusterDefinition cluster, int vehicleIndex, bool[] usedSlots)
    {
        if (vehicleIndex >= vehicles.Count)
            return true;

        LockedVehicleInfo v = vehicles[vehicleIndex];
        for (int slotIndex = 0; slotIndex < cluster.Slots.Length; slotIndex++)
        {
            if (usedSlots[slotIndex])
                continue;

            if (!IsSameSlot(v.Position, cluster.Slots[slotIndex].Position))
                continue;

            usedSlots[slotIndex] = true;
            if (MatchVehicleRecursive(vehicles, cluster, vehicleIndex + 1, usedSlots))
                return true;
            usedSlots[slotIndex] = false;
        }

        return false;
    }

    private bool IsSameSlot(Vector3 a, Vector3 b)
    {
        return Distance3D(a, b) <= CLUSTER_MATCH_TOLERANCE;
    }

    private static float Distance3D(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return (float)Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    private static int GetCurrentCharacterHash()
    {
        try
        {
            Ped p = Game.Player.Character;
            if (p == null || !p.Exists())
                return 0;

            int h = p.Model.Hash;
            if (h == FRANKLIN_HASH || h == MICHAEL_HASH || h == TREVOR_HASH)
                return h;
        }
        catch
        {
        }

        return 0;
    }

    private static int GetVehicleClassFromModelHash(uint modelHash)
    {
        try
        {
            return Function.Call<int>(Hash.GET_VEHICLE_CLASS_FROM_NAME, modelHash);
        }
        catch
        {
            return -1;
        }
    }

    private List<object> GetPersistentVehicleSnapshot()
    {
        try
        {
            Type pmType = typeof(PersistentManager);
            FieldInfo vehiclesField = pmType.GetField("_persistVehicles", AnyStatic);
            if (vehiclesField == null)
                return new List<object>();

            object listObj = vehiclesField.GetValue(null);
            if (listObj == null)
                return new List<object>();

            if (listObj is System.Collections.IEnumerable enumerable)
            {
                List<object> snapshot = new List<object>();

                try
                {
                    lock (listObj)
                    {
                        foreach (object item in enumerable)
                            snapshot.Add(item);
                    }
                }
                catch
                {
                    foreach (object item in enumerable)
                        snapshot.Add(item);
                }

                return snapshot;
            }
        }
        catch
        {
        }

        return new List<object>();
    }

    private bool UpdateCluster19Interaction(int ownerHash)
    {
        try
        {
            if (_cluster19TransitionBusy)
            {
                UpdateCluster19Transition();
                return true;
            }

            if (!IsCluster19DoorEnabledForOwner(ownerHash))
                return false;

            // Marker vàng ngoài cửa vẫn giữ nguyên
            DrawCluster19Marker();

            // Nếu đang ở bên trong thì thêm marker đỏ để ra ngoài
            if (_cluster19InsideInterior)
                DrawCluster19InteriorExitMarker();

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return true;

            bool playerInAnyVehicle = IsPedInAnyVehicle(player);

            if (_cluster19InsideInterior && playerInAnyVehicle)
            {
                Vehicle currentVehicle = player.CurrentVehicle;
                if (currentVehicle != null && currentVehicle.Exists() &&
                    Distance3D(currentVehicle.Position, CLUSTER19_DOOR_SLOT_POSITION) <= CLUSTER19_INTERACTION_RADIUS)
                {
                    GTA.UI.Screen.ShowHelpTextThisFrame(
                        L("HELP_EXIT_INTERIOR", "Nhấn ~INPUT_FRONTEND_ACCEPT~ để ra ngoài")
                    );

                    if (Game.IsControlJustPressed(Control.FrontendAccept))
                    {
                        BeginCluster19Transition(false);
                    }
                }

                return true;
            }

            if (_cluster19InsideInterior && !playerInAnyVehicle)
            {
                if (Distance3D(player.Position, CLUSTER19_INTERIOR_EXIT_MARKER_POSITION) <= CLUSTER19_INTERIOR_EXIT_RADIUS)
                {
                    GTA.UI.Screen.ShowHelpTextThisFrame(
                        L("HELP_EXIT_INTERIOR", "Nhấn ~INPUT_FRONTEND_ACCEPT~ để ra ngoài")
                    );

                    if (Game.IsControlJustPressed(Control.FrontendAccept))
                    {
                        BeginCluster19Transition(false);
                    }
                }

                return true;
            }

            // Chưa ở bên trong thì giữ logic vào trong như cũ
            if (!playerInAnyVehicle)
            {
                if (Distance3D(player.Position, CLUSTER19_MARKER_POSITION) <= CLUSTER19_INTERACTION_RADIUS)
                {
                    GTA.UI.Screen.ShowHelpTextThisFrame(
                        L("HELP_ENTER_WAREHOUSE", "Nhấn ~INPUT_FRONTEND_ACCEPT~ để vào trong kho")
                    );

                    if (Game.IsControlJustPressed(Control.FrontendAccept))
                    {
                        BeginCluster19Transition(true);
                    }
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool IsCluster19DoorEnabledForOwner(int ownerHash)
    {
        try
        {
            if (ownerHash == 0)
                return false;

            int savedClusterIndex = LoadCollateralClusterIndexForOwner(ownerHash, ClusterPoolKind.Aircraft);
            if (savedClusterIndex != CLUSTER19_AIRCRAFT_CLUSTER_INDEX)
                return false;

            return IsCluster19DoorSlotUsed(ownerHash);
        }
        catch
        {
            return false;
        }
    }

    private bool IsCluster19DoorSlotUsed(int ownerHash)
    {
        try
        {
            List<object> snapshot = GetPersistentVehicleSnapshot();
            if (snapshot == null || snapshot.Count == 0)
                return false;

            for (int i = 0; i < snapshot.Count; i++)
            {
                object entry = snapshot[i];
                if (entry == null)
                    continue;

                int entryOwnerHash = ReadField<int>(entry, "OwnerModelHash", 0);
                if (entryOwnerHash != ownerHash)
                    continue;

                bool isLocked = ReadField<bool>(entry, "IsCollateralLocked", false);
                if (!isLocked)
                    continue;

                Vector3 pos = ReadField<Vector3>(entry, "Position", Vector3.Zero);
                if (Distance3D(pos, CLUSTER19_DOOR_SLOT_POSITION) <= CLUSTER19_SLOT_MATCH_TOLERANCE)
                    return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private void DrawCluster19Marker()
    {
        try
        {
            // Marker vòng trụ màu vàng
            Function.Call(
                Hash.DRAW_MARKER,
                1,
                CLUSTER19_MARKER_POSITION.X,
                CLUSTER19_MARKER_POSITION.Y,
                CLUSTER19_MARKER_POSITION.Z - 0.95f,
                0f, 0f, 0f,
                0f, 0f, 0f,
                0.85f, 0.85f, 0.85f,
                255, 215, 0, 165,
                false,
                false,
                2,
                false,
                null,
                null,
                false
            );
        }
        catch
        {
        }
    }

    private void DrawCluster19InteriorExitMarker()
    {
        try
        {
            Function.Call(
                Hash.DRAW_MARKER,
                1,
                CLUSTER19_INTERIOR_EXIT_MARKER_POSITION.X,
                CLUSTER19_INTERIOR_EXIT_MARKER_POSITION.Y,
                CLUSTER19_INTERIOR_EXIT_MARKER_POSITION.Z - 0.95f,
                0f, 0f, 0f,
                0f, 0f, 0f,
                0.85f, 0.85f, 0.85f,
                255, 0, 0, 165,
                false,
                false,
                2,
                false,
                null,
                null,
                false
            );
        }
        catch
        {
        }
    }

    private void BeginCluster19Transition(bool toInterior)
    {
        try
        {
            if (_cluster19TransitionBusy)
                return;

            _cluster19TransitionBusy = true;
            _cluster19TransitionToInterior = toInterior;
            _cluster19TransitionStartGameTime = Game.GameTime;
            _cluster19TeleportDone = false;

            Function.Call(Hash.DO_SCREEN_FADE_OUT, CLUSTER19_FADE_MS);
        }
        catch
        {
            _cluster19TransitionBusy = false;
            _cluster19TeleportDone = false;
        }
    }

    private void UpdateCluster19Transition()
    {
        try
        {
            int elapsed = Game.GameTime - _cluster19TransitionStartGameTime;

            if (!_cluster19TeleportDone && elapsed >= CLUSTER19_FADE_MS)
            {
                if (_cluster19TransitionToInterior)
                {
                    TeleportPlayerToPosition(CLUSTER19_INTERIOR_SPAWN_POSITION);
                    _cluster19InsideInterior = true;
                }
                else
                {
                    TeleportPlayerOrVehicleToExitPosition();
                    _cluster19InsideInterior = false;
                }

                Function.Call(Hash.DO_SCREEN_FADE_IN, CLUSTER19_FADE_MS);
                _cluster19TeleportDone = true;
            }

            if (_cluster19TeleportDone && elapsed >= (CLUSTER19_FADE_MS * 2))
            {
                _cluster19TransitionBusy = false;
                _cluster19TeleportDone = false;
            }
        }
        catch
        {
            _cluster19TransitionBusy = false;
            _cluster19TeleportDone = false;
        }
    }

    private void TeleportPlayerToPosition(Vector3 pos)
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return;

            if (IsPedInAnyVehicle(player))
            {
                Vehicle v = player.CurrentVehicle;
                if (v != null && v.Exists())
                {
                    TeleportVehicleToGroundedPosition(v, pos, v.Heading);
                }
            }
            else
            {
                player.Position = pos;
                player.Velocity = Vector3.Zero;
            }
        }
        catch
        {
        }
    }

    private void TeleportPlayerOrVehicleToExitPosition()
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return;

            if (IsPedInAnyVehicle(player))
            {
                Vehicle v = player.CurrentVehicle;
                if (v != null && v.Exists())
                {
                    TeleportVehicleToGroundedPosition(v, CLUSTER19_EXIT_SPAWN_POSITION, CLUSTER19_EXIT_SPAWN_HEADING);
                }
            }
            else
            {
                player.Position = CLUSTER19_EXIT_PED_POSITION;
                player.Heading = CLUSTER19_EXIT_PED_HEADING;
                player.Velocity = Vector3.Zero;
            }
        }
        catch
        {
        }
    }

    private void TeleportVehicleToGroundedPosition(Vehicle v, Vector3 pos, float heading)
    {
        try
        {
            if (v == null || !v.Exists())
                return;

            try
            {
                Function.Call(Hash.REQUEST_COLLISION_AT_COORD, pos.X, pos.Y, pos.Z);
            }
            catch { }

            float groundZ = pos.Z;
            bool foundGround = false;

            try
            {
                OutputArgument groundArg = new OutputArgument();
                foundGround = Function.Call<bool>(
                    Hash.GET_GROUND_Z_FOR_3D_COORD,
                    pos.X,
                    pos.Y,
                    pos.Z + 1000.0f,
                    groundArg,
                    false
                );

                if (foundGround)
                    groundZ = groundArg.GetResult<float>();
            }
            catch
            {
                foundGround = false;
            }

            Vector3 finalPos = pos;
            if (foundGround)
                finalPos = new Vector3(pos.X, pos.Y, groundZ + 1.0f);

            try
            {
                Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET,
                    v.Handle,
                    finalPos.X,
                    finalPos.Y,
                    finalPos.Z,
                    false, false, false);
            }
            catch
            {
                try { v.Position = finalPos; } catch { }
            }

            try
            {
                Function.Call(Hash.SET_ENTITY_HEADING, v.Handle, heading);
            }
            catch
            {
                try { v.Heading = heading; } catch { }
            }

            try { v.Velocity = Vector3.Zero; } catch { }

            try
            {
                Function.Call(Hash.SET_ENTITY_ANGULAR_VELOCITY, v.Handle, 0f, 0f, 0f);
            }
            catch
            {
            }

            try
            {
                Function.Call(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, v.Handle);
            }
            catch
            {
            }

            try { v.Position = finalPos; } catch { }
            try { v.Heading = heading; } catch { }
            try { v.Velocity = Vector3.Zero; } catch { }
        }
        catch
        {
        }
    }

    private static bool IsPedInAnyVehicle(Ped ped)
    {
        try
        {
            if (ped == null || !ped.Exists())
                return false;

            return Function.Call<bool>(Hash.IS_PED_IN_ANY_VEHICLE, ped.Handle, false);
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

            return new string(plate.Trim().Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToUpperInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string BuildBatchSignature(int ownerHash, List<LockedVehicleInfo> vehicles)
    {
        try
        {
            StringBuilder sb = new StringBuilder(256);
            sb.Append(ownerHash.ToString(CultureInfo.InvariantCulture));
            sb.Append('|');
            sb.Append(vehicles.Count.ToString(CultureInfo.InvariantCulture));
            sb.Append('|');

            foreach (LockedVehicleInfo v in vehicles.OrderBy(x => x.ListIndex))
            {
                sb.Append(v.ListIndex.ToString(CultureInfo.InvariantCulture));
                sb.Append(':');
                sb.Append(v.ModelHash.ToString("X", CultureInfo.InvariantCulture));
                sb.Append(':');
                sb.Append(v.VehicleClass.ToString(CultureInfo.InvariantCulture));
                sb.Append(':');
                sb.Append(NormalizePlate(v.Plate));
                sb.Append('|');
            }

            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static T ReadField<T>(object obj, string fieldName, T fallback = default(T))
    {
        try
        {
            if (obj == null)
                return fallback;

            FieldInfo f = obj.GetType().GetField(fieldName, AnyInstance);
            if (f == null)
                return fallback;

            object value = f.GetValue(obj);
            if (value == null)
                return fallback;

            if (value is T typed)
                return typed;

            return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
        }
        catch
        {
            return fallback;
        }
    }

    private static void WriteField(object obj, string fieldName, object value)
    {
        try
        {
            if (obj == null)
                return;

            FieldInfo f = obj.GetType().GetField(fieldName, AnyInstance);
            if (f == null)
                return;

            f.SetValue(obj, value);
        }
        catch
        {
        }
    }

    private void SavePersistentVehiclesNow()
    {
        try
        {
            Type pmType = typeof(PersistentManager);

            FieldInfo dirtyField = pmType.GetField("_vehiclesDirty", AnyStatic);
            if (dirtyField != null)
                dirtyField.SetValue(null, true);

            MethodInfo saveMethod = pmType.GetMethod("SaveVehiclesFileInternal", AnyStatic);
            if (saveMethod != null)
                saveMethod.Invoke(null, null);
        }
        catch { }
    }

    private void Shuffle<T>(IList<T> list)
    {
        try
        {
            if (list == null || list.Count <= 1)
                return;

            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                T temp = list[i];
                list[i] = list[j];
                list[j] = temp;
            }
        }
        catch
        {
        }
    }
}