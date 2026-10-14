using Eto.Forms;
using Eto.Drawing;
using Eto.ExtendedRichTextArea.Model;

namespace Eto.ExtendedRichTextArea;

partial class TextAreaDrawable
{
	protected override void OnTextInsertionBoundsRequested(TextInsertionBoundsEventArgs e)
	{
		base.OnTextInsertionBoundsRequested(e);
		e.Bounds = _compositionBounds ?? CaretBounds;
	}

	bool _isComposingText;
	string? _compositionText;
	IDisposable? _compositionTrackingScope;
	DocumentState.DocumentSnapshot? _compositionState;
	bool _duringOnTextComposition;
	RectangleF? _compositionBounds;

	void ApplyCompositionText(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			Invalidate(false);
			return;
		}
		var attributes = _textArea.SelectionAttributes?.Clone() ?? Document.GetAttributes(_caret.Index, _caret.Index)?.Clone() ?? new Attributes();

		Document.BeginEdit();
		if (Selection.Length > 0)
		{
			Document.RemoveAt(Selection.Start, Selection.Length);
			_caret.SetIndex(Selection.Start, false);
			SetSelection(null, false);
		}

		attributes.Underline = true;
		Document.InsertText(_caret.Index, text, attributes);
		Document.EndEdit();
		_caret.SetIndex(_caret.Index + text.Length, false);
		// SetSelection(null, true);
		Invalidate(false);
	}

	void BeginComposition()
	{
		if (_isComposingText || DocumentState == null)
			return;

		_caret.IndexChanged -= Caret_IndexChangedDuringComposition;
		_caret.IndexChanged += Caret_IndexChangedDuringComposition;
		_compositionState = DocumentState.Capture();
		_compositionTrackingScope = DocumentState.SuspendTracking();
		_isComposingText = true;
		_compositionBounds = CaretBounds;
		DocumentChanged?.Invoke(this, EventArgs.Empty);
	}

	private void Caret_IndexChangedDuringComposition(object? sender, EventArgs e)
	{
		if (_duringOnTextComposition)
			return;
		var index = _caret.Index;
		CommitTextComposition();
		_textArea.CaretIndex = index;
	}

	void EndComposition()
	{
		if (!_isComposingText)
			return;

		if (_compositionState != null)
			DocumentState?.RestoreSnapshot(_compositionState.Value, true);

		ResetComposition();
	}

	// Drops composition state without restoring its snapshot, which may belong to a previous document.
	void ResetComposition()
	{
		if (!_isComposingText)
			return;

		_caret.IndexChanged -= Caret_IndexChangedDuringComposition;
		_compositionState = null;
		_compositionTrackingScope?.Dispose();
		_compositionTrackingScope = null;
		_isComposingText = false;
		_compositionBounds = null;
		DocumentChanged?.Invoke(this, EventArgs.Empty);
		Invalidate(false);
	}

	protected override void OnTextComposition(TextCompositionEventArgs e)
	{
		base.OnTextComposition(e);
		if (e.IsActive)
			SetCompositionText(e.Text);
		else
			EndComposition();
		e.Handled = true;
	}

	private void SetCompositionText(string text)
	{
		_duringOnTextComposition = true;
		BeginComposition();
		if (_compositionState != null)
		{
			DocumentState?.RestoreSnapshot(_compositionState.Value, true);
			ApplyCompositionText(text);
			_compositionText = text;
		}
		_duringOnTextComposition = false;
	}

	internal void UpdateSelectionAttributes(Attributes selectionAttributes)
	{
		if (selectionAttributes != null)
		{
			Selection.Attributes = selectionAttributes;
			if (_isComposingText && _compositionText != null && !string.IsNullOrEmpty(_compositionText))
				SetCompositionText(_compositionText);
		}
	}
}
