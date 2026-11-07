using System;
using System.Collections.Generic;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Ocelot.Config.Fields;
using Ocelot.Config.Renderers;
using Ocelot.Extensions;
using Ocelot.Services.Translation;
using Ocelot.UI;

namespace Botja.Windows.Config;

public sealed class MountSelectAttribute() : UIFieldAttribute(typeof(MountSelectRenderer));

// IUnlockState 在 Dalamud 里标了 [Experimental("Dalamud001")]，但这是官方唯一一个
// 「这个坐骑我有没有」的查询入口（Mount 表里全是未解锁的坐骑，直接列出来没法用）。
#pragma warning disable Dalamud001
public sealed class MountSelectRenderer(IServiceProvider services, IDataManager data) : IFieldRenderer<MountSelectAttribute>
{
    // 0 是「随机坐骑」，对应游戏里的坐骑轮盘（ActionType.GeneralAction 9）。
    private const uint RouletteId = 0;
    private const string RouletteLabel = "随机（坐骑轮盘）";

    private readonly List<(uint Id, string Name)> entries = [];
    private bool loaded;
    private string search = string.Empty;
    private IUnlockState? unlockState;
    private bool unlockResolved;

    public bool Render(object target, PropertyInfo prop, MountSelectAttribute attr, Type owner, ITranslator translator)
    {
        if (prop.PropertyType != typeof(uint))
        {
            throw new InvalidOperationException(
                $"[MountSelect] can only be used on uint properties. {prop.DeclaringType?.Name}.{prop.Name} is {prop.PropertyType.Name}.");
        }

        EnsureLoaded();

        var value = (uint)(prop.GetValue(target) ?? 0u);
        var label = prop.Label(owner, translator);
        var order = new List<(uint Id, string Name)> { (RouletteId, RouletteLabel) };
        order.AddRange(entries);

        var changed = false;
        OcelotUi.PushFieldStyle();
        try
        {
            ImGui.SetNextItemWidth(MathF.Max(220f, ImGui.GetFontSize() * 16f));
            if (ImGui.BeginCombo(label, NameOf(value)))
            {
                if (ImGui.IsWindowAppearing())
                {
                    search = string.Empty;
                    ImGui.SetKeyboardFocusHere();
                }

                ImGui.SetNextItemWidth(-1f);
                ImGui.InputTextWithHint("##mount_search", "搜索坐骑名称…", ref search, 64);

                var listHeight = MathF.Max(120f, ImGui.GetFontSize() * 14f);
                if (ImGui.BeginChild("##mount_list", new System.Numerics.Vector2(0f, listHeight), true))
                {
                    var any = false;
                    foreach (var entry in order)
                    {
                        if (!Matches(entry.Name))
                            continue;

                        any = true;
                        if (!ImGui.Selectable($"{entry.Name}##mount_{entry.Id}", entry.Id == value))
                            continue;

                        prop.SetValue(target, entry.Id);
                        value = entry.Id;
                        changed = true;
                        ImGui.CloseCurrentPopup();
                        break;
                    }

                    if (!any)
                        ImGui.TextUnformatted("没有匹配的坐骑。");
                }

                ImGui.EndChild();
                ImGui.EndCombo();
            }

            prop.Tooltip(owner, translator);

            if (value != RouletteId && !entries.Exists(entry => entry.Id == value))
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.75f, 0.2f, 1f), "该坐骑当前不可用（未解锁 / 不在可选列表里），会自动改用坐骑轮盘。");

            if (prop.Name == nameof(MountConfig.PreferredMountId))
                ImGui.TextDisabled($"可选坐骑 {entries.Count} 个");
        }
        finally
        {
            OcelotUi.PopFieldStyle();
        }

        return changed;
    }

    private string NameOf(uint id)
    {
        if (id == RouletteId)
            return RouletteLabel;

        foreach (var entry in entries)
        {
            if (entry.Id == id)
                return entry.Name;
        }

        // 配置里存了一个现在已经不可用的坐骑（未解锁 / 表数据变了），不要把下拉框显示成空的。
        return $"坐骑 ID {id}（不可用）";
    }

    private bool Matches(string name) =>
        search.Length == 0 || name.Contains(search, StringComparison.CurrentCultureIgnoreCase);

    private void EnsureLoaded()
    {
        if (loaded)
            return;

        loaded = true;

        var sheet = data.GetExcelSheet<Mount>();
        if (sheet is null)
            return;

        var all = new List<(uint Id, string Name)>();
        var unlocked = new List<(uint Id, string Name)>();

        foreach (var row in sheet)
        {
            if (row.RowId == 0)
                continue;

            var name = row.Singular.ToString();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            all.Add((row.RowId, name));
            if (IsUnlocked(row))
                unlocked.Add((row.RowId, name));
        }

        // 没登录 / 解锁状态查不出来时 unlocked 会是空的 —— 这时退回「全部坐骑」，
        // 至少让用户能选，选错了运行时还有坐骑轮盘兜底。
        var source = unlocked.Count > 0 ? unlocked : all;
        source.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
        entries.AddRange(source);
    }

    private bool IsUnlocked(Mount row)
    {
        if (!unlockResolved)
        {
            unlockResolved = true;
            try
            {
                unlockState = services.GetService(typeof(IUnlockState)) as IUnlockState;
            }
            catch
            {
                unlockState = null;
            }
        }

        // 查不到解锁状态（老版本 Dalamud / 服务没注册 / 还没登录）时就别过滤，把所有坐骑都列出来。
        if (unlockState is null)
            return true;

        try
        {
            return unlockState.IsMountUnlocked(row);
        }
        catch
        {
            return false;
        }
    }
}
#pragma warning restore Dalamud001
