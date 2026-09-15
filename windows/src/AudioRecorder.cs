using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace WhisperWin
{
    /// Records microphone audio to a 16 kHz mono 16-bit WAV file (the format Whisper expects)
    /// using the winmm waveIn API. Exposes a live RMS level (0..1) for the waveform overlay.
    public class AudioRecorder : IDisposable
    {
        private const int SampleRate = 16000;
        private const int BufferMs = 100;
        private const int BufferBytes = SampleRate * 2 * BufferMs / 1000; // 3200
        private const int BufferCount = 8;
        private const uint WaveMapper = 0xFFFFFFFF;
        private const int WhdrDone = 0x00000001;

        private IntPtr _hWaveIn = IntPtr.Zero;
        private IntPtr[] _headers = new IntPtr[BufferCount];
        private IntPtr[] _buffers = new IntPtr[BufferCount];
        private FileStream _wavStream;
        private string _wavPath;
        private long _pcmBytes;
        private readonly byte[] _chunk = new byte[BufferBytes];
        private Thread _pollThread;
        private volatile bool _running;
        private int _headerSize;
        private int _flagsOffset;
        private int _bytesOffset;
        private readonly object _gate = new object();

        public bool IsRecording { get; private set; }
        public float Level { get; private set; }   // 0..1, updated while recording

        /// Starts capture. Returns false if no microphone / device error.
        public bool Start()
        {
            lock (_gate)
            {
                if (IsRecording) return true;

                var fmt = new WAVEFORMATEX
                {
                    wFormatTag = 1, // PCM
                    nChannels = 1,
                    nSamplesPerSec = SampleRate,
                    wBitsPerSample = 16,
                    nBlockAlign = 2,
                    nAvgBytesPerSec = SampleRate * 2,
                    cbSize = 0,
                };

                int rc = waveInOpen(out _hWaveIn, WaveMapper, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
                if (rc != 0)
                {
                    Log.Error("waveInOpen failed rc=" + rc + " (no microphone?)");
                    _hWaveIn = IntPtr.Zero;
                    return false;
                }

                _headerSize = Marshal.SizeOf(typeof(WAVEHDR));
                _flagsOffset = (int)Marshal.OffsetOf(typeof(WAVEHDR), "dwFlags");
                _bytesOffset = (int)Marshal.OffsetOf(typeof(WAVEHDR), "dwBytesRecorded");
                _wavPath = Path.Combine(Path.GetTempPath(), "whisper_" + Guid.NewGuid().ToString("N") + ".wav");
                try
                {
                    // Stream PCM directly to disk. This keeps memory bounded even if the
                    // user dictates for a long time instead of retaining the whole recording.
                    _wavStream = new FileStream(_wavPath, FileMode.Create, FileAccess.ReadWrite,
                                                FileShare.Read, 32 * 1024, FileOptions.SequentialScan);
                    WriteWavHeader(_wavStream, 0);
                    _pcmBytes = 0;

                    for (int i = 0; i < BufferCount; i++)
                    {
                        _buffers[i] = Marshal.AllocHGlobal(BufferBytes);
                        _headers[i] = Marshal.AllocHGlobal(_headerSize);
                        PrepareAndAdd(i);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Audio buffer setup: " + ex.Message);
                    CleanupNative();
                    CloseRecordingFile(false);
                    return false;
                }

                rc = waveInStart(_hWaveIn);
                if (rc != 0)
                {
                    Log.Error("waveInStart failed rc=" + rc);
                    CleanupNative();
                    CloseRecordingFile(false);
                    return false;
                }

                IsRecording = true;
                _running = true;
                _pollThread = new Thread(PollLoop) { IsBackground = true, Name = "waveIn-poll" };
                _pollThread.Start();
                Log.Info("Recording started");
                return true;
            }
        }

        /// Stops capture and writes the WAV file. Returns null if nothing was recorded.
        public string Stop()
        {
            Thread poll = null;
            lock (_gate)
            {
                if (!IsRecording) return null;
                IsRecording = false;
                _running = false;
                poll = _pollThread;
                _pollThread = null;
            }

            if (poll != null) poll.Join(500);

            string path = null;
            long recordedBytes = 0;
            lock (_gate)
            {
                if (_hWaveIn != IntPtr.Zero)
                {
                    waveInStop(_hWaveIn);
                    waveInReset(_hWaveIn); // marks all pending buffers done
                    DrainDoneBuffers();
                }

                if (_wavStream != null && _pcmBytes > 0)
                {
                    try
                    {
                        recordedBytes = _pcmBytes;
                        WriteWavHeader(_wavStream, _pcmBytes);
                        _wavStream.Flush();
                        path = _wavPath;
                    }
                    catch (Exception ex)
                    {
                        Log.Error("WAV finalize: " + ex.Message);
                    }
                }

                CloseRecordingFile(path != null);
                CleanupNative();
                Level = 0;
            }

            if (path == null) return null;
            Log.Info("Recording stopped → " + path + " (" + recordedBytes + " bytes, " +
                     (recordedBytes / (double)(SampleRate * 2)).ToString("0.00") + "s)");
            return path;
        }

        public double RecordedSeconds
        {
            get
            {
                lock (_gate)
                {
                    return _pcmBytes / (double)(SampleRate * 2);
                }
            }
        }

        /// Stops capture and releases all native buffers without keeping a recording file.
        public void Dispose()
        {
            if (IsRecording)
            {
                var path = Stop();
                if (path != null)
                {
                    try { File.Delete(path); } catch { }
                }
                return;
            }

            lock (_gate)
            {
                CloseRecordingFile(false);
                CleanupNative();
            }
        }

        // ---------- internals ----------

        private void PrepareAndAdd(int i)
        {
            var hdr = new WAVEHDR
            {
                lpData = _buffers[i],
                dwBufferLength = (uint)BufferBytes,
            };
            Marshal.StructureToPtr(hdr, _headers[i], false);
            waveInPrepareHeader(_hWaveIn, _headers[i], _headerSize);
            waveInAddBuffer(_hWaveIn, _headers[i], _headerSize);
        }

        private void PollLoop()
        {
            int next = 0;
            while (_running)
            {
                bool consumed = false;
                lock (_gate)
                {
                    if (!_running || _hWaveIn == IntPtr.Zero) break;
                    // buffers complete in the order they were added → check only the next expected one
                    int flags = Marshal.ReadInt32(_headers[next], _flagsOffset);
                    if ((flags & WhdrDone) != 0)
                    {
                        ConsumeBuffer(next);
                        waveInUnprepareHeader(_hWaveIn, _headers[next], _headerSize);
                        PrepareAndAdd(next);
                        next = (next + 1) % BufferCount;
                        consumed = true;
                    }
                }
                if (!consumed) Thread.Sleep(15);
            }
        }

        /// Copies recorded bytes from a done buffer into the PCM stream and updates Level.
        private void ConsumeBuffer(int i)
        {
            int bytes = Marshal.ReadInt32(_headers[i], _bytesOffset);
            bytes = Math.Max(0, Math.Min(BufferBytes, bytes));
            if (bytes <= 0) return;
            Marshal.Copy(_buffers[i], _chunk, 0, bytes);
            if (_wavStream != null)
            {
                _wavStream.Write(_chunk, 0, bytes);
                _pcmBytes += bytes;
            }

            // RMS level for waveform display — same ×8 visibility scale as the macOS version
            int n = bytes / 2;
            if (n > 0)
            {
                double sum = 0;
                for (int s = 0; s < n; s++)
                {
                    short v = (short)(_chunk[s * 2] | (_chunk[s * 2 + 1] << 8));
                    double f = v / 32768.0;
                    sum += f * f;
                }
                var rms = Math.Sqrt(sum / n);
                Level = (float)Math.Min(1.0, rms * 8);
            }
        }

        private void DrainDoneBuffers()
        {
            for (int i = 0; i < BufferCount; i++)
            {
                if (_headers[i] == IntPtr.Zero) continue;
                int flags = Marshal.ReadInt32(_headers[i], _flagsOffset);
                if ((flags & WhdrDone) != 0)
                {
                    ConsumeBuffer(i);
                    waveInUnprepareHeader(_hWaveIn, _headers[i], _headerSize);
                }
            }
        }

        private void CleanupNative()
        {
            if (_hWaveIn != IntPtr.Zero)
            {
                waveInClose(_hWaveIn);
                _hWaveIn = IntPtr.Zero;
            }
            for (int i = 0; i < BufferCount; i++)
            {
                if (_headers[i] != IntPtr.Zero) { Marshal.FreeHGlobal(_headers[i]); _headers[i] = IntPtr.Zero; }
                if (_buffers[i] != IntPtr.Zero) { Marshal.FreeHGlobal(_buffers[i]); _buffers[i] = IntPtr.Zero; }
            }
        }

        private void CloseRecordingFile(bool keep)
        {
            var path = _wavPath;
            try
            {
                if (_wavStream != null) _wavStream.Dispose();
            }
            catch (Exception ex) { Log.Error("WAV close: " + ex.Message); }
            _wavStream = null;
            _wavPath = null;
            _pcmBytes = 0;

            if (!keep && path != null)
            {
                try { File.Delete(path); } catch { }
            }
        }

        private static void WriteWavHeader(Stream stream, long pcmBytes)
        {
            var header = new byte[44];
            Buffer.BlockCopy(System.Text.Encoding.ASCII.GetBytes("RIFF"), 0, header, 0, 4);
            Buffer.BlockCopy(System.Text.Encoding.ASCII.GetBytes("WAVE"), 0, header, 8, 4);
            Buffer.BlockCopy(System.Text.Encoding.ASCII.GetBytes("fmt "), 0, header, 12, 4);
            Buffer.BlockCopy(System.Text.Encoding.ASCII.GetBytes("data"), 0, header, 36, 4);

            int dataLength = pcmBytes > int.MaxValue ? int.MaxValue : (int)Math.Max(0, pcmBytes);
            int riffLength = dataLength > int.MaxValue - 36 ? int.MaxValue : dataLength + 36;
            WriteInt32(header, 4, riffLength);
            WriteInt32(header, 16, 16);
            WriteInt16(header, 20, 1); // PCM
            WriteInt16(header, 22, 1); // mono
            WriteInt32(header, 24, SampleRate);
            WriteInt32(header, 28, SampleRate * 2);
            WriteInt16(header, 32, 2);
            WriteInt16(header, 34, 16);
            WriteInt32(header, 40, dataLength);

            stream.Position = 0;
            stream.Write(header, 0, header.Length);
            stream.Position = stream.Length;
        }

        private static void WriteInt16(byte[] bytes, int offset, short value)
        {
            var raw = BitConverter.GetBytes(value);
            Buffer.BlockCopy(raw, 0, bytes, offset, raw.Length);
        }

        private static void WriteInt32(byte[] bytes, int offset, int value)
        {
            var raw = BitConverter.GetBytes(value);
            Buffer.BlockCopy(raw, 0, bytes, offset, raw.Length);
        }

        // ---------- P/Invoke ----------

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public int nSamplesPerSec;
            public int nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
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
        private static extern int waveInOpen(out IntPtr hWaveIn, uint deviceId, ref WAVEFORMATEX fmt,
                                             IntPtr callback, IntPtr instance, int flags);
        [DllImport("winmm.dll")]
        private static extern int waveInPrepareHeader(IntPtr hWaveIn, IntPtr header, int size);
        [DllImport("winmm.dll")]
        private static extern int waveInUnprepareHeader(IntPtr hWaveIn, IntPtr header, int size);
        [DllImport("winmm.dll")]
        private static extern int waveInAddBuffer(IntPtr hWaveIn, IntPtr header, int size);
        [DllImport("winmm.dll")]
        private static extern int waveInStart(IntPtr hWaveIn);
        [DllImport("winmm.dll")]
        private static extern int waveInStop(IntPtr hWaveIn);
        [DllImport("winmm.dll")]
        private static extern int waveInReset(IntPtr hWaveIn);
        [DllImport("winmm.dll")]
        private static extern int waveInClose(IntPtr hWaveIn);
    }
}
