using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Botja.Services;

/// <summary>
/// One row of the Resistance Recruitment window, as the game itself sees it. Read straight out of
/// <see cref="AgentMycBattleAreaInfo"/> so it is independent of the Lumina sheet and of any
/// translated text we would otherwise have to pattern-match.
/// </summary>
public readonly record struct CeWindowRow(int Index, ushort EventId, string Name, MycDynamicEventState State, uint SecondsLeft, ushort Participants);

/// <summary>Which of the two clicks the signup flow wants to perform.</summary>
public enum CeButtonIntent
{
    /// <summary>The button that enrols us while the CE is still recruiting.</summary>
    Register,

    /// <summary>The button that warps us in once the CE is preparing to start.</summary>
    Commence,
}

/// <summary>
/// Everything that touches the Resistance Recruitment window. Split out from the rest of
/// <see cref="GuiInteractionService"/> because the original implementation matched buttons by their
/// English label only, which silently never matched on the CN/KR/JP clients and left the signup
/// state machine waiting forever.
///
/// Buttons are now located structurally (row index / event param / position) and the localised
/// label list is only a fallback, so a client whose button text we have never seen still works.
/// </summary>
public unsafe partial class GuiInteractionService
{
    private DateTime ceNextOpenAttempt = DateTime.MinValue;

    // Fallbacks only. They exist for clients whose window lays buttons out in an order we cannot
    // infer, and for the (rare) case of a window showing exactly one button.
    private static readonly string[] RegisterLabels =
    [
        "Register", "Request Deployment", "Deploy", "Join",
        "报名", "報名", "参加", "參加", "参战", "參戰", "出击", "出擊",
        "エントリー", "参加登録", "参加する",
    ];

    private static readonly string[] CommenceLabels =
    [
        "Commence", "Enter", "Deploy Now", "Proceed", "Begin", "Start", "Join",
        "出击", "出擊", "进入", "進入", "参战", "參戰", "参加", "參加",
        "出撃", "開始", "参加する",
    ];

    // Never treat a dismissal control as "the" registration button, even when it is the only
    // enabled one left in the window.
    private static readonly string[] DismissLabels =
    [
        "Close", "Cancel", "Back", "Dismiss",
        "关闭", "關閉", "取消", "返回",
        "閉じる", "キャンセル", "戻る",
    ];

    public static AgentMycBattleAreaInfo* GetCeRecruitmentAgent()
    {
        var agentModule = AgentModule.Instance();
        return agentModule == null
            ? null
            : (AgentMycBattleAreaInfo*)agentModule->GetAgentByInternalId(AgentId.MycBattleAreaInfo);
    }

    /// <summary>The addon the recruitment agent owns, if it is currently open and fully loaded.</summary>
    public AtkUnitBase* GetCeRecruitmentAddon()
    {
        var agent = GetCeRecruitmentAgent();
        if (agent == null || !((AgentInterface*)agent)->IsAgentActive())
            return null;

        uint addonId = ((AgentInterface*)agent)->GetAddonId();
        if (addonId == 0)
            return null;

        var manager = RaptureAtkUnitManager.Instance();
        var addon = manager == null ? null : manager->GetAddonById(checked((ushort)addonId));
        return addon != null && addon->IsVisible && addon->UldManager.LoadedState == AtkLoadState.Loaded
            ? addon
            : null;
    }

    public bool IsCeRecruitmentWindowOpen() => GetCeRecruitmentAddon() != null;

    /// <summary>
    /// Opens the recruitment window, retrying at most once every two seconds. The original code
    /// latched a single attempt, so one failed Show() left signup stuck until the whole flow timed
    /// out five minutes later.
    /// </summary>
    public bool EnsureCeRecruitmentWindowOpen()
    {
        if (IsCeRecruitmentWindowOpen())
        {
            ceNextOpenAttempt = DateTime.MinValue;
            return true;
        }

        if (DateTime.UtcNow < ceNextOpenAttempt)
            return false;

        ceNextOpenAttempt = DateTime.UtcNow.AddSeconds(2);
        return OpenCeRecruitmentWindow();
    }

    public bool OpenCeRecruitmentWindow()
    {
        var agent = GetCeRecruitmentAgent();
        if (agent == null)
            return false;

        ((AgentInterface*)agent)->Show();
        return IsCeRecruitmentWindowOpen();
    }

    /// <summary>Rows currently listed by the game's own recruitment window.</summary>
    public IReadOnlyList<CeWindowRow> GetCeRecruitmentRows()
    {
        var rows = new List<CeWindowRow>();

        var agent = GetCeRecruitmentAgent();
        if (agent == null)
            return rows;

        var data = agent->MycDynamicEventData;
        if (data == null)
            return rows;

        int index = 0;
        foreach (ref var ev in data->Array)
        {
            if (ev.Id != 0 && ev.State != MycDynamicEventState.None)
                rows.Add(new CeWindowRow(index, ev.Id, ev.Name.ToString(), ev.State, ev.TimeLeft, ev.ParticipantCount));

            index++;
        }

        return rows;
    }

    public string? GetCeRecruitmentRowName(ushort eventId)
        => GetCeRecruitmentRows().FirstOrDefault(row => row.EventId == eventId).Name;

    /// <summary>
    /// Clicks the registration / deployment button belonging to <paramref name="eventId"/>.
    /// Returns false with a human-readable reason in <paramref name="detail"/> — that reason is what
    /// surfaces in the window status and the log, so a mismatch is diagnosable instead of silent.
    /// </summary>
    public bool TryClickCeButton(ushort eventId, CeButtonIntent intent, out string detail)
    {
        detail = string.Empty;

        var addon = GetCeRecruitmentAddon();
        if (addon == null)
        {
            detail = "recruitment window not open";
            return false;
        }

        var rows = GetCeRecruitmentRows();
        int rowIndex = rows.Count == 0 ? -1 : rows.ToList().FindIndex(row => row.EventId == eventId);

        var buttons = CollectCeButtons(addon, includeDisabled: false)
            .Where(button => !IsDismissLabel(button.Text))
            .ToList();

        if (buttons.Count == 0)
        {
            detail = $"no enabled button (rows={rows.Count})";
            return false;
        }

        CeButtonRef? pick = null;
        string reason = string.Empty;

        // 1. The button carries the row index in its event param — the most reliable signal, and
        //    the one the game's own UI uses to dispatch the click.
        if (rowIndex >= 0)
        {
            var byParam = buttons.FirstOrDefault(button => button.Param == rowIndex);
            if (byParam != null)
            {
                pick = byParam;
                reason = $"event param {rowIndex}";
            }
        }

        // 2. Some clients carry the event id itself in the param instead of the row index.
        if (pick == null)
        {
            var byEventId = buttons.FirstOrDefault(button => button.Param == eventId);
            if (byEventId != null)
            {
                pick = byEventId;
                reason = $"event param #{eventId}";
            }
        }

        // 3. Exactly one clickable row and one button: nothing to disambiguate.
        if (pick == null && rows.Count == 1 && buttons.Count == 1)
        {
            pick = buttons[0];
            reason = "single row, single button";
        }

        // 4. Localised label match.
        if (pick == null)
        {
            var labels = intent == CeButtonIntent.Register ? RegisterLabels : CommenceLabels;
            pick = buttons.FirstOrDefault(button => labels.Any(label => button.Text.Trim().Equals(label, StringComparison.OrdinalIgnoreCase)));
            if (pick != null)
                reason = $"label '{pick.Text}'";
        }

        // 5. Window shows one button per row and they are in row order.
        if (pick == null && rowIndex >= 0 && buttons.Count == rows.Count && rowIndex < buttons.Count)
        {
            pick = buttons[rowIndex];
            reason = $"position {rowIndex} of {rows.Count}";
        }

        // 6. Only one enabled button at all — take it rather than sit idle.
        if (pick == null && buttons.Count == 1)
        {
            pick = buttons[0];
            reason = "only enabled button";
        }

        if (pick == null)
        {
            detail = "no matching button | rows=" + string.Join(", ", rows.Select(row => $"#{row.Index} id={row.EventId} {row.State} '{row.Name}'")) +
                     " | buttons=" + string.Join(", ", buttons.Select(button => $"'{button.Text}'/p{button.Param}"));
            return false;
        }

        FireCeButton(addon, pick);
        detail = $"clicked '{pick.Text}' (param={pick.Param}, {reason})";
        return true;
    }

    /// <summary>Every button in the window, for the debug pane and the clipboard dump.</summary>
    public IReadOnlyList<string> GetCeRecruitmentButtons()
    {
        var addon = GetCeRecruitmentAddon();
        if (addon == null)
            return [];

        return CollectCeButtons(addon, includeDisabled: true)
            .Select(button => $"'{button.Text}' param={button.Param} type={button.EventType} enabled={button.Button->IsEnabled}")
            .ToList();
    }

    /// <summary>Clicks a button by its exact on-screen text, whatever row it belongs to.</summary>
    public bool ClickCeRecruitmentButton(string label)
    {
        var addon = GetCeRecruitmentAddon();
        if (addon == null || string.IsNullOrWhiteSpace(label))
            return false;

        var button = CollectCeButtons(addon, includeDisabled: true)
            .FirstOrDefault(candidate => candidate.Text.Equals(label, StringComparison.OrdinalIgnoreCase));

        if (button == null)
            return false;

        FireCeButton(addon, button);
        return true;
    }

    /// <summary>
    /// Full state of the recruitment window, for the clipboard dump. Contains everything needed to
    /// work out why a click did not land: the agent's own row list plus every button's label,
    /// event param and enabled flag.
    /// </summary>
    public string DumpCeRecruitment()
    {
        var sb = new StringBuilder();

        var agent = GetCeRecruitmentAgent();
        if (agent == null)
        {
            sb.AppendLine("agent: unavailable (not in Bozja / Zadnor, or not loaded yet)");
            return sb.ToString();
        }

        var agentInterface = (AgentInterface*)agent;
        sb.AppendLine($"agent: active={agentInterface->IsAgentActive()} addonId={agentInterface->GetAddonId()}");

        var data = agent->MycDynamicEventData;
        sb.AppendLine(data == null ? "eventData: null" : $"eventData: count={data->Count}");

        var rows = GetCeRecruitmentRows();
        sb.AppendLine($"rows: {rows.Count}");
        foreach (var row in rows)
        {
            sb.AppendLine($"  #{row.Index} id={row.EventId} state={row.State} left={row.SecondsLeft}s ppl={row.Participants} name=\"{row.Name}\"");
        }

        var addon = GetCeRecruitmentAddon();
        if (addon == null)
        {
            sb.AppendLine("addon: not open");
            return sb.ToString();
        }

        sb.AppendLine($"addon: open id={addon->Id} visible={addon->IsVisible}");
        sb.AppendLine("buttons:");
        foreach (var button in CollectCeButtons(addon, includeDisabled: true))
        {
            sb.AppendLine($"  '{button.Text}' param={button.Param} type={button.EventType} enabled={button.Button->IsEnabled}");
        }

        sb.AppendLine("text nodes:");
        for (int index = 0; index < addon->UldManager.NodeListCount; index++)
        {
            var node = addon->UldManager.NodeList[index];
            if (node == null || node->Type != NodeType.Text || !node->IsVisible())
                continue;

            string text = ((AtkTextNode*)node)->NodeText.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                sb.AppendLine($"  \"{text}\"");
        }

        return sb.ToString();
    }

    private static bool IsDismissLabel(string text)
        => DismissLabels.Any(label => text.Trim().Equals(label, StringComparison.OrdinalIgnoreCase));

    private sealed class CeButtonRef
    {
        public AtkComponentButton* Button;
        public AtkResNode* OwnerNode;
        public AtkEvent* Event;
        public AtkEventType EventType;
        public int Param;
        public string Text = string.Empty;
    }

    private static List<CeButtonRef> CollectCeButtons(AtkUnitBase* addon, bool includeDisabled)
    {
        var buttons = new List<CeButtonRef>();
        Walk(&addon->UldManager, buttons, includeDisabled, 0);
        return buttons;

        static void Walk(AtkUldManager* manager, List<CeButtonRef> buttons, bool includeDisabled, int depth)
        {
            if (manager == null || manager->LoadedState != AtkLoadState.Loaded || manager->NodeList == null || depth > 6 || buttons.Count > 64)
                return;

            for (int index = 0; index < manager->NodeListCount; index++)
            {
                var node = manager->NodeList[index];
                if (node == null || !node->IsVisible() || (ushort)node->Type < 1000)
                    continue;

                var component = ((AtkComponentNode*)node)->Component;
                if (component == null)
                    continue;

                if (component->GetComponentType() == ComponentType.Button)
                {
                    var button = (AtkComponentButton*)component;
                    var textNode = button->ButtonTextNode;
                    if (textNode != null && (includeDisabled || button->IsEnabled))
                    {
                        var reference = BuildButtonRef(button, textNode->NodeText.ToString());
                        if (reference != null)
                            buttons.Add(reference);
                    }
                }

                Walk(&component->UldManager, buttons, includeDisabled, depth + 1);
            }
        }
    }

    private static CeButtonRef? BuildButtonRef(AtkComponentButton* button, string text)
    {
        var ownerNode = button->AtkComponentBase.OwnerNode;
        if (ownerNode == null)
            return null;

        var atkEvent = ownerNode->AtkResNode.AtkEventManager.Event;
        for (int depth = 0; atkEvent != null && depth < 16; depth++)
        {
            if (atkEvent->State.EventType is AtkEventType.MouseClick or AtkEventType.ButtonClick)
                break;

            if (atkEvent->NextEvent == null)
                break;

            atkEvent = atkEvent->NextEvent;
        }

        if (atkEvent == null)
            return null;

        return new CeButtonRef
        {
            Button = button,
            OwnerNode = &ownerNode->AtkResNode,
            Event = atkEvent,
            EventType = atkEvent->State.EventType,
            Param = unchecked((int)atkEvent->Param),
            Text = text,
        };
    }

    private static void FireCeButton(AtkUnitBase* addon, CeButtonRef button)
    {
        var atkEvent = new AtkEvent
        {
            Node = button.OwnerNode,
            Listener = (AtkEventListener*)addon,
            Param = button.Event->Param,
        };

        var eventData = new AtkEventData();
        addon->ReceiveEvent(button.EventType, unchecked((int)button.Event->Param), &atkEvent, &eventData);
    }
}
