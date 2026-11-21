using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace ChatLocal
{
    public partial class SplashWindow : Window
    {
        public SplashWindow()
        {
            InitializeComponent();

            Loaded += (s, e) =>
            {
                // La barre se remplit en 3 secondes, puis la fenêtre se ferme
                var anim = new DoubleAnimation(0, 200, TimeSpan.FromSeconds(3));
                anim.Completed += (s2, e2) => Close();
                Fill.BeginAnimation(WidthProperty, anim);
            };
        }
    }
}
