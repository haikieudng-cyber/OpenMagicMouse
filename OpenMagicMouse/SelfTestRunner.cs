using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using OpenMagicMouse.Backend;
using OpenMagicMouse.Gestures;
using OpenMagicMouse.Models;
using OpenMagicMouse.Scrolling;

namespace OpenMagicMouse;

/// <summary>
/// Deterministic offline self-test suite (plus optional live pipe handshake check).
/// Never injects synthetic SendInput events into the desktop.
/// </summary>
public static class SelfTestRunner
{
    public static int Run(bool checkLivePipe)
    {
        try
        {
            VerifyInputStructSize();
            VerifyPacketDecoder();
            VerifyWireRoundTrip();
            VerifyGestureEngineAndPalmRejection();
            VerifyMacScrollPhysicsEngine();

            if (checkLivePipe)
            {
                VerifyLivePipeHandshake();
            }

            Console.WriteLine("SELFTEST: ALL CHECKS PASSED.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SELFTEST FAILED: {ex.Message}");
            return 1;
        }
    }

    private static void Assert(bool cond, string message)
    {
        if (!cond) throw new InvalidOperationException(message);
    }

    private static void VerifyInputStructSize()
    {
        int sz = ActionExecutor.InputStructSize;
        Assert(sz == 40, $"Marshal.SizeOf<INPUT>() must be 40 on x64, got {sz}");
        Console.WriteLine($"[PASS] ActionExecutor.INPUT size = {sz} bytes (x64 ABI verified)");
    }

    private static byte[] MakeEvent402Acl(byte bipType, byte hidHdr, byte[] reportPayload)
    {
        int l2capLen = 1 + reportPayload.Length;
        int aclLen = l2capLen + 4;
        int hciLen = 4 + aclLen;
        byte[] buf = new byte[8 + hciLen];

        buf[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(1, 2), 8);
        buf[3] = bipType;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), (uint)hciLen);

        // HCI ACL header
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8, 2), 0x200C);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(10, 2), (ushort)aclLen);

        // L2CAP header
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(12, 2), (ushort)l2capLen);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(14, 2), 0x0041);

        // HID payload
        buf[16] = hidHdr;
        Array.Copy(reportPayload, 0, buf, 17, reportPayload.Length);
        return buf;
    }

    private static byte[] MakeReport12(byte clicks, short dx, short dy, params TouchFinger[] fingers)
    {
        int len = fingers.Length == 0 ? 8 : 14 + fingers.Length * 8;
        byte[] rep = new byte[len];
        rep[0] = 0x12;
        rep[1] = clicks;
        BinaryPrimitives.WriteInt16LittleEndian(rep.AsSpan(2, 2), dx);
        BinaryPrimitives.WriteInt16LittleEndian(rep.AsSpan(4, 2), dy);

        for (int i = 0; i < fingers.Length; i++)
        {
            var f = fingers[i];
            int o = 14 + i * 8;
            rep[o + 0] = (byte)(f.X & 0xFF);
            rep[o + 1] = (byte)(((f.X >> 8) & 0x0F) | ((f.Y & 0x0F) << 4));
            rep[o + 2] = (byte)((f.Y >> 4) & 0xFF);
            rep[o + 3] = f.TouchMajor;
            rep[o + 4] = f.TouchMinor;
            rep[o + 5] = (byte)((f.Size & 0x3F) | ((f.Id & 0x03) << 6));
            rep[o + 6] = (byte)((f.Id >> 2) & 0x03);
            rep[o + 7] = f.State;
        }
        return rep;
    }

    private static void VerifyPacketDecoder()
    {
        int batt = -1;
        bool conn = false;

        // 1. TX packets must be rejected
        var txPkt = MakeEvent402Acl(MagicMousePacketDecoder.BipHciAclTx, 0xA1, MakeReport12(0, 0, 0));
        Assert(!MagicMousePacketDecoder.TryDecodeEvent402(txPkt, 100, ref batt, ref conn, out _), "TX ACL must be ignored");

        // 2. Passive battery capture (0x47 [2 bytes] & 0x90 [3 bytes: status, pct]) and reject 0% sentinel
        var battPkt = MakeEvent402Acl(MagicMousePacketDecoder.BipHciAclRx, 0xA3, [0x47, 84]);
        Assert(!MagicMousePacketDecoder.TryDecodeEvent402(battPkt, 101, ref batt, ref conn, out _), "Battery packet is not touch frame");
        Assert(batt == 84, $"Expected battery 84, got {batt}");

        var batt90Zero = MakeEvent402Acl(MagicMousePacketDecoder.BipHciAclRx, 0xA1, [0x90, 0x00, 0]);
        Assert(!MagicMousePacketDecoder.TryDecodeEvent402(batt90Zero, 101, ref batt, ref conn, out _), "0x90 battery packet is not touch frame");
        Assert(batt == 84, $"Invalid 0x90 zero byte overwrote battery 84 with {batt}");

        var batt90Valid = MakeEvent402Acl(MagicMousePacketDecoder.BipHciAclRx, 0xA1, [0x90, 0x00, 87]);
        Assert(!MagicMousePacketDecoder.TryDecodeEvent402(batt90Valid, 101, ref batt, ref conn, out _), "0x90 battery packet is not touch frame");
        Assert(batt == 87, $"Expected 0x90 battery 87, got {batt}");

        // 3. 0-finger release packet (8 bytes)
        var pkt0 = MakeEvent402Acl(MagicMousePacketDecoder.BipHciAclRx, 0xA1, MakeReport12(0, 3, -2));
        Assert(MagicMousePacketDecoder.TryDecodeEvent402(pkt0, 102, ref batt, ref conn, out var f0) && f0 != null, "0-finger decode failed");
        Assert(conn && f0!.Fingers.Length == 0 && f0.Dx == 3 && f0.Dy == -2 && f0.BatteryPercent == 87, "0-finger fields mismatch");

        // 4. 1, 2, 3 fingers with signed negative/positive X,Y
        TouchFinger[] sample3 =
        [
            new(1, -450, 920, 40, 28, 22, 0x40),
            new(2, 310, -1200, 38, 26, 20, 0x30),
            new(7, 995, -1600, 50, 30, 32, 0x40)
        ];
        var pkt3 = MakeEvent402Acl(MagicMousePacketDecoder.BipHciAclRx, 0xA1, MakeReport12(1, -12, 45, sample3));
        Assert(MagicMousePacketDecoder.TryDecodeEvent402(pkt3, 103, ref batt, ref conn, out var f3) && f3 != null, "3-finger decode failed");
        Assert(f3!.Clicks == 1 && f3.Fingers.Length == 3, "3-finger header mismatch");
        for (int i = 0; i < 3; i++)
        {
            Assert(f3.Fingers[i] == sample3[i], $"Finger {i} mismatch: got {f3.Fingers[i]} expected {sample3[i]}");
        }

        // 5. Scrubbed Windows 11 Event 402 header-only detection (UserDataLength=16, aclLen=27, l2capLen=23)
        byte[] scrubbedPkt = [0x01, 0x06, 0x00, MagicMousePacketDecoder.BipHciAclRx, 0x08, 0x00, 0x00, 0x00, 0x0A, 0x20, 0x1B, 0x00, 0x17, 0x00, 0x41, 0x00];
        var reassembler = new L2capReassembler();
        Assert(!MagicMousePacketDecoder.TryDecodeEvent402(scrubbedPkt, 104, reassembler, ref batt, ref conn, out bool wasScrubbed, out _) && wasScrubbed,
            "Scrubbed Event 402 was not detected as wasScrubbed=true");

        // 6. 2-Fragment DH1 L2CAP Reassembly test (2-finger Report 0x12 = 30 bytes, l2capLen=31, aclLen=35 -> split 27 + 8)
        TouchFinger[] sample2 =
        [
            new(2, -240, 480, 42, 29, 21, 0x30),
            new(5, 410, -760, 39, 27, 19, 0x40)
        ];
        byte[] rep2 = MakeReport12(0, 7, -9, sample2); // 30 bytes
        int l2Len2 = 1 + rep2.Length; // 31 bytes (0x001F)
        byte[] fullL2Payload = new byte[4 + l2Len2];
        BinaryPrimitives.WriteUInt16LittleEndian(fullL2Payload.AsSpan(0, 2), (ushort)l2Len2);
        BinaryPrimitives.WriteUInt16LittleEndian(fullL2Payload.AsSpan(2, 2), 0x0041);
        fullL2Payload[4] = 0xA1;
        Array.Copy(rep2, 0, fullL2Payload, 5, rep2.Length); // Total 35 bytes

        byte[] frag1 = new byte[8 + 4 + 27];
        frag1[0] = 1; frag1[3] = MagicMousePacketDecoder.BipHciAclRx;
        BinaryPrimitives.WriteUInt32LittleEndian(frag1.AsSpan(4, 4), 4 + 27);
        BinaryPrimitives.WriteUInt16LittleEndian(frag1.AsSpan(8, 2), 0x200A); // PB=0x02, Handle=0x000A
        BinaryPrimitives.WriteUInt16LittleEndian(frag1.AsSpan(10, 2), 27);
        Array.Copy(fullL2Payload, 0, frag1, 12, 27);

        byte[] frag2 = new byte[8 + 4 + 8];
        frag2[0] = 1; frag2[3] = MagicMousePacketDecoder.BipHciAclRx;
        BinaryPrimitives.WriteUInt32LittleEndian(frag2.AsSpan(4, 4), 4 + 8);
        BinaryPrimitives.WriteUInt16LittleEndian(frag2.AsSpan(8, 2), 0x100A); // PB=0x01 continuation, Handle=0x000A
        BinaryPrimitives.WriteUInt16LittleEndian(frag2.AsSpan(10, 2), 8);
        Array.Copy(fullL2Payload, 27, frag2, 12, 8);

        Assert(!MagicMousePacketDecoder.TryDecodeEvent402(frag1, 105, reassembler, ref batt, ref conn, out bool scrub1, out var fPart1) && !scrub1 && fPart1 == null,
            "Fragment 1 of DH1 split should buffer cleanly without error");
        Assert(MagicMousePacketDecoder.TryDecodeEvent402(frag2, 106, reassembler, ref batt, ref conn, out bool scrub2, out var fReassembled) && !scrub2 && fReassembled != null,
            "Fragment 2 of DH1 split failed to reassemble 2-finger Report 0x12");
        Assert(fReassembled!.Fingers.Length == 2 && fReassembled.Dx == 7 && fReassembled.Dy == -9, "Reassembled 2-finger header mismatch");
        Assert(fReassembled.Fingers[0] == sample2[0] && fReassembled.Fingers[1] == sample2[1], "Reassembled 2-finger coordinates mismatch");

        Console.WriteLine("[PASS] MagicMousePacketDecoder (0/1/2/3 fingers, 2-fragment L2CAP reassembly, scrubbed detection, battery 0x47, TX rejection)");
    }

    private static void VerifyWireRoundTrip()
    {
        var orig = new TouchFrame
        {
            TimestampMs = 123456,
            Clicks = 1,
            Dx = -15,
            Dy = 24,
            BatteryPercent = 79,
            IsConnected = true,
            Fingers = [new(3, -320, 640, 42, 30, 18, 0x40), new(4, 180, 610, 40, 28, 17, 0x40)]
        };
        string wire = TouchWireProtocol.SerializeFrame(orig);
        Assert(TouchWireProtocol.TryParseFrame(wire, out var parsed) && parsed != null, "Wire frame parse failed");
        Assert(parsed!.TimestampMs == orig.TimestampMs && parsed.BatteryPercent == 79 && parsed.Fingers.Length == 2, "Wire scalar mismatch");
        Assert(parsed.Fingers[0] == orig.Fingers[0] && parsed.Fingers[1] == orig.Fingers[1], "Wire finger mismatch");

        string hello = TouchWireProtocol.SerializeHello(79, true, 42, 0, 120, 48);
        Assert(TouchWireProtocol.TryParseHello(hello, out int hb, out bool hc, out long df, out long sp, out long te, out int mb) &&
               hb == 79 && hc && df == 42 && sp == 0 && te == 120 && mb == 48,
               "Wire HELLO telemetry mismatch");
        Console.WriteLine("[PASS] TouchWireProtocol Named Pipe serialization round-trip");
    }

    private static void VerifyGestureEngineAndPalmRejection()
    {
        var events = new List<(GestureAction Action, string Label)>();
        var cfg = new GestureConfig();
        var engine = new MultiTouchGestureEngine(cfg, (a, l) => events.Add((a, l)));

        static TouchFrame Mk(long ts, byte clicks, params TouchFinger[] fingers) => new()
        {
            TimestampMs = ts,
            Clicks = clicks,
            IsConnected = true,
            Fingers = fingers
        };

        // 1. Palm / Edge-Grip rejection: stationary thumb at X=1020 + normal 1-finger drag should NOT count as 2-finger swipe
        engine.ProcessFrame(Mk(1000, 0, new(1, 1020, -400, 30, 20, 15, 0x30), new(2, 0, 500, 30, 20, 15, 0x30)));
        engine.ProcessFrame(Mk(1030, 0, new(1, 1025, -405, 30, 20, 15, 0x40), new(2, 0, 100, 30, 20, 15, 0x40)));
        engine.ProcessFrame(Mk(1060, 0));
        Assert(events.Count == 0, $"Edge grip caused false gesture: {string.Join(',', events)}");

        // 2. 2-finger swipe Left with Natural Swipe ON -> VirtualDesktopRight
        engine.ProcessFrame(Mk(2000, 0, new(1, 300, 600, 30, 20, 18, 0x30), new(2, 550, 580, 30, 20, 18, 0x30)));
        engine.ProcessFrame(Mk(2080, 0, new(1, -120, 595, 30, 20, 18, 0x40), new(2, 130, 575, 30, 20, 18, 0x40)));
        engine.ProcessFrame(Mk(2120, 0));
        Assert(events.Count == 1 && events[0].Action == GestureAction.VirtualDesktopRight, "Expected VirtualDesktopRight");

        // 3. 2-finger Double-Tap -> TaskView (Mission Control)
        events.Clear();
        engine.ProcessFrame(Mk(3000, 0, new(1, -150, 500, 30, 20, 16, 0x30), new(2, 150, 500, 30, 20, 16, 0x30)));
        engine.ProcessFrame(Mk(3070, 0));
        engine.ProcessFrame(Mk(3180, 0, new(3, -145, 510, 30, 20, 16, 0x30), new(4, 155, 505, 30, 20, 16, 0x30)));
        engine.ProcessFrame(Mk(3250, 0));
        Assert(events.Count == 1 && events[0].Action == GestureAction.TaskView, "Expected 2-finger double-tap TaskView");

        // 4. 3-finger Tap -> MiddleClick
        events.Clear();
        engine.ProcessFrame(Mk(4000, 0, new(1, -250, 500, 30, 20, 15, 0x30), new(2, 0, 560, 30, 20, 15, 0x30), new(3, 250, 500, 30, 20, 15, 0x30)));
        engine.ProcessFrame(Mk(4090, 0));
        Assert(events.Count == 1 && events[0].Action == GestureAction.MiddleClick, "Expected 3-finger MiddleClick");

        // 5. 1-finger fast flick Right -> BrowserBack (Natural Swipe ON)
        events.Clear();
        engine.ProcessFrame(Mk(5000, 0, new TouchFinger(1, -350, 500, 30, 20, 15, 0x30)));
        engine.ProcessFrame(Mk(5090, 0, new TouchFinger(1, 320, 510, 30, 20, 15, 0x40)));
        engine.ProcessFrame(Mk(5120, 0));
        Assert(events.Count == 1 && events[0].Action == GestureAction.BrowserBack, "Expected 1-finger flick BrowserBack");

        Console.WriteLine("[PASS] MultiTouchGestureEngine (Palm rejection, 2F Swipe, 2F DblTap, 3F MiddleClick, 1F Back/Forward)");
    }

    private static void VerifyMacScrollPhysicsEngine()
    {
        var scrollEvents = new List<(int Dy, int Dx)>();
        var cfg = new GestureConfig();
        using var scrollEngine = new MacScrollPhysicsEngine(cfg, (dy, dx) =>
        {
            lock (scrollEvents) scrollEvents.Add((dy, dx));
        });

        static TouchFrame Mk(long ts, byte clicks, params TouchFinger[] fingers) => new()
        {
            TimestampMs = ts,
            Clicks = clicks,
            IsConnected = true,
            Fingers = fingers
        };

        // 1. 1-finger vertical slide UP (+Y) with NaturalScroll ON -> emits negative sub-detent deltas
        for (int i = 0; i <= 6; i++)
        {
            scrollEngine.ProcessFrame(Mk(1000 + i * 8, 0, new TouchFinger(1, 0, i * 65, 35, 25, 18, i == 0 ? (byte)0x30 : (byte)0x40)));
        }

        int activeCount;
        lock (scrollEvents)
        {
            activeCount = scrollEvents.Count;
            Assert(activeCount > 0, "1-finger smooth scroll produced 0 frames");
            foreach (var ev in scrollEvents)
            {
                Assert(ev.Dy < 0, $"Expected negative NaturalScroll delta for finger-up slide, got {ev.Dy}");
                Assert(ev.Dy % 120 != 0 && (ev.Dx == 0 || ev.Dx % 120 != 0), $"Emitted exact 120x WHEEL_DELTA ({ev.Dy}, {ev.Dx}) which disables Chromium precise delta flag");
            }
        }

        // 2. Lift finger -> 125Hz inertial coasting launches & emits decaying frames
        scrollEngine.ProcessFrame(Mk(1056, 0));
        Assert(scrollEngine.IsCoasting, "Expected inertial momentum coasting to start after fast 1-finger flick lift");
        Thread.Sleep(90);

        int postCoastCount;
        lock (scrollEvents)
        {
            postCoastCount = scrollEvents.Count;
            Assert(postCoastCount > activeCount, $"Expected coasting frames > activeCount ({postCoastCount} <= {activeCount})");
            for (int i = activeCount; i < postCoastCount; i++)
            {
                Assert(scrollEvents[i].Dy < 0 && scrollEvents[i].Dy % 120 != 0, $"Coast frame {i} invalid delta {scrollEvents[i].Dy}");
            }
        }

        // 3. Touch finger back down -> instantaneous brake
        scrollEngine.ProcessFrame(Mk(1200, 0, new TouchFinger(2, 0, 100, 35, 25, 18, 0x30)));
        Assert(!scrollEngine.IsCoasting, "Touching finger down must stop coasting immediately");
        scrollEngine.ProcessFrame(Mk(1210, 0));

        // 4. 2-finger gesture must suppress 1-finger scrolling completely
        lock (scrollEvents) scrollEvents.Clear();
        scrollEngine.ProcessFrame(Mk(2000, 0, new(1, 300, 400, 30, 20, 18, 0x30), new(2, 550, 400, 30, 20, 18, 0x30)));
        scrollEngine.ProcessFrame(Mk(2024, 0, new(1, -100, 520, 30, 20, 18, 0x40), new(2, 150, 520, 30, 20, 18, 0x40)));
        scrollEngine.ProcessFrame(Mk(2040, 0));
        lock (scrollEvents)
        {
            Assert(scrollEvents.Count == 0, $"2-finger gesture leaked {scrollEvents.Count} scroll frames");
        }

        // 5. Force-proportional dynamic velocity scaling & fingertip peel-off immunity
        long MeasureSequence(long tStart, int stepDy, int stepCount, int extraPeelOffFrames)
        {
            lock (scrollEvents) scrollEvents.Clear();
            long t = tStart;
            for (int i = 0; i < stepCount; i++)
            {
                byte st = i == 0 ? (byte)0x30 : (byte)0x40;
                scrollEngine.ProcessFrame(Mk(t, 0, new TouchFinger(10 + (int)(tStart % 100), 0, i * stepDy, 35, 25, 18, st)));
                t += 11;
            }

            int lastY = (stepCount - 1) * stepDy;
            for (int p = 1; p <= extraPeelOffFrames; p++)
            {
                lastY += 2;
                scrollEngine.ProcessFrame(Mk(t, 0, new TouchFinger(10 + (int)(tStart % 100), 0, lastY, 18, 12, 8, 0x40)));
                t += 11;
            }

            scrollEngine.ProcessFrame(Mk(t, 0));
            return t;
        }

        // a) Gentle micro-roll (+8 units / 11ms = 0.73 units/ms) -> clean stop, no coasting
        MeasureSequence(3000, 8, 6, 0);
        Assert(!scrollEngine.IsCoasting, "Gentle micro-roll should stop cleanly without coasting");
        Thread.Sleep(40);
        long totalSlow;
        lock (scrollEvents) totalSlow = scrollEvents.Sum(e => Math.Abs((long)e.Dy));
        Assert(totalSlow > 0, "Gentle micro-roll must still produce fine scroll movement");

        // b) Light flick (+38 units / 11ms = 3.45 units/ms)
        MeasureSequence(4000, 38, 6, 0);
        Assert(scrollEngine.IsCoasting, "Light flick must launch inertial coasting");
        Thread.Sleep(180);
        long totalLight;
        lock (scrollEvents) totalLight = scrollEvents.Sum(e => Math.Abs((long)e.Dy));
        scrollEngine.ProcessFrame(Mk(4500, 0, new TouchFinger(2, 0, 0, 35, 25, 18, 0x30)));
        scrollEngine.ProcessFrame(Mk(4510, 0));

        // c) Strong power flick (+165 units / 11ms = 15.0 units/ms) WITH 2 shrinking liftoff peel-off frames
        MeasureSequence(5000, 165, 6, 2);
        Assert(scrollEngine.IsCoasting, "Strong flick must launch high-speed inertial coasting even with liftoff peel-off frames");
        Thread.Sleep(180);
        long totalStrong;
        lock (scrollEvents) totalStrong = scrollEvents.Sum(e => Math.Abs((long)e.Dy));
        scrollEngine.ProcessFrame(Mk(5500, 0, new TouchFinger(3, 0, 0, 35, 25, 18, 0x30)));
        scrollEngine.ProcessFrame(Mk(5510, 0));

        Assert(totalLight > totalSlow * 3.5, $"Expected light flick ({totalLight}) > 3.5x slow micro-roll ({totalSlow})");
        Assert(totalStrong > totalLight * 4.5, $"Expected strong flick ({totalStrong}) > 4.5x light flick ({totalLight})");

        Console.WriteLine($"[PASS] MacScrollPhysicsEngine (125Hz sub-120 deltas, force-proportional inertia: slow={totalSlow}, light={totalLight}, strong={totalStrong}, instant brake, 2F guard)");
    }

    private static void VerifyLivePipeHandshake()
    {
        using var pipe = new NamedPipeClientStream(".", PipeTouchServer.PipeName, PipeDirection.In);
        pipe.Connect(3000);
        using var reader = new StreamReader(pipe, Encoding.UTF8);
        string? line = reader.ReadLine();
        int batt = -1;
        bool conn = false;
        long decodedFrames = 0;
        long scrubbedPackets = 0;
        long totalEvent402 = 0;
        int maxEvent402Bytes = 0;
        bool ok = !string.IsNullOrEmpty(line) &&
                  TouchWireProtocol.TryParseHello(line, out batt, out conn, out decodedFrames, out scrubbedPackets, out totalEvent402, out maxEvent402Bytes);
        Assert(ok, $"Invalid or empty HELLO line from live pipe: '{line}'");
        Assert(scrubbedPackets == 0, $"Live BTHPORT is still scrubbing Event 402 packets (scrubbedPackets={scrubbedPackets}, totalEvent402={totalEvent402}, maxBytes={maxEvent402Bytes}).");
        Console.WriteLine($"[PASS] Live Pipe Handshake: '{line}' (battery={batt}, connected={conn}, decoded={decodedFrames}, scrubbed={scrubbedPackets}, totalEvent402={totalEvent402}, maxBytes={maxEvent402Bytes})");
    }
}
