namespace CoRhino.Panel
{
    /// <summary>
    /// Two-language (ja/en) localization keyed to RHINO's UI language, not the
    /// OS locale — the panel should read like the app it is docked in. Japanese
    /// Rhino → Japanese; anything else → English. Evaluated once per process:
    /// changing Rhino's language requires a restart anyway. This governs the
    /// panel UI (via the CORHINO_LANG handshake in LoadWebView), the dialogs,
    /// and the text CoRhino injects toward claude (layer prompt, seeded
    /// CLAUDE.md / skills README).
    /// </summary>
    static class L10n
    {
        // Rhino LCID 1041 = Japanese.
        public static readonly bool Japanese = DetectJapanese();

        /// <summary>"ja" or "en" — handed to the WebView as window.CORHINO_LANG.</summary>
        public static string Tag => Japanese ? "ja" : "en";

        public static string T(string ja, string en) => Japanese ? ja : en;

        static bool DetectJapanese()
        {
            try
            {
                return Rhino.ApplicationSettings.AppearanceSettings.LanguageIdentifier == 1041;
            }
            catch
            {
                // Headless/test contexts without app settings: default to English.
                return false;
            }
        }
    }
}
