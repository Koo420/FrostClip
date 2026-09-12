using Frost.Shared.Hotkeys;
using Frost.Shared.Settings;

namespace Frost.Engine.Hotkeys;

/// <summary>A hotkey that fired.</summary>
public readonly record struct HotkeyFired(HotkeyAssignment Assignment, long TimestampTicks);

/// <summary>
/// Matches key events against the configured hotkeys.
/// </summary>
/// <remarks>
/// <para>This runs inside the low-level keyboard hook, which is the most
/// latency-sensitive code in the application by some distance: every keystroke
/// the user makes — including the ones they are aiming with — passes through it,
/// and Windows will silently unhook a callback that takes too long. So matching
/// is a loop over a small pre-built array with no allocation, no LINQ, no
/// locking and no dictionary hashing, and the action itself is only queued.</para>
///
/// <para>Edge detection is here rather than in the hook: a held key produces a
/// stream of key-down messages, and without it holding Alt+F10 would try to save
/// a clip thirty times a second.</para>
///
/// <para>Keys are never swallowed. A binding may well collide with something the
/// game uses, and eating the keystroke would be a far worse failure than an
/// accidental clip.</para>
/// </remarks>
public sealed class HotkeyRouter
{
    private readonly HotkeyAssignment[] _assignments;
    private readonly int[] _virtualKeys;
    private readonly HotkeyModifiers[] _modifiers;
    private readonly bool[] _down;

    public HotkeyRouter(IEnumerable<HotkeyAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(assignments);

        _assignments = assignments.Where(a => a.Enabled && a.Binding.IsBound).ToArray();

        foreach (var assignment in _assignments)
        {
            assignment.Validate();
        }

        // Flattened into parallel arrays so the hook path touches contiguous
        // memory and never dereferences a record to compare a key code.
        _virtualKeys = new int[_assignments.Length];
        _modifiers = new HotkeyModifiers[_assignments.Length];
        _down = new bool[_assignments.Length];

        for (var i = 0; i < _assignments.Length; i++)
        {
            _virtualKeys[i] = _assignments[i].Binding.VirtualKey;
            _modifiers[i] = _assignments[i].Binding.Modifiers;
        }
    }

    public int Count => _assignments.Length;

    /// <summary>
    /// Raised when a hotkey transitions from up to down, on the hook thread.
    /// Handlers must do nothing but queue work.
    /// </summary>
    public event Action<HotkeyFired>? Fired;

    /// <summary>
    /// Longest clip duration any enabled hotkey asks for, which is what the ring
    /// buffer has to be sized for.
    /// </summary>
    public TimeSpan LongestClipDuration
    {
        get
        {
            var longest = TimeSpan.Zero;

            foreach (var assignment in _assignments)
            {
                if (assignment.Action == HotkeyAction.SaveClip &&
                    assignment.ClipDuration is { } duration &&
                    duration > longest)
                {
                    longest = duration;
                }
            }

            return longest;
        }
    }

    /// <summary>
    /// Handles a key-down. Returns the number of hotkeys that fired.
    /// Allocation-free.
    /// </summary>
    public int OnKeyDown(int virtualKey, HotkeyModifiers modifiers, long timestampTicks = 0)
    {
        var fired = 0;

        for (var i = 0; i < _virtualKeys.Length; i++)
        {
            if (_virtualKeys[i] != virtualKey || _modifiers[i] != modifiers)
            {
                continue;
            }

            if (_down[i])
            {
                // Auto-repeat. Holding the key must not save thirty clips.
                continue;
            }

            _down[i] = true;
            fired++;
            Fired?.Invoke(new HotkeyFired(_assignments[i], timestampTicks));
        }

        return fired;
    }

    /// <summary>Handles a key-up, re-arming any binding on that key.</summary>
    public void OnKeyUp(int virtualKey)
    {
        for (var i = 0; i < _virtualKeys.Length; i++)
        {
            if (_virtualKeys[i] == virtualKey)
            {
                _down[i] = false;
            }
        }
    }

    /// <summary>
    /// Clears all held state, e.g. after the hook is re-installed or focus was
    /// lost mid-press — otherwise a binding could stay latched down forever.
    /// </summary>
    public void ResetHeldState() => Array.Clear(_down);

    /// <summary>Assignments this router is serving, for display.</summary>
    public IReadOnlyList<HotkeyAssignment> Assignments => _assignments;

    /// <summary>
    /// Bindings assigned to more than one action, which would fire both at once.
    /// </summary>
    /// <remarks>
    /// Reported rather than rejected: the settings UI should warn about it, and
    /// a user who genuinely wants one key to both clip and bookmark is not wrong.
    /// </remarks>
    public IReadOnlyList<HotkeyBinding> Conflicts()
    {
        var conflicts = new List<HotkeyBinding>();

        for (var i = 0; i < _assignments.Length; i++)
        {
            for (var j = i + 1; j < _assignments.Length; j++)
            {
                if (_assignments[i].Binding == _assignments[j].Binding &&
                    !conflicts.Contains(_assignments[i].Binding))
                {
                    conflicts.Add(_assignments[i].Binding);
                }
            }
        }

        return conflicts;
    }
}
