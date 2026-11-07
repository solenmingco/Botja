using Botja.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Ocelot.Lifecycle;
using Ocelot.Windows;
using System.Collections.Generic;
using System.Linq;

namespace Botja.Windows;

public partial class FateListWindow(
    IFateTable fateTable,
    FateNavigationService nav,
    FatePriorityService fatePriority,
    IAetheryteList aetheryteList,
    IClientState clientState,
    ITargetManager targets,
    IObjectTable objects,
    IConfigWindow configWindow,
    GuiInteractionService guiInteract,
    CeSignupService ceSignup,
    CombatControlService combatControl,
    AutoModeService autoMode,
    MobFarmService mobFarm,
    HostileDetectionService hostileDetection
) : OcelotWindow("博兹雅助手 CBB###Cant be Bozja'ed"), IOnLoad, IOnTerritoryChanged, IMainWindow
// 标题里的 ### 保留原来的 ImGui 窗口 id，升级后窗口位置/大小不会被重置。
{
    // Bozjan Southern Front and Zadnor — auto-open the window on entering these zones.
    internal static readonly HashSet<uint> BozjaZones = [920, 975];

    public void OnLoad()
    {
    }

    public void OnTerritoryChanged(uint territory)
    {
        if (BozjaZones.Contains(territory))
            IsOpen = true;
    }

    protected override void Render()
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            if (ImGui.Button($"{FontAwesomeIcon.Stop.ToIconString()}##stop"))
                nav.Stop();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("停止当前移动 / 取消寻路");

        ImGui.SameLine();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            if (ImGui.Button($"{FontAwesomeIcon.Cog.ToIconString()}##settings"))
                configWindow.Toggle();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("设置");

        ImGui.SameLine();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            using (ImRaii.Disabled(fateTable.Length == 0))
            {
                if (ImGui.Button($"{FontAwesomeIcon.Star.ToIconString()}##topfate") && fatePriority.GetAutoSelectableFates(fateTable).FirstOrDefault() is { } top)
                {
                    combatControl.SetActiveFate(top.FateId);
                    nav.AutoNavigate(top.Position, top.Name.ToString());
                }
            }
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("自动前往优先级最高的 FATE");

        ImGui.Separator();

        if (ImGui.Button(ceSignup.IsRunning ? "停止报名紧急遭遇战" : "报名正在招募的紧急遭遇战"))
        {
            if (ceSignup.IsRunning)
                ceSignup.Stop();
            else
                ceSignup.Start();
        }
        ImGui.TextUnformatted(ceSignup.Status);
        if (!string.IsNullOrEmpty(ceSignup.LastAction))
            ImGui.TextUnformatted($"上一次点击：{ceSignup.LastAction}");
        ImGui.TextUnformatted($"检测到的 CE 计时：{ceSignup.DetectedTimer}");

        if (ceSignup.TryFindRecruitingEvent(out var recruitingCeId))
        {
            string ceKind = ceSignup.IsLargeScaleEvent(recruitingCeId) ? "大型攻城战" : "紧急遭遇战";
            ImGui.TextUnformatted($"正在招募：{ceSignup.GetCeName(recruitingCeId)}（{ceKind}，ID {recruitingCeId}）");
            ImGui.SameLine();
            using (ImRaii.Disabled(ceSignup.IsCeBlacklisted(recruitingCeId)))
            {
                if (ImGui.Button("加入忽略名单##ceblacklist"))
                    ceSignup.BlacklistCe(recruitingCeId);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("以后不再自动报名这个 CE");
        }

        ImGui.Separator();

        if (ImGui.Button(combatControl.ManualEnabled ? "停止战斗循环" : "启动战斗循环"))
        {
            if (combatControl.ManualEnabled)
                combatControl.StopManualCombat();
            else
                combatControl.StartManualCombat();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("无视自动模式 / FATE / CE 状态，立即强制开启所选战斗循环");

        if (ImGui.Button(mobFarm.IsRunning ? "停止刷怪" : "开始刷怪"))
        {
            if (mobFarm.IsRunning)
                mobFarm.Stop();
            else
                mobFarm.Start();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("在设定好的刷怪点自动刷选中的博兹雅 / 天佑女王怪物");
        ImGui.TextUnformatted($"刷怪状态：{mobFarm.Status}");
        if (mobFarm.IsRunning)
        {
            ImGui.TextUnformatted($"阶段：{PhaseText(mobFarm.Phase)} ｜ 刷怪点：{mobFarm.CurrentSpotName}");
            ImGui.TextUnformatted($"已选模板：{mobFarm.SelectedMobCount} ｜ 空闲：{mobFarm.FreeMobCount} ｜ 正在攻击你：{mobFarm.EngagedMobCount} ｜ 被抢：{mobFarm.ContestedMobCount}");
            if (!string.IsNullOrEmpty(mobFarm.CurrentTargetName))
                ImGui.TextUnformatted($"当前目标：{mobFarm.CurrentTargetName}");
        }

        if (ImGui.Button(autoMode.IsRunning ? "停止自动模式" : "启动自动模式"))
        {
            if (autoMode.IsRunning)
                autoMode.Stop();
            else
                autoMode.Start();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("自动跑图打下一个 FATE，遇到招募中的 CE 先报名");
        if (!string.IsNullOrEmpty(autoMode.Status))
            ImGui.TextUnformatted(autoMode.Status);

        if (nav.DebugPhase != "None" && !string.IsNullOrEmpty(nav.DebugPendingDestinationName))
            ImGui.TextUnformatted($"正在前往：{nav.DebugPendingDestinationName}（{nav.DebugPhaseText}）");

        RenderHostileStatus();
        RenderDebugInfo();

        ImGui.Separator();

        if (fateTable.Length == 0)
        {
            ImGui.TextUnformatted("当前区域没有进行中的 FATE。");
            return;
        }

        ImGui.Columns(8, "fates", true);
        ImGui.TextUnformatted("名称"); ImGui.NextColumn();
        ImGui.TextUnformatted("ID"); ImGui.NextColumn();
        ImGui.TextUnformatted("区域"); ImGui.NextColumn();
        ImGui.TextUnformatted("进度"); ImGui.NextColumn();
        ImGui.TextUnformatted("剩余"); ImGui.NextColumn();
        ImGui.TextUnformatted("预计"); ImGui.NextColumn();
        ImGui.TextUnformatted("前往"); ImGui.NextColumn();
        ImGui.TextUnformatted(""); ImGui.NextColumn();
        ImGui.Separator();

        int i = 0;
        foreach (var fate in fatePriority.GetSortedFates(fateTable))
        {
            var isBlacklisted = fatePriority.IsBlacklisted(fate);
            var isFollowFate = fatePriority.IsFollowFate(fate);
            var rem = fate.TimeRemaining;
            var time = rem > 0 ? $"{rem / 60:D2}:{rem % 60:D2}" : "--:--";
            var eta = nav.EstimateTravelSeconds(fate.Position);
            var etaText = $"{(int)eta / 60:D2}:{(int)eta % 60:D2}";
            var sector = fatePriority.GetSector(fate);

            using (ImRaii.PushColor(ImGuiCol.Text, isBlacklisted ? 0xFF909090 : 0xFFFFFFFF))
                ImGui.TextUnformatted(fate.Name.ToString()); ImGui.NextColumn();
            ImGui.TextUnformatted(fatePriority.GetFateTemplateId(fate).ToString()); ImGui.NextColumn();
            ImGui.TextUnformatted(sector.HasValue ? ((int)sector.Value).ToString() : "--"); ImGui.NextColumn();
            ImGui.TextUnformatted($"{fate.Progress}%"); ImGui.NextColumn();
            ImGui.TextUnformatted(time); ImGui.NextColumn();
            ImGui.TextUnformatted(etaText); ImGui.NextColumn();


            // Walk button
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                if (ImGui.Button($"{FontAwesomeIcon.Running.ToIconString()}##walk_{i}"))
                {
                    combatControl.SetActiveFate(fate.FateId);
                    nav.PathTo(fate.Position, fate.Name.ToString());
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"步行前往 {fate.Name}");

            ImGui.SameLine();

            // Auto button — /return to camp if needed, then aethernet teleport, then walk
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                if (ImGui.Button($"{FontAwesomeIcon.Magic.ToIconString()}##auto_{i}"))
                {
                    combatControl.SetActiveFate(fate.FateId);
                    nav.AutoNavigate(fate.Position, fate.Name.ToString());
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"自动前往 {fate.Name}（传送 → 步行）");

            ImGui.NextColumn();

            // Blacklist button
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                using (ImRaii.PushColor(ImGuiCol.Button, isBlacklisted ? 0xFFCC6666 : 0xFF4A4A4A))
                {
                    if (!isBlacklisted && ImGui.Button($"{FontAwesomeIcon.Ban.ToIconString()}##blacklist_{i}"))
                        fatePriority.Blacklist(fate);
                    else if (isBlacklisted && ImGui.Button($"{FontAwesomeIcon.Ban.ToIconString()}##blacklisted_{i}"))
                        fatePriority.Unblacklist(fate);
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(isBlacklisted
                    ? $"自动导航时不再忽略 {fate.Name}"
                    : $"自动导航时忽略 {fate.Name}");

            ImGui.NextColumn();
            i++;
        }

        ImGui.Columns(1);
    }

    private static string PhaseText(MobFarmPhase phase) => phase switch
    {
        MobFarmPhase.Waiting => "等待",
        MobFarmPhase.Gathering => "拉怪",
        MobFarmPhase.Stacking => "聚怪",
        MobFarmPhase.Fighting => "输出",
        _ => phase.ToString(),
    };

    private void RenderHostileStatus()
    {
        var hostiles = hostileDetection.GetNearbyHostiles();
        if (hostiles.Count == 0)
            return;

        bool underAttack = hostileDetection.IsPlayerUnderAttack();
        using (ImRaii.PushColor(ImGuiCol.Text, underAttack ? 0xFF0000FF : 0xFFFFAA00))
            ImGui.TextUnformatted($"附近敌对 NPC：{hostiles.Count}{(underAttack ? "（正在被攻击！）" : string.Empty)}");

        if (!ImGui.CollapsingHeader($"附近敌对 NPC（{hostiles.Count}）"))
            return;

        if (!ImGui.BeginTable("hostiles", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            return;

        ImGui.TableSetupColumn("名称");
        ImGui.TableSetupColumn("距离");
        ImGui.TableSetupColumn("血量");
        ImGui.TableSetupColumn("坐标");
        ImGui.TableSetupColumn("DataId");
        ImGui.TableSetupColumn("仇恨类型");
        ImGui.TableSetupColumn("FATE ID");
        ImGui.TableHeadersRow();

        foreach (var hostile in hostiles)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(hostile.Name);
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted($"{hostile.DistanceYalms:F1}y");
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted($"{hostile.CurrentHp}/{hostile.MaxHp}");
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted($"({hostile.Position.X:F0}, {hostile.Position.Z:F0})");
            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(hostile.DataId.ToString());
            ImGui.TableSetColumnIndex(5);
            ImGui.TextUnformatted(hostile.AggroType.ToString());
            ImGui.TableSetColumnIndex(6);
            ImGui.TextUnformatted(hostile.FateId == 0 ? "-" : hostile.FateId.ToString());
        }

        ImGui.EndTable();
    }

}
