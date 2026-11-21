using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ChatLocal
{
    public partial class LoginWindow : Window
    {
        public string UserName { get; private set; }
        private string _pin = "";
        private string _firstPin;
        private string _pinFile;
        private bool _isConfirmingPin;

        private readonly SolidColorBrush _accentBrush;
        private readonly SolidColorBrush _emptyDotBrush;

        public LoginWindow(string initialName = null)
        {
            InitializeComponent();

            _accentBrush = (SolidColorBrush)FindResource("Accent");
            _emptyDotBrush = new SolidColorBrush(Color.FromRgb(42, 57, 66));

            Loaded += (s, e) =>
            {
                NameBox.Text = !string.IsNullOrWhiteSpace(initialName) ? initialName : Environment.UserName;
                UpdatePinFile();
                UpdateUiForUser();
                this.KeyDown += LoginWindow_KeyDown;
            };

            NameBox.TextChanged += (s, e) =>
            {
                _pin = "";
                _firstPin = null;
                _isConfirmingPin = false;
                PinBox.Clear();
                UpdateDots();
                UpdatePinFile();
                UpdateUiForUser();
            };
        }

        private void LoginWindow_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key >= System.Windows.Input.Key.D0 && e.Key <= System.Windows.Input.Key.D9)
            {
                AppendPinDigit((e.Key - System.Windows.Input.Key.D0).ToString());
                e.Handled = true;
            }
            else if (e.Key >= System.Windows.Input.Key.NumPad0 && e.Key <= System.Windows.Input.Key.NumPad9)
            {
                AppendPinDigit((e.Key - System.Windows.Input.Key.NumPad0).ToString());
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Back)
            {
                BackspacePin();
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Enter)
            {
                Submit_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }

        private void UpdatePinFile()
        {
            var n = (NameBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(n)) n = "user";
            var safe = string.Join("_", n.Split(Path.GetInvalidFileNameChars()));
            _pinFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChatLocal", safe, "pin.txt");
        }

        private void UpdateUiForUser()
        {
            if (PinPromptText == null || SubmitButton == null || string.IsNullOrEmpty(_pinFile)) return;
            bool hasPin = File.Exists(_pinFile);
            if (CreateModeRadio.IsChecked == true)
            {
                PinPromptText.Text = _isConfirmingPin
                    ? "Confirmez votre code PIN (4 chiffres)"
                    : hasPin ? "Ce compte existe déjà. Choisissez « Se connecter »." : "Choisissez un code PIN (4 chiffres)";
                SubmitButton.Content = "Créer le compte";
            }
            else
            {
                PinPromptText.Text = hasPin
                    ? "Entrez votre code PIN (4 chiffres)"
                    : "Aucun compte trouvé. Choisissez « Créer un compte ».";
                SubmitButton.Content = "Se connecter";
            }
            ErrorText.Text = "";
        }

        private void Mode_Changed(object sender, RoutedEventArgs e)
        {
            if (PinBox == null) return;
            _pin = "";
            _firstPin = null;
            _isConfirmingPin = false;
            PinBox.Clear();
            UpdateDots();
            UpdateUiForUser();
        }

        private void AppendPinDigit(string digit)
        {
            if (_pin.Length < 4)
            {
                _pin += digit;
                UpdateDots();
                ErrorText.Text = "";

                if (_pin.Length == 4)
                {
                    Dispatcher.BeginInvoke(new Action(() => Submit_Click(this, new RoutedEventArgs())), DispatcherPriority.Background);
                }
            }
        }

        private void BackspacePin()
        {
            if (_pin.Length > 0)
            {
                _pin = _pin.Substring(0, _pin.Length - 1);
                UpdateDots();
                ErrorText.Text = "";
            }
        }

        private void UpdateDots()
        {
            Dot1.Background = _pin.Length >= 1 ? _accentBrush : _emptyDotBrush;
            Dot2.Background = _pin.Length >= 2 ? _accentBrush : _emptyDotBrush;
            Dot3.Background = _pin.Length >= 3 ? _accentBrush : _emptyDotBrush;
            Dot4.Background = _pin.Length >= 4 ? _accentBrush : _emptyDotBrush;
        }

        private void Num_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Content is string digit)
            {
                AppendPinDigit(digit);
            }
        }

        private void Backspace_Click(object sender, RoutedEventArgs e)
        {
            BackspacePin();
        }

        private void Enter_Click(object sender, RoutedEventArgs e)
        {
            Submit_Click(sender, e);
        }

        private void PinBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            _pin = PinBox.Password;
            UpdateDots();
        }

        private void Submit_Click(object sender, RoutedEventArgs e)
        {
            var name = (NameBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                ErrorText.Text = "Veuillez entrer un nom d'utilisateur.";
                return;
            }

            UserName = name;
            UpdatePinFile();

            if (CreateModeRadio.IsChecked == true && File.Exists(_pinFile))
            {
                ErrorText.Text = "Ce nom possède déjà un compte. Connecte-toi ou choisis un autre nom.";
                _pin = "";
                _firstPin = null;
                _isConfirmingPin = false;
                UpdateDots();
                UpdateUiForUser();
                return;
            }

            if (_pin.Length < 4)
            {
                ErrorText.Text = "Le code PIN doit comporter 4 chiffres.";
                return;
            }

            if (CreateModeRadio.IsChecked == true)
            {
                if (!_isConfirmingPin)
                {
                    _firstPin = _pin;
                    _pin = "";
                    _isConfirmingPin = true;
                    UpdateDots();
                    PinPromptText.Text = "Confirmez votre code PIN (4 chiffres)";
                    ErrorText.Text = "";
                    return;
                }

                if (!string.Equals(_firstPin, _pin, StringComparison.Ordinal))
                {
                    ErrorText.Text = "Les codes PIN ne correspondent pas. Recommencez.";
                    _pin = "";
                    _firstPin = null;
                    _isConfirmingPin = false;
                    UpdateDots();
                    UpdateUiForUser();
                    return;
                }

                var salt = RandomNumberGenerator.GetBytes(16);
                var hash = Rfc2898DeriveBytes.Pbkdf2(_pin, salt, 150000, HashAlgorithmName.SHA256, 32);
                Directory.CreateDirectory(Path.GetDirectoryName(_pinFile)!);
                File.WriteAllLines(_pinFile, new[] { Convert.ToBase64String(salt), Convert.ToBase64String(hash) });
                DialogResult = true;
            }
            else if (File.Exists(_pinFile))
            {
                try
                {
                    var lines = File.ReadAllLines(_pinFile);
                    if (lines.Length >= 2 && Verify(_pin, lines[0], lines[1]))
                    {
                        DialogResult = true;
                        return;
                    }
                }
                catch { }

                ErrorText.Text = "Code PIN incorrect. Veuillez réessayer.";
                _pin = "";
                UpdateDots();
            }
            else
            {
                ErrorText.Text = "Aucun compte trouvé. Choisissez « Créer un compte ».";
                _pin = "";
                UpdateDots();
            }
        }

        private static bool Verify(string pin, string saltText, string hashText)
        {
            try
            {
                var salt = Convert.FromBase64String(saltText);
                var expected = Convert.FromBase64String(hashText);
                var actual = Rfc2898DeriveBytes.Pbkdf2(pin, salt, 150000, HashAlgorithmName.SHA256, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch { return false; }
        }
    }
}
