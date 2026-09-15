using Eto.Forms;
using Eto.ExtendedRichTextArea.Model;

namespace Eto.ExtendedRichTextArea.Commands;

enum CaseTransform
{
	Upper,
	Lower,
	Capitalize
}

/// <summary>
/// Changes the case of the selected text, or of the word at the caret when nothing is selected,
/// keeping every run's formatting. Mirrors the "Transformations" submenu of a native macOS text view.
/// </summary>
class ChangeCaseCommand : Command
{
	readonly TextAreaDrawable _textArea;
	readonly CaseTransform _transform;

	public ChangeCaseCommand(TextAreaDrawable textArea, CaseTransform transform)
	{
		_textArea = textArea;
		_transform = transform;
		MenuText = Application.Instance.Localize(typeof(ExtendedRichTextArea), MenuTextFor(transform));
	}

	static string MenuTextFor(CaseTransform transform)
	{
		switch (transform)
		{
			case CaseTransform.Upper:
				return "Make Upper Case";
			case CaseTransform.Lower:
				return "Make Lower Case";
			default:
				return "Capitalize";
		}
	}

	public override bool Enabled
	{
		get => base.Enabled && !_textArea.ReadOnly;
		set => base.Enabled = value;
	}

	protected override void OnExecuted(EventArgs e)
	{
		if (_textArea.ReadOnly)
			return;
		if (!TryGetRange(out var start, out var end))
			return;
		if (!CaseTransformer.Apply(_textArea.Document, start, end, _transform))
			return;
		_textArea.Caret.SetIndex(end, false);
		_textArea.SetSelection(_textArea.Document.GetRange(start, end), true);
	}

	bool TryGetRange(out int start, out int end)
	{
		if (_textArea.HasSelection)
		{
			start = _textArea.Selection.Start;
			end = _textArea.Selection.End;
			return end > start;
		}
		var word = _textArea.Document.EnumerateWords(_textArea.Caret.Index, true).FirstOrDefault();
		if (word.text == null)
		{
			start = end = 0;
			return false;
		}
		start = word.start;
		end = word.start + word.text.Length;
		return end > start;
	}
}

static class CaseTransformer
{
	/// <summary>
	/// Re-cases the text in [start, end) run by run, so each run keeps its own attributes,
	/// as a single undoable edit. Returns false when nothing needed to change.
	/// </summary>
	public static bool Apply(Document document, int start, int end, CaseTransform transform)
	{
		start = Math.Max(0, start);
		end = Math.Min(end, document.Length);
		if (end <= start)
			return false;

		// The live runs overlapping the range, with the overlap in document-absolute positions.
		var runs = new List<(TextElement element, int start, int end)>();
		foreach (var element in document.EnumerateInlines(start, end, false))
		{
			if (element is not TextElement textElement)
				continue;
			var runStart = Math.Max(textElement.DocumentStart, start);
			var runEnd = Math.Min(textElement.DocumentStart + textElement.Length, end);
			if (runEnd > runStart)
				runs.Add((textElement, runStart, runEnd));
		}
		if (runs.Count == 0)
			return false;

		// Case the whole range as one string so a word split across runs is treated as one word;
		// whatever isn't text (paragraph breaks, images) reads as a space and delimits words.
		var chars = new string(' ', end - start).ToCharArray();
		foreach (var run in runs)
			run.element.Text.CopyTo(run.start - run.element.DocumentStart, chars, run.start - start, run.end - run.start);
		if (!Transform(chars, transform))
			return false;

		// Recasing keeps every length, so each run's text is rewritten in place. Removing and
		// re-inserting text would not do: an inserted span without attributes of its own is merged
		// into whichever run it lands in, and a whole run replaced that way takes on its neighbour's
		// formatting. In place, the run structure and every position stay exactly as they were.
		document.BeginEdit();
		foreach (var run in runs)
		{
			var text = run.element.Text;
			var offset = run.start - run.element.DocumentStart;
			var length = run.end - run.start;
			var replacement = new string(chars, run.start - start, length);
			if (string.CompareOrdinal(text, offset, replacement, 0, length) == 0)
				continue;
			run.element.Text = text.Substring(0, offset) + replacement + text.Substring(offset + length);
		}
		document.EndEdit();
		return true;
	}

	// Per-character mapping keeps every index stable, so the recased text drops straight
	// back into the runs it came from.
	static bool Transform(char[] chars, CaseTransform transform)
	{
		var changed = false;
		var wordStart = true;
		for (int i = 0; i < chars.Length; i++)
		{
			var c = chars[i];
			if (char.IsWhiteSpace(c))
			{
				wordStart = true;
				continue;
			}
			char replacement;
			switch (transform)
			{
				case CaseTransform.Upper:
					replacement = char.ToUpper(c);
					break;
				case CaseTransform.Lower:
					replacement = char.ToLower(c);
					break;
				default:
					replacement = wordStart ? char.ToUpper(c) : char.ToLower(c);
					break;
			}
			wordStart = false;
			if (replacement != c)
			{
				chars[i] = replacement;
				changed = true;
			}
		}
		return changed;
	}
}