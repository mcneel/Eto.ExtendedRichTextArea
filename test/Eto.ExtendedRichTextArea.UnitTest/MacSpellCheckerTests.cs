#if MAC
using System.Reflection;
using Eto.ExtendedRichTextArea.Mac;
using MonoMac.AppKit;

namespace Eto.ExtendedRichTextArea.UnitTest;

[TestFixture]
public class MacSpellCheckerTests : TestBase
{
	static NSSpellChecker GetNativeChecker(MacSpellChecker checker)
		=> (NSSpellChecker)typeof(MacSpellChecker).GetField("_checker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(checker)!;

	// RH-99470: a private NSSpellChecker crashes on the next Dictation notification after it is freed.
	[Test]
	public void DefaultLanguageShouldUseSharedChecker()
	{
		Assert.That(GetNativeChecker(new MacSpellChecker()).Handle, Is.EqualTo(NSSpellChecker.SharedSpellChecker.Handle));
	}

	[Test]
	public void ExplicitLanguageShouldUseSharedChecker()
	{
		var shared = NSSpellChecker.SharedSpellChecker;
		var before = shared.Language;
		try
		{
			var checker = GetNativeChecker(new MacSpellChecker("fr"));
			Assert.That(checker.Handle, Is.EqualTo(shared.Handle));
			Assert.That(shared.Language, Is.EqualTo("fr"));
		}
		finally
		{
			shared.Language = before;
		}
	}
}
#endif
