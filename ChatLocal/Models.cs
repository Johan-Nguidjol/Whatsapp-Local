using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ChatLocal
{
    public static class Palette
    {
        private static readonly (string Bg, string Fg)[] Colors =
        {
            ("#EFEEFF", "#5149C6"), ("#DCEBFA", "#2869A8"), ("#FFF0DA", "#AD6B12"),
            ("#FCE3E7", "#B84E68"), ("#E9E2FA", "#7053B7"), ("#DDF2EE", "#258477")
        };

        public static (string Bg, string Fg) For(string name)
        {
            int h = 0;
            foreach (char ch in name ?? "") h = (h * 31 + ch) & 0x7FFFFFFF;
            return Colors[h % Colors.Length];
        }

        public static string InitialOf(string name)
            => string.IsNullOrWhiteSpace(name) ? "?" : name.Trim().Substring(0, 1).ToUpper();
    }

    public class Contact : ObservableObject
    {
        private string _name;
        private bool _online;
        private int _unread;
        private string _lastText;
        private DateTime? _lastTime;
        private bool _isArchived;
        private string _profilePhotoData;
        private int _ephemeralSeconds;

        public string Id { get; set; }
        public string Ip { get; set; }
        public int Port { get; set; }
        public DateTime LastSeen { get; set; }
        public string ProfilePhotoData
        {
            get => _profilePhotoData;
            set
            {
                if (Set(ref _profilePhotoData, value)) OnPropertyChanged(nameof(ProfilePhoto));
            }
        }

        [JsonIgnore]
        public ImageSource ProfilePhoto
        {
            get
            {
                if (string.IsNullOrWhiteSpace(ProfilePhotoData)) return null;
                try
                {
                    var bytes = Convert.FromBase64String(ProfilePhotoData);
                    using (var stream = new MemoryStream(bytes))
                    {
                        var image = new BitmapImage();
                        image.BeginInit();
                        image.CacheOption = BitmapCacheOption.OnLoad;
                        image.DecodePixelWidth = 128;
                        image.StreamSource = stream;
                        image.EndInit();
                        image.Freeze();
                        return image;
                    }
                }
                catch { return null; }
            }
        }
        public bool IsArchived
        {
            get => _isArchived;
            set => Set(ref _isArchived, value);
        }
        public int EphemeralSeconds { get => _ephemeralSeconds; set => Set(ref _ephemeralSeconds, value); }

        public string Name
        {
            get => _name;
            set
            {
                if (Set(ref _name, value))
                {
                    OnPropertyChanged(nameof(Initial));
                    OnPropertyChanged(nameof(AvatarBg));
                    OnPropertyChanged(nameof(AvatarFg));
                }
            }
        }

        public bool IsOnline
        {
            get => _online;
            set { if (Set(ref _online, value)) OnPropertyChanged(nameof(PresenceText)); }
        }

        public int Unread
        {
            get => _unread;
            set { if (Set(ref _unread, value)) OnPropertyChanged(nameof(HasUnread)); }
        }

        public string LastText
        {
            get => _lastText;
            set => Set(ref _lastText, value);
        }

        public DateTime? LastTime
        {
            get => _lastTime;
            set
            {
                if (Set(ref _lastTime, value))
                {
                    OnPropertyChanged(nameof(LastTimeText));
                    OnPropertyChanged(nameof(SortKey));
                }
            }
        }

        public string Initial => Palette.InitialOf(Name);
        public string AvatarBg => Palette.For(Name).Bg;
        public string AvatarFg => Palette.For(Name).Fg;
        public bool HasUnread => Unread > 0;
        public string PresenceText => IsOnline ? AppPreferences.Text("en ligne", "online") : AppPreferences.Text("hors ligne", "offline");
        public long SortKey => LastTime?.Ticks ?? 0;

        public string LastTimeText
        {
            get
            {
                if (LastTime == null) return "";
                var t = LastTime.Value;
                if (t.Date == DateTime.Today) return t.ToString("HH:mm");
                if (t.Date == DateTime.Today.AddDays(-1)) return AppPreferences.Text("Hier", "Yesterday");
                return t.ToString("dd/MM/yyyy");
            }
        }
    }

    public class Community : ObservableObject
    {
        private string _name;
        private int _unreadCount;
        private List<string> _memberIds = new List<string>();
        public string Id { get; set; }
        public string Name { get => _name; set => Set(ref _name, value); }
        public List<string> MemberIds
        {
            get => _memberIds;
            set
            {
                if (Set(ref _memberIds, value ?? new List<string>()))
                    OnPropertyChanged(nameof(MemberCount));
            }
        }
        public string MemberSummary { get; set; }
        public int MemberCount => MemberIds?.Count ?? 0;
        public string MemberCountText => AppPreferences.LanguageCode == "en"
            ? MemberCount + (MemberCount == 1 ? " member" : " members")
            : MemberCount + (MemberCount == 1 ? " membre" : " membres");
        public int UnreadCount
        {
            get => _unreadCount;
            set
            {
                if (Set(ref _unreadCount, value)) OnPropertyChanged(nameof(HasUnread));
            }
        }
        public bool HasUnread => UnreadCount > 0;
        public string Initial => Palette.InitialOf(Name);
        public string AvatarBg => Palette.For(Name).Bg;
        public string AvatarFg => Palette.For(Name).Fg;
        public void RefreshLocalizedText() => OnPropertyChanged(nameof(MemberCountText));
    }

    public class CommunityMemberOption : ObservableObject
    {
        private bool _isSelected;
        public Contact Contact { get; set; }
        public string Name => Contact?.Name ?? "Contact";
        public string PresenceText => Contact?.PresenceText ?? AppPreferences.Text("hors ligne", "offline");
        public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    }

    public class ChatMessage : ObservableObject
    {
        private int _state; // 0 = en attente, 1 = remis, 2 = échec
        private bool _revealed;
        private bool _viewed;
        private bool _isPlayingVoice;
        private double _voiceProgress;

        public string Id { get; set; }
        public string ContactId { get; set; }
        public string CommunityId { get; set; }
        public string SenderName { get; set; }
        public bool IsMine { get; set; }
        public string Text { get; set; }
        public string Kind { get; set; } = "text"; // "text", "image", "voice", "call"
        public string Data { get; set; }
        public string AttachmentName { get; set; }
        public bool ViewOnce { get; set; }
        public int EphemeralSeconds { get; set; }
        public string VoiceDuration { get; set; }

        public bool Viewed
        {
            get => _viewed;
            set
            {
                if (Set(ref _viewed, value))
                {
                    OnPropertyChanged(nameof(IsOpenable));
                    OnPropertyChanged(nameof(CanShowImage));
                    OnPropertyChanged(nameof(CanShowText));
                    OnPropertyChanged(nameof(ViewOnceStatusText));
                    OnPropertyChanged(nameof(IsViewOnceDone));
                }
            }
        }

        public bool Revealed
        {
            get => _revealed;
            set
            {
                if (Set(ref _revealed, value))
                {
                    OnPropertyChanged(nameof(IsOpenable));
                    OnPropertyChanged(nameof(CanShowImage));
                    OnPropertyChanged(nameof(CanShowText));
                    OnPropertyChanged(nameof(ViewOnceStatusText));
                    OnPropertyChanged(nameof(IsViewOnceDone));
                }
            }
        }

        [JsonIgnore] public bool IsImage => Kind == "image" && !string.IsNullOrEmpty(Data);
        [JsonIgnore] public bool IsFile => Kind == "file" && !string.IsNullOrEmpty(Data);
        [JsonIgnore] public bool IsSticker => Kind == "sticker";
        [JsonIgnore] public bool IsVoice => Kind == "voice" && !string.IsNullOrEmpty(Data);
        [JsonIgnore] public bool IsCall => Kind == "call";
        [JsonIgnore] public bool IsOpenable => ViewOnce && !IsMine && !Revealed && !Viewed;
        [JsonIgnore] public bool IsViewOnceDone => ViewOnce && (Revealed || Viewed);
        [JsonIgnore] public bool CanShowImage => IsImage && !ViewOnce;
        [JsonIgnore] public bool CanShowText => (Kind == "text" || string.IsNullOrEmpty(Kind)) && !ViewOnce;

        [JsonIgnore]
        public string ViewOnceStatusText
        {
            get
            {
                if (!ViewOnce) return "";
                if (Viewed || Revealed) return AppPreferences.Text("① Ouvert", "① Opened");
                if (Kind == "image") return IsMine ? "① Photo" : AppPreferences.Text("① Photo à vue unique", "① View-once photo");
                return IsMine ? "① Message" : AppPreferences.Text("① Message à vue unique", "① View-once message");
            }
        }

        [JsonIgnore]
        public bool IsPlayingVoice
        {
            get => _isPlayingVoice;
            set => Set(ref _isPlayingVoice, value);
        }

        [JsonIgnore]
        public double VoiceProgress
        {
            get => _voiceProgress;
            set => Set(ref _voiceProgress, value);
        }

        [JsonIgnore]
        public ImageSource Image
        {
            get
            {
                if (!IsImage) return null;
                try
                {
                    var bytes = Convert.FromBase64String(Data);
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.StreamSource = new MemoryStream(bytes);
                    bi.EndInit();
                    bi.Freeze();
                    return bi;
                }
                catch { return null; }
            }
        }

        public DateTime Time { get; set; }

        public int State
        {
            get => _state;
            set
            {
                if (Set(ref _state, value))
                {
                    OnPropertyChanged(nameof(IsPending));
                    OnPropertyChanged(nameof(IsDelivered));
                    OnPropertyChanged(nameof(IsFailed));
                }
            }
        }

        [JsonIgnore] public bool IsPending => State == 0;
        [JsonIgnore] public bool IsDelivered => State == 1;
        [JsonIgnore] public bool IsFailed => State == 2;
        [JsonIgnore] public string TimeText => Time.ToLocalTime().ToString("HH:mm");
    }

    public class StatusPost : ObservableObject
    {
        private bool _seen;
        private ImageSource _image;

        public string Id { get; set; }
        public string AuthorId { get; set; }
        public string AuthorName { get; set; }
        public string Kind { get; set; } = "text";   // "text" ou "image"
        public string Text { get; set; }
        public string Color { get; set; }
        public string ImageData { get; set; }         // JPEG en base64 (statut photo)
        public DateTime Time { get; set; }
        public List<string> Viewers { get; set; } = new List<string>();

        public bool Seen
        {
            get => _seen;
            set => Set(ref _seen, value);
        }

        [JsonIgnore] public bool IsImage => Kind == "image";
        [JsonIgnore] public bool IsText => Kind != "image";
        [JsonIgnore] public int ViewCount => Viewers?.Count ?? 0;

        public void RaiseViewCount() => OnPropertyChanged(nameof(ViewCount));

        [JsonIgnore]
        public ImageSource Image
        {
            get
            {
                if (_image == null && !string.IsNullOrEmpty(ImageData))
                {
                    try
                    {
                        var bytes = Convert.FromBase64String(ImageData);
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.StreamSource = new MemoryStream(bytes);
                        bi.EndInit();
                        bi.Freeze();
                        _image = bi;
                    }
                    catch { }
                }
                return _image;
            }
        }

        [JsonIgnore]
        public string TimeText
        {
            get
            {
                var t = Time.ToLocalTime();
                return (t.Date == DateTime.Today ? AppPreferences.Text("Aujourd’hui", "Today") : AppPreferences.Text("Hier", "Yesterday")) + " " + AppPreferences.Text("à", "at") + " " + t.ToString("HH:mm");
            }
        }
    }

    public class StatusGroup
    {
        private Brush _thumb;

        public StatusGroup(string authorId, string authorName, List<StatusPost> posts, bool isMine = false)
        {
            AuthorId = authorId;
            AuthorName = authorName;
            Posts = posts;
            IsMine = isMine;
        }

        public string AuthorId { get; }
        public string AuthorName { get; }
        public List<StatusPost> Posts { get; }
        public bool IsMine { get; }

        public int Total => Posts.Count;
        public int SeenCount => IsMine ? Posts.Count : Posts.Count(p => p.Seen);
        public bool HasUnseen => !IsMine && Posts.Any(p => !p.Seen);
        public bool NoPosts => Posts.Count == 0;

        public string Initial => Palette.InitialOf(AuthorName);
        public string AvatarBg => Palette.For(AuthorName).Bg;
        public string AvatarFg => Palette.For(AuthorName).Fg;

        public Brush ThumbBrush
        {
            get
            {
                if (_thumb != null) return _thumb;

                Brush b;
                var last = Posts.LastOrDefault();
                if (last == null)
                    b = (Brush)new BrushConverter().ConvertFromString(AvatarBg);
                else if (last.IsImage && last.Image != null)
                    b = new ImageBrush(last.Image) { Stretch = Stretch.UniformToFill };
                else
                    b = (Brush)new BrushConverter().ConvertFromString(last.Color ?? "#635BDB");

                b.Freeze();
                _thumb = b;
                return b;
            }
        }

        public string TimeText => Posts.Count == 0 ? AppPreferences.Text("Cliquez pour ajouter un statut", "Click to add a status") : Posts.Last().TimeText;
    }

    public class SegmentVM : ObservableObject
    {
        private double _value;
        public double Value
        {
            get => _value;
            set => Set(ref _value, value);
        }
    }
}
