using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace ChatLocal
{
    public static class GeminiApiKeyStore
    {
        private static readonly string KeyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChatLocal", "gemini-key.bin");
        private static readonly string ProviderPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChatLocal", "ai-provider.txt");
        private static readonly byte[] Entropy = System.Text.Encoding.UTF8.GetBytes("ChatLocal.Gemini.ApiKey.v1");

        public static bool HasKey => File.Exists(KeyPath);

        public static string Provider
        {
            get
            {
                try
                {
                    var saved = File.Exists(ProviderPath) ? File.ReadAllText(ProviderPath).Trim() : "";
                    if (saved == "Gemini" || saved == "OpenRouter") return saved;
                }
                catch { }
                return HasKey ? "Gemini" : "OpenRouter";
            }
        }

        public static void SaveProvider(string provider)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProviderPath));
            File.WriteAllText(ProviderPath, provider == "Gemini" ? "Gemini" : "OpenRouter");
        }

        public static void Save(string apiKey)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes((apiKey ?? "").Trim());
            var encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(KeyPath));
            File.WriteAllBytes(KeyPath, encrypted);
            Array.Clear(bytes, 0, bytes.Length);
        }

        public static string Read()
        {
            if (!File.Exists(KeyPath)) return "";
            try
            {
                var encrypted = File.ReadAllBytes(KeyPath);
                var bytes = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                var key = System.Text.Encoding.UTF8.GetString(bytes);
                Array.Clear(bytes, 0, bytes.Length);
                return key;
            }
            catch { return ""; }
        }

        public static void Delete()
        {
            if (File.Exists(KeyPath)) File.Delete(KeyPath);
        }
    }

    public static class UploadLimits
    {
        public const long MaxSourceFileBytes = 50L * 1024 * 1024;
        public const int MaxEncodedImageCharacters = 70_000_000;
        public static readonly TimeSpan TransferTimeout = TimeSpan.FromMinutes(2);
    }

    public class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }

    public class RelayCommand : ICommand
    {
        private readonly Action<object> _exec;
        private readonly Predicate<object> _can;

        public RelayCommand(Action<object> exec, Predicate<object> can = null)
        {
            _exec = exec;
            _can = can;
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object parameter) => _can == null || _can(parameter);
        public void Execute(object parameter) => _exec(parameter);
    }

    public static class AppPreferences
    {
        public static string LanguageCode { get; private set; } = "fr";
        public static string ThemeColor { get; private set; } = "Green";
        public static event Action Changed;
        private static readonly string PreferencesDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChatLocal");
        private static readonly string PreferencesPath = Path.Combine(PreferencesDirectory, "preferences.json");
        private static readonly object PreferencesLock = new object();
        private static FileSystemWatcher _watcher;
        private static bool _initialized;
        private static int _reloadQueued;

        private sealed class PreferencesData
        {
            public string ThemeColor { get; set; }
            public string Language { get; set; }
        }

        public static string Text(string french, string english) => LanguageCode == "en" ? english : french;

        public static void Initialize()
        {
            if (_initialized) return;
            lock (PreferencesLock)
            {
                if (_initialized) return;
                _initialized = true;
                try
                {
                    Directory.CreateDirectory(PreferencesDirectory);
                    ReadPreferencesFile();
                    _watcher = new FileSystemWatcher(PreferencesDirectory, Path.GetFileName(PreferencesPath))
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                        EnableRaisingEvents = true
                    };
                    _watcher.Changed += OnPreferencesFileChanged;
                    _watcher.Created += OnPreferencesFileChanged;
                    _watcher.Renamed += OnPreferencesFileChanged;
                }
                catch { }
                ApplyTheme(ThemeColor);
                ApplyLanguage(LanguageCode);
            }
        }

        public static void InitializeFromLegacy(string theme, string language)
        {
            Initialize();
            if (File.Exists(PreferencesPath)) return;
            lock (PreferencesLock)
            {
                if (File.Exists(PreferencesPath)) return;
                ThemeColor = NormalizeTheme(theme);
                LanguageCode = language == "en" ? "en" : "fr";
                ApplyTheme(ThemeColor);
                ApplyLanguage(LanguageCode);
                SavePreferencesFile();
            }
        }

        public static void SetTheme(string theme)
        {
            Initialize();
            ThemeColor = NormalizeTheme(theme);
            ApplyTheme(ThemeColor);
            SavePreferencesFile();
            Changed?.Invoke();
        }

        public static void SetLanguage(string language)
        {
            Initialize();
            LanguageCode = language == "en" ? "en" : "fr";
            ApplyLanguage(LanguageCode);
            SavePreferencesFile();
            Changed?.Invoke();
        }

        private static string NormalizeTheme(string theme) =>
            theme == "Indigo" || theme == "Green" || theme == "Blue" || theme == "Coral" ? theme : "Green";

        private static void SavePreferencesFile()
        {
            try
            {
                Directory.CreateDirectory(PreferencesDirectory);
                using (var mutex = new Mutex(false, @"Local\ChatLocal.GlobalPreferences"))
                {
                    bool acquired = false;
                    try
                    {
                        acquired = mutex.WaitOne(TimeSpan.FromSeconds(3));
                        if (!acquired) return;
                        var payload = JsonSerializer.Serialize(new PreferencesData { ThemeColor = ThemeColor, Language = LanguageCode }, new JsonSerializerOptions { WriteIndented = true });
                        string temp = PreferencesPath + ".tmp";
                        File.WriteAllText(temp, payload);
                        if (File.Exists(PreferencesPath)) File.Replace(temp, PreferencesPath, null, true);
                        else File.Move(temp, PreferencesPath);
                    }
                    finally { if (acquired) mutex.ReleaseMutex(); }
                }
            }
            catch { }
        }

        private static void OnPreferencesFileChanged(object sender, FileSystemEventArgs e)
        {
            if (Interlocked.Exchange(ref _reloadQueued, 1) != 0) return;
            _ = Task.Run(async () =>
            {
                await Task.Delay(120);
                try
                {
                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher == null) ReadExternalPreferences();
                    else _ = dispatcher.BeginInvoke(new Action(ReadExternalPreferences));
                }
                finally { Interlocked.Exchange(ref _reloadQueued, 0); }
            });
        }

        private static void ReadExternalPreferences()
        {
            string oldTheme = ThemeColor;
            string oldLanguage = LanguageCode;
            try { ReadPreferencesFile(); } catch { return; }
            if (ThemeColor == oldTheme && LanguageCode == oldLanguage) return;
            ApplyTheme(ThemeColor);
            ApplyLanguage(LanguageCode);
            Changed?.Invoke();
        }

        private static void ReadPreferencesFile()
        {
            if (!File.Exists(PreferencesPath)) return;
            var data = JsonSerializer.Deserialize<PreferencesData>(File.ReadAllText(PreferencesPath));
            if (data == null) return;
            ThemeColor = NormalizeTheme(data.ThemeColor);
            LanguageCode = data.Language == "en" ? "en" : "fr";
        }

        private static readonly (string Accent, string Soft, string Selected)[] ThemePalette =
        {
            ("#635BDB", "#40336B", "#302A4B"),
            ("#25D366", "#005C4B", "#2A2A2A"),
            ("#2F80ED", "#124D67", "#20374A"),
            ("#E76F51", "#613D36", "#49322E")
        };

        public static void ApplyTheme(string theme)
        {
            if (Application.Current == null) return;
            var colors = theme switch
            {
                "Green" => ThemePalette[1],
                "Blue" => ThemePalette[2],
                "Coral" => ThemePalette[3],
                _ => ThemePalette[0]
            };
            Application.Current.Resources["Accent"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors.Accent));
            Application.Current.Resources["BubbleOut"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors.Soft));
            Application.Current.Resources["Selected"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors.Selected));
        }

        public static void ApplyLanguage(string language)
        {
            LanguageCode = language == "en" ? "en" : "fr";
            if (Application.Current == null) return;
            var values = LanguageCode == "en" ? English : French;
            foreach (var item in values)
                Application.Current.Resources["UI." + item.Key] = item.Value;
        }

        private static readonly IReadOnlyDictionary<string, string> French = new Dictionary<string, string>
        {
            ["Chats"] = "Discussions", ["Status"] = "Statut", ["Archives"] = "Archives", ["Communities"] = "Communautés",
            ["Settings"] = "Paramètres", ["Search"] = "Rechercher ou démarrer une discussion", ["All"] = "Toutes", ["Unread"] = "Non lues",
            ["ArchiveTitle"] = "Archives", ["CommunityTitle"] = "Communautés", ["SettingsTitle"] = "Paramètres",
            ["NewCommunity"] = "Nouvelle communauté", ["Appearance"] = "Apparence",
            ["AddStatus"] = "Ajouter un statut", ["MyStatus"] = "Mon statut", ["PhotosVideos"] = "Photos et vidéos",
            ["PlainText"] = "Texte", ["Recents"] = "RÉCENTS", ["NoStatus"] = "Aucune mise à jour récente.",
            ["ChatDescription"] = "Envoyez des messages, photos, notes vocales, vues uniques et passez des appels audio et vidéo directement sur votre réseau local.",
            ["TapToView"] = "Appuyez pour afficher", ["NotDelivered"] = "Non remis : sera renvoyé", ["Emoji"] = "Insérer un emoji",
            ["SendPhoto"] = "Envoyer une photo", ["SendViewOnce"] = "Envoyer à vue unique (photo ou texte)",
            ["MessagePlaceholder"] = "Entrez un message", ["RecordVoice"] = "Enregistrer une note vocale", ["Send"] = "Envoyer",
            ["Recording"] = "Enregistrement vocal en cours...", ["CancelRecording"] = "Annuler l'enregistrement", ["VoiceNote"] = "Envoyer la note vocale",
            ["Emojis"] = "Emojis", ["StatusWelcome"] = "Partager des statuts", ["StatusDescription"] = "Partagez des photos et du texte qui disparaissent au bout de 24 heures.",
            ["WrittenStatus"] = "Statut écrit", ["Photo"] = "Photo", ["CancelAction"] = "Annuler", ["Publish"] = "Publier",
            ["EnterStatus"] = "Saisissez un statut", ["DeleteStatus"] = "Supprimer le statut", ["ReplyPrivate"] = "Répondre en privé",
            ["Back"] = "Retour", ["Close"] = "Fermer", ["TypeReply"] = "Taper une réponse…", ["You"] = "Vous",
            ["RejectCall"] = "Refuser l'appel", ["AnswerCall"] = "Décrocher", ["Microphone"] = "Activer/Couper le microphone",
            ["AudioCall"] = "Appel audio", ["VideoCall"] = "Appel vidéo",
            ["GreenTheme"] = "Vert", ["BlueTheme"] = "Bleu", ["CoralTheme"] = "Corail",
            ["Camera"] = "Activer/Couper la caméra", ["HangUp"] = "Raccrocher", ["ViewOnceTitle"] = "Message à vue unique",
            ["ViewOnceDescription"] = "Ce contenu ne peut être affiché qu'une seule fois.", ["ArchivedTooltip"] = "Discussion archivée",
            ["NoContacts"] = "Aucun contact détecté pour l'instant. Lancez l'application sur un autre ordinateur du même réseau.",
            ["ArchiveSubtitle"] = "Tes discussions mises de côté", ["NoArchives"] = "Aucune discussion archivée.",
            ["CommunitySubtitle"] = "Des groupes pour échanger ensemble", ["CreateCommunity"] = "Créer une communauté",
            ["NoCommunities"] = "Pas encore de communauté", ["NoCommunitiesHelp"] = "Crée un groupe et invite tes contacts.",
            ["CommunityHome"] = "Les conversations de groupe, au même endroit", ["CommunityHomeHelp"] = "Crée une communauté, choisis des membres et échange avec eux dans un fil commun.",
            ["Group"] = "Communauté", ["SettingsHelp"] = "Gère ton profil et les préférences de WhatsApp.",
            ["Profile"] = "Profil", ["ProfilePhoto"] = "Photo de profil", ["ChooseProfilePhoto"] = "Choisir une photo", ["RemoveProfilePhoto"] = "Supprimer la photo", ["Notifications"] = "Notifications", ["NotificationSounds"] = "Jouer un son pour les messages et appels entrants",
            ["Theme"] = "Couleur du thème", ["Language"] = "Langue de l’application", ["Network"] = "Réseau local",
            ["NetworkHelp"] = "Les profils doivent être connectés au même réseau local pour échanger.", ["SaveSettings"] = "Enregistrer les paramètres",
            ["CommunityName"] = "Nom de la communauté", ["ChooseMembers"] = "Choisis les contacts à inviter", ["Cancel"] = "Annuler", ["CreateAndInvite"] = "Créer et inviter"
        };

        private static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>
        {
            ["Chats"] = "Chats", ["Status"] = "Status", ["Archives"] = "Archived", ["Communities"] = "Communities",
            ["Settings"] = "Settings", ["Search"] = "Search or start a chat", ["All"] = "All", ["Unread"] = "Unread",
            ["ArchiveTitle"] = "Archived chats", ["CommunityTitle"] = "Communities", ["SettingsTitle"] = "Settings",
            ["NewCommunity"] = "New community", ["Appearance"] = "Appearance",
            ["AddStatus"] = "Add status", ["MyStatus"] = "My status", ["PhotosVideos"] = "Photos and videos",
            ["PlainText"] = "Text", ["Recents"] = "RECENT", ["NoStatus"] = "No recent updates.",
            ["ChatDescription"] = "Send messages, photos, voice notes, view-once messages, and make audio or video calls over your local network.",
            ["TapToView"] = "Tap to view", ["NotDelivered"] = "Not delivered: will retry", ["Emoji"] = "Insert emoji",
            ["SendPhoto"] = "Send a photo", ["SendViewOnce"] = "Send as view-once (photo or text)",
            ["MessagePlaceholder"] = "Enter a message", ["RecordVoice"] = "Record a voice note", ["Send"] = "Send",
            ["Recording"] = "Recording voice note...", ["CancelRecording"] = "Cancel recording", ["VoiceNote"] = "Send voice note",
            ["Emojis"] = "Emojis", ["StatusWelcome"] = "Share status updates", ["StatusDescription"] = "Share photos and text that disappear after 24 hours.",
            ["WrittenStatus"] = "Text status", ["Photo"] = "Photo", ["CancelAction"] = "Cancel", ["Publish"] = "Publish",
            ["EnterStatus"] = "Write a status", ["DeleteStatus"] = "Delete status", ["ReplyPrivate"] = "Reply privately",
            ["Back"] = "Back", ["Close"] = "Close", ["TypeReply"] = "Type a reply…", ["You"] = "You",
            ["RejectCall"] = "Decline call", ["AnswerCall"] = "Answer", ["Microphone"] = "Mute/unmute microphone",
            ["AudioCall"] = "Audio call", ["VideoCall"] = "Video call",
            ["GreenTheme"] = "Green", ["BlueTheme"] = "Blue", ["CoralTheme"] = "Coral",
            ["Camera"] = "Turn camera on/off", ["HangUp"] = "Hang up", ["ViewOnceTitle"] = "View-once message",
            ["ViewOnceDescription"] = "This content can only be viewed once.", ["ArchivedTooltip"] = "Archived conversation",
            ["NoContacts"] = "No contacts found yet. Start the app on another computer on the same network.",
            ["ArchiveSubtitle"] = "Conversations you have put away", ["NoArchives"] = "No archived conversations.",
            ["CommunitySubtitle"] = "Groups for sharing and talking", ["CreateCommunity"] = "Create a community",
            ["NoCommunities"] = "No communities yet", ["NoCommunitiesHelp"] = "Create a group and invite your contacts.",
            ["CommunityHome"] = "Group conversations, all in one place", ["CommunityHomeHelp"] = "Create a community, choose members, and chat together in one shared thread.",
            ["Group"] = "Community", ["SettingsHelp"] = "Manage your profile and WhatsApp preferences.",
            ["Profile"] = "Profile", ["ProfilePhoto"] = "Profile photo", ["ChooseProfilePhoto"] = "Choose a photo", ["RemoveProfilePhoto"] = "Remove photo", ["Notifications"] = "Notifications", ["NotificationSounds"] = "Play a sound for incoming messages and calls",
            ["Theme"] = "Theme color", ["Language"] = "App language", ["Network"] = "Local network",
            ["NetworkHelp"] = "Profiles must be connected to the same local network to exchange messages.", ["SaveSettings"] = "Save settings",
            ["CommunityName"] = "Community name", ["ChooseMembers"] = "Choose contacts to invite", ["Cancel"] = "Cancel", ["CreateAndInvite"] = "Create and invite"
        };
    }

    public class InverseBoolToVisConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool b = value is bool val && val;
            return b ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
