using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace HyLordServerUtil
{
    public static class TextHighlighter
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.RegisterAttached(
                "Text",
                typeof(string),
                typeof(TextHighlighter),
                new PropertyMetadata("", OnChanged));

        public static readonly DependencyProperty HighlightProperty =
            DependencyProperty.RegisterAttached(
                "Highlight",
                typeof(string),
                typeof(TextHighlighter),
                new PropertyMetadata("", OnChanged));

        public static string GetText(DependencyObject obj) => (string)obj.GetValue(TextProperty);
        public static void SetText(DependencyObject obj, string value) => obj.SetValue(TextProperty, value);

        public static string GetHighlight(DependencyObject obj) => (string)obj.GetValue(HighlightProperty);
        public static void SetHighlight(DependencyObject obj, string value) => obj.SetValue(HighlightProperty, value);

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TextBlock tb) return;

            var text = GetText(tb) ?? "";
            var query = GetHighlight(tb) ?? "";

            tb.Inlines.Clear();

            if (string.IsNullOrWhiteSpace(query))
            {
                tb.Inlines.Add(new Run(text));
                return;
            }

            var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                              .Distinct(StringComparer.OrdinalIgnoreCase)
                              .ToArray();

            if (tokens.Length == 0)
            {
                tb.Inlines.Add(new Run(text));
                return;
            }

            int i = 0;
            while (i < text.Length)
            {
                int bestIndex = -1;
                string? bestToken = null;

                foreach (var t in tokens)
                {
                    var idx = text.IndexOf(t, i, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0 && (bestIndex == -1 || idx < bestIndex))
                    {
                        bestIndex = idx;
                        bestToken = t;
                    }
                }

                if (bestIndex == -1 || bestToken == null)
                {
                    tb.Inlines.Add(new Run(text.Substring(i)));
                    break;
                }

                if (bestIndex > i)
                    tb.Inlines.Add(new Run(text.Substring(i, bestIndex - i)));

                var matchRun = new Run(text.Substring(bestIndex, bestToken.Length))
                {
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xCC, 0x66))
                };

                tb.Inlines.Add(matchRun);

                i = bestIndex + bestToken.Length;
            }
        }
    }
}
