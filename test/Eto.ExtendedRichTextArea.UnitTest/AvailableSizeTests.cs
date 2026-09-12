using System.Reflection;
using Eto.Drawing;
using Eto.Forms;

namespace Eto.ExtendedRichTextArea.UnitTest;

public class AvailableSizeTests : TestBase
{
	static void SetAvailableSize(ExtendedRichTextArea textArea, Size size)
	{
		var field = textArea.GetType().GetField("_drawable", BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.That(field, Is.Not.Null, "Expected private field '_drawable' on ExtendedRichTextArea.");
		var drawable = field!.GetValue(textArea)!;
		var method = drawable.GetType().GetMethod("SetAvailableSize", BindingFlags.Instance | BindingFlags.Public);
		Assert.That(method, Is.Not.Null, "Expected SetAvailableSize method.");
		method!.Invoke(drawable, new object[] { size });
	}

	static ExtendedRichTextArea CreateWrappingTextArea()
	{
		var textArea = new ExtendedRichTextArea();
		textArea.Document.WrapMode = WrapMode.Character;
		textArea.Document.Text = "<>";
		return textArea;
	}

	[Test]
	public void EmptyAvailableSizeShouldNotCollapseTheDocument()
	{
		var textArea = CreateWrappingTextArea();
		var measured = textArea.Document.Size;
		Assert.That(measured.Width, Is.GreaterThan(0), "Precondition: the document should measure to a non-zero width.");

		// A host that has not been laid out yet reports an empty client size. Wrapping to zero
		// width collapses the document, and it stays collapsed until the next resize, which on
		// macOS left the annotation text field blank the first time it was shown (RH-96175).
		SetAvailableSize(textArea, Size.Empty);

		Assert.That(textArea.Document.Size, Is.EqualTo(measured));
	}

	[Test]
	public void ZeroWidthAvailableSizeShouldNotCollapseTheDocument()
	{
		var textArea = CreateWrappingTextArea();
		var measured = textArea.Document.Size;

		SetAvailableSize(textArea, new Size(0, 100));

		Assert.That(textArea.Document.Size, Is.EqualTo(measured));
	}

	[Test]
	public void RealAvailableSizeShouldStillBeApplied()
	{
		var textArea = CreateWrappingTextArea();

		SetAvailableSize(textArea, new Size(200, 100));

		Assert.That(textArea.Document.AvailableSize, Is.EqualTo(new SizeF(200, 100)));
		Assert.That(textArea.Document.Size.Width, Is.GreaterThan(0));
	}
}
