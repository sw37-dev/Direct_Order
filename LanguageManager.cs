using GTA;
using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;

public static class Language
{
    private const string ConfigFileName = "DirectOrder.ini";
    private const string LanguageRootFolder = "DirectOrder_Data";
    private const string LanguageSubFolder = "Languages";

    private static readonly object SyncRoot = new object();
    private static readonly Dictionary<string, string> Entries =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static bool _initialized = false;
    private static string _selectedCode = "VN";

    public static string SelectedLanguageCode
    {
        get
        {
            EnsureInitialized();
            return _selectedCode;
        }
    }

    public static void Initialize()
    {
        lock (SyncRoot)
        {
            if (_initialized)
                return;

            ReloadInternal();
            _initialized = true;
        }
    }

    public static void Reload()
    {
        lock (SyncRoot)
        {
            ReloadInternal();
            _initialized = true;
        }
    }

    public static string Get(string key, string fallback = "")
    {
        EnsureInitialized();

        string value;
        if (Entries.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value))
            return value;

        return fallback ?? string.Empty;
    }

    public static string ReplaceTokens(string text, params string[] tokensAndValues)
    {
        if (string.IsNullOrEmpty(text) || tokensAndValues == null || tokensAndValues.Length == 0)
            return text ?? string.Empty;

        for (int i = 0; i + 1 < tokensAndValues.Length; i += 2)
        {
            string token = tokensAndValues[i] ?? string.Empty;
            string value = tokensAndValues[i + 1] ?? string.Empty;
            text = text.Replace(token, value);
        }

        return text;
    }

    private static void EnsureInitialized()
    {
        if (!_initialized)
            Initialize();
    }

    private static void ReloadInternal()
    {
        Entries.Clear();

        string scriptsDir = GetScriptsDirectory();
        string iniPath = Path.Combine(scriptsDir, ConfigFileName);

        _selectedCode = ReadIniValue(iniPath, "Language", "VN");
        if (string.IsNullOrWhiteSpace(_selectedCode))
            _selectedCode = "VN";

        string languageDir = Path.Combine(scriptsDir, LanguageRootFolder, LanguageSubFolder);

        if (!LoadLanguageFile(Path.Combine(languageDir, _selectedCode + ".xml")))
        {
            if (!LoadLanguageFile(Path.Combine(languageDir, "VN.xml")))
            {
                TryLoadFirstAvailableLanguage(languageDir);
            }
        }
    }

    private static bool LoadLanguageFile(string filePath)
    {
        if (!File.Exists(filePath))
            return false;

        try
        {
            XDocument doc = XDocument.Load(filePath);
            XElement root = doc.Root;
            if (root == null)
                return false;

            Entries.Clear();

            foreach (XElement element in root.Elements())
            {
                string key = GetElementKey(element);
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                string value = element.Value ?? string.Empty;
                Entries[key.Trim()] = value;
            }

            if (Entries.Count > 0)
            {
                _selectedCode = Path.GetFileNameWithoutExtension(filePath);
                return true;
            }
        }
        catch
        {
            // Bỏ qua lỗi parse để rơi xuống fallback.
        }

        return false;
    }

    private static void TryLoadFirstAvailableLanguage(string languageDir)
    {
        try
        {
            if (!Directory.Exists(languageDir))
                return;

            string[] files = Directory.GetFiles(languageDir, "*.xml", SearchOption.TopDirectoryOnly);
            foreach (string file in files)
            {
                if (LoadLanguageFile(file))
                {
                    _selectedCode = Path.GetFileNameWithoutExtension(file);
                    return;
                }
            }
        }
        catch
        {
            // Bỏ qua lỗi fallback.
        }
    }

    private static string GetElementKey(XElement element)
    {
        if (element == null)
            return string.Empty;

        XAttribute keyAttr = element.Attribute("Key");
        if (keyAttr != null && !string.IsNullOrWhiteSpace(keyAttr.Value))
            return keyAttr.Value;

        XAttribute nameAttr = element.Attribute("Name");
        if (nameAttr != null && !string.IsNullOrWhiteSpace(nameAttr.Value))
            return nameAttr.Value;

        XAttribute idAttr = element.Attribute("Id");
        if (idAttr != null && !string.IsNullOrWhiteSpace(idAttr.Value))
            return idAttr.Value;

        return string.Empty;
    }

    private static string ReadIniValue(string iniPath, string keyName, string defaultValue)
    {
        try
        {
            if (!File.Exists(iniPath))
                return defaultValue;

            foreach (string rawLine in File.ReadAllLines(iniPath))
            {
                string line = rawLine.Trim();

                if (line.Length == 0)
                    continue;

                if (line.StartsWith(";") || line.StartsWith("#"))
                    continue;

                int equalsIndex = line.IndexOf('=');
                if (equalsIndex <= 0)
                    continue;

                string key = line.Substring(0, equalsIndex).Trim();
                string value = line.Substring(equalsIndex + 1).Trim();

                if (key.Equals(keyName, StringComparison.OrdinalIgnoreCase))
                    return TrimQuotes(value);
            }
        }
        catch
        {
            // Bỏ qua lỗi đọc ini.
        }

        return defaultValue;
    }

    private static string TrimQuotes(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        value = value.Trim();

        if (value.Length >= 2 && value.StartsWith("\"") && value.EndsWith("\""))
            return value.Substring(1, value.Length - 2).Trim();

        return value;
    }

    private static string GetScriptsDirectory()
    {
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string scriptsDir = Path.Combine(baseDir, "scripts");

            if (Directory.Exists(scriptsDir))
                return scriptsDir;

            return baseDir;
        }
        catch
        {
            return ".";
        }
    }
}