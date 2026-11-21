using System;
using System.Windows;
using System.Windows.Threading;

namespace ChatLocal
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            DataContextChanged += (s, e) =>
            {
                if (e.NewValue is MainViewModel vm)
                {
                    vm.ScrollRequested += () => Dispatcher.BeginInvoke(
                        new Action(() => MessagesScroll.ScrollToEnd()), DispatcherPriority.Background);

                    vm.FocusRequested += () => Dispatcher.BeginInvoke(
                        new Action(() => InputBox.Focus()), DispatcherPriority.Background);
                }
            };
        }

        private void SaveGeminiApiKey_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.ConfigureGeminiApiKey(GeminiApiKeyBox.Password);
                GeminiApiKeyBox.Clear();
            }
        }
    }
}
