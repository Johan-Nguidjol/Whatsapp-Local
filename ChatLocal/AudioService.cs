using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Threading;

namespace ChatLocal
{
    public static class AudioRecorder
    {
        [DllImport("winmm.dll", EntryPoint = "mciSendStringA", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern int mciSendString(string command, StringBuilder returnString, int returnLength, IntPtr hwndCallback);

        private static string _currentRecordingFile;
        private static bool _isRecording;

        public static bool IsRecording => _isRecording;

        public static bool StartRecording(out string error)
        {
            error = null;
            try
            {
                StopRecording(false);

                _currentRecordingFile = Path.Combine(Path.GetTempPath(), $"chatlocal_voice_{Guid.NewGuid():N}.wav");

                mciSendString("close voiceRec", null, 0, IntPtr.Zero);
                mciSendString("open new Type waveaudio Alias voiceRec", null, 0, IntPtr.Zero);
                mciSendString("set voiceRec bitspersample 16 channels 1 samplespersec 16000", null, 0, IntPtr.Zero);
                mciSendString("record voiceRec", null, 0, IntPtr.Zero);

                _isRecording = true;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                _isRecording = false;
                return false;
            }
        }

        public static string StopRecording(bool save)
        {
            if (!_isRecording && string.IsNullOrEmpty(_currentRecordingFile))
                return null;

            string base64Data = null;
            try
            {
                if (save && !string.IsNullOrEmpty(_currentRecordingFile))
                {
                    mciSendString($"save voiceRec \"{_currentRecordingFile}\"", null, 0, IntPtr.Zero);
                    mciSendString("close voiceRec", null, 0, IntPtr.Zero);
                    _isRecording = false;

                    if (File.Exists(_currentRecordingFile))
                    {
                        var bytes = File.ReadAllBytes(_currentRecordingFile);
                        if (bytes.Length > 200)
                        {
                            base64Data = Convert.ToBase64String(bytes);
                        }
                        try { File.Delete(_currentRecordingFile); } catch { }
                    }
                }
                else
                {
                    mciSendString("close voiceRec", null, 0, IntPtr.Zero);
                    _isRecording = false;
                    if (!string.IsNullOrEmpty(_currentRecordingFile) && File.Exists(_currentRecordingFile))
                    {
                        try { File.Delete(_currentRecordingFile); } catch { }
                    }
                }
            }
            catch
            {
                base64Data = null;
            }
            finally
            {
                _isRecording = false;
                _currentRecordingFile = null;
            }

            return base64Data;
        }
    }

    public class AudioPlayer : IDisposable
    {
        private readonly MediaPlayer _player = new MediaPlayer();
        private readonly DispatcherTimer _timer;
        private string _tempFile;
        private ChatMessage _currentMessage;

        public event Action<ChatMessage, double> ProgressChanged;
        public event Action<ChatMessage> PlaybackEnded;

        public ChatMessage CurrentMessage => _currentMessage;
        public bool IsPlaying { get; private set; }

        public AudioPlayer()
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _timer.Tick += Timer_Tick;

            _player.MediaEnded += (s, e) => Stop();
            _player.MediaFailed += (s, e) => Stop();
        }

        public void Play(ChatMessage msg)
        {
            if (msg == null || string.IsNullOrEmpty(msg.Data)) return;

            if (_currentMessage == msg && IsPlaying)
            {
                Pause();
                return;
            }

            Stop();

            try
            {
                byte[] bytes = Convert.FromBase64String(msg.Data);
                _tempFile = Path.Combine(Path.GetTempPath(), $"chatlocal_play_{Guid.NewGuid():N}.wav");
                File.WriteAllBytes(_tempFile, bytes);

                _currentMessage = msg;
                _player.Open(new Uri(_tempFile));
                _player.Play();
                IsPlaying = true;
                _timer.Start();

                msg.IsPlayingVoice = true;
            }
            catch
            {
                Stop();
            }
        }

        public void Pause()
        {
            if (IsPlaying)
            {
                _player.Pause();
                IsPlaying = false;
                _timer.Stop();
                if (_currentMessage != null)
                {
                    _currentMessage.IsPlayingVoice = false;
                }
            }
        }

        public void Stop()
        {
            _timer.Stop();
            try { _player.Stop(); _player.Close(); } catch { }
            IsPlaying = false;

            if (_currentMessage != null)
            {
                _currentMessage.IsPlayingVoice = false;
                _currentMessage.VoiceProgress = 0;
                PlaybackEnded?.Invoke(_currentMessage);
                _currentMessage = null;
            }

            if (!string.IsNullOrEmpty(_tempFile) && File.Exists(_tempFile))
            {
                try { File.Delete(_tempFile); } catch { }
                _tempFile = null;
            }
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (!IsPlaying || _currentMessage == null || !_player.NaturalDuration.HasTimeSpan)
                return;

            double total = _player.NaturalDuration.TimeSpan.TotalSeconds;
            double current = _player.Position.TotalSeconds;

            if (total > 0)
            {
                double prog = Math.Min(1.0, current / total);
                _currentMessage.VoiceProgress = prog;
                ProgressChanged?.Invoke(_currentMessage, prog);
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
