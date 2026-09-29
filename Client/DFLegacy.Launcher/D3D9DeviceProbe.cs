using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DFLegacy.Launcher;

internal static class D3D9DeviceProbe
{
    private const uint D3DSdkVersion = 32;
    private const uint D3DDevTypeHal = 1;
    private const uint D3DCreateFpuPreserve = 0x00000002;
    private const uint D3DCreateMultithreaded = 0x00000004;
    private const uint D3DCreateSoftwareVertexProcessing = 0x00000020;
    private const uint D3DSwapEffectDiscard = 1;
    private const uint ProbeWidth = 800;
    private const uint ProbeHeight = 600;
    private const int SmCMonitors = 80;
    private const int SmRemoteSession = 0x1000;

    internal static async Task<D3D9ProbeResult> WaitForDeviceAsync(
        int adapter,
        TimeSpan timeout,
        TimeSpan retryInterval)
    {
        var started = Stopwatch.GetTimestamp();
        var attempt = 0;
        D3D9ProbeResult lastResult;

        do
        {
            attempt++;
            lastResult = Probe(adapter) with
            {
                Attempt = attempt,
                Elapsed = Stopwatch.GetElapsedTime(started)
            };
            if (lastResult.Available)
            {
                return lastResult;
            }

            var remaining = timeout - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return lastResult;
            }

            await Task.Delay(remaining < retryInterval ? remaining : retryInterval);
        }
        while (true);
    }

    internal static D3D9ProbeResult Probe(int adapter)
    {
        var monitorCount = GetSystemMetrics(SmCMonitors);
        var remoteSession = GetSystemMetrics(SmRemoteSession) != 0;
        nint d3d = 0;
        nint device = 0;
        ProbeWindow? window = null;

        try
        {
            d3d = Direct3DCreate9(D3DSdkVersion);
            if (d3d == 0)
            {
                return Failure(
                    adapter,
                    monitorCount,
                    remoteSession,
                    "Direct3DCreate9 returned null");
            }

            var adapterCount = GetMethod<GetAdapterCountDelegate>(d3d, 4)(d3d);
            if (adapterCount == 0)
            {
                return Failure(
                    adapter,
                    monitorCount,
                    remoteSession,
                    "no_d3d9_adapters",
                    adapterCount);
            }

            if (adapter < 0 || (uint)adapter >= adapterCount)
            {
                return Failure(
                    adapter,
                    monitorCount,
                    remoteSession,
                    $"adapter_out_of_range: {adapter} >= {adapterCount}",
                    adapterCount);
            }

            var getDisplayMode = GetMethod<GetAdapterDisplayModeDelegate>(d3d, 8);
            var displayModeResult = getDisplayMode(d3d, (uint)adapter, out var displayMode);
            if (displayModeResult != 0)
            {
                return Failure(
                    adapter,
                    monitorCount,
                    remoteSession,
                    $"GetAdapterDisplayMode failed ({FormatHResult(displayModeResult)})",
                    adapterCount);
            }

            if (displayMode.Width == 0 || displayMode.Height == 0)
            {
                return Failure(
                    adapter,
                    monitorCount,
                    remoteSession,
                    "adapter_has_no_active_display_mode",
                    adapterCount,
                    displayMode);
            }

            var checkDeviceType = GetMethod<CheckDeviceTypeDelegate>(d3d, 9);
            var checkDeviceTypeResult = checkDeviceType(
                d3d,
                (uint)adapter,
                D3DDevTypeHal,
                displayMode.Format,
                displayMode.Format,
                1);

            window = new ProbeWindow();
            var presentParameters = new D3DPresentParameters
            {
                BackBufferWidth = ProbeWidth,
                BackBufferHeight = ProbeHeight,
                BackBufferFormat = 0,
                BackBufferCount = 1,
                SwapEffect = D3DSwapEffectDiscard,
                DeviceWindow = window.Handle,
                Windowed = 1
            };
            var behaviorFlags = D3DCreateFpuPreserve
                | D3DCreateMultithreaded
                | D3DCreateSoftwareVertexProcessing;
            var createDeviceResult = GetMethod<CreateDeviceDelegate>(d3d, 16)(
                d3d,
                (uint)adapter,
                D3DDevTypeHal,
                window.Handle,
                behaviorFlags,
                ref presentParameters,
                out device);
            if (createDeviceResult != 0 || device == 0)
            {
                return Failure(
                    adapter,
                    monitorCount,
                    remoteSession,
                    $"CreateDevice(HAL, windowed) failed ({FormatHResult(createDeviceResult)})",
                    adapterCount,
                    displayMode,
                    checkDeviceTypeResult,
                    createDeviceResult);
            }

            return new D3D9ProbeResult(
                true,
                adapter,
                monitorCount,
                remoteSession,
                adapterCount,
                displayMode.Width,
                displayMode.Height,
                displayMode.RefreshRate,
                checkDeviceTypeResult,
                createDeviceResult,
                "CreateDevice(HAL, windowed) succeeded");
        }
        catch (DllNotFoundException exception)
        {
            return Failure(
                adapter,
                monitorCount,
                remoteSession,
                $"d3d9.dll_load_failed: {exception.Message}");
        }
        catch (Exception exception)
        {
            return Failure(
                adapter,
                monitorCount,
                remoteSession,
                $"probe_exception: {exception.Message}");
        }
        finally
        {
            Release(device);
            window?.Dispose();
            Release(d3d);
        }
    }

    private static D3D9ProbeResult Failure(
        int adapter,
        int monitorCount,
        bool remoteSession,
        string reason,
        uint adapterCount = 0,
        D3DDisplayMode displayMode = default,
        int checkDeviceTypeResult = 0,
        int createDeviceResult = 0) =>
        new(
            false,
            adapter,
            monitorCount,
            remoteSession,
            adapterCount,
            displayMode.Width,
            displayMode.Height,
            displayMode.RefreshRate,
            checkDeviceTypeResult,
            createDeviceResult,
            reason);

    private static T GetMethod<T>(nint instance, int index)
        where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(instance);
        var method = Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(method);
    }

    private static void Release(nint instance)
    {
        if (instance == 0)
        {
            return;
        }

        try
        {
            GetMethod<ReleaseDelegate>(instance, 2)(instance);
        }
        catch
        {
            // Probe cleanup must not hide the actual availability result.
        }
    }

    private static string FormatHResult(int value)
    {
        var hex = $"0x{unchecked((uint)value):X8}";
        return unchecked((uint)value) switch
        {
            0x00000000 => $"{hex} D3D_OK",
            0x88760827 => $"{hex} D3DERR_INVALIDCALL",
            0x88760868 => $"{hex} D3DERR_DEVICELOST",
            0x88760869 => $"{hex} D3DERR_DEVICENOTRESET",
            0x8876086A => $"{hex} D3DERR_NOTAVAILABLE",
            0x8876086C => $"{hex} D3DERR_OUTOFVIDEOMEMORY",
            0x8876086E => $"{hex} D3DERR_INVALIDDEVICE",
            _ => hex
        };
    }

    [DllImport("d3d9.dll", ExactSpelling = true)]
    private static extern nint Direct3DCreate9(uint sdkVersion);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetSystemMetrics(int index);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseDelegate(nint instance);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetAdapterCountDelegate(nint instance);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAdapterDisplayModeDelegate(
        nint instance,
        uint adapter,
        out D3DDisplayMode mode);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CheckDeviceTypeDelegate(
        nint instance,
        uint adapter,
        uint deviceType,
        uint adapterFormat,
        uint backBufferFormat,
        int windowed);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateDeviceDelegate(
        nint instance,
        uint adapter,
        uint deviceType,
        nint focusWindow,
        uint behaviorFlags,
        ref D3DPresentParameters presentationParameters,
        out nint returnedDevice);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct D3DDisplayMode
    {
        internal readonly uint Width;
        internal readonly uint Height;
        internal readonly uint RefreshRate;
        internal readonly uint Format;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DPresentParameters
    {
        internal uint BackBufferWidth;
        internal uint BackBufferHeight;
        internal uint BackBufferFormat;
        internal uint BackBufferCount;
        internal uint MultiSampleType;
        internal uint MultiSampleQuality;
        internal uint SwapEffect;
        internal nint DeviceWindow;
        internal int Windowed;
        internal int EnableAutoDepthStencil;
        internal uint AutoDepthStencilFormat;
        internal uint Flags;
        internal uint FullScreenRefreshRateInHz;
        internal uint PresentationInterval;
    }

    private sealed class ProbeWindow : NativeWindow, IDisposable
    {
        internal ProbeWindow()
        {
            CreateHandle(new CreateParams
            {
                Caption = "DFLegacy D3D9 probe",
                X = 0,
                Y = 0,
                Width = (int)ProbeWidth,
                Height = (int)ProbeHeight,
                Style = 0x00CF0000
            });
        }

        public void Dispose()
        {
            DestroyHandle();
        }
    }
}

internal sealed record D3D9ProbeResult(
    bool Available,
    int Adapter,
    int MonitorCount,
    bool RemoteSession,
    uint AdapterCount,
    uint Width,
    uint Height,
    uint RefreshRate,
    int CheckDeviceTypeResult,
    int CreateDeviceResult,
    string Reason)
{
    internal int Attempt { get; init; }

    internal TimeSpan Elapsed { get; init; }

    internal string Summary =>
        Available
            ? $"adapter={Adapter} display={Width}x{Height} back_buffer=800x600 " +
              $"create_device=0x{unchecked((uint)CreateDeviceResult):X8}"
            : $"{Reason}; monitors={MonitorCount}; remote_session={RemoteSession.ToString().ToLowerInvariant()}";
}
