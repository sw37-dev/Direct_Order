using GTA;
using GTA.Native;
using GTA.UI;
using GTA.Math;
using System;
using System.Collections.Generic;
using System.Drawing;
using LemonUI;
using LemonUI.Menus;

public partial class InstantRefill : Script
{
    private int AmmoToAddGun, ExplosiveToAdd;
    private int SingleAmmoAmountGun, SingleExplosiveAmount;
    private int RandomMinGun, RandomMaxGun, RandomMinExplosive, RandomMaxExplosive;

    private readonly Queue<uint> _weaponHashesToProcess = new Queue<uint>();
    private bool _isProcessingAmmo = false;

    // ===================== LEMONUI: WEAPON DETAIL MENU =====================
    private NativeMenu _luiWeaponDetailMenu;
    private NativeStatsPanel _luiWeaponStatsPanel;
    private NativeStatsInfo _luiWeaponStatDamage;
    private NativeStatsInfo _luiWeaponStatFireRate;
    private NativeStatsInfo _luiWeaponStatAccuracy;
    private NativeStatsInfo _luiWeaponStatRange;

    private WeaponEntry _luiWeaponDetailEntry = null;
    private int _luiWeaponDetailPrice = 0;
    private int _luiWeaponDetailAmmo = 0;
    private bool _luiWeaponDetailHasAccessory = false;
    private int _luiWeaponDetailAccessoryCost = 0;

    internal class WeaponEntry
    {
        public string Name;
        public uint Hash;
        public bool Unlocked;
        public int TimesPurchased;

        public int PriceMin;
        public int PriceMax;
        public int FirstAmmoMin;
        public int FirstAmmoMax;

        public int RepeatAmmo;
        public int RepeatAmmoMin = -1;
        public int RepeatAmmoMax = -1;

        public bool IsExplosiveWeapon;
        public bool PlayerHasWeapon = false;
        public bool PlayerHasAccessory = false;

        public WeaponEntry()
        {
            Unlocked = false;
            TimesPurchased = 0;
            PriceMin = 45000;
            PriceMax = 70000;
            FirstAmmoMin = 20;
            FirstAmmoMax = 30;
            RepeatAmmo = 5;
            IsExplosiveWeapon = false;
            RepeatAmmoMin = -1;
            RepeatAmmoMax = -1;
            PlayerHasWeapon = false;
            PlayerHasAccessory = false;
        }

        public int GetRandomPrice(Random rng)
        {
            if (PriceMax < PriceMin) return PriceMin;
            return rng.Next(PriceMin, PriceMax + 1);
        }

        public int GetFirstAmmo(Random rng)
        {
            if (FirstAmmoMax < FirstAmmoMin) return FirstAmmoMin;
            if (FirstAmmoMin == FirstAmmoMax) return FirstAmmoMin;
            return rng.Next(FirstAmmoMin, FirstAmmoMax + 1);
        }

        public int GetRepeatAmmo(Random rng)
        {
            if (RepeatAmmoMin >= 0 && RepeatAmmoMax >= RepeatAmmoMin)
                return rng.Next(RepeatAmmoMin, RepeatAmmoMax + 1);
            return RepeatAmmo;
        }
    }

    private readonly List<WeaponEntry> _specialWeapons = new List<WeaponEntry>();
    private readonly Dictionary<uint, uint[]> _weaponAttachments = new Dictionary<uint, uint[]>();

    // -------------------------- Initialize weapons (unchanged but complete) --------------------------
    private void InitializeSpecialWeaponList()
    {
        _specialWeapons.Clear();
        _weaponAttachments.Clear();

        // 1) Unholy Hellbringer
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_PlasmaGun", "Súng Máy Plasma"),
            Hash = 0x476BF155u, // 1198256469
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 150000,
            PriceMax = 225000,
            FirstAmmoMin = 1750,
            FirstAmmoMax = 3000,
            RepeatAmmoMin = 750,
            RepeatAmmoMax = 1500,
            IsExplosiveWeapon = false
        });

        // 2) Combat MG Mk2
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_CombatMGMk2", "Súng Máy Mk2"),
            Hash = 0xDBBD7280u, // 3686625920
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = WeaponOfferDefaultMinPrice,
            PriceMax = WeaponOfferDefaultMaxPrice,
            FirstAmmoMin = 300,
            FirstAmmoMax = 420,
            RepeatAmmoMin = 150,
            RepeatAmmoMax = 210,
            IsExplosiveWeapon = false
        });

        // 3) Special Carbine Mk2
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_SpecialCarbineMk2", "Súng Trường Mk2"),
            Hash = 0x969C3D67u, // 2526821735
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = WeaponOfferDefaultMinPrice,
            PriceMax = WeaponOfferDefaultMaxPrice,
            FirstAmmoMin = WeaponOfferFirstAmmoMin,
            FirstAmmoMax = WeaponOfferFirstAmmoMax,
            RepeatAmmoMin = 100,
            RepeatAmmoMax = 150,
            IsExplosiveWeapon = false
        });

        // 4) Heavy Sniper Mk2
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_HeavySniperMk2", "Súng Tỉa Hạng Nặng"),
            Hash = 0x0A914799u, // 177293209 (0x0A914799)
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 125000,
            PriceMax = 175000,
            FirstAmmoMin = 50,
            FirstAmmoMax = 80,
            RepeatAmmoMin = 20,
            RepeatAmmoMax = 30,
            IsExplosiveWeapon = false
        });

        // 5) Musket
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_Musket", "Súng Trường Cổ"),
            Hash = 0xA89CB99Eu, // 2828843422
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = WeaponOfferDefaultMinPrice,
            PriceMax = WeaponOfferDefaultMaxPrice,
            FirstAmmoMin = WeaponOfferFirstAmmoMin,
            FirstAmmoMax = WeaponOfferFirstAmmoMax,
            RepeatAmmoMin = 75,
            RepeatAmmoMax = 100,
            IsExplosiveWeapon = false
        });

        // 6) Compact Grenade Launcher
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_CompactGrenadeLauncher", "Phóng Lựu Cầm Tay"),
            Hash = 0x0781FE4Au, // 125959754 (0x0781FE4A)
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = WeaponOfferDefaultMinPrice,
            PriceMax = WeaponOfferDefaultMaxPrice,
            FirstAmmoMin = WeaponOfferFirstAmmoMin,
            FirstAmmoMax = WeaponOfferFirstAmmoMax,
            RepeatAmmoMin = 75,
            RepeatAmmoMax = 100,
            IsExplosiveWeapon = true
        });

        // 7) Minigun
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_Minigun", "Súng 6 Nòng"),
            Hash = 0x42BF8A85u, // 1119849093
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 125000,
            PriceMax = 150000,
            FirstAmmoMin = 2000,
            FirstAmmoMax = 3000,
            RepeatAmmoMin = 1500,
            RepeatAmmoMax = 1500,
            IsExplosiveWeapon = false
        });

        // --- Additional entries provided in original code (kept as-is) ---
        // 8) Stun Gun Multiplayer
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_StunGun", "Súng Điện"),
            Hash = 0x45CD9CF3u, // 1171102963
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 50000,
            PriceMax = 100000,
            FirstAmmoMin = 0,
            FirstAmmoMax = 0,
            RepeatAmmoMin = 0,
            RepeatAmmoMax = 0,
            IsExplosiveWeapon = false
        });

        // 9) Revolver Mk 2
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_RevolverMk2", "Súng Lục Ổ Quay"),
            Hash = 0xCB96392Fu, // 3415619887
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = WeaponOfferDefaultMinPrice,
            PriceMax = WeaponOfferDefaultMaxPrice,
            FirstAmmoMin = WeaponOfferFirstAmmoMin,
            FirstAmmoMax = WeaponOfferFirstAmmoMax,
            RepeatAmmoMin = 75,
            RepeatAmmoMax = 100,
            IsExplosiveWeapon = false
        });

        // 10) Unknown (DAC00025)
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_GoldPistol", "Súng Lục Vàng"),
            Hash = 0xDAC00025u, // 3670016037
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = WeaponOfferDefaultMinPrice,
            PriceMax = WeaponOfferDefaultMaxPrice,
            FirstAmmoMin = WeaponOfferFirstAmmoMin,
            FirstAmmoMax = WeaponOfferFirstAmmoMax,
            RepeatAmmoMin = 75,
            RepeatAmmoMax = 100,
            IsExplosiveWeapon = false
        });

        // 11) Pool Cue
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_PoolCue", "Gậy Bida"),
            Hash = 0x94117305u, // 2484171525
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 1000,
            PriceMax = 2000,
            FirstAmmoMin = 0,
            FirstAmmoMax = 0,
            RepeatAmmoMin = 0,
            RepeatAmmoMax = 0,
            IsExplosiveWeapon = false
        });

        // 12) Sawn Off Shotgun
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_SawnoffShotgun", "Shotgun Cưa Nòng"),
            Hash = 0x7846A318u, // 2017895192
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = WeaponOfferDefaultMinPrice,
            PriceMax = WeaponOfferDefaultMaxPrice,
            FirstAmmoMin = WeaponOfferFirstAmmoMin,
            FirstAmmoMax = WeaponOfferFirstAmmoMax,
            RepeatAmmoMin = 75,
            RepeatAmmoMax = 100,
            IsExplosiveWeapon = false
        });

        // 13) Widowmaker
        _specialWeapons.Add(new WeaponEntry
        {
            Name = "Widowmaker",
            Hash = 0xB62D1F67u, // 3056410471
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 200000,
            PriceMax = 300000,
            FirstAmmoMin = 1000,
            FirstAmmoMax = 2000,
            RepeatAmmoMin = 1000,
            RepeatAmmoMax = 1000,
            IsExplosiveWeapon = false
        });

        // 14) Snowball Launcher
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_SnowballLauncher", "Súng Bắn Tuyết"),
            Hash = 0x03BF5575u, // 62870901 (0x03BF5575)
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 1000,
            PriceMax = 2000,
            FirstAmmoMin = 15,
            FirstAmmoMax = 20,
            RepeatAmmoMin = 15,
            RepeatAmmoMax = 15,
            IsExplosiveWeapon = false
        });

        // 15) Compact EMP Launcher
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_CompactEMPLauncher", "Súng Phóng Điện EMP"),
            Hash = 0xDB26713Au, // 3676729658
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 115000,
            PriceMax = 140000,
            FirstAmmoMin = 15,
            FirstAmmoMax = 20,
            RepeatAmmoMin = 10,
            RepeatAmmoMax = 15,
            IsExplosiveWeapon = true
        });

        // 16) Pipe Bomb
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_PipeBomb", "Bom Ống"),
            Hash = 0xBA45E8B8u, // 3125143736
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = WeaponOfferDefaultMinPrice,
            PriceMax = WeaponOfferDefaultMaxPrice,
            FirstAmmoMin = 5,
            FirstAmmoMax = 10,
            RepeatAmmoMin = 5,
            RepeatAmmoMax = 5,
            IsExplosiveWeapon = true
        });

        // 17) Flare
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_Flare", "Pháo Sáng"),
            Hash = 0x497FACC3u, // 1233104067
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 500,
            PriceMax = 500,
            FirstAmmoMin = 10,
            FirstAmmoMax = 25,
            RepeatAmmoMin = 10,
            RepeatAmmoMax = 10,
            IsExplosiveWeapon = false
        });

        // 18) Fire Extinguisher
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_FireExtinguisher", "Bình Cứu Hỏa"),
            Hash = 0x060EC506u, // 101631238 (0x060EC506)
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 1000,
            PriceMax = 2000,
            FirstAmmoMin = 100,
            FirstAmmoMax = 100,
            RepeatAmmoMin = 100,
            RepeatAmmoMax = 100,
            IsExplosiveWeapon = false
        });

        // --- New additions with accessories (from scan) not present before ---
        // Up-n-Atomizer
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_UpNAtomizer", "Súng Sóng Âm"),
            Hash = 0xAF3696A1u, // 2138347493
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 135000,
            PriceMax = 160000,
            FirstAmmoMin = 20,
            FirstAmmoMax = 40,
            RepeatAmmoMin = 10,
            RepeatAmmoMax = 20,
            IsExplosiveWeapon = true
        });
        _weaponAttachments[0xAF3696A1u] = new uint[]
        {
        0xD7DBF707u // UpNatoizerVarmodXmas18
        };


        // Firework Launcher
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_FireworkLauncher", "Súng Pháo Hoa"),
            Hash = 0x7F7497E5u, // 2138347493
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 30000,
            PriceMax = 50000,
            FirstAmmoMin = 20,
            FirstAmmoMax = 40,
            RepeatAmmoMin = 10,
            RepeatAmmoMax = 20,
            IsExplosiveWeapon = true
        });
        _weaponAttachments[0x7F7497E5u] = new uint[]
        {
        0xE4E4C28Du // component from scan
        };

        // Grenade Launcher (Súng phóng lựu)
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_FireworkLauncher", "Súng Pháo Hoa"),
            Hash = 0xA284510Bu, //  -1568386805
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 75000,
            PriceMax = 100000,
            FirstAmmoMin = 8,
            FirstAmmoMax = 10,
            RepeatAmmoMin = 5,
            RepeatAmmoMax = 8,
            IsExplosiveWeapon = true
        });
        _weaponAttachments[0xA284510Bu] = new uint[]
        {
        0x11AE5C97u,
        0x7BC4CDDCu,
        0x0C164F53u, // grip hex as in scan (kept)
        0xAA2C45B4u
        };

        // Pump Shotgun Mk II already present (kept). Add other shotguns from scan:

        // Heavy Shotgun
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_HeavyShotgun", "Shotgun"),
            Hash = 0x3AABBBAAu, // 984333226
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 90000,
            PriceMax = 100000,
            FirstAmmoMin = 30,
            FirstAmmoMax = 40,
            RepeatAmmoMin = 15,
            RepeatAmmoMax = 25,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0x3AABBBAAu] = new uint[]
        {
        0x324F2D5Fu,
        0x971CF6FDu,
        0x88C7DA53u,
        0x7BC4CDDCu,
        0xA73D4664u,
        0x0C164F53u
        };

        // Double Barrel Shotgun
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_DoubleBarrelShotgun", "Shotgun Hai Nòng"),
            Hash = 0xEF951FBBu, // -275439685
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 60000,
            PriceMax = 80000,
            FirstAmmoMin = 8,
            FirstAmmoMax = 12,
            RepeatAmmoMin = 6,
            RepeatAmmoMax = 8,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0xEF951FBBu] = new uint[]
        {
        0x29EA741Eu
        };

        // Sweeper Shotgun
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_SweeperShotgun", "Sweeper Shotgun"),
            Hash = 0x12E82D3Du, // 317205821
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 50000,
            PriceMax = 75000,
            FirstAmmoMin = 12,
            FirstAmmoMax = 20,
            RepeatAmmoMin = 6,
            RepeatAmmoMax = 10,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0x12E82D3Du] = new uint[]
        {
        0x0A19D08Eu // 0xA19D08E from scan (leading zero handled)
        };

        // Combat Shotgun
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_CombatShotgun", "Shotgun (CS)"),
            Hash = 0x05A96BA4u, // 94989220 (0x05A96BA4)
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 50000,
            PriceMax = 60000,
            FirstAmmoMin = 50,
            FirstAmmoMax = 80,
            RepeatAmmoMin = 30,
            RepeatAmmoMax = 40,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0x05A96BA4u] = new uint[]
        {
        0xC6153655u,
        0x7BC4CDDCu,
        0xA73D4664u
        };

        // Marksman Rifle (non-Mk2)
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_MarksmanRifle", "Súng Bắn Tỉa"),
            Hash = 0xC734385Au, // -952879014
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = WeaponOfferDefaultMinPrice,
            PriceMax = WeaponOfferDefaultMaxPrice,
            FirstAmmoMin = 30,
            FirstAmmoMax = 60,
            RepeatAmmoMin = 10,
            RepeatAmmoMax = 20,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0xC734385Au] = new uint[]
        {
        0xD83B4141u,
        0xCCFD2AC5u,
        0x7BC4CDDCu,
        0x1C221B1Au,
        0x837445AAu,
        0x0C164F53u,
        0x161E9241u,
        0x60BD749Cu
        };

        // Precision Rifle
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_PrecisionRifle", "Súng Bắn Tỉa (PR)"),
            Hash = 0x6E7DDDECu, // 1853742572
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 80000,
            PriceMax = 100000,
            FirstAmmoMin = 25,
            FirstAmmoMax = 50,
            RepeatAmmoMin = 10,
            RepeatAmmoMax = 20,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0x6E7DDDECu] = new uint[]
        {
        0xF2EACF0Au
        };

        // Special Carbine (non-Mk2)
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_SpecialCarbine", "Carbine"),
            Hash = 0xC0A3098Du, // -1063057011
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = WeaponOfferDefaultMinPrice,
            PriceMax = WeaponOfferDefaultMaxPrice,
            FirstAmmoMin = WeaponOfferFirstAmmoMin,
            FirstAmmoMax = WeaponOfferFirstAmmoMax,
            RepeatAmmoMin = 100,
            RepeatAmmoMax = 150,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0xC0A3098Du] = new uint[]
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
        };

        // Bullpup Rifle (non-Mk2)
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_BullpupRifle", "Súng Bullpup"),
            Hash = 0x7F229F94u, // 2132975508
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = WeaponOfferDefaultMinPrice,
            PriceMax = WeaponOfferDefaultMaxPrice,
            FirstAmmoMin = WeaponOfferFirstAmmoMin,
            FirstAmmoMax = WeaponOfferFirstAmmoMax,
            RepeatAmmoMin = 75,
            RepeatAmmoMax = 120,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0x7F229F94u] = new uint[]
        {
        0xC5A12F80u,
        0xB3688B0Fu,
        0x7BC4CDDCu,
        0xAA2C45B4u,
        0x837445AAu,
        0x0C164F53u,
        0xA857BC78u,
        0x60BD749Cu
        };

        // Compact Rifle
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_CompactRifle", "Carbine Nhỏ"),
            Hash = 0x624FE830u, // 1649403952
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 45000,
            PriceMax = 60000,
            FirstAmmoMin = 120,
            FirstAmmoMax = 180,
            RepeatAmmoMin = 60,
            RepeatAmmoMax = 90,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0x624FE830u] = new uint[]
        {
        0x513F0A63u,
        0x59FF9BF8u,
        0xC607740Eu
        };

        // Military Rifle
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_MilitaryRifle", "Súng Trường Quân Sự"),
            Hash = 0x9D1F17E6u, // -1658906650
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = WeaponOfferDefaultMinPrice,
            PriceMax = WeaponOfferDefaultMaxPrice,
            FirstAmmoMin = WeaponOfferFirstAmmoMin,
            FirstAmmoMax = WeaponOfferFirstAmmoMax,
            RepeatAmmoMin = 80,
            RepeatAmmoMax = 120,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0x9D1F17E6u] = new uint[]
        {
        0x2D46D83Bu,
        0x684ACE42u,
        0x7BC4CDDCu,
        0x6B82F395u,
        0xAA2C45B4u,
        0x837445AAu
        };

        // Heavy Rifle
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_HeavyRifle", "Súng Trường Cao Cấp"),
            Hash = 0xC78D71B4u, // -947031628
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 60000,
            PriceMax = 80000,
            FirstAmmoMin = 240,
            FirstAmmoMax = 300,
            RepeatAmmoMin = 100,
            RepeatAmmoMax = 150,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0xC78D71B4u] = new uint[]
        {
        0x5AF49386u,
        0x6CBF371Bu,
        0xB3E1C452u,
        0xA0D89C42u,
        0x7BC4CDDCu,
        0x837445AAu,
        0x0C164F53u,
        0xEC9FECD9u
        };

        // Service Carbine
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_ServiceCarbine", "Service Carbine"),
            Hash = 0xD1D5F52Bu, // -774507221
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = WeaponOfferDefaultMinPrice,
            PriceMax = WeaponOfferDefaultMaxPrice,
            FirstAmmoMin = WeaponOfferFirstAmmoMin,
            FirstAmmoMax = WeaponOfferFirstAmmoMax,
            RepeatAmmoMin = 60,
            RepeatAmmoMax = 100,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0xD1D5F52Bu] = new uint[]
        {
        0x3749B8BBu,
        0x8594554Fu,
        0x9DB1E023u,
        0xA73D4664u,
        0x0C164F53u
        };

        // Gusenberg Sweeper
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_GusenbergSweeper", "Súng Gusenberg"),
            Hash = 0x61012683u, // 1627465347
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 40000,
            PriceMax = 65000,
            FirstAmmoMin = 150,
            FirstAmmoMax = 220,
            RepeatAmmoMin = 80,
            RepeatAmmoMax = 120,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0x61012683u] = new uint[]
        {
        0x1CE5A6A5u,
        0xEAC8C270u
        };

        // Combat MG (non-Mk2)
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_CombatMG", "Súng Máy"),
            Hash = 0x7FD62962u, // 2144741730
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = WeaponOfferDefaultMinPrice,
            PriceMax = WeaponOfferDefaultMaxPrice,
            FirstAmmoMin = 300,
            FirstAmmoMax = 500,
            RepeatAmmoMin = 150,
            RepeatAmmoMax = 250,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0x7FD62962u] = new uint[]
        {
        0xE1FFB34Au,
        0xD6C59CD6u,
        0xA0D89C42u,
        0x0C164F53u,
        0x92FECCDDu,
        0x60BD749Cu
        };

        // Combat PDW
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_CombatPDW", "Súng PDW"),
            Hash = 0x0A3D4D34u, // 171789620 (0x0A3D4D34)
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 45000,
            PriceMax = 55000,
            FirstAmmoMin = 120,
            FirstAmmoMax = 200,
            RepeatAmmoMin = 60,
            RepeatAmmoMax = 100,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0x0A3D4D34u] = new uint[]
        {
        0x4317F19Eu,
        0x334A5203u,
        0x6EB8C8DBu,
        0x7BC4CDDCu,
        0x0C164F53u,
        0xAA2C45B4u
        };

        // SMG / Mini SMG / Machine Pistol
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_MiniSMG", "Mini SMG"),
            Hash = 0xBD248B55u, // -1121678507
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 48000,
            PriceMax = 70000,
            FirstAmmoMin = 80,
            FirstAmmoMax = 140,
            RepeatAmmoMin = 40,
            RepeatAmmoMax = 80,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0xBD248B55u] = new uint[]
        {
        0x84C8B2D3u,
        0x937ED0B7u
        };

        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_MachinePistol", "Súng Lục Máy"),
            Hash = 0xDB1AA450u, // -619010992
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 40000,
            PriceMax = 58000,
            FirstAmmoMin = 80,
            FirstAmmoMax = 160,
            RepeatAmmoMin = 40,
            RepeatAmmoMax = 80,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0xDB1AA450u] = new uint[]
        {
        0x476E85FFu,
        0xB92C6979u,
        0xA9E9CAF4u,
        0xC304849Au
        };

        // AP Pistol
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_APPistol", "Súng Lục AP"),
            Hash = 0x22D8FE39u, // 584646201
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 45000,
            PriceMax = 60000,
            FirstAmmoMin = 60,
            FirstAmmoMax = 120,
            RepeatAmmoMin = 30,
            RepeatAmmoMax = 60,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0x22D8FE39u] = new uint[]
        {
        0x31C4B22Au,
        0x249A17D5u,
        0x359B7AAEu,
        0xC304849Au,
        0x9B76C72Cu,
        0x62CF4F46u
        };

        // WM 29 Pistol
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_WM29Pistol", "Súng WM29"),
            Hash = 0x1BC4FDB9u, // 465894841
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 50000,
            PriceMax = 75000,
            FirstAmmoMin = 40,
            FirstAmmoMax = 100,
            RepeatAmmoMin = 30,
            RepeatAmmoMax = 60,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0x1BC4FDB9u] = new uint[]
        {
        0x1663E75Eu,
        0x1E02B7E0u
        };

        // Perico Pistol
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_PericoPistol", "Súng Perico"),
            Hash = 0x57A4368Cu, // 1470379660
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 50000,
            PriceMax = 60000,
            FirstAmmoMin = 30,
            FirstAmmoMax = 80,
            RepeatAmmoMin = 20,
            RepeatAmmoMax = 40,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0x57A4368Cu] = new uint[]
        {
        0xAB276E49u
        };

        // Navy Revolver
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_NavyRevolver", "Súng Lục Hải Quân"),
            Hash = 0x917F6C8Cu, // -1853920116
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 70000,
            PriceMax = 100000,
            FirstAmmoMin = 30,
            FirstAmmoMax = 70,
            RepeatAmmoMin = 20,
            RepeatAmmoMax = 40,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0x917F6C8Cu] = new uint[]
        {
        0x985EC267u
        };

        // Double-Action Revolver
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_DoubleActionRevolver", "Súng Lục Hai Cơ"),
            Hash = 0x97EA20B8u, // -1746263880
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 70000,
            PriceMax = 90000,
            FirstAmmoMin = 30,
            FirstAmmoMax = 70,
            RepeatAmmoMin = 20,
            RepeatAmmoMax = 40,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0x97EA20B8u] = new uint[]
        {
        0x4F312CC1u
        };

        // Heavy Revolver (non-Mk2)
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_HeavyRevolver", "Súng Lục Cao Cấp"),
            Hash = 0xC1B3C3D1u, // -1045183535
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 75000,
            PriceMax = 100000,
            FirstAmmoMin = 200,
            FirstAmmoMax = 240,
            RepeatAmmoMin = 50,
            RepeatAmmoMax = 100,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0xC1B3C3D1u] = new uint[]
        {
        0xE9867CE3u,
        0x16EE3040u,
        0x9493B80Du,
        0x60BD749Cu
        };

        // Marksman Pistol
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_MarksmanPistol", "Súng Lục Bắn Tỉa"),
            Hash = 0xDC4DB296u, // -598887786
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 40000,
            PriceMax = 65000,
            FirstAmmoMin = 20,
            FirstAmmoMax = 50,
            RepeatAmmoMin = 10,
            RepeatAmmoMax = 25,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0xDC4DB296u] = new uint[]
        {
        0xCB9E41EDu
        };

        // Ceramic Pistol
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_CeramicPistol", "Súng Lục Gốm"),
            Hash = 0x2B5EF5ECu, // 727643628
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 45000,
            PriceMax = 60000,
            FirstAmmoMin = 30,
            FirstAmmoMax = 60,
            RepeatAmmoMin = 15,
            RepeatAmmoMax = 30,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0x2B5EF5ECu] = new uint[]
        {
        0x54D41361u,
        0x81786CA9u,
        0x9307D6FAu
        };

        // SNS Pistol Mk II
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_SNSPistolMk2", "SNS Mk 2"),
            Hash = 0x88374054u, // -2009644972
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = WeaponOfferDefaultMinPrice,
            PriceMax = WeaponOfferDefaultMaxPrice,
            FirstAmmoMin = WeaponOfferFirstAmmoMin,
            FirstAmmoMax = WeaponOfferFirstAmmoMax,
            RepeatAmmoMin = 40,
            RepeatAmmoMax = 80,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0x88374054u] = new uint[]
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
        };

        // Pistol Mk II
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_PistolMk2", "Súng Lục Mk 2"),
            Hash = 0xBFE256D4u, // -1075685676
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = WeaponOfferDefaultMinPrice,
            PriceMax = WeaponOfferDefaultMaxPrice,
            FirstAmmoMin = WeaponOfferFirstAmmoMin,
            FirstAmmoMax = WeaponOfferFirstAmmoMax,
            RepeatAmmoMin = 25,
            RepeatAmmoMax = 60,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0xBFE256D4u] = new uint[]
        {
        0x94F42D62u,
        0x5ED6C128u,
        0x4F37DF2Au,
        0x85FEA109u,
        0x2BBD7A3Au,
        0x25CAAEAFu,
        0x8ED4BB70u,
        0x43FD595Bu,
        0x65EA7EBBu,
        0x21E34793u
        };

        // Melee / misc: Switchblade, Flashlight, Baseball Bat, Knife, etc.
        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_Switchblade", "Dao Gập"),
            Hash = 0xDFE37640u, // -538741184
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 1000,
            PriceMax = 2000,
            FirstAmmoMin = 0,
            FirstAmmoMax = 0,
            RepeatAmmoMin = 0,
            RepeatAmmoMax = 0,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0xDFE37640u] = new uint[]
        {
        0x9137A500u,
        0x5B3E7DB6u,
        0xE7939662u
        };

        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_Flashlight", "Đèn Pin"),
            Hash = 0x8BB05FD7u, // -1951375401
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 1000,
            PriceMax = 2000,
            FirstAmmoMin = 0,
            FirstAmmoMax = 0,
            RepeatAmmoMin = 0,
            RepeatAmmoMax = 0,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0x8BB05FD7u] = new uint[]
        {
        0xDDB7390Fu
        };

        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_BaseballBat", "Gậy Bóng Chày"),
            Hash = 0x958A4A8Fu, // -1786099057
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 1000,
            PriceMax = 2000,
            FirstAmmoMin = 0,
            FirstAmmoMax = 0,
            RepeatAmmoMin = 0,
            RepeatAmmoMax = 0,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0x958A4A8Fu] = new uint[]
        {
        0x2AB07663u,
        0x1A998E71u,
        0x3E52D5E3u,
        0x4FA5F889u,
        0xE1669C0Cu,
        0xE550A3E0u,
        0xF71B4775u,
        0x09F0ED20u,
        0x7B4E4FBDu,
        0x68FCAB1Au
        };

        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_Knife", "Dao"),
            Hash = 0x99B507EAu, // -1716189206
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 1000,
            PriceMax = 2000,
            FirstAmmoMin = 0,
            FirstAmmoMax = 0,
            RepeatAmmoMin = 0,
            RepeatAmmoMax = 0,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0x99B507EAu] = new uint[]
        {
        0x1615CCD3u,
        0x4227D6ADu,
        0x7A83C764u,
        0x64C49BE6u,
        0x9EA58FABu,
        0xA8E3A427u,
        0x96797F47u,
        0x853ADCCAu,
        0xB3B4B9C1u,
        0xC8FDE453u
        };

        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_Machete", "Rìu Machete"),
            Hash = 0xDD5DF8D9u, // -581044007
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 1000,
            PriceMax = 2000,
            FirstAmmoMin = 0,
            FirstAmmoMax = 0,
            RepeatAmmoMin = 0,
            RepeatAmmoMax = 0,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0xDD5DF8D9u] = new uint[] { };

        _specialWeapons.Add(new WeaponEntry
        {
            Name = L("WeaponName_Hammer", "Búa"),
            Hash = 0x4E875F73u, // 1317494643
            Unlocked = false,
            TimesPurchased = 0,
            PriceMin = 1000,
            PriceMax = 2000,
            FirstAmmoMin = 0,
            FirstAmmoMax = 0,
            RepeatAmmoMin = 0,
            RepeatAmmoMax = 0,
            IsExplosiveWeapon = false
        });
        _weaponAttachments[0x4E875F73u] = new uint[] { };

        // --- ensure attachment entries for legacy simple weapons exist ---
        _weaponAttachments[0x969C3D67u] = new uint[]
        {
        0x503DEA90u, // SpecialCarbineMk2ClipFMJ
        0x7BC4CDDCu, // AtArFish
        0xC6686542u, // AtScopeMediumMk2 (note: keep consistent)
        0x4DB62ABEu, // AtMuzzle07
        0x9D65907Au, // AtArAfGrip02
        0xF97F783Bu, // AtScBarrel02
        0x5218C819u  // SpecialCarbineMk2CamoIndependence01
        };

        _weaponAttachments[0xDBBD7280u] = new uint[]
        {
        0x57EF1CC8u, // CombatMGMk2ClipFMJ
        0xC66B6542u, // AtScopeMediumMk2 (variant)
        0x4DB62ABEu, // AtMuzzle07
        0x9D65907Au, // AtArAfGrip02
        0xB5E2575Bu, // AtMGBarrel02
        0xD703C94Du  // CombatMGMk2CamoIndependence01
        };

        _weaponAttachments[0x0A914799u] = new uint[]
        {
        0x3BE948F6u, // HeavySniperMk2ClipFMJ
        0x2E43DA41u, // AtScope Thermal
        0x6927E1A1u, // AtMuzzle09
        0x108AB09Eu, // AtSrBarrel02
        0x6C32D2EBu  // HeavySniperMk2CamoIndependence01
        };

        _weaponAttachments[0xA89CB99Eu] = new uint[]
        {
        0x4ED2073Fu // MusketClip01
        };

        _weaponAttachments[0x7846A318u] = new uint[]
        {
        0xC7D62225u, // SawnoffShotgunClip01
        0x85A64DF9u  // SawnoffShotgunVarmodLuxe
        };

        _weaponAttachments[0xB62D1F67u] = new uint[]
        {
        0xC8DE6F06u // MinigunClip01
        };

        _weaponAttachments[0x0781FE4Au] = new uint[]
        {
        0x49A3CF0Cu // CompactGrenadeLauncherClip01
        };

        _weaponAttachments[0x42BF8A85u] = new uint[]
        {
        0xC8DE6F06u // MinigunClip01 (also used)
        };

        _weaponAttachments[0xDB26713Au] = new uint[]
        {
        0xD28F58F1u // CompactEMPLauncherClip01
        };

        _weaponAttachments[0xBA45E8B8u] = new uint[] { };

        // ensure keys for weapons with no attachments exist to simplify lookups
        if (!_weaponAttachments.ContainsKey(0x497FACC3u)) _weaponAttachments[0x497FACC3u] = new uint[] { };
        if (!_weaponAttachments.ContainsKey(0x060EC506u)) _weaponAttachments[0x060EC506u] = new uint[] { };
        if (!_weaponAttachments.ContainsKey(0x03BF5575u)) _weaponAttachments[0x03BF5575u] = new uint[] { };
        if (!_weaponAttachments.ContainsKey(0x94117305u)) _weaponAttachments[0x94117305u] = new uint[] { };

        // finalize
        UpdateSpecialWeaponUnlocks();
        UpdateAccessoryOwnership();
    }

    private void HandleSingleAmmoInitiate(Ped ped)
    {
        if (!PrepareSingleAmmoOffer(ped, _ammoSingleOffer))
        {
            GTA.UI.Screen.ShowSubtitle(
                "~HUD_COLOUR_DEGEN_RED~Không có vũ khí đang cầm để nạp đạn.",
                3000);
            return;
        }

        OpenWeaponAmmoDetailMenu(_ammoSingleOffer);
    }

    private void HandleAllAmmoInitiate(Ped ped)
    {
        if (!PrepareAllAmmoOffer(ped, _ammoAllOffer))
        {
            GTA.UI.Screen.ShowSubtitle(
                "~HUD_COLOUR_DEGEN_RED~Không có loại vũ khí nào để nạp.",
                3000);
            return;
        }

        OpenWeaponAmmoDetailMenu(_ammoAllOffer);
    }

    private void InitializeLemonWeaponMenus()
    {
        try
        {
            if (_luiWeaponDetailMenu != null)
                return;

            _luiWeaponDetailMenu = new NativeMenu(
                "AmmuNation",
                L("WeaponMenu_DetailTitle", "Thông tin chi tiết vũ khí"));

            _luiPool.Add(_luiWeaponDetailMenu);
            ConfigureKeyboardOnlyVehicleMenu(_luiWeaponDetailMenu);

            _luiWeaponDetailMenu.Visible = false;
        }
        catch
        {
            // swallow
        }
    }

    private void EnsureWeaponStatsPanel()
    {
        if (_luiWeaponStatsPanel != null)
            return;

        _luiWeaponStatDamage = new NativeStatsInfo(L("WeaponStat_Damage", "Sát thương"), 0);
        _luiWeaponStatFireRate = new NativeStatsInfo(L("WeaponStat_FireRate", "Tốc độ bắn"), 0);
        _luiWeaponStatAccuracy = new NativeStatsInfo(L("WeaponStat_Accuracy", "Độ chinh xác"), 0);
        _luiWeaponStatRange = new NativeStatsInfo(L("WeaponStat_Range", "Tầm bắn"), 0);

        _luiWeaponStatsPanel = new NativeStatsPanel(
            _luiWeaponStatDamage,
            _luiWeaponStatFireRate,
            _luiWeaponStatAccuracy,
            _luiWeaponStatRange
        )
        {
            BackgroundColor = Color.FromArgb(180, 32, 32, 32),
            ForegroundColor = Color.FromArgb(255, 255, 255, 255),
            Visible = true
        };
    }

    private void EstimateWeaponUiStats(WeaponEntry weapon, out float damage, out float fireRate, out float accuracy, out float range)
    {
        string name = (weapon?.Name ?? string.Empty).ToLowerInvariant();
        bool explosive = weapon != null && weapon.IsExplosiveWeapon;

        bool melee =
            name.Contains("dao gập") ||
            name.Contains("knife") ||
            name.Contains("machete") ||
            name.Contains("hammer") ||
            name.Contains("bat") ||
            name.Contains("flashlight") ||
            name.Contains("switchblade") ||
            name.Contains("cue");

        if (melee)
        {
            damage = 35f;
            fireRate = 70f;
            accuracy = 10f;
            range = 10f;
            return;
        }

        if (name.Contains("sniper") || name.Contains("tỉa") || name.Contains("precision") || name.Contains("marksman"))
        {
            damage = 95f;
            fireRate = 22f;
            accuracy = 92f;
            range = 98f;
        }
        else if (name.Contains("minigun"))
        {
            damage = 82f;
            fireRate = 98f;
            accuracy = 55f;
            range = 70f;
        }
        else if (name.Contains("shotgun"))
        {
            damage = 88f;
            fireRate = 42f;
            accuracy = 50f;
            range = 32f;
        }
        else if (name.Contains("smg") || name.Contains("machine pistol") || name.Contains("pdw") || name.Contains("mini smg"))
        {
            damage = 58f;
            fireRate = 90f;
            accuracy = 62f;
            range = 45f;
        }
        else if (name.Contains("rifle") || name.Contains("carbine") || name.Contains("mg") || name.Contains("combat") || name.Contains("service"))
        {
            damage = 72f;
            fireRate = 70f;
            accuracy = 72f;
            range = 75f;
        }
        else if (name.Contains("pistol") || name.Contains("revolver") || name.Contains("sns"))
        {
            damage = 55f;
            fireRate = 58f;
            accuracy = 66f;
            range = 38f;
        }
        else if (explosive)
        {
            damage = 98f;
            fireRate = 32f;
            accuracy = 45f;
            range = 82f;
        }
        else
        {
            damage = 65f;
            fireRate = 60f;
            accuracy = 60f;
            range = 55f;
        }

        if (weapon != null)
        {
            if (weapon.PriceMin >= 100000)
                damage = Math.Min(100f, damage + 4f);

            if (weapon.PriceMin >= 150000)
                range = Math.Min(100f, range + 4f);
        }
    }

    private void UpdateWeaponStatsPanel(WeaponEntry weapon)
    {
        if (weapon == null)
            return;

        EnsureWeaponStatsPanel();

        EstimateWeaponUiStats(weapon, out float damage, out float fireRate, out float accuracy, out float range);

        _luiWeaponStatDamage.Name = L("WeaponStat_Damage", "Sát thương");
        _luiWeaponStatFireRate.Name = L("WeaponStat_FireRate", "Tốc độ bắn");
        _luiWeaponStatAccuracy.Name = L("WeaponStat_Accuracy", "Độ chính xác");
        _luiWeaponStatRange.Name = L("WeaponStat_Range", "Tầm bắn");

        _luiWeaponStatDamage.Value = (int)Math.Round(ClampStat(damage));
        _luiWeaponStatFireRate.Value = (int)Math.Round(ClampStat(fireRate));
        _luiWeaponStatAccuracy.Value = (int)Math.Round(ClampStat(accuracy));
        _luiWeaponStatRange.Value = (int)Math.Round(ClampStat(range));
    }

    private void CloseLemonWeaponMenus(bool setCooldown)
    {
        try
        {
            if (_luiWeaponDetailMenu != null)
            {
                _luiWeaponDetailMenu.Visible = false;
                _luiWeaponDetailMenu.Clear();
            }
        }
        catch { }

        _luiWeaponDetailEntry = null;
        _luiWeaponDetailPrice = 0;
        _luiWeaponDetailAmmo = 0;
        _luiWeaponDetailHasAccessory = false;
        _luiWeaponDetailAccessoryCost = 0;

        _luiMenuAutoCloseEnabled = false;
        _luiMenuAutoCloseExpiry = 0;

        if (setCooldown)
            EnsureHelpBoxCooldownSet();
    }

    private void OpenWeaponDetailMenu(WeaponEntry chosen)
    {
        try
        {
            if (chosen == null)
                return;

            EnsureWeaponStatsPanel();
            UpdateWeaponStatsPanel(chosen);

            _luiWeaponDetailEntry = chosen;
            _luiWeaponDetailPrice = chosen.GetRandomPrice(_rng);
            _luiWeaponDetailAmmo = (chosen.TimesPurchased == 0)
                ? chosen.GetFirstAmmo(_rng)
                : chosen.GetRepeatAmmo(_rng);

            _luiWeaponDetailHasAccessory = false;
            _luiWeaponDetailAccessoryCost = 0;

            if (_weaponAttachments.TryGetValue(chosen.Hash, out uint[] comps) &&
                comps != null &&
                comps.Length > 0 &&
                !chosen.PlayerHasAccessory)
            {
                _luiWeaponDetailHasAccessory = true;
                int pct = _rng.Next(120, 146);
                _luiWeaponDetailAccessoryCost = Math.Max(
                    0,
                    (int)Math.Round(_luiWeaponDetailPrice * (pct / 100.0), MidpointRounding.AwayFromZero));
            }

            BuildWeaponDetailMenu(chosen);

            ConfigureKeyboardOnlyVehicleMenu(_luiWeaponDetailMenu);
            _luiWeaponDetailMenu.Visible = true;

            _luiMenuAutoCloseEnabled = true;
            _luiMenuAutoCloseExpiry = Game.GameTime + WeaponOfferDurationMs;

            Interval = 0;
            PlayFrontendSound("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        }
        catch
        {
            CloseLemonWeaponMenus(false);
        }
    }

    private void BuildWeaponDetailMenu(WeaponEntry chosen)
    {
        if (_luiWeaponDetailMenu == null || chosen == null)
            return;

        _luiWeaponDetailMenu.Clear();

        var nameItem = new NativeItem(
            LT("WeaponDetail_Name", "Tên vũ khí: {name}",
                "{name}", chosen.Name ?? string.Empty));
        nameItem.Panel = _luiWeaponStatsPanel;
        nameItem.Selected += (s, e) =>
        {
            try { UpdateWeaponStatsPanel(chosen); } catch { }
        };
        nameItem.Activated += (s, e) =>
        {
            try { UpdateWeaponStatsPanel(chosen); } catch { }
        };
        _luiWeaponDetailMenu.Add(nameItem);

        AddReadOnlyMenuLine(
            _luiWeaponDetailMenu,
            LT("WeaponDetail_Ammo", "Số lượng đạn tặng kèm: {ammo}",
                "{ammo}", _luiWeaponDetailAmmo.ToString("N0"))
        );

        AddReadOnlyMenuLine(
            _luiWeaponDetailMenu,
            LT("WeaponDetail_Price", "Giá mua: ${price}",
                "{price}", _luiWeaponDetailPrice.ToString("N0"))
        );

        if (_luiWeaponDetailHasAccessory)
        {
            AddReadOnlyMenuLine(
                _luiWeaponDetailMenu,
                LT("WeaponDetail_Accessory", "Phụ kiện: ${price}",
                    "{price}", _luiWeaponDetailAccessoryCost.ToString("N0"))
            );
        }

        var confirmSingle = new NativeItem(
            LT("WeaponDetail_ConfirmSingle", "Xác nhận mua vũ khí"));
        confirmSingle.Activated += (s, e) =>
        {
            ConfirmWeaponPurchaseFromMenu(chosen, false);
        };
        _luiWeaponDetailMenu.Add(confirmSingle);

        if (_luiWeaponDetailHasAccessory)
        {
            var confirmCombo = new NativeItem(
                LT("WeaponDetail_ConfirmCombo", "Xác nhận mua combo vũ khí"));
            confirmCombo.Activated += (s, e) =>
            {
                ConfirmWeaponPurchaseFromMenu(chosen, true);
            };
            _luiWeaponDetailMenu.Add(confirmCombo);
        }

        var decline = new NativeItem(
            LT("WeaponDetail_Decline", "Từ chối mua vũ khí"));
        decline.Activated += (s, e) =>
        {
            try { RecordBackCancel(PendingType.WeaponOffer); } catch { }
            CloseLemonWeaponMenus(true);
        };
        _luiWeaponDetailMenu.Add(decline);
    }

    private void ConfirmWeaponPurchaseFromMenu(WeaponEntry chosen, bool includeAccessory)
    {
        try
        {
            if (chosen == null)
            {
                CloseLemonWeaponMenus(false);
                return;
            }

            // Giữ nguyên logic mua cũ của AcceptPending()
            _pendingType = PendingType.WeaponOffer;
            _pendingWeaponHash = chosen.Hash;
            _peCo = _luiWeaponDetailPrice;
            _peAA = _luiWeaponDetailAmmo;
            _pendingAccessoryAvailable = _luiWeaponDetailHasAccessory;
            _pendAcCost = _luiWeaponDetailAccessoryCost;
            _pendingExpiryGameTime = Game.GameTime + WeaponOfferDurationMs;

            AcceptPending(includeAccessory);
            CloseLemonWeaponMenus(true);
        }
        catch
        {
            CloseLemonWeaponMenus(false);
        }
    }

    private void HandleCtrlLWeaponOffer(Ped ped)
    {
        try
        {
            UpdateSpecialWeaponUnlocks();
            UpdateAccessoryOwnership();

            var unlocked = new List<WeaponEntry>();
            foreach (var w in _specialWeapons)
            {
                if (w != null && w.Unlocked)
                    unlocked.Add(w);
            }

            if (unlocked.Count == 0)
            {
                GTA.UI.Screen.ShowSubtitle(
                    "~y~~h~" + L("WeaponOffer_NotUnlocked", "Loại vũ khí này chưa được mở khóa!"),
                    3000);
                return;
            }

            WeaponEntry chosen = unlocked[_rng.Next(unlocked.Count)];
            OpenWeaponDetailMenu(chosen);
        }
        catch
        {
            GTA.UI.Screen.ShowSubtitle(
                "~HUD_COLOUR_DEGEN_RED~~h~Lỗi khi mở menu vũ khí.",
                3000);
        }
    }

    // ----- AMMO processing (optimized, smoother) -----
    private void ProcessAmmoBatch()
    {
        Ped ped = Game.Player.Character;
        if (ped == null || !ped.Exists())
        {
            _isProcessingAmmo = false;
            return;
        }

        // đảm bảo không đặt Interval = 0 ở đây (vì 0 là per-frame và có thể gây spike khi queue lớn)
        int prevInterval = Interval;
        Interval = AMMO_PROCESS_INTERVAL_MS;

        int processedThisTick = 0;
        try
        {
            while (processedThisTick < AMMO_PROCESS_PER_TICK && _weaponHashesToProcess.Count > 0)
            {
                uint weaponHash = 0;
                try { weaponHash = _weaponHashesToProcess.Dequeue(); } catch { weaponHash = 0; }
                if (weaponHash == 0) { processedThisTick++; continue; }

                try
                {
                    bool isExplosive = IsExplosive(weaponHash);
                    int bonus = _rng.Next(isExplosive ? RandomMinExplosive : RandomMinGun,
                               (isExplosive ? RandomMaxExplosive : RandomMaxGun) + 1);
                    int add = (isExplosive ? ExplosiveToAdd : AmmoToAddGun) + bonus;

                    bool hasWeapon = false;
                    try { hasWeapon = Function.Call<bool>(Hash.HAS_PED_GOT_WEAPON, ped.Handle, weaponHash, false); } catch { hasWeapon = false; }

                    if (!hasWeapon)
                    {
                        try { Function.Call(Hash.GIVE_WEAPON_TO_PED, ped.Handle, weaponHash, 0, false, false); } catch { }
                    }

                    try { Function.Call(Hash.ADD_AMMO_TO_PED, ped.Handle, weaponHash, add); } catch { }
                }
                catch { /* swallow individual weapon errors */ }

                processedThisTick++;
            }
        }
        catch { /* outer swallow */ }

        // nếu đã xử lý xong mọi thứ -> restore trạng thái
        if (_weaponHashesToProcess.Count == 0)
        {
            _isProcessingAmmo = false;
            Interval = prevInterval > 0 ? prevInterval : 1000;
        }
        else
        {
            // vẫn còn, giữ trạng thái xử lý (Interval đã set AMMO_PROCESS_INTERVAL_MS)
        }
    }

    private bool IsExplosive(uint weaponHash)
    {
        if (weaponHash == (uint)WeaponHash.RPG ||
            weaponHash == (uint)WeaponHash.GrenadeLauncher ||
            weaponHash == (uint)WeaponHash.Grenade ||
            weaponHash == (uint)WeaponHash.StickyBomb ||
            weaponHash == (uint)WeaponHash.ProximityMine ||
            weaponHash == (uint)WeaponHash.PipeBomb)
            return true;

        var entry = _specialWeapons.Find(w => w.Hash == weaponHash);
        if (entry != null) return entry.IsExplosiveWeapon;
        return false;
    }

    // Re-scan whether engine has seen / loaded these weapons and whether player has them
    private void UpdateSpecialWeaponUnlocks()
    {
        Ped player = Game.Player.Character;

        bool playerValid = player != null && player.Exists();

        for (int i = 0; i < _specialWeapons.Count; i++)
        {
            var we = _specialWeapons[i];

            bool valid = SafeCall(
                () => Function.Call<bool>(Hash.IS_WEAPON_VALID, we.Hash),
                false
            );

            if (playerValid)
            {
                bool playerHasWeapon = SafeCall(
                    () => Function.Call<bool>(
                        Hash.HAS_PED_GOT_WEAPON,
                        player.Handle,
                        we.Hash,
                        false),
                    false
                );

                if (playerHasWeapon)
                    valid = true;
            }

            we.Unlocked = valid;
        }
    }

    // Update per-player ownership of weapon and accessory components
    private void UpdateAccessoryOwnership()
    {
        Ped player = Game.Player.Character;
        if (player == null || !player.Exists())
            return;

        int playerHandle = player.Handle;

        foreach (var we in _specialWeapons)
        {
            bool hasWeapon = SafeCall(
                () => Function.Call<bool>(
                    Hash.HAS_PED_GOT_WEAPON,
                    playerHandle,
                    we.Hash,
                    false),
                false
            );

            we.PlayerHasWeapon = hasWeapon;

            bool hasAccessory = false;

            if (hasWeapon &&
                _weaponAttachments.TryGetValue(we.Hash, out uint[] comps) &&
                comps != null &&
                comps.Length > 0)
            {
                bool allComponentsPresent = true;

                foreach (var comp in comps)
                {
                    bool hasComp = SafeCall(
                        () => Function.Call<bool>(
                            Hash.HAS_PED_GOT_WEAPON_COMPONENT,
                            playerHandle,
                            we.Hash,
                            comp),
                        false
                    );

                    if (!hasComp)
                    {
                        allComponentsPresent = false;
                        break;
                    }
                }

                hasAccessory = allComponentsPresent;
            }

            we.PlayerHasAccessory = hasAccessory;
        }
    }
}