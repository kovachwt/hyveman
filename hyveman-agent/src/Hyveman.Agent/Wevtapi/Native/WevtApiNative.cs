using System.Runtime.InteropServices;

namespace Hyveman.Agent.Wevtapi.Native;

/// <summary>PInvoke layer over wevtapi.dll (AGENT.md §6.1). All handles must be EvtClose'd.</summary>
public static partial class WevtApiNative
{
    internal const string Dll = "wevtapi.dll";

    // ---- EvtSubscribe flags ----
    public const uint EvtSubscribeToFutureEvents = 1;
    public const uint EvtSubscribeStartAfterBookmark = 3;
    public const uint EvtSubscribeTolerateQueryErrors = 0x1000;

    // ---- EvtRender flags ----
    public const uint EvtRenderEventValues = 0;
    public const uint EvtRenderEventXml = 1;
    public const uint EvtRenderBookmark = 2;

    // ---- EvtFormatMessage flags ----
    // Only the MESSAGE render is used in v1; the other flags (Event/Level/
    // Task/Opcode/Keyword/Channel/Provider/Id/XML) are reserved for §6.1
    // completeness if the agent ever renders display strings for them.
    public const uint EvtFormatMessageMessage = 10;

    // ---- EVT_VARIANT_TYPE ----
    internal const uint EvtVarTypeNull = 0;
    internal const uint EvtVarTypeString = 1;
    internal const uint EvtVarTypeAnsiString = 2;
    internal const uint EvtVarTypeSByte = 3;
    internal const uint EvtVarTypeByte = 4;
    internal const uint EvtVarTypeInt16 = 5;
    internal const uint EvtVarTypeUInt16 = 6;
    internal const uint EvtVarTypeInt32 = 7;
    internal const uint EvtVarTypeUInt32 = 8;
    internal const uint EvtVarTypeInt64 = 9;
    internal const uint EvtVarTypeUInt64 = 10;
    internal const uint EvtVarTypeSingle = 11;
    internal const uint EvtVarTypeDouble = 12;
    internal const uint EvtVarTypeBoolean = 13;
    internal const uint EvtVarTypeBinary = 14;
    internal const uint EvtVarTypeGuid = 15;
    internal const uint EvtVarTypeSizeT = 16;
    internal const uint EvtVarTypeFileTime = 17;
    internal const uint EvtVarTypeSysTime = 18;
    internal const uint EvtVarTypeSid = 19;
    internal const uint EvtVarTypeHexInt32 = 20;
    internal const uint EvtVarTypeHexInt64 = 21;
    internal const uint EvtVarTypeEvtHandle = 32;
    internal const uint EvtVarTypeEvtXml = 35;
    internal const uint EvtVarTypeStringArr = 128;
    internal const uint EvtVarTypeByteArr = 129;
    internal const uint EvtVarTypeSidArr = 130;

    // ---- subscribe callback actions ----
    public const uint EvtSubscribeActionError = 0;
    public const uint EvtSubscribeActionDeliver = 1;

    // ---- well-known Win32 error codes we branch on ----
    internal const int ErrorEvtChannelNotFound = 15007;
    internal const int ErrorEvtQueryResultInvalidPosition = 15021;
    internal const int ErrorEvtMessageNotFound = 15005;
    internal const int ErrorEvtMessageIdNotFound = 15027;
    internal const int ErrorEvtMessageUnresolvableValue = 15011;
    internal const int ErrorEvtMessageResourceNotFound = 15028;
    internal const int ErrorInvalidParameter = 87;
    internal const int ErrorInsufficientBuffer = 122;
    internal const int ErrorAccessDenied = 5;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate uint EvtSubscribeCallback(uint action, IntPtr userContext, IntPtr eventHandle);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr EvtSubscribe(
        IntPtr session,
        IntPtr signalEvent,
        [MarshalAs(UnmanagedType.LPWStr)] string channelPath,
        [MarshalAs(UnmanagedType.LPWStr)] string? query,
        IntPtr bookmark,
        IntPtr context,
        EvtSubscribeCallback callback,
        uint flags);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr EvtCreateBookmark([MarshalAs(UnmanagedType.LPWStr)] string? bookmarkXml);

    // EVT_RENDER_CONTEXT_FLAGS
    public const uint EvtRenderContextSystem = 1;

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr EvtCreateRenderContext(int valuePathsCount, IntPtr valuePaths, uint flags);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EvtUpdateBookmark(IntPtr bookmark, IntPtr eventHandle);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EvtFormatMessage(
        IntPtr publisherMetadata,
        IntPtr eventHandle,
        uint messageId,
        uint valueCount,
        IntPtr values,
        uint flags,
        uint bufferSize,
        IntPtr buffer,
        out uint bufferUsed);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EvtRender(
        IntPtr context,
        IntPtr fragment,
        uint flags,
        uint bufferSize,
        IntPtr buffer,
        out uint bufferUsed,
        out uint propertyCount);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EvtClose(IntPtr handle);

    public static void ThrowLastError(string op)
    {
        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"{op} failed");
    }
}

/// <summary>
/// EVT_VARIANT (wevtapi.h). Union is pointer-sized; Count/Type follow.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct EvtVariant
{
    public EvtVariantUnion Union;
    public uint Count;
    public uint Type;
}

[StructLayout(LayoutKind.Explicit)]
public struct EvtVariantUnion
{
    [FieldOffset(0)] internal byte ByteVal;
    [FieldOffset(0)] internal short Int16Val;
    [FieldOffset(0)] internal ushort UInt16Val;
    [FieldOffset(0)] internal int Int32Val;
    [FieldOffset(0)] internal uint UInt32Val;
    [FieldOffset(0)] internal long Int64Val;
    [FieldOffset(0)] internal ulong UInt64Val;
    [FieldOffset(0)] internal float SingleVal;
    [FieldOffset(0)] internal double DoubleVal;
    [FieldOffset(0)] internal uint BooleanVal;   // BOOL
    [FieldOffset(0)] internal IntPtr Pointer;    // string / binary / guid / sid / array
}
