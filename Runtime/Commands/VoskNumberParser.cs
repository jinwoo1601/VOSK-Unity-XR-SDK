using System;
using System.Collections.Generic;

namespace VoskXR.Commands
{
    /// <summary>
    /// Converts spoken number-word sequences to integers.
    /// Provides the digit vocabulary used by <see cref="VoskCommandParser"/> for
    /// NumberSequence slot matching and grammar generation.
    /// </summary>
    public static class VoskNumberParser
    {
        static readonly Dictionary<string, int> WordValues = new Dictionary<string, int>(30, StringComparer.Ordinal)
        {
            { "zero", 0 }, { "one", 1 }, { "two", 2 }, { "three", 3 }, { "four", 4 },
            { "five", 5 }, { "six", 6 }, { "seven", 7 }, { "eight", 8 }, { "nine", 9 },
            { "ten", 10 }, { "eleven", 11 }, { "twelve", 12 }, { "thirteen", 13 },
            { "fourteen", 14 }, { "fifteen", 15 }, { "sixteen", 16 }, { "seventeen", 17 },
            { "eighteen", 18 }, { "nineteen", 19 },
            { "twenty", 20 }, { "thirty", 30 }, { "forty", 40 }, { "fifty", 50 },
            { "sixty", 60 }, { "seventy", 70 }, { "eighty", 80 }, { "ninety", 90 },
            { "hundred", 100 }, { "thousand", 1000 }
        };

        /// <summary>
        /// All number words recognized by the parser.
        /// Derived from WordValues to maintain a single source of truth.
        /// </summary>
        public static readonly HashSet<string> DigitVocabulary =
            new HashSet<string>(WordValues.Keys, StringComparer.Ordinal);

        static readonly char[] SplitSeparator = { ' ' };

        /// <summary>
        /// Parses a digit-per-word sequence by concatenating single digits.
        /// Each word must map to 0–9. "two seven zero" → 270, "one five" → 15.
        /// </summary>
        /// <returns>The concatenated integer, or 0 for null/empty input.</returns>
        /// <exception cref="FormatException">A word is not a single digit (0–9).</exception>
        public static int ParseDigitSequence(string words)
        {
            if (string.IsNullOrWhiteSpace(words))
                return 0;

            string[] tokens = words.Split(SplitSeparator, StringSplitOptions.RemoveEmptyEntries);
            int result = 0;

            foreach (string token in tokens)
            {
                if (!WordValues.TryGetValue(token, out int value) || value > 9)
                    throw new FormatException($"'{token}' is not a single-digit word (zero–nine).");

                result = result * 10 + value;
            }

            return result;
        }

        /// <summary>
        /// Parses a cardinal number phrase. Handles teens, tens, hundreds, and thousands.
        /// "fifteen" → 15, "twenty" → 20, "two hundred" → 200.
        /// </summary>
        /// <returns>The parsed integer, or 0 for null/empty input.</returns>
        /// <exception cref="FormatException">A word is not a recognized number word.</exception>
        public static int ParseCardinal(string words)
        {
            if (string.IsNullOrWhiteSpace(words))
                return 0;

            string[] tokens = words.Split(SplitSeparator, StringSplitOptions.RemoveEmptyEntries);

            int result = 0;
            int current = 0;

            foreach (string token in tokens)
            {
                if (!WordValues.TryGetValue(token, out int value))
                    throw new FormatException($"'{token}' is not a recognized number word.");

                if (value == 1000)
                {
                    current = (current == 0 ? 1 : current) * 1000;
                    result += current;
                    current = 0;
                }
                else if (value == 100)
                {
                    current = (current == 0 ? 1 : current) * 100;
                }
                else
                {
                    current += value;
                }
            }

            return result + current;
        }
    }
}
