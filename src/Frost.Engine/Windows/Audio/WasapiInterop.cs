using System.Runtime.InteropServices;

namespace Frost.Engine.Windows.Audio;

/// <summary>
/// Raw-COM wrappers for the two WASAPI interfaces Vortice does not project.
/// </summary>
/// <remarks>
/// <para>Vortice.MediaFoundation gives us <c>IMMDeviceEnumerator</c> and
/// <c>IMMDevice</c> but not <c>IAudioClient</c>, and loopback capture needs
/// <c>IAudioClient</c> for its <c>AUDCLNT_STREAMFLAGS_LOOPBACK</c> flag —
/// Media Foundation's own audio capture covers microphones only.</para>
///
/// <para>Both interfaces are small and their vtable order has been fixed since
/// Windows Vista, which is why hand-writing them is a reasonable trade against
/// taking a whole audio library as a dependency in a process with a 50MB budget.
/// The slot numbers below are from the SDK headers and are commented with the
/// method they correspond to, because an off-by-one here is a hard-to-diagnose
/// crash rather than a compile error.</para>
/// </remarks>
internal static class WasapiInterop
{
    /// <summary>IAudioClient.</summary>
    internal static readonly Guid IAudioClientIid = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    /// <summary>IAudioCaptureClient.</summary>
    internal static readonly Guid IAudioCaptureClientIid = new("C8ADBD64-5F1D-4b61-B8FF-AD58F04CBBAA");

    /// <summary>AUDCLNT_SHAREMODE_SHARED. Exclusive mode would lock other apps out of the device.</summary>
    internal const int ShareModeShared = 0;

    /// <summary>AUDCLNT_STREAMFLAGS_LOOPBACK — capture what is being played.</summary>
    internal const uint StreamFlagsLoopback = 0x00020000;

    /// <summary>AUDCLNT_STREAMFLAGS_EVENTCALLBACK.</summary>
    internal const uint StreamFlagsEventCallback = 0x00040000;

    /// <summary>AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY.</summary>
    internal const uint BufferFlagsDataDiscontinuity = 0x1;

    /// <summary>AUDCLNT_BUFFERFLAGS_SILENT — the packet is silence and its buffer may be garbage.</summary>
    internal const uint BufferFlagsSilent = 0x2;

    /// <summary>AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR.</summary>
    internal const uint BufferFlagsTimestampError = 0x4;

    /// <summary>AUDCLNT_E_DEVICE_INVALIDATED — the endpoint went away.</summary>
    internal const int DeviceInvalidated = unchecked((int)0x88890004);

    /// <summary>AUDCLNT_S_BUFFER_EMPTY.</summary>
    internal const int BufferEmpty = 0x08890001;

    // WAVE_FORMAT_* tags.
    internal const ushort WaveFormatPcm = 1;
    internal const ushort WaveFormatIeeeFloat = 3;
    internal const ushort WaveFormatExtensible = 0xFFFE;
}

/// <summary><c>WAVEFORMATEX</c>.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WaveFormatEx
{
    internal ushort FormatTag;
    internal ushort Channels;
    internal uint SamplesPerSecond;
    internal uint AverageBytesPerSecond;
    internal ushort BlockAlign;
    internal ushort BitsPerSample;
    internal ushort ExtraSize;
}

/// <summary>
/// <c>WAVEFORMATEXTENSIBLE</c>. Multi-channel and high-bit-depth endpoints report
/// this rather than plain <c>WAVEFORMATEX</c>, with the real sample type in
/// <see cref="SubFormat"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WaveFormatExtensible
{
    internal WaveFormatEx Format;
    internal ushort ValidBitsPerSample;
    internal uint ChannelMask;
    internal Guid SubFormat;

    /// <summary>KSDATAFORMAT_SUBTYPE_IEEE_FLOAT.</summary>
    internal static readonly Guid SubTypeIeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");

    /// <summary>KSDATAFORMAT_SUBTYPE_PCM.</summary>
    internal static readonly Guid SubTypePcm = new("00000001-0000-0010-8000-00AA00389B71");
}

/// <summary>Raw-COM view of <c>IAudioClient</c>.</summary>
/// <remarks>
/// Vtable after IUnknown's three slots: 3 Initialize, 4 GetBufferSize,
/// 5 GetStreamLatency, 6 GetCurrentPadding, 7 IsFormatSupported, 8 GetMixFormat,
/// 9 GetDevicePeriod, 10 Start, 11 Stop, 12 Reset, 13 SetEventHandle,
/// 14 GetService.
/// </remarks>
internal sealed unsafe class AudioClient : IDisposable
{
    private nint _client;

    internal AudioClient(nint client) => _client = client;

    internal nint Pointer => _client;

    /// <summary>The endpoint's mix format. Caller must free the returned pointer with CoTaskMemFree.</summary>
    internal nint GetMixFormat()
    {
        var vtable = *(nint**)_client;
        var getMixFormat = (delegate* unmanaged[Stdcall]<nint, nint*, int>)vtable[8];

        nint format;
        Interop.ComHelpers.ThrowIfFailed(getMixFormat(_client, &format), "IAudioClient::GetMixFormat");
        return format;
    }

    /// <summary>Default and minimum device periods, in 100ns units.</summary>
    internal (long Default, long Minimum) GetDevicePeriod()
    {
        var vtable = *(nint**)_client;
        var getDevicePeriod = (delegate* unmanaged[Stdcall]<nint, long*, long*, int>)vtable[9];

        long defaultPeriod;
        long minimumPeriod;
        Interop.ComHelpers.ThrowIfFailed(
            getDevicePeriod(_client, &defaultPeriod, &minimumPeriod), "IAudioClient::GetDevicePeriod");

        return (defaultPeriod, minimumPeriod);
    }

    internal void Initialize(int shareMode, uint streamFlags, long bufferDuration, nint format)
    {
        var vtable = *(nint**)_client;
        var initialize =
            (delegate* unmanaged[Stdcall]<nint, int, uint, long, long, nint, nint, int>)vtable[3];

        Interop.ComHelpers.ThrowIfFailed(
            initialize(_client, shareMode, streamFlags, bufferDuration, 0, format, 0),
            "IAudioClient::Initialize");
    }

    internal uint GetBufferSize()
    {
        var vtable = *(nint**)_client;
        var getBufferSize = (delegate* unmanaged[Stdcall]<nint, uint*, int>)vtable[4];

        uint frames;
        Interop.ComHelpers.ThrowIfFailed(getBufferSize(_client, &frames), "IAudioClient::GetBufferSize");
        return frames;
    }

    internal void SetEventHandle(nint handle)
    {
        var vtable = *(nint**)_client;
        var setEventHandle = (delegate* unmanaged[Stdcall]<nint, nint, int>)vtable[13];

        Interop.ComHelpers.ThrowIfFailed(
            setEventHandle(_client, handle), "IAudioClient::SetEventHandle");
    }

    internal void Start()
    {
        var vtable = *(nint**)_client;
        var start = (delegate* unmanaged[Stdcall]<nint, int>)vtable[10];
        Interop.ComHelpers.ThrowIfFailed(start(_client), "IAudioClient::Start");
    }

    internal void Stop()
    {
        if (_client == 0)
        {
            return;
        }

        var vtable = *(nint**)_client;
        var stop = (delegate* unmanaged[Stdcall]<nint, int>)vtable[11];

        // Stopping a client that never started returns S_FALSE, not an error.
        stop(_client);
    }

    /// <summary>Gets the capture client. Caller owns the returned pointer.</summary>
    internal nint GetCaptureClient()
    {
        var vtable = *(nint**)_client;
        var getService = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtable[14];

        var iid = WasapiInterop.IAudioCaptureClientIid;
        nint service;
        Interop.ComHelpers.ThrowIfFailed(
            getService(_client, &iid, &service), "IAudioClient::GetService(IAudioCaptureClient)");
        return service;
    }

    public void Dispose()
    {
        if (_client != 0)
        {
            Marshal.Release(_client);
            _client = 0;
        }
    }
}

/// <summary>Raw-COM view of <c>IAudioCaptureClient</c>.</summary>
/// <remarks>
/// Vtable after IUnknown: 3 GetBuffer, 4 ReleaseBuffer, 5 GetNextPacketSize.
/// </remarks>
internal sealed unsafe class AudioCaptureClient : IDisposable
{
    private nint _client;

    internal AudioCaptureClient(nint client) => _client = client;

    /// <summary>Frames in the next packet, or zero when there is nothing waiting.</summary>
    internal uint GetNextPacketSize()
    {
        var vtable = *(nint**)_client;
        var getNextPacketSize = (delegate* unmanaged[Stdcall]<nint, uint*, int>)vtable[5];

        uint frames;
        var hr = getNextPacketSize(_client, &frames);

        if (hr < 0)
        {
            Interop.ComHelpers.ThrowIfFailed(hr, "IAudioCaptureClient::GetNextPacketSize");
        }

        return frames;
    }

    /// <summary>
    /// Acquires the next packet.
    /// </summary>
    /// <returns>
    /// False when the buffer is empty. When true, <see cref="ReleaseBuffer"/> must
    /// be called — holding a WASAPI buffer stalls the endpoint for every
    /// application on the machine.
    /// </returns>
    internal bool TryGetBuffer(out AudioPacket packet)
    {
        var vtable = *(nint**)_client;
        var getBuffer =
            (delegate* unmanaged[Stdcall]<nint, nint*, uint*, uint*, ulong*, ulong*, int>)vtable[3];

        nint data;
        uint frames;
        uint flags;
        ulong devicePosition;
        ulong qpcPosition;

        var hr = getBuffer(_client, &data, &frames, &flags, &devicePosition, &qpcPosition);

        if (hr == WasapiInterop.BufferEmpty || (hr >= 0 && frames == 0))
        {
            packet = default;

            // A zero-frame packet still has to be released.
            if (hr >= 0)
            {
                ReleaseBuffer(0);
            }

            return false;
        }

        Interop.ComHelpers.ThrowIfFailed(hr, "IAudioCaptureClient::GetBuffer");

        packet = new AudioPacket(data, frames, flags, qpcPosition);
        return true;
    }

    internal void ReleaseBuffer(uint frames)
    {
        var vtable = *(nint**)_client;
        var releaseBuffer = (delegate* unmanaged[Stdcall]<nint, uint, int>)vtable[4];
        releaseBuffer(_client, frames);
    }

    public void Dispose()
    {
        if (_client != 0)
        {
            Marshal.Release(_client);
            _client = 0;
        }
    }
}

/// <summary>One packet from WASAPI.</summary>
/// <param name="Data">Pointer into WASAPI's buffer, valid only until released.</param>
/// <param name="FrameCount">Frames available.</param>
/// <param name="Flags">AUDCLNT_BUFFERFLAGS_*.</param>
/// <param name="QpcPosition">
/// Performance-counter value for the first frame, in 100ns units.
/// </param>
internal readonly record struct AudioPacket(nint Data, uint FrameCount, uint Flags, ulong QpcPosition)
{
    /// <summary>
    /// True when WASAPI says the packet is silence.
    /// </summary>
    /// <remarks>
    /// The buffer contents are undefined in that case — it is not guaranteed to be
    /// zeroed — so it must be treated as silence rather than copied.
    /// </remarks>
    internal bool IsSilent => (Flags & WasapiInterop.BufferFlagsSilent) != 0;

    /// <summary>True when frames were lost before this packet.</summary>
    internal bool IsDiscontinuous => (Flags & WasapiInterop.BufferFlagsDataDiscontinuity) != 0;

    /// <summary>True when the QPC timestamp is not trustworthy.</summary>
    internal bool HasTimestampError => (Flags & WasapiInterop.BufferFlagsTimestampError) != 0;
}
