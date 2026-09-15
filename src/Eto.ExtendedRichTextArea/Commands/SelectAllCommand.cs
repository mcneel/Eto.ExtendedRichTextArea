using Eto.Forms;

namespace Eto.ExtendedRichTextArea.Commands;

class SelectAllCommand : Command
{
	readonly TextAreaDrawable _textArea;

	public SelectAllCommand(TextAreaDrawable textArea)
	{
		MenuText = Application.Instance.Localize(typeof(ExtendedRichTextArea), "Select All");
		Shortcut = Application.Instance.CommonModifier | Keys.A;
		_textArea = textArea;
	}

	protected override void OnExecuted(EventArgs e)
	{
		var document = _textArea.Document;
		_textArea.SetSelection(document.GetRange(0, document.Length), true);
	}
}