using Dalamud.Configuration;
using Ocelot.Config;
using Ocelot.Config.Fields;
using Botja.Services;
using Botja.Windows;
using Botja.Windows.Config;
using System.Collections.Generic;

namespace Botja;

public interface IPluginConfig
{
    NavigationConfig Navigation { get; }

    MountConfig Mount { get; }

    FatePriorityConfig FatePriority { get; }

    ItemInspectionConfig ItemInspection { get; }

    CombatConfig Combat { get; }

    BlacklistConfig Blacklist { get; }

    HostileDetectionConfig HostileDetection { get; }

    FollowConfig Follow { get; }

    AutoModeConfig AutoMode { get; }

    MobFarmConfig MobFarm { get; }
}

public sealed class PluginConfig : IPluginConfig, IPluginConfiguration
{
    // 1 = 坐骑设置从 NavigationConfig 拆到独立的 MountConfig。
    public int Version { get; set; } = 0;

    public NavigationConfig Navigation { get; set; } = new();

    public MountConfig Mount { get; set; } = new();

    public FatePriorityConfig FatePriority { get; set; } = new();

    public ItemInspectionConfig ItemInspection { get; set; } = new();

    public CombatConfig Combat { get; set; } = new();

    public BlacklistConfig Blacklist { get; set; } = new();

    public HostileDetectionConfig HostileDetection { get; set; } = new();

    public FollowConfig Follow { get; set; } = new();

    public AutoModeConfig AutoMode { get; set; } = new();

    public MobFarmConfig MobFarm { get; set; } = new();

    public void HydrateMissingSections()
    {
        Navigation ??= new();
        Mount ??= new();
        FatePriority ??= new();
        ItemInspection ??= new();
        Combat ??= new();
        Blacklist ??= new();
        HostileDetection ??= new();
        Follow ??= new();
        AutoMode ??= new();
        MobFarm ??= new();
        ItemInspection.SkipItemIds ??= [];
        Blacklist.FateIds ??= [];
        Blacklist.CeEventIds ??= [];
        Combat.SelectedAis ??= [];
        Combat.SelectedBossModProfiles ??= [];
        Follow.FateIds ??= [];
        MobFarm.MobBaseIds ??= [];
        MobFarm.Spots ??= [];
    }

    // 老版本把「多远才上坐骑」放在移动与导航分页里，拆出坐骑分页后没做过迁移的配置会
    // 悄悄退回默认值 —— 这里只在真正是老配置（Version < 1）时搬一次。
    public void Migrate()
    {
        if (Version >= 1)
            return;

        if (Navigation.MountRange > 0f)
            Mount.MountRange = Navigation.MountRange;

        Version = 1;
    }
}

public enum FateSectorPreference
{
    Any = 0,
    Sector1 = 1,
    Sector2 = 2,
    Sector3 = 3,
}

public sealed class FatePriorityConfig : IAutoConfig
{
    [FloatRange(-10f, 10f)]
    public float ProgressWeight { get; set; } = 1f;

    [FloatRange(-10f, 10f)]
    public float TimeRemainingWeight { get; set; } = 0.35f;

    [FloatRange(-10f, 10f)]
    public float TravelTimeWeight { get; set; } = -0.5f;

    [FloatRange(-10f, 10f)]
    public float ProgressRateWeight { get; set; } = 1f;

    [EnumSelect<FateSectorPreference>]
    public FateSectorPreference PreferredSector { get; set; } = FateSectorPreference.Any;

    [Checkbox]
    public bool LimitToPreferredSector { get; set; } = false;

    [FloatRange(-10f, 10f)]
    public float PreferredSectorWeight { get; set; } = 0.2f;

    // FATEs with progress above this are never picked as auto-navigate targets.
    [FloatRange(0f, 100f)]
    public float MaxProgressPercent { get; set; } = 100f;
}

// 导航距离设置。旧版本把「多远才上坐骑」放在这里，现在只留一个无 UI 的字段给
// PluginConfig.Migrate() 读一次，坐骑相关选项都在 MountConfig 里。
public sealed class NavigationConfig : IAutoConfig
{
    public float MountRange { get; set; } = 30f;

    // 走到离以太水晶圆心多近就停稳 / 喊 Lifestream。游戏的水晶交互半径约 4.3 米，
    // BOCCHI 用 3.2 米站位 + 3.7 米到达判定；这里默认 4 米，比原来的 6 米近很多。
    // 别调太大，超过 4.3 米 Lifestream 会直接拒绝传送（Destination could not be found）。
    [FloatRange(4f, 8f, Order = 0)]
    public float ShardApproachDistance { get; set; } = 4f;
}

public sealed class MountConfig : IAutoConfig
{
    // 赶路时要不要自动上坐骑。关掉之后人物全程跑路（战斗中仍然会用冲刺赶路）。
    [Checkbox(Order = 0)]
    public bool UseMount { get; set; } = true;

    // 只有剩余路程超过这个距离才会召唤坐骑，太近就直接跑过去。
    [FloatRange(0f, 100f, Order = 1, Requires = nameof(UseMount))]
    public float MountRange { get; set; } = 30f;

    // 0 = 随机坐骑（坐骑轮盘），其它值是 Mount 表 RowId。
    [MountSelect(Order = 2, Requires = nameof(UseMount))]
    public uint PreferredMountId { get; set; } = 0;
}

public sealed class ItemInspectionConfig : IAutoConfig
{
    [ItemInspectionChecklist]
    public HashSet<int> SkipItemIds { get; set; } = [];
}

public sealed class CombatConfig : IAutoConfig
{
    // Automatically enable/disable the rotation plugin's auto-rotation based on FATE/CE state:
    // off while travelling, off once the current FATE/CE has ended, on while actively fighting it.
    [Checkbox]
    public bool AutoControlEnabled { get; set; } = true;

    [CombatAiChecklist]
    public List<CombatAiSelection> SelectedAis { get; set; } = [];

    [BossModProfileChecklist]
    public List<string> SelectedBossModProfiles { get; set; } = [];
}

// Fate/CE template IDs (not per-instance runtime IDs) the player never wants Auto Mode to pick.
public sealed class BlacklistConfig : IAutoConfig
{
    [BlacklistChecklist(false)]
    public HashSet<uint> FateIds { get; set; } = [];

    [BlacklistChecklist(true)]
    public HashSet<uint> CeEventIds { get; set; } = [];
}

// Fate template IDs (not per-instance runtime IDs) known to chain into a follow-up FATE at the
// same spot — Auto Mode waits there after one of these finishes instead of moving on immediately.
public sealed class FollowConfig : IAutoConfig
{
    public HashSet<uint> FateIds { get; set; } = [];

    [FloatRange(10f, 300f)]
    public float WaitSeconds { get; set; } = 90f;

    [FloatRange(5f, 100f)]
    public float RadiusYalms { get; set; } = 30f;
}

public sealed class AutoModeConfig : IAutoConfig
{
    [Checkbox]
    public bool DoCriticalEngagements { get; set; } = true;

    [Checkbox]
    public bool DoFates { get; set; } = true;

    // 湖岸攻城战 / 达尔马斯塔这类 48 人大型攻城战单独一个开关：
    // 它们不是普通的 8 人 CE，一旦参加就是 20~30 分钟的独立副本，会长时间打断刷 FATE，所以默认关闭。
    [Checkbox]
    public bool DoLargeScaleEngagements { get; set; } = false;
}

public sealed class MobFarmConfig : IAutoConfig
{
    [MobTemplateChecklist]
    public HashSet<uint> MobBaseIds { get; set; } = [];

    [Checkbox]
    public bool ShouldHandleTargeting { get; set; } = true;

    [Checkbox]
    public bool ForceTargetCentralEnemy { get; set; } = true;

    [FloatRange(10f, 200f)]
    public float MaxMobDistanceYalms { get; set; } = 75f;

    [FarmSpotList]
    public List<MobFarmSpot> Spots { get; set; } = [];

    [FloatRange(3f, 30f)]
    public float ClaimedSpotSeconds { get; set; } = 8f;

    [FloatRange(3f, 15f)]
    public float ClaimedPlayerRadiusYalms { get; set; } = 5f;

    [IntRange(0, 20)]
    public int MinimumMobsToStartLoop { get; set; } = 0;

    [IntRange(1, 20)]
    public int MinimumMobsToStartFight { get; set; } = 5;

    [FloatRange(5f, 60f)]
    public float StackingTimeoutSeconds { get; set; } = 15f;

    [Checkbox]
    public bool UseRangedPull { get; set; } = true;

    [Checkbox]
    public bool UseProvoke { get; set; }

    [Checkbox]
    public bool UseGapCloser { get; set; }

    [Checkbox]
    public bool ReturnToStartInWaitingPhase { get; set; }

    [FloatRange(10f, 1000f)]
    public float MinDistanceToReturnHomeYalms { get; set; } = 200f;

    [Checkbox]
    public bool RenderDebugLines { get; set; }
}

public sealed class HostileDetectionConfig : IAutoConfig
{
    // Maximum distance at which hostile NPCs are tracked.
    [FloatRange(5f, 200f)]
    public float DetectionRadiusYalms { get; set; } = 100f;

    // Detour around hostiles blocking the path instead of walking straight through them.
    [Checkbox]
    public bool EnableNavmeshAvoidance { get; set; } = true;

    // Omnidirectional aggro range for Sound-type mobs — anything closer than this triggers them
    // regardless of facing.
    [FloatRange(5f, 30f)]
    public float SoundAggroRangeYalms { get; set; } = 12f;

    // Extra buffer added on top of every sight/sound obstacle to account for mobs wandering/turning
    // between replans.
    [FloatRange(0f, 10f)]
    public float AvoidanceSafetyMarginYalms { get; set; } = 1f;

    // Minimum distance always kept from a hostile's own position regardless of aggro type/facing —
    // a Sight-type mob's cone only blocks its front, so without this the path could otherwise brush
    // right past its side/back at near-zero range, which "passes by" safely but looks/feels wrong.
    [FloatRange(0f, 15f)]
    public float AvoidancePersonalSpaceYalms { get; set; } = 5f;

    // How far beyond an obstacle's real aggro radius still counts as "worth avoiding when the extra
    // detour distance is cheap" — the knob for how wide a berth routes prefer to give hostiles when
    // it's free to do so, versus only barely clearing their edge.
    [FloatRange(0f, 20f)]
    public float AvoidanceComfortDistanceYalms { get; set; } = 6f;
}


