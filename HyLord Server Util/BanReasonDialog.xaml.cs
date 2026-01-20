using System.Windows;

namespace HyLordServerUtil
{
    public partial class BanReasonDialog : Window
    {
        public string Reason => ReasonBox.Text.Trim();

        public BanReasonDialog(string playerName)
        {
            InitializeComponent();
            TitleText.Text = $"Ban {playerName}";
            ReasonBox.Focus();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Ban_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}
