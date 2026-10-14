using System.Reflection;
using Eto.Forms;
using Eto.ExtendedRichTextArea.Model;

namespace Eto.ExtendedRichTextArea.UnitTest;

public class CompositionTests : TestBase
{
	static void RaiseTextComposition(ExtendedRichTextArea textArea, string text, bool isActive)
	{
		var drawable = textArea.GetType().GetField("_drawable", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(textArea)!;
		var method = drawable.GetType().GetMethod("OnTextComposition", BindingFlags.Instance | BindingFlags.NonPublic)!;
		method.Invoke(drawable, new object[] { new TextCompositionEventArgs(text, isActive) });
	}

	// RH-99379: ending a composition started before the document was replaced restored the old document's selection and threw.
	[Test]
	public void ReplacingDocumentDuringCompositionShouldNotThrow()
	{
		var textArea = new ExtendedRichTextArea();
		textArea.Document.Text = "hello world";
		var oldDocument = textArea.Document;
		textArea.Selection = oldDocument.GetRange(0, 5);

		RaiseTextComposition(textArea, "ni", true);
		Assert.That(oldDocument.Text, Is.EqualTo("ni world"), "Precondition: the composed text should replace the selection.");

		var newDocument = new Document { Text = "new text" };
		textArea.Document = newDocument;

		Assert.DoesNotThrow(() => RaiseTextComposition(textArea, string.Empty, false));
		Assert.DoesNotThrow(() => textArea.CaretIndex = 3);
		Assert.That(newDocument.Text, Is.EqualTo("new text"));
	}
}
