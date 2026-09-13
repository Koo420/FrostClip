using Frost.Shared.Hotkeys;
using Frost.Shared.Settings;

namespace Frost.Shared.Shell;

/// <summary>Why a proposed hotkey was refused.</summary>
public enum RebindRejection
{
    None = 0,

    /// <summary>Nothing was pressed, or only modifiers were.</summary>
    NoKey = 1,

    /// <summary>Another Frost action already uses it.</summary>
    AlreadyUsed = 2,

    /// <summary>
    /// A bare key with no modifier, which would fire while typing in chat.
    /// </summary>
    NeedsAModifier = 3,

    /// <summary>A combination Windows or the shell takes first.</summary>
    ReservedBySystem = 4,
}

/// <summary>Whether a rebind can go ahead.</summary>
/// <param name="Rejection">Why not, or <see cref="RebindRejection.None"/>.</param>
/// <param name="ConflictingAction">
/// The action that already owns the binding, when that is the reason.
/// </param>
/// <param name="Message">Text for the UI.</param>
public readonly record struct RebindCheck(
    RebindRejection Rejection,
    HotkeyAction? ConflictingAction,
    string? Message)
{
    public bool IsAllowed => Rejection == RebindRejection.None;
}

/// <summary>
/// Decides whether a key combination can be bound to an action.
/// </summary>
/// <remarks>
/// <para>Frost's hotkeys go through a low-level keyboard hook rather than
/// <c>RegisterHotKey</c>, so that they work under a fullscreen-exclusive game.
/// That choice is what makes this validation necessary: <c>RegisterHotKey</c>
/// refuses a combination another application already owns, and a hook does not.
/// A hook sees everything, which means Frost can bind a bare <c>G</c> and then
/// fire it every time the user types "gg" in chat — and the hook also does not
/// stop the keystroke reaching the game, so the user sees their character do
/// something and a clip get saved with no idea the two are related.</para>
///
/// <para>So bare keys are refused, with two deliberate exceptions: the function
/// keys F13–F24, which no keyboard sends without deliberate remapping and which
/// are exactly what a macro key on a gaming keyboard emits, and the print-screen
/// key, which is what people expect a clip button to be.</para>
/// </remarks>
public static class HotkeyRebind
{
    private const int PrintScreen = 0x2C;
    private const int F13 = 0x7C;
    private const int F24 = 0x87;
    private const int Escape = 0x1B;
    private const int Tab = 0x09;
    private const int Delete = 0x2E;
    private const int F4 = 0x73;
    private const int LeftWindows = 0x5B;
    private const int RightWindows = 0x5C;

    /// <summary>
    /// Checks a proposed binding against everything already bound.
    /// </summary>
    /// <param name="settings">The current settings, for the other assignments.</param>
    /// <param name="action">The action being rebound.</param>
    /// <param name="binding">What the user pressed.</param>
    /// <param name="clipSeconds">
    /// For a <see cref="HotkeyAction.SaveClip"/> rebind, which of several clip
    /// hotkeys is being edited — they are distinguished by duration, not action.
    /// </param>
    public static RebindCheck Check(
        FrostSettings settings,
        HotkeyAction action,
        HotkeyBinding binding,
        double? clipSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!binding.IsBound)
        {
            return Refuse(RebindRejection.NoKey, null, "Press a key combination.");
        }

        // A modifier on its own arrives as a binding whose key *is* the modifier.
        if (IsModifierKey(binding.VirtualKey))
        {
            return Refuse(
                RebindRejection.NoKey,
                null,
                "A modifier on its own is not a hotkey — add a key to it.");
        }

        if (IsReserved(binding))
        {
            return Refuse(
                RebindRejection.ReservedBySystem,
                null,
                "Windows takes that combination before Frost can see it.");
        }

        if (binding.Modifiers == HotkeyModifiers.None && !MayStandAlone(binding.VirtualKey))
        {
            // The specific harm: the hook does not swallow the keystroke, so the
            // key still reaches the game. The user's character does something,
            // a clip saves, and nothing connects the two.
            return Refuse(
                RebindRejection.NeedsAModifier,
                null,
                "Add Ctrl, Alt or Shift. A bare key would fire while you are typing " +
                "in chat, and the game would still receive it.");
        }

        foreach (var existing in settings.Hotkeys)
        {
            if (!HotkeyBinding.TryParse(existing.Binding, out var parsed) || parsed != binding)
            {
                continue;
            }

            var existingAction = (HotkeyAction)existing.Action;

            // Rebinding a hotkey to what it already is, is not a conflict with
            // itself. Clip hotkeys are told apart by duration because several
            // share the SaveClip action.
            var isSelf = existingAction == action
                && (action != HotkeyAction.SaveClip
                    || NearlyEqual(existing.ClipSeconds, clipSeconds));

            if (isSelf)
            {
                continue;
            }

            // A disabled assignment still holds its binding: it will conflict
            // the moment it is switched back on, and finding that out then is
            // worse than being told now.
            return Refuse(
                RebindRejection.AlreadyUsed,
                existingAction,
                $"{binding} is already bound to {Describe(existingAction, existing)}" +
                (existing.Enabled ? "." : " (currently turned off)."));
        }

        return new RebindCheck(RebindRejection.None, null, null);
    }

    /// <summary>
    /// Applies a checked rebind, leaving every other assignment alone.
    /// </summary>
    public static FrostSettings Apply(
        FrostSettings settings,
        HotkeyAction action,
        HotkeyBinding binding,
        double? clipSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var hotkeys = settings.Hotkeys
            .Select(h =>
                (HotkeyAction)h.Action == action
                && (action != HotkeyAction.SaveClip || NearlyEqual(h.ClipSeconds, clipSeconds))
                    ? h with { Binding = binding.ToString() }
                    : h)
            .ToList();

        return settings with { Hotkeys = hotkeys };
    }

    /// <summary>
    /// Bindings that appear on more than one enabled assignment.
    /// </summary>
    /// <remarks>
    /// For settings that arrived from a hand-edited file rather than through
    /// <see cref="Check"/>: the page shows these as warnings rather than
    /// silently letting one hotkey shadow another.
    /// </remarks>
    public static IReadOnlyList<HotkeyBinding> Conflicts(FrostSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var seen = new Dictionary<HotkeyBinding, int>();

        foreach (var hotkey in settings.Hotkeys)
        {
            if (!hotkey.Enabled || !HotkeyBinding.TryParse(hotkey.Binding, out var parsed))
            {
                continue;
            }

            seen[parsed] = seen.GetValueOrDefault(parsed) + 1;
        }

        return seen.Where(pair => pair.Value > 1).Select(pair => pair.Key).ToList();
    }

    /// <summary>
    /// Keys that do not need a modifier.
    /// </summary>
    /// <remarks>
    /// F13–F24 because no keyboard sends them unprompted — they are what a macro
    /// key on a gaming keyboard emits, which is the ideal clip button. Print
    /// Screen because that is what people reach for anyway.
    /// </remarks>
    public static bool MayStandAlone(int virtualKey) =>
        virtualKey is >= F13 and <= F24 or PrintScreen;

    private static bool IsReserved(HotkeyBinding binding)
    {
        // Ctrl+Alt+Del and Win+L are taken by the OS below any hook, and
        // Alt+Tab, Alt+F4 and Escape are taken by the shell or the game. Binding
        // any of them produces a hotkey that never fires, or fires and also
        // closes the game.
        if (binding.VirtualKey == Delete
            && binding.Modifiers.HasFlag(HotkeyModifiers.Control)
            && binding.Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            return true;
        }

        if (binding.Modifiers.HasFlag(HotkeyModifiers.Alt)
            && binding.VirtualKey is Tab or F4)
        {
            return true;
        }

        return binding.VirtualKey == Escape && binding.Modifiers == HotkeyModifiers.None;
    }

    private static bool IsModifierKey(int virtualKey) =>
        virtualKey is 0x10 or 0x11 or 0x12          // Shift, Control, Alt
            or 0xA0 or 0xA1 or 0xA2 or 0xA3         // left/right Shift, Control
            or 0xA4 or 0xA5                         // left/right Alt
            or LeftWindows or RightWindows;

    private static string Describe(HotkeyAction action, HotkeyAssignmentModel existing)
    {
        var name = action switch
        {
            HotkeyAction.SaveClip => "Save clip",
            HotkeyAction.ToggleFullSessionRecording => "Record session",
            HotkeyAction.Bookmark => "Bookmark",
            HotkeyAction.ToggleMicrophoneMute => "Mute microphone",
            _ => action.ToString(),
        };

        return action == HotkeyAction.SaveClip && existing.ClipSeconds is { } seconds
            ? $"{name} ({DashboardModel.FormatDuration(seconds)})"
            : name;
    }

    /// <summary>
    /// Compares clip durations tolerantly.
    /// </summary>
    /// <remarks>
    /// They round-trip through JSON as doubles, so an exact comparison would
    /// eventually fail to recognise a hotkey as itself and refuse a rebind as a
    /// conflict with its own entry.
    /// </remarks>
    private static bool NearlyEqual(double? left, double? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return Math.Abs(left.Value - right.Value) < 0.001;
    }

    private static RebindCheck Refuse(
        RebindRejection rejection,
        HotkeyAction? conflicting,
        string message) => new(rejection, conflicting, message);
}
