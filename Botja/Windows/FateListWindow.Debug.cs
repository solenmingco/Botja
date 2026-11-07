using Dalamud.Bindings.ImGui;
using System;
using System.Linq;

namespace Botja.Windows;

public partial class FateListWindow
{
    private string inspectAddonName = "Talk";
    private int callbackValue;
    private string uiClickFilter = "ItemInspection";
    private string uiClickName = "";
    private string hoveredNodeInspection = "";
    private bool liveInspectHoveredNodes;
    private DateTime? delayedHoverCaptureAt;
    private int itemInspectionListRowIndex;
    private bool itemInspectionListDispatchEvent = true;
    private string ceRecruitmentButton = "Register";
    private string genericListAddonName = "SkyIslandExchange2";
    private int genericListNodeId = 13;
    private int genericListRowIndex;
    private bool genericListDispatchEvent = true;

    private void RenderDebugInfo()
    {
        if (!ImGui.CollapsingHeader("调试信息"))
            return;

        if (targets.Target is { } distanceTarget && objects.LocalPlayer is { } localPlayer)
        {
            float distance = System.Numerics.Vector3.Distance(localPlayer.Position, distanceTarget.Position);
            ImGui.TextUnformatted($"到目标距离：{distance:F1}y");
        }
        else
        {
            ImGui.TextUnformatted("到目标距离：没有目标");
        }

        if (targets.Target is { } target)
        {
            ImGui.TextUnformatted($"目标：{target.Name} — BaseId：{target.BaseId}  坐标：{target.Position:f1}");
        }
        else
        {
            ImGui.TextUnformatted("选中一个以太之光可以查看它的 BaseId。");
        }

        var zoneShards = aetheryteList
            .Where(a => a.TerritoryId == clientState.TerritoryType)
            .ToList();
        if (zoneShards.Count > 0)
        {
            ImGui.TextUnformatted("本区域已登记的以太之光：");
            foreach (var shard in zoneShards)
            {
                uint pnId = shard.AetheryteData.Value.PlaceName.RowId;
                string pnName = shard.AetheryteData.Value.PlaceName.Value.Name.ToString();
                ImGui.BulletText($"{pnName} — 地名 ID：{pnId}  以太之光 ID：{shard.AetheryteId}");
            }
        }

        ImGui.Separator();

        ImGui.TextUnformatted("导航调试：");
        ImGui.TextUnformatted($"阶段：{nav.DebugPhase}（tick：{nav.DebugPhaseTicks}）");
        ImGui.TextUnformatted($"vnav：寻路中={nav.DebugVnavIsPathfinding}  移动中={nav.DebugVnavIsRunning}  网格就绪={nav.DebugVnavIsNavmeshReady}");
        ImGui.TextUnformatted($"vnav 实际路径点数：{nav.DebugVnavPathWaypointCount}");
        ImGui.TextUnformatted($"Lifestream 忙碌：{nav.DebugLifestreamBusy}");
        ImGui.TextUnformatted($"最终目标：{nav.DebugPendingDestination:F1}");
        ImGui.TextUnformatted($"起点水晶：{nav.DebugPendingSourceShardPos:F1}");
        ImGui.TextUnformatted($"终点水晶：{nav.DebugPendingDestShardPos:F1}");

        ImGui.Separator();

        ImGui.TextUnformatted("敌对检测调试：");
        ImGui.TextUnformatted($"检测半径：{hostileDetection.DetectionRadiusYalms:F1}y");
        ImGui.TextUnformatted($"扫描对象：{hostileDetection.DebugTotalObjectCount}  战斗 NPC：{hostileDetection.DebugBattleNpcCount}  敌对标记：{hostileDetection.DebugHostileFlaggedCount}  半径内：{hostileDetection.DebugWithinRadiusCount}");
        if (ImGui.Button("复制 NPC 详细数据"))
            ImGui.SetClipboardText(hostileDetection.DumpNpcDebugData(targets.Target, clientState.TerritoryType));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("把当前加载的所有战斗 NPC、当前目标、原始标记、native 字段与敌对判定结果复制成文本。");

        ImGui.Separator();

        ImGui.TextUnformatted("界面交互测试：");
        if (ImGui.Button("点击「是」（SelectYesno）"))
            guiInteract.ClickSelectYesnoYes();
        ImGui.TextUnformatted(guiInteract.DebugAddonInfo("SelectYesno"));

        ImGui.Separator();

        // Confirms what the appraising overlay computes each frame, without needing to hover-dump
        // the native window manually.
        ImGui.TextUnformatted("鉴定覆盖层调试：");
        var appraisingRect = guiInteract.GetAddonScreenRect("ItemInspectionList");
        ImGui.TextUnformatted(appraisingRect is { } rect
            ? $"ItemInspectionList rect: X={rect.X:F0} Y={rect.Y:F0} W={rect.Width:F0} H={rect.Height:F0}  overlay->({rect.X + rect.Width + 8:F0},{rect.Y:F0})"
            : "ItemInspectionList：未打开");
        if (ImGui.Button("复制跳过勾选框的行坐标"))
            ImGui.SetClipboardText(guiInteract.DumpItemInspectionRowPositions());

        ImGui.Separator();

        ImGui.TextUnformatted("CE 招募调试：");
        if (ImGui.Button("打开义军招募窗口"))
            guiInteract.OpenCeRecruitmentWindow();
        ImGui.SameLine();
        if (ImGui.Button("复制 CE 按钮"))
            ImGui.SetClipboardText(string.Join("\n", guiInteract.GetCeRecruitmentButtons()));
        ImGui.SameLine();
        if (ImGui.Button("复制 CE 数据"))
            ImGui.SetClipboardText(guiInteract.DumpCeRecruitment());
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("复制招募窗口的行数据、每个按钮的文字 / 事件参数 / 是否可用，以及窗口里的全部文本节点。");
        ImGui.TextUnformatted(string.Join("\n", guiInteract.DumpCeRecruitment().Split('\n').Take(8)));
        ImGui.InputText("CE 按钮文字", ref ceRecruitmentButton, 64);
        ImGui.SameLine();
        if (ImGui.Button("点击 CE 按钮"))
            guiInteract.ClickCeRecruitmentButton(ceRecruitmentButton);

        ImGui.Separator();

        if (ImGui.Button("列出已加载界面"))
            ImGui.SetClipboardText(string.Join("\n", guiInteract.ListLoadedAddons()));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("把所有当前已加载的界面名复制到剪贴板");

        foreach (var addonInfo in guiInteract.ListLoadedAddons())
        {
            if (!addonInfo.StartsWith("Talk") && !addonInfo.StartsWith("SelectYesno") && !addonInfo.Contains("Visible=True"))
                continue;
            ImGui.BulletText(addonInfo);
        }

        ImGui.InputText("要导出的界面名", ref inspectAddonName, 64);
        ImGui.SameLine();
        if (ImGui.Button("导出节点"))
            ImGui.SetClipboardText(guiInteract.DumpAddonNodes(inspectAddonName));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("把节点信息（id / 类型 / 坐标 / 文字）复制到剪贴板");

        ImGui.InputInt("回调参数", ref callbackValue);
        ImGui.SameLine();
        if (ImGui.Button("触发回调##inspectAddon"))
            guiInteract.FireAddonCallback(inspectAddonName, true, callbackValue);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Callback.Fire(\"{inspectAddonName}\", true, {callbackValue})");

        if (ImGui.Button("鉴定结果：下一个"))
            guiInteract.ClickItemInspectionResultNext();
        ImGui.SameLine();
        if (ImGui.Button("关闭"))
            guiInteract.ClickItemInspectionResultClose();

        if (ImGui.Button("导出鉴定列表行"))
            ImGui.SetClipboardText(guiInteract.DumpAddonListRows("ItemInspectionList", 7));
        if (ImGui.BeginTable("ItemInspectionDebugRows", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("行");
            ImGui.TableSetupColumn("物品 ID");
            ImGui.TableSetupColumn("数量");
            ImGui.TableSetupColumn("跳过");
            ImGui.TableHeadersRow();

            foreach (var row in guiInteract.GetItemInspectionDebugRows())
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(row.RowIndex.ToString());
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(row.TrueItemId.ToString());
                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(row.Quantity.ToString());
                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted(row.Skipped ? "是" : "否");
            }

            ImGui.EndTable();
        }
        ImGui.InputInt("鉴定列表行号", ref itemInspectionListRowIndex);
        ImGui.SameLine();
        ImGui.Checkbox("派发事件", ref itemInspectionListDispatchEvent);
        if (ImGui.Button("选中鉴定列表行"))
            guiInteract.SelectItemInspectionListRow(itemInspectionListRowIndex, itemInspectionListDispatchEvent);
        ImGui.SameLine();
        if (ImGui.Button("点击该行"))
            guiInteract.ClickItemInspectionListRow(itemInspectionListRowIndex);
        ImGui.InputText("界面点击过滤", ref uiClickFilter, 64);
        ImGui.SameLine();
        if (ImGui.Button("复制可用的界面点击项"))
            ImGui.SetClipboardText(string.Join("\n", guiInteract.ListAvailableUiClicks(uiClickFilter)));
        ImGui.InputText("界面点击名称", ref uiClickName, 128);
        ImGui.SameLine();
        if (ImGui.Button("发送界面点击##inspectAddon"))
            guiInteract.SendUiClick(inspectAddonName, uiClickName);

        ImGui.Separator();

        // Generic AtkComponentList row select/click — same mechanics verified on ItemInspectionList,
        // parameterized by addon name + list node ID so it works on new addons like SkyIslandExchange2.
        ImGui.TextUnformatted("通用界面列表测试：");
        ImGui.InputText("列表界面名", ref genericListAddonName, 64);
        ImGui.InputInt("列表节点 ID", ref genericListNodeId);
        if (ImGui.Button("导出列表行"))
            ImGui.SetClipboardText(guiInteract.DumpAddonListRows(genericListAddonName, (uint)genericListNodeId));
        ImGui.InputInt("列表行号", ref genericListRowIndex);
        ImGui.SameLine();
        ImGui.Checkbox("派发事件##genericList", ref genericListDispatchEvent);
        if (ImGui.Button("选中列表行"))
            guiInteract.SelectAddonListRow(genericListAddonName, (uint)genericListNodeId, genericListRowIndex, genericListDispatchEvent);
        ImGui.SameLine();
        if (ImGui.Button("点击列表行"))
            guiInteract.ClickAddonListRow(genericListAddonName, (uint)genericListNodeId, genericListRowIndex);

        var mousePos = ImGui.GetMousePos();
        ImGui.TextUnformatted($"鼠标：{mousePos.X:F0}, {mousePos.Y:F0}");
        ImGui.Checkbox("实时检查鼠标下的游戏节点", ref liveInspectHoveredNodes);
        if (liveInspectHoveredNodes)
            hoveredNodeInspection = guiInteract.InspectNodesAt(mousePos.X, mousePos.Y);
        if (ImGui.Button("检查鼠标下的游戏节点"))
            hoveredNodeInspection = guiInteract.InspectNodesAt(mousePos.X, mousePos.Y);
        ImGui.SameLine();
        if (ImGui.Button("3 秒后抓取"))
            delayedHoverCaptureAt = DateTime.UtcNow.AddSeconds(3);
        if (delayedHoverCaptureAt is { } captureAt)
        {
            double secondsLeft = (captureAt - DateTime.UtcNow).TotalSeconds;
            if (secondsLeft <= 0)
            {
                var capturePos = ImGui.GetMousePos();
                hoveredNodeInspection = guiInteract.InspectNodesAt(capturePos.X, capturePos.Y);
                delayedHoverCaptureAt = null;
            }
            else
            {
                ImGui.SameLine();
                ImGui.TextUnformatted($"{secondsLeft:F1} 秒后抓取");
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("复制悬停节点报告"))
            ImGui.SetClipboardText(hoveredNodeInspection);
        if (!string.IsNullOrWhiteSpace(hoveredNodeInspection))
            ImGui.TextUnformatted(hoveredNodeInspection.Split('\n')[0]);

        if (ImGui.Button("切换鉴定列表事件日志"))
            guiInteract.ToggleItemInspectionEventLogging();
        ImGui.SameLine();
        if (ImGui.Button("复制最近一次物品事件"))
            ImGui.SetClipboardText(guiInteract.RecentItemInspectionEvents);
        ImGui.TextUnformatted(guiInteract.LastItemInspectionEvent);
    }
}
