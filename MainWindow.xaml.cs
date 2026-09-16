using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using CyberpunkTerminalWidget.Settings;
using CyberpunkTerminalWidget.Terminal;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;
using Path = System.IO.Path;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace CyberpunkTerminalWidget
{
    public partial class MainWindow : Window
    {
        // Cached pre-frozen solid color brushes for CRT glitches
        private static readonly Brush[] GlitchBrushes = CreateGlitchBrushes();

        // Shared static retro CRT static noise frames (256x256 frozen bitmaps, created lazily once)
        private static readonly List<WriteableBitmap> SharedSnowBitmaps = new List<WriteableBitmap>();
        private static readonly object SnowLock = new object();

        // Pre-frozen rainbow rotation animation
        private static readonly DoubleAnimation RainbowAnimation = CreateRainbowAnimation();

        // Persisted settings
        private readonly WidgetSettings _settings;

        // Terminal backend session
        private TerminalSession _terminalSession;

        // High-throughput output batching queue to prevent UI freezes on massive console streams
        private readonly ConcurrentQueue<(string text, bool isError)> _outputQueue = new ConcurrentQueue<(string text, bool isError)>();
        private int _isFlushScheduled = 0;

        // Visual opacity state
        private double _windowOpacity = 0.75;
        private double _fontOpacity = 1.0;

        // Font & styling state
        private FontFamily _currentFontFamily;
        private double _currentFontSize = 12.0;
        private Color _currentFontColor = Color.FromRgb(0, 255, 102); // Matrix Green
        private SolidColorBrush _currentFontBrush;

        // Effects state
        private bool _rainbowBorderEnabled = false;
        private double _borderWidth = 4.0;

        private bool _crtScanlinesEnabled = false;
        private double _scanlineThickness = 3.0;

        private bool _crtGlitchEnabled = false;
        private double _glitchChance = 15.0;
        private readonly DispatcherTimer _glitchTimer;

        private bool _crtSnowEnabled = false;
        private double _snowAmount = 25.0;
        private readonly DispatcherTimer _snowTimer;
        private int _snowFrameIndex = 0;

        private readonly DispatcherTimer _effectsCloseTimer;
        private readonly DispatcherTimer _saveDebounceTimer;
        private FrameworkElement? _subscribedPopupChild = null;

        private bool _isClosed = false;
        private bool _isInitialized = false;

        public MainWindow()
        {
            App.EnsureStandardMenuDropAlignment();

            // Load persisted settings before InitializeComponent so field initializers and sliders have access
            _settings = WidgetSettings.Load();

            InitializeComponent();

            // Restore font & color from settings
            try
            {
                _currentFontFamily = new FontFamily(_settings.FontFamily);
            }
            catch
            {
                _currentFontFamily = new FontFamily("Cascadia Code, Consolas, Courier New, monospace");
            }

            _currentFontSize = _settings.FontSize > 6 ? _settings.FontSize : 12.0;

            try
            {
                _currentFontColor = (Color)ColorConverter.ConvertFromString(_settings.FontColorHex);
            }
            catch
            {
                _currentFontColor = Color.FromRgb(0, 255, 102);
            }

            _currentFontBrush = new SolidColorBrush(_currentFontColor);
            _currentFontBrush.Freeze();

            // Restore visual state from settings
            _windowOpacity = Math.Clamp(_settings.WindowOpacity, 0.0, 1.0);
            _fontOpacity = Math.Clamp(_settings.FontOpacity, 0.1, 1.0);

            _rainbowBorderEnabled = _settings.RainbowBorderEnabled;
            _borderWidth = Math.Clamp(_settings.BorderWidth, 1.0, 20.0);

            _crtScanlinesEnabled = _settings.CrtScanlinesEnabled;
            _scanlineThickness = Math.Clamp(_settings.ScanlineThickness, 1.0, 20.0);

            _crtGlitchEnabled = _settings.CrtGlitchEnabled;
            _glitchChance = Math.Clamp(_settings.GlitchChance, 0.0, 100.0);

            _crtSnowEnabled = _settings.CrtSnowEnabled;
            _snowAmount = Math.Clamp(_settings.SnowAmount, 0.0, 100.0);

            // Set up CRT glitch timer (120ms tick rate)
            _glitchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _glitchTimer.Tick += GlitchTimer_Tick;

            // Set up CRT snow static animation timer (65ms tick rate, ~15 fps)
            _snowTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(65) };
            _snowTimer.Tick += SnowTimer_Tick;

            // Set up Effects submenu close delay timer (700ms)
            _effectsCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _effectsCloseTimer.Tick += EffectsCloseTimer_Tick;

            // Set up settings save debounce timer (400ms)
            _saveDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _saveDebounceTimer.Tick += (s, e) =>
            {
                _saveDebounceTimer.Stop();
                _settings?.Save();
            };

            // Initialize terminal session with restored shell type
            ShellType initialShell = _settings.Shell == "CommandPrompt" ? ShellType.CommandPrompt : ShellType.PowerShell;
            _terminalSession = new TerminalSession(initialShell);
            _terminalSession.OutputReceived += OnTerminalOutputReceived;
            _terminalSession.DirectoryChanged += OnTerminalDirectoryChanged;
            _terminalSession.ProcessExited += OnTerminalProcessExited;

            // Initialize UI elements
            ApplyFontAndColor();
            PopulateFontFamilies();
            UpdateScanlinesBrush(_scanlineThickness);

            // Restore window size
            if (_settings.WindowWidth.HasValue && _settings.WindowWidth.Value >= MinWidth)
            {
                Width = _settings.WindowWidth.Value;
            }
            if (_settings.WindowHeight.HasValue && _settings.WindowHeight.Value >= MinHeight)
            {
                Height = _settings.WindowHeight.Value;
            }

            // Restore window position across multi-monitor setups
            if (_settings.WindowLeft.HasValue && _settings.WindowTop.HasValue)
            {
                double vLeft = SystemParameters.VirtualScreenLeft;
                double vTop = SystemParameters.VirtualScreenTop;
                double vWidth = SystemParameters.VirtualScreenWidth;
                double vHeight = SystemParameters.VirtualScreenHeight;

                double left = _settings.WindowLeft.Value;
                double top = _settings.WindowTop.Value;

                if (left >= vLeft - 20 && left < vLeft + vWidth - 100 &&
                    top >= vTop - 20 && top < vTop + vHeight - 100)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = left;
                    Top = top;
                }
            }

            // Restore window options
            Topmost = _settings.AlwaysOnTop;
            AlwaysOnTopMenuItem.IsChecked = _settings.AlwaysOnTop;
            ShadowMenuItem.IsChecked = _settings.WindowShadow;
            WindowDropShadow.Opacity = _settings.WindowShadow ? 0.35 : 0.0;

            // Apply restored sliders and toggles
            WindowOpacitySlider.Value = _windowOpacity;
            FontOpacitySlider.Value = _fontOpacity;
            BorderWidthSlider.Value = _borderWidth;
            ScanlineThicknessSlider.Value = _scanlineThickness;
            GlitchChanceSlider.Value = _glitchChance;
            SnowAmountSlider.Value = _snowAmount;

            RainbowBorderMenuItem.IsChecked = _rainbowBorderEnabled;
            CrtScanlinesMenuItem.IsChecked = _crtScanlinesEnabled;
            CrtGlitchMenuItem.IsChecked = _crtGlitchEnabled;
            CrtSnowMenuItem.IsChecked = _crtSnowEnabled;

            ShellPowerShellMenuItem.IsChecked = (initialShell == ShellType.PowerShell);
            ShellCmdMenuItem.IsChecked = (initialShell == ShellType.CommandPrompt);

            // Apply shell badge and prompt text
            UpdatePromptDirectory(_terminalSession.WorkingDirectory);

            // Restore background image if saved
            if (!string.IsNullOrEmpty(_settings.BackgroundImagePath) && File.Exists(_settings.BackgroundImagePath))
            {
                LoadBackgroundImage(_settings.BackgroundImagePath);
            }

            // Apply effects state
            ApplyEffectsState();

            // Display welcome message
            AppendWelcomeBanner();

            // Register system display changes
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            // Apply initial visual opacity to window layers
            if (WindowBackgroundBorder != null) WindowBackgroundBorder.Opacity = _windowOpacity;
            if (BackgroundImageBorder != null) BackgroundImageBorder.Opacity = _windowOpacity;
            if (TerminalContentGrid != null) TerminalContentGrid.Opacity = _fontOpacity;
            if (WindowOpacityValueText != null) WindowOpacityValueText.Text = $"{Math.Round(_windowOpacity * 100)}%";
            if (FontOpacityValueText != null) FontOpacityValueText.Text = $"{Math.Round(_fontOpacity * 100)}%";

            _isInitialized = true;

            Loaded += (s, e) =>
            {
                InputTextBox.Focus();
                UpdateBackgroundClip();
            };
        }

        private static DoubleAnimation CreateRainbowAnimation()
        {
            var anim = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = new Duration(TimeSpan.FromSeconds(5)),
                RepeatBehavior = RepeatBehavior.Forever
            };
            anim.Freeze();
            return anim;
        }

        private static Brush[] CreateGlitchBrushes()
        {
            Color[] colors =
            {
                Color.FromArgb(220, 0, 240, 255),   // Neon cyan
                Color.FromArgb(220, 255, 0, 127),   // Neon magenta
                Color.FromArgb(240, 255, 255, 255), // Phosphor white
                Color.FromArgb(235, 0, 0, 0),       // Deep horizontal scan dropout
                Color.FromArgb(210, 0, 255, 102),   // Neon lime
                Color.FromArgb(200, 255, 230, 0)    // Cyber yellow
            };

            var brushes = new Brush[colors.Length];
            for (int i = 0; i < colors.Length; i++)
            {
                var brush = new SolidColorBrush(colors[i]);
                brush.Freeze();
                brushes[i] = brush;
            }
            return brushes;
        }

        private static List<WriteableBitmap> GetOrCreateSnowBitmaps()
        {
            if (SharedSnowBitmaps.Count == 8)
            {
                return SharedSnowBitmaps;
            }

            lock (SnowLock)
            {
                if (SharedSnowBitmaps.Count == 8)
                {
                    return SharedSnowBitmaps;
                }

                int w = 256, h = 256;
                for (int f = 0; f < 8; f++)
                {
                    var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
                    int stride = w * 4;
                    byte[] pixels = new byte[stride * h];
                    for (int i = 0; i < pixels.Length; i += 4)
                    {
                        byte val = (byte)Random.Shared.Next(256);
                        bool colorSpeck = Random.Shared.Next(30) == 0;
                        pixels[i] = colorSpeck ? (byte)Random.Shared.Next(256) : val;     // B
                        pixels[i + 1] = colorSpeck ? (byte)Random.Shared.Next(256) : val; // G
                        pixels[i + 2] = colorSpeck ? (byte)Random.Shared.Next(256) : val; // R
                        pixels[i + 3] = 255;
                    }
                    wb.WritePixels(new Int32Rect(0, 0, w, h), pixels, stride, 0);
                    wb.Freeze();
                    SharedSnowBitmaps.Add(wb);
                }
                return SharedSnowBitmaps;
            }
        }

        private void AppendWelcomeBanner()
        {
            var p = new Paragraph();
            var runHeader = new Run("┌────────────────────────────────────────────────────────┐\n│  CYBERPUNK TERMINAL HUD v1.0 • WINDOWS CLI/POWERSHELL  │\n└────────────────────────────────────────────────────────┘\n")
            {
                Foreground = _currentFontBrush,
                FontWeight = FontWeights.Bold
            };
            var runInfo = new Run($"Session: {_terminalSession.CurrentShell} | Directory: {_terminalSession.WorkingDirectory}\nType commands below and press [Enter]. Right-click for HUD options.\n\n")
            {
                Foreground = new SolidColorBrush(Color.FromArgb(180, _currentFontColor.R, _currentFontColor.G, _currentFontColor.B))
            };
            p.Inlines.Add(runHeader);
            p.Inlines.Add(runInfo);
            TerminalFlowDocument.Blocks.Add(p);
        }

        #region Terminal I/O & High-Throughput Output Batching

        private void OnTerminalOutputReceived(string line, bool isError)
        {
            if (_isClosed) return;
            _outputQueue.Enqueue((line, isError));
            ScheduleFlushOutput();
        }

        private void ScheduleFlushOutput()
        {
            if (Interlocked.CompareExchange(ref _isFlushScheduled, 1, 0) == 0)
            {
                Dispatcher.InvokeAsync(FlushOutputQueue, DispatcherPriority.Normal);
            }
        }

        private void FlushOutputQueue()
        {
            Interlocked.Exchange(ref _isFlushScheduled, 0);
            if (_isClosed) return;

            int processedCount = 0;
            // Drain in chunks up to 100 per UI frame to maintain smooth 60fps rendering
            while (processedCount < 100 && _outputQueue.TryDequeue(out var item))
            {
                AppendOutputLine(item.text, item.isError);
                processedCount++;
            }

            if (processedCount > 0)
            {
                // Cap block history to prevent memory growth
                while (TerminalFlowDocument.Blocks.Count > 1200)
                {
                    TerminalFlowDocument.Blocks.Remove(TerminalFlowDocument.Blocks.FirstBlock);
                }

                TerminalOutputBox.ScrollToEnd();
            }

            // Schedule next frame if items remain in queue
            if (!_outputQueue.IsEmpty && !_isClosed)
            {
                ScheduleFlushOutput();
            }
        }

        private void OnTerminalDirectoryChanged(string newDirectory)
        {
            if (_isClosed) return;

            Dispatcher.InvokeAsync(() =>
            {
                if (_isClosed) return;
                UpdatePromptDirectory(newDirectory);
            });
        }

        private void UpdatePromptDirectory(string directory)
        {
            string shellPrefix = _terminalSession.CurrentShell == ShellType.PowerShell ? "PS " : "";
            PromptPrefixTextBlock.Text = $"{shellPrefix}{directory} > ";
            StatusText.Text = directory;
        }

        private void AppendOutputLine(string text, bool isError)
        {
            var p = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };

            if (isError)
            {
                var errorBrush = new SolidColorBrush(Color.FromRgb(255, 50, 80));
                errorBrush.Freeze();
                p.Inlines.Add(new Run(text) { Foreground = errorBrush });
            }
            else
            {
                var segments = AnsiColorParser.Parse(text, _currentFontColor);
                if (segments.Count == 0)
                {
                    p.Inlines.Add(new Run(text) { Foreground = _currentFontBrush });
                }
                else
                {
                    foreach (var seg in segments)
                    {
                        var brush = seg.Color.HasValue
                            ? new SolidColorBrush(seg.Color.Value)
                            : _currentFontBrush;
                        var run = new Run(seg.Text)
                        {
                            Foreground = brush,
                            FontWeight = seg.IsBold ? FontWeights.Bold : FontWeights.Normal
                        };
                        p.Inlines.Add(run);
                    }
                }
            }

            TerminalFlowDocument.Blocks.Add(p);
        }

        private void OnTerminalProcessExited(int exitCode)
        {
            if (_isClosed) return;
            Dispatcher.InvokeAsync(() =>
            {
                if (_isClosed) return;
                AppendOutputLine($"\n[Active shell process terminated (code {exitCode}). Re-initializing session...]", true);
                _terminalSession.RestartSession();
            });
        }

        private void TerminalOutputBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Text))
            {
                InputTextBox.Focus();
                InputTextBox.Text += e.Text;
                InputTextBox.CaretIndex = InputTextBox.Text.Length;
                e.Handled = true;
            }
        }

        private void InputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                string command = InputTextBox.Text;
                InputTextBox.Text = string.Empty;

                // Echo user input to log feed with current directory prompt
                var p = new Paragraph { Margin = new Thickness(0, 4, 0, 1) };
                var promptRun = new Run(PromptPrefixTextBlock.Text)
                {
                    Foreground = _currentFontBrush,
                    FontWeight = FontWeights.Bold
                };
                var cmdRun = new Run(command)
                {
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.SemiBold
                };
                p.Inlines.Add(promptRun);
                p.Inlines.Add(cmdRun);
                TerminalFlowDocument.Blocks.Add(p);

                // Handle local clear commands
                if (command.Trim().Equals("cls", StringComparison.OrdinalIgnoreCase) ||
                    command.Trim().Equals("clear", StringComparison.OrdinalIgnoreCase))
                {
                    ClearTerminal();
                    return;
                }

                _terminalSession.SendCommand(command);
                TerminalOutputBox.ScrollToEnd();
            }
            else if (e.Key == Key.Up)
            {
                e.Handled = true;
                string? prev = _terminalSession.GetPreviousHistory(InputTextBox.Text);
                if (prev != null)
                {
                    InputTextBox.Text = prev;
                    InputTextBox.CaretIndex = InputTextBox.Text.Length;
                }
            }
            else if (e.Key == Key.Down)
            {
                e.Handled = true;
                string? next = _terminalSession.GetNextHistory();
                if (next != null)
                {
                    InputTextBox.Text = next;
                    InputTextBox.CaretIndex = InputTextBox.Text.Length;
                }
            }
            else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                if (string.IsNullOrEmpty(InputTextBox.SelectedText))
                {
                    e.Handled = true;
                    _terminalSession.SendBreak();
                }
            }
        }

        private void ClearTerminal()
        {
            TerminalFlowDocument.Blocks.Clear();
        }

        private void ClearTerminal_Click(object sender, RoutedEventArgs e)
        {
            ClearTerminal();
        }

        private void RestartShell_Click(object sender, RoutedEventArgs e)
        {
            _terminalSession.SendBreak();
        }

        #endregion

        #region Shell Switching

        private void ShellPowerShell_Click(object sender, RoutedEventArgs e)
        {
            ShellPowerShellMenuItem.IsChecked = true;
            ShellCmdMenuItem.IsChecked = false;
            SwitchShell(ShellType.PowerShell);
        }

        private void ShellCmd_Click(object sender, RoutedEventArgs e)
        {
            ShellPowerShellMenuItem.IsChecked = false;
            ShellCmdMenuItem.IsChecked = true;
            SwitchShell(ShellType.CommandPrompt);
        }

        private void SwitchShell(ShellType shellType)
        {
            _terminalSession.SwitchShell(shellType);
            if (shellType == ShellType.PowerShell)
            {
                ShellBadgeText.Text = "[ POWERSHELL ]";
                _settings.Shell = "PowerShell";
            }
            else
            {
                ShellBadgeText.Text = "[ CMD ]";
                _settings.Shell = "CommandPrompt";
            }
            UpdatePromptDirectory(_terminalSession.WorkingDirectory);
            AppendOutputLine($"\n[Switched active shell to {shellType}]", false);
            SaveCurrentSettings();
        }

        #endregion

        #region Dual Transparency Sliders

        private void WindowOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isInitialized || _settings == null) return;

            _windowOpacity = e.NewValue;
            _settings.WindowOpacity = _windowOpacity;

            if (WindowOpacityValueText != null)
            {
                WindowOpacityValueText.Text = $"{Math.Round(_windowOpacity * 100)}%";
            }

            // Apply opacity to background glass border and background image
            if (WindowBackgroundBorder != null)
            {
                WindowBackgroundBorder.Opacity = _windowOpacity;
            }
            if (BackgroundImageBorder != null)
            {
                BackgroundImageBorder.Opacity = _windowOpacity;
            }

            // Modulate prompt bar background tint so that at 0% opacity the window chrome is fully invisible
            if (PromptBarBorder != null)
            {
                byte promptBgAlpha = (byte)Math.Clamp((int)(0x18 * _windowOpacity), 0, 255);
                byte promptBorderAlpha = (byte)Math.Clamp((int)(0x25 * _windowOpacity), 0, 255);
                PromptBarBorder.Background = new SolidColorBrush(Color.FromArgb(promptBgAlpha, 0, 0, 0));
                PromptBarBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(promptBorderAlpha, 255, 255, 255));
            }

            SaveCurrentSettings(false);
        }

        private void FontOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isInitialized || _settings == null) return;

            _fontOpacity = e.NewValue;
            _settings.FontOpacity = _fontOpacity;

            if (FontOpacityValueText != null)
            {
                FontOpacityValueText.Text = $"{Math.Round(_fontOpacity * 100)}%";
            }

            // Apply opacity independently to terminal content (text, prompt, and header)
            if (TerminalContentGrid != null)
            {
                TerminalContentGrid.Opacity = _fontOpacity;
            }

            SaveCurrentSettings(false);
        }

        #endregion

        #region Font & Color Selection

        private void PopulateFontFamilies()
        {
            // Keep the first item ("Choose Font...") and separator, clear previous dynamic items
            while (FontFamilyMenuItem.Items.Count > 2)
            {
                FontFamilyMenuItem.Items.RemoveAt(2);
            }

            string[] candidateFonts =
            {
                "Cascadia Code",
                "Cascadia Mono",
                "Consolas",
                "Lucida Console",
                "Courier New",
                "Fira Code",
                "JetBrains Mono",
                "Segoe UI Mono"
            };

            var installedFamilies = Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var fontName in candidateFonts)
            {
                if (installedFamilies.Contains(fontName) || fontName == "Consolas" || fontName == "Courier New")
                {
                    var item = new MenuItem
                    {
                        Header = fontName,
                        Tag = fontName,
                        IsCheckable = true,
                        IsChecked = _currentFontFamily.Source.Contains(fontName, StringComparison.OrdinalIgnoreCase)
                    };
                    item.Click += FontFamily_Click;
                    FontFamilyMenuItem.Items.Add(item);
                }
            }
        }

        private void ChooseAllFonts_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FontPickerWindow(_currentFontFamily, _currentFontSize, _currentFontBrush)
            {
                Owner = this
            };

            if (picker.ShowDialog() == true)
            {
                _currentFontFamily = picker.SelectedFontFamily;
                _currentFontSize = picker.SelectedFontSize;
                _settings.FontFamily = _currentFontFamily.Source;
                _settings.FontSize = _currentFontSize;

                ApplyFontAndColor();
                PopulateFontFamilies();
                UpdateFontSizeMenuChecks();
                SaveCurrentSettings();
            }
        }

        private void FontFamily_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is string fontName)
            {
                foreach (var item in FontFamilyMenuItem.Items.OfType<MenuItem>().Where(i => i.Tag != null))
                {
                    item.IsChecked = (item == menuItem);
                }

                _currentFontFamily = new FontFamily(fontName);
                _settings.FontFamily = fontName;
                ApplyFontAndColor();
                SaveCurrentSettings();
            }
        }

        private void FontSize_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is string sizeStr && double.TryParse(sizeStr, out double size))
            {
                _currentFontSize = size;
                _settings.FontSize = size;
                UpdateFontSizeMenuChecks();
                ApplyFontAndColor();
                SaveCurrentSettings();
            }
        }

        private void UpdateFontSizeMenuChecks()
        {
            foreach (var item in FontSizeMenuItem.Items.OfType<MenuItem>())
            {
                if (item.Tag is string tagStr && double.TryParse(tagStr, out double s))
                {
                    item.IsChecked = Math.Abs(s - _currentFontSize) < 0.1;
                }
            }
        }

        private void FontColor_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is string colorHex)
            {
                UpdateFontColorMenuChecks(menuItem);

                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(colorHex);
                    SetTerminalFontColor(color, colorHex);
                }
                catch { }
            }
        }

        private void UpdateFontColorMenuChecks(MenuItem activeItem)
        {
            foreach (var item in FontColorMenuItem.Items.OfType<MenuItem>())
            {
                item.IsChecked = (item == activeItem);
            }
        }

        private void CustomFontColor_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Window
            {
                Title = "Select Custom Color",
                Width = 320,
                Height = 160,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(20, 24, 35)),
                Foreground = Brushes.White
            };

            var sp = new StackPanel { Margin = new Thickness(16) };
            sp.Children.Add(new TextBlock
            {
                Text = "Enter Hex Color Code (e.g. #00F0FF, #FF007F):",
                Foreground = Brushes.WhiteSmoke,
                Margin = new Thickness(0, 0, 0, 8)
            });

            var tb = new TextBox
            {
                Text = $"#{_currentFontColor.R:X2}{_currentFontColor.G:X2}{_currentFontColor.B:X2}",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 14,
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 12)
            };
            sp.Children.Add(tb);

            var btnOk = new Button
            {
                Content = "Apply Color",
                Width = 100,
                Height = 28,
                HorizontalAlignment = HorizontalAlignment.Right,
                IsDefault = true
            };
            btnOk.Click += (s, args) =>
            {
                try
                {
                    string hex = tb.Text.Trim();
                    if (!hex.StartsWith("#")) hex = "#" + hex;
                    var color = (Color)ColorConverter.ConvertFromString(hex);
                    SetTerminalFontColor(color, hex);
                    dialog.DialogResult = true;
                    dialog.Close();
                }
                catch
                {
                    MessageBox.Show("Please enter a valid hex color format (#RRGGBB).", "Invalid Color", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            sp.Children.Add(btnOk);

            dialog.Content = sp;
            dialog.ShowDialog();
        }

        private void SetTerminalFontColor(Color color, string hexCode)
        {
            _currentFontColor = color;
            _settings.FontColorHex = hexCode;

            _currentFontBrush = new SolidColorBrush(color);
            _currentFontBrush.Freeze();

            ApplyFontAndColor();
            SaveCurrentSettings();
        }

        private void ApplyFontAndColor()
        {
            TerminalOutputBox.FontFamily = _currentFontFamily;
            TerminalOutputBox.FontSize = _currentFontSize;
            TerminalOutputBox.Foreground = _currentFontBrush;
            TerminalOutputBox.CaretBrush = _currentFontBrush;

            InputTextBox.FontFamily = _currentFontFamily;
            InputTextBox.FontSize = _currentFontSize;
            InputTextBox.Foreground = _currentFontBrush;
            InputTextBox.CaretBrush = _currentFontBrush;

            PromptPrefixTextBlock.FontFamily = _currentFontFamily;
            PromptPrefixTextBlock.FontSize = _currentFontSize;
            PromptPrefixTextBlock.Foreground = _currentFontBrush;

            ShellBadgeText.Foreground = _currentFontBrush;
            ShellBadgeBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(120, _currentFontColor.R, _currentFontColor.G, _currentFontColor.B));
            ShellBadgeBorder.Background = Brushes.Transparent;

            // Minimalist scrollbar thumb indicator tint
            var thumbColor = Color.FromArgb(100, _currentFontColor.R, _currentFontColor.G, _currentFontColor.B);
            var thumbBrush = new SolidColorBrush(thumbColor);
            thumbBrush.Freeze();
            TerminalOutputBox.Resources["ScrollThumbBrush"] = thumbBrush;
        }

        #endregion

        #region Background Image Support

        private void SelectBackgroundImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select Terminal Background Image",
                Filter = "Image Files (*.png;*.jpg;*.jpeg;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All Files (*.*)|*.*",
                CheckFileExists = true
            };

            if (dlg.ShowDialog(this) == true)
            {
                LoadBackgroundImage(dlg.FileName);
            }
        }

        private void LoadBackgroundImage(string filePath)
        {
            try
            {
                var bitmap = new BitmapImage();
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                }
                bitmap.Freeze();

                BackgroundImageElement.Source = bitmap;
                BackgroundImageElement.Visibility = Visibility.Visible;
                ClearBackgroundImageMenuItem.IsEnabled = true;

                _settings.BackgroundImagePath = filePath;
                SaveCurrentSettings();
                UpdateBackgroundClip();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load background image: {ex.Message}", "Image Load Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ClearBackgroundImage_Click(object sender, RoutedEventArgs e)
        {
            BackgroundImageElement.Source = null;
            BackgroundImageElement.Visibility = Visibility.Collapsed;
            ClearBackgroundImageMenuItem.IsEnabled = false;

            _settings.BackgroundImagePath = null;
            SaveCurrentSettings();
        }

        private void UpdateBackgroundClip()
        {
            if (BackgroundImageBorder == null) return;

            double width = BackgroundImageBorder.ActualWidth;
            double height = BackgroundImageBorder.ActualHeight;

            if (width > 0 && height > 0)
            {
                var clip = new RectangleGeometry
                {
                    Rect = new Rect(0, 0, width, height),
                    RadiusX = 8,
                    RadiusY = 8
                };
                clip.Freeze();
                BackgroundImageBorder.Clip = clip;
            }
        }

        #endregion

        #region Visual Effects Suite (Rainbow, Scanlines, Glitch, Snow)

        private void ApplyEffectsState()
        {
            // Rainbow border
            if (_rainbowBorderEnabled)
            {
                CyberpunkRainbowBorder.BorderThickness = new Thickness(_borderWidth);
                CyberpunkRainbowBorder.Visibility = Visibility.Visible;
                RainbowRotateTransform.BeginAnimation(RotateTransform.AngleProperty, RainbowAnimation);
            }
            else
            {
                CyberpunkRainbowBorder.Visibility = Visibility.Collapsed;
                RainbowRotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);
            }

            // Scanlines
            CrtScanlinesOverlay.Visibility = _crtScanlinesEnabled ? Visibility.Visible : Visibility.Collapsed;
            if (_crtScanlinesEnabled)
            {
                UpdateScanlinesBrush(_scanlineThickness);
            }

            // Glitch
            CrtGlitchCanvas.Visibility = _crtGlitchEnabled ? Visibility.Visible : Visibility.Collapsed;
            if (_crtGlitchEnabled)
            {
                _glitchTimer.Start();
            }
            else
            {
                _glitchTimer.Stop();
            }

            // Snow
            CrtSnowOverlay.Visibility = _crtSnowEnabled ? Visibility.Visible : Visibility.Collapsed;
            if (_crtSnowEnabled)
            {
                CrtSnowOverlay.Opacity = (_snowAmount / 100.0) * 0.45;
                _snowTimer.Start();
            }
            else
            {
                _snowTimer.Stop();
            }
        }

        private void RainbowBorder_Click(object sender, RoutedEventArgs e)
        {
            _rainbowBorderEnabled = RainbowBorderMenuItem.IsChecked;
            _settings.RainbowBorderEnabled = _rainbowBorderEnabled;

            if (_rainbowBorderEnabled)
            {
                CyberpunkRainbowBorder.BorderThickness = new Thickness(_borderWidth);
                CyberpunkRainbowBorder.Visibility = Visibility.Visible;
                RainbowRotateTransform.BeginAnimation(RotateTransform.AngleProperty, RainbowAnimation);
            }
            else
            {
                CyberpunkRainbowBorder.Visibility = Visibility.Collapsed;
                RainbowRotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);
            }

            SaveCurrentSettings();
        }

        private void BorderWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isInitialized || _settings == null) return;

            _borderWidth = e.NewValue;
            _settings.BorderWidth = _borderWidth;

            if (BorderWidthValueText != null)
            {
                BorderWidthValueText.Text = $"{Math.Round(_borderWidth)}px";
            }
            if (_rainbowBorderEnabled && CyberpunkRainbowBorder != null)
            {
                CyberpunkRainbowBorder.BorderThickness = new Thickness(_borderWidth);
            }

            SaveCurrentSettings();
        }

        private void CrtScanlines_Click(object sender, RoutedEventArgs e)
        {
            _crtScanlinesEnabled = CrtScanlinesMenuItem.IsChecked;
            _settings.CrtScanlinesEnabled = _crtScanlinesEnabled;

            CrtScanlinesOverlay.Visibility = _crtScanlinesEnabled ? Visibility.Visible : Visibility.Collapsed;
            if (_crtScanlinesEnabled)
            {
                UpdateScanlinesBrush(_scanlineThickness);
            }

            SaveCurrentSettings();
        }

        private void ScanlineThicknessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isInitialized || _settings == null) return;

            _scanlineThickness = e.NewValue;
            _settings.ScanlineThickness = _scanlineThickness;

            if (ScanlineThicknessValueText != null)
            {
                ScanlineThicknessValueText.Text = $"{Math.Round(_scanlineThickness)}px";
            }
            if (_crtScanlinesEnabled)
            {
                UpdateScanlinesBrush(_scanlineThickness);
            }

            SaveCurrentSettings();
        }

        private void UpdateScanlinesBrush(double thickness)
        {
            double totalHeight = Math.Max(2.0, thickness * 2.0);
            CrtDrawingBrush.Viewport = new Rect(0, 0, 1, totalHeight);

            var drawingGroup = new DrawingGroup();
            var transparentRect = new RectangleGeometry(new Rect(0, 0, 1, totalHeight));
            transparentRect.Freeze();
            drawingGroup.Children.Add(new GeometryDrawing(Brushes.Transparent, null, transparentRect));

            var scanlineColor = Color.FromArgb(240, 0, 0, 0);
            var scanlineBrush = new SolidColorBrush(scanlineColor);
            scanlineBrush.Freeze();

            var scanlineRect = new RectangleGeometry(new Rect(0, 0, 1, thickness));
            scanlineRect.Freeze();
            drawingGroup.Children.Add(new GeometryDrawing(scanlineBrush, null, scanlineRect));

            drawingGroup.Freeze();
            CrtDrawingBrush.Drawing = drawingGroup;
        }

        private void CrtGlitch_Click(object sender, RoutedEventArgs e)
        {
            _crtGlitchEnabled = CrtGlitchMenuItem.IsChecked;
            _settings.CrtGlitchEnabled = _crtGlitchEnabled;

            CrtGlitchCanvas.Visibility = _crtGlitchEnabled ? Visibility.Visible : Visibility.Collapsed;

            if (_crtGlitchEnabled)
            {
                _glitchTimer.Start();
            }
            else
            {
                _glitchTimer.Stop();
                CrtGlitchCanvas.Children.Clear();
                ContentJitterTransform.X = 0;
                ContentJitterTransform.Y = 0;
            }

            SaveCurrentSettings();
        }

        private void GlitchChanceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isInitialized || _settings == null) return;

            _glitchChance = e.NewValue;
            _settings.GlitchChance = _glitchChance;

            if (GlitchChanceValueText != null)
            {
                GlitchChanceValueText.Text = $"{Math.Round(_glitchChance)}%";
            }

            SaveCurrentSettings();
        }

        private void GlitchTimer_Tick(object? sender, EventArgs e)
        {
            if (!_crtGlitchEnabled || _isClosed) return;

            CrtGlitchCanvas.Children.Clear();

            if (Random.Shared.Next(100) < _glitchChance)
            {
                int sliceCount = Random.Shared.Next(2, 6);
                double w = CrtGlitchCanvas.ActualWidth;
                double h = CrtGlitchCanvas.ActualHeight;

                if (w > 20 && h > 20)
                {
                    for (int i = 0; i < sliceCount; i++)
                    {
                        var rect = new Rectangle
                        {
                            Width = Random.Shared.Next(30, (int)w),
                            Height = Random.Shared.Next(2, 8),
                            Fill = GlitchBrushes[Random.Shared.Next(GlitchBrushes.Length)],
                            Opacity = Random.Shared.NextDouble() * 0.7 + 0.3
                        };

                        Canvas.SetLeft(rect, Random.Shared.Next(-10, (int)w - 20));
                        Canvas.SetTop(rect, Random.Shared.Next(0, (int)h - 8));
                        CrtGlitchCanvas.Children.Add(rect);
                    }

                    // Content jitter displacement
                    ContentJitterTransform.X = (Random.Shared.NextDouble() * 6.0) - 3.0;
                    ContentJitterTransform.Y = (Random.Shared.NextDouble() * 4.0) - 2.0;
                }
            }
            else
            {
                ContentJitterTransform.X = 0;
                ContentJitterTransform.Y = 0;
            }
        }

        private void CrtSnow_Click(object sender, RoutedEventArgs e)
        {
            _crtSnowEnabled = CrtSnowMenuItem.IsChecked;
            _settings.CrtSnowEnabled = _crtSnowEnabled;

            CrtSnowOverlay.Visibility = _crtSnowEnabled ? Visibility.Visible : Visibility.Collapsed;

            if (_crtSnowEnabled)
            {
                CrtSnowOverlay.Opacity = (_snowAmount / 100.0) * 0.45;
                _snowTimer.Start();
            }
            else
            {
                _snowTimer.Stop();
                CrtSnowOverlay.Opacity = 0.0;
            }

            SaveCurrentSettings();
        }

        private void SnowAmountSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isInitialized || _settings == null) return;

            _snowAmount = e.NewValue;
            _settings.SnowAmount = _snowAmount;

            if (SnowAmountValueText != null)
            {
                SnowAmountValueText.Text = $"{Math.Round(_snowAmount)}%";
            }
            if (_crtSnowEnabled && CrtSnowOverlay != null)
            {
                CrtSnowOverlay.Opacity = (_snowAmount / 100.0) * 0.45;
            }

            SaveCurrentSettings();
        }

        private void SnowTimer_Tick(object? sender, EventArgs e)
        {
            if (!_crtSnowEnabled || _isClosed) return;

            var frames = GetOrCreateSnowBitmaps();
            _snowFrameIndex = (_snowFrameIndex + 1) % frames.Count;
            CrtSnowOverlay.Source = frames[_snowFrameIndex];
        }

        #endregion

        #region Context Menu & Hover Bridge

        private void Window_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            App.EnsureStandardMenuDropAlignment();
            _effectsCloseTimer.Stop();
        }

        private void MainContextMenu_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            _effectsCloseTimer.Stop();
        }

        private void MainContextMenu_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _effectsCloseTimer.Stop();
        }

        private void MainContextMenu_Closed(object sender, RoutedEventArgs e)
        {
            _effectsCloseTimer.Stop();
            DetachPopupHook();
        }

        private void EffectsMenuItem_MouseEnter(object sender, MouseEventArgs e)
        {
            _effectsCloseTimer.Stop();
        }

        private void EffectsMenuItem_MouseLeave(object sender, MouseEventArgs e)
        {
            if (EffectsMenuItem.IsSubmenuOpen)
            {
                _effectsCloseTimer.Stop();
                _effectsCloseTimer.Start();
            }
        }

        private void EffectsMenuItem_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            _effectsCloseTimer.Stop();
            AttachPopupHook();
        }

        private void EffectsMenuItem_SubmenuClosed(object sender, RoutedEventArgs e)
        {
            _effectsCloseTimer.Stop();
            DetachPopupHook();
        }

        private void EffectsCloseTimer_Tick(object? sender, EventArgs e)
        {
            _effectsCloseTimer.Stop();
            if (EffectsMenuItem != null && EffectsMenuItem.IsSubmenuOpen)
            {
                EffectsMenuItem.IsSubmenuOpen = false;
            }
        }

        private void AttachPopupHook()
        {
            if (_subscribedPopupChild != null) return;
            try
            {
                var popup = FindVisualChild<Popup>(EffectsMenuItem);
                if (popup?.Child is FrameworkElement popupChild)
                {
                    _subscribedPopupChild = popupChild;
                    _subscribedPopupChild.MouseEnter += Submenu_MouseEnter;
                    _subscribedPopupChild.MouseLeave += Submenu_MouseLeave;
                }
            }
            catch { }
        }

        private void DetachPopupHook()
        {
            if (_subscribedPopupChild != null)
            {
                try
                {
                    _subscribedPopupChild.MouseEnter -= Submenu_MouseEnter;
                    _subscribedPopupChild.MouseLeave -= Submenu_MouseLeave;
                }
                catch { }
                _subscribedPopupChild = null;
            }
        }

        private void Submenu_MouseEnter(object sender, MouseEventArgs e)
        {
            _effectsCloseTimer.Stop();
        }

        private void Submenu_MouseLeave(object sender, MouseEventArgs e)
        {
            if (EffectsMenuItem.IsSubmenuOpen)
            {
                _effectsCloseTimer.Stop();
                _effectsCloseTimer.Start();
            }
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed) return typed;
                var found = FindVisualChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        private void Slider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is Slider slider)
            {
                double delta = e.Delta > 0 ? slider.SmallChange : -slider.SmallChange;
                slider.Value = Math.Clamp(slider.Value + delta, slider.Minimum, slider.Maximum);
                e.Handled = true;
            }
        }

        #endregion

        #region Window Drag, Sizing & Lifecycle

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateBackgroundClip();
        }

        private void AlwaysOnTop_Click(object sender, RoutedEventArgs e)
        {
            Topmost = AlwaysOnTopMenuItem.IsChecked;
            _settings.AlwaysOnTop = Topmost;
            SaveCurrentSettings();
        }

        private void Shadow_Click(object sender, RoutedEventArgs e)
        {
            WindowDropShadow.Opacity = ShadowMenuItem.IsChecked ? 0.35 : 0.0;
            _settings.WindowShadow = ShadowMenuItem.IsChecked;
            SaveCurrentSettings();
        }

        private void NewWindow_Click(object sender, RoutedEventArgs e)
        {
            var newWindow = new MainWindow();
            newWindow.Show();
        }

        private void CloseWidget_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void ExitAll_Click(object sender, RoutedEventArgs e)
        {
            App.CleanProcessExit(0);
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            if (_isClosed) return;
            Dispatcher.InvokeAsync(() =>
            {
                if (!_isClosed) InvalidateVisual();
            });
        }

        private void SaveCurrentSettings(bool immediate = false)
        {
            if (!_isInitialized || _isClosed || _settings == null) return;

            if (WindowState == WindowState.Normal)
            {
                _settings.WindowWidth = ActualWidth;
                _settings.WindowHeight = ActualHeight;
                _settings.WindowLeft = Left;
                _settings.WindowTop = Top;
            }

            if (immediate)
            {
                _saveDebounceTimer?.Stop();
                _settings.Save();
            }
            else
            {
                _saveDebounceTimer?.Stop();
                _saveDebounceTimer?.Start();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _isClosed = true;
            _glitchTimer.Stop();
            _snowTimer.Stop();
            _effectsCloseTimer.Stop();
            _saveDebounceTimer.Stop();

            SaveCurrentSettings(true);

            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _terminalSession.Dispose();

            base.OnClosed(e);
        }

        #endregion
    }
}

