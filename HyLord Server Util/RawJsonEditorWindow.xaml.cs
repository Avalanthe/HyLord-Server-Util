using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace HyLordServerUtil
{
    public partial class RawJsonEditorWindow : Window
    {
        public string JsonText => JsonBox.Text;

        public RawJsonEditorWindow(string titleKey, string initialJson)
        {
            InitializeComponent();
            Header.Text = $"Edit: {titleKey}";
            JsonBox.Text = initialJson ?? "";
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _ = JsonNode.Parse(JsonBox.Text);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Invalid JSON: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
