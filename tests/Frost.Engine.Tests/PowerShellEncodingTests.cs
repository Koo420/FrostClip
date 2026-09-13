using Xunit;

namespace Frost.Engine.Tests;

/// <summary>
/// Guards the build scripts against an encoding bug that only shows up on the
/// machine they are meant to run on.
/// </summary>
/// <remarks>
/// <para>Windows PowerShell 5.1 — the one built into Windows, and therefore the
/// one anyone actually runs a cloned script with — reads a <c>.ps1</c> file as
/// Windows-1252 unless it starts with a UTF-8 BOM. A single em dash written by
/// an editor that assumes UTF-8 arrives as three mojibake characters, and
/// because it lands inside a double-quoted string it does not produce a tidy
/// encoding error: it produces a cascade of "string is missing the terminator"
/// and "an empty pipe element is not allowed" from lines that are perfectly
/// fine, which is extremely misleading to debug.</para>
///
/// <para>This cost a round trip on a real machine, so it is a test rather than a
/// note. Either condition is sufficient — pure ASCII parses under any encoding,
/// and a BOM makes any encoding correct — and a script satisfying neither is
/// broken for the majority of Windows users regardless of whether it happens to
/// work under PowerShell 7.</para>
/// </remarks>
public sealed class PowerShellEncodingTests
{
    private static IEnumerable<string> Scripts() =>
        Directory.EnumerateFiles(TestPaths.RepoRoot, "*.ps1", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    [Fact]
    public void EveryPowerShellScriptIsReadableByWindowsPowerShell()
    {
        var scripts = Scripts().ToList();

        Assert.NotEmpty(scripts);

        foreach (var script in scripts)
        {
            var bytes = File.ReadAllBytes(script);
            var name = Path.GetFileName(script);

            var hasBom = bytes.Length >= 3
                && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

            if (hasBom)
            {
                continue;
            }

            var firstNonAscii = -1;

            for (var i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] > 127)
                {
                    firstNonAscii = i;
                    break;
                }
            }

            // Built only when there is something to report: Assert.True's
            // message argument is evaluated eagerly, so interpolating the
            // offending byte unconditionally throws on the passing path.
            Assert.True(
                firstNonAscii < 0,
                firstNonAscii < 0
                    ? string.Empty
                    : $"{name} is neither pure ASCII nor UTF-8 with a BOM, so Windows "
                        + "PowerShell 5.1 will read it as Windows-1252 and fail to parse "
                        + $"it. First non-ASCII byte at offset {firstNonAscii} "
                        + $"(0x{bytes[firstNonAscii]:X2}). Either replace the character "
                        + "with ASCII or write the file with a BOM.");
        }
    }

    [Fact]
    public void TheScriptsUseCrlfOrAreAtLeastConsistent()
    {
        // Not a correctness issue for the parser, but a .ps1 with mixed line
        // endings confuses here-strings, which build.ps1 uses for its help.
        foreach (var script in Scripts())
        {
            var text = File.ReadAllText(script);
            var lone = 0;

            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n' && (i == 0 || text[i - 1] != '\r'))
                {
                    lone++;
                }
            }

            var crlf = text.Split("\r\n").Length - 1;

            Assert.True(
                lone == 0 || crlf == 0,
                $"{Path.GetFileName(script)} mixes CRLF and bare LF line endings " +
                $"({crlf} CRLF, {lone} LF).");
        }
    }

    [Fact]
    public void TheVerificationScriptDocumentsWhatItCannotDo()
    {
        // The script's value depends on it not implying a complete run: the
        // keyboard hook under a fullscreen-exclusive game, the PresentMon
        // comparison and the Mica observation all need a person.
        var script = Path.Combine(TestPaths.RepoRoot, "verify-windows.ps1");
        var text = File.ReadAllText(script, System.Text.Encoding.UTF8);

        Assert.Contains("Still needs a person", text);
        Assert.Contains("PresentMon", text);
        Assert.Contains("fullscreen-exclusive", text);
    }
}
