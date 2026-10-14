using Eto.Drawing;
using Eto.Forms;

#if WINDOWS
// WPF controls must be created on an STA thread
[assembly: Apartment(ApartmentState.STA)]
#endif

namespace Eto.ExtendedRichTextArea.UnitTest;

public class TestBase
{
	protected FontFamily DefaultFontFamily => SystemFonts.Default().Family;
	protected FontFamily ArialFontFamily => new FontFamily("Arial");
	protected FontFamily TimesNewRomanFontFamily => new FontFamily("Times New Roman");
	protected FontFamily CourierNewFontFamily => new FontFamily("Courier New");
	
	[SetUp]
	public void Setup()
	{
		if (Application.Instance == null)
		{
			Platform platform;
#if MAC
			if (EtoEnvironment.Platform.IsMac)
				platform = new Eto.Mac.Platform();
#endif
			// else if (EtoEnvironment.Platform.IsGtk)
			// 	platform = new Eto.GtkSharp.Platform();
			// else if (EtoEnvironment.Platform.IsWinForms)
			// 	platform = new Eto.WinForms.Platform();
#if WINDOWS
			if (EtoEnvironment.Platform.IsWindows)
				platform = new Eto.Wpf.Platform();
#endif
			else
				throw new NotSupportedException("Platform not supported");
			var app = new Application(platform);
			app.Attach();
		}
	}

	[TearDown]
	public void TearDown()
	{
	}	
}
