using System.Reflection;
using Frost.Shared.Hotkeys;
using Frost.Shared.Settings;
using Frost.Shared.Shell;
using Xunit;

namespace Frost.Engine.Tests;

/// <summary>
/// The settings page's two jobs: covering the whole schema, and not letting
/// someone bind a hotkey that will misbehave under a low-level keyboard hook.
/// </summary>
public sealed class SettingsUiTests
{
    // ---- coverage ----------------------------------------------------------

    /// <summary>
    /// Every "Section.Property" in the settings schema that a UI row should edit.
    /// </summary>
    /// <remarks>
    /// Reflected here rather than in <c>Frost.Shared</c> on purpose: the Engine
    /// is published AOT, so reflecting over the schema in the product would be a
    /// trim warning for a check only worth running while the code is changing.
    /// </remarks>
    private static IReadOnlyList<string> SchemaPaths()
    {
        var paths = new List<string>();

        foreach (var section in typeof(FrostSettings).GetProperties(
            BindingFlags.Public | BindingFlags.Instance))
        {
            if (SettingsEditor.NonFieldProperties.Contains(section.Name))
            {
                continue;
            }

            foreach (var property in section.PropertyType.GetProperties(
                BindingFlags.Public | BindingFlags.Instance))
            {
                // Computed properties are projections of stored ones, not
                // settings in their own right.
                if (!property.CanWrite)
                {
                    continue;
                }

                paths.Add($"{section.Name}.{property.Name}");
            }
        }

        return paths;
    }

    [Fact]
    public void TheSettingsPageCoversEverySettingInTheSchema()
    {
        // This is the test the whole descriptor-list design exists for. Phase 7
        // asks for a settings UI "covering everything in Phase 4's schema", and
        // the failure mode is a schema that grows a field while the UI quietly
        // does not — leaving a setting reachable only by editing JSON. Adding a
        // property to FrostSettings now fails here instead.
        var covered = SettingsEditor.Fields.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var gaps = SchemaPaths().Where(p => !covered.Contains(p)).ToList();

        Assert.Empty(gaps);
    }

    [Fact]
    public void NoRowOnTheSettingsPageBindsToASettingThatDoesNotExist()
    {
        // The other direction: a renamed setting leaves a row bound to nothing,
        // which in XAML is a silent no-op rather than an error.
        var schema = SchemaPaths().ToHashSet(StringComparer.Ordinal);
        var dangling = SettingsEditor.Fields
            .Select(f => f.Path)
            .Where(p => !schema.Contains(p))
            .ToList();

        Assert.Empty(dangling);
    }

    [Fact]
    public void TheSchemaReflectionActuallyFindsSomething()
    {
        // Guards the two tests above against passing vacuously: a reflection bug
        // that returned nothing would make both of them trivially green.
        var paths = SchemaPaths();

        Assert.Contains("Capture.Fps", paths);
        Assert.Contains("Storage.ClipDirectory", paths);
        Assert.True(paths.Count > 25, $"Only found {paths.Count} settings.");
    }

    [Fact]
    public void EveryRowHasALabelAndAUniquePath()
    {
        Assert.All(SettingsEditor.Fields, field =>
        {
            Assert.False(string.IsNullOrWhiteSpace(field.Label));
            Assert.False(string.IsNullOrWhiteSpace(field.Section));
            Assert.False(string.IsNullOrWhiteSpace(field.Property));
        });

        var paths = SettingsEditor.Fields.Select(f => f.Path).ToList();
        Assert.Equal(paths.Count, paths.Distinct().Count());
    }

    [Fact]
    public void AFieldPathMatchesTheKeyUsedByACorrection()
    {
        // SettingsStore reports corrections as "Capture.Fps"; the page has to be
        // able to find the row a correction is talking about and highlight it.
        var corrections = new List<SettingsCorrection>();
        SettingsStore.Normalise(
            new FrostSettings { Capture = new CaptureSettings { Fps = 9000 } },
            corrections);

        var correction = Assert.Single(corrections);
        Assert.Contains(SettingsEditor.Fields, f => f.Path == correction.Field);
    }

    [Fact]
    public void ValidationReusesTheStoresOwnClampingRatherThanRestatingIt()
    {
        // Two copies of the ranges would eventually disagree, and then the UI
        // and a hand-edited file would accept different things.
        var (settings, corrections) = SettingsEditor.Validate(
            new FrostSettings { Capture = new CaptureSettings { Fps = 0 } });

        Assert.NotEmpty(corrections);
        Assert.InRange(settings.Capture.Fps, 1, 480);
    }

    [Fact]
    public void ValidSettingsProduceNoCorrections()
    {
        var (_, corrections) = SettingsEditor.Validate(new FrostSettings());

        Assert.Empty(corrections);
    }

    [Fact]
    public void SettingsThatOnlyTakeEffectOnRearmingAreMarkedAsSuch()
    {
        // The page greys these with a note; getting it wrong means someone
        // changes the codec mid-session and assumes it did nothing.
        var restartRequired = SettingsEditor.Fields
            .Where(f => f.RequiresRestart)
            .Select(f => f.Path)
            .ToList();

        Assert.Contains("Capture.Fps", restartRequired);
        Assert.Contains("Encoder.Codec", restartRequired);
        Assert.Contains("Buffer.MaxMemoryBytes", restartRequired);

        // And things that take effect immediately are not marked.
        Assert.DoesNotContain("Appearance.Theme", restartRequired);
        Assert.DoesNotContain("Behaviour.ShowClipSavedToast", restartRequired);
    }

    // ---- hotkey rebinding --------------------------------------------------

    private static HotkeyBinding Bind(int key, HotkeyModifiers modifiers = HotkeyModifiers.None) =>
        new(key, modifiers);

    private static FrostSettings WithHotkeys(params HotkeyAssignmentModel[] hotkeys) =>
        new() { Hotkeys = [.. hotkeys] };

    private static HotkeyAssignmentModel Clip(
        string binding,
        double seconds,
        bool enabled = true) => new()
    {
        Action = (int)HotkeyAction.SaveClip,
        Binding = binding,
        ClipSeconds = seconds,
        Enabled = enabled,
    };

    [Fact]
    public void AModifiedKeyThatNothingElseUsesIsAccepted()
    {
        var check = HotkeyRebind.Check(
            WithHotkeys(Clip("Alt+F10", 30)),
            HotkeyAction.Bookmark,
            Bind(VirtualKeys.F9, HotkeyModifiers.Alt));

        Assert.True(check.IsAllowed);
        Assert.Null(check.Message);
    }

    [Fact]
    public void ABareKeyIsRefusedBecauseTheHookDoesNotSwallowIt()
    {
        // The harm is specific: a low-level hook sees the key but does not stop
        // it reaching the game. Bind a bare G and typing "gg" in chat both makes
        // the character do something and saves a clip, with nothing connecting
        // the two for the user.
        var check = HotkeyRebind.Check(
            new FrostSettings(),
            HotkeyAction.SaveClip,
            Bind(0x47)); // G

        Assert.Equal(RebindRejection.NeedsAModifier, check.Rejection);
        Assert.Contains("chat", check.Message);
    }

    [Fact]
    public void AMacroKeyMayStandAloneBecauseNoKeyboardSendsItByAccident()
    {
        // F13–F24 are exactly what a gaming keyboard's macro key emits, and are
        // the best possible clip button.
        Assert.True(HotkeyRebind.Check(
            new FrostSettings(),
            HotkeyAction.SaveClip,
            Bind(0x7C)).IsAllowed); // F13

        Assert.True(HotkeyRebind.Check(
            new FrostSettings(),
            HotkeyAction.SaveClip,
            Bind(0x87)).IsAllowed); // F24

        // Print Screen too — it is what people reach for anyway.
        Assert.True(HotkeyRebind.Check(
            new FrostSettings(),
            HotkeyAction.SaveClip,
            Bind(0x2C)).IsAllowed);

        // But F12 is an ordinary key a keyboard sends, so it still needs one.
        Assert.Equal(
            RebindRejection.NeedsAModifier,
            HotkeyRebind.Check(
                new FrostSettings(),
                HotkeyAction.SaveClip,
                Bind(VirtualKeys.F12)).Rejection);
    }

    [Theory]
    [InlineData(0x10)] // Shift
    [InlineData(0x11)] // Control
    [InlineData(0x12)] // Alt
    [InlineData(0x5B)] // Left Windows
    [InlineData(0xA0)] // Left Shift
    public void AModifierOnItsOwnIsNotAHotkey(int virtualKey)
    {
        var check = HotkeyRebind.Check(
            new FrostSettings(),
            HotkeyAction.SaveClip,
            Bind(virtualKey));

        Assert.Equal(RebindRejection.NoKey, check.Rejection);
    }

    [Fact]
    public void AnUnboundBindingIsRefused() =>
        Assert.Equal(
            RebindRejection.NoKey,
            HotkeyRebind.Check(new FrostSettings(), HotkeyAction.SaveClip, HotkeyBinding.None)
                .Rejection);

    [Theory]
    // Taken by the OS below any hook — the hotkey would simply never fire.
    [InlineData(0x2E, HotkeyModifiers.Control | HotkeyModifiers.Alt)]
    // Taken by the shell, and Alt+F4 would also close the game.
    [InlineData(0x09, HotkeyModifiers.Alt)]
    [InlineData(0x73, HotkeyModifiers.Alt)]
    // Escape alone is how you leave a menu.
    [InlineData(0x1B, HotkeyModifiers.None)]
    public void CombinationsWindowsTakesFirstAreRefused(int key, HotkeyModifiers modifiers)
    {
        var check = HotkeyRebind.Check(
            new FrostSettings(),
            HotkeyAction.SaveClip,
            Bind(key, modifiers));

        Assert.Equal(RebindRejection.ReservedBySystem, check.Rejection);
    }

    [Fact]
    public void ABindingAnotherActionUsesIsRefusedAndNamesIt()
    {
        var settings = WithHotkeys(
            Clip("Alt+F10", 30),
            new HotkeyAssignmentModel
            {
                Action = (int)HotkeyAction.ToggleFullSessionRecording,
                Binding = "Alt+F11",
            });

        var check = HotkeyRebind.Check(
            settings,
            HotkeyAction.Bookmark,
            Bind(VirtualKeys.F11, HotkeyModifiers.Alt));

        Assert.Equal(RebindRejection.AlreadyUsed, check.Rejection);
        Assert.Equal(HotkeyAction.ToggleFullSessionRecording, check.ConflictingAction);
        Assert.Contains("Record session", check.Message);
    }

    [Fact]
    public void AConflictWithADisabledHotkeyIsStillReported()
    {
        // It will conflict the moment it is switched back on, and finding that
        // out then is worse than being told now — so it is refused, but the
        // message says the other one is currently off.
        var check = HotkeyRebind.Check(
            WithHotkeys(Clip("Alt+F10", 30, enabled: false)),
            HotkeyAction.Bookmark,
            Bind(VirtualKeys.F10, HotkeyModifiers.Alt));

        Assert.Equal(RebindRejection.AlreadyUsed, check.Rejection);
        Assert.Contains("turned off", check.Message);
    }

    [Fact]
    public void RebindingAHotkeyToWhatItAlreadyIsIsNotAConflictWithItself()
    {
        var check = HotkeyRebind.Check(
            WithHotkeys(Clip("Alt+F10", 30)),
            HotkeyAction.SaveClip,
            Bind(VirtualKeys.F10, HotkeyModifiers.Alt),
            clipSeconds: 30);

        Assert.True(check.IsAllowed);
    }

    [Fact]
    public void TwoClipHotkeysWithDifferentDurationsConflictWithEachOther()
    {
        // Several SaveClip assignments exist at once, told apart by duration —
        // so identity has to be action *and* duration, not action alone. Getting
        // this wrong lets the 15s hotkey silently steal the 30s one's key.
        var settings = WithHotkeys(Clip("Alt+F10", 30), Clip("Alt+F9", 15));

        var check = HotkeyRebind.Check(
            settings,
            HotkeyAction.SaveClip,
            Bind(VirtualKeys.F10, HotkeyModifiers.Alt),
            clipSeconds: 15);

        Assert.Equal(RebindRejection.AlreadyUsed, check.Rejection);
        Assert.Contains("30s", check.Message);
    }

    [Fact]
    public void AClipDurationThatDriftedThroughJsonStillRecognisesItsOwnHotkey()
    {
        // Durations round-trip as doubles; an exact comparison would eventually
        // fail to see a hotkey as itself and refuse a rebind as a self-conflict.
        var check = HotkeyRebind.Check(
            WithHotkeys(Clip("Alt+F10", 30.0000001)),
            HotkeyAction.SaveClip,
            Bind(VirtualKeys.F10, HotkeyModifiers.Alt),
            clipSeconds: 30);

        Assert.True(check.IsAllowed);
    }

    [Fact]
    public void ApplyingARebindChangesOneAssignmentAndLeavesTheRest()
    {
        var settings = WithHotkeys(
            Clip("Alt+F10", 30),
            Clip("Alt+F9", 15),
            new HotkeyAssignmentModel
            {
                Action = (int)HotkeyAction.Bookmark,
                Binding = "Alt+F8",
            });

        var updated = HotkeyRebind.Apply(
            settings,
            HotkeyAction.SaveClip,
            Bind(VirtualKeys.F12, HotkeyModifiers.Control),
            clipSeconds: 15);

        Assert.Equal("Alt+F10", updated.Hotkeys.Single(h => h.ClipSeconds == 30).Binding);
        Assert.Equal("Ctrl+F12", updated.Hotkeys.Single(h => h.ClipSeconds == 15).Binding);
        Assert.Equal(
            "Alt+F8",
            updated.Hotkeys.Single(h => h.Action == (int)HotkeyAction.Bookmark).Binding);

        // And the original is untouched, since it is a record.
        Assert.Equal("Alt+F9", settings.Hotkeys.Single(h => h.ClipSeconds == 15).Binding);
    }

    [Fact]
    public void ConflictsInAHandEditedFileAreReportedRatherThanLettingOneShadowTheOther()
    {
        var settings = WithHotkeys(
            Clip("Alt+F10", 30),
            Clip("Alt+F10", 15),
            new HotkeyAssignmentModel
            {
                Action = (int)HotkeyAction.Bookmark,
                Binding = "Alt+F8",
            });

        var conflicts = HotkeyRebind.Conflicts(settings);

        var conflict = Assert.Single(conflicts);
        Assert.Equal(Bind(VirtualKeys.F10, HotkeyModifiers.Alt), conflict);
    }

    [Fact]
    public void ADisabledDuplicateIsNotAnActiveConflict()
    {
        var settings = WithHotkeys(Clip("Alt+F10", 30), Clip("Alt+F10", 15, enabled: false));

        Assert.Empty(HotkeyRebind.Conflicts(settings));
    }

    [Fact]
    public void AnUnparseableBindingIsIgnoredRatherThanCrashingTheSettingsPage()
    {
        // A hand-edited file can contain anything, and the settings page opening
        // at all matters more than honouring a line nobody can read.
        var settings = WithHotkeys(Clip("Alt+Nonsense", 30), Clip("Alt+F10", 15));

        Assert.Empty(HotkeyRebind.Conflicts(settings));

        Assert.True(HotkeyRebind.Check(
            settings,
            HotkeyAction.Bookmark,
            Bind(VirtualKeys.F9, HotkeyModifiers.Alt)).IsAllowed);
    }

    [Fact]
    public void TheDefaultHotkeysDoNotConflictWithEachOther()
    {
        // The shipped defaults going through the same check the UI uses.
        Assert.Empty(HotkeyRebind.Conflicts(new FrostSettings()));
    }

    [Fact]
    public void EveryDefaultHotkeyWouldBeAcceptedByTheRebindUi()
    {
        // A default the settings page would refuse to let you re-enter is a
        // contradiction worth catching.
        var defaults = new FrostSettings();

        foreach (var hotkey in defaults.Hotkeys)
        {
            Assert.True(
                HotkeyBinding.TryParse(hotkey.Binding, out var binding),
                $"Default binding '{hotkey.Binding}' does not parse.");

            var check = HotkeyRebind.Check(
                defaults,
                (HotkeyAction)hotkey.Action,
                binding,
                hotkey.ClipSeconds);

            Assert.True(
                check.IsAllowed,
                $"Default binding '{hotkey.Binding}' would be refused: {check.Message}");
        }
    }
}
