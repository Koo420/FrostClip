using System.Runtime.InteropServices;
using Frost.Engine.Capture;
using Frost.Engine.Diagnostics;
using Frost.Engine.Audio;
using Frost.Engine.Clips;
using Frost.Engine.Encoding;
using Frost.Engine.Windows.Encode;
using Frost.Shared.Clips;
using Frost.Engine.Windows.Diagnostics;
using Frost.Engine.Ipc;
using Frost.Shared.Ipc;
using Frost.Shared.Settings;

namespace Frost.Engine.Windows;

/// <summary>
/// Windows process host for the Engine.
/// </summary>
/// <remarks>
/// Normal launch is tray-resident with no console. The diagnostic verbs
/// (<c>--soak</c>, and the encoder probes added in Phase 2) attach to the parent
/// console so they can be driven from a terminal.
/// </remarks>
internal static partial class EngineHost
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    internal static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            // The resident tray Engine. Wired up in Phase 4 (hotkeys + IPC) and
            // Phase 8 (tray icon); until then there is nothing to sit resident
            // for, so say so rather than spinning.
            return RunDiagnostic(log =>
            {
                log.Info("Frost Engine has no resident mode yet. Try --soak [minutes] or --help.");
                return 0;
            });
        }

        return args[0] switch
        {
            "--soak" => RunDiagnostic(log => Soak(args, log)),
            "--encoders" => RunDiagnostic(ListEncoders),
            "--ipc-server" => RunDiagnostic(RunIpcServer),
            "--encode-test" => RunDiagnostic(log => EncodeTest(args, log)),
            "--clip-test" => RunDiagnostic(log => ClipTest(args, log)),
            "--displays" => RunDiagnostic(ListDisplays),
            "--windows" => RunDiagnostic(ListWindows),
            "--help" or "-h" or "/?" => RunDiagnostic(PrintUsage),
            _ => RunDiagnostic(log =>
            {
                log.Error($"Unknown argument '{args[0]}'.");
                PrintUsage(log);
                return 1;
            }),
        };
    }

    private static int Soak(string[] args, IEngineLog log)
    {
        var minutes = 10.0;
        if (args.Length > 1 && !double.TryParse(args[1], out minutes))
        {
            log.Error($"'{args[1]}' is not a number of minutes.");
            return 1;
        }

        if (minutes is <= 0 or > 720)
        {
            log.Error("Soak duration must be between 0 and 720 minutes.");
            return 1;
        }

        var config = new CaptureConfiguration
        {
            Target = CaptureTarget.PrimaryMonitor,
            TargetFps = 60,
        };

        return CaptureSoakTest.Run(config, TimeSpan.FromMinutes(minutes), log);
    }

    /// <summary>
    /// Reports what hardware encoders exist and which one Frost would pick, so
    /// the "no hardware encoder" case is diagnosable without starting a capture.
    /// </summary>
    private static int ListEncoders(IEngineLog log)
    {
        using var mediaFoundation = new MediaFoundationRuntime(log);
        var discovered = new MediaFoundationEncoderEnumerator(log).Enumerate();

        uint? adapterVendorId = null;
        try
        {
            using var device = GraphicsDevice.Create(CaptureTarget.PrimaryMonitor, log);
            adapterVendorId = device.AdapterVendorId;
            log.Info($"Capture adapter: {device.AdapterDescription} (VEN_{adapterVendorId:X4}).");
        }
        catch (Exception ex)
        {
            log.Warn("Could not open a graphics device; adapter affinity will be ignored.", ex);
        }

        var codecs = EncoderSelector.AvailableCodecs(discovered);
        log.Info(codecs.Count == 0
            ? "Hardware codecs available: none."
            : $"Hardware codecs available: {string.Join(", ", codecs)}.");

        var preferences = new EncoderPreferences { CaptureAdapterVendorId = adapterVendorId };

        try
        {
            log.Info($"Frost would use: {EncoderSelector.Select(discovered, preferences)}");
            return 0;
        }
        catch (NoHardwareEncoderException ex)
        {
            log.Error(ex.Message);
            return 3;
        }
    }

    /// <summary>
    /// Records the primary display for a few seconds and writes an MP4, proving
    /// the whole chain: WGC capture, hardware NV12 conversion, hardware encode,
    /// and the Sink Writer muxing already-encoded samples.
    /// </summary>
    private static int EncodeTest(string[] args, IEngineLog log)
    {
        var seconds = 10.0;
        if (args.Length > 1 && !double.TryParse(args[1], out seconds))
        {
            log.Error($"'{args[1]}' is not a number of seconds.");
            return 1;
        }

        if (seconds is <= 0 or > 3600)
        {
            log.Error("Duration must be between 0 and 3600 seconds.");
            return 1;
        }

        var output = args.Length > 2
            ? args[2]
            : Path.Combine(Path.GetTempPath(), $"frost-encode-test-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");

        using var mediaFoundation = new MediaFoundationRuntime(log);
        using var device = GraphicsDevice.Create(CaptureTarget.PrimaryMonitor, log);

        var captureConfig = new CaptureConfiguration
        {
            Target = CaptureTarget.PrimaryMonitor,
            TargetFps = 60,
        };

        // Counts samples before the muxer exists, because the muxer needs the
        // encoder's media type and the encoder only has one after it starts.
        var counter = new CountingSampleSink();

        using var pipeline = VideoEncodePipeline.Start(
            device,
            captureConfig,
            new EncoderPreferences { Codec = VideoCodec.H264 },
            (width, height) => EncoderSettings.For(VideoCodec.H264, width, height, captureConfig.TargetFps),
            counter,
            log);

        using var encodedType = pipeline.GetEncodedMediaType();
        using var muxer = new Mp4Muxer(output, encodedType, log);
        pipeline.SetSampleSink(muxer);

        using var gpuCounters = GpuEngineCounters.TryCreate(log);
        var attribution = new EncodeAttributionTracker();
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var processorCount = Environment.ProcessorCount;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var lastCpu = self.TotalProcessorTime;
        var lastElapsed = clock.Elapsed;

        // The first second is encoder warm-up: the driver is still allocating,
        // and sampling it would make every run look CPU-heavy.
        var warmUp = TimeSpan.FromSeconds(Math.Min(1.0, seconds / 4));

        while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            Thread.Sleep(500);

            if (!pipeline.IsCaptureRunning)
            {
                log.Error("Capture stopped early.");
                break;
            }

            self.Refresh();
            var cpu = self.TotalProcessorTime;
            var elapsed = clock.Elapsed;
            var window = elapsed - lastElapsed;

            if (window > TimeSpan.Zero && elapsed > warmUp)
            {
                // Share of the whole machine, which is what the performance
                // budget is expressed in.
                var cpuPercent =
                    (cpu - lastCpu).TotalSeconds / window.TotalSeconds / processorCount * 100.0;

                var engines = gpuCounters?.Sample() ?? [];
                attribution.Add(
                    engines.Select(e =>
                        new KeyValuePair<string, double>(e.EngineType, e.UtilizationPercent)),
                    cpuPercent);
            }

            lastCpu = cpu;
            lastElapsed = elapsed;
        }

        pipeline.Stop();
        muxer.Finish(TimeSpan.FromSeconds(15));

        var attributionReport = attribution.Report();
        log.Info($"Encode attribution:{Environment.NewLine}{attributionReport}");

        var info = new FileInfo(output);
        log.Info(
            $"captured={pipeline.FramesCaptured} encoded={pipeline.FramesEncoded} " +
            $"keyframes={pipeline.KeyFramesEncoded} " +
            $"dropped(capture)={pipeline.FramesDroppedByCapture} " +
            $"dropped(encode)={pipeline.FramesDroppedByEncoder} " +
            $"written={muxer.SamplesWritten} refused={muxer.SamplesRefused}");

        if (!info.Exists || info.Length < 1024)
        {
            log.Error($"{output} was not written, or is too small to be a real recording.");
            return 4;
        }

        log.Info($"Wrote {output} ({info.Length / (1024.0 * 1024.0):F2}MB).");

        if (pipeline.KeyFramesEncoded == 0)
        {
            log.Error("No keyframes were produced; clips could not be trimmed from this stream.");
            return 5;
        }

        if (!attributionReport.CountersAvailable)
        {
            // Not a failure: the encode worked, we just could not prove which
            // engine did it. Say so plainly rather than implying a pass.
            log.Warn(
                "GPU engine counters were unavailable, so which engine did the encoding " +
                "could not be verified on this machine.");
            return 0;
        }

        if (!attributionReport.Passes)
        {
            log.Error($"Encode attribution check failed: {attributionReport.Verdict}");
            return 6;
        }

        log.Info(
            $"Encoding ran on the GPU's video encode engine " +
            $"({attributionReport.VideoEncodePercent:F1}%) with " +
            $"{attributionReport.ProcessCpuPercent:F2}% CPU.");

        return 0;
    }

    /// <summary>
    /// Records video and loopback audio into the ring buffer, then saves a clip —
    /// the whole instant-clip path end to end, including the audio track.
    /// </summary>
    private static int ClipTest(string[] args, IEngineLog log)
    {
        var seconds = 20.0;
        if (args.Length > 1 && !double.TryParse(args[1], out seconds))
        {
            log.Error($"'{args[1]}' is not a number of seconds.");
            return 1;
        }

        if (seconds is < 3 or > 600)
        {
            log.Error("Duration must be between 3 and 600 seconds.");
            return 1;
        }

        var directory = args.Length > 2
            ? args[2]
            : Path.Combine(Path.GetTempPath(), "frost-clip-test");

        var clipSeconds = Math.Min(10.0, seconds - 2);

        using var mediaFoundation = new MediaFoundationRuntime(log);
        using var device = GraphicsDevice.Create(CaptureTarget.PrimaryMonitor, log);

        var captureConfig = new CaptureConfiguration
        {
            Target = CaptureTarget.PrimaryMonitor,
            TargetFps = 60,
        };

        EncoderSettings? encoderSettings = null;

        var ring = new EncodedSampleRing(new RingBufferOptions
        {
            MaxTrailingDuration = TimeSpan.FromSeconds(clipSeconds),
            BitsPerSecond = 12_400_000,
            Fps = captureConfig.TargetFps,
        });

        using var pipeline = VideoEncodePipeline.Start(
            device,
            captureConfig,
            new EncoderPreferences { Codec = VideoCodec.H264 },
            (width, height) =>
            {
                encoderSettings = EncoderSettings.For(
                    VideoCodec.H264, width, height, captureConfig.TargetFps);
                return encoderSettings;
            },
            ring,
            log);

        var audioWindow = TimeSpan.FromSeconds(clipSeconds * 1.5);

        // Audio is best-effort: a machine with no playback device, or one whose
        // endpoint reports a format we cannot read, still records video.
        var system = TryStartAudio(
            Frost.Engine.Windows.Audio.WasapiCaptureMode.Loopback,
            audioWindow, trackIndex: 0, microphone: null, log);

        // The microphone is a second track, not a mix, so it can be muted or
        // dropped later without touching the game audio.
        var wantsMic = args.Any(a => string.Equals(a, "--mic", StringComparison.Ordinal));
        var microphoneState = wantsMic ? new MicrophoneState() : null;

        var microphone = wantsMic
            ? TryStartAudio(
                Frost.Engine.Windows.Audio.WasapiCaptureMode.Microphone,
                audioWindow, trackIndex: 1, microphoneState, log)
            : null;

        var audioTracks = new List<AudioTrackBuffer>();
        if (system is not null)
        {
            audioTracks.Add(system.Buffer);
        }

        if (microphone is not null)
        {
            audioTracks.Add(microphone.Buffer);
        }

        try
        {
            var writer = new Mp4ClipWriter(
                pipeline.GetEncodedMediaType,
                encoderSettings ?? throw new InvalidOperationException("encoder settings were not produced"),
                log,
                ClipKind.InstantClip,
                audioTracks);

            using var clips = new ClipService(ring, writer, directory, log);

            ClipResult? result = null;
            clips.ClipCompleted += completed => result = completed;

            log.Info($"Filling the buffer for {seconds:F0}s, then saving a {clipSeconds:F0}s clip.");
            Thread.Sleep(TimeSpan.FromSeconds(seconds));

            if (!pipeline.IsCaptureRunning)
            {
                log.Error("Capture stopped early.");
                return 4;
            }

            clips.Request(new ClipRequest(TimeSpan.FromSeconds(clipSeconds), $"{clipSeconds:F0}s", "clip-test"));

            if (!clips.WaitForIdle(TimeSpan.FromSeconds(60)))
            {
                log.Error("The clip did not finish writing within 60s.");
                return 4;
            }

            log.Info(
                $"buffered={ring.HeldDuration.TotalSeconds:F1}s " +
                $"video={ring.SamplesWritten} " +
                $"system-audio-blocks={system?.BlocksRouted ?? 0} " +
                $"system-silence-frames={system?.SilenceFramesInserted ?? 0} " +
                $"mic-blocks={microphone?.BlocksRouted ?? 0}");

            if (result is null || !result.Succeeded)
            {
                log.Error($"The clip failed: {result?.Error ?? "no result"}");
                return 5;
            }

            var info = new FileInfo(result.Metadata!.FilePath);
            log.Info(
                $"Wrote {info.FullName} ({info.Length / (1024.0 * 1024.0):F2}MB, " +
                $"{result.Metadata.Duration.TotalSeconds:F1}s, written in " +
                $"{result.WriteDuration.TotalMilliseconds:F0}ms).");

            if (system is null)
            {
                log.Warn("No audio was captured, so the clip has no audio track to check.");
                return 0;
            }

            if (system.BlocksRouted == 0)
            {
                log.Error("Audio capture ran but delivered nothing; the clip is silent.");
                return 6;
            }

            log.Info(
                $"Clip written with {audioTracks.Count} audio track(s)" +
                $"{(microphone is not null ? " (system + microphone)" : " (system)")}.");
            return 0;
        }
        finally
        {
            microphone?.Dispose();
            system?.Dispose();
        }
    }

    /// <summary>
    /// Starts one audio capture, returning null when it cannot start.
    /// </summary>
    /// <remarks>
    /// Audio is never allowed to fail a recording: no playback device, a format we
    /// cannot read, or a microphone the user has denied access to all mean a
    /// missing track, not a missing clip.
    /// </remarks>
    private static AudioCapturePipeline? TryStartAudio(
        Frost.Engine.Windows.Audio.WasapiCaptureMode mode,
        TimeSpan window,
        int trackIndex,
        MicrophoneState? microphone,
        IEngineLog log)
    {
        AudioCapturePipeline? pipeline = null;

        try
        {
            var capture = new Frost.Engine.Windows.Audio.WasapiCapture(
                mode,
                deviceId: null,
                log,
                gain: microphone?.Gain ?? 1.0,
                isMuted: microphone is null ? null : () => microphone.IsMuted);

            pipeline = new AudioCapturePipeline(capture, window, log, bookmarker: null, trackIndex);
            pipeline.Start();

            log.Info($"{mode} audio on track {trackIndex}: {pipeline.DeviceName}, {pipeline.Format}.");
            return pipeline;
        }
        catch (Exception ex)
        {
            log.Warn($"{mode} audio capture could not start; that track will be missing.", ex);
            pipeline?.Dispose();
            return null;
        }
    }

    /// <summary>Counts samples produced before the real sink is attached.</summary>
    private sealed class CountingSampleSink : IEncodedSampleSink
    {
        internal long Samples { get; private set; }

        public bool TryWrite(ReadOnlySpan<byte> data, long timestampTicks, long durationTicks, bool isKeyFrame)
        {
            Samples++;
            return true;
        }
    }

    /// <summary>
    /// Runs the IPC server until a key is pressed, so the Shell can be developed
    /// and the channel exercised against a real Engine process.
    /// </summary>
    private static int RunIpcServer(IEngineLog log)
    {
        var loaded = SettingsStore.Load();

        foreach (var correction in loaded.Corrections)
        {
            log.Warn($"Settings correction: {correction}");
        }

        var commands = new DiagnosticEngineCommands(loaded.Settings, log);
        var server = new IpcServer(commands, log);

        try
        {
            server.Start();
            log.Info(
                $"IPC server running on '{FrostIpc.PipeName}' " +
                $"(protocol v{FrostIpc.ProtocolVersion}). Press Ctrl+C to stop.");

            using var stop = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, args) =>
            {
                args.Cancel = true;
                stop.Set();
            };

            stop.Wait();

            log.Info(
                $"Served {server.ClientsAccepted} client(s), " +
                $"{server.MessagesReceived} received / {server.MessagesSent} sent, " +
                $"{server.HandlerFailures} handler failure(s).");
            return 0;
        }
        finally
        {
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static int ListDisplays(IEngineLog log)
    {
        foreach (var display in DisplayEnumerator.Displays())
        {
            log.Info(
                $"{display.DeviceName}  {display.Width}x{display.Height}  " +
                $"{display.FriendlyName}{(display.IsPrimary ? "  (primary)" : string.Empty)}");
        }

        return 0;
    }

    private static int ListWindows(IEngineLog log)
    {
        foreach (var window in DisplayEnumerator.Windows())
        {
            log.Info($"0x{window.Handle:X}  [{window.ProcessName}]  {window.Title}");
        }

        return 0;
    }

    private static int PrintUsage(IEngineLog log)
    {
        log.Info(
            """
            Frost.Engine.exe — capture/encode background process.

              (no arguments)      run resident (tray)
              --soak [minutes]    capture soak test, default 10 minutes; exit 0 if memory is flat
              --encoders          list hardware encoders and the one Frost would pick
              --ipc-server        run the Engine/Shell IPC server until Ctrl+C
              --encode-test [s] [out.mp4]
                                  record the primary display and write an MP4
              --clip-test [s] [dir] [--mic]
                                  fill the ring buffer, then save a clip with audio
                                  (--mic adds the microphone as a second track)
              --displays          list capture-able displays
              --windows           list capture-able windows
              --help              this text
            """);
        return 0;
    }

    /// <summary>
    /// Runs a console verb. The Engine is a WinExe so that the resident process
    /// has no console window; a diagnostic run borrows the caller's.
    /// </summary>
    private static int RunDiagnostic(Func<IEngineLog, int> body)
    {
        AttachConsole(AttachParentProcess);
        var log = new ConsoleEngineLog(LogLevel.Debug);

        try
        {
            return body(log);
        }
        catch (Exception ex)
        {
            log.Error("Engine failed.", ex);
            return 1;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);
}
