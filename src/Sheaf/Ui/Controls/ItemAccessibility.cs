using System.Windows.Forms.Automation;

namespace Sheaf.Ui.Controls;

/// <summary>A control that draws a row of items itself (tabs, thumbnails, segments, list rows).</summary>
internal interface IItemHost
{
    int ItemCount { get; }

    /// <summary>What a screen reader says for the item, such as "Page 3" or "Merge PDF, unsaved".</summary>
    string ItemName(int index);

    /// <summary>The item's box in the control's client coordinates.</summary>
    Rectangle ItemBounds(int index);

    bool IsItemSelected(int index);

    /// <summary>The item with the keyboard focus, or -1.</summary>
    int FocusedItem { get; }

    /// <summary>Does what clicking the item does.</summary>
    void ActivateItem(int index);
}

/// <summary>Speaks short messages through the screen reader in use (Narrator, NVDA, JAWS).</summary>
internal static class ScreenReader
{
    /// <summary>Says <paramref name="text"/> on behalf of <paramref name="control"/>, replacing anything it said before.</summary>
    public static void Say(Control control, string text)
    {
        if (!control.IsHandleCreated || text.Length == 0) return;
        control.AccessibilityObject.RaiseAutomationNotification(AutomationNotificationKind.Other, AutomationNotificationProcessing.MostRecent, text);
    }
}

/// <summary>
/// The accessible object of a control whose state reads best as a sentence (the page area, the scan view): its
/// help text, which readers say after the name and role, comes from <paramref name="describe"/>.
/// </summary>
internal sealed class DescribedAccessible(Control owner, Func<string?> describe) : Control.ControlAccessibleObject(owner)
{
    public override string? Description => describe();

    public override string? Help => describe();
}

/// <summary>
/// Makes the items of an <see cref="IItemHost"/> reachable for screen readers. The current item is the control's
/// description (read when it gets the focus) and is announced as it changes; for readers that use MSAA each item
/// is also a child with its name, place on screen and selection. The owner must be a <see cref="Panel"/> (or
/// another control with UI Automation support) or nothing reaches UI Automation at all.
/// </summary>
internal sealed class ItemHostAccessible(Control owner, IItemHost host, AccessibleRole itemRole) : Control.ControlAccessibleObject(owner)
{
    Control Control => Owner!;

    int Current
    {
        get
        {
            int item = host.FocusedItem;
            if (item < 0 || item >= host.ItemCount) item = Enumerable.Range(0, host.ItemCount).FirstOrDefault(host.IsItemSelected, -1);
            return item;
        }
    }

    /// <summary>"Page 3, 3 of 6, selected".</summary>
    public string Spoken(int index) =>
        $"{host.ItemName(index)}, {index + 1} of {host.ItemCount}{(host.IsItemSelected(index) ? ", selected" : "")}";

    public override string? Description => Current is int item and >= 0 ? Spoken(item) : Control.AccessibleDescription;

    /// <summary>UI Automation's help text, which readers say after the name and role.</summary>
    public override string? Help => Description;

    public override int GetChildCount() => host.ItemCount;

    public override AccessibleObject? GetChild(int index) =>
        index >= 0 && index < host.ItemCount ? new ItemAccessible(this, Control, host, index, itemRole) : null;

    public override AccessibleObject? GetFocused() =>
        Control.Focused && host.FocusedItem >= 0 ? GetChild(host.FocusedItem) : base.GetFocused();

    public override AccessibleObject? GetSelected() => Current >= 0 ? GetChild(Current) : base.GetSelected();

    public override AccessibleObject? HitTest(int x, int y)
    {
        Point client = Control.PointToClient(new Point(x, y));
        for (int i = 0; i < host.ItemCount; i++)
            if (host.ItemBounds(i).Contains(client)) return GetChild(i);
        return base.HitTest(x, y);
    }

    /// <summary>Call when the focused or selected item changes, and when the control gets the focus.</summary>
    public static void ItemFocused(Control owner, int index)
    {
        if (!owner.IsHandleCreated || owner.AccessibilityObject is not ItemHostAccessible accessible) return;
        if (index < 0 || index >= accessible.GetChildCount()) return;
        accessible.NotifyClients(AccessibleEvents.Selection, index);
        accessible.NotifyClients(AccessibleEvents.DescriptionChange);
        if (!owner.Focused) return;
        accessible.NotifyClients(AccessibleEvents.Focus, index);
        ScreenReader.Say(owner, accessible.Spoken(index));
    }

    sealed class ItemAccessible(ItemHostAccessible parent, Control owner, IItemHost host, int index, AccessibleRole role) : AccessibleObject
    {
        public override string? Name => host.ItemName(index);

        public override AccessibleRole Role => role;

        public override AccessibleObject Parent => parent;

        public override Rectangle Bounds => owner.RectangleToScreen(host.ItemBounds(index));

        public override string DefaultAction => role == AccessibleRole.PageTab ? "Switch" : "Select";

        public override AccessibleStates State
        {
            get
            {
                AccessibleStates state = AccessibleStates.Selectable | AccessibleStates.Focusable;
                if (host.IsItemSelected(index)) state |= AccessibleStates.Selected;
                if (owner.Focused && host.FocusedItem == index) state |= AccessibleStates.Focused;
                if (!owner.ClientRectangle.IntersectsWith(host.ItemBounds(index))) state |= AccessibleStates.Offscreen;
                return state;
            }
        }

        public override void DoDefaultAction() => host.ActivateItem(index);

        public override void Select(AccessibleSelection flags)
        {
            if (flags.HasFlag(AccessibleSelection.TakeFocus)) owner.Focus();
            if (flags.HasFlag(AccessibleSelection.TakeSelection) || flags.HasFlag(AccessibleSelection.AddSelection)) host.ActivateItem(index);
        }

        public override AccessibleObject? Navigate(AccessibleNavigation direction) => direction switch
        {
            AccessibleNavigation.Next or AccessibleNavigation.Down or AccessibleNavigation.Right => parent.GetChild(index + 1),
            AccessibleNavigation.Previous or AccessibleNavigation.Up or AccessibleNavigation.Left => parent.GetChild(index - 1),
            _ => base.Navigate(direction),
        };
    }
}
