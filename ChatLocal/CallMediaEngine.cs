using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ChatLocal
{
    public class CallMediaEngine : IDisposable
    {
        private readonly Dispatcher _ui = Application.Current.Dispatcher;

        // WinMM Audio Structures & P/Invoke
        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
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

        private const int WAVE_MAPPER = -1;
        private const ushort WAVE_FORMAT_PCM = 1;
        private const int CALLBACK_FUNCTION = 0x00030000;
        private const int WIM_DATA = 0x3C0;
        private const int WOM_DONE = 0x3BD;

        private delegate void WaveCallback(IntPtr hWave, int uMsg, IntPtr dwUser, IntPtr dwParam1, IntPtr dwParam2);

        [DllImport("winmm.dll")]
        private static extern int waveInOpen(out IntPtr phwi, int uDeviceID, ref WAVEFORMATEX lpFormat, WaveCallback dwCallback, IntPtr dwInstance, int fdwOpen);

        [DllImport("winmm.dll")]
        private static extern int waveInPrepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveInAddBuffer(IntPtr hwi, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveInStart(IntPtr hwi);

        [DllImport("winmm.dll")]
        private static extern int waveInReset(IntPtr hwi);

        [DllImport("winmm.dll")]
        private static extern int waveInClose(IntPtr hwi);

        [DllImport("winmm.dll")]
        private static extern int waveOutOpen(out IntPtr phwo, int uDeviceID, ref WAVEFORMATEX lpFormat, WaveCallback dwCallback, IntPtr dwInstance, int fdwOpen);

        [DllImport("winmm.dll")]
        private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveOutReset(IntPtr hwo);

        [DllImport("winmm.dll")]
        private static extern int waveOutClose(IntPtr hwo);

        // Audio state
        private IntPtr _hWaveIn = IntPtr.Zero;
        private IntPtr _hWaveOut = IntPtr.Zero;
        private WaveCallback _inCallback;
        private WaveCallback _outCallback;
        private const int AudioBufferSize = 3200; // 100 ms at 16kHz 16-bit mono
        private const int NumAudioBuffers = 4;
        private IntPtr[] _inHdrs = new IntPtr[NumAudioBuffers];
        private IntPtr[] _inBuffers = new IntPtr[NumAudioBuffers];
        private readonly ConcurrentDictionary<IntPtr, byte> _queuedWaveOutBuffers = new ConcurrentDictionary<IntPtr, byte>();
        private readonly object _waveOutWriteLock = new object();
        private int _waveInCallbackCount;
        private int _waveOutCallbackCount;

        // Network state
        private TcpListener _mediaListener;
        private TcpClient _mediaClient;
        private NetworkStream _mediaStream;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private bool _isDisposed;

        // Video state
        private CameraCapture _camera;
        private SharedCameraRelay _cameraRelay;
        private DispatcherTimer _cameraWatchdog;
        private long _lastCameraFrameTicks;
        private int _cameraRecoveryRunning;
        private double _localAudioLevel;
        private double _remoteAudioLevel;
        private bool _isCameraOff;

        public int MediaPort { get; private set; }
        public bool IsMuted { get; set; }
        public bool IsCameraOff
        {
            get => _isCameraOff;
            set
            {
                if (_isCameraOff == value) return;
                _isCameraOff = value;
                try
                {
                    if (_camera != null)
                    {
                        if (value)
                        {
                            _cameraWatchdog?.Stop();
                            _camera.Stop();
                        }
                        else
                        {
                            Interlocked.Exchange(ref _lastCameraFrameTicks, DateTime.UtcNow.Ticks);
                            _camera.Start();
                            _cameraWatchdog?.Start();
                        }
                    }
                    else if (!value && _cameraRelay == null && IsVideoMode && _mediaStream != null)
                    {
                        StartVideoLoop();
                    }
                    if (value)
                        LocalVideoFrameReady?.Invoke(GenerateCameraOffFrame());
                }
                catch (Exception ex) { CameraError?.Invoke(ex.Message); }
            }
        }
        public bool IsVideoMode { get; set; }
        public string LocalUserName { get; set; } = "Vous";
        public string RemoteUserName { get; set; } = "Contact";

        public event Action<ImageSource> LocalVideoFrameReady;
        public event Action<ImageSource> RemoteVideoFrameReady;
        public event Action<double> LocalAudioLevelChanged;
        public event Action<double> RemoteAudioLevelChanged;
        public event Action<string> CameraError;

        public CallMediaEngine(bool isVideo)
        {
            IsVideoMode = isVideo;
        }

        // Démarrer l'écoute TCP média (pour l'appelant ou appelé)
        public int StartListening()
        {
            _mediaListener = new TcpListener(IPAddress.Any, 0);
            _mediaListener.Start();
            MediaPort = ((IPEndPoint)_mediaListener.LocalEndpoint).Port;

            _ = Task.Run(async () =>
            {
                try
                {
                    var client = await _mediaListener.AcceptTcpClientAsync(_cts.Token);
                    SetupMediaStream(client);
                }
                catch { }
            });

            return MediaPort;
        }

        public void StartVideoCapture()
        {
            if (IsVideoMode) StartVideoLoop();
        }

        // Se connecter au port média du pair
        public async Task<bool> ConnectAsync(string ip, int port)
        {
            try
            {
                var client = new TcpClient();
                using (var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                {
                    await client.ConnectAsync(ip, port, connectCts.Token);
                }
                SetupMediaStream(client);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void SetupMediaStream(TcpClient client)
        {
            _mediaClient = client;
            _mediaStream = client.GetStream();

            // Démarrer audio in / out
            StartAudioCapture();
            StartAudioPlayback();

            // Démarrer la boucle de réception réseau
            _ = Task.Run(ReceiveLoop);

            // Si mode vidéo, démarrer l'envoi de trames
            if (IsVideoMode)
            {
                _ui.BeginInvoke(new Action(StartVideoLoop));
            }
        }

        // ================= AUDIO (waveIn / waveOut) =================

        private void StartAudioCapture()
        {
            try
            {
                var fmt = new WAVEFORMATEX
                {
                    wFormatTag = WAVE_FORMAT_PCM,
                    nChannels = 1,
                    nSamplesPerSec = 16000,
                    nAvgBytesPerSec = 32000,
                    nBlockAlign = 2,
                    wBitsPerSample = 16,
                    cbSize = 0
                };

                _inCallback = OnWaveIn;
                int res = waveInOpen(out _hWaveIn, WAVE_MAPPER, ref fmt, _inCallback, IntPtr.Zero, CALLBACK_FUNCTION);
                if (res != 0) return;

                for (int i = 0; i < NumAudioBuffers; i++)
                {
                    _inBuffers[i] = Marshal.AllocHGlobal(AudioBufferSize);
                    var hdr = new WAVEHDR
                    {
                        lpData = _inBuffers[i],
                        dwBufferLength = AudioBufferSize
                    };
                    _inHdrs[i] = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WAVEHDR)));
                    Marshal.StructureToPtr(hdr, _inHdrs[i], false);

                    waveInPrepareHeader(_hWaveIn, _inHdrs[i], Marshal.SizeOf(typeof(WAVEHDR)));
                    waveInAddBuffer(_hWaveIn, _inHdrs[i], Marshal.SizeOf(typeof(WAVEHDR)));
                }

                waveInStart(_hWaveIn);
            }
            catch { }
        }

        private void OnWaveIn(IntPtr hWave, int uMsg, IntPtr dwUser, IntPtr dwParam1, IntPtr dwParam2)
        {
            if (uMsg != WIM_DATA) return;
            Interlocked.Increment(ref _waveInCallbackCount);
            try
            {
                if (_isDisposed || _hWaveIn == IntPtr.Zero) return;
                IntPtr pHdr = dwParam1;
                var hdr = Marshal.PtrToStructure<WAVEHDR>(pHdr);

                if (hdr.dwBytesRecorded > 0)
                {
                    byte[] data = new byte[hdr.dwBytesRecorded];
                    Marshal.Copy(hdr.lpData, data, 0, (int)hdr.dwBytesRecorded);

                    // Calcul du niveau audio
                    double rms = ComputeRms(data);
                    _localAudioLevel = rms;
                    _ui.BeginInvoke(new Action(() => LocalAudioLevelChanged?.Invoke(rms)));

                    // Envoyer au pair si pas en sourdine
                    if (!IsMuted && _mediaStream != null && _mediaStream.CanWrite)
                    {
                        SendPacket(1, data); // 1 = Audio
                    }
                }

                if (!_isDisposed && _hWaveIn != IntPtr.Zero)
                    waveInAddBuffer(_hWaveIn, pHdr, Marshal.SizeOf(typeof(WAVEHDR)));

            }
            catch { }
            finally { Interlocked.Decrement(ref _waveInCallbackCount); }
        }

        private void StartAudioPlayback()
        {
            try
            {
                var fmt = new WAVEFORMATEX
                {
                    wFormatTag = WAVE_FORMAT_PCM,
                    nChannels = 1,
                    nSamplesPerSec = 16000,
                    nAvgBytesPerSec = 32000,
                    nBlockAlign = 2,
                    wBitsPerSample = 16,
                    cbSize = 0
                };

                _outCallback = OnWaveOut;
                waveOutOpen(out _hWaveOut, WAVE_MAPPER, ref fmt, _outCallback, IntPtr.Zero, CALLBACK_FUNCTION);
            }
            catch { }
        }

        private void OnWaveOut(IntPtr hWave, int uMsg, IntPtr dwUser, IntPtr dwParam1, IntPtr dwParam2)
        {
            if (uMsg != WOM_DONE || dwParam1 == IntPtr.Zero) return;
            Interlocked.Increment(ref _waveOutCallbackCount);
            try
            {
                if (_queuedWaveOutBuffers.TryRemove(dwParam1, out _))
                    ReleaseWaveOutBuffer(dwParam1);
            }
            finally { Interlocked.Decrement(ref _waveOutCallbackCount); }
        }

        private void PlayAudioChunk(byte[] pcmData)
        {
            if (pcmData == null || pcmData.Length == 0) return;
            lock (_waveOutWriteLock)
            {
                if (_isDisposed || _hWaveOut == IntPtr.Zero) return;
                IntPtr pData = IntPtr.Zero;
                IntPtr pHdr = IntPtr.Zero;
                bool prepared = false;
                bool tracked = false;
                try
                {
                double rms = ComputeRms(pcmData);
                _remoteAudioLevel = rms;
                _ui.BeginInvoke(new Action(() => RemoteAudioLevelChanged?.Invoke(rms)));

                pData = Marshal.AllocHGlobal(pcmData.Length);
                Marshal.Copy(pcmData, 0, pData, pcmData.Length);

                var hdr = new WAVEHDR
                {
                    lpData = pData,
                    dwBufferLength = (uint)pcmData.Length
                };
                pHdr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WAVEHDR)));
                Marshal.StructureToPtr(hdr, pHdr, false);

                if (waveOutPrepareHeader(_hWaveOut, pHdr, Marshal.SizeOf(typeof(WAVEHDR))) != 0) return;
                prepared = true;
                _queuedWaveOutBuffers[pHdr] = 0;
                tracked = true;
                if (waveOutWrite(_hWaveOut, pHdr, Marshal.SizeOf(typeof(WAVEHDR))) != 0)
                {
                    if (_queuedWaveOutBuffers.TryRemove(pHdr, out _))
                        ReleaseWaveOutBuffer(pHdr);
                    pHdr = IntPtr.Zero;
                    pData = IntPtr.Zero;
                }
                else
                {
                    pHdr = IntPtr.Zero;
                    pData = IntPtr.Zero;
                }
                }
                catch { }
                finally
                {
                    if (pHdr != IntPtr.Zero)
                    {
                        if (tracked)
                        {
                            if (_queuedWaveOutBuffers.TryRemove(pHdr, out _))
                                ReleaseWaveOutBuffer(pHdr);
                        }
                        else
                        {
                            if (prepared) ReleaseWaveOutBuffer(pHdr);
                            else
                            {
                                try
                                {
                                    var hdr = Marshal.PtrToStructure<WAVEHDR>(pHdr);
                                    if (hdr.lpData != IntPtr.Zero) Marshal.FreeHGlobal(hdr.lpData);
                                    Marshal.FreeHGlobal(pHdr);
                                }
                                catch { }
                            }
                        }
                        pHdr = IntPtr.Zero;
                        pData = IntPtr.Zero;
                    }
                    if (pData != IntPtr.Zero)
                        try { Marshal.FreeHGlobal(pData); } catch { }
                }
            }
        }

        private void ReleaseWaveOutBuffer(IntPtr headerPtr)
        {
            if (headerPtr == IntPtr.Zero) return;
            try
            {
                if (_hWaveOut != IntPtr.Zero)
                    waveOutUnprepareHeader(_hWaveOut, headerPtr, Marshal.SizeOf(typeof(WAVEHDR)));
                var hdr = Marshal.PtrToStructure<WAVEHDR>(headerPtr);
                if (hdr.lpData != IntPtr.Zero) Marshal.FreeHGlobal(hdr.lpData);
                Marshal.FreeHGlobal(headerPtr);
            }
            catch { }
        }

        private static double ComputeRms(byte[] buffer)
        {
            if (buffer.Length < 2) return 0;
            double sum = 0;
            int count = buffer.Length / 2;
            for (int i = 0; i < buffer.Length - 1; i += 2)
            {
                short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                sum += (sample / 32768.0) * (sample / 32768.0);
            }
            double rms = Math.Sqrt(sum / count);
            return Math.Min(1.0, rms * 5.0); // Boost level for visualizer
        }

        // ================= VIDÉO (Stream dynamique + JPEG) =================

        private async void StartVideoLoop()
        {
            if (_isDisposed || _camera != null || IsCameraOff) return;
            try
            {
                _camera = new CameraCapture();
                _camera.FrameReady += OnCameraFrame;
                _camera.Start();
                _cameraRelay = SharedCameraRelay.StartHost();
                Interlocked.Exchange(ref _lastCameraFrameTicks, DateTime.UtcNow.Ticks);
                StartCameraWatchdog();
            }
            catch (Exception ex)
            {
                try { _camera?.Dispose(); } catch { }
                _camera = null;
                try
                {
                    // Another ChatLocal profile on this PC may already own the physical webcam.
                    var relay = await SharedCameraRelay.ConnectToHostAsync(TimeSpan.FromSeconds(5), _cts.Token);
                    if (!_isDisposed && relay != null)
                    {
                        _cameraRelay?.Dispose();
                        _cameraRelay = relay;
                        AttachSharedCameraRelay(_cameraRelay);
                        Interlocked.Exchange(ref _lastCameraFrameTicks, DateTime.UtcNow.Ticks);
                        StartCameraWatchdog();
                        return;
                    }
                    relay?.Dispose();
                }
                catch { }

                if (!_isDisposed)
                {
                    IsCameraOff = true;
                    CameraError?.Invoke("Accès à la caméra impossible. Vérifiez les autorisations de confidentialité de Windows, qu’une autre application ne la monopolise pas, puis relancez l’appel. " + ex.Message);
                }
            }
        }

        private void StartCameraWatchdog()
        {
            _cameraWatchdog?.Stop();
            _cameraWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _cameraWatchdog.Tick += (s, e) =>
            {
                long lastFrame = Interlocked.Read(ref _lastCameraFrameTicks);
                if (!_isDisposed && !IsCameraOff && DateTime.UtcNow.Ticks - lastFrame > TimeSpan.FromSeconds(6).Ticks)
                {
                    IsCameraOff = true;
                    CameraError?.Invoke("Aucune image caméra reçue. Vérifiez que la caméra est activée et qu’aucune autre application ne l’utilise.");
                    Interlocked.Exchange(ref _lastCameraFrameTicks, DateTime.UtcNow.Ticks);
                }
            };
            _cameraWatchdog.Start();
        }

        private void OnSharedCameraFrame(byte[] jpeg, int width, int height)
        {
            if (_isDisposed || IsCameraOff || jpeg == null) return;
            // A remote profile may resume publishing after a temporary camera pause.
            if (_isCameraOff) _isCameraOff = false;
            Interlocked.Exchange(ref _lastCameraFrameTicks, DateTime.UtcNow.Ticks);
            try
            {
                using var stream = new MemoryStream(jpeg, writable: false);
                var frame = new BitmapImage();
                frame.BeginInit();
                frame.CacheOption = BitmapCacheOption.OnLoad;
                frame.StreamSource = stream;
                frame.EndInit();
                frame.Freeze();
                LocalVideoFrameReady?.Invoke(frame);
                if (_mediaStream != null && _mediaStream.CanWrite) SendPacket(2, jpeg);
            }
            catch { }
        }

        private void AttachSharedCameraRelay(SharedCameraRelay relay)
        {
            relay.FrameReceived += OnSharedCameraFrame;
            relay.ConnectionEnded += OnSharedCameraConnectionEnded;
        }

        private void OnSharedCameraConnectionEnded()
        {
            if (_isDisposed || IsCameraOff || _camera != null) return;
            if (Interlocked.Exchange(ref _cameraRecoveryRunning, 1) != 0) return;
            _ = RecoverCameraSourceAsync();
        }

        private async Task RecoverCameraSourceAsync()
        {
            try
            {
                await Task.Delay(250).ConfigureAwait(false);
                if (_isDisposed || IsCameraOff || _camera != null) return;

                try
                {
                    await _ui.InvokeAsync(new Action(() =>
                    {
                        var camera = new CameraCapture();
                        camera.FrameReady += OnCameraFrame;
                        camera.Start();
                        _camera = camera;
                    })).Task.ConfigureAwait(false);
                    if (_cameraRelay != null)
                    {
                        _cameraRelay.FrameReceived -= OnSharedCameraFrame;
                        _cameraRelay.ConnectionEnded -= OnSharedCameraConnectionEnded;
                        _cameraRelay.Dispose();
                    }
                    _cameraRelay = SharedCameraRelay.StartHost();
                    Interlocked.Exchange(ref _lastCameraFrameTicks, DateTime.UtcNow.Ticks);
                    _ = _ui.BeginInvoke(new Action(StartCameraWatchdog));
                    return;
                }
                catch { }

                var replacement = await SharedCameraRelay.ConnectToHostAsync(TimeSpan.FromSeconds(5), _cts.Token).ConfigureAwait(false);
                if (_isDisposed) { replacement?.Dispose(); return; }
                if (replacement != null)
                {
                    _cameraRelay?.Dispose();
                    _cameraRelay = replacement;
                    AttachSharedCameraRelay(replacement);
                    Interlocked.Exchange(ref _lastCameraFrameTicks, DateTime.UtcNow.Ticks);
                    return;
                }

                _cameraRelay?.Dispose();
                _cameraRelay = null;
                _ = _ui.BeginInvoke(new Action(() =>
                {
                    if (_isDisposed) return;
                    IsCameraOff = true;
                    CameraError?.Invoke("Le profil qui partageait la caméra a fermé son appel et aucune caméra locale n’a pu prendre le relais.");
                }));
            }
            catch { }
            finally { Interlocked.Exchange(ref _cameraRecoveryRunning, 0); }
        }

        private void OnCameraFrame(byte[] pixels, int width, int height, int stride)
        {
            if (_isDisposed || IsCameraOff || pixels == null) return;
            Interlocked.Exchange(ref _lastCameraFrameTicks, DateTime.UtcNow.Ticks);
            try
            {
                BitmapSource frame = BitmapSource.Create(width, height, 96, 96,
                    PixelFormats.Bgr24, null, pixels, stride);
                frame.Freeze();
                LocalVideoFrameReady?.Invoke(frame);
                if (_cameraRelay != null || (_mediaStream != null && _mediaStream.CanWrite))
                {
                    byte[] jpeg = EncodeFrameToJpeg(frame);
                    if (jpeg != null)
                    {
                        _cameraRelay?.Publish(jpeg, width, height);
                        if (_mediaStream != null && _mediaStream.CanWrite) SendPacket(2, jpeg);
                    }
                }
            }
            catch { return; }
        }

        private static BitmapSource GenerateCameraOffFrame()
        {
            int width = 320;
            int height = 240;

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(17, 27, 33)), null, new Rect(0, 0, width, height));
                var ftOff = new FormattedText("📷 Caméra désactivée",
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 14, Brushes.Gray, 1.25);
                dc.DrawText(ftOff, new Point((width - ftOff.Width) / 2, (height - ftOff.Height) / 2));
            }

            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        private static byte[] EncodeFrameToJpeg(BitmapSource src)
        {
            try
            {
                var enc = new JpegBitmapEncoder { QualityLevel = 60 };
                enc.Frames.Add(BitmapFrame.Create(src));
                using var ms = new MemoryStream();
                enc.Save(ms);
                return ms.ToArray();
            }
            catch
            {
                return null;
            }
        }

        // ================= PROTOCOLE RÉSEAU P2P (TCP) =================

        private void SendPacket(byte type, byte[] payload)
        {
            if (_mediaStream == null || !_mediaStream.CanWrite || _isDisposed) return;

            try
            {
                byte[] lenBytes = BitConverter.GetBytes(payload.Length);
                byte[] header = new byte[5];
                header[0] = type;
                Buffer.BlockCopy(lenBytes, 0, header, 1, 4);

                lock (_mediaStream)
                {
                    _mediaStream.Write(header, 0, 5);
                    _mediaStream.Write(payload, 0, payload.Length);
                }
            }
            catch { }
        }

        private async Task ReceiveLoop()
        {
            byte[] header = new byte[5];

            while (!_cts.IsCancellationRequested && !_isDisposed && _mediaStream != null)
            {
                try
                {
                    // Lire 5 octets d'en-tête (Type + Longueur)
                    if (!await ReadExactAsync(_mediaStream, header, 0, 5, _cts.Token))
                        break;

                    byte type = header[0];
                    int len = BitConverter.ToInt32(header, 1);
                    if (len <= 0 || len > 2000000) continue; // Sécurité

                    byte[] payload = new byte[len];
                    if (!await ReadExactAsync(_mediaStream, payload, 0, len, _cts.Token))
                        break;

                    if (type == 1) // Audio PCM
                    {
                        PlayAudioChunk(payload);
                    }
                    else if (type == 2) // Video JPEG
                    {
                        _ = _ui.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                var bi = new BitmapImage();
                                bi.BeginInit();
                                bi.CacheOption = BitmapCacheOption.OnLoad;
                                bi.StreamSource = new MemoryStream(payload);
                                bi.EndInit();
                                bi.Freeze();
                                RemoteVideoFrameReady?.Invoke(bi);
                            }
                            catch { }
                        }));
                    }
                }
                catch
                {
                    break;
                }
            }
        }

        private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int read = 0;
            while (read < count)
            {
                int r = await stream.ReadAsync(buffer, offset + read, count - read, ct);
                if (r <= 0) return false;
                read += r;
            }
            return true;
        }

        // ================= NETTOYAGE =================

        public void Dispose()
        {
            _isDisposed = true;
            _cts.Cancel();
            try { _mediaStream?.Dispose(); } catch { }

            try
            {
                _cameraWatchdog?.Stop();
                if (_cameraRelay != null)
                {
                    _cameraRelay.FrameReceived -= OnSharedCameraFrame;
                    _cameraRelay.ConnectionEnded -= OnSharedCameraConnectionEnded;
                }
                _cameraRelay?.Dispose();
                _cameraRelay = null;
                if (_camera != null)
                {
                    _camera.FrameReady -= OnCameraFrame;
                    _camera.Dispose();
                    _camera = null;
                }
            }
            catch { }

            // Fermer waveIn
            if (_hWaveIn != IntPtr.Zero)
            {
                try
                {
                    waveInReset(_hWaveIn);
                    SpinWait.SpinUntil(() => Volatile.Read(ref _waveInCallbackCount) == 0, TimeSpan.FromSeconds(2));
                    for (int i = 0; i < NumAudioBuffers; i++)
                    {
                        if (_inHdrs[i] != IntPtr.Zero)
                        {
                            waveInUnprepareHeader(_hWaveIn, _inHdrs[i], Marshal.SizeOf(typeof(WAVEHDR)));
                            Marshal.FreeHGlobal(_inHdrs[i]);
                            _inHdrs[i] = IntPtr.Zero;
                        }
                        if (_inBuffers[i] != IntPtr.Zero)
                        {
                            Marshal.FreeHGlobal(_inBuffers[i]);
                            _inBuffers[i] = IntPtr.Zero;
                        }
                    }
                    waveInClose(_hWaveIn);
                }
                catch { }
                _hWaveIn = IntPtr.Zero;
            }

            // Fermer waveOut
            if (_hWaveOut != IntPtr.Zero)
            {
                try
                {
                    lock (_waveOutWriteLock)
                        waveOutReset(_hWaveOut);
                    SpinWait.SpinUntil(
                        () => _queuedWaveOutBuffers.IsEmpty && Volatile.Read(ref _waveOutCallbackCount) == 0,
                        TimeSpan.FromSeconds(2));

                    foreach (var headerPtr in _queuedWaveOutBuffers.Keys)
                        if (_queuedWaveOutBuffers.TryRemove(headerPtr, out _))
                            ReleaseWaveOutBuffer(headerPtr);

                    SpinWait.SpinUntil(() => Volatile.Read(ref _waveOutCallbackCount) == 0, TimeSpan.FromSeconds(2));
                    waveOutClose(_hWaveOut);
                }
                catch { }
                _hWaveOut = IntPtr.Zero;
            }

            // Fermer connexions réseau
            try { _mediaStream?.Dispose(); } catch { }
            try { _mediaClient?.Close(); } catch { }
            try { _mediaListener?.Stop(); } catch { }
        }
    }
}
