using Eto.Forms;
using Eto.Drawing;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Eto.ExtendedRichTextArea.Model;
using Eto.ExtendedRichTextArea.Commands;
using Eto.ExtendedRichTextArea.SpellCheck;

namespace Eto.ExtendedRichTextArea;

partial class TextAreaDrawable : Drawable
{
	Document? _document;
	Document? _placeholder;
	readonly CaretBehavior _caret;
	readonly KeyboardBehavior _keyboard;
	readonly MouseBehavior _mouse;
#if DEBUG
	bool _isValid = true;
#endif
	Scrollable? _parentScrollable;
	DocumentRange? _selection;
	readonly ExtendedRichTextArea _textArea;
	bool _readOnly;

	DocumentState? _documentState;
	public DocumentState? DocumentState => _documentState;

	// Overlays painted after the text and before the caret (e.g. spell-check squiggles). The
	// collection repaints the control whenever it changes, so callers just add/remove entries.
	sealed class AdornmentCollection : Collection<ITextAdornment>
	{
		readonly TextAreaDrawable _owner;
		public AdornmentCollection(TextAreaDrawable owner) => _owner = owner;
		protected override void InsertItem(int index, ITextAdornment item) { base.InsertItem(index, item); _owner.Invalidate(false); }
		protected override void SetItem(int index, ITextAdornment item) { base.SetItem(index, item); _owner.Invalidate(false); }
		protected override void RemoveItem(int index) { base.RemoveItem(index); _owner.Invalidate(false); }
		protected override void ClearItems() { base.ClearItems(); _owner.Invalidate(false); }
	}

	/// <summary>
	/// Overlays painted on top of the text (after it, before the caret) — e.g. spell-check squiggles.
	/// Adding or removing an entry repaints the control automatically.
	/// </summary>
	public Collection<ITextAdornment> Adornments { get; }

	public Document? Placeholder
	{
		get => _placeholder;
		set
		{
			if (_placeholder != null)
				_placeholder.Changed -= Placeholder_Changed;
			_placeholder = value;
			if (_placeholder != null)
				_placeholder.Changed += Placeholder_Changed;
			if (_document == null || _document.Length == 0)
				Invalidate(false);
		}
	}

	private void Placeholder_Changed(object? sender, EventArgs e)
	{
		if (_document == null || _document.Length == 0)
			Invalidate(false);
	}

	public Document Document
	{
		get => _document ?? (Document = new Document());
		set
		{
			if (_document != null)
			{
				_document.Changed -= Document_Changed;
				_document.OverrideAttributes -= Document_OverrideAttributes;
				_document.DefaultAttributesChanged -= Document_DefaultAttributesChanged;
			}
			_document = value ?? new Document();
			_document.Changed += Document_Changed;
			_document.OverrideAttributes += Document_OverrideAttributes;
			_document.DefaultAttributesChanged += Document_DefaultAttributesChanged;
			_selection = null; // Clear stale selection from previous document
			_caret.SetIndex(0, true);
			CreateContextMenu();
			// Always refresh SelectionAttributes from the new document.
			// SetIndex above may skip the update when the caret is already at 0.
			_textArea.SelectionAttributes = _document.GetAttributes(0, 0);
			Invalidate(false);
			_documentState = CreateDocumentState(_document);
		}
	}

	protected override void OnEnabledChanged(EventArgs e)
	{
		base.OnEnabledChanged(e);
		Invalidate();
	}

	private void CreateContextMenu()
	{
		var menu = new ContextMenu();
		bool? lastAlwaysShowSelection = null;
		menu.Opening += (sender, e) =>
		{
			lastAlwaysShowSelection = AlwaysShowSelection;
			AlwaysShowSelection = true;
			UpdateSpellSuggestions(menu);
		};
		menu.Closed += (sender, e) =>
		{
			if (lastAlwaysShowSelection.HasValue)
				AlwaysShowSelection = lastAlwaysShowSelection.Value;
		};
		if (ReadOnly)
		{
			menu.Items.Add(new CopyCommand(this));
		}
		else
		{
			menu.Items.Add(new UndoCommand(this));
			menu.Items.Add(new RedoCommand(this));
			menu.Items.Add(new SeparatorMenuItem());
			menu.Items.Add(new CutCommand(this));
			menu.Items.Add(new CopyCommand(this));
			menu.Items.Add(new PasteCommand(this));
			menu.Items.Add(new PasteWithoutFormattingCommand(this));
		}
		ContextMenu = menu;
	}

	List<MenuItem>? _injectedSpellItems;

	// Inject spelling/grammar suggestions for the word at the caret (right-click moves the caret to
	// the clicked position) at the top of the context menu, removing any from a previous open.
	void UpdateSpellSuggestions(ContextMenu menu)
	{
		if (_injectedSpellItems != null)
		{
			foreach (var item in _injectedSpellItems)
				menu.Items.Remove(item);
			_injectedSpellItems = null;
		}

		if (ReadOnly)
			return;
		var controller = _textArea.SpellCheckController;
		if (controller == null)
			return;

		var suggestions = controller.CreateSuggestionMenuItems(_caret.Index);
		if (suggestions.Count == 0)
			return;

		var injected = new List<MenuItem>(suggestions);
		injected.Add(new SeparatorMenuItem());
		for (int i = 0; i < injected.Count; i++)
			menu.Items.Insert(i, injected[i]);
		_injectedSpellItems = injected;
	}

	public bool ReadOnly
	{
		get => _readOnly;
		set
		{
			if (_readOnly != value)
			{
				_readOnly = value;
				Invalidate();
				CreateContextMenu();
			}
		}
	}

	private void Document_DefaultAttributesChanged(object? sender, EventArgs e)
	{
		if (Selection?.Length > 0)
			_textArea.SelectionAttributes = Document.GetAttributes(Selection.Start, Selection.End);
		else
			_textArea.SelectionAttributes = Document.GetAttributes(_caret.Index, _caret.Index);
	}

	public Attributes HighlightAttributes { get; set; } = new Attributes { Background = new SolidBrush(SystemColors.Highlight), Foreground = new SolidBrush(SystemColors.HighlightText) };

	private void Document_OverrideAttributes(object? sender, OverrideAttributesEventArgs e)
	{
		if (!Enabled)
			return;
		if (Selection?.Length > 0 && (AlwaysShowSelection || HasFocus))
			e.NewAttributes.Add(new AttributeRange(Selection.Start, Selection.End, HighlightAttributes));
	}
	
	public event EventHandler<EventArgs>? DocumentChanged;

	private void Document_Changed(object? sender, EventArgs e)
	{
		_caret.CalculateCaretBounds();
		Size = Size.Ceiling(Document.Size);
#if DEBUG
		_isValid = Document.IsValid();
#endif
		Invalidate(false);

		// Shouldn't be needed after https://github.com/picoe/Eto/pull/2709
		if (Platform.IsWpf)
			_parentScrollable?.UpdateScrollSizes();
		
		DocumentChanged?.Invoke(this, EventArgs.Empty);
	}

	internal CaretBehavior Caret => _caret;
	internal KeyboardBehavior Keyboard => _keyboard;
	internal MouseBehavior Mouse => _mouse;
	internal ExtendedRichTextArea TextArea => _textArea;

	private sealed class ExtraState
	{
		public ExtraState(int caretIndex, DocumentRange? selection, Point scrollPosition)
		{
			CaretIndex = caretIndex;
			Selection = selection;
			ScrollPosition = scrollPosition;
		}
		public int CaretIndex { get; }
		public DocumentRange? Selection { get; }
		public Point ScrollPosition { get; }
	}

	private sealed class CompositionState
	{
		public CompositionState(List<IBlockElement> elements, Attributes? documentAttributes, ExtraState extraState)
		{
			Elements = elements;
			DocumentAttributes = documentAttributes;
			ExtraState = extraState;
		}

		public List<IBlockElement> Elements { get; }
		public Attributes? DocumentAttributes { get; }
		public ExtraState ExtraState { get; }
	}

	DocumentState CreateDocumentState(Document doc)
	{
		var state = new DocumentState(doc);
		state.CaptureExtra = () => new ExtraState(_caret.Index, _selection?.Clone(), _textArea.ScrollPosition);
		state.RestoreExtra = s =>
		{
			if (s is ExtraState extra)
			{
				_caret.SetIndex(extra.CaretIndex, false);
				SetSelection(extra.Selection, true);
			}
		};
		state.RestoreExtraPost = s =>
		{
			if (s is ExtraState extra)
				_textArea.ScrollPosition = extra.ScrollPosition;
		};
		return state;
	}

	public TextAreaDrawable(ExtendedRichTextArea textArea) : base(false)
	{
		_textArea = textArea;
		_caret = new CaretBehavior(this);
		_keyboard = new KeyboardBehavior(this, _caret);
		_mouse = new MouseBehavior(this, _caret);
		Adornments = new AdornmentCollection(this);
		CanFocus = true;
	}

	public bool HasSelection => _selection != null && _selection.Length > 0;

	public DocumentRange Selection
	{
		get
		{
			if (_selection == null)
				SetSelection(Document.GetRange(_caret.Index, _caret.Index), false);
			
			return _selection!;
		}
	}
	
	DocumentRange? _lastSetSelection;

	public void SetSelection(DocumentRange? value, bool updateSelectionAttributes)
	{
		if (_selection == value)
			return;

		_selection = value;
		if (updateSelectionAttributes)
		{
			if (_selection != null)
			{
				if (!ReferenceEquals(_selection.Document, Document))
					throw new ArgumentOutOfRangeException(nameof(value), "Selection must be from this document");

				_textArea.SelectionAttributes = Document.GetAttributes(_selection.Start, _selection.End);
			}
			else
				_textArea.SelectionAttributes = Document.GetAttributes(_caret.Index, _caret.Index);
		}
		Invalidate(false);
		if (_selection != _lastSetSelection)
		{
			_lastSetSelection = _selection;
			SelectionChanged?.Invoke(this, EventArgs.Empty);
		}
	}

	public event EventHandler<EventArgs>? SelectionChanged;
	



	protected override void OnLoad(EventArgs e)
	{
		base.OnLoad(e);
		if (Parent is Scrollable scrollable)
		{
			_parentScrollable = scrollable;
			_parentScrollable.Scroll += parentScrollable_Scroll;
		}
	}

	protected override void OnUnLoad(EventArgs e)
	{
		base.OnUnLoad(e);
		if (_parentScrollable != null)
		{
			_parentScrollable.Scroll -= parentScrollable_Scroll;
			_parentScrollable = null;
		}
	}

	private void parentScrollable_Scroll(object? sender, ScrollEventArgs e)
	{
		Invalidate(false);
	}

	protected override void OnPaint(PaintEventArgs e)
	{
		base.OnPaint(e);

#if DEBUG
		if (!_isValid)
		{
			using var invalidText = new FormattedText { Text = "INVALID", Font = SystemFonts.Bold(), ForegroundBrush = Brushes.Red };
			var size = invalidText.Measure();
			var point = new PointF(Width - size.Width, 0);
			e.Graphics.DrawText(invalidText, point);
		}
#endif
		var clip = e.ClipRectangle;
		if (_parentScrollable != null && Loaded)
		{
			try
			{
				var rect = _parentScrollable.RectangleToScreen(new RectangleF(_parentScrollable.ClientSize));
				clip.Intersect(RectangleFromScreen(rect));
			}
			catch
			{
				// Eto.Wpf currently has an issue getting client size before loaded during a drawing operation, so ignore for now.
			}
		}
		// _selection?.Paint(e.Graphics);

		var screen = ParentWindow?.Screen ?? Screen.PrimaryScreen;
		var document = Document;

		if (_placeholder != null && document.Length == 0)
		{
			_placeholder.ScreenScale = screen.Scale;
			_placeholder.Paint(e.Graphics, clip);
		}
		else
		{
			document.ScreenScale = screen.Scale;
			document.Paint(e.Graphics, clip);

			for (int i = 0; i < Adornments.Count; i++)
				Adornments[i].Paint(document, e.Graphics, clip);
		}

		_caret.Paint(e);
	}

	internal float Scale => ParentWindow?.Screen.Scale ?? 1;

	public RectangleF CaretBounds => _caret.CaretBounds;

	public bool CanUndo => DocumentState?.CanUndo ?? false;

	public bool CanRedo => DocumentState?.CanRedo ?? false;

	public bool AlwaysShowSelection { get; internal set; }

	public void SetAvailableSize(Size size)
	{
		if (size.Width <= 0 || size.Height <= 0)
			return;
		if (_document != null)
			_document.AvailableSize = size;
		if (_placeholder != null)
			_placeholder.AvailableSize = size;
	}

	public bool Undo()
	{
		if (_isComposingText)
			CancelTextComposition();
		if (DocumentState == null || !DocumentState.Undo())
			return false;
		Invalidate(false);
		return true;
	}

	public bool Redo()
	{
		if (_isComposingText)
			CancelTextComposition();
		if (DocumentState == null || !DocumentState.Redo())
			return false;
		Invalidate(false);
		return true;
	}

	internal void SetCaretSelection(int lastCaretIndex, bool extendSelection, bool useOriginalStart = true)
	{
		if (lastCaretIndex == _caret.Index)
			return;

		if (extendSelection)
		{
			if (_selection != null)
			{
				if (useOriginalStart)
					lastCaretIndex = _selection.OriginalStart;
				else
					lastCaretIndex = _caret.Index < _selection.Start ? _selection.End : _selection.Start;
			}
			SetSelection(Document.GetRange(lastCaretIndex, _caret.Index), true);
		}
		else
		{
			SetSelection(null, true); // Document.GetRange(_caret.Index, _caret.Index);
		}
	}

	protected override void OnGotFocus(EventArgs e)
	{
		base.OnGotFocus(e);
		if (!AlwaysShowSelection)
			Invalidate();
	}

	protected override void OnLostFocus(EventArgs e)
	{
		base.OnLostFocus(e);
		if (!AlwaysShowSelection)
			Invalidate();
	}
}
