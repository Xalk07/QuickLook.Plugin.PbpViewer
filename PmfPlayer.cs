using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LightCodec;
using LightCodec.av;
using LightCodec.h264;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace QuickLook.Plugin.PbpViewer {
    public sealed class PmfPlayer : IDisposable {
        private byte[] _pmfData;
        private Thread _thread;
        private volatile bool _stop;

        private BitmapSource _bitmap;
        private readonly object _lock = new object();

        private Dispatcher _dispatcher;

        // Реальный размер видео из PSMF header.
        private int _pmfWidth;
        private int _pmfHeight;

        public BitmapSource CurrentFrame {
            get {
                lock (_lock)
                    return _bitmap;
            }

            private set {
                lock (_lock)
                    _bitmap = value;
            }
        }

        public event Action FrameUpdated;

        public void SetData(byte[] pmfData) {
            Stop();

            _pmfData = pmfData;

            _pmfWidth = 0;
            _pmfHeight = 0;

            lock (_lock) {
                _bitmap = null;
            }
        }

        public void Start(Dispatcher dispatcher) {
            Stop();

            if (_pmfData == null || _pmfData.Length < 2048)
                return;

            _dispatcher = dispatcher;

            _stop = false;

            _thread = new Thread(PlaybackLoop) {
                IsBackground = true,
                Name = "PbpIcon1Video"
            };

            _thread.Start();
        }

        public void Stop() {
            _stop = true;

            Thread thread = _thread;

            if (thread != null) {
                try {
                    if (!thread.Join(1500)) {
                        // Поток закончится сам при следующем выходе
                        // из decoder/demuxer.
                    }
                }
                catch {
                }

                _thread = null;
            }
        }

        public void Dispose() {
            Stop();

            _pmfData = null;
            _dispatcher = null;

            lock (_lock) {
                _bitmap = null;
            }
        }

        private void PlaybackLoop() {
            try {
                while (!_stop) {
                    PlayOnce();

                    if (!_stop)
                        Thread.Sleep(50);
                }
            }
            catch {
                // Не даём исключению убить QuickLook.
            }
        }

        private void PlayOnce() {
            byte[] data = _pmfData;

            if (data == null || data.Length < 2048)
                return;

            try {
                // =========================================================
                // PSMF HEADER
                // =========================================================

                if (data.Length < 0x90)
                    return;

                // Magic: PSMF
                if (data[0] != (byte) 'P' ||
                    data[1] != (byte) 'S' ||
                    data[2] != (byte) 'M' ||
                    data[3] != (byte) 'F') {
                    return;
                }

                // MPEG data offset.
                //
                // Для обычного PMF это 2048 (0x800),
                // но берём значение непосредственно из header.
                int mpegOffset = ReadBE32(data, 0x08);

                if (mpegOffset <= 0 ||
                    mpegOffset >= data.Length) {
                    mpegOffset = 2048;
                }

                // =========================================================
                // РЕАЛЬНЫЙ РАЗМЕР VIDEO из PSMF
                //
                // Jpcsp:
                //
                // frameWidth  = read8(..., 0x8E) << 4
                // frameHeight = read8(..., 0x8F) << 4
                //
                // =========================================================

                _pmfWidth = data[0x8E] << 4;
                _pmfHeight = data[0x8F] << 4;

                // Если header каким-то образом повреждён,
                // используем размеры decoder как fallback.
                if (_pmfWidth <= 0 || _pmfHeight <= 0) {
                    _pmfWidth = 0;
                    _pmfHeight = 0;
                }

                using (var pmfStream = new MemoryStream(data, false)) {
                    pmfStream.Position = mpegOffset;

                    // =====================================================
                    // MPEG-PS -> H264 elementary stream
                    // =====================================================

                    var videoStream = new MemoryStream();

                    var demuxer =
                        new SimpleMpegPsDemuxer(pmfStream);

                    var decoder =
                        new H264Decoder(videoStream);

                    decoder.init(null);

                    // В PMF видео обычно находится в 0xE0.
                    while (!_stop && demuxer.HasMorePackets) {
                        Packet packet = demuxer.ReadPacket();

                        if (packet == null)
                            break;

                        if (!packet.IsVideo)
                            continue;

                        packet.Payload.Position = 0;
                        packet.Payload.CopyTo(videoStream);
                    }

                    if (videoStream.Length == 0)
                        return;

                    // =====================================================
                    // H264 decoder
                    // =====================================================

                    videoStream.Position = 0;

                    int framesDecoded = 0;

                    while (!_stop) {
                        AVFrame frame;

                        try {
                            frame = decoder.DecodeFrame();
                        }
                        catch (EndOfStreamException) {
                            break;
                        }
                        catch {
                            break;
                        }

                        if (frame == null)
                            break;

                        framesDecoded++;

                        UpdateFrame(frame);

                        // PMF обычно около 29.97 FPS.
                        Thread.Sleep(33);
                    }

                    decoder = null;
                }
            }
            catch {
                // Любая ошибка конкретного PMF не должна закрывать QuickLook.
            }
        }

        // ================================================================
        // PSMF helpers
        // ================================================================

        private static int ReadBE32(byte[] data, int offset) {
            if (data == null ||
                offset < 0 ||
                offset + 4 > data.Length) {
                return 0;
            }

            return
                (data[offset] << 24) |
                (data[offset + 1] << 16) |
                (data[offset + 2] << 8) |
                data[offset + 3];
        }

        // ================================================================
        // YUV420P -> WPF Bitmap
        // ================================================================

        private void UpdateFrame(AVFrame frame) {
            if (frame == null)
                return;

            Dispatcher dispatcher = _dispatcher;

            if (dispatcher == null || _stop)
                return;

            try {
                int targetWidth = _pmfWidth > 0 ? _pmfWidth : 144;
                int targetHeight = _pmfHeight > 0 ? _pmfHeight : 80;

                using (Bitmap rawBitmap = FrameUtils.imageFromFrameWithoutEdges(frame, targetWidth, targetHeight)) {
                    if (rawBitmap == null)
                        return;

                    // Настройка индивидуальных отступов для каждой из сторон (в пикселях):
                    int marginLeft = 2;   // Слева
                    int marginTop = 2;    // Сверху
                    int marginRight = 3;  // Справа (на 1px больше)
                    int marginBottom = 3; // Снизу (на 1px больше)

                    using (Bitmap croppedBitmap = CropMargins(rawBitmap, marginLeft, marginTop, marginRight, marginBottom)) {
                        IntPtr hBitmap = croppedBitmap.GetHbitmap();

                        try {
                            BitmapSource source =
                                Imaging.CreateBitmapSourceFromHBitmap(
                                    hBitmap,
                                    IntPtr.Zero,
                                    Int32Rect.Empty,
                                    BitmapSizeOptions.FromEmptyOptions());

                            source.Freeze();

                            dispatcher.BeginInvoke(
                                DispatcherPriority.Render,
                                new Action(() => {
                                    if (_stop)
                                        return;

                                    CurrentFrame = source;

                                    FrameUpdated?.Invoke();
                                }));
                        }
                        finally {
                            DeleteObject(hBitmap);
                        }
                    }
                }
            }
            catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine("UpdateFrame ERROR: " + ex);
            }
        }

        /// <summary>
        /// Обрезает индивидуальное количество пикселей с каждой из 4 сторон.
        /// </summary>
        private Bitmap CropMargins(Bitmap src, int left, int top, int right, int bottom) {
            if (src == null) return null;

            int newWidth = src.Width - left - right;
            int newHeight = src.Height - top - bottom;

            // Проверка корректности итоговых размеров
            if (newWidth <= 0 || newHeight <= 0) {
                return new Bitmap(src);
            }

            Rectangle cropRect = new Rectangle(left, top, newWidth, newHeight);
            return src.Clone(cropRect, src.PixelFormat);
        }

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
        private static int Clamp(
            int value,
            int min,
            int max) {
            if (value < min)
                return min;

            if (value > max)
                return max;

            return value;
        }

        // ================================================================
        // MPEG-PS / PES DEMUXER
        // ================================================================

        internal sealed class SimpleMpegPsDemuxer {
            private readonly Stream _stream;

            public SimpleMpegPsDemuxer(Stream stream) {
                _stream = stream;
            }

            public bool HasMorePackets {
                get {
                    return _stream != null &&
                           _stream.Position <
                           _stream.Length - 3;
                }
            }

            public Packet ReadPacket() {
                while (HasMorePackets) {
                    uint startCode =
                        FindNextStartCode();

                    if (startCode == 0xFFFFFFFF)
                        return null;

                    byte streamId =
                        (byte) (startCode & 0xFF);

                    // ----------------------------------------------------
                    // Program End Code
                    // ----------------------------------------------------

                    if (startCode == 0x000001B9)
                        return null;

                    // ----------------------------------------------------
                    // PACK
                    // ----------------------------------------------------

                    if (startCode == 0x000001BA) {
                        byte[] pack =
                            ReadExact(
                                _stream,
                                10);

                        if (pack == null)
                            return null;

                        int stuffing =
                            pack[9] & 0x07;

                        if (!SkipExact(
                                _stream,
                                stuffing)) {
                            return null;
                        }

                        continue;
                    }

                    // ----------------------------------------------------
                    // SYSTEM HEADER
                    // ----------------------------------------------------

                    if (startCode == 0x000001BB) {
                        int length =
                            ReadBE16();

                        if (length < 0)
                            return null;

                        if (!SkipExact(
                                _stream,
                                length)) {
                            return null;
                        }

                        continue;
                    }

                    // ----------------------------------------------------
                    // PROGRAM STREAM MAP
                    // ----------------------------------------------------

                    if (startCode == 0x000001BC) {
                        int length =
                            ReadBE16();

                        if (length < 0)
                            return null;

                        if (!SkipExact(
                                _stream,
                                length)) {
                            return null;
                        }

                        continue;
                    }

                    // ----------------------------------------------------
                    // PADDING STREAM
                    // ----------------------------------------------------

                    if (startCode == 0x000001BE) {
                        int length =
                            ReadBE16();

                        if (length < 0)
                            return null;

                        if (!SkipExact(
                                _stream,
                                length)) {
                            return null;
                        }

                        continue;
                    }

                    // ----------------------------------------------------
                    // PRIVATE STREAM 2
                    // ----------------------------------------------------

                    if (startCode == 0x000001BF) {
                        int length =
                            ReadBE16();

                        if (length < 0)
                            return null;

                        if (!SkipExact(
                                _stream,
                                length)) {
                            return null;
                        }

                        continue;
                    }

                    // ----------------------------------------------------
                    // VIDEO
                    //
                    // PSP PMF video = E0.
                    // ----------------------------------------------------

                    if (streamId == 0xE0) {
                        Packet packet =
                            ReadPesPacket(true);

                        if (packet != null)
                            return packet;

                        continue;
                    }

                    // ----------------------------------------------------
                    // PRIVATE STREAM 1
                    //
                    // Usually ATRAC audio.
                    // ----------------------------------------------------

                    if (streamId == 0xBD) {
                        Packet packet =
                            ReadPesPacket(false);

                        if (packet != null)
                            return packet;

                        continue;
                    }

                    // ----------------------------------------------------
                    // Other PES
                    // ----------------------------------------------------

                    if (IsPesStreamId(streamId)) {
                        int length =
                            ReadBE16();

                        if (length < 0)
                            return null;

                        if (length == 0)
                            return null;

                        if (!SkipExact(
                                _stream,
                                length)) {
                            return null;
                        }

                        continue;
                    }

                    // Unknown start code.
                    // Continue searching.
                }

                return null;
            }

            private Packet ReadPesPacket(
                bool isVideo) {
                int packetLength =
                    ReadBE16();

                if (packetLength < 0)
                    return null;

                if (packetLength == 0)
                    return null;

                byte[] raw =
                    ReadExact(
                        _stream,
                        packetLength);

                if (raw == null ||
                    raw.Length == 0) {
                    return null;
                }

                int payloadOffset =
                    GetPesPayloadOffset(raw);

                if (payloadOffset < 0 ||
                    payloadOffset >= raw.Length) {
                    return null;
                }

                int payloadLength =
                    raw.Length -
                    payloadOffset;

                if (payloadLength <= 0)
                    return null;

                var payload =
                    new MemoryStream(
                        raw,
                        payloadOffset,
                        payloadLength,
                        false);

                return new Packet {
                    IsVideo = isVideo,
                    Payload = payload
                };
            }

            private static int GetPesPayloadOffset(
                byte[] raw) {
                if (raw == null ||
                    raw.Length == 0) {
                    return -1;
                }

                // ---------------------------------------------------------
                // MPEG-2 PES
                //
                // 10xxxxxx
                // flags
                // header_data_length
                // ---------------------------------------------------------

                if (raw.Length >= 3 &&
                    (raw[0] & 0xC0) == 0x80) {
                    int headerLength =
                        raw[2];

                    int offset =
                        3 + headerLength;

                    if (offset > raw.Length)
                        return -1;

                    return offset;
                }

                // ---------------------------------------------------------
                // MPEG-1 PES fallback
                // ---------------------------------------------------------

                int p = 0;

                // stuffing
                while (p < raw.Length &&
                       raw[p] == 0xFF) {
                    p++;
                }

                if (p >= raw.Length)
                    return -1;

                // STD buffer
                if (p + 1 < raw.Length &&
                    (raw[p] & 0xC0) == 0x40) {
                    p += 2;
                }

                if (p >= raw.Length)
                    return -1;

                // No timestamp
                if (raw[p] == 0x0F) {
                    return p + 1;
                }

                // PTS only
                if ((raw[p] & 0xF0) == 0x20) {
                    p += 5;

                    if (p > raw.Length)
                        return -1;

                    return p;
                }

                // PTS + DTS
                if ((raw[p] & 0xF0) == 0x30) {
                    p += 10;

                    if (p > raw.Length)
                        return -1;

                    return p;
                }

                return p;
            }

            private static bool IsPesStreamId(
                byte streamId) {
                // Video
                if (streamId >= 0xE0 &&
                    streamId <= 0xEF) {
                    return true;
                }

                // Audio
                if (streamId >= 0xC0 &&
                    streamId <= 0xDF) {
                    return true;
                }

                // Private stream 1
                if (streamId == 0xBD)
                    return true;

                return false;
            }

            private uint FindNextStartCode() {
                int state = 0;

                while (true) {
                    int b =
                        _stream.ReadByte();

                    if (b < 0)
                        return 0xFFFFFFFF;

                    if (state == 0) {
                        if (b == 0x00)
                            state = 1;
                    }
                    else if (state == 1) {
                        if (b == 0x00) {
                            state = 2;
                        }
                        else {
                            state = 0;
                        }
                    }
                    else {
                        if (b == 0x01) {
                            int id =
                                _stream.ReadByte();

                            if (id < 0)
                                return 0xFFFFFFFF;

                            return
                                0x00000100u |
                                (uint) id;
                        }

                        if (b == 0x00) {
                            state = 2;
                        }
                        else {
                            state = 0;
                        }
                    }
                }
            }

            private int ReadBE16() {
                int hi =
                    _stream.ReadByte();

                int lo =
                    _stream.ReadByte();

                if (hi < 0 || lo < 0)
                    return -1;

                return
                    (hi << 8) |
                    lo;
            }

            private static byte[] ReadExact(
                Stream stream,
                int count) {
                if (stream == null ||
                    count < 0) {
                    return null;
                }

                byte[] result =
                    new byte[count];

                int offset = 0;

                while (offset < count) {
                    int read =
                        stream.Read(
                            result,
                            offset,
                            count - offset);

                    if (read <= 0)
                        return null;

                    offset += read;
                }

                return result;
            }

            private static bool SkipExact(
                Stream stream,
                int count) {
                if (stream == null ||
                    count < 0) {
                    return false;
                }

                if (count == 0)
                    return true;

                byte[] buffer =
                    new byte[Math.Min(
                        4096,
                        count)];

                int remaining =
                    count;

                while (remaining > 0) {
                    int toRead =
                        Math.Min(
                            buffer.Length,
                            remaining);

                    int read =
                        stream.Read(
                            buffer,
                            0,
                            toRead);

                    if (read <= 0)
                        return false;

                    remaining -= read;
                }

                return true;
            }
        }

        internal sealed class Packet {
            public bool IsVideo;
            public MemoryStream Payload;
        }
    }
}