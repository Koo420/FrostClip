using System.Runtime.InteropServices;
using Frost.Engine.Diagnostics;

namespace Frost.Engine.Windows.Encode;

/// <summary>
/// Minimal <c>ICodecAPI</c> wrapper for the encoder tuning knobs that have no
/// media-type equivalent.
/// </summary>
/// <remarks>
/// Vortice does not project <c>ICodecAPI</c>, so this talks to it directly. Every
/// setter is best-effort: these are tuning parameters, not correctness
/// requirements, and drivers differ in which ones they accept. A rejected knob is
/// logged and the encoder runs with the driver's default, which is always a valid
/// configuration — far better than refusing to record because a GPU would not
/// take a GOP-size hint.
/// </remarks>
internal sealed unsafe class CodecApi : IDisposable
{
    private static readonly Guid ICodecApiIid = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    /// <summary><c>CODECAPI_AVEncCommonRateControlMode</c>.</summary>
    internal static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");

    /// <summary><c>CODECAPI_AVEncCommonMeanBitRate</c>.</summary>
    internal static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");

    /// <summary><c>CODECAPI_AVEncCommonMaxBitRate</c>.</summary>
    internal static readonly Guid MaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");

    /// <summary><c>CODECAPI_AVEncMPVGOPSize</c>.</summary>
    internal static readonly Guid GopSize = new("95f31be2-ff9c-4d6d-b1d7-6bdba0f2b9a7");

    /// <summary><c>CODECAPI_AVEncCommonQualityVsSpeed</c>.</summary>
    internal static readonly Guid QualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");

    /// <summary><c>CODECAPI_AVLowLatencyMode</c>.</summary>
    internal static readonly Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    private readonly IEngineLog _log;
    private nint _codecApi;

    private CodecApi(nint codecApi, IEngineLog log)
    {
        _codecApi = codecApi;
        _log = log;
    }

    /// <summary>
    /// Wraps the transform's <c>ICodecAPI</c>, or returns null when the MFT does
    /// not expose one.
    /// </summary>
    internal static CodecApi? TryCreate(nint transformUnknown, IEngineLog log)
    {
        var pointer = Interop.ComHelpers.TryQueryInterface(transformUnknown, ICodecApiIid);
        if (pointer == 0)
        {
            log.Debug("Encoder does not expose ICodecAPI; using media-type configuration only.");
            return null;
        }

        return new CodecApi(pointer, log);
    }

    /// <summary>Sets a UINT32-valued parameter. Returns false if the driver refused it.</summary>
    internal bool TrySetUInt32(Guid parameter, uint value, string name)
    {
        if (_codecApi == 0)
        {
            return false;
        }

        // VT_UI4 = 19. A VARIANT is 16 bytes on x86 and 24 on x64; the value
        // lives at offset 8 either way.
        var variant = stackalloc byte[24];
        for (var i = 0; i < 24; i++)
        {
            variant[i] = 0;
        }

        *(ushort*)variant = 19;
        *(uint*)(variant + 8) = value;

        var vtable = *(nint**)_codecApi;

        // ICodecAPI vtable: 3 IsSupported, 4 IsModifiable, 5 GetParameterRange,
        // 6 GetParameterValues, 7 GetDefaultValue, 8 GetValue, 9 SetValue.
        var setValue = (delegate* unmanaged[Stdcall]<nint, Guid*, byte*, int>)vtable[9];

        var hr = setValue(_codecApi, &parameter, variant);
        if (hr < 0)
        {
            _log.Debug($"Encoder rejected {name} = {value} (0x{hr:X8}); keeping the driver default.");
            return false;
        }

        _log.Debug($"Encoder {name} = {value}.");
        return true;
    }

    /// <summary>Whether the driver supports a parameter at all.</summary>
    internal bool IsSupported(Guid parameter)
    {
        if (_codecApi == 0)
        {
            return false;
        }

        var vtable = *(nint**)_codecApi;
        var isSupported = (delegate* unmanaged[Stdcall]<nint, Guid*, int>)vtable[3];
        return isSupported(_codecApi, &parameter) >= 0;
    }

    public void Dispose()
    {
        if (_codecApi != 0)
        {
            Marshal.Release(_codecApi);
            _codecApi = 0;
        }
    }
}

/// <summary>
/// <c>eAVEncCommonRateControlMode</c> values.
/// </summary>
internal static class RateControlModeValues
{
    internal const uint Cbr = 0;
    internal const uint PeakConstrainedVbr = 1;
    internal const uint UnconstrainedVbr = 2;
    internal const uint Quality = 3;
    internal const uint LowDelayVbr = 4;
}
