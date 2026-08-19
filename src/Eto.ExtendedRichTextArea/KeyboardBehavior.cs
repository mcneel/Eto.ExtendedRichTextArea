using Eto.Forms;
using Eto.ExtendedRichTextArea.Model;

using System;
using Eto.ExtendedRichTextArea.Commands;
using System.Windows.Input;

namespace Eto.ExtendedRichTextArea;

class KeyboardBehavior
{
	readonly TextAreaDrawable _textArea;
	readonly CaretBehavior _caret;
	readonly List<Command> _commands = new List<Command>();

	Document Document => _textArea.Document;

	public KeyboardBehavior(TextAreaDrawable textArea, CaretBehavior caret)
	{
		_caret = caret;
		_textArea = textArea;
		_textArea.TextInput += TextArea_TextInput;
		_textArea.KeyDown += TextArea_KeyDown;

		AddCommand(new CutCommand(textArea), "cut");
		AddCommand(new CopyCommand(textArea), "copy");
		AddCommand(new PasteCommand(textArea), "paste");
		AddCommand(new PasteWithoutFormattingCommand(textArea), "pasteAsPlainText");
		AddCommand(new UndoCommand(textArea), "undo");
		AddCommand(new RedoCommand(textArea), "redo");
		AddCommand(new BoldCommand(textArea));
		AddCommand(new ItalicCommand(textArea));

	}

	void AddCommand(Command command, string? macPlatformCommand = null)
	{
		_commands.Add(command);
		if (_textArea.Platform.IsMac && macPlatformCommand != null)
			_textArea.MapPlatformCommand(macPlatformCommand, command);
	}

	int GetPreviousCaretIndex()
	{
		var text = Document.Text;
		return TextBoundary.GetPreviousScalarStart(text, _caret.Index);
	}

	int GetNextCaretIndex()
	{
		var text = Document.Text;
		return TextBoundary.GetNextScalarEnd(text, _caret.Index);
	}


	private void TextArea_KeyDown_Navigation(object? sender, KeyEventArgs e)
	{
		if (e.Handled)
			return;
		var extendSelection = e.Modifiers.HasFlag(Keys.Shift);
		var lastCaretIndex = _caret.Index;

		if (Eto.Platform.Instance.IsMac)
			TextArea_KeyDown_Navigation_Mac(e);
		else
			TextArea_KeyDown_Navigation_Generic(e);

		if (!e.Handled)
		{
			switch (e.KeyData & ~Keys.Shift)
			{
				case Keys.PageUp:
					_caret.SetIndex(0, true);
					e.Handled = true;
					break;
				case Keys.PageDown:
					_caret.SetIndex(_textArea.Document.Length, true);
					e.Handled = true;
					break;
				case Keys.Home:
					_caret.Navigate(DocumentNavigationMode.BeginningOfLine);
					e.Handled = true;
					break;
				case Keys.End:
					_caret.Navigate(DocumentNavigationMode.EndOfLine);
					e.Handled = true;
					break;
				case Keys.Left:
					if (!extendSelection && _textArea.Selection?.Length > 0)
					{
						_caret.SetIndex(_textArea.Selection.Start, false);
						_textArea.SetSelection(null, true);
					}
					else
						_caret.SetIndex(GetPreviousCaretIndex(), true);
					e.Handled = true;
					break;
				case Keys.Right:
					if (!extendSelection && _textArea.Selection?.Length > 0)
					{
						_caret.SetIndex(_textArea.Selection.End, false);
						_textArea.SetSelection(null, true);
					}
					else
						_caret.SetIndex(GetNextCaretIndex(), true);
					e.Handled = true;
					break;
				case Keys.Up:
					_caret.Navigate(DocumentNavigationMode.PreviousLine);
					e.Handled = true;
					break;
				case Keys.Down:
					_caret.Navigate(DocumentNavigationMode.NextLine);
					e.Handled = true;
					break;
				case Keys.Control | Keys.Alt | Keys.Right:
				case Keys.Alt | Keys.Right:
					_caret.Navigate(DocumentNavigationMode.NextWord);
					e.Handled = true;
					break;
				case Keys.Control | Keys.Alt | Keys.Left:
				case Keys.Alt | Keys.Left:
					_caret.Navigate(DocumentNavigationMode.PreviousWord);
					e.Handled = true;
					break;
			}
		}
		if (e.Handled)
			_textArea.SetCaretSelection(lastCaretIndex, extendSelection);
	}

	private void TextArea_TextInput(object? sender, TextInputEventArgs e)
	{
		if (_textArea.ReadOnly)
		{
			e.Cancel = true;
			return;
		}
		if (e.Text.Length > 0)
		{
			if (char.IsControl(e.Text[0]))
				return;
			_textArea.TextArea.InsertText(e.Text);
			e.Cancel = true;
		}
	}

	private void TextArea_KeyDown_Generic(object? sender, KeyEventArgs e)
	{
		switch (e.KeyData)
		{
			case Keys.Control | Keys.A:
				_textArea.SetSelection(Document.GetRange(0, _textArea.Document.Length), true);
				e.Handled = true;
				break;
		}
	}

	private void TextArea_KeyDown_Navigation_Generic(KeyEventArgs e)
	{
		switch (e.KeyData & ~Keys.Shift)
		{
			case Keys.PageUp:
				_caret.SetIndex(0, true);
				e.Handled = true;
				break;
			case Keys.PageDown:
				_caret.SetIndex(_textArea.Document.Length, true);
				e.Handled = true;
				break;
			case Keys.Home:
				_caret.Navigate(DocumentNavigationMode.BeginningOfLine);
				e.Handled = true;
				break;
			case Keys.End:
				_caret.Navigate(DocumentNavigationMode.EndOfLine);
				e.Handled = true;
				break;
		}
	}

	private void TextArea_KeyDown_Mac(object? sender, KeyEventArgs e)
	{
		switch (e.KeyData)
		{
			case Keys.Application | Keys.A:
				_textArea.SetSelection(Document.GetRange(0, _textArea.Document.Length), true);
				e.Handled = true;
				break;
		}
	}

	private void TextArea_KeyDown_Navigation_Mac(KeyEventArgs e)
	{
		switch (e.KeyData & ~Keys.Shift)
		{
			case Keys.Application | Keys.Up:
				_caret.SetIndex(0, true);
				e.Handled = true;
				break;
			case Keys.Application | Keys.Down:
				_caret.SetIndex(_textArea.Document.Length, true);
				e.Handled = true;
				break;
			case Keys.Application | Keys.Left:
				_caret.Navigate(DocumentNavigationMode.BeginningOfLine);
				e.Handled = true;
				break;
			case Keys.Application | Keys.Right:
				_caret.Navigate(DocumentNavigationMode.EndOfLine);
				e.Handled = true;
				break;
		}
	}

	private void TextArea_KeyDown(object? sender, KeyEventArgs e)
	{
		if (!_textArea.Enabled)
			return;
		// Always call OnKeyDown on the document, even when there's no selection.
		// Use caret position for both start and end when Selection is null.
		var start = _textArea.Selection?.Start ?? _caret.Index;
		var end = _textArea.Selection?.End ?? _caret.Index;
		((IElement)_textArea.Document).OnKeyDown(start, end, e);

		if (e.Handled)
			return;

		if (Platform.Instance.IsMac)
			TextArea_KeyDown_Mac(sender, e);
		else
			TextArea_KeyDown_Generic(sender, e);

		if (e.Handled)
			return;

		TextArea_KeyDown_Navigation(sender, e);

		if (e.Handled)
			return;

		TextArea_KeyDown_Manipulation(e);

		if (e.Handled)
			return;

		foreach (var command in _commands)
		{
			if (command.Enabled && command.Shortcut == e.KeyData)
			{
				command.Execute();
				e.Handled = true;
				break;
			}
		}
	}
	
	private void TextArea_KeyDown_Manipulation(KeyEventArgs e)
	{
		if (_textArea.ReadOnly)
			return;
		switch (e.KeyData)
		{
			case Keys.Backspace:
				if (_textArea.Selection?.Length > 0)
				{
					var start = _textArea.Selection.Start;
					_textArea.Document.RemoveAt(_textArea.Selection.Start, _textArea.Selection.Length);
					_caret.SetIndex(start, false);
					_textArea.SetSelection(null, true);
				}
				else if (_caret.Index > 0)
				{
					var index = _caret.Index;
					var start = GetPreviousCaretIndex();
					_textArea.Document.RemoveAt(start, index - start);
					_caret.SetIndex(start, false);
					_textArea.SetSelection(null, true);
				}
				e.Handled = true;
				break;
			case Keys.Delete:
				if (_textArea.Selection?.Length > 0)
				{
					var start = _textArea.Selection.Start;
					_textArea.Document.RemoveAt(_textArea.Selection.Start, _textArea.Selection.Length);
					_caret.SetIndex(start, false);
					_textArea.SetSelection(null, true);
				}
				else if (_caret.Index < _textArea.Document.Length)
				{
					var end = GetNextCaretIndex();
					_textArea.Document.RemoveAt(_caret.Index, end - _caret.Index);
					_textArea.SetSelection(null, true);
				}
				e.Handled = true;
				break;
			case Keys.Enter:
				_textArea.TextArea.InsertText("\n");
				e.Handled = true;
				break;
			case Keys.Shift | Keys.Enter:
				_textArea.TextArea.InsertText("\x2028"); // line break
				e.Handled = true;
				break;
			case Keys.Tab:
				_textArea.TextArea.InsertText("\t"); // tab
				e.Handled = true;
				break;
		}
	}
}
