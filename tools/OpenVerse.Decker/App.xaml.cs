using System.Diagnostics;
using System.Globalization;
using System.Windows;
using OpenVerse.Decker.Internal;
using OpenVerse.Decker.View;

namespace OpenVerse.Decker;

public partial class App : Application
{
    private Window? coreWindow = null;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Logger.Log("Started decker app");

        var culture = CultureInfo.CurrentCulture.Name;
        try
        {
            var local = CultureInfo.CreateSpecificCulture(culture).Name.ToLower();
            var localDictionary = new ResourceDictionary
            {
                Source = new Uri($"Resources/StringResource.{local}.xaml", UriKind.Relative),
            };
            Resources.MergedDictionaries.Add(localDictionary);
            Logger.Log($"Use localized dictionary for {culture}");
        }
        catch (CultureNotFoundException)
        {
            Logger.Log($"{culture} is not a recognized culture - using the default strings.");
        }
        catch
        {
            Logger.Log($"No localization file for {culture} - using the default dictionary.");
        }

        coreWindow = new CoreWindow();
        coreWindow.Show();

        Logger.Log("Showed core window");
    }
}
