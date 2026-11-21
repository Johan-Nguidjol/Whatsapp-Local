using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ChatLocal
{
    public class EphemeralOption
    {
        public int Seconds { get; set; }
        public string Label { get; set; }
    }

    public class EmojiCategory
    {
        public string Name { get; set; }
        public string Icon { get; set; }
        public List<string> Emojis { get; set; } = new List<string>();
    }

    public class SettingsSection
    {
        public string Key { get; set; }
        public string Icon { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
    }

    public class MetaAiDemoMessage
    {
        public bool IsFromUser { get; set; }
        public string Text { get; set; }
        public string Time { get; set; }
    }

    public class MainViewModel : ObservableObject, IDisposable
    {
        private static readonly HttpClient MetaAiHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        private readonly Dispatcher _ui = Application.Current.Dispatcher;
        private readonly DataStore _store;
        private bool _readyForPersistence;
        private bool _isDisposed;
        private byte[] _profilePhotoBytes;
        private readonly NetworkService _net;
        private readonly DispatcherTimer _timer;
        private readonly List<ChatMessage> _messages;
        private readonly List<StatusPost> _statuses;
        private readonly List<CommunityData> _communities;
        private int _tick;

        // Audio Services
        private readonly AudioPlayer _audioPlayer;
        private readonly DispatcherTimer _voiceTimer;
        private int _recordingSeconds;

        // Call Services & Media Engine
        private CallMediaEngine _mediaEngine;
        private readonly DispatcherTimer _callTimer;
        private int _callSeconds;
        private string _activeCallId;
        private Contact _activeCallContact;
        private int _incomingMediaPort;

        // View Once Services
        private readonly DispatcherTimer _viewOnceTimer;
        private int _viewOnceCountdown;
        private ChatMessage _currentViewOnceMessage;

        public event Action ScrollRequested;
        public event Action FocusRequested;
        public event Action SwitchAccountRequested;

        // ---------- Collections ----------
        public ObservableCollection<Contact> Contacts { get; } = new ObservableCollection<Contact>();
        public ObservableCollection<Community> Communities { get; } = new ObservableCollection<Community>();
        public ObservableCollection<CommunityMemberOption> CommunityMemberOptions { get; } = new ObservableCollection<CommunityMemberOption>();
        public EphemeralOption[] EphemeralOptions { get; } = new[]
        {
            new EphemeralOption { Seconds = 0, Label = "Désactivés" },
            new EphemeralOption { Seconds = 86400, Label = "24 heures" },
            new EphemeralOption { Seconds = 604800, Label = "7 jours" },
            new EphemeralOption { Seconds = 7776000, Label = "90 jours" }
        };
        public ICollectionView ContactsView { get; }
        public ObservableCollection<SettingsSection> SettingsSections { get; } = new ObservableCollection<SettingsSection>();
        public ICollectionView SettingsSectionsView { get; private set; }
        public ObservableCollection<ChatMessage> CurrentMessages { get; } = new ObservableCollection<ChatMessage>();
        public ObservableCollection<MetaAiDemoMessage> MetaAiMessages { get; } = new ObservableCollection<MetaAiDemoMessage>();
        private string _metaAiPrompt = "";
        public string MetaAiPrompt
        {
            get => _metaAiPrompt;
            set => Set(ref _metaAiPrompt, value ?? "");
        }
        private string _metaAiProvider = GeminiApiKeyStore.Provider;
        public string MetaAiProvider
        {
            get => _metaAiProvider;
            set
            {
                var provider = value == "Gemini" ? "Gemini" : "OpenRouter";
                if (Set(ref _metaAiProvider, provider))
                {
                    GeminiApiKeyStore.SaveProvider(provider);
                    MetaAiStatusText = GeminiApiKeyStore.HasKey
                        ? "Clé enregistrée. Envoie une question pour tester " + provider + "."
                        : "Ajoute une clé " + provider + " pour activer les réponses en ligne.";
                }
            }
        }
        private string _metaAiStatusText = GeminiApiKeyStore.HasKey
            ? "Clé enregistrée. Envoie une question pour tester le fournisseur sélectionné."
            : "Mode hors ligne. Choisis un fournisseur puis ajoute sa clé API.";
        public string MetaAiStatusText
        {
            get => _metaAiStatusText;
            private set => Set(ref _metaAiStatusText, value ?? "");
        }
        public ObservableCollection<StatusGroup> RecentStatuses { get; } = new ObservableCollection<StatusGroup>();
        public string[] StatusColors { get; } =
            new[] { "#635BDB", "#278B84", "#3579B7", "#8A5AD2", "#C25070", "#C87A32", "#394567", "#A14EA2" };

        public List<EmojiCategory> EmojiCategories { get; } = new List<EmojiCategory>();
        public string[] StickerChoices { get; } = { "😂", "🥰", "😎", "🤔", "😭", "🔥", "👏", "❤️", "🎉", "👍", "🙏", "🤝" };

        // ---------- Commandes Chat ----------
        public ICommand ShowChatsCommand { get; }
        public ICommand ShowStatusCommand { get; }
        public ICommand ShowArchivesCommand { get; }
        public ICommand ShowCommunitiesCommand { get; }
        public ICommand ShowSettingsCommand { get; }
        public ICommand SelectSettingsSectionCommand { get; }
        public ICommand SwitchAccountCommand { get; }
        public ICommand ShowMetaAiCommand { get; }
        public ICommand SendMetaAiCommand { get; }
        public ICommand RemoveGeminiApiKeyCommand { get; }
        public ICommand ToggleArchiveCommand { get; }
        public ICommand SelectCommunityCommand { get; }
        public ICommand OpenCommunityComposerCommand { get; }
        public ICommand CancelCommunityComposerCommand { get; }
        public ICommand CreateCommunityCommand { get; }
        public ICommand SendCommunityMessageCommand { get; }
        public ICommand SendCommand { get; }
        public ICommand PickChatPhotoCommand { get; }
        public ICommand PickChatFileCommand { get; }
        public ICommand SaveAttachmentCommand { get; }
        public ICommand ToggleEmojiPickerCommand { get; }
        public ICommand SelectEmojiCommand { get; }
        public ICommand ToggleStickerModeCommand { get; }
        public ICommand SelectStickerCommand { get; }
        public ICommand ToggleViewOnceCommand { get; }
        public ICommand ToggleEphemeralPickerCommand { get; }
        public ICommand SetEphemeralDurationCommand { get; }
        public ICommand OpenViewOnceCommand { get; }
        public ICommand CloseViewOnceModalCommand { get; }
        public ICommand OpenPhotoPreviewCommand { get; }
        public ICommand ClosePhotoPreviewCommand { get; }

        // ---------- Commandes Voice ----------
        public ICommand StartVoiceRecordCommand { get; }
        public ICommand CancelVoiceRecordCommand { get; }
        public ICommand SendVoiceRecordCommand { get; }
        public ICommand TogglePlayVoiceCommand { get; }

        // ---------- Commandes Appels ----------
        public ICommand StartAudioCallCommand { get; }
        public ICommand StartVideoCallCommand { get; }
        public ICommand AcceptCallCommand { get; }
        public ICommand RejectCallCommand { get; }
        public ICommand EndCallCommand { get; }
        public ICommand ToggleMicMuteCommand { get; }
        public ICommand ToggleCameraCommand { get; }

        // ---------- Commandes Statut ----------
        public ICommand StartComposeCommand { get; }
        public ICommand CancelComposeCommand { get; }
        public ICommand PublishStatusCommand { get; }
        public ICommand SetStatusColorCommand { get; }
        public ICommand OpenMyStatusCommand { get; }
        public ICommand OpenStatusGroupCommand { get; }
        public ICommand OpenStatusMenuCommand { get; }
        public ICommand OpenMoreMenuCommand { get; }
        public ICommand SaveSettingsCommand { get; }
        public ICommand SetThemeColorCommand { get; }
        public ICommand ChooseProfilePhotoCommand { get; }
        public ICommand RemoveProfilePhotoCommand { get; }
        public ICommand PickPhotoCommand { get; }
        public ICommand CloseViewerCommand { get; }
        public ICommand NextCommand { get; }
        public ICommand PrevCommand { get; }
        public ICommand TogglePauseCommand { get; }
        public ICommand ToggleMuteCommand { get; }
        public ICommand DeleteStatusCommand { get; }
        public ICommand ReplyPrivateCommand { get; }
        public ICommand SendReplyCommand { get; }
        public ICommand ReactCommand { get; }

        // ---------- Infos utilisateur ----------
        public string MyName => _store.UserName;
        public string MyInitial => Palette.InitialOf(_store.UserName);
        private ImageSource _profilePhoto;
        public ImageSource ProfilePhoto
        {
            get => _profilePhoto;
            private set => Set(ref _profilePhoto, value);
        }
        public string WindowTitle => "WhatsApp";
        public string OnlineText => AppPreferences.LanguageCode == "en"
            ? Contacts.Count(c => c.IsOnline) + " online"
            : Contacts.Count(c => c.IsOnline) + " en ligne";
        public bool NoContacts => Contacts.All(c => c.IsArchived);
        public bool NoArchivedContacts => Contacts.All(c => !c.IsArchived);
        public bool NoCommunities => Communities.Count == 0;
        public bool NoRecents => RecentStatuses.Count == 0;
        public int TotalUnread => Contacts.Sum(c => c.Unread);
        public bool HasUnread => TotalUnread > 0;

        // ---------- Onglets / états d'affichage ----------
        private int _activeTab;
        public int ActiveTab
        {
            get => _activeTab;
            set
            {
                if (Set(ref _activeTab, value))
                {
                    ContactsView?.Refresh();
                    RaiseViewState();
                }
            }
        }

        public bool IsChatsTab => _activeTab == 0;
        public bool IsStatusTab => _activeTab == 1;
        public bool IsArchivesTab => _activeTab == 2;
        public bool IsCommunitiesTab => _activeTab == 3;
        public bool IsSettingsTab => _activeTab == 4;
        private string _selectedSettingsSection = "Profile";
        public string SelectedSettingsSection
        {
            get => _selectedSettingsSection;
            set
            {
                if (Set(ref _selectedSettingsSection, value ?? "Profile"))
                {
                    OnPropertyChanged(nameof(IsSettingsProfileSection));
                    OnPropertyChanged(nameof(IsSettingsGeneralSection));
                    OnPropertyChanged(nameof(IsSettingsAccountSection));
                    OnPropertyChanged(nameof(IsSettingsPrivacySection));
                    OnPropertyChanged(nameof(IsSettingsChatsSection));
                    OnPropertyChanged(nameof(IsSettingsVideoAudioSection));
                    OnPropertyChanged(nameof(IsSettingsNotificationsSection));
                    OnPropertyChanged(nameof(IsSettingsShortcutsSection));
                    OnPropertyChanged(nameof(IsSettingsHelpSection));
                }
            }
        }
        public bool IsSettingsProfileSection => SelectedSettingsSection == "Profile";
        public bool IsSettingsGeneralSection => SelectedSettingsSection == "General";
        public bool IsSettingsAccountSection => SelectedSettingsSection == "Account";
        public bool IsSettingsPrivacySection => SelectedSettingsSection == "Privacy";
        public bool IsSettingsChatsSection => SelectedSettingsSection == "Chats";
        public bool IsSettingsVideoAudioSection => SelectedSettingsSection == "VideoAudio";
        public bool IsSettingsNotificationsSection => SelectedSettingsSection == "Notifications";
        public bool IsSettingsShortcutsSection => SelectedSettingsSection == "Shortcuts";
        public bool IsSettingsHelpSection => SelectedSettingsSection == "Help";
        private string _settingsSearchText = "";
        public string SettingsSearchText
        {
            get => _settingsSearchText;
            set { if (Set(ref _settingsSearchText, value ?? "")) SettingsSectionsView?.Refresh(); }
        }
        public bool IsMetaAiTab => _activeTab == 5;
        public bool ShowWelcome => (IsChatsTab && (SelectedContact == null || SelectedContact.IsArchived)) || (IsArchivesTab && SelectedContact == null);
        public bool ShowChat => (IsChatsTab && SelectedContact != null && !SelectedContact.IsArchived) || (IsArchivesTab && SelectedContact != null && SelectedContact.IsArchived);
        public bool ShowStatusHome => IsStatusTab && !IsComposing;
        public bool ShowStatusCompose => IsStatusTab && IsComposing;
        public bool ShowCommunityHome => IsCommunitiesTab && SelectedCommunity == null;
        public bool ShowCommunityChat => IsCommunitiesTab && SelectedCommunity != null;
        public bool IsArchiveAction => SelectedContact?.IsArchived ?? false;
        public string ArchiveActionText => IsArchiveAction ? "Désarchiver" : "Archiver";

        private void RaiseViewState()
        {
            OnPropertyChanged(nameof(IsChatsTab));
            OnPropertyChanged(nameof(IsStatusTab));
            OnPropertyChanged(nameof(IsArchivesTab));
            OnPropertyChanged(nameof(IsCommunitiesTab));
            OnPropertyChanged(nameof(IsSettingsTab));
            OnPropertyChanged(nameof(IsMetaAiTab));
            OnPropertyChanged(nameof(ShowWelcome));
            OnPropertyChanged(nameof(ShowChat));
            OnPropertyChanged(nameof(ShowStatusHome));
            OnPropertyChanged(nameof(ShowStatusCompose));
            OnPropertyChanged(nameof(ShowCommunityHome));
            OnPropertyChanged(nameof(ShowCommunityChat));
        }

        // ---------- Recherche / filtres ----------
        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set { if (Set(ref _searchText, value ?? "")) ContactsView?.Refresh(); }
        }

        private bool _filterUnread;
        public bool FilterAll
        {
            get => !_filterUnread;
            set { if (value && _filterUnread) { _filterUnread = false; OnFilterChanged(); } }
        }
        public bool FilterUnread
        {
            get => _filterUnread;
            set { if (value && !_filterUnread) { _filterUnread = true; OnFilterChanged(); } }
        }

        private void OnFilterChanged()
        {
            OnPropertyChanged(nameof(FilterAll));
            OnPropertyChanged(nameof(FilterUnread));
            ContactsView?.Refresh();
        }

        private bool FilterContact(object o)
        {
            var c = (Contact)o;
            if (ActiveTab == 2 ? !c.IsArchived : c.IsArchived) return false;
            if (FilterUnread && c.Unread == 0) return false;
            if (!string.IsNullOrWhiteSpace(SearchText) &&
                c.Name.IndexOf(SearchText, StringComparison.CurrentCultureIgnoreCase) < 0) return false;
            return true;
        }

        private bool FilterSettingsSection(object o)
        {
            if (!(o is SettingsSection section)) return false;
            if (string.IsNullOrWhiteSpace(SettingsSearchText)) return true;
            return section.Title.IndexOf(SettingsSearchText, StringComparison.CurrentCultureIgnoreCase) >= 0
                || section.Subtitle.IndexOf(SettingsSearchText, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private Community _selectedCommunity;
        public Community SelectedCommunity
        {
            get => _selectedCommunity;
            set
            {
                if (Set(ref _selectedCommunity, value))
                {
                    if (value != null && value.UnreadCount > 0)
                    {
                        value.UnreadCount = 0;
                        Save();
                    }
                    LoadCommunityMessages(value);
                    OnPropertyChanged(nameof(ShowCommunityHome));
                    OnPropertyChanged(nameof(ShowCommunityChat));
                }
            }
        }

        public ObservableCollection<ChatMessage> CurrentCommunityMessages { get; } = new ObservableCollection<ChatMessage>();
        private string _communityMessageText = "";
        public string CommunityMessageText
        {
            get => _communityMessageText;
            set => Set(ref _communityMessageText, value ?? "");
        }

        private string _communityNameDraft = "";
        public string CommunityNameDraft
        {
            get => _communityNameDraft;
            set => Set(ref _communityNameDraft, value ?? "");
        }

        private bool _isCommunityComposerOpen;
        public bool IsCommunityComposerOpen
        {
            get => _isCommunityComposerOpen;
            set => Set(ref _isCommunityComposerOpen, value);
        }

        private bool _notificationSoundsEnabled = true;
        public bool NotificationSoundsEnabled
        {
            get => _notificationSoundsEnabled;
            set { if (Set(ref _notificationSoundsEnabled, value) && _readyForPersistence) Save(); }
        }

        public string ProfileSummary => AppPreferences.LanguageCode == "en" ? "Local profile: " + _store.UserName : "Profil local : " + _store.UserName;
        public string NetworkSummary => _net == null
            ? (AppPreferences.LanguageCode == "en" ? "Local network is starting" : "Réseau local en attente")
            : (AppPreferences.LanguageCode == "en" ? "Listening port: " : "Port de réception : ") + _net.TcpPort;

        private string _themeColor = "Green";
        public string ThemeColor
        {
            get => _themeColor;
            set
            {
                if (Set(ref _themeColor, value ?? "Green"))
                {
                    AppPreferences.SetTheme(_themeColor);
                    if (_readyForPersistence) Save();
                }
            }
        }
        public string[] ThemeColors { get; } = new[] { "Indigo", "Green", "Blue", "Coral" };

        private string _language = "fr";
        public string Language
        {
            get => _language;
            set
            {
                if (Set(ref _language, value == "en" ? "en" : "fr"))
                {
                    AppPreferences.SetLanguage(_language);
                    OnPropertyChanged(nameof(OnlineText));
                    OnPropertyChanged(nameof(ProfileSummary));
                    OnPropertyChanged(nameof(NetworkSummary));
                    foreach (var community in Communities) community.RefreshLocalizedText();
                    if (_readyForPersistence) Save();
                }
            }
        }

        // ---------- Discussion ----------
        private Contact _selectedContact;
        public Contact SelectedContact
        {
            get => _selectedContact;
            set
            {
                if (Set(ref _selectedContact, value))
                {
                    if (value != null)
                    {
                        value.Unread = 0;
                        LoadMessages(value);
                        FocusRequested?.Invoke();
                    }
                    OnPropertyChanged(nameof(IsArchiveAction));
                    OnPropertyChanged(nameof(ArchiveActionText));
                    OnPropertyChanged(nameof(EphemeralDurationLabel));
                    RaiseViewState();
                }
            }
        }

        private string _messageText = "";
        public string MessageText
        {
            get => _messageText;
            set => Set(ref _messageText, value ?? "");
        }

        private bool _sendViewOnce;
        public bool SendViewOnce { get => _sendViewOnce; set => Set(ref _sendViewOnce, value); }

        // ---------- Emojis ----------
        private bool _isEmojiPickerOpen;
        public bool IsEmojiPickerOpen
        {
            get => _isEmojiPickerOpen;
            set => Set(ref _isEmojiPickerOpen, value);
        }

        private bool _isStickerMode;
        public bool IsStickerMode { get => _isStickerMode; set => Set(ref _isStickerMode, value); }

        private bool _isEphemeralPickerOpen;
        public bool IsEphemeralPickerOpen { get => _isEphemeralPickerOpen; set => Set(ref _isEphemeralPickerOpen, value); }

        public string EphemeralDurationLabel
        {
            get
            {
                int seconds = SelectedContact?.EphemeralSeconds ?? 0;
                return seconds switch { 86400 => "24 h", 604800 => "7 j", 7776000 => "90 j", _ => "Off" };
            }
        }

        // ---------- Voice Recording ----------
        private bool _isRecordingVoice;
        public bool IsRecordingVoice
        {
            get => _isRecordingVoice;
            set => Set(ref _isRecordingVoice, value);
        }

        private string _recordingTimeText = "0:00";
        public string RecordingTimeText
        {
            get => _recordingTimeText;
            set => Set(ref _recordingTimeText, value);
        }

        // ---------- Appels Audio / Vidéo Média ----------
        private bool _isInCall;
        public bool IsInCall
        {
            get => _isInCall;
            set => Set(ref _isInCall, value);
        }

        private bool _isIncomingCall;
        public bool IsIncomingCall
        {
            get => _isIncomingCall;
            set => Set(ref _isIncomingCall, value);
        }

        private bool _isCallConnected;
        public bool IsCallConnected
        {
            get => _isCallConnected;
            set => Set(ref _isCallConnected, value);
        }

        private string _callType = "audio";
        public string CallType
        {
            get => _callType;
            set
            {
                if (Set(ref _callType, value))
                {
                    OnPropertyChanged(nameof(IsVideoCall));
                    OnPropertyChanged(nameof(IsAudioCall));
                }
            }
        }
        public bool IsVideoCall => _callType == "video";
        public bool IsAudioCall => _callType != "video";

        private string _callContactName = "";
        public string CallContactName
        {
            get => _callContactName;
            set => Set(ref _callContactName, value);
        }

        private string _callStatusText = "Appel en cours...";
        public string CallStatusText
        {
            get => _callStatusText;
            set => Set(ref _callStatusText, value);
        }

        private string _callDurationText = "00:00";
        public string CallDurationText
        {
            get => _callDurationText;
            set => Set(ref _callDurationText, value);
        }

        private bool _isMicMuted;
        public bool IsMicMuted
        {
            get => _isMicMuted;
            set => Set(ref _isMicMuted, value);
        }

        private bool _isCameraOff;
        public bool IsCameraOff
        {
            get => _isCameraOff;
            set => Set(ref _isCameraOff, value);
        }

        public string CallContactInitial => Palette.InitialOf(CallContactName);
        public string CallContactAvatarBg => Palette.For(CallContactName).Bg;
        public string CallContactAvatarFg => Palette.For(CallContactName).Fg;

        // Vraies images vidéo et ondes audio
        private ImageSource _localVideoImage;
        public ImageSource LocalVideoImage
        {
            get => _localVideoImage;
            set => Set(ref _localVideoImage, value);
        }

        private ImageSource _remoteVideoImage;
        public ImageSource RemoteVideoImage
        {
            get => _remoteVideoImage;
            set
            {
                if (Set(ref _remoteVideoImage, value))
                    OnPropertyChanged(nameof(HasRemoteVideo));
            }
        }
        public bool HasRemoteVideo => _remoteVideoImage != null;

        private double _localAudioLevel;
        public double LocalAudioLevel
        {
            get => _localAudioLevel;
            set => Set(ref _localAudioLevel, value);
        }

        private double _remoteAudioLevel;
        public double RemoteAudioLevel
        {
            get => _remoteAudioLevel;
            set
            {
                if (Set(ref _remoteAudioLevel, value))
                    AudioWaveScale = 1.0 + (value * 0.4);
            }
        }

        private double _audioWaveScale = 1.0;
        public double AudioWaveScale
        {
            get => _audioWaveScale;
            set => Set(ref _audioWaveScale, value);
        }

        // ---------- Photo Preview (Lightbox) ----------
        private bool _isPhotoViewerOpen;
        public bool IsPhotoViewerOpen
        {
            get => _isPhotoViewerOpen;
            set => Set(ref _isPhotoViewerOpen, value);
        }

        private ImageSource _previewImage;
        public ImageSource PreviewImage
        {
            get => _previewImage;
            set => Set(ref _previewImage, value);
        }

        // ---------- View Once Modal ----------
        private bool _isViewOnceModalOpen;
        public bool IsViewOnceModalOpen
        {
            get => _isViewOnceModalOpen;
            set => Set(ref _isViewOnceModalOpen, value);
        }

        private ImageSource _viewOnceImage;
        public ImageSource ViewOnceImage
        {
            get => _viewOnceImage;
            set
            {
                if (Set(ref _viewOnceImage, value))
                    OnPropertyChanged(nameof(HasViewOnceImage));
            }
        }
        public bool HasViewOnceImage => _viewOnceImage != null;

        private string _viewOnceText = "";
        public string ViewOnceText
        {
            get => _viewOnceText;
            set
            {
                if (Set(ref _viewOnceText, value))
                    OnPropertyChanged(nameof(HasViewOnceText));
            }
        }
        public bool HasViewOnceText => !string.IsNullOrEmpty(_viewOnceText);

        private string _viewOnceCountdownText = "10s";
        public string ViewOnceCountdownText
        {
            get => _viewOnceCountdownText;
            set => Set(ref _viewOnceCountdownText, value);
        }

        // ---------- Statuts ----------
        private StatusGroup _myStatusGroup;
        public StatusGroup MyStatusGroup
        {
            get => _myStatusGroup;
            private set => Set(ref _myStatusGroup, value);
        }

        public bool HasUnseenStatuses => RecentStatuses.Any(g => g.HasUnseen);

        private bool _isComposing;
        public bool IsComposing
        {
            get => _isComposing;
            set { if (Set(ref _isComposing, value)) RaiseViewState(); }
        }

        private string _statusText = "";
        public string StatusText
        {
            get => _statusText;
            set => Set(ref _statusText, value ?? "");
        }

        private string _statusColor = "#635BDB";
        public string StatusColor
        {
            get => _statusColor;
            set => Set(ref _statusColor, value);
        }

        private bool _isStatusMenuOpen;
        public bool IsStatusMenuOpen
        {
            get => _isStatusMenuOpen;
            set => Set(ref _isStatusMenuOpen, value);
        }

        private bool _isMoreMenuOpen;
        public bool IsMoreMenuOpen
        {
            get => _isMoreMenuOpen;
            set => Set(ref _isMoreMenuOpen, value);
        }

        private string _toastText;
        public string ToastText
        {
            get => _toastText;
            set { if (Set(ref _toastText, value)) OnPropertyChanged(nameof(HasToast)); }
        }
        public bool HasToast => !string.IsNullOrEmpty(ToastText);

        private readonly DispatcherTimer _toastTimer;

        private void ShowToast(string text)
        {
            ToastText = text;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        // ---------- Visionneur de statut ----------
        private const double StoryDurationMs = 5000;
        private readonly DispatcherTimer _viewerTimer;
        private double _progress;
        private int _viewerIndex;

        public ObservableCollection<SegmentVM> Segments { get; } = new ObservableCollection<SegmentVM>();

        private bool _isViewerOpen;
        public bool IsViewerOpen
        {
            get => _isViewerOpen;
            private set => Set(ref _isViewerOpen, value);
        }

        private StatusGroup _viewerGroup;
        public StatusGroup ViewerGroup
        {
            get => _viewerGroup;
            private set
            {
                if (Set(ref _viewerGroup, value))
                {
                    OnPropertyChanged(nameof(ViewerIsMine));
                    OnPropertyChanged(nameof(ViewerIsOther));
                    OnPropertyChanged(nameof(ViewerName));
                    OnPropertyChanged(nameof(CurrentPost));
                }
            }
        }

        public StatusPost CurrentPost =>
            (ViewerGroup != null && _viewerIndex >= 0 && _viewerIndex < ViewerGroup.Posts.Count)
                ? ViewerGroup.Posts[_viewerIndex] : null;

        public bool ViewerIsMine => ViewerGroup != null && ViewerGroup.AuthorId == _store.UserId;
        public bool ViewerIsOther => ViewerGroup != null && !ViewerIsMine;
        public string ViewerName => ViewerIsMine ? "Vous" : ViewerGroup?.AuthorName;
        public bool HasPrev => ViewerGroup != null && (_viewerIndex > 0 || NeighbourGroup(-1) != null);
        public bool HasNext => ViewerGroup != null && (_viewerIndex < ViewerGroup.Posts.Count - 1 || NeighbourGroup(+1) != null);

        private bool _isPaused;
        public bool IsPaused
        {
            get => _isPaused;
            set { if (Set(ref _isPaused, value)) OnPropertyChanged(nameof(IsPlaying)); }
        }
        public bool IsPlaying => !_isPaused;

        private bool _isMuted = true;
        public bool IsMuted
        {
            get => _isMuted;
            set { if (Set(ref _isMuted, value)) OnPropertyChanged(nameof(IsNotMuted)); }
        }
        public bool IsNotMuted => !_isMuted;

        private string _replyText = "";
        public string ReplyText
        {
            get => _replyText;
            set => Set(ref _replyText, value ?? "");
        }

        // =====================================================================

        public MainViewModel(string name)
        {
            _store = new DataStore(name);
            var data = _store.Load();
            AppPreferences.InitializeFromLegacy(data.ThemeColor ?? "Green", data.Language ?? "fr");
            _profilePhotoBytes = _store.LoadProfilePhoto();
            ProfilePhoto = LoadProfilePhotoImage(_profilePhotoBytes);
            _messages = data.Messages ?? new List<ChatMessage>();
            _statuses = data.Statuses ?? new List<StatusPost>();
            _communities = data.Communities ?? new List<CommunityData>();
            NotificationSoundsEnabled = data.NotificationSoundsEnabled;
            ThemeColor = AppPreferences.ThemeColor;
            Language = AppPreferences.LanguageCode;

            InitEmojiCategories();
            MetaAiMessages.Add(new MetaAiDemoMessage
            {
                IsFromUser = false,
                Text = "Salut ! Pose-moi une question. Avec une clé Gemini, je réponds grâce au modèle en ligne ; sans clé, le mode de démonstration hors ligne reste disponible. Évite de partager des informations confidentielles.",
                Time = DateTime.Now.ToString("HH:mm")
            });

            foreach (var cd in data.Contacts ?? new List<ContactData>())
                AddContact(new Contact { Id = cd.Id, Name = cd.Name, IsArchived = cd.IsArchived, ProfilePhotoData = cd.ProfilePhotoData, EphemeralSeconds = cd.EphemeralSeconds });

            foreach (var community in _communities)
                Communities.Add(new Community
                {
                    Id = community.Id,
                    Name = community.Name,
                    MemberIds = community.MemberIds ?? new List<string>(),
                    UnreadCount = community.UnreadCount
                });

            foreach (var c in Contacts)
            {
                var last = _messages.Where(m => m.ContactId == c.Id).OrderBy(m => m.Time).LastOrDefault();
                if (last != null) UpdatePreview(c, last);
            }

            ContactsView = CollectionViewSource.GetDefaultView(Contacts);
            ContactsView.SortDescriptions.Add(new SortDescription(nameof(Contact.SortKey), ListSortDirection.Descending));
            ContactsView.SortDescriptions.Add(new SortDescription(nameof(Contact.Name), ListSortDirection.Ascending));
            ContactsView.Filter = FilterContact;

            SettingsSections.Add(new SettingsSection { Key = "General", Icon = "\uE713", Title = "Général", Subtitle = "Démarrer et fermer" });
            SettingsSections.Add(new SettingsSection { Key = "Profile", Icon = "\uE77B", Title = "Profil", Subtitle = "Nom, photo de profil et humeur" });
            SettingsSections.Add(new SettingsSection { Key = "Account", Icon = "\uE8D7", Title = "Compte", Subtitle = "Changer de compte et informations" });
            SettingsSections.Add(new SettingsSection { Key = "Privacy", Icon = "\uE72E", Title = "Confidentialité", Subtitle = "Contacts bloqués, messages éphémères" });
            SettingsSections.Add(new SettingsSection { Key = "Chats", Icon = "\uE8BD", Title = "Discussions", Subtitle = "Thème, langue et paramètres des discussions" });
            SettingsSections.Add(new SettingsSection { Key = "VideoAudio", Icon = "\uE714", Title = "Vidéo et audio", Subtitle = "Caméra, micro et appels" });
            SettingsSections.Add(new SettingsSection { Key = "Notifications", Icon = "\uE91C", Title = "Notifications", Subtitle = "Messages, groupes et sons" });
            SettingsSections.Add(new SettingsSection { Key = "Shortcuts", Icon = "\uE765", Title = "Raccourcis clavier", Subtitle = "Actions rapides" });
            SettingsSections.Add(new SettingsSection { Key = "Help", Icon = "\uE897", Title = "Aide et commentaires", Subtitle = "Aide, réseau et commentaires" });
            SettingsSectionsView = CollectionViewSource.GetDefaultView(SettingsSections);
            SettingsSectionsView.Filter = FilterSettingsSection;

            Contacts.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(NoContacts));
                OnPropertyChanged(nameof(NoArchivedContacts));
                OnPropertyChanged(nameof(OnlineText));
            };
            Communities.CollectionChanged += (s, e) => OnPropertyChanged(nameof(NoCommunities));
            RecentStatuses.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(NoRecents));
                OnPropertyChanged(nameof(HasUnseenStatuses));
            };

            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _toastTimer.Tick += (s, e) => { _toastTimer.Stop(); ToastText = null; };

            _viewerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _viewerTimer.Tick += (s, e) => OnViewerTick();

            // Audio Player & Voice Timer
            _audioPlayer = new AudioPlayer();
            _voiceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _voiceTimer.Tick += (s, e) =>
            {
                _recordingSeconds++;
                int min = _recordingSeconds / 60;
                int sec = _recordingSeconds % 60;
                RecordingTimeText = $"{min}:{sec:00}";
            };

            // Call Timers
            _callTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _callTimer.Tick += (s, e) =>
            {
                _callSeconds++;
                int min = _callSeconds / 60;
                int sec = _callSeconds % 60;
                CallDurationText = $"{min:00}:{sec:00}";
                CallStatusText = AppPreferences.Text($"En communication ({CallDurationText})", $"Connected ({CallDurationText})");
            };

            // View Once Countdown Timer
            _viewOnceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _viewOnceTimer.Tick += (s, e) =>
            {
                _viewOnceCountdown--;
                ViewOnceCountdownText = $"{_viewOnceCountdown}s";
                if (_viewOnceCountdown <= 0)
                {
                    CloseViewOnceModal();
                }
            };

            RebuildStatusGroups();

            // Initialiser les commandes
            ShowChatsCommand = new RelayCommand(_ => ActiveTab = 0);
            ShowStatusCommand = new RelayCommand(_ => ActiveTab = 1);
            ShowArchivesCommand = new RelayCommand(_ => ActiveTab = 2);
            ShowCommunitiesCommand = new RelayCommand(_ => ActiveTab = 3);
            ShowSettingsCommand = new RelayCommand(_ => { if (!IsSettingsTab) SelectedSettingsSection = "Profile"; ActiveTab = 4; });
            SelectSettingsSectionCommand = new RelayCommand(p => SelectedSettingsSection = p as string ?? "Profile");
            ShowMetaAiCommand = new RelayCommand(_ => ActiveTab = 5);
            SendMetaAiCommand = new RelayCommand(_ => SendMetaAiDemoMessage());
            RemoveGeminiApiKeyCommand = new RelayCommand(_ => RemoveGeminiApiKey());
            SwitchAccountCommand = new RelayCommand(_ => SwitchAccountRequested?.Invoke());
            ToggleArchiveCommand = new RelayCommand(_ => ToggleArchiveSelectedContact(), _ => SelectedContact != null);
            SelectCommunityCommand = new RelayCommand(p => SelectedCommunity = p as Community);
            OpenCommunityComposerCommand = new RelayCommand(_ => OpenCommunityComposer());
            CancelCommunityComposerCommand = new RelayCommand(_ => IsCommunityComposerOpen = false);
            CreateCommunityCommand = new RelayCommand(_ => CreateCommunity());
            SendCommunityMessageCommand = new RelayCommand(_ => SendCommunityMessage());
            SendCommand = new RelayCommand(_ => Send());
            PickChatPhotoCommand = new RelayCommand(_ => PickChatPhoto());
            PickChatFileCommand = new RelayCommand(_ => PickChatFile());
            SaveAttachmentCommand = new RelayCommand(p => SaveAttachment(p as ChatMessage));
            ToggleEmojiPickerCommand = new RelayCommand(_ => IsEmojiPickerOpen = !IsEmojiPickerOpen);
            SelectEmojiCommand = new RelayCommand(p => InsertEmoji(p as string));
            ToggleStickerModeCommand = new RelayCommand(_ => IsStickerMode = !IsStickerMode);
            SelectStickerCommand = new RelayCommand(p => SendSticker(p as string));
            ToggleViewOnceCommand = new RelayCommand(_ => SendViewOnce = !SendViewOnce);
            ToggleEphemeralPickerCommand = new RelayCommand(_ => IsEphemeralPickerOpen = !IsEphemeralPickerOpen);
            SetEphemeralDurationCommand = new RelayCommand(p => SetEphemeralDuration(p));
            OpenViewOnceCommand = new RelayCommand(p => OpenViewOnce(p as ChatMessage));
            CloseViewOnceModalCommand = new RelayCommand(_ => CloseViewOnceModal());
            OpenPhotoPreviewCommand = new RelayCommand(p => OpenPhotoPreview(p as ChatMessage));
            ClosePhotoPreviewCommand = new RelayCommand(_ => IsPhotoViewerOpen = false);

            // Voice Commands
            StartVoiceRecordCommand = new RelayCommand(_ => StartRecordingVoice());
            CancelVoiceRecordCommand = new RelayCommand(_ => CancelRecordingVoice());
            SendVoiceRecordCommand = new RelayCommand(_ => SendRecordingVoice());
            TogglePlayVoiceCommand = new RelayCommand(p => TogglePlayVoice(p as ChatMessage));

            // Call Commands
            StartAudioCallCommand = new RelayCommand(_ => StartCall(SelectedContact, "audio"));
            StartVideoCallCommand = new RelayCommand(_ => StartCall(SelectedContact, "video"));
            AcceptCallCommand = new RelayCommand(_ => AcceptIncomingCall());
            RejectCallCommand = new RelayCommand(_ => RejectIncomingCall());
            EndCallCommand = new RelayCommand(_ => EndCurrentCall());
            ToggleMicMuteCommand = new RelayCommand(_ => ToggleMicMute());
            ToggleCameraCommand = new RelayCommand(_ => ToggleCamera());

            // Status Commands
            StartComposeCommand = new RelayCommand(_ => { IsStatusMenuOpen = false; StatusText = ""; IsComposing = true; });
            CancelComposeCommand = new RelayCommand(_ => IsComposing = false);
            PublishStatusCommand = new RelayCommand(_ => PublishStatus());
            SetStatusColorCommand = new RelayCommand(p => StatusColor = (string)p);
            OpenStatusMenuCommand = new RelayCommand(_ => IsStatusMenuOpen = true);
            OpenMoreMenuCommand = new RelayCommand(_ => IsMoreMenuOpen = true);
            SaveSettingsCommand = new RelayCommand(_ =>
            {
                if (Save()) ShowToast(AppPreferences.Text("Paramètres enregistrés.", "Settings saved."));
            });
            SetThemeColorCommand = new RelayCommand(p => ThemeColor = p as string ?? "Green");
            ChooseProfilePhotoCommand = new RelayCommand(_ => ChooseProfilePhoto());
            RemoveProfilePhotoCommand = new RelayCommand(_ => RemoveProfilePhoto());
            PickPhotoCommand = new RelayCommand(_ => PickPhoto());
            OpenMyStatusCommand = new RelayCommand(_ =>
            {
                if (MyStatusGroup.Posts.Count == 0) IsStatusMenuOpen = true;
                else OpenViewer(MyStatusGroup, 0);
            });
            OpenStatusGroupCommand = new RelayCommand(p =>
            {
                var g = p as StatusGroup;
                if (g != null) OpenViewer(g, FirstUnseenIndex(g));
            });
            CloseViewerCommand = new RelayCommand(_ => CloseViewer());
            NextCommand = new RelayCommand(_ => Next());
            PrevCommand = new RelayCommand(_ => Prev());
            TogglePauseCommand = new RelayCommand(_ => IsPaused = !IsPaused);
            ToggleMuteCommand = new RelayCommand(_ => IsMuted = !IsMuted);
            DeleteStatusCommand = new RelayCommand(_ => DeleteCurrentStatus());
            ReplyPrivateCommand = new RelayCommand(_ => ReplyPrivate());
            SendReplyCommand = new RelayCommand(_ => SendStatusReply(ReplyText));
            ReactCommand = new RelayCommand(p => SendStatusReply(p as string));

            // Réseau
            _net = new NetworkService(_store.UserId, _store.UserName);
            _net.PeerSeen += (p, ip) => _ui.BeginInvoke(new Action(() => { try { OnPeer(p, ip); } catch { } }));
            _net.PacketReceived += (p, ip) => _ui.BeginInvoke(new Action(() => { try { OnPacket(p, ip); } catch { } }));
            try { _net.Start(); }
            catch (Exception ex) { MessageBox.Show(AppPreferences.Text("Impossible de démarrer le réseau : ", "Could not start the network: ") + ex.Message); }
            OnPropertyChanged(nameof(NetworkSummary));

            // Détection hors ligne + expiration statuts
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += (s, e) =>
            {
                var now = DateTime.UtcNow;
                foreach (var c in Contacts)
                    if (c.IsOnline && (now - c.LastSeen).TotalSeconds > 10) c.IsOnline = false;

                var expiredMessages = _messages.Where(m => m.EphemeralSeconds > 0 && m.Time.AddSeconds(m.EphemeralSeconds) <= now).ToList();
                if (expiredMessages.Count > 0)
                {
                    foreach (var expired in expiredMessages)
                    {
                        _messages.Remove(expired);
                        if (SelectedContact?.Id == expired.ContactId) CurrentMessages.Remove(expired);
                    }
                    foreach (var contact in Contacts) UpdatePreview(contact, _messages.Where(m => m.ContactId == contact.Id).OrderBy(m => m.Time).LastOrDefault());
                    ContactsView.Refresh();
                    Save();
                }

                if (++_tick % 30 == 0 && _statuses.Any(x => x.Time < now.AddHours(-24)))
                {
                    RebuildStatusGroups();
                    Save();
                }
            };
            _timer.Start();
            _readyForPersistence = true;
            AppPreferences.Changed += OnAppPreferencesChanged;
        }

        private void InitEmojiCategories()
        {
            EmojiCategories.Add(new EmojiCategory
            {
                Name = "Sourires",
                Icon = "😊",
                Emojis = new List<string> {
                    "😀", "😃", "😄", "😁", "😆", "😅", "😂", "🤣", "🙂", "🙃",
                    "😉", "😊", "😇", "🥰", "😍", "🤩", "😘", "😗", "😚", "😙",
                    "😋", "😛", "😜", "🤪", "😝", "🤑", "🤗", "🤭", "🤫", "🤔",
                    "🤐", "🤨", "😐", "😑", "😶", "😏", "😒", "🙄", "😬", "🤥",
                    "😌", "😔", "😪", "🤤", "😴", "😷", "🤒", "🤕", "🤢", "🤮",
                    "🤧", "🥵", "🥶", "🥴", "😵", "🤯", "🤠", "🥳", "🥺", "😎"
                }
            });
            EmojiCategories.Add(new EmojiCategory
            {
                Name = "Gestes & Mains",
                Icon = "👍",
                Emojis = new List<string> {
                    "👍", "👎", "👏", "🙌", "👐", "🤲", "🤝", "🙏", "✌️", "🤞",
                    "🤟", "🤘", "👌", "🤌", "🤏", "👈", "👉", "👆", "👇", "☝️",
                    "✋", "🤚", "🖐️", "🖖", "👋", "🤙", "💪", "👊", "✊", "🤛"
                }
            });
            EmojiCategories.Add(new EmojiCategory
            {
                Name = "Cœurs & Amour",
                Icon = "❤️",
                Emojis = new List<string> {
                    "❤️", "🧡", "💛", "💚", "💙", "💜", "🖤", "🤍", "🤎", "💔",
                    "❣️", "💕", "💞", "💓", "💗", "💖", "💘", "💝", "💟", "🔥",
                    "✨", "⭐", "🌟", "💫", "💥", "💯", "💢", "💌", "💋", "💐"
                }
            });
            EmojiCategories.Add(new EmojiCategory
            {
                Name = "Fêtes & Objets",
                Icon = "🎉",
                Emojis = new List<string> {
                    "🎉", "🎊", "🎈", "🎁", "🏆", "🥇", "🥈", "🥉", "⚽", "🏀",
                    "🍕", "🍔", "🍟", "🍦", "🎂", "🍰", "☕", "🍺", "🥂", "🍿",
                    "🚗", "🚀", "💻", "📱", "📸", "🎥", "🎵", "🎧", "🔔", "💡"
                }
            });
        }

        private void InsertEmoji(string emoji)
        {
            if (string.IsNullOrEmpty(emoji)) return;
            IsStickerMode = false;
            MessageText += emoji;
            FocusRequested?.Invoke();
        }

        private void SendSticker(string sticker)
        {
            var contact = SelectedContact;
            if (contact == null || string.IsNullOrWhiteSpace(sticker)) return;
            var msg = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"), ContactId = contact.Id, IsMine = true,
                Kind = "sticker", Text = sticker, EphemeralSeconds = contact.EphemeralSeconds,
                Time = DateTime.UtcNow, State = 0
            };
            _messages.Add(msg);
            CurrentMessages.Add(msg);
            UpdatePreview(contact, msg);
            ContactsView.Refresh();
            ScrollRequested?.Invoke();
            IsStickerMode = false;
            IsEmojiPickerOpen = false;
            Save();
            _ = DeliverAsync(contact, msg);
        }

        private void SetEphemeralDuration(object value)
        {
            if (SelectedContact == null || value == null) return;
            int seconds;
            if (value is EphemeralOption option) seconds = option.Seconds;
            else if (!int.TryParse(value.ToString(), out seconds)) return;
            SelectedContact.EphemeralSeconds = seconds;
            OnPropertyChanged(nameof(EphemeralDurationLabel));
            IsEphemeralPickerOpen = false;
        }

        // ---------- Contacts ----------

        private void AddContact(Contact c)
        {
            c.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(Contact.EphemeralSeconds) && _readyForPersistence) Save();
                if (e.PropertyName == nameof(Contact.IsOnline))
                    OnPropertyChanged(nameof(OnlineText));
                if (e.PropertyName == nameof(Contact.Unread))
                {
                    OnPropertyChanged(nameof(TotalUnread));
                    OnPropertyChanged(nameof(HasUnread));
                }
            };
            Contacts.Add(c);
        }

        private void ToggleArchiveSelectedContact()
        {
            var contact = SelectedContact;
            if (contact == null) return;

            contact.IsArchived = !contact.IsArchived;
            ActiveTab = contact.IsArchived ? 2 : 0;
            ContactsView.Refresh();
            OnPropertyChanged(nameof(NoContacts));
            OnPropertyChanged(nameof(NoArchivedContacts));
            OnPropertyChanged(nameof(IsArchiveAction));
            OnPropertyChanged(nameof(ArchiveActionText));
            Save();
            ShowToast(contact.IsArchived
                ? AppPreferences.Text($"Discussion avec {contact.Name} archivée.", $"Conversation with {contact.Name} archived.")
                : AppPreferences.Text($"Discussion avec {contact.Name} restaurée.", $"Conversation with {contact.Name} restored."));
        }

        private void OpenCommunityComposer()
        {
            CommunityNameDraft = "";
            CommunityMemberOptions.Clear();
            foreach (var contact in Contacts.OrderBy(c => c.Name))
                CommunityMemberOptions.Add(new CommunityMemberOption { Contact = contact });
            IsCommunityComposerOpen = true;
        }

        private void CreateCommunity()
        {
            var name = CommunityNameDraft.Trim();
            if (name.Length == 0)
            {
                ShowToast(AppPreferences.Text("Donne un nom à la communauté.", "Enter a community name."));
                return;
            }

            var members = CommunityMemberOptions.Where(x => x.IsSelected && x.Contact != null)
                .Select(x => x.Contact.Id).Distinct().ToList();
            if (members.Count == 0)
            {
                ShowToast(AppPreferences.Text("Sélectionne au moins un membre.", "Select at least one member."));
                return;
            }

            members.Add(_store.UserId);
            var community = new Community
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                MemberIds = members.Distinct().ToList()
            };
            Communities.Add(community);
            _communities.Add(new CommunityData { Id = community.Id, Name = community.Name, MemberIds = community.MemberIds, UnreadCount = community.UnreadCount });
            IsCommunityComposerOpen = false;
            ActiveTab = 3;
            SelectedCommunity = community;
            Save();

            foreach (var contact in Contacts.Where(c => community.MemberIds.Contains(c.Id) && c.IsOnline).ToList())
                _ = SendCommunityInviteAsync(contact, community);
        }

        private async Task SendCommunityInviteAsync(Contact contact, Community community)
        {
            if (contact == null || community == null || !contact.IsOnline) return;
            await _net.SendAsync(contact.Ip, contact.Port, new Packet
            {
                T = "cinvite",
                Id = community.Id,
                From = _store.UserId,
                FromName = _store.UserName,
                Text = community.Name,
                Data = JsonSerializer.Serialize(community.MemberIds)
            });
        }

        private void ReceiveCommunityInvite(Contact sender, Packet packet)
        {
            if (string.IsNullOrWhiteSpace(packet.Id)) return;
            List<string> memberIds;
            try { memberIds = JsonSerializer.Deserialize<List<string>>(packet.Data ?? "[]") ?? new List<string>(); }
            catch { memberIds = new List<string>(); }
            if (!memberIds.Contains(sender.Id)) memberIds.Add(sender.Id);
            if (!memberIds.Contains(_store.UserId)) memberIds.Add(_store.UserId);

            var community = Communities.FirstOrDefault(c => c.Id == packet.Id);
            if (community == null)
            {
                community = new Community { Id = packet.Id, Name = packet.Text ?? "Communauté", MemberIds = memberIds };
                Communities.Add(community);
                _communities.Add(new CommunityData { Id = community.Id, Name = community.Name, MemberIds = community.MemberIds, UnreadCount = community.UnreadCount });
            }
            else
            {
                community.Name = packet.Text ?? community.Name;
                community.MemberIds = memberIds;
            }
            Save();
            ShowToast(AppPreferences.Text($"Invitation reçue : {community.Name}.", $"Invitation received: {community.Name}."));
        }

        private void LoadCommunityMessages(Community community)
        {
            CurrentCommunityMessages.Clear();
            if (community == null) return;
            foreach (var message in _messages.Where(m => m.CommunityId == community.Id).OrderBy(m => m.Time))
                CurrentCommunityMessages.Add(message);
        }

        private async void SendCommunityMessage()
        {
            var community = SelectedCommunity;
            var text = (CommunityMessageText ?? "").Trim();
            if (community == null || text.Length == 0) return;

            var message = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                ContactId = community.Id,
                CommunityId = community.Id,
                IsMine = true,
                Text = text,
                Time = DateTime.UtcNow,
                State = 0
            };
            CommunityMessageText = "";
            _messages.Add(message);
            CurrentCommunityMessages.Add(message);
            Save();

            var recipients = Contacts.Where(c => c.IsOnline && community.MemberIds.Contains(c.Id)).ToList();
            if (recipients.Count == 0)
            {
                message.State = 2;
                ShowToast(AppPreferences.Text("Aucun membre n’est en ligne pour recevoir le message.", "No members are online to receive this message."));
                Save();
                return;
            }

            var results = await Task.WhenAll(recipients.Select(c => _net.SendAsync(c.Ip, c.Port, new Packet
            {
                T = "cmsg",
                Id = message.Id,
                From = _store.UserId,
                FromName = _store.UserName,
                Text = text,
                CommunityId = community.Id,
                Ticks = message.Time.Ticks
            })));
            message.State = results.Any(delivered => delivered) ? 1 : 2;
            Save();
            if (message.State == 2) ShowToast(AppPreferences.Text("Le message communautaire n’a pas pu être remis.", "The community message could not be delivered."));
        }

        private void ReceiveCommunityMessage(Contact sender, Packet packet)
        {
            var community = Communities.FirstOrDefault(c => c.Id == packet.CommunityId);
            if (community == null || !community.MemberIds.Contains(_store.UserId) || !community.MemberIds.Contains(sender.Id) ||
                string.IsNullOrWhiteSpace(packet.Id) || _messages.Any(m => m.Id == packet.Id)) return;

            var message = new ChatMessage
            {
                Id = packet.Id,
                ContactId = community.Id,
                CommunityId = community.Id,
                IsMine = false,
                SenderName = sender.Name,
                Text = packet.Text ?? "",
                Time = new DateTime(packet.Ticks, DateTimeKind.Utc),
                State = 1
            };
            _messages.Add(message);
            if (SelectedCommunity == community && IsCommunitiesTab)
            {
                CurrentCommunityMessages.Add(message);
            }
            else
            {
                community.UnreadCount++;
                if (NotificationSoundsEnabled) System.Media.SystemSounds.Asterisk.Play();
            }
            Save();
        }

        private void OnPeer(Packet p, string ip)
        {
            var c = Contacts.FirstOrDefault(x => x.Id == p.From);

            if (p.T == "bye")
            {
                if (c != null) c.IsOnline = false;
                return;
            }

            bool isNew = false;
            if (c == null)
            {
                c = new Contact { Id = p.From, Name = p.FromName };
                AddContact(c);
                isNew = true;
            }
            else if (!string.IsNullOrWhiteSpace(p.FromName) && c.Name != p.FromName)
            {
                c.Name = p.FromName;
            }

            bool cameOnline = !c.IsOnline;
            c.Ip = ip;
            c.Port = p.Port;
            c.LastSeen = DateTime.UtcNow;
            c.IsOnline = true;

            if (isNew) Save();
            if (cameOnline) _ = OnContactOnlineAsync(c);
            if (cameOnline) _ = SendProfilePhotoAsync(c);
            ContactsView.Refresh();
        }

        private Task<bool> SendProfilePhotoAsync(Contact contact)
        {
            if (contact == null || !contact.IsOnline || contact.Port <= 0) return Task.FromResult(false);
            return _net.SendAsync(contact.Ip, contact.Port, new Packet
            {
                T = "profile",
                From = _store.UserId,
                FromName = _store.UserName,
                Data = _profilePhotoBytes == null ? "" : Convert.ToBase64String(_profilePhotoBytes)
            });
        }

        private void BroadcastProfilePhoto()
        {
            foreach (var contact in Contacts.Where(c => c.IsOnline && c.Port > 0).ToList())
                _ = SendProfilePhotoAsync(contact);
        }

        private async Task OnContactOnlineAsync(Contact c)
        {
            var pending = _messages
                .Where(m => m.IsMine && m.ContactId == c.Id && m.State != 1)
                .OrderBy(m => m.Time).ToList();
            foreach (var m in pending) await DeliverAsync(c, m);

            foreach (var s in _statuses.Where(x => x.AuthorId == _store.UserId).ToList())
                await SendStatusAsync(c, s);
            foreach (var community in Communities.Where(x => x.MemberIds.Contains(c.Id)).ToList())
                await SendCommunityInviteAsync(c, community);
        }

        // ---------- Réception ----------

        private void OnPacket(Packet p, string ip)
        {
            var c = Contacts.FirstOrDefault(x => x.Id == p.From);
            if (c == null)
            {
                c = new Contact { Id = p.From, Name = string.IsNullOrWhiteSpace(p.FromName) ? "Inconnu" : p.FromName, Ip = ip };
                AddContact(c);
            }

            if (p.T == "profile") ReceiveProfilePhoto(c, p);
            else if (p.T == "msg") ReceiveMessage(c, p);
            else if (p.T == "cinvite") ReceiveCommunityInvite(c, p);
            else if (p.T == "cmsg") ReceiveCommunityMessage(c, p);
            else if (p.T == "vonce") HandleViewOnceOpenedNotification(p.Id);
            else if (p.T == "status") ReceiveStatus(p);
            else if (p.T == "sview") ReceiveStatusView(p);
            else if (p.T == "sdel") ReceiveStatusDelete(p);
            else if (p.T == "call_req") ReceiveCallRequest(c, p);
            else if (p.T == "call_accept") ReceiveCallAccept(p);
            else if (p.T == "call_reject") ReceiveCallReject(p);
            else if (p.T == "call_end") ReceiveCallEnd(p);
        }

        private void ReceiveProfilePhoto(Contact contact, Packet packet)
        {
            string data = packet.Data ?? "";
            if (data.Length > 1_000_000) return;
            if (data.Length > 0)
            {
                try { Convert.FromBase64String(data); }
                catch { return; }
            }
            contact.ProfilePhotoData = data.Length == 0 ? null : data;
            Save();
        }

        private void ReceiveMessage(Contact c, Packet p)
        {
            if (string.IsNullOrEmpty(p.Id) || _messages.Any(m => m.Id == p.Id)) return;

            var msg = new ChatMessage
            {
                Id = p.Id,
                ContactId = c.Id,
                IsMine = false,
                Text = p.Text ?? "",
                Kind = p.Kind ?? "text",
                Data = p.Data,
                AttachmentName = p.AttachmentName,
                ViewOnce = p.ViewOnce,
                EphemeralSeconds = p.EphemeralSeconds,
                VoiceDuration = p.VoiceDuration,
                Time = new DateTime(p.Ticks, DateTimeKind.Utc),
                State = 1
            };
            _messages.Add(msg);
            UpdatePreview(c, msg);

            if (ShowChat && SelectedContact == c)
            {
                CurrentMessages.Add(msg);
                ScrollRequested?.Invoke();
            }
            else
            {
                c.Unread++;
                if (NotificationSoundsEnabled) System.Media.SystemSounds.Asterisk.Play();
            }

            Save();
            ContactsView.Refresh();
        }

        private void HandleViewOnceOpenedNotification(string id)
        {
            var msg = _messages.FirstOrDefault(x => x.Id == id);
            if (msg != null)
            {
                msg.Viewed = true;
                msg.Revealed = true;
                Save();
            }
        }

        private void ReceiveStatus(Packet p)
        {
            if (string.IsNullOrEmpty(p.Id) || _statuses.Any(s => s.Id == p.Id)) return;

            bool isImage = p.Kind == "image" && !string.IsNullOrEmpty(p.Data) && p.Data.Length < UploadLimits.MaxEncodedImageCharacters;

            var post = new StatusPost
            {
                Id = p.Id,
                AuthorId = p.From,
                AuthorName = p.FromName,
                Kind = isImage ? "image" : "text",
                Text = p.Text ?? "",
                Color = Regex.IsMatch(p.Color ?? "", "^#[0-9A-Fa-f]{6}$") ? p.Color : "#635BDB",
                ImageData = isImage ? p.Data : null,
                Time = new DateTime(p.Ticks, DateTimeKind.Utc)
            };
            if (post.Time < DateTime.UtcNow.AddHours(-24)) return;

            _statuses.Add(post);
            RebuildStatusGroups();
            Save();
        }

        private void ReceiveStatusView(Packet p)
        {
            var post = _statuses.FirstOrDefault(s => s.Id == p.Id && s.AuthorId == _store.UserId);
            if (post == null || string.IsNullOrEmpty(p.From)) return;

            if (post.Viewers == null) post.Viewers = new List<string>();
            if (post.Viewers.Contains(p.From)) return;

            post.Viewers.Add(p.From);
            post.RaiseViewCount();
            Save();
        }

        private void ReceiveStatusDelete(Packet p)
        {
            if (_statuses.RemoveAll(s => s.Id == p.Id && s.AuthorId == p.From) > 0)
            {
                RebuildStatusGroups();
                Save();
            }
        }

        // ---------- Appels Audio / Vidéo Média Réel ----------

        private void StartCall(Contact c, string type)
        {
            if (c == null) return;
            if (!c.IsOnline)
            {
                ShowToast(AppPreferences.Text($"{c.Name} est actuellement hors ligne.", $"{c.Name} is currently offline."));
                return;
            }

            _activeCallContact = c;
            _activeCallId = Guid.NewGuid().ToString("N");
            CallContactName = c.Name;
            CallType = type;
            CallStatusText = AppPreferences.Text("Appel en cours... Sonnerie...", "Calling... Ringing...");
            CallDurationText = "00:00";
            _callSeconds = 0;
            IsInCall = true;
            IsIncomingCall = false;
            IsCallConnected = false;
            IsMicMuted = false;
            IsCameraOff = false;

            OnPropertyChanged(nameof(CallContactInitial));
            OnPropertyChanged(nameof(CallContactAvatarBg));
            OnPropertyChanged(nameof(CallContactAvatarFg));

            // Démarrer le moteur média pour l'appelant
            _mediaEngine?.Dispose();
            _mediaEngine = new CallMediaEngine(type == "video")
            {
                LocalUserName = _store.UserName,
                RemoteUserName = c.Name
            };
            _mediaEngine.LocalVideoFrameReady += frame => _ui.BeginInvoke(new Action(() => LocalVideoImage = frame));
            _mediaEngine.RemoteVideoFrameReady += frame => _ui.BeginInvoke(new Action(() => RemoteVideoImage = frame));
            _mediaEngine.LocalAudioLevelChanged += level => _ui.BeginInvoke(new Action(() => LocalAudioLevel = level));
            _mediaEngine.RemoteAudioLevelChanged += level => _ui.BeginInvoke(new Action(() => RemoteAudioLevel = level));
            _mediaEngine.CameraError += message => _ui.BeginInvoke(new Action(() =>
            {
                IsCameraOff = true;
                CallStatusText = message;
                ShowToast(message);
            }));

            int localMediaPort = _mediaEngine.StartListening();
            if (type == "video") _mediaEngine.StartVideoCapture();

            _ = _net.SendAsync(c.Ip, c.Port, new Packet
            {
                T = "call_req",
                Id = _activeCallId,
                From = _store.UserId,
                FromName = _store.UserName,
                CallType = type,
                CallId = _activeCallId,
                MediaPort = localMediaPort
            });
        }

        private void ReceiveCallRequest(Contact c, Packet p)
        {
            if (IsInCall)
            {
                _ = _net.SendAsync(c.Ip, c.Port, new Packet
                {
                    T = "call_reject",
                    CallId = p.CallId,
                    From = _store.UserId,
                    FromName = _store.UserName
                });
                return;
            }

            _activeCallContact = c;
            _activeCallId = p.CallId;
            _incomingMediaPort = p.MediaPort;
            CallContactName = c.Name;
            CallType = p.CallType ?? "audio";
            CallStatusText = AppPreferences.LanguageCode == "en" ? $"Incoming {CallType} call..." : $"Appel {CallType} entrant...";
            CallDurationText = "00:00";
            _callSeconds = 0;
            IsInCall = true;
            IsIncomingCall = true;
            IsCallConnected = false;
            IsMicMuted = false;
            IsCameraOff = false;

            OnPropertyChanged(nameof(CallContactInitial));
            OnPropertyChanged(nameof(CallContactAvatarBg));
            OnPropertyChanged(nameof(CallContactAvatarFg));

        }

        private void AcceptIncomingCall()
        {
            IsIncomingCall = false;
            IsCallConnected = true;
            CallStatusText = AppPreferences.Text("En communication", "Connected");
            _callTimer.Start();

            // Démarrer le moteur média pour l'appelé
            _mediaEngine?.Dispose();
            _mediaEngine = new CallMediaEngine(CallType == "video")
            {
                LocalUserName = _store.UserName,
                RemoteUserName = _activeCallContact?.Name ?? "Contact"
            };
            _mediaEngine.LocalVideoFrameReady += frame => _ui.BeginInvoke(new Action(() => LocalVideoImage = frame));
            _mediaEngine.RemoteVideoFrameReady += frame => _ui.BeginInvoke(new Action(() => RemoteVideoImage = frame));
            _mediaEngine.LocalAudioLevelChanged += level => _ui.BeginInvoke(new Action(() => LocalAudioLevel = level));
            _mediaEngine.RemoteAudioLevelChanged += level => _ui.BeginInvoke(new Action(() => RemoteAudioLevel = level));
            _mediaEngine.CameraError += message => _ui.BeginInvoke(new Action(() =>
            {
                IsCameraOff = true;
                CallStatusText = message;
                ShowToast(message);
            }));

            if (CallType == "video") _mediaEngine.StartVideoCapture();

            if (_activeCallContact != null && _activeCallContact.IsOnline)
            {
                // Se connecter au flux média de l'appelant
                if (_incomingMediaPort > 0)
                {
                    _ = _mediaEngine.ConnectAsync(_activeCallContact.Ip, _incomingMediaPort);
                }

                _ = _net.SendAsync(_activeCallContact.Ip, _activeCallContact.Port, new Packet
                {
                    T = "call_accept",
                    CallId = _activeCallId,
                    From = _store.UserId,
                    FromName = _store.UserName
                });
            }
        }

        private void RejectIncomingCall()
        {
            IsInCall = false;
            IsIncomingCall = false;
            IsCallConnected = false;

            if (_activeCallContact != null && _activeCallContact.IsOnline)
            {
                _ = _net.SendAsync(_activeCallContact.Ip, _activeCallContact.Port, new Packet
                {
                    T = "call_reject",
                    CallId = _activeCallId,
                    From = _store.UserId,
                    FromName = _store.UserName
                });
            }

            LogCallMessage(_activeCallContact, $"Appel {CallType} manqué", false);
            _activeCallContact = null;
            _activeCallId = null;
        }

        private void ReceiveCallAccept(Packet p)
        {
            if (_activeCallId == p.CallId)
            {
                IsCallConnected = true;
                CallStatusText = AppPreferences.Text("En communication", "Connected");
                _callTimer.Start();

                // L'appelé ouvre le flux vers le port média réservé par l'appelant.
            }
        }

        private void ReceiveCallReject(Packet p)
        {
            if (_activeCallId == p.CallId)
            {
                ShowToast(AppPreferences.Text($"{CallContactName} a refusé l'appel.", $"{CallContactName} declined the call."));
                EndCallInternal(false);
            }
        }

        private void ReceiveCallEnd(Packet p)
        {
            if (_activeCallId == p.CallId)
            {
                ShowToast(AppPreferences.Text("L'appel est terminé.", "The call has ended."));
                EndCallInternal(true);
            }
        }

        private void EndCurrentCall()
        {
            if (_activeCallContact != null && _activeCallContact.IsOnline)
            {
                _ = _net.SendAsync(_activeCallContact.Ip, _activeCallContact.Port, new Packet
                {
                    T = "call_end",
                    CallId = _activeCallId,
                    From = _store.UserId,
                    FromName = _store.UserName
                });
            }
            EndCallInternal(true);
        }

        private void EndCallInternal(bool log)
        {
            _callTimer.Stop();

            try { _mediaEngine?.Dispose(); } catch { }
            _mediaEngine = null;

            LocalVideoImage = null;
            RemoteVideoImage = null;
            LocalAudioLevel = 0;
            RemoteAudioLevel = 0;
            AudioWaveScale = 1.0;

            if (log && _activeCallContact != null)
            {
                string info = IsCallConnected
                    ? $"Appel {CallType} ({CallDurationText})"
                    : $"Appel {CallType} sans réponse";
                LogCallMessage(_activeCallContact, info, true);
            }

            IsInCall = false;
            IsIncomingCall = false;
            IsCallConnected = false;
            _activeCallContact = null;
            _activeCallId = null;
        }

        private void ToggleMicMute()
        {
            IsMicMuted = !IsMicMuted;
            if (_mediaEngine != null) _mediaEngine.IsMuted = IsMicMuted;
        }

        private void ToggleCamera()
        {
            IsCameraOff = !IsCameraOff;
            if (_mediaEngine != null) _mediaEngine.IsCameraOff = IsCameraOff;
        }

        private void LogCallMessage(Contact c, string text, bool isMine)
        {
            if (c == null) return;
            var msg = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                ContactId = c.Id,
                IsMine = isMine,
                Kind = "call",
                Text = text,
                Time = DateTime.UtcNow,
                State = 1
            };
            _messages.Add(msg);
            if (SelectedContact == c) CurrentMessages.Add(msg);
            UpdatePreview(c, msg);
            ContactsView.Refresh();
            ScrollRequested?.Invoke();
            Save();
        }

        // ---------- Envoi Message Normal ----------

        private async void Send()
        {
            var text = (MessageText ?? "").Trim();
            var c = SelectedContact;
            if (text.Length == 0 || c == null) return;

            var viewOnce = SendViewOnce;
            MessageText = "";
            SendViewOnce = false;
            IsEmojiPickerOpen = false;

            var msg = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                ContactId = c.Id,
                IsMine = true,
                Text = text,
                Kind = "text",
                ViewOnce = viewOnce,
                EphemeralSeconds = c.EphemeralSeconds,
                Time = DateTime.UtcNow,
                State = 0
            };
            _messages.Add(msg);
            CurrentMessages.Add(msg);
            UpdatePreview(c, msg);
            ContactsView.Refresh();
            ScrollRequested?.Invoke();
            Save();

            await DeliverAsync(c, msg);
        }

        public void ConfigureGeminiApiKey(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                MetaAiStatusText = "Colle une clé API " + MetaAiProvider + " avant de l’enregistrer.";
                return;
            }
            GeminiApiKeyStore.Save(apiKey);
            MetaAiStatusText = "Clé enregistrée et chiffrée pour ton compte Windows. Envoie une question pour tester " + MetaAiProvider + ".";
        }

        private void RemoveGeminiApiKey()
        {
            GeminiApiKeyStore.Delete();
            MetaAiStatusText = "Clé supprimée. L’assistant repasse en mode hors ligne.";
        }

        private async void SendMetaAiDemoMessage()
        {
            var prompt = (MetaAiPrompt ?? "").Trim();
            if (prompt.Length == 0) return;

            MetaAiMessages.Add(new MetaAiDemoMessage
            {
                IsFromUser = true,
                Text = prompt,
                Time = DateTime.Now.ToString("HH:mm")
            });
            MetaAiPrompt = "";

            string answer;
            var apiKey = GeminiApiKeyStore.Read();
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                try
                {
                    MetaAiStatusText = MetaAiProvider + " réfléchit…";
                    answer = await AskGeminiAsync(apiKey);
                    MetaAiStatusText = "Connecté à " + MetaAiProvider + " · réponse en ligne. Évite les informations confidentielles.";
                    AddMetaAiReply(answer);
                    return;
                }
                catch (Exception ex)
                {
                    answer = "Je n’ai pas pu joindre " + MetaAiProvider + ". " + ex.Message;
                    MetaAiStatusText = MetaAiProvider + " indisponible. Corrige la clé ou réessaie plus tard.";
                    AddMetaAiReply(answer);
                    return;
                }
            }

            var normalized = prompt.ToLowerInvariant();
            if (normalized.Contains("bonjour") || normalized.Contains("salut") || normalized.Contains("coucou"))
                answer = "Bonjour ! Je peux t’aider à découvrir cette démo. Essaie de me demander comment créer un groupe ou envoyer une photo.";
            else if (normalized.Contains("groupe"))
                answer = "Pour démarrer un groupe dans cette démo, ouvre l’onglet Communautés puis utilise le bouton + pour le créer et choisir son nom.";
            else if (normalized.Contains("photo") || normalized.Contains("fichier") || normalized.Contains("image"))
                answer = "Dans une conversation, touche le bouton + près de la zone de saisie pour choisir une photo ou un fichier à partager.";
            else if (normalized.Contains("éphém") || normalized.Contains("ephem") || normalized.Contains("dispara"))
                answer = "Dans une conversation, ouvre le menu avec l’icône sablier puis choisis une durée. Les messages de démonstration utilisent le stockage local de l’application.";
            else if (normalized.Contains("appel") || normalized.Contains("caméra") || normalized.Contains("camera"))
                answer = "Ouvre une conversation puis utilise les boutons d’appel audio ou vidéo. Pour la vidéo, Windows doit autoriser l’accès à la caméra.";
            else if (normalized.Contains("paramètre") || normalized.Contains("parametre") || normalized.Contains("thème") || normalized.Contains("theme") || normalized.Contains("langue"))
                answer = "Les paramètres se trouvent avec l’icône engrenage en bas à gauche. Tu peux y régler l’apparence et la langue de l’application.";
            else if (normalized.Contains("aide") || normalized.Contains("que peux-tu") || normalized.Contains("que peux tu"))
                answer = "Je peux répondre à quelques questions prévues pour la présentation : groupes, partage de fichiers, messages éphémères, appels et paramètres. Je fonctionne hors ligne avec des réponses préparées.";
            else
                answer = "Je suis en mode démo hors ligne : je n’ai pas compris cette question. Essaie de demander de l’aide sur les groupes, les photos, les messages éphémères, les appels ou les paramètres.";

            AddMetaAiReply(answer);
        }

        private void AddMetaAiReply(string answer)
        {
            MetaAiMessages.Add(new MetaAiDemoMessage
            {
                IsFromUser = false,
                Text = answer,
                Time = DateTime.Now.ToString("HH:mm")
            });
        }

        private async Task<string> AskGeminiAsync(string apiKey)
        {
            var history = MetaAiMessages
                .Where(m => !string.IsNullOrWhiteSpace(m.Text))
                .TakeLast(12)
                .Select(m => new
                {
                    role = m.IsFromUser ? "user" : "model",
                    parts = new[] { new { text = m.Text } }
                })
                .ToArray();

            if (MetaAiProvider == "OpenRouter")
            {
                var messages = new List<object>
                {
                    new { role = "system", content = "Tu es un assistant convivial intégré à une application de messagerie de démonstration. Réponds en français sauf si l'utilisateur écrit dans une autre langue. Sois utile, clair et concis. Ne prétends pas être le service officiel Meta AI." }
                };
                foreach (var item in history)
                {
                    var text = item.parts.Length > 0 ? item.parts[0].text : "";
                    messages.Add(new { role = item.role, content = text });
                }
                var openRouterPayload = new { model = "openrouter/free", messages, max_tokens = 512, temperature = 0.7 };
                using (var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions"))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    request.Content = new StringContent(JsonSerializer.Serialize(openRouterPayload), Encoding.UTF8, "application/json");
                    using (var response = await MetaAiHttp.SendAsync(request))
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode) throw DescribeAiHttpError(response.StatusCode, body);
                        using (var json = JsonDocument.Parse(body))
                        {
                            var text = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Le fournisseur a renvoyé une réponse vide.");
                            return text.Trim();
                        }
                    }
                }
            }

            var payload = new
            {
                systemInstruction = new { parts = new[] { new { text = "Tu es un assistant convivial intégré à une application de messagerie de démonstration. Réponds en français sauf si l'utilisateur écrit dans une autre langue. Sois utile, clair et concis. Ne prétends pas être le service officiel Meta AI." } } },
                contents = history,
                generationConfig = new { maxOutputTokens = 512, temperature = 0.7 }
            };
            using (var request = new HttpRequestMessage(HttpMethod.Post,
                "https://generativelanguage.googleapis.com/v1beta/models/gemini-3.8-flash:generateContent"))
            {
                request.Headers.Add("x-goog-api-key", apiKey);
                request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using (var response = await MetaAiHttp.SendAsync(request))
                {
                    var body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode) throw DescribeAiHttpError(response.StatusCode, body);
                    using (var json = JsonDocument.Parse(body))
                    {
                        var text = json.RootElement.GetProperty("candidates")[0]
                            .GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
                        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Gemini a renvoyé une réponse vide.");
                        return text.Trim();
                    }
                }
            }
        }

        private static Exception DescribeAiHttpError(System.Net.HttpStatusCode statusCode, string body)
        {
            var code = (int)statusCode;
            if (statusCode == System.Net.HttpStatusCode.Unauthorized || statusCode == System.Net.HttpStatusCode.Forbidden)
                return new InvalidOperationException("clé API refusée (HTTP " + code + "). Vérifie qu’elle vient du fournisseur sélectionné et qu’elle est active.");
            if (statusCode == System.Net.HttpStatusCode.TooManyRequests)
                return new InvalidOperationException("quota gratuit ou limite de requêtes atteinte (HTTP 429). Réessaie plus tard.");
            if (statusCode == System.Net.HttpStatusCode.NotFound)
                return new InvalidOperationException("modèle ou endpoint introuvable (HTTP 404). Le fournisseur a peut-être changé son catalogue.");
            if ((int)statusCode >= 500)
                return new InvalidOperationException("service temporairement indisponible (HTTP " + code + "). Réessaie plus tard.");
            string providerMessage = "";
            try
            {
                using (var json = JsonDocument.Parse(body))
                {
                    if (json.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message))
                        providerMessage = message.GetString() ?? "";
                }
            }
            catch { }
            if (providerMessage.Length > 240) providerMessage = providerMessage.Substring(0, 240);
            return new InvalidOperationException("erreur HTTP " + code + (string.IsNullOrWhiteSpace(providerMessage) ? "." : " : " + providerMessage));
        }

        // ---------- Photo Message ----------

        private void PickChatPhoto()
        {
            var c = SelectedContact;
            if (c == null) return;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choisir une photo",
                Filter = "Images (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                if (new FileInfo(dlg.FileName).Length > UploadLimits.MaxSourceFileBytes)
                {
                    ShowToast(AppPreferences.Text("La photo dépasse la limite de 50 Mo.", "The photo exceeds the 50 MB limit."));
                    return;
                }
                var viewOnce = SendViewOnce;
                SendViewOnce = false;

                var msg = new ChatMessage
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ContactId = c.Id,
                    IsMine = true,
                    Kind = "image",
                    Data = EncodeImage(dlg.FileName),
                    Text = "📷 Photo",
                    ViewOnce = viewOnce,
                    EphemeralSeconds = c.EphemeralSeconds,
                    Time = DateTime.UtcNow,
                    State = 0
                };
                _messages.Add(msg);
                CurrentMessages.Add(msg);
                UpdatePreview(c, msg);
                ContactsView.Refresh();
                ScrollRequested?.Invoke();
                Save();
                _ = DeliverAsync(c, msg);
            }
            catch
            {
                ShowToast(AppPreferences.Text("Impossible d’ajouter cette photo.", "Could not add this photo."));
            }
        }

        private void PickChatFile()
        {
            var contact = SelectedContact;
            if (contact == null) return;
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = AppPreferences.Text("Ajouter un fichier", "Add a file"),
                Filter = "Tous les fichiers (*.*)|*.*"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                var file = new FileInfo(dialog.FileName);
                if (file.Length > UploadLimits.MaxSourceFileBytes)
                {
                    ShowToast(AppPreferences.Text("Le fichier dépasse la limite de 50 Mo.", "The file exceeds the 50 MB limit."));
                    return;
                }

                string ext = file.Extension.ToLowerInvariant();
                bool isImage = ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".bmp";
                bool viewOnce = isImage && SendViewOnce;
                SendViewOnce = false;
                var msg = new ChatMessage
                {
                    Id = Guid.NewGuid().ToString("N"), ContactId = contact.Id, IsMine = true,
                    Kind = isImage ? "image" : "file",
                    Data = isImage ? EncodeImage(dialog.FileName) : Convert.ToBase64String(File.ReadAllBytes(dialog.FileName)),
                    AttachmentName = file.Name,
                    Text = isImage ? "📷 " + file.Name : file.Name,
                    ViewOnce = viewOnce,
                    EphemeralSeconds = contact.EphemeralSeconds,
                    Time = DateTime.UtcNow, State = 0
                };
                _messages.Add(msg);
                CurrentMessages.Add(msg);
                UpdatePreview(contact, msg);
                ContactsView.Refresh();
                ScrollRequested?.Invoke();
                Save();
                _ = DeliverAsync(contact, msg);
            }
            catch (Exception ex)
            {
                ShowToast(AppPreferences.Text("Impossible d’ajouter ce fichier : ", "Could not add this file: ") + ex.Message);
            }
        }

        private void SaveAttachment(ChatMessage message)
        {
            if (message == null || !message.IsFile || string.IsNullOrEmpty(message.Data)) return;
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = string.IsNullOrWhiteSpace(message.AttachmentName) ? "fichier" : Path.GetFileName(message.AttachmentName),
                Filter = "Tous les fichiers (*.*)|*.*"
            };
            if (dialog.ShowDialog() != true) return;
            try { File.WriteAllBytes(dialog.FileName, Convert.FromBase64String(message.Data)); }
            catch (Exception ex) { ShowToast(AppPreferences.Text("Impossible d’enregistrer le fichier : ", "Could not save the file: ") + ex.Message); }
        }

        private void OpenPhotoPreview(ChatMessage msg)
        {
            if (msg != null && msg.Image != null)
            {
                PreviewImage = msg.Image;
                IsPhotoViewerOpen = true;
            }
        }

        // ---------- Voice Messages (Voices) ----------

        private void StartRecordingVoice()
        {
            if (SelectedContact == null) return;

            if (AudioRecorder.StartRecording(out string error))
            {
                _recordingSeconds = 0;
                RecordingTimeText = "0:00";
                IsRecordingVoice = true;
                _voiceTimer.Start();
            }
            else
            {
                ShowToast(AppPreferences.Text("Erreur microphone : ", "Microphone error: ") + (error ?? AppPreferences.Text("inconnu", "unknown")));
            }
        }

        private void CancelRecordingVoice()
        {
            _voiceTimer.Stop();
            IsRecordingVoice = false;
            AudioRecorder.StopRecording(false);
            RecordingTimeText = "0:00";
        }

        private void SendRecordingVoice()
        {
            _voiceTimer.Stop();
            IsRecordingVoice = false;
            string voiceData = AudioRecorder.StopRecording(true);

            if (string.IsNullOrEmpty(voiceData))
            {
                ShowToast(AppPreferences.Text("Enregistrement trop court ou annulé.", "Recording was too short or was cancelled."));
                return;
            }

            var c = SelectedContact;
            if (c == null) return;

            string duration = RecordingTimeText;
            var msg = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                ContactId = c.Id,
                IsMine = true,
                Kind = "voice",
                Data = voiceData,
                VoiceDuration = duration,
                EphemeralSeconds = c.EphemeralSeconds,
                Text = $"🎤 Message vocal ({duration})",
                Time = DateTime.UtcNow,
                State = 0
            };

            _messages.Add(msg);
            CurrentMessages.Add(msg);
            UpdatePreview(c, msg);
            ContactsView.Refresh();
            ScrollRequested?.Invoke();
            Save();

            _ = DeliverAsync(c, msg);
        }

        private void TogglePlayVoice(ChatMessage msg)
        {
            if (msg == null) return;

            if (msg.IsPlayingVoice)
            {
                _audioPlayer.Pause();
            }
            else
            {
                _audioPlayer.Play(msg);
            }
        }

        // ---------- View Once (Vues Uniques) ----------

        private void OpenViewOnce(ChatMessage msg)
        {
            if (msg == null || !msg.ViewOnce || msg.Viewed) return;

            _currentViewOnceMessage = msg;
            ViewOnceImage = msg.Kind == "image" && !string.IsNullOrWhiteSpace(msg.Data) ? DecodeImageData(msg.Data) : null;
            ViewOnceText = msg.Kind == "image" ? "" : msg.Text;
            _viewOnceCountdown = 10;
            ViewOnceCountdownText = "10s";

            IsViewOnceModalOpen = true;
            _viewOnceTimer.Start();
        }

        private void CloseViewOnceModal()
        {
            _viewOnceTimer.Stop();
            IsViewOnceModalOpen = false;

            if (_currentViewOnceMessage != null)
            {
                _currentViewOnceMessage.Revealed = true;
                _currentViewOnceMessage.Viewed = true;
                _currentViewOnceMessage.Data = null;
                _currentViewOnceMessage.Text = "① Message ouvert";
                Save();

                var c = Contacts.FirstOrDefault(x => x.Id == _currentViewOnceMessage.ContactId);
                if (c != null && c.IsOnline)
                {
                    _ = _net.SendAsync(c.Ip, c.Port, new Packet
                    {
                        T = "vonce",
                        Id = _currentViewOnceMessage.Id,
                        From = _store.UserId,
                        FromName = _store.UserName
                    });
                }

                _currentViewOnceMessage = null;
            }

            ViewOnceImage = null;
            ViewOnceText = "";
        }

        // ---------- Envoi Réseau ----------

        private async Task DeliverAsync(Contact c, ChatMessage msg)
        {
            if (!c.IsOnline || c.Port <= 0)
            {
                msg.State = 0;
                Save();
                return;
            }

            bool ok = await _net.SendAsync(c.Ip, c.Port, new Packet
            {
                T = "msg",
                Id = msg.Id,
                From = _store.UserId,
                FromName = _store.UserName,
                Text = msg.Text,
                Kind = msg.Kind,
                Data = msg.Data,
                AttachmentName = msg.AttachmentName,
                ViewOnce = msg.ViewOnce,
                EphemeralSeconds = msg.EphemeralSeconds,
                VoiceDuration = msg.VoiceDuration,
                Ticks = msg.Time.Ticks
            });

            msg.State = ok ? 1 : 2;
            Save();
        }

        private Task<bool> SendStatusAsync(Contact c, StatusPost s)
        {
            return _net.SendAsync(c.Ip, c.Port, new Packet
            {
                T = "status",
                Id = s.Id,
                From = _store.UserId,
                FromName = _store.UserName,
                Text = s.Text,
                Color = s.Color,
                Kind = s.Kind,
                Data = s.ImageData,
                Ticks = s.Time.Ticks
            });
        }

        private void LoadMessages(Contact c)
        {
            CurrentMessages.Clear();
            foreach (var m in _messages.Where(x => x.ContactId == c.Id).OrderBy(x => x.Time))
                CurrentMessages.Add(m);
            ScrollRequested?.Invoke();
        }

        private static void UpdatePreview(Contact c, ChatMessage m)
        {
            var local = m.Time.ToLocalTime();
            if (c.LastTime == null || local >= c.LastTime.Value)
            {
                string text = m.Text;
                if (m.ViewOnce) text = m.IsViewOnceDone ? AppPreferences.Text("① Ouvert", "① Opened") : AppPreferences.Text("① Vue unique", "① View-once");
                c.LastText = (m.IsMine ? AppPreferences.Text("Vous : ", "You: ") : "") + (text ?? "").Replace("\r", " ").Replace("\n", " ");
                c.LastTime = local;
            }
        }

        // ---------- Statuts ----------

        private void ChooseProfilePhoto()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = AppPreferences.Text("Choisir une photo de profil", "Choose a profile photo"),
                Filter = "Images (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var info = new FileInfo(dlg.FileName);
                if (info.Length > UploadLimits.MaxSourceFileBytes)
                {
                    ShowToast(AppPreferences.Text("La photo dépasse la limite de 50 Mo.", "The photo exceeds the 50 MB limit."));
                    return;
                }
                byte[] imageBytes = EncodeProfilePhoto(dlg.FileName);
                _store.SaveProfilePhoto(imageBytes);
                _profilePhotoBytes = imageBytes;
                ProfilePhoto = LoadProfilePhotoImage(imageBytes);
                OnPropertyChanged(nameof(ProfilePhoto));
                BroadcastProfilePhoto();
                ShowToast(AppPreferences.Text("Photo de profil mise à jour.", "Profile photo updated."));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                ShowToast(AppPreferences.Text("Impossible d’enregistrer cette photo.", "Could not save this photo."));
            }
        }

        private void RemoveProfilePhoto()
        {
            try
            {
                _store.RemoveProfilePhoto();
                _profilePhotoBytes = null;
                ProfilePhoto = null;
                BroadcastProfilePhoto();
                ShowToast(AppPreferences.Text("Photo de profil supprimée.", "Profile photo removed."));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                ShowToast(AppPreferences.Text("Impossible de supprimer cette photo.", "Could not remove this photo."));
            }
        }

        private static byte[] EncodeProfilePhoto(string path)
        {
            var source = new BitmapImage();
            source.BeginInit();
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.DecodePixelWidth = 512;
            source.UriSource = new Uri(path);
            source.EndInit();

            double scale = Math.Min(1.0, 512.0 / Math.Max(source.PixelWidth, source.PixelHeight));
            BitmapSource resized = scale < 1.0
                ? new TransformedBitmap(source, new ScaleTransform(scale, scale))
                : source;
            var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
            encoder.Frames.Add(BitmapFrame.Create(resized));
            using (var stream = new MemoryStream())
            {
                encoder.Save(stream);
                return stream.ToArray();
            }
        }

        private static ImageSource LoadProfilePhotoImage(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;
            try
            {
                using (var stream = new MemoryStream(bytes))
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
            }
            catch { return null; }
        }

        private void PublishStatus()
        {
            var text = (StatusText ?? "").Trim();
            if (text.Length == 0) return;

            StatusText = "";
            IsComposing = false;
            PublishPost("text", text, StatusColor, null);
        }

        private void PickPhoto()
        {
            IsStatusMenuOpen = false;

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Ouvrir",
                Filter = "Images (*.jpg;*.jpeg;*.png;*.bmp;*.gif)|*.jpg;*.jpeg;*.png;*.bmp;*.gif"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var info = new FileInfo(dlg.FileName);
                if (info.Length > UploadLimits.MaxSourceFileBytes)
                {
                ShowToast(AppPreferences.Text("1 fichier que vous avez essayé d’ajouter dépasse la limite de 50 Mo.", "One file you tried to add exceeds the 50 MB limit."));
                    return;
                }
                PublishPost("image", null, null, EncodeImage(dlg.FileName));
            }
            catch
            {
                ShowToast(AppPreferences.Text("Impossible d’ajouter cette photo.", "Could not add this photo."));
            }
        }

        private static string EncodeImage(string path)
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 1280;
            bi.UriSource = new Uri(path);
            bi.EndInit();

            BitmapSource src = bi;
            double scale = Math.Min(1.0, 1280.0 / Math.Max(bi.PixelWidth, bi.PixelHeight));
            if (scale < 1.0) src = new TransformedBitmap(bi, new ScaleTransform(scale, scale));

            var enc = new JpegBitmapEncoder { QualityLevel = 80 };
            enc.Frames.Add(BitmapFrame.Create(src));
            using (var ms = new MemoryStream())
            {
                enc.Save(ms);
                string encoded = Convert.ToBase64String(ms.ToArray());
                if (encoded.Length >= UploadLimits.MaxEncodedImageCharacters)
                    throw new InvalidDataException("Image compressée trop volumineuse.");
                return encoded;
            }
        }

        private static ImageSource DecodeImageData(string data)
        {
            try
            {
                var bytes = Convert.FromBase64String(data);
                using (var stream = new MemoryStream(bytes))
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
            }
            catch { return null; }
        }

        private async void PublishPost(string kind, string text, string color, string imageData)
        {
            var post = new StatusPost
            {
                Id = Guid.NewGuid().ToString("N"),
                AuthorId = _store.UserId,
                AuthorName = _store.UserName,
                Kind = kind,
                Text = text,
                Color = color,
                ImageData = imageData,
                Time = DateTime.UtcNow
            };
            _statuses.Add(post);
            RebuildStatusGroups();
            Save();

            var targets = Contacts.Where(c => c.IsOnline && c.Port > 0).ToList();
            await Task.WhenAll(targets.Select(c => SendStatusAsync(c, post)));
        }

        private void RebuildStatusGroups()
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);
            _statuses.RemoveAll(s => s.Time < cutoff);

            var me = _store.UserId;

            MyStatusGroup = new StatusGroup(me, _store.UserName,
                _statuses.Where(s => s.AuthorId == me).OrderBy(s => s.Time).ToList(), true);

            RecentStatuses.Clear();
            foreach (var g in _statuses.Where(s => s.AuthorId != me)
                                       .GroupBy(s => s.AuthorId)
                                       .OrderByDescending(g => g.Max(s => s.Time)))
            {
                var ordered = g.OrderBy(s => s.Time).ToList();
                RecentStatuses.Add(new StatusGroup(g.Key, ordered.Last().AuthorName, ordered));
            }

            OnPropertyChanged(nameof(HasUnseenStatuses));
        }

        private static int FirstUnseenIndex(StatusGroup g)
        {
            int i = g.Posts.FindIndex(p => !p.Seen);
            return i < 0 ? 0 : i;
        }

        private StatusGroup NeighbourGroup(int delta)
        {
            if (ViewerGroup == null || ViewerIsMine) return null;

            int i = -1;
            for (int k = 0; k < RecentStatuses.Count; k++)
                if (RecentStatuses[k].AuthorId == ViewerGroup.AuthorId) { i = k; break; }

            int j = i + delta;
            return (i >= 0 && j >= 0 && j < RecentStatuses.Count) ? RecentStatuses[j] : null;
        }

        private void OpenViewer(StatusGroup g, int index)
        {
            if (g == null || g.Posts.Count == 0) return;

            IsStatusMenuOpen = false;
            IsMoreMenuOpen = false;
            IsPaused = false;
            ReplyText = "";

            ViewerGroup = g;
            ShowPost(Math.Max(0, Math.Min(index, g.Posts.Count - 1)));

            IsViewerOpen = true;
            _viewerTimer.Start();
        }

        private void ShowPost(int index)
        {
            _viewerIndex = index;
            _progress = 0;

            Segments.Clear();
            for (int i = 0; i < ViewerGroup.Posts.Count; i++)
                Segments.Add(new SegmentVM { Value = i < index ? 1 : 0 });

            OnPropertyChanged(nameof(CurrentPost));
            OnPropertyChanged(nameof(HasPrev));
            OnPropertyChanged(nameof(HasNext));

            var post = CurrentPost;
            if (post != null && !ViewerIsMine && !post.Seen)
            {
                post.Seen = true;
                Save();
                _ = NotifyViewAsync(post);
            }
        }

        private void OnViewerTick()
        {
            if (!IsViewerOpen || IsPaused || !string.IsNullOrEmpty(ReplyText)) return;

            _progress += 50.0 / StoryDurationMs;
            if (_progress >= 1.0) { Next(); return; }
            if (_viewerIndex < Segments.Count) Segments[_viewerIndex].Value = _progress;
        }

        private void Next()
        {
            if (ViewerGroup == null) return;

            if (_viewerIndex < ViewerGroup.Posts.Count - 1) { ShowPost(_viewerIndex + 1); return; }

            var g = NeighbourGroup(+1);
            if (g != null)
            {
                ViewerGroup = g;
                ShowPost(FirstUnseenIndex(g));
            }
            else CloseViewer();
        }

        private void Prev()
        {
            if (ViewerGroup == null) return;

            if (_viewerIndex > 0) { ShowPost(_viewerIndex - 1); return; }

            var g = NeighbourGroup(-1);
            if (g != null)
            {
                ViewerGroup = g;
                ShowPost(0);
            }
            else ShowPost(0);
        }

        private void CloseViewer()
        {
            _viewerTimer.Stop();
            IsViewerOpen = false;
            IsMoreMenuOpen = false;
            ReplyText = "";
            ViewerGroup = null;
            RebuildStatusGroups();
        }

        private async Task NotifyViewAsync(StatusPost post)
        {
            var c = Contacts.FirstOrDefault(x => x.Id == post.AuthorId);
            if (c == null || !c.IsOnline || c.Port <= 0) return;

            await _net.SendAsync(c.Ip, c.Port, new Packet
            {
                T = "sview",
                Id = post.Id,
                From = _store.UserId,
                FromName = _store.UserName
            });
        }

        private async void DeleteCurrentStatus()
        {
            var post = CurrentPost;
            IsMoreMenuOpen = false;
            if (post == null || !ViewerIsMine) return;

            _statuses.RemoveAll(s => s.Id == post.Id);
            Save();

            var targets = Contacts.Where(c => c.IsOnline && c.Port > 0).ToList();
            CloseViewer();

            await Task.WhenAll(targets.Select(c => _net.SendAsync(c.Ip, c.Port, new Packet
            {
                T = "sdel",
                Id = post.Id,
                From = _store.UserId,
                FromName = _store.UserName
            })));
        }

        private void ReplyPrivate()
        {
            var g = ViewerGroup;
            IsMoreMenuOpen = false;
            if (g == null || ViewerIsMine) return;

            var c = Contacts.FirstOrDefault(x => x.Id == g.AuthorId);
            CloseViewer();
            if (c != null)
            {
                ActiveTab = 0;
                SelectedContact = c;
            }
        }

        private async void SendStatusReply(string text)
        {
            text = (text ?? "").Trim();
            var g = ViewerGroup;
            if (text.Length == 0 || g == null || ViewerIsMine) return;

            var c = Contacts.FirstOrDefault(x => x.Id == g.AuthorId);
            if (c == null) return;

            ReplyText = "";
            var msg = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                ContactId = c.Id,
                IsMine = true,
                Text = "↪ Réponse à votre statut : " + text,
                Time = DateTime.UtcNow,
                State = 0
            };
            _messages.Add(msg);
            if (SelectedContact == c) CurrentMessages.Add(msg);
            UpdatePreview(c, msg);
            ContactsView.Refresh();
            Save();

            await DeliverAsync(c, msg);
        }

        // ---------- Sauvegarde / arrêt ----------

        private bool Save(bool reportFailure = true)
        {
            try
            {
                _store.Save(new StoreData
                {
                    Contacts = Contacts.Select(c => new ContactData { Id = c.Id, Name = c.Name, IsArchived = c.IsArchived, ProfilePhotoData = c.ProfilePhotoData, EphemeralSeconds = c.EphemeralSeconds }).ToList(),
                    Messages = _messages,
                    Statuses = _statuses,
                    Communities = Communities.Select(c => new CommunityData { Id = c.Id, Name = c.Name, MemberIds = c.MemberIds, UnreadCount = c.UnreadCount }).ToList(),
                    NotificationSoundsEnabled = NotificationSoundsEnabled,
                    ThemeColor = ThemeColor,
                    Language = Language
                });
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                if (reportFailure)
                    ShowToast(AppPreferences.Text("Échec de sauvegarde. Vérifiez l’espace disque et les autorisations.", "Save failed. Check disk space and permissions."));
                return false;
            }
        }

        private void OnAppPreferencesChanged()
        {
            if (_isDisposed) return;
            _ui.BeginInvoke(new Action(() =>
            {
                if (_isDisposed) return;
                if (_themeColor != AppPreferences.ThemeColor)
                {
                    _themeColor = AppPreferences.ThemeColor;
                    OnPropertyChanged(nameof(ThemeColor));
                }
                if (_language != AppPreferences.LanguageCode)
                {
                    _language = AppPreferences.LanguageCode;
                    OnPropertyChanged(nameof(Language));
                }
                OnPropertyChanged(nameof(OnlineText));
                OnPropertyChanged(nameof(ProfileSummary));
                OnPropertyChanged(nameof(NetworkSummary));
                foreach (var community in Communities) community.RefreshLocalizedText();
            }));
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            AppPreferences.Changed -= OnAppPreferencesChanged;
            _timer.Stop();
            _viewerTimer.Stop();
            _toastTimer.Stop();
            _voiceTimer.Stop();
            _callTimer.Stop();
            _viewOnceTimer.Stop();
            try { _mediaEngine?.Dispose(); } catch { }
            _audioPlayer.Dispose();
            _net.Dispose();
            Save(reportFailure: false);
        }
    }
}
