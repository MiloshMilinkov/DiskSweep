using DiskSweep.Core.Models;

namespace DiskSweep.Core.Safety;

public readonly record struct SelectionDecision(bool CanSelect, bool Preselect, string? BlockedReason)
{
    public static SelectionDecision Blocked(string reason) => new(false, false, reason);
}

/// <summary>Decides what may be ticked. Only Safe items are ever ticked automatically.</summary>
public static class SelectionPolicy
{
    public static SelectionDecision Evaluate(ScanItem item, SafetyGuard guard)
    {
        if (item.IsReportOnly || item.Kind == ItemKind.RegistryKey)
            return SelectionDecision.Blocked(item.ReportOnlyReason ?? "Report only. DiskSweep never edits the registry.");

        if (item.Kind == ItemKind.Program)
        {
            // Programs are only ever removed by launching their own uninstaller, never by deleting files.
            return string.IsNullOrWhiteSpace(item.UninstallCommand) && string.IsNullOrWhiteSpace(item.QuietUninstallCommand)
                ? SelectionDecision.Blocked("This program has no registered uninstaller.")
                : new SelectionDecision(true, false, null);
        }

        if (item.ContainsReparsePoint is not null)
            return SelectionDecision.Blocked($"Contains a junction, symbolic link or other reparse point: {item.ContainsReparsePoint}");

        var verdict = guard.Check(item.Path);
        if (!verdict.IsAllowed) return SelectionDecision.Blocked(verdict.Reason);

        return new SelectionDecision(true, item.Risk == RiskLevel.Safe, null);
    }
}

/// <summary>
/// A result row's tick box. Every attempt to tick re-runs the safety checks, so a protected path or a link
/// can't be selected no matter how the UI (or a bug in it) asks.
/// </summary>
public sealed class SelectableItem
{
    private readonly SafetyGuard _guard;

    public ScanItem Item { get; }
    public SelectionDecision Decision { get; private set; }
    public bool IsSelected { get; private set; }

    public SelectableItem(ScanItem item, SafetyGuard guard)
    {
        Item = item;
        _guard = guard;
        Decision = SelectionPolicy.Evaluate(item, guard);
        IsSelected = Decision.CanSelect && Decision.Preselect;
    }

    /// <summary>Returns the state actually applied. Unticking always works; ticking only when the checks pass right now.</summary>
    public bool TrySetSelected(bool selected)
    {
        if (!selected)
        {
            IsSelected = false;
            return false;
        }

        Decision = SelectionPolicy.Evaluate(Item, _guard);
        IsSelected = Decision.CanSelect;
        return IsSelected;
    }
}
