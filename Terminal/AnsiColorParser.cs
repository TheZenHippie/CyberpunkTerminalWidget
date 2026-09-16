using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace CyberpunkTerminalWidget.Terminal
{
    /// <summary>
    /// Represents a parsed segment of terminal text with optional ANSI color styling.
    /// </summary>
    public class TextSegment
    {
        public string Text { get; set; } = string.Empty;
        public Color? Color { get; set; }
        public bool IsBold { get; set; }

        public TextSegment(string text, Color? color = null, bool isBold = false)
        {
            Text = text;
            Color = color;
            IsBold = isBold;
        }
    }

    /// <summary>
    /// High-performance ANSI escape sequence parser supporting 16-color, 256-color palette,
    /// 24-bit TrueColor RGB, SGR formatting, and sanitizing non-color terminal control sequences.
    /// </summary>
    public static class AnsiColorParser
    {
        // Matches ANSI SGR sequences "\x1B[...m"
        private static readonly Regex SgrRegex = new Regex(@"\x1B\[([0-9;]*)m", RegexOptions.Compiled);

        // Matches non-SGR CSI sequences (e.g. cursor movements, screen clears) and OSC title/hyperlink sequences
        private static readonly Regex NonColorAnsiRegex = new Regex(@"(?:\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)|\x1B\[[0-9;?]*[A-Za-ln-z]|\x1B[=>])", RegexOptions.Compiled);

        // Precomputed 256-color palette
        private static readonly Color[] Palette256 = Initialize256Palette();

        // Standard 16-color ANSI mapping (30-37, 90-97)
        private static readonly Dictionary<int, Color> Standard16Colors = InitializeStandard16();

        private static Dictionary<int, Color> InitializeStandard16()
        {
            return new Dictionary<int, Color>
            {
                // Standard 8 (30-37)
                { 30, Color.FromRgb(30, 30, 30) },    // Black / Dark Slate
                { 31, Color.FromRgb(255, 34, 68) },   // Red / Glitch Red
                { 32, Color.FromRgb(0, 255, 102) },   // Green / Matrix Green
                { 33, Color.FromRgb(255, 230, 0) },   // Yellow / Cyber Yellow
                { 34, Color.FromRgb(0, 200, 255) },   // Blue / Neon Blue
                { 35, Color.FromRgb(255, 0, 127) },   // Magenta / Synth Pink
                { 36, Color.FromRgb(0, 240, 255) },   // Cyan / Neon Cyan
                { 37, Color.FromRgb(230, 230, 230) }, // White / Light Gray

                // High-intensity 8 (90-97)
                { 90, Color.FromRgb(128, 128, 128) }, // Bright Black / Gray
                { 91, Color.FromRgb(255, 80, 110) },  // Bright Red
                { 92, Color.FromRgb(80, 255, 140) },  // Bright Green
                { 93, Color.FromRgb(255, 245, 100) }, // Bright Yellow
                { 94, Color.FromRgb(80, 220, 255) },  // Bright Blue
                { 95, Color.FromRgb(255, 110, 190) }, // Bright Magenta
                { 96, Color.FromRgb(130, 255, 255) }, // Bright Cyan
                { 97, Color.FromRgb(255, 255, 255) }  // Bright White
            };
        }

        private static Color[] Initialize256Palette()
        {
            var palette = new Color[256];

            // 0-15: Standard 16
            var std = InitializeStandard16();
            for (int i = 0; i < 8; i++) palette[i] = std[30 + i];
            for (int i = 0; i < 8; i++) palette[8 + i] = std[90 + i];

            // 16-231: 6x6x6 RGB color cube
            for (int i = 16; i < 232; i++)
            {
                int val = i - 16;
                int b = val % 6;
                int g = (val / 6) % 6;
                int r = val / 36;

                byte R = (byte)(r > 0 ? r * 40 + 55 : 0);
                byte G = (byte)(g > 0 ? g * 40 + 55 : 0);
                byte B = (byte)(b > 0 ? b * 40 + 55 : 0);

                palette[i] = Color.FromRgb(R, G, B);
            }

            // 232-255: 24 grayscale levels
            for (int i = 232; i < 256; i++)
            {
                byte gray = (byte)((i - 232) * 10 + 8);
                palette[i] = Color.FromRgb(gray, gray, gray);
            }

            return palette;
        }

        /// <summary>
        /// Parses raw CLI stdout text containing ANSI escape sequences into styled text segments.
        /// </summary>
        public static List<TextSegment> Parse(string rawText, Color defaultColor)
        {
            var segments = new List<TextSegment>();
            if (string.IsNullOrEmpty(rawText))
            {
                return segments;
            }

            // Quick path if no escape character exists
            if (!rawText.Contains('\x1B'))
            {
                segments.Add(new TextSegment(rawText, defaultColor));
                return segments;
            }

            // Clean out non-color ANSI control sequences (cursor position, private modes, OSC titles)
            string sanitized = NonColorAnsiRegex.Replace(rawText, string.Empty);
            if (string.IsNullOrEmpty(sanitized))
            {
                return segments;
            }

            if (!sanitized.Contains('\x1B'))
            {
                segments.Add(new TextSegment(sanitized, defaultColor));
                return segments;
            }

            Color currentColor = defaultColor;
            bool isBold = false;
            int lastIndex = 0;

            var matches = SgrRegex.Matches(sanitized);
            foreach (Match match in matches)
            {
                if (match.Index > lastIndex)
                {
                    string plainText = sanitized.Substring(lastIndex, match.Index - lastIndex);
                    if (plainText.Length > 0)
                    {
                        segments.Add(new TextSegment(plainText, currentColor, isBold));
                    }
                }

                // Parse code sequence inside "\x1B[...m"
                string codeSeq = match.Groups[1].Value;
                if (string.IsNullOrEmpty(codeSeq) || codeSeq == "0")
                {
                    currentColor = defaultColor;
                    isBold = false;
                }
                else
                {
                    string[] codeParts = codeSeq.Split(';');
                    for (int i = 0; i < codeParts.Length; i++)
                    {
                        if (int.TryParse(codeParts[i], out int code))
                        {
                            switch (code)
                            {
                                case 0:
                                    currentColor = defaultColor;
                                    isBold = false;
                                    break;
                                case 1:
                                    isBold = true;
                                    break;
                                case 22:
                                    isBold = false;
                                    break;
                                case 39:
                                    currentColor = defaultColor;
                                    break;
                                case 38: // Extended foreground color
                                    if (i + 1 < codeParts.Length && int.TryParse(codeParts[i + 1], out int mode))
                                    {
                                        if (mode == 5 && i + 2 < codeParts.Length && int.TryParse(codeParts[i + 2], out int colorIndex))
                                        {
                                            // 256-color palette: 38;5;n
                                            if (colorIndex >= 0 && colorIndex < Palette256.Length)
                                            {
                                                currentColor = Palette256[colorIndex];
                                            }
                                            i += 2;
                                        }
                                        else if (mode == 2 && i + 4 < codeParts.Length &&
                                                 int.TryParse(codeParts[i + 2], out int r) &&
                                                 int.TryParse(codeParts[i + 3], out int g) &&
                                                 int.TryParse(codeParts[i + 4], out int b))
                                        {
                                            // 24-bit TrueColor: 38;2;r;g;b
                                            currentColor = Color.FromRgb(
                                                (byte)Math.Clamp(r, 0, 255),
                                                (byte)Math.Clamp(g, 0, 255),
                                                (byte)Math.Clamp(b, 0, 255));
                                            i += 4;
                                        }
                                    }
                                    break;
                                default:
                                    if (Standard16Colors.TryGetValue(code, out Color ansiColor))
                                    {
                                        currentColor = ansiColor;
                                    }
                                    break;
                            }
                        }
                    }
                }

                lastIndex = match.Index + match.Length;
            }

            if (lastIndex < sanitized.Length)
            {
                string remaining = sanitized.Substring(lastIndex);
                if (remaining.Length > 0)
                {
                    segments.Add(new TextSegment(remaining, currentColor, isBold));
                }
            }

            return segments;
        }

        /// <summary>
        /// Strips all ANSI codes and control sequences completely for plain text log representations.
        /// </summary>
        public static string StripAnsi(string rawText)
        {
            if (string.IsNullOrEmpty(rawText) || !rawText.Contains('\x1B'))
            {
                return rawText;
            }

            string stripped = NonColorAnsiRegex.Replace(rawText, string.Empty);
            return SgrRegex.Replace(stripped, string.Empty);
        }
    }
}

