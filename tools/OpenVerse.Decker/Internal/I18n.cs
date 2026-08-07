using System.Windows;

namespace OpenVerse.Decker.Internal;

public static class I18n
{
    public static string Text(string key) => (string)Application.Current.Resources[key];

    public static string Format(string key, string value) => Text(key).Replace("*", value);
}
