using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using LightCodec;

namespace QuickLook.Plugin.PbpViewer {
    public sealed class At3Player : IDisposable {
        const int BitsPerSample = 16;
        const int BufferMs = 200;

        byte[] _at3;
        Thread _thread;
        volatile bool _stop;
        volatile bool _playing;
        IntPtr _waveOut = IntPtr.Zero;

        public bool IsPlaying => _playing;
        public bool HasData => _at3 != null && _at3.Length > 0;

        public void SetData(byte[] snd0At3) {
            Stop();
            _at3 = snd0At3;
        }

        public void Toggle() {
            if (_playing) Stop();
            else Play();
        }

        public void Play() {
            if (_at3 == null || _at3.Length < 16) return;
            if (_playing) return;

            _stop = false;
            _playing = true;
            _thread = new Thread(PlaybackLoop) {
                IsBackground = true,
                Name = "PbpSnd0"
            };
            _thread.Start();
        }

        public void Stop() {
            _stop = true;
            if (_waveOut != IntPtr.Zero) {
                try { waveOutReset(_waveOut); } catch { }
            }
            if (_thread != null) {
                if (!_thread.Join(3000)) {
                    try { _thread.Abort(); } catch { }
                }
                _thread = null;
            }
            CloseWaveOut();
            _playing = false;
        }

        public void Dispose() {
            Stop();
            _at3 = null;
        }

        void PlaybackLoop() {
            GCHandle pinA = default(GCHandle);
            GCHandle pinB = default(GCHandle);
            WaveHdr hdrA = new WaveHdr();
            WaveHdr hdrB = new WaveHdr();
            bool preparedA = false;
            bool preparedB = false;

            try {
                int rate, channels;
                byte[] pcmAll = DecodeAll(_at3, out rate, out channels);


                if (pcmAll == null || pcmAll.Length < 4)
                    return;

                if (!OpenWaveOut(rate, channels))
                    return;

                int frameAlign = channels * (BitsPerSample / 8);
                if (frameAlign < 2) frameAlign = 4;

                int bytesPerSec = rate * frameAlign;
                int chunkBytes = bytesPerSec * BufferMs / 1000;
                chunkBytes = (chunkBytes / frameAlign) * frameAlign;
                if (chunkBytes < 4096) chunkBytes = 4096;

                byte[] bufA = new byte[chunkBytes];
                byte[] bufB = new byte[chunkBytes];
                int offset = 0;

                int n = CopyLoop(pcmAll, ref offset, bufA, chunkBytes);
                pinA = GCHandle.Alloc(bufA, GCHandleType.Pinned);
                hdrA = MakeHdr(pinA, n);
                if (waveOutPrepareHeader(_waveOut, ref hdrA, Marshal.SizeOf(typeof(WaveHdr))) != 0)
                    return;
                preparedA = true;
                if (waveOutWrite(_waveOut, ref hdrA, Marshal.SizeOf(typeof(WaveHdr))) != 0)
                    return;

                n = CopyLoop(pcmAll, ref offset, bufB, chunkBytes);
                pinB = GCHandle.Alloc(bufB, GCHandleType.Pinned);
                hdrB = MakeHdr(pinB, n);
                if (waveOutPrepareHeader(_waveOut, ref hdrB, Marshal.SizeOf(typeof(WaveHdr))) != 0)
                    return;
                preparedB = true;
                if (waveOutWrite(_waveOut, ref hdrB, Marshal.SizeOf(typeof(WaveHdr))) != 0)
                    return;

                bool nextIsA = true;
                while (!_stop) {
                    if (nextIsA) {
                        if (!WaitDone(ref hdrA)) break;
                        waveOutUnprepareHeader(_waveOut, ref hdrA, Marshal.SizeOf(typeof(WaveHdr)));
                        preparedA = false;

                        n = CopyLoop(pcmAll, ref offset, bufA, chunkBytes);
                        hdrA = MakeHdr(pinA, n);
                        waveOutPrepareHeader(_waveOut, ref hdrA, Marshal.SizeOf(typeof(WaveHdr)));
                        preparedA = true;
                        waveOutWrite(_waveOut, ref hdrA, Marshal.SizeOf(typeof(WaveHdr)));
                    }
                    else {
                        if (!WaitDone(ref hdrB)) break;
                        waveOutUnprepareHeader(_waveOut, ref hdrB, Marshal.SizeOf(typeof(WaveHdr)));
                        preparedB = false;

                        n = CopyLoop(pcmAll, ref offset, bufB, chunkBytes);
                        hdrB = MakeHdr(pinB, n);
                        waveOutPrepareHeader(_waveOut, ref hdrB, Marshal.SizeOf(typeof(WaveHdr)));
                        preparedB = true;
                        waveOutWrite(_waveOut, ref hdrB, Marshal.SizeOf(typeof(WaveHdr)));
                    }
                    nextIsA = !nextIsA;
                }

                try { waveOutReset(_waveOut); } catch { }
                if (preparedA) {
                    try { waveOutUnprepareHeader(_waveOut, ref hdrA, Marshal.SizeOf(typeof(WaveHdr))); } catch { }
                }
                if (preparedB) {
                    try { waveOutUnprepareHeader(_waveOut, ref hdrB, Marshal.SizeOf(typeof(WaveHdr))); } catch { }
                }
            }
            catch {
            }
            finally {
                if (pinA.IsAllocated) pinA.Free();
                if (pinB.IsAllocated) pinB.Free();
                CloseWaveOut();
                _playing = false;
            }
        }

        bool WaitDone(ref WaveHdr hdr) {
            int spins = 0;
            while (!_stop && (hdr.dwFlags & 0x00000001) == 0 && spins < 20000) {
                Thread.Sleep(1);
                spins++;
            }
            return !_stop;
        }

        static int CopyLoop(byte[] pcm, ref int offset, byte[] dst, int length) {
            int written = 0;
            while (written < length) {
                if (offset >= pcm.Length)
                    offset = 0;
                int left = pcm.Length - offset;
                int n = Math.Min(left, length - written);
                Buffer.BlockCopy(pcm, offset, dst, written, n);
                written += n;
                offset += n;
            }
            return written;
        }

        static WaveHdr MakeHdr(GCHandle pin, int len) {
            return new WaveHdr {
                lpData = pin.AddrOfPinnedObject(),
                dwBufferLength = (uint) len,
                dwBytesRecorded = 0,
                dwUser = IntPtr.Zero,
                dwFlags = 0,
                dwLoops = 0,
                lpNext = IntPtr.Zero,
                reserved = IntPtr.Zero
            };
        }

        unsafe byte[] DecodeAll(byte[] at3, out int sampleRate, out int channels) {
            sampleRate = 44100;
            channels = 2;

            if (!TryParseRiff(at3, out FmtInfo fmt, out int dataOffset, out int dataLength))
                return null;

            sampleRate = fmt.SampleRate > 0 ? (int) fmt.SampleRate : 44100;
            channels = fmt.Channels > 0 ? fmt.Channels : 2;

            byte[] result = TryDecode(at3, fmt, dataOffset, dataLength, channels, 0);
            if (result == null || result.Length < 4)
                result = TryDecode(at3, fmt, dataOffset, dataLength, channels, 1);

            return result;
        }
        unsafe byte[] TryDecode(
    byte[] at3,
    FmtInfo fmt,
    int dataOffset,
    int dataLength,
    int channels,
    int codingMode) {
            int blockSize;
            ILightCodec codec;

            if (fmt.FormatTag == 0x0270) {
                codec = CodecFactory.Get(AudioCodec.AT3);
                blockSize = fmt.BlockAlign > 0 ? fmt.BlockAlign : 192;
            }
            else if (fmt.FormatTag == 0xFFFE) {
                codec = CodecFactory.Get(AudioCodec.AT3plus);
                blockSize = fmt.BlockAlign;
                if (blockSize <= 0) return null;
            }
            else return null;

            if (codec.init(blockSize, channels, channels, codingMode) < 0)
                return null;

            using (var ms = new MemoryStream()) {
                byte[] frame = new byte[blockSize];
                int maxSamples = Math.Max(codec.NumberOfSamples, 2048) * channels + 256;
                if (maxSamples < 8192) maxSamples = 8192;
                short[] pcm = new short[maxSamples];

                // Буфер последнего хорошего кадра для concealment
                byte[] lastGood = null;
                int lastGoodLen = 0;

                int pos = dataOffset;
                int end = dataOffset + dataLength;
                int framesOk = 0;
                int framesErr = 0;

                while (pos + blockSize <= end) {
                    Buffer.BlockCopy(at3, pos, frame, 0, blockSize);
                    pos += blockSize;

                    int outLen = 0;
                    int rs;

                    fixed (byte* inPtr = frame)
                    fixed (short* outPtr = pcm) {
                        rs = codec.decode(inPtr, blockSize, outPtr, out outLen);
                    }

                    if (rs < 0) {
                        framesErr++;

                        // === КЛЮЧЕВОЕ ===
                        // Состояние декодера испорчено → полностью сбрасываем
                        codec.init(blockSize, channels, channels, codingMode);

                        // Concealment: повторяем последний хороший кадр
                        if (lastGood != null && lastGoodLen > 0) {
                            ms.Write(lastGood, 0, lastGoodLen);
                        }
                        else {
                            // Если ещё не было хороших — тишина
                            int silenceBytes = codec.NumberOfSamples * channels * 2;
                            byte[] silence = new byte[silenceBytes];
                            ms.Write(silence, 0, silenceBytes);
                        }
                        continue;
                    }

                    if (outLen <= 0) continue;

                    // AT3plus: outLen всегда в байтах
                    int byteLen = outLen;
                    if (byteLen > pcm.Length * 2) byteLen = pcm.Length * 2;
                    byteLen -= byteLen % 2;
                    if (byteLen <= 0) continue;

                    byte[] chunk = new byte[byteLen];
                    Buffer.BlockCopy(pcm, 0, chunk, 0, byteLen);
                    ms.Write(chunk, 0, byteLen);

                    // Запоминаем как последний хороший
                    lastGood = chunk;
                    lastGoodLen = byteLen;
                    framesOk++;
                }

                if (framesOk == 0) return null;

                byte[] raw = ms.ToArray();
                int align = channels * 2;
                int len = raw.Length - (raw.Length % align);
                if (len < align) return null;

                if (len == raw.Length) return raw;

                byte[] trimmed = new byte[len];
                Buffer.BlockCopy(raw, 0, trimmed, 0, len);
                return trimmed;
            }
        }
        

        #region RIFF

        struct FmtInfo {
            public ushort FormatTag;
            public ushort Channels;
            public uint SampleRate;
            public uint AvgBytesPerSec;
            public ushort BlockAlign;
            public ushort BitsPerSample;
            public byte[] Extra;
        }

        static bool TryParseRiff(byte[] data, out FmtInfo fmt, out int dataOffset, out int dataLength) {
            fmt = default(FmtInfo);
            dataOffset = 0;
            dataLength = 0;

            if (data == null || data.Length < 12)
                return false;
            if (data[0] != (byte) 'R' || data[1] != (byte) 'I' ||
                data[2] != (byte) 'F' || data[3] != (byte) 'F')
                return false;
            if (data[8] != (byte) 'W' || data[9] != (byte) 'A' ||
                data[10] != (byte) 'V' || data[11] != (byte) 'E')
                return false;

            int pos = 12;
            bool haveFmt = false;
            bool haveData = false;

            while (pos + 8 <= data.Length) {
                string id = System.Text.Encoding.ASCII.GetString(data, pos, 4);
                int size = BitConverter.ToInt32(data, pos + 4);
                if (size < 0)
                    break;

                int chunkData = pos + 8;
                int next = chunkData + size;
                if ((size & 1) != 0)
                    next++;

                if (id == "fmt " && size >= 14 && chunkData + size <= data.Length) {
                    fmt.FormatTag = BitConverter.ToUInt16(data, chunkData + 0);
                    fmt.Channels = BitConverter.ToUInt16(data, chunkData + 2);
                    fmt.SampleRate = BitConverter.ToUInt32(data, chunkData + 4);
                    fmt.AvgBytesPerSec = BitConverter.ToUInt32(data, chunkData + 8);
                    fmt.BlockAlign = BitConverter.ToUInt16(data, chunkData + 12);
                    if (size >= 16)
                        fmt.BitsPerSample = BitConverter.ToUInt16(data, chunkData + 14);
                    if (size >= 18) {
                        ushort cb = BitConverter.ToUInt16(data, chunkData + 16);
                        int extraLen = Math.Min((int) cb, size - 18);
                        if (extraLen > 0 && chunkData + 18 + extraLen <= data.Length) {
                            fmt.Extra = new byte[extraLen];
                            Buffer.BlockCopy(data, chunkData + 18, fmt.Extra, 0, extraLen);
                        }
                    }
                    haveFmt = true;
                }
                else if (id == "data") {
                    dataOffset = chunkData;
                    dataLength = Math.Min(size, data.Length - chunkData);
                    haveData = true;
                }

                if (next <= pos)
                    break;
                pos = next;
            }

            return haveFmt && haveData && dataLength > 0;
        }

        #endregion

        #region waveOut

        [StructLayout(LayoutKind.Sequential)]
        struct WaveFormat {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WaveHdr {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [DllImport("winmm.dll")]
        static extern int waveOutOpen(out IntPtr hWaveOut, int uDeviceID,
            ref WaveFormat lpFormat, IntPtr dwCallback, IntPtr dwInstance, int dwFlags);

        [DllImport("winmm.dll")]
        static extern int waveOutPrepareHeader(IntPtr hWaveOut, ref WaveHdr lpWaveOutHdr, int uSize);

        [DllImport("winmm.dll")]
        static extern int waveOutWrite(IntPtr hWaveOut, ref WaveHdr lpWaveOutHdr, int uSize);

        [DllImport("winmm.dll")]
        static extern int waveOutUnprepareHeader(IntPtr hWaveOut, ref WaveHdr lpWaveOutHdr, int uSize);

        [DllImport("winmm.dll")]
        static extern int waveOutClose(IntPtr hWaveOut);

        [DllImport("winmm.dll")]
        static extern int waveOutReset(IntPtr hWaveOut);

        bool OpenWaveOut(int rate, int channels) {
            CloseWaveOut();
            var wf = new WaveFormat {
                wFormatTag = 1,
                nChannels = (ushort) channels,
                nSamplesPerSec = (uint) rate,
                wBitsPerSample = BitsPerSample,
                nBlockAlign = (ushort) (channels * BitsPerSample / 8),
                nAvgBytesPerSec = (uint) (rate * channels * BitsPerSample / 8),
                cbSize = 0
            };
            int r = waveOutOpen(out _waveOut, -1, ref wf, IntPtr.Zero, IntPtr.Zero, 0);
            return r == 0 && _waveOut != IntPtr.Zero;
        }

        void CloseWaveOut() {
            if (_waveOut != IntPtr.Zero) {
                try { waveOutReset(_waveOut); } catch { }
                try { waveOutClose(_waveOut); } catch { }
                _waveOut = IntPtr.Zero;
            }
        }

        #endregion
    }
}