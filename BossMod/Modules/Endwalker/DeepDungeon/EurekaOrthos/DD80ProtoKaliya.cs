namespace BossMod.Endwalker.DeepDungeon.EurekaOrthos.DD80ProtoKaliya;

public enum OID : uint
{
    Boss = 0x3D18, // R5.000, x?
    WeaponsDrone = 0x3D19, // R2.000, x?
    Helper = 0x233C, // R0.500, x12, 523 type
}

public enum AID : uint
{
    Aetheromagnetism_Type2 = 31431, // Helper->player, no cast, single-target
    Aetheromagnetism_Type1 = 31430, // Helper->player, no cast, single-target
    AutoAttack = 31421, // Boss->players, no cast, range 6+R ?-degree cone
    AutoCannons = 31432, // WeaponsDrone->self, 4.0s cast, range 41+R width 5 rect
    Barofield = 31427, // Boss->self, 3.0s cast, single-target
    CentralizedNerveGas_Boss = 31423, // Boss->self, 4.5s cast, range 25+R ?-degree cone
    CentralizedNerveGas_Helper = 32933, // Helper->self, 5.3s cast, range 25+R 120-degree cone
    LeftwardNerveGas_Boss = 31424, // Boss->self, 4.5s cast, range 25+R ?-degree cone
    LeftwardNerveGas_Helper = 32934, // Helper->self, 5.3s cast, range 25+R 180-degree cone
    NanosporeJet = 31429, // Boss->self, 5.0s cast, range 100 circle
    NerveGasRing_Boss = 31426, // Boss->self, 5.0s cast, range ?-30 donut
    NerveGasRing_Helper = 32930, // Helper->self, 7.2s cast, range ?-30 donut
    Resonance = 31422, // Boss->player, 5.0s cast, range 12 ?-degree cone // Tank Buster
    RightwardNerveGas_Boss = 31425, // Boss->self, 4.5s cast, range 25+R ?-degree cone
    RightwardNerveGas_Helper = 32935, // Helper->self, 5.3s cast, range 25+R 180-degree cone
}

public enum SID : uint // Status Effect information
{
    Barofield_Status = 3420, // none->Boss, extra=0x0
    NegativeCharge_Player = 3419, // none->player, extra=0x0
    PositiveCharge_Player = 3418, // none->player, extra=0x0
    NegativeCharge_Drone = 3417, // none->WeaponsDrone, extra=0x0
    PositiveCharge_Drone = 3416, // none->WeaponsDrone, extra=0x0
}

public enum IconID : uint
{
    Icon_230 = 230, // player // TankBuster
    Icon_378 = 378, // player
    Icon_377 = 377, // player
}

public enum TetherID : uint
{
    Tether_38 = 38, // WeaponsDrone->player // Used to show which drone you're tied to for magnets
}

// class Resonance(BossModule module) : Components.

class DD80ProtoKaliyaStates : StateMachineBuilder
{
    public DD80ProtoKaliyaStates(BossModule module) : base(module)
    {
        TrivialPhase()
            //.ActivateOnEnter<Resonance>()
            ;
    }
}

[ModuleInfo(BossModuleInfo.Maturity.Contributed, Contributors = "legendoficeman", GroupType = BossModuleInfo.GroupType.CFC, GroupID = 903, NameID = 12247)] // Make sure to edit this Ice
public class DD80ProtoKaliya(WorldState ws, Actor primary) : BossModule(ws, primary, new(-600, -300), new ArenaBoundsCircle(20));

// STILL A WIP
