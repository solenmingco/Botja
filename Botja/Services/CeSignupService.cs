using Dalamud.Game.Chat;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using LuminaDynamicEvent = Lumina.Excel.Sheets.DynamicEvent;
using Ocelot.Config;
using Ocelot.Lifecycle;
using System;
using System.Linq;

namespace Botja.Services;

/// <summary>
/// Drives the game's own Resistance Recruitment window to enrol in a Critical Engagement.
///
/// Bozja/Zadnor CEs cannot be joined by simply standing in the area the way a skirmish can — the
/// player has to open the recruitment window and press its button while the CE is inside its
/// registration window, then confirm again once the CE starts preparing. That makes this the one
/// part of Botja that has to touch the UI, and the previous implementation broke on any client
/// whose buttons are not labelled in English: it matched the on-screen text against a hard-coded
/// list, never matched, and then sat in "Waiting for the recruitment window" until the timeout
/// fired. Button targeting now lives in <see cref="GuiInteractionService.TryClickCeButton"/> and
/// keys off the window's own row data instead of its text.
/// </summary>
public sealed unsafe class CeSignupService(
    GuiInteractionService guiInteract,
    IDataManager data,
    IChatGui chatGui,
    IObjectTable objects,
    IPluginConfig pluginConfig,
    IConfigSaver configSaver,
    IPluginLog log,
    AutoModeState autoModeState) : IOnUpdate, IOnLoad, IOnStop
{
    private static readonly TimeSpan ActionDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromMinutes(6);

    private const uint LargeScaleEventType = 4;
    private const uint SoloEngagementEnemyType = 3;

    // The game announces a nearby CE in chat before it shows up in the DynamicEventContainer; treat
    // it as a short-lived hint so Auto Mode can start watching before the container catches up.
    // Localised, because the CN client obviously does not print the English string.
    private static readonly string[] CeAnnouncementTexts =
    [
        "critical engagement",
        "紧急遭遇战", "緊急遭遇戰", "特殊场景探索",
        "クリティカルエンゲージメント",
        "kritische begegnung",
    ];

    private static readonly TimeSpan CeAnnouncementWindow = TimeSpan.FromMinutes(2);
    private DateTime? ceAnnouncedAt;

    private enum Phase
    {
        Idle,
        Opening,
        WaitingForCommence,
        ConfirmingCommence,
    }

    private enum CePhase
    {
        Unknown,
        Registering,
        Commencing,
        Underway,
        Unavailable,
    }

    private readonly record struct TargetState(CePhase Phase, uint SecondsLeft, string Name)
    {
        public static TargetState Unknown => new(CePhase.Unknown, 0, string.Empty);
    }

    // Guards against hammering the window: Bozja registration completes silently (no SelectYesno),
    // so without a bound a click that silently does nothing would repeat every half second for the
    // whole timeout.
    private const int MaxClicks = 40;
    private const int MaxCommenceRetries = 6;

    private Phase phase;
    private DateTime startedAt;
    private DateTime nextActionAt;
    private DateTime lastClickAt = DateTime.MinValue;
    private int clickCount;
    private int commenceRetries;
    private ushort targetEventId;

    public string Status { get; private set; } = "Idle";

    public bool IsRunning => phase != Phase.Idle;

    /// <summary>Human-readable detail of the last click attempt, surfaced next to <see cref="Status"/>.</summary>
    public string LastAction { get; private set; } = string.Empty;

    public string DetectedTimer => GetDetectedTimer();

    // Set once a CE's Commence is confirmed; stays set (even after Stop/Idle) until a caller
    // consumes it, so CombatControlService can pick up the newly-active CE on the next tick.
    public ushort LastCommencedEventId { get; private set; }

    // True for a short window after chat announces a nearby CE.
    public bool HasRecentCeAnnouncement => ceAnnouncedAt is { } at && DateTime.UtcNow - at < CeAnnouncementWindow;

    public void OnLoad() => chatGui.ChatMessage += OnChatMessage;

    public void OnStop() => chatGui.ChatMessage -= OnChatMessage;

    private void OnChatMessage(IHandleableChatMessage message)
    {
        var text = message.Message.TextValue;
        if (CeAnnouncementTexts.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            ceAnnouncedAt = DateTime.UtcNow;
    }

    public void Start()
    {
        phase = Phase.Opening;
        startedAt = DateTime.UtcNow;
        nextActionAt = DateTime.MinValue;
        targetEventId = 0;
        LastAction = string.Empty;
        lastClickAt = DateTime.MinValue;
        clickCount = 0;
        commenceRetries = 0;
        Status = "Opening Resistance Recruitment.";
    }

    public void Stop()
    {
        phase = Phase.Idle;
        Status = "Stopped";
    }

    public void Update()
    {
        if (phase == Phase.Idle || DateTime.UtcNow < nextActionAt)
            return;

        nextActionAt = DateTime.UtcNow + ActionDelay;

        if (objects.LocalPlayer?.IsDead == true)
        {
            StopWithStatus("Paused: player is dead.");
            return;
        }

        if (DateTime.UtcNow - startedAt > AttemptTimeout)
        {
            StopWithStatus("Stopped: CE signup timed out.");
            return;
        }

        switch (phase)
        {
            case Phase.Opening:
                UpdateOpening();
                break;

            case Phase.WaitingForCommence:
                UpdateWaitingForCommence();
                break;

            case Phase.ConfirmingCommence:
                UpdateConfirmingCommence();
                break;
        }
    }

    private void UpdateOpening()
    {
        if (targetEventId == 0 && !TryFindRecruitingEvent(out targetEventId, includeWarmup: true))
        {
            Status = "Waiting for a joinable CE (non-solo, non-large-scale).";
            return;
        }

        // The game can warp us in on its own (or we may already be inside one) — nothing to click.
        if (TryGetCurrentEventId(out var currentEventId) && currentEventId == targetEventId)
        {
            LastCommencedEventId = currentEventId;
            StopWithStatus("Already inside the CE.");
            return;
        }

        var state = GetTargetState();
        if (state.Phase is CePhase.Unavailable or CePhase.Underway)
        {
            targetEventId = 0;
            Status = "CE is no longer joinable; looking for another.";
            return;
        }

        if (!guiInteract.EnsureCeRecruitmentWindowOpen())
        {
            Status = "Waiting for the recruitment window.";
            return;
        }

        if (state.Phase == CePhase.Commencing)
        {
            Advance(Phase.WaitingForCommence, "CE warmup already started; waiting for the deployment timer.");
            return;
        }

        if (guiInteract.TryClickCeButton(targetEventId, CeButtonIntent.Register, out var detail))
        {
            LastAction = detail;
            lastClickAt = DateTime.UtcNow;
            clickCount++;
            log.Debug("[CE] register #{Id}: {Detail}", targetEventId, detail);

            if (clickCount > MaxClicks)
            {
                StopWithStatus("Stopped: the recruitment window keeps accepting clicks without registering — copy the CE dump from the debug pane.");
                return;
            }

            // Bozja registration normally completes silently; there is no confirmation dialog on
            // most clients. Answer one if it appears (handled in WaitingForCommence) but do not
            // block on it, which is what left the old flow stuck here forever.
            Advance(Phase.WaitingForCommence, "Registration requested; waiting for the CE timer.");
        }
        else
        {
            LastAction = detail;
            log.Debug("[CE] could not click register for #{Id}: {Detail}", targetEventId, detail);
            Status = "Waiting for the recruitment window.";
        }
    }

    private void UpdateWaitingForCommence()
    {
        // Only right after one of our own clicks — this must never confirm an unrelated dialog.
        if (DateTime.UtcNow - lastClickAt < TimeSpan.FromSeconds(3) && guiInteract.ClickSelectYesnoYes())
            lastClickAt = DateTime.MinValue;

        var state = GetTargetState();

        if (state.Phase == CePhase.Underway)
        {
            LastCommencedEventId = targetEventId;
            StopWithStatus("CE commenced.");
            return;
        }

        if (state.Phase == CePhase.Unavailable)
        {
            StopWithStatus("Stopped: CE deployment is no longer available.");
            return;
        }

        Status = state.Phase == CePhase.Registering
            ? $"Registered; waiting for selection ({state.SecondsLeft}s)."
            : $"Registered; waiting for deployment ({state.SecondsLeft}s).";

        if (!ShouldCommenceNow(state, autoModeState.IsEngagedInFate))
            return;

        if (!guiInteract.EnsureCeRecruitmentWindowOpen())
        {
            Status = "Registered; waiting for the recruitment window.";
            return;
        }

        if (guiInteract.TryClickCeButton(targetEventId, CeButtonIntent.Commence, out var detail))
        {
            LastAction = detail;
            lastClickAt = DateTime.UtcNow;
            log.Debug("[CE] commence #{Id}: {Detail}", targetEventId, detail);
            Advance(Phase.ConfirmingCommence, "Commence requested; waiting for confirmation.");
            return;
        }

        // Some clients keep the very same button for both phases, so a second attempt at the
        // register click is a harmless fallback rather than a dead end.
        if (guiInteract.TryClickCeButton(targetEventId, CeButtonIntent.Register, out detail))
        {
            LastAction = detail;
            lastClickAt = DateTime.UtcNow;
            clickCount++;
            log.Debug("[CE] register (fallback) #{Id}: {Detail}", targetEventId, detail);
            Advance(Phase.WaitingForCommence, "Registration requested; waiting for the CE timer.");
            return;
        }

        LastAction = detail;

        // Nothing left to click and the battle is about to start — the game warps registered
        // players in by itself, and CombatControlService checks the current event directly anyway.
        if (state.Phase == CePhase.Commencing && state.SecondsLeft is > 0 and <= 3)
        {
            LastCommencedEventId = targetEventId;
            StopWithStatus("CE starting; expecting the game to warp us in.");
            return;
        }

        log.Debug("[CE] could not click commence for #{Id}: {Detail}", targetEventId, detail);
    }

    private void UpdateConfirmingCommence()
    {
        if (guiInteract.ClickSelectYesnoYes())
        {
            LastCommencedEventId = targetEventId;
            StopWithStatus("Commence confirmed.");
            return;
        }

        var state = GetTargetState();
        if (state.Phase == CePhase.Underway)
        {
            LastCommencedEventId = targetEventId;
            StopWithStatus("CE commenced.");
            return;
        }

        if (state.Phase == CePhase.Unavailable)
        {
            StopWithStatus("Stopped: CE deployment is no longer available.");
            return;
        }

        if (state.Phase == CePhase.Commencing && state.SecondsLeft is > 0 and <= 1)
        {
            LastCommencedEventId = targetEventId;
            StopWithStatus("CE starting; expecting the game to warp us in.");
            return;
        }

        // The click landed but we are still outside: retry a bounded number of times, then stop
        // with a status that says so instead of spinning until the six minute timeout.
        commenceRetries++;
        if (commenceRetries > MaxCommenceRetries)
        {
            StopWithStatus("Stopped: the deployment click is not taking effect — copy the CE dump from the debug pane.");
            return;
        }

        Advance(Phase.WaitingForCommence, $"Registered; waiting for deployment ({state.SecondsLeft}s).");
    }

    /// <summary>
    /// Whether to press the deployment button now. While a FATE is in progress we stay with it as
    /// long as there is time to spare, then join the CE for its final seconds; otherwise we go in
    /// as soon as the warmup starts.
    /// </summary>
    private static bool ShouldCommenceNow(TargetState state, bool engagedInFate)
    {
        if (state.Phase != CePhase.Commencing)
            return false;

        if (state.SecondsLeft <= 15)
            return true;

        return !engagedInFate;
    }

    // Scans for any CE currently open for registration, regardless of our own signup state —
    // used by AutoModeService to decide whether a CE should take priority over the next FATE.
    public bool TryFindRecruitingEvent(out ushort eventId, bool includeWarmup = false)
    {
        eventId = 0;

        var container = GetContainer();
        if (container == null)
            return false;

        // Prefer the CEs the recruitment window is actually listing, since those are the rows we
        // know how to click; then take the one with the least time left.
        var windowRows = guiInteract.GetCeRecruitmentRows();

        long bestScore = long.MaxValue;
        foreach (ref var dynamicEvent in container->Events)
        {
            if (dynamicEvent.DynamicEventId == 0)
                continue;

            bool joinable = dynamicEvent.State == DynamicEventState.Register ||
                            (includeWarmup && dynamicEvent.State == DynamicEventState.Warmup);

            if (!joinable || IsExcludedEvent(dynamicEvent.DynamicEventId))
                continue;

            // Copy the id out first: a lambda cannot capture a ref local.
            ushort candidateEventId = dynamicEvent.DynamicEventId;
            bool listed = windowRows.Any(row => row.EventId == candidateEventId);
            long score = (listed ? 0 : 1_000_000) + GetTimerSeconds(dynamicEvent);
            if (score >= bestScore)
                continue;

            bestScore = score;
            eventId = dynamicEvent.DynamicEventId;
        }

        return eventId != 0;
    }

    // True if the local player is currently inside an in-progress CE (Warmup/Battle) — regardless of
    // whether Botja's own signup flow was the one that got them there.
    public bool TryGetCurrentEventId(out ushort eventId)
    {
        eventId = 0;

        var container = GetContainer();
        if (container == null || container->CurrentEventIndex < 0 || container->CurrentEventId == 0)
            return false;

        foreach (ref var dynamicEvent in container->Events)
        {
            if (dynamicEvent.DynamicEventId != container->CurrentEventId)
                continue;

            if (dynamicEvent.State is not (DynamicEventState.Warmup or DynamicEventState.Battle))
                return false;

            eventId = container->CurrentEventId;
            return true;
        }

        return false;
    }

    private TargetState GetTargetState()
    {
        if (targetEventId == 0)
            return TargetState.Unknown;

        // The recruitment window's own data is what the buttons are bound to, so trust it first.
        foreach (var row in guiInteract.GetCeRecruitmentRows())
        {
            if (row.EventId != targetEventId)
                continue;

            return new TargetState(
                row.State switch
                {
                    MycDynamicEventState.Register => CePhase.Registering,
                    MycDynamicEventState.Commence => CePhase.Commencing,
                    MycDynamicEventState.Underway => CePhase.Underway,
                    _ => CePhase.Unavailable,
                },
                row.SecondsLeft,
                row.Name);
        }

        var container = GetContainer();
        if (container == null)
            return TargetState.Unknown;

        foreach (ref var dynamicEvent in container->Events)
        {
            if (dynamicEvent.DynamicEventId != targetEventId)
                continue;

            return new TargetState(
                dynamicEvent.State switch
                {
                    DynamicEventState.Register => CePhase.Registering,
                    DynamicEventState.Warmup => CePhase.Commencing,
                    DynamicEventState.Battle => CePhase.Underway,
                    _ => CePhase.Unavailable,
                },
                GetTimerSeconds(dynamicEvent),
                dynamicEvent.Name.ToString());
        }

        return new TargetState(CePhase.Unavailable, 0, string.Empty);
    }

    private static DynamicEventContainer* GetContainer()
    {
        try
        {
            return DynamicEventContainer.GetInstance();
        }
        catch
        {
            return null;
        }
    }

    private static uint GetTimerSeconds(in DynamicEvent dynamicEvent)
    {
        if (dynamicEvent.StartTimestamp > 0)
        {
            long remaining = dynamicEvent.StartTimestamp - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (remaining > 0)
                return (uint)Math.Min(remaining, uint.MaxValue);
        }

        return dynamicEvent.SecondsLeft;
    }

    private void Advance(Phase nextPhase, string status)
    {
        phase = nextPhase;
        Status = status;
    }

    private void StopWithStatus(string status)
    {
        phase = Phase.Idle;
        Status = status;
    }

    private bool IsExcludedEvent(ushort eventId)
    {
        var dynamicEvent = data.GetExcelSheet<LuminaDynamicEvent>()?.GetRowOrDefault(eventId);
        if (dynamicEvent is not { } row)
            return false;

        return row.EventType.RowId == LargeScaleEventType ||
               row.EnemyType.RowId == SoloEngagementEnemyType ||
               row.MaxParticipants == 1 ||
               pluginConfig.Blacklist.CeEventIds.Contains(eventId);
    }

    // eventId here is the DynamicEvent row ID (the CE's template/type ID, stable across every
    // occurrence of that CE) — not a per-instance runtime handle, so this blacklist persists.
    public bool IsCeBlacklisted(ushort eventId) => pluginConfig.Blacklist.CeEventIds.Contains(eventId);

    public void BlacklistCe(ushort eventId)
    {
        if (pluginConfig.Blacklist.CeEventIds.Contains(eventId))
            return;

        pluginConfig.Blacklist.CeEventIds = [.. pluginConfig.Blacklist.CeEventIds, eventId];
        log.Info("[Blacklist] Saving CE IDs: {CeEventIds}", string.Join(", ", pluginConfig.Blacklist.CeEventIds));
        configSaver.Save();
    }

    public void UnblacklistCe(ushort eventId)
    {
        if (!pluginConfig.Blacklist.CeEventIds.Remove(eventId))
            return;

        log.Info("[Blacklist] Saving CE IDs: {CeEventIds}", string.Join(", ", pluginConfig.Blacklist.CeEventIds));
        configSaver.Save();
    }

    public string GetCeName(ushort eventId)
    {
        foreach (var row in guiInteract.GetCeRecruitmentRows())
        {
            if (row.EventId == eventId && !string.IsNullOrWhiteSpace(row.Name))
                return row.Name;
        }

        var container = GetContainer();
        if (container != null)
        {
            foreach (ref var dynamicEvent in container->Events)
            {
                if (dynamicEvent.DynamicEventId != eventId)
                    continue;

                string name = dynamicEvent.Name.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }
        }

        var sheetRow = data.GetExcelSheet<LuminaDynamicEvent>()?.GetRowOrDefault(eventId);
        return sheetRow?.Name.ToString() ?? $"CE #{eventId}";
    }

    private string GetDetectedTimer()
    {
        var container = GetContainer();
        if (container == null)
            return "No CE timer detected";

        string selectedTimer = "No active CE timer";
        int selectedPriority = 0;

        foreach (ref var dynamicEvent in container->Events)
        {
            if (dynamicEvent.DynamicEventId == 0 || dynamicEvent.State == DynamicEventState.Inactive)
                continue;

            bool isCurrentEvent = container->CurrentEventIndex >= 0 && dynamicEvent.DynamicEventId == container->CurrentEventId;
            if (dynamicEvent.State == DynamicEventState.Battle && !isCurrentEvent)
                continue;

            int priority = isCurrentEvent
                ? 3
                : dynamicEvent.DynamicEventId == targetEventId ? 2 : 1;

            if (priority <= selectedPriority)
                continue;

            selectedPriority = priority;
            selectedTimer = $"CE #{dynamicEvent.DynamicEventId}: {GetTimerSeconds(dynamicEvent)}s ({dynamicEvent.State})";
        }

        return selectedTimer;
    }
}
