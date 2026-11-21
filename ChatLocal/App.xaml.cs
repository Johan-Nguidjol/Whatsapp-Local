using System.Windows;

namespace ChatLocal
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            AppPreferences.Initialize();
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            string name = null;
            string cliName = (e.Args != null && e.Args.Length > 0 && !string.IsNullOrWhiteSpace(e.Args[0])) ? e.Args[0].Trim() : null;
            
            {
                var login = new LoginWindow(cliName);
                if (login.ShowDialog() != true)
                {
                    Shutdown();
                    return;
                }
                name = login.UserName;
            }

            var vm = new MainViewModel(name.Trim());
            var win = new MainWindow { DataContext = vm };

            // Écran de démarrage : logo + barre de progression pendant 3 secondes
            new SplashWindow().ShowDialog();

            MainWindow = win;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            vm.SwitchAccountRequested += () => SwitchAccount(vm);
            win.Closed += (s, a) => vm.Dispose();
            win.Show();
        }

        private void SwitchAccount(MainViewModel previousViewModel)
        {
            var oldWindow = MainWindow;
            var login = new LoginWindow(null) { Owner = oldWindow };
            if (login.ShowDialog() != true) return;

            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var nextViewModel = new MainViewModel(login.UserName.Trim());
            var nextWindow = new MainWindow { DataContext = nextViewModel };
            nextViewModel.SwitchAccountRequested += () => SwitchAccount(nextViewModel);
            nextWindow.Closed += (s, e) => nextViewModel.Dispose();
            MainWindow = nextWindow;
            nextWindow.Show();
            oldWindow?.Close();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }
    }
}
