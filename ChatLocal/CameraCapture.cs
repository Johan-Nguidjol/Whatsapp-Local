using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Threading;
using DirectShowLib;

namespace ChatLocal
{
    /// <summary>Captures real webcam frames through the Windows DirectShow camera graph.</summary>
    public sealed class CameraCapture : ISampleGrabberCB, IDisposable
    {
        private IGraphBuilder _graph;
        private ICaptureGraphBuilder2 _captureGraph;
        private ISampleGrabber _sampleGrabber;
        private IBaseFilter _captureFilter;
        private IBaseFilter _nullRenderer;
        private int _width;
        private int _height;
        private int _stride;
        private bool _topDown;
        private bool _disposed;
        private int _activeFrameCallbacks;

        public event Action<byte[], int, int, int> FrameReady;

        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CameraCapture));
            if (_graph != null)
            {
                DsError.ThrowExceptionForHR(((IMediaControl)_graph).Run());
                return;
            }

            DsDevice[] devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
            if (devices == null || devices.Length == 0)
                throw new InvalidOperationException("Aucune caméra n’a été détectée.");

            var failures = new List<string>();
            try
            {
                foreach (var device in devices)
                {
                    string deviceName = device.Name ?? "Caméra";
                    try
                    {
                        StartDevice(device);
                        return;
                    }
                    catch (Exception ex)
                    {
                        failures.Add(deviceName + ": " + ex.Message);
                        ReleaseGraph();
                    }
                }
            }
            finally
            {
                foreach (var device in devices)
                    try { if (device.Mon != null) Marshal.ReleaseComObject(device.Mon); } catch { }
            }

            throw new InvalidOperationException("Aucune caméra n’a pu démarrer. " + string.Join(" | ", failures));
        }

        private void StartDevice(DsDevice device)
        {
            Guid filterId = typeof(IBaseFilter).GUID;
            device.Mon.BindToObject(null, null, ref filterId, out object source);
            _captureFilter = (IBaseFilter)source;

            _graph = (IGraphBuilder)new FilterGraph();
            _captureGraph = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();
            DsError.ThrowExceptionForHR(_captureGraph.SetFiltergraph(_graph));
            DsError.ThrowExceptionForHR(_graph.AddFilter(_captureFilter, device.Name ?? "Webcam"));

            _sampleGrabber = (ISampleGrabber)new SampleGrabber();
            var mediaType = new AMMediaType
            {
                majorType = MediaType.Video,
                subType = MediaSubType.RGB24,
                formatType = FormatType.VideoInfo
            };
            try { DsError.ThrowExceptionForHR(_sampleGrabber.SetMediaType(mediaType)); }
            finally { DsUtils.FreeAMMediaType(mediaType); }
            DsError.ThrowExceptionForHR(_sampleGrabber.SetOneShot(false));
            DsError.ThrowExceptionForHR(_sampleGrabber.SetBufferSamples(false));
            DsError.ThrowExceptionForHR(_sampleGrabber.SetCallback(this, 1));

            DsError.ThrowExceptionForHR(_graph.AddFilter((IBaseFilter)_sampleGrabber, "Video frame callback"));
            _nullRenderer = (IBaseFilter)new NullRenderer();
            DsError.ThrowExceptionForHR(_graph.AddFilter(_nullRenderer, "Video sink"));
            DsError.ThrowExceptionForHR(_captureGraph.RenderStream(
                PinCategory.Capture, MediaType.Video, _captureFilter,
                (IBaseFilter)_sampleGrabber, _nullRenderer));

            var connectedType = new AMMediaType();
            try
            {
                DsError.ThrowExceptionForHR(_sampleGrabber.GetConnectedMediaType(connectedType));
                if (connectedType.formatPtr == IntPtr.Zero)
                    throw new InvalidOperationException("La caméra n’a pas fourni de format vidéo.");

                BitmapInfoHeader bitmapHeader;
                if (connectedType.formatType == FormatType.VideoInfo)
                    bitmapHeader = Marshal.PtrToStructure<VideoInfoHeader>(connectedType.formatPtr).BmiHeader;
                else if (connectedType.formatType == FormatType.VideoInfo2)
                    bitmapHeader = Marshal.PtrToStructure<VideoInfoHeader2>(connectedType.formatPtr).BmiHeader;
                else
                    throw new InvalidOperationException("Format vidéo non pris en charge : " + connectedType.formatType);

                _width = Math.Abs(bitmapHeader.Width);
                _height = Math.Abs(bitmapHeader.Height);
                _topDown = bitmapHeader.Height < 0;
                _stride = ((_width * 3 + 3) / 4) * 4;
                if (_width == 0 || _height == 0)
                    throw new InvalidOperationException("La résolution de la caméra est invalide.");
            }
            finally { DsUtils.FreeAMMediaType(connectedType); }

            DsError.ThrowExceptionForHR(((IMediaControl)_graph).Run());
        }

        public void Stop()
        {
            if (_graph == null || _disposed) return;
            try { ((IMediaControl)_graph).Stop(); } catch { }
        }

        int ISampleGrabberCB.SampleCB(double sampleTime, IMediaSample sample) => 0;

        int ISampleGrabberCB.BufferCB(double sampleTime, IntPtr buffer, int bufferLength)
        {
            Interlocked.Increment(ref _activeFrameCallbacks);
            try
            {
                if (_disposed || buffer == IntPtr.Zero || _width <= 0 || _height <= 0) return 0;
                int rowBytes = _width * 3;
                if (bufferLength < _stride * _height) return 0;

                var pixels = new byte[rowBytes * _height];
                for (int row = 0; row < _height; row++)
                {
                    int sourceRow = _topDown ? row : _height - row - 1;
                    Marshal.Copy(IntPtr.Add(buffer, sourceRow * _stride), pixels, row * rowBytes, rowBytes);
                }

                FrameReady?.Invoke(pixels, _width, _height, rowBytes);
                return 0;
            }
            finally { Interlocked.Decrement(ref _activeFrameCallbacks); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { if (_graph != null) ((IMediaControl)_graph).Stop(); } catch { }
            SpinWait.SpinUntil(() => Volatile.Read(ref _activeFrameCallbacks) == 0, TimeSpan.FromSeconds(2));
            ReleaseGraph();
        }

        private void ReleaseGraph()
        {
            try { if (_graph != null) ((IMediaControl)_graph).Stop(); } catch { }
            ReleaseComObject(_captureGraph);
            _captureGraph = null;
            ReleaseComObject(_nullRenderer);
            _nullRenderer = null;
            ReleaseComObject(_sampleGrabber);
            _sampleGrabber = null;
            ReleaseComObject(_captureFilter);
            _captureFilter = null;
            ReleaseComObject(_graph);
            _graph = null;
        }

        private static void ReleaseComObject(object value)
        {
            try { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
            catch { }
        }
    }
}
