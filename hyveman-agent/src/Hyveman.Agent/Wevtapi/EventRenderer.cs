using System.Runtime.InteropServices;
using System.Xml.Linq;
using Hyveman.Agent.Wevtapi.Native;

namespace Hyveman.Agent.Wevtapi;

/// <summary>
/// Rendered representation of one Windows event log record, produced by
/// <see cref="EventRenderer"/> from an EVT handle inside the subscribe
/// callback. Carries everything the envelope builder needs (AGENT App. A).
/// </summary>
public sealed class EvtLogEvent
{
    public string Channel = "";                   // actual event-log channel
    public string DedupScope = "";               // idempotency scope; = config entry name (PROTOCOL §11.1)
    public ulong RecordId;
    public DateTime TimeCreatedUtc;
    public int Level;                       // Windows Level: 1=Critical..5=Verbose (0 = unspecified)
    public uint EventId;
    public ushort Task;
    public ushort Opcode;
    public ulong Keywords;
    public string? ProviderName;
    public string? ProviderGuid;
    public string? Computer;
    public Guid? ActivityId;
    public uint? ProcessId;
    public uint? ThreadId;
    public Dictionary<string, string?>? EventData;
    public string? RawXml;                  // EvtRender(EVT_RENDER_EVENT_XML)
    public string? Message;                 // EvtFormatMessage(EVT_FORMAT_MESSAGE_MESSAGE)
    public string? BookmarkXml;             // EvtRender(EVT_RENDER_BOOKMARK) — position of THIS event
    public int Epoch;                       // channel reset epoch stamped at delivery (PROTOCOL §11.1)
}

/// <summary>
/// Renders EVT handles to <see cref="EvtLogEvent"/> (AGENT.md §6.1, App. A).
/// All calls are local & cheap enough for the subscribe callback.
/// </summary>
public static class EventRenderer
{
    private static readonly XNamespace EvNs = "http://schemas.microsoft.com/win/2004/08/events/event";

    // Rendering context for EVT_RENDER_EVENT_VALUES: ValuePaths=NULL +
    // EvtRenderContextSystem renders the standard top-level (System) properties
    // in the documented order. One handle for the process lifetime.
    private static readonly Lazy<IntPtr> RenderContext = new(() =>
    {
        var ctx = WevtApiNative.EvtCreateRenderContext(0, IntPtr.Zero, WevtApiNative.EvtRenderContextSystem);
        if (ctx == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "EvtCreateRenderContext failed");
        return ctx;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Renders the event; never throws (runs on the ETW thread-pool thread —
    /// the callback contract, AGENT §6.3). Returns null when unusable.
    /// </summary>
    public static EvtLogEvent? Render(IntPtr eventHandle, string channel, IntPtr? bookmarkHandle = null)
    {
        EvtVariant[]? props;
        try
        {
            props = RenderValues(eventHandle);
        }
        catch (Exception)
        {
            return null;
        }

        if (props is null || props.Length == 0)
            return null;

        var ev = new EvtLogEvent { Channel = channel };
        try
        {
            ReadValues(ev, props);
            var xml = RenderString(eventHandle, WevtApiNative.EvtRenderEventXml);
            ev.RawXml = xml;
            ev.EventData = ParseEventData(xml);
            ev.Message = FormatMessage(eventHandle);
            if (bookmarkHandle is { } bh)
                ev.BookmarkXml = RenderBookmark(bh, eventHandle);
        }
        catch (Exception)
        {
            // Partial render is still usable; the envelope builder tolerates nulls.
        }

        return ev;
    }

    internal static EvtVariant[]? RenderValues(IntPtr eventHandle)
    {
        var ctx = RenderContext.Value;

        // Size probe: fails with ERROR_INSUFFICIENT_BUFFER by design, setting `used`.
        if (!WevtApiNative.EvtRender(ctx, eventHandle, WevtApiNative.EvtRenderEventValues,
                0, IntPtr.Zero, out var used, out var count))
        {
            if (Marshal.GetLastWin32Error() != WevtApiNative.ErrorInsufficientBuffer)
                return null;
        }
        if (used == 0)
            return null;

        var buf = Marshal.AllocHGlobal((int)used);
        try
        {
            if (!WevtApiNative.EvtRender(ctx, eventHandle, WevtApiNative.EvtRenderEventValues,
                    used, buf, out _, out _))
                return null;

            var variants = new EvtVariant[count];
            for (int i = 0; i < count; i++)
            {
                var off = IntPtr.Add(buf, i * Marshal.SizeOf<EvtVariant>());
                variants[i] = Marshal.PtrToStructure<EvtVariant>(off);
            }
            return variants;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>
    /// Advances a per-subscriber bookmark handle to the event and returns the
    /// bookmark XML for the event's position (persisted per AGENT §6.6).
    /// </summary>
    public static string? RenderBookmark(IntPtr bookmarkHandle, IntPtr eventHandle)
    {
        if (!WevtApiNative.EvtUpdateBookmark(bookmarkHandle, eventHandle))
            return null;
        return RenderString(bookmarkHandle, WevtApiNative.EvtRenderBookmark);
    }

    /// <summary>
    /// EvtFormatMessage(EVT_FORMAT_MESSAGE_MESSAGE). Null when the provider has
    /// no message table (ERROR_EVT_MESSAGE_NOT_FOUND family) — the envelope
    /// then falls back to a summary text.
    /// </summary>
    public static string? FormatMessage(IntPtr eventHandle)
    {
        if (!WevtApiNative.EvtFormatMessage(IntPtr.Zero, eventHandle, 0, 0, IntPtr.Zero,
                WevtApiNative.EvtFormatMessageMessage, 0, IntPtr.Zero, out var used))
        {
            // ERROR_INSUFFICIENT_BUFFER is the normal size-probe outcome;
            // ERROR_EVT_MESSAGE_NOT_FOUND family means "no message table".
            var err = Marshal.GetLastWin32Error();
            if (err != WevtApiNative.ErrorInsufficientBuffer)
                return null;
        }
        if (used == 0)
            return null;

        var buf = Marshal.AllocHGlobal((int)used * 2);
        try
        {
            if (!WevtApiNative.EvtFormatMessage(IntPtr.Zero, eventHandle, 0, 0, IntPtr.Zero,
                    WevtApiNative.EvtFormatMessageMessage, used * 2, buf, out _))
                return null;
            return Marshal.PtrToStringUni(buf, (int)used)?.TrimEnd('\0');
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>
    /// Parses &lt;EventData&gt;&lt;Data Name="k"&gt;v&lt;/Data&gt; pairs from the
    /// rendered XML (the render-values output does not include EventData).
    /// </summary>
    public static Dictionary<string, string?>? ParseEventData(string? xml)
    {
        if (string.IsNullOrEmpty(xml))
            return null;

        try
        {
            var doc = XDocument.Parse(xml);
            var eventData = doc.Root?.Element(EvNs + "EventData");
            if (eventData is null)
                return null;

            var dict = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var data in eventData.Elements(EvNs + "Data"))
            {
                var name = (string?)data.Attribute("Name");
                if (name is null)
                    continue;
                dict[name] = data.Value;
            }
            return dict.Count == 0 ? null : dict;
        }
        catch (Exception)
        {
            return null; // malformed XML must never break the callback
        }
    }

    private static string? RenderString(IntPtr eventHandle, uint flag)
    {
        if (!WevtApiNative.EvtRender(IntPtr.Zero, eventHandle, flag, 0, IntPtr.Zero, out var used, out _))
        {
            if (Marshal.GetLastWin32Error() != WevtApiNative.ErrorInsufficientBuffer)
                return null;
        }
        if (used == 0)
            return null;

        var buf = Marshal.AllocHGlobal((int)used);
        try
        {
            if (!WevtApiNative.EvtRender(IntPtr.Zero, eventHandle, flag, used, buf, out _, out _))
                return null;
            return Marshal.PtrToStringUni(buf, (int)(used / 2))?.TrimEnd('\0');
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static void ReadValues(EvtLogEvent ev, EvtVariant[] props)
    {
        // Documented render order (EvtRender docs): 0 Provider Name, 1 Provider
        // Guid, 2 EventID, 3 Qualifiers, 4 Level, 5 Task, 6 Opcode, 7 Keywords,
        // 8 TimeCreated, 9 EventRecordID, 10 ActivityID, 11 RelatedActivityID,
        // 12 ProcessID, 13 ThreadID, 14 Channel, 15 Computer, 16 UserID, 17 Version
        if (props.Length > 0) ev.ProviderName = GetString(props[0]);
        if (props.Length > 1) ev.ProviderGuid = GetGuid(props[1])?.ToString();
        if (props.Length > 2) ev.EventId = GetUInt(props[2]);
        if (props.Length > 4) ev.Level = (int)GetUInt(props[4]);
        if (props.Length > 5) ev.Task = (ushort)GetUInt(props[5]);
        if (props.Length > 6) ev.Opcode = (ushort)GetUInt(props[6]);
        if (props.Length > 7) ev.Keywords = GetULong(props[7]);
        if (props.Length > 8) ev.TimeCreatedUtc = GetFileTime(props[8]) ?? DateTime.MinValue;
        if (props.Length > 9) ev.RecordId = GetULong(props[9]);
        if (props.Length > 10) ev.ActivityId = GetGuid(props[10]);
        if (props.Length > 12) ev.ProcessId = (uint?)GetULongNullable(props[12]);
        if (props.Length > 13) ev.ThreadId = (uint?)GetULongNullable(props[13]);
        if (props.Length > 15) ev.Computer = GetString(props[15]);
    }

    private static string? GetString(EvtVariant v)
    {
        if (v.Type == WevtApiNative.EvtVarTypeNull) return null;
        if (v.Type == WevtApiNative.EvtVarTypeString) return Marshal.PtrToStringUni(v.Union.Pointer);
        if (v.Type == WevtApiNative.EvtVarTypeAnsiString) return Marshal.PtrToStringAnsi(v.Union.Pointer);
        return null;
    }

    private static Guid? GetGuid(EvtVariant v)
    {
        if (v.Type == WevtApiNative.EvtVarTypeNull || v.Union.Pointer == IntPtr.Zero) return null;
        if (v.Type == WevtApiNative.EvtVarTypeGuid) return Marshal.PtrToStructure<Guid>(v.Union.Pointer);
        return null;
    }

    private static uint GetUInt(EvtVariant v) => v.Type switch
    {
        WevtApiNative.EvtVarTypeByte => v.Union.ByteVal,
        WevtApiNative.EvtVarTypeUInt16 => v.Union.UInt16Val,
        WevtApiNative.EvtVarTypeInt16 => (ushort)v.Union.Int16Val,
        WevtApiNative.EvtVarTypeUInt32 => v.Union.UInt32Val,
        WevtApiNative.EvtVarTypeInt32 => (uint)v.Union.Int32Val,
        _ => 0
    };

    private static ulong GetULong(EvtVariant v) => v.Type switch
    {
        WevtApiNative.EvtVarTypeByte => v.Union.ByteVal,
        WevtApiNative.EvtVarTypeUInt16 => v.Union.UInt16Val,
        WevtApiNative.EvtVarTypeUInt32 => v.Union.UInt32Val,
        WevtApiNative.EvtVarTypeInt32 => (ulong)v.Union.Int32Val,
        WevtApiNative.EvtVarTypeUInt64 => v.Union.UInt64Val,
        WevtApiNative.EvtVarTypeHexInt64 => v.Union.UInt64Val,
        WevtApiNative.EvtVarTypeHexInt32 => v.Union.UInt32Val,
        _ => 0
    };

    private static ulong? GetULongNullable(EvtVariant v)
    {
        if (v.Type == WevtApiNative.EvtVarTypeNull) return null;
        return GetULong(v);
    }

    private static DateTime? GetFileTime(EvtVariant v)
    {
        if (v.Type == WevtApiNative.EvtVarTypeNull) return null;
        if (v.Type == WevtApiNative.EvtVarTypeFileTime) return DateTime.FromFileTimeUtc(v.Union.Int64Val);
        if (v.Type == WevtApiNative.EvtVarTypeSysTime) return DateTime.FromFileTimeUtc(v.Union.Int64Val);
        return null;
    }
}
