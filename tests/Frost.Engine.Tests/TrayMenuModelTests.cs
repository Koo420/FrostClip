using Frost.Engine.Tray;
using Frost.Shared.Ipc;
using Xunit;

namespace Frost.Engine.Tests;

public sealed class TrayMenuModelTests
{
    private static EngineStatus Armed(double bufferedSeconds = 30) => new()
    {
        IsArmed = true,
        BufferedSeconds = bufferedSeconds,
        MaxClipSeconds = 60,
        CaptureFps = 60,
    };

    private static TrayMenuItem ItemFor(IReadOnlyList<TrayMenuItem> menu, TrayCommand command) =>
        menu.Single(i => !i.IsSeparator && i.Command == command);

    [Fact]
    public void OpeningFrostIsAlwaysAvailableAndIsTheDoubleClickAction()
    {
        foreach (var status in new EngineStatus?[] { null, Armed(), Armed(0) })
        {
            var menu = TrayMenuModel.Build(status);
            var open = ItemFor(menu, TrayCommand.OpenGallery);

            Assert.True(open.IsEnabled);
            Assert.True(open.IsDefault);
        }
    }

    [Fact]
    public void ExitIsAlwaysAvailable()
    {
        // A tray app the user cannot quit from the tray is a support ticket.
        foreach (var status in new EngineStatus?[] { null, Armed() })
        {
            Assert.True(ItemFor(TrayMenuModel.Build(status), TrayCommand.Exit).IsEnabled);
        }
    }

    [Fact]
    public void SaveClipShowsHowMuchIsBufferedWhenItWouldWork()
    {
        var menu = TrayMenuModel.Build(Armed(bufferedSeconds: 42));
        var save = ItemFor(menu, TrayCommand.SaveClip);

        Assert.True(save.IsEnabled);
        Assert.Equal("Save clip (42s buffered)", save.Text);
    }

    [Fact]
    public void SaveClipIsGreyedOutWhenTheBufferIsStillFilling()
    {
        // Offering an action that would fail is worse than absent: the user
        // clicks, nothing happens, and there is no way to know why.
        var save = ItemFor(TrayMenuModel.Build(Armed(bufferedSeconds: 0.2)), TrayCommand.SaveClip);

        Assert.False(save.IsEnabled);
        Assert.Equal("Save clip", save.Text);
    }

    [Fact]
    public void SaveClipIsGreyedOutWhenNotArmed()
    {
        var status = Armed() with { IsArmed = false };
        Assert.False(ItemFor(TrayMenuModel.Build(status), TrayCommand.SaveClip).IsEnabled);
    }

    [Fact]
    public void TheRecordingItemSaysWhatItWillDo()
    {
        Assert.Equal(
            "Record whole session",
            ItemFor(TrayMenuModel.Build(Armed()), TrayCommand.ToggleSessionRecording).Text);

        Assert.Equal(
            "Stop recording session",
            ItemFor(
                TrayMenuModel.Build(Armed() with { IsRecordingSession = true }),
                TrayCommand.ToggleSessionRecording).Text);
    }

    [Fact]
    public void TheBufferItemSaysWhatItWillDo()
    {
        Assert.Equal(
            "Pause clip buffer",
            ItemFor(TrayMenuModel.Build(Armed()), TrayCommand.ToggleArmed).Text);

        Assert.Equal(
            "Resume clip buffer",
            ItemFor(
                TrayMenuModel.Build(Armed() with { IsArmed = false }), TrayCommand.ToggleArmed).Text);
    }

    [Fact]
    public void AFaultedEngineOffersNoRecordingControls()
    {
        var status = Armed() with { FaultMessage = "No hardware encoder is available." };
        var menu = TrayMenuModel.Build(status);

        Assert.False(ItemFor(menu, TrayCommand.ToggleSessionRecording).IsEnabled);
        Assert.False(ItemFor(menu, TrayCommand.ToggleArmed).IsEnabled);

        // But the user can still open the window to find out why, and still quit.
        Assert.True(ItemFor(menu, TrayCommand.OpenGallery).IsEnabled);
        Assert.True(ItemFor(menu, TrayCommand.Exit).IsEnabled);
    }

    [Fact]
    public void AnAbsentEngineOffersOnlyWhatCanWork()
    {
        var menu = TrayMenuModel.Build(null);

        Assert.False(ItemFor(menu, TrayCommand.SaveClip).IsEnabled);
        Assert.False(ItemFor(menu, TrayCommand.ToggleArmed).IsEnabled);
        Assert.True(ItemFor(menu, TrayCommand.OpenSettings).IsEnabled);
        Assert.True(ItemFor(menu, TrayCommand.Exit).IsEnabled);

        // And it does not pretend there is a session to stop.
        Assert.DoesNotContain(
            menu, i => !i.IsSeparator && i.Command == TrayCommand.ToggleSessionRecording);
    }

    [Fact]
    public void OpeningTheClipFolderIsGreyedOutWhenThereIsNone()
    {
        Assert.False(
            ItemFor(TrayMenuModel.Build(Armed(), hasClipFolder: false), TrayCommand.OpenClipFolder)
                .IsEnabled);
    }

    [Fact]
    public void SeparatorsGroupTheMenuWithoutLeadingOrTrailingDividers()
    {
        foreach (var status in new EngineStatus?[] { null, Armed() })
        {
            var menu = TrayMenuModel.Build(status);

            Assert.False(menu[0].IsSeparator);
            Assert.False(menu[^1].IsSeparator);

            // And no two dividers in a row, which renders as a double line.
            for (var i = 1; i < menu.Count; i++)
            {
                Assert.False(menu[i].IsSeparator && menu[i - 1].IsSeparator);
            }
        }
    }

    [Fact]
    public void EveryNonSeparatorItemHasText()
    {
        foreach (var status in new EngineStatus?[] { null, Armed(), Armed(0) })
        {
            Assert.All(
                TrayMenuModel.Build(status).Where(i => !i.IsSeparator),
                item => Assert.False(string.IsNullOrWhiteSpace(item.Text)));
        }
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(-5, "0s")]
    [InlineData(42, "42s")]
    [InlineData(60, "1m")]
    [InlineData(90, "1m 30s")]
    [InlineData(300, "5m")]
    public void BufferedDurationsReadTheWayAPersonWouldSayThem(double seconds, string expected) =>
        Assert.Equal(expected, TrayMenuModel.FormatBuffered(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void TheTooltipSaysWhatIsHappening()
    {
        Assert.Equal("Frost — engine not running", TrayMenuModel.BuildTooltip(null));
        Assert.Equal("Frost — armed, 30s buffered", TrayMenuModel.BuildTooltip(Armed()));
        Assert.Equal("Frost — paused", TrayMenuModel.BuildTooltip(Armed() with { IsArmed = false }));
        Assert.Contains(
            "recording session",
            TrayMenuModel.BuildTooltip(Armed() with { IsRecordingSession = true }));
    }

    [Fact]
    public void AFaultIsTheOnlyThingTheTooltipSaysWhenThereIsOne()
    {
        var tooltip = TrayMenuModel.BuildTooltip(
            Armed() with { FaultMessage = "No hardware encoder is available." });

        Assert.Contains("No hardware encoder", tooltip);
        Assert.DoesNotContain("buffered", tooltip);
    }

    [Fact]
    public void TheTooltipStaysInsideWindowsLimit()
    {
        // Windows truncates at 128 characters including the terminator, and a
        // silently cut tooltip looks like a bug.
        var status = Armed() with { FaultMessage = new string('x', 400) };
        var tooltip = TrayMenuModel.BuildTooltip(status);

        Assert.True(tooltip.Length <= 127, $"tooltip was {tooltip.Length} characters");
        Assert.EndsWith("…", tooltip);
    }
}
