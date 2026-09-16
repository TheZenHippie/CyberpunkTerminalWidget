using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CyberpunkTerminalWidget
{
    public partial class FontPickerWindow : Window
    {
        private readonly List<FontFamily> _allFonts;
        public FontFamily SelectedFontFamily { get; private set; }
        public double SelectedFontSize { get; private set; }

        public FontPickerWindow(FontFamily currentFont, double currentSize, Brush fontBrush)
        {
            InitializeComponent();

            SelectedFontFamily = currentFont;
            SelectedFontSize = currentSize;

            PreviewTextBlock.Foreground = fontBrush;

            // Load all installed fonts on the system sorted alphabetically
            _allFonts = Fonts.SystemFontFamilies.OrderBy(f => f.Source).ToList();
            FontListBox.ItemsSource = _allFonts;

            // Select current font if found
            var currentMatch = _allFonts.FirstOrDefault(f => f.Source.Equals(currentFont.Source, StringComparison.OrdinalIgnoreCase));
            if (currentMatch != null)
            {
                FontListBox.SelectedItem = currentMatch;
                FontListBox.ScrollIntoView(currentMatch);
            }
            else
            {
                FontListBox.SelectedIndex = 0;
            }

            // Populate font sizes
            double[] sizes = { 8, 9, 10, 11, 12, 13, 14, 16, 18, 20, 22, 24, 28, 32, 36 };
            SizeListBox.ItemsSource = sizes;

            var sizeMatch = sizes.FirstOrDefault(s => Math.Abs(s - currentSize) < 0.1);
            if (sizeMatch > 0)
            {
                SizeListBox.SelectedItem = sizeMatch;
                SizeListBox.ScrollIntoView(sizeMatch);
            }
            else
            {
                SizeListBox.SelectedItem = 12.0;
            }

            UpdatePreview();

            // Keyboard shortcut support
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    DialogResult = false;
                    Close();
                }
                else if (e.Key == Key.Enter && !SearchBox.IsFocused)
                {
                    DialogResult = true;
                    Close();
                }
            };

            FontListBox.MouseDoubleClick += (s, e) =>
            {
                if (FontListBox.SelectedItem != null)
                {
                    DialogResult = true;
                    Close();
                }
            };

            SizeListBox.MouseDoubleClick += (s, e) =>
            {
                if (SizeListBox.SelectedItem != null)
                {
                    DialogResult = true;
                    Close();
                }
            };
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string filter = SearchBox.Text.Trim();
            if (string.IsNullOrEmpty(filter))
            {
                FontListBox.ItemsSource = _allFonts;
            }
            else
            {
                var filtered = _allFonts.Where(f => f.Source.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
                FontListBox.ItemsSource = filtered;
                if (filtered.Count > 0)
                {
                    FontListBox.SelectedIndex = 0;
                }
            }
        }

        private void FontListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FontListBox.SelectedItem is FontFamily selected)
            {
                SelectedFontFamily = selected;
                UpdatePreview();
            }
        }

        private void SizeListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SizeListBox.SelectedItem is double size)
            {
                SelectedFontSize = size;
                UpdatePreview();
            }
        }

        private void UpdatePreview()
        {
            if (PreviewTextBlock == null) return;
            PreviewTextBlock.FontFamily = SelectedFontFamily;
            PreviewTextBlock.FontSize = SelectedFontSize;
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}

