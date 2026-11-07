using Botja.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using Ocelot.Config.Fields;
using Ocelot.Config.Renderers;
using Ocelot.Services.Translation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;

namespace Botja.Windows.Config;

public sealed class MobTemplateChecklistAttribute() : UIFieldAttribute(typeof(MobTemplateChecklistRenderer));

public sealed class MobTemplateChecklistRenderer(HostileDetectionService hostiles)
    : IFieldRenderer<MobTemplateChecklistAttribute>
{
    public bool Render(object target, PropertyInfo prop, MobTemplateChecklistAttribute attr, Type owner, ITranslator translator)
    {
        var selected = (HashSet<uint>?)prop.GetValue(target) ?? [];
        var available = hostiles.GetNearbyHostiles()
            .GroupBy(hostile => hostile.DataId)
            .Select(group => group.First())
            .OrderBy(hostile => hostile.Name, StringComparer.Ordinal)
            .ToArray();

        ImGui.TextUnformatted("勾选当前附近检测到的怪物模板。");
        var changed = false;
        foreach (var hostile in available)
        {
            bool isSelected = selected.Contains(hostile.DataId);
            if (!ImGui.Checkbox($"{hostile.Name}（ID {hostile.DataId}）##mob_{hostile.DataId}", ref isSelected))
                continue;

            if (isSelected)
                selected.Add(hostile.DataId);
            else
                selected.Remove(hostile.DataId);
            changed = true;
        }

        foreach (uint selectedId in selected.Except(available.Select(hostile => hostile.DataId)).OrderBy(id => id).ToArray())
        {
            ImGui.TextUnformatted($"已选怪物 ID {selectedId} 当前没有检测到。");
            ImGui.SameLine();
            if (ImGui.SmallButton($"移除##mob_{selectedId}"))
            {
                selected.Remove(selectedId);
                changed = true;
            }
        }

        if (changed)
            prop.SetValue(target, selected);

        return changed;
    }
}

public sealed class FarmSpotListAttribute() : UIFieldAttribute(typeof(FarmSpotListRenderer));

public sealed class FarmSpotListRenderer(IObjectTable objects)
    : IFieldRenderer<FarmSpotListAttribute>
{
    public bool Render(object target, PropertyInfo prop, FarmSpotListAttribute attr, Type owner, ITranslator translator)
    {
        var spots = (List<MobFarmSpot>?)prop.GetValue(target) ?? [];
        bool changed = false;

        if (ImGui.Button("在当前位置添加刷怪点") && objects.LocalPlayer is { } player)
        {
            var spot = new MobFarmSpot { Name = $"刷怪点 {spots.Count + 1}" };
            spot.SetOrigin(player.Position);
            spots.Add(spot);
            changed = true;
        }

        for (int index = 0; index < spots.Count; index++)
        {
            var spot = spots[index];
            ImGui.PushID(index);
            if (ImGui.TreeNode($"{spot.Name}##spot"))
            {
                string name = spot.Name;
                if (ImGui.InputText("名称", ref name, 64))
                {
                    spot.Name = name;
                    changed = true;
                }

                bool enabled = spot.Enabled;
                if (ImGui.Checkbox("启用", ref enabled))
                {
                    spot.Enabled = enabled;
                    changed = true;
                }

                int priority = spot.Priority;
                if (ImGui.InputInt("优先级", ref priority))
                {
                    spot.Priority = priority;
                    changed = true;
                }

                Vector3 origin = spot.Origin;
                if (ImGui.InputFloat3("刷怪坐标", ref origin))
                {
                    spot.SetOrigin(origin);
                    changed = true;
                }

                if (ImGui.Button("把刷怪坐标设为当前位置") && objects.LocalPlayer is { } localPlayer)
                {
                    spot.SetOrigin(localPlayer.Position);
                    changed = true;
                }

                bool useStackPoint = spot.UseStackPoint;
                if (ImGui.Checkbox("使用聚怪点", ref useStackPoint))
                {
                    spot.UseStackPoint = useStackPoint;
                    changed = true;
                }

                if (spot.UseStackPoint)
                {
                    Vector3 stackPoint = spot.StackPoint ?? Vector3.Zero;
                    if (ImGui.InputFloat3("聚怪坐标", ref stackPoint))
                    {
                        spot.SetStackPoint(stackPoint);
                        changed = true;
                    }

                    if (ImGui.Button("把聚怪坐标设为当前位置") && objects.LocalPlayer is { } stackPlayer)
                    {
                        spot.SetStackPoint(stackPlayer.Position);
                        changed = true;
                    }
                }

                int minimum = spot.MinimumMobsToStartFight;
                if (ImGui.InputInt("开打阈值（0 = 用全局设置）", ref minimum))
                {
                    spot.MinimumMobsToStartFight = Math.Max(0, minimum);
                    changed = true;
                }

                if (ImGui.Button("删除刷怪点"))
                {
                    spots.RemoveAt(index);
                    index--;
                    changed = true;
                }

                ImGui.TreePop();
            }
            ImGui.PopID();
        }

        if (changed)
            prop.SetValue(target, spots);

        return changed;
    }
}