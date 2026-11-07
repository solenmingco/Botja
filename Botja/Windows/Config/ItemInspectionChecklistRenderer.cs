using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Ocelot.Config.Fields;
using Ocelot.Config.Renderers;
using Ocelot.Services.Translation;

namespace Botja.Windows.Config;

public sealed class ItemInspectionChecklistAttribute()
    : UIFieldAttribute(typeof(ItemInspectionChecklistRenderer));

public sealed class ItemInspectionChecklistRenderer(IDataManager data) : IFieldRenderer<ItemInspectionChecklistAttribute>
{
    private string GetItemName(int itemId)
    {
        var item = data.GetExcelSheet<Lumina.Excel.Sheets.Item>()?.GetRowOrDefault((uint)itemId);
        return item is { } row && !string.IsNullOrWhiteSpace(row.Name.ToString())
            ? row.Name.ToString()
            : $"物品 ID {itemId}";
    }

    public bool Render(object target, PropertyInfo prop, ItemInspectionChecklistAttribute attr, Type owner, ITranslator translator)
    {
        if (prop.PropertyType != typeof(HashSet<int>))
        {
            throw new InvalidOperationException(
                $"[ItemInspectionChecklist] can only be used on HashSet<int> properties. {prop.DeclaringType?.Name}.{prop.Name} is {prop.PropertyType.Name}.");
        }

        var skippedIds = (HashSet<int>?)prop.GetValue(target) ?? [];
        var changed = false;

        ImGui.TextUnformatted("在鉴定列表里勾选物品左侧的复选框，就会加到这里。");
        if (skippedIds.Count == 0)
        {
            ImGui.TextUnformatted("当前没有跳过任何物品。");
            return false;
        }

        if (ImGui.Button("清空跳过列表"))
        {
            skippedIds.Clear();
            changed = true;
        }

        foreach (var itemId in skippedIds.OrderBy(id => GetItemName(id), StringComparer.OrdinalIgnoreCase).ToArray())
        {
            var skipped = true;
            if (!ImGui.Checkbox($"{GetItemName(itemId)}（ID {itemId}）##{prop.Name}_{itemId}", ref skipped))
                continue;

            skippedIds.Remove(itemId);
            changed = true;
        }

        if (changed)
            prop.SetValue(target, skippedIds);

        return changed;
    }
}