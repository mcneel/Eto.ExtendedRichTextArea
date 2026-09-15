using Eto.Forms;

namespace Eto.ExtendedRichTextArea.UnitTest;

public class ChangeCaseTests : TestBase
{
	const string UpperCase = "Make Upper Case";
	const string LowerCase = "Make Lower Case";
	const string Capitalize = "Capitalize";

	static ButtonMenuItem? FindTransformations(ExtendedRichTextArea textArea)
		=> textArea.ContextMenu.Items.OfType<ButtonMenuItem>().FirstOrDefault(i => i.Text == "Transformations");

	static void Apply(ExtendedRichTextArea textArea, string transformation)
	{
		var transformations = FindTransformations(textArea);
		Assert.That(transformations, Is.Not.Null, "Expected a Transformations submenu in the context menu.");
		var item = transformations!.Items.OfType<ButtonMenuItem>().FirstOrDefault(i => i.Text == transformation);
		Assert.That(item, Is.Not.Null, $"Expected a '{transformation}' item in the Transformations submenu.");
		item!.PerformClick();
	}

	static ExtendedRichTextArea CreateWithHtml(string html)
	{
		var textArea = new ExtendedRichTextArea();
		var loaded = DocumentFormat.Html.LoadFromString(textArea.Document.DocumentRange, html);
		Assert.That(loaded, Is.True);
		return textArea;
	}

	static void SelectAll(ExtendedRichTextArea textArea)
		=> textArea.Selection = textArea.Document.GetRange(0, textArea.Document.Length);

	static void Undo(ExtendedRichTextArea textArea)
	{
		var undo = textArea.ContextMenu.Items.OfType<ButtonMenuItem>().FirstOrDefault(i => i.Text == "Undo");
		Assert.That(undo, Is.Not.Null, "Expected an Undo item in the context menu.");
		undo!.PerformClick();
	}

	[TestCase(UpperCase, "hello there", "HELLO THERE")]
	[TestCase(LowerCase, "Hello THERE", "hello there")]
	[TestCase(Capitalize, "hello THERE fRIEND", "Hello There Friend")]
	[TestCase(Capitalize, "hello\tthere\nfriend", "Hello\tThere\nFriend")]
	public void TransformationShouldRecaseTheSelection(string transformation, string text, string expected)
	{
		var textArea = new ExtendedRichTextArea();
		textArea.Document.Text = text;
		SelectAll(textArea);

		Apply(textArea, transformation);

		Assert.That(textArea.Document.Text, Is.EqualTo(expected));
	}

	[Test]
	public void TransformationShouldOnlyTouchTheSelectionAndKeepIt()
	{
		var textArea = new ExtendedRichTextArea();
		textArea.Document.Text = "hello there friend";
		textArea.Selection = textArea.Document.GetRange(6, 11);

		Apply(textArea, UpperCase);

		Assert.That(textArea.Document.Text, Is.EqualTo("hello THERE friend"));
		Assert.That(textArea.Selection.Start, Is.EqualTo(6));
		Assert.That(textArea.Selection.End, Is.EqualTo(11));
	}

	[Test]
	public void TransformationShouldKeepEachRunsFormatting()
	{
		var textArea = CreateWithHtml("<p>hello <b>there</b> friend</p>");
		SelectAll(textArea);

		Apply(textArea, UpperCase);

		var document = textArea.Document;
		Assert.That(document.Text, Is.EqualTo("HELLO THERE FRIEND"));
		Assert.That(document.GetAttributes(0, 5).Bold, Is.Not.True);
		Assert.That(document.GetAttributes(6, 11).Bold, Is.True);
		Assert.That(document.GetAttributes(12, 18).Bold, Is.Not.True);
	}

	[Test]
	public void CapitalizeShouldTreatAWordSplitAcrossRunsAsOneWord()
	{
		var textArea = CreateWithHtml("<p>wo<b>rd</b> two</p>");
		SelectAll(textArea);

		Apply(textArea, Capitalize);

		var document = textArea.Document;
		Assert.That(document.Text, Is.EqualTo("Word Two"));
		Assert.That(document.GetAttributes(2, 4).Bold, Is.True);
		Assert.That(document.GetAttributes(0, 2).Bold, Is.Not.True);
	}

	[Test]
	public void TransformationShouldSpanParagraphs()
	{
		var textArea = new ExtendedRichTextArea();
		textArea.Document.Text = "hello\nthere";
		SelectAll(textArea);

		Apply(textArea, UpperCase);

		Assert.That(textArea.Document.Text, Is.EqualTo("HELLO\nTHERE"));
		Assert.That(textArea.Document.Count, Is.EqualTo(2));
	}

	[Test]
	public void TransformationWithoutSelectionShouldUseTheWordAtTheCaret()
	{
		var textArea = new ExtendedRichTextArea();
		textArea.Document.Text = "hello there friend";
		textArea.CaretIndex = 8;

		Apply(textArea, UpperCase);

		Assert.That(textArea.Document.Text, Is.EqualTo("hello THERE friend"));
		Assert.That(textArea.Selection.Start, Is.EqualTo(6));
		Assert.That(textArea.Selection.End, Is.EqualTo(11));
	}

	[Test]
	public void TransformationShouldUndoInOneStep()
	{
		var textArea = CreateWithHtml("<p>hello <b>there</b> friend</p>");
		SelectAll(textArea);

		Apply(textArea, UpperCase);
		Assert.That(textArea.Document.Text, Is.EqualTo("HELLO THERE FRIEND"));

		Undo(textArea);
		Assert.That(textArea.Document.Text, Is.EqualTo("hello there friend"));
		Assert.That(textArea.Document.GetAttributes(6, 11).Bold, Is.True);
	}

	[Test]
	public void ReadOnlyEditorShouldNotOfferTransformations()
	{
		var textArea = new ExtendedRichTextArea();
		textArea.Document.Text = "hello";
		textArea.ReadOnly = true;

		Assert.That(FindTransformations(textArea), Is.Null);
	}
}
