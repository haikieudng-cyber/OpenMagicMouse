using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using OpenMagicMouse.Models;

namespace OpenMagicMouse.Backend;

/// <summary>
/// Per-ACL-handle stateful L2CAP PDU reassembler for fragmented Bluetooth DH1 packets
/// (2+ finger Magic Mouse 2 reports exceed the 27-byte DH1 single-slot MTU).
/// </summary>
public sealed class L2capReassembler
{
    private struct PartialPdu
    {
        public ushort Handle;
        public ushort ExpectedLen;
        public ushort Cid;
        public int Offset;
        public byte[]? Buffer;
    }

    private readonly PartialPdu[] _slots = new PartialPdu[4];

    public void Reset(ushort handle)
    {
        for (int i = 0; i < _slots.Length; i++)
        {
            if (_slots[i].Handle == handle && _slots[i].ExpectedLen != 0)
            {
                _slots[i].ExpectedLen = 0;
                _slots[i].Offset = 0;
            }
        }
    }

    public void Start(ushort handle, ushort expectedLen, ushort cid, ReadOnlySpan<byte> initialChunk)
    {
        if (expectedLen == 0 || expectedLen > 512 || initialChunk.Length >= expectedLen)
        {
            Reset(handle);
            return;
        }

        int targetIdx = -1;
        for (int i = 0; i < _slots.Length; i++)
        {
            if (_slots[i].Handle == handle)
            {
                targetIdx = i;
                break;
            }
            if (targetIdx < 0 && _slots[i].ExpectedLen == 0)
            {
                targetIdx = i;
            }
        }

        if (targetIdx < 0) targetIdx = 0;

        ref var slot = ref _slots[targetIdx];
        slot.Buffer ??= new byte[512];
        slot.Handle = handle;
        slot.ExpectedLen = expectedLen;
        slot.Cid = cid;
        initialChunk.CopyTo(slot.Buffer);
        slot.Offset = initialChunk.Length;
    }

    public bool Append(ushort handle, ReadOnlySpan<byte> continuationChunk, out ushort cid, out ReadOnlySpan<byte> completePayload)
    {
        cid = 0;
        completePayload = default;

        for (int i = 0; i < _slots.Length; i++)
        {
            ref var slot = ref _slots[i];
            if (slot.Handle != handle || slot.ExpectedLen == 0 || slot.Buffer == null)
            {
                continue;
            }

            int remaining = slot.ExpectedLen - slot.Offset;
            int toCopy = Math.Min(remaining, continuationChunk.Length);
            if (toCopy <= 0 || slot.Offset + toCopy > slot.Buffer.Length)
            {
                slot.ExpectedLen = 0;
                slot.Offset = 0;
                return false;
            }

            continuationChunk.Slice(0, toCopy).CopyTo(slot.Buffer.AsSpan(slot.Offset));
            slot.Offset += toCopy;

            if (slot.Offset >= slot.ExpectedLen)
            {
                cid = slot.Cid;
                int len = slot.ExpectedLen;
                slot.ExpectedLen = 0;
                slot.Offset = 0;
                completePayload = slot.Buffer.AsSpan(0, len);
                return true;
            }

            return false;
        }

        return false;
    }
}

/// <summary>
/// Decodes raw Microsoft-Windows-BTH-BTHPORT Event 402 (HCIRAW) payloads and
/// Apple Magic Mouse 2 (0x004C:0x0269) Bluetooth HID Report 0x12 frames.
/// </summary>
public static class MagicMousePacketDecoder
{
    public const byte BipHciCmdTx = 0x01;
    public const byte BipHciEvtRx = 0x02;
    public const byte BipHciAclRx = 0x03; // Controller -> Host (Inbound ACL from Magic Mouse 2)
    public const byte BipHciAclTx = 0x04; // Host -> Controller (Outbound ACL)

    public static bool TryDecodeEvent402(
        ReadOnlySpan<byte> userData,
        long timestampMs,
        ref int batteryPercent,
        ref bool isConnected,
        out TouchFrame? frame)
        => TryDecodeEvent402(userData, timestampMs, null, ref batteryPercent, ref isConnected, out _, out frame);

    public static bool TryDecodeEvent402(
        ReadOnlySpan<byte> userData,
        long timestampMs,
        L2capReassembler? reassembler,
        ref int batteryPercent,
        ref bool isConnected,
        out bool wasScrubbed,
        out TouchFrame? frame)
    {
        wasScrubbed = false;
        frame = null;
        if (userData.Length < 8)
        {
            return false;
        }

        byte bipType = userData[3];
        uint bipLen = BinaryPrimitives.ReadUInt32LittleEndian(userData.Slice(4, 4));
        if (bipLen == 0 || bipLen > (uint)(userData.Length - 8))
        {
            return false;
        }

        var hci = userData.Slice(8, (int)bipLen);

        // Reject host -> controller TX packets
        if (bipType == BipHciCmdTx || bipType == BipHciAclTx)
        {
            return false;
        }

        if (bipType == BipHciEvtRx)
        {
            // HCI_Connection_Complete: event code 0x03, len >= 11, status == 0x00
            if (hci.Length >= 11 && hci[0] == 0x03 && hci[2] == 0x00)
            {
                isConnected = true;
            }
            // HCI_Disconnection_Complete: event code 0x05, status == 0x00
            else if (hci.Length >= 5 && hci[0] == 0x05 && hci[2] == 0x00 && reassembler != null)
            {
                ushort discHandle = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(hci.Slice(3, 2)) & 0x0FFF);
                reassembler.Reset(discHandle);
            }
            return false;
        }

        if (bipType != BipHciAclRx || hci.Length < 4)
        {
            return false;
        }

        ushort handleAndFlags = BinaryPrimitives.ReadUInt16LittleEndian(hci.Slice(0, 2));
        ushort handle = (ushort)(handleAndFlags & 0x0FFF);
        int pbFlag = (handleAndFlags >> 12) & 0x03;
        ushort aclLen = BinaryPrimitives.ReadUInt16LittleEndian(hci.Slice(2, 2));

        // bthport.sys truncates HCI ACL payloads to header-only when EtwLogSensitiveData != 1
        if (4 + aclLen > hci.Length)
        {
            if (aclLen > 4)
            {
                wasScrubbed = true;
            }
            return false;
        }

        // Start of L2CAP PDU (PB = 0b10 auto-flushable / 0b00 non-auto-flushable)
        if (pbFlag == 0x02 || pbFlag == 0x00)
        {
            if (aclLen < 4 || hci.Length < 8)
            {
                return false;
            }

            ushort l2capLen = BinaryPrimitives.ReadUInt16LittleEndian(hci.Slice(4, 2));
            ushort cid = BinaryPrimitives.ReadUInt16LittleEndian(hci.Slice(6, 2));
            if (l2capLen < 2 || l2capLen > 512)
            {
                reassembler?.Reset(handle);
                return false;
            }

            if (aclLen == l2capLen + 4)
            {
                reassembler?.Reset(handle);
                return TryDecodeL2capPayload(hci.Slice(8, l2capLen), timestampMs, ref batteryPercent, ref isConnected, out frame);
            }

            if (aclLen < l2capLen + 4 && reassembler != null && aclLen > 4)
            {
                reassembler.Start(handle, l2capLen, cid, hci.Slice(8, aclLen - 4));
            }
            else
            {
                reassembler?.Reset(handle);
            }

            return false;
        }

        // Continuation fragment (PB = 0b01)
        if (pbFlag == 0x01 && reassembler != null && aclLen > 0)
        {
            if (reassembler.Append(handle, hci.Slice(4, aclLen), out _, out var fullL2cap))
            {
                return TryDecodeL2capPayload(fullL2cap, timestampMs, ref batteryPercent, ref isConnected, out frame);
            }
        }

        return false;
    }

    private static bool TryDecodeL2capPayload(
        ReadOnlySpan<byte> l2cap,
        long timestampMs,
        ref int batteryPercent,
        ref bool isConnected,
        out TouchFrame? frame)
    {
        frame = null;
        if (l2cap.Length < 2)
        {
            return false;
        }

        byte hidHdr = l2cap[0];
        byte reportId = l2cap[1];

        // Passive Battery Capture (Feature/Input report 0x47 [3 bytes] or 0x90 [4 bytes: status, pct])
        if ((hidHdr == 0xA3 || hidHdr == 0xA1) && reportId == 0x47 && l2cap.Length >= 3)
        {
            int pct = l2cap[2];
            if (pct >= 1 && pct <= 100)
            {
                batteryPercent = pct;
                isConnected = true;
            }
            return false;
        }

        if ((hidHdr == 0xA3 || hidHdr == 0xA1) && reportId == 0x90 && l2cap.Length >= 4)
        {
            int pct = l2cap[3];
            if (pct >= 1 && pct <= 100)
            {
                batteryPercent = pct;
                isConnected = true;
            }
            return false;
        }

        // Apple Magic Mouse 2 multi-touch Input Report 0x12
        if (hidHdr == 0xA1 && reportId == 0x12)
        {
            isConnected = true;
            return TryDecodeReport12(l2cap.Slice(1), timestampMs, batteryPercent, out frame);
        }

        return false;
    }

    public static bool TryDecodeReport12(
        ReadOnlySpan<byte> data,
        long timestampMs,
        int batteryPercent,
        out TouchFrame? frame)
    {
        frame = null;
        if (data.Length < 8 || data[0] != 0x12)
        {
            return false;
        }

        bool validLen = data.Length == 8 ||
                        (data.Length >= 14 && (data.Length - 14) % 8 == 0 && (data.Length - 14) / 8 <= 15);
        if (!validLen)
        {
            return false;
        }

        byte clicks = data[1];
        short dx = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(2, 2));
        short dy = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(4, 2));
        int npoints = data.Length >= 14 ? (data.Length - 14) / 8 : 0;

        var fingers = npoints > 0 ? new TouchFinger[npoints] : [];
        for (int i = 0; i < npoints; i++)
        {
            var t = data.Slice(14 + i * 8, 8);
            int id = ((t[6] << 2) | (t[5] >> 6)) & 0x0F;
            int x = ((t[1] << 28) | (t[0] << 20)) >> 20;
            int y = ((t[2] << 24) | (t[1] << 16)) >> 20;
            byte touchMajor = t[3];
            byte touchMinor = t[4];
            byte size = (byte)(t[5] & 0x3F);
            byte state = (byte)(t[7] & 0xF0);

            fingers[i] = new TouchFinger(id, x, y, touchMajor, touchMinor, size, state);
        }

        frame = new TouchFrame
        {
            TimestampMs = timestampMs,
            Clicks = clicks,
            Dx = dx,
            Dy = dy,
            BatteryPercent = batteryPercent,
            IsConnected = true,
            Fingers = fingers
        };
        return true;
    }
}

public static class TouchWireProtocol
{
    public static string SerializeHello(
        int batteryPercent,
        bool isConnected,
        long decodedFrames = 0,
        long scrubbedPackets = 0,
        long totalEvent402 = 0,
        int maxEvent402Bytes = 0)
        => $"HELLO|OpenMagicMouseService|1.1|{batteryPercent}|{(isConnected ? 1 : 0)}|{decodedFrames}|{scrubbedPackets}|{totalEvent402}|{maxEvent402Bytes}";

    public static bool TryParseHello(string line, out int batteryPercent, out bool isConnected)
        => TryParseHello(line, out batteryPercent, out isConnected, out _, out _, out _, out _);

    public static bool TryParseHello(
        string line,
        out int batteryPercent,
        out bool isConnected,
        out long decodedFrames,
        out long scrubbedPackets,
        out long totalEvent402,
        out int maxEvent402Bytes)
    {
        batteryPercent = -1;
        isConnected = false;
        decodedFrames = 0;
        scrubbedPackets = 0;
        totalEvent402 = 0;
        maxEvent402Bytes = 0;
        if (!line.StartsWith("HELLO|", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = line.Split('|');
        if (parts.Length < 5)
        {
            return false;
        }

        int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out batteryPercent);
        isConnected = parts[4] == "1";

        if (parts.Length >= 9)
        {
            long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out decodedFrames);
            long.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out scrubbedPackets);
            long.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out totalEvent402);
            int.TryParse(parts[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out maxEvent402Bytes);
        }
        return true;
    }

    public static string SerializeFrame(TouchFrame frame)
    {
        var sb = new StringBuilder(128);
        sb.Append("FRAME|")
          .Append(frame.TimestampMs.ToString(CultureInfo.InvariantCulture)).Append('|')
          .Append(frame.Clicks.ToString(CultureInfo.InvariantCulture)).Append('|')
          .Append(frame.Dx.ToString(CultureInfo.InvariantCulture)).Append('|')
          .Append(frame.Dy.ToString(CultureInfo.InvariantCulture)).Append('|')
          .Append(frame.BatteryPercent.ToString(CultureInfo.InvariantCulture)).Append('|')
          .Append(frame.Fingers.Length.ToString(CultureInfo.InvariantCulture)).Append('|');

        for (int i = 0; i < frame.Fingers.Length; i++)
        {
            if (i > 0) sb.Append(';');
            var f = frame.Fingers[i];
            sb.Append(f.Id.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(f.X.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(f.Y.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(f.TouchMajor.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(f.TouchMinor.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(f.Size.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(f.State.ToString(CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    public static bool TryParseFrame(string line, out TouchFrame? frame)
    {
        frame = null;
        if (!line.StartsWith("FRAME|", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = line.Split('|');
        if (parts.Length < 8)
        {
            return false;
        }

        if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ts) ||
            !byte.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte clicks) ||
            !short.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out short dx) ||
            !short.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out short dy) ||
            !int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int batt) ||
            !int.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
        {
            return false;
        }

        var fingers = count > 0 ? new TouchFinger[count] : [];
        if (count > 0 && !string.IsNullOrEmpty(parts[7]))
        {
            var items = parts[7].Split(';');
            if (items.Length != count)
            {
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                var fields = items[i].Split(',');
                if (fields.Length != 7 ||
                    !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ||
                    !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
                    !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) ||
                    !byte.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte maj) ||
                    !byte.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte min) ||
                    !byte.TryParse(fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte sz) ||
                    !byte.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte st))
                {
                    return false;
                }

                fingers[i] = new TouchFinger(id, x, y, maj, min, sz, st);
            }
        }

        frame = new TouchFrame
        {
            TimestampMs = ts,
            Clicks = clicks,
            Dx = dx,
            Dy = dy,
            BatteryPercent = batt,
            IsConnected = true,
            Fingers = fingers
        };
        return true;
    }
}
