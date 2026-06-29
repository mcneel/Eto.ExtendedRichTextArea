using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Eto.Drawing;
using Eto.Forms;
using Eto.ExtendedRichTextArea.Model;

namespace Eto.ExtendedRichTextArea.SpellCheck;

/// <summary>
/// Drives an <see cref="ITextChecker"/> for an <see cref="ExtendedRichTextArea"/>: schedules
/// background checking, caches results, and paints the squiggly underlines.
/// </summary>
/// <remarks>
/// Created and owned by <see cref="ExtendedRichTextArea.SpellChecker"/>; hosts normally just
/// assign a checker and never touch this class directly, other than for the optional
/// suggestion/hit-test helpers used to build a context menu.
///
/// <para><b>Performance.</b> Checking is demand-driven and paragraph-granular:</para>
/// <list type="bullet">
/// <item>Only paragraphs intersecting the visible (clip) region are considered each paint, so
/// cost is independent of document size and scrolling naturally pulls in new paragraphs.</item>
/// <item>Results are cached per <see cref="IBlockElement"/> in a <see cref="ConditionalWeakTable{TKey,TValue}"/>,
/// keyed by the exact text checked. A paragraph whose text still matches its cache is drawn
/// from cache and never re-checked; a paragraph whose text changed is re-checked (and its stale
/// squiggles suppressed until the new result arrives).</item>
/// <item>Checks are debounced (so typing doesn't trigger per-keystroke work) and run on a
/// background thread over snapshotted strings, then marshalled back to the UI thread. A new
/// edit cancels the in-flight pass.</item>
/// </list>
/// </remarks>
internal sealed class SpellCheckController : ITextAdornment, IDisposable
{
	sealed class Entry
	{
		public string CheckedText = string.Empty;
		public IReadOnlyList<TextProblem> Problems = Array.Empty<TextProblem>();
		public int Epoch;
	}

	readonly ExtendedRichTextArea _textArea;
	readonly ConditionalWeakTable<IBlockElement, Entry> _cache = new ConditionalWeakTable<IBlockElement, Entry>();
	readonly HashSet<IBlockElement> _pending = new HashSet<IBlockElement>();

	ITextChecker? _checker;
	UITimer? _debounce;
	CancellationTokenSource? _cts;
	Document? _subscribedDocument;
	bool _registered;
	bool _timerRunning;
	// Bumped on reset (document swap, dictionary change). A cache entry is only valid for the
	// current epoch, so a reset forces every paragraph to re-check even when its text is unchanged.
	int _epoch;

	// Rendering/menu options live on the control so the host can configure them explicitly (see
	// ExtendedRichTextArea.SpellCheckOptions); the controller always reads the current instance.
	SpellCheckOptions Options => _textArea.SpellCheckOptions;

	internal SpellCheckController(ExtendedRichTextArea textArea)
	{
		_textArea = textArea;
	}

	/// <summary>Gets or sets the checking engine. Setting it (re)starts checking; null disables it.</summary>
	public ITextChecker? Checker
	{
		get => _checker;
		set
		{
			if (ReferenceEquals(_checker, value))
				return;
			if (_checker != null)
				_checker.DictionaryChanged -= Checker_DictionaryChanged;
			_checker = value;
			if (_checker != null)
				_checker.DictionaryChanged += Checker_DictionaryChanged;

			if (_checker != null)
				Enable();
			else
				Disable();

			Reset();
		}
	}

	void Enable()
	{
		if (_registered)
			return;
		_registered = true;
		if (!_textArea.Adornments.Contains(this))
			_textArea.Adornments.Add(this);
		_textArea.DocumentChanged += TextArea_DocumentChanged;
		BindDocument();
	}

	void Disable()
	{
		if (!_registered)
			return;
		_registered = false;
		_textArea.Adornments.Remove(this);
		_textArea.DocumentChanged -= TextArea_DocumentChanged;
		if (_subscribedDocument != null)
		{
			_subscribedDocument.Changed -= Document_Changed;
			_subscribedDocument = null;
		}
	}

	void BindDocument()
	{
		var document = _textArea.Document;
		if (ReferenceEquals(_subscribedDocument, document))
			return;
		if (_subscribedDocument != null)
			_subscribedDocument.Changed -= Document_Changed;
		_subscribedDocument = document;
		_subscribedDocument.Changed += Document_Changed;
	}

	void TextArea_DocumentChanged(object? sender, EventArgs e)
	{
		// The Document instance was swapped; move the change hook to the new document and drop all cached results.
		BindDocument();
		Reset();
	}

	void Document_Changed(object? sender, EventArgs e)
	{
		// A content edit: push out the debounce clock (so a burst of typing checks once, after the
		// pause) and trigger a paint, which detects the changed paragraphs and queues the re-check.
		ScheduleCheck(restart: true);
		_textArea.InvalidateContent();
	}

	void Checker_DictionaryChanged(object? sender, EventArgs e) => Reset();

	void Reset()
	{
		_cts?.Cancel();
		_cts = null;
		_pending.Clear();
		// ConditionalWeakTable has no Clear on the older targets, so instead of evicting entries we
		// bump the epoch: every cached entry is now from a previous epoch and is treated as stale,
		// forcing a re-check on the next paint even when the paragraph text is unchanged (e.g. a word
		// was just added to the dictionary).
		_epoch++;
		_textArea.InvalidateContent();
	}

	void ITextAdornment.Paint(Document document, Graphics graphics, RectangleF clipBounds)
	{
		var checker = _checker;
		if (checker == null)
			return;

		graphics.AntiAlias = true;

		// Only paragraphs intersecting the paint's clip are revisited here — the clip can be a tiny
		// region (e.g. a caret-blink repaint), so _pending is NOT cleared first: paragraphs queued by
		// an earlier full paint must stay queued even when this paint only covers the caret's line.
		foreach (var obj in document)
		{
			if (obj is not IBlockElement block)
				continue;
			if (!block.Bounds.Intersects(clipBounds))
				continue;

			var text = block.Text;
			if (_cache.TryGetValue(block, out var entry) && entry.Epoch == _epoch && entry.CheckedText == text)
			{
				PaintProblems(document, graphics, block, entry, clipBounds);
				_pending.Remove(block); // up to date — no longer needs a check
			}
			else
			{
				// Unchecked, edited, or invalidated since last check: suppress stale squiggles and queue a re-check.
				_pending.Add(block);
			}
		}

		if (_pending.Count > 0)
			ScheduleCheck(restart: false);
	}

	void PaintProblems(Document document, Graphics graphics, IBlockElement block, Entry entry, RectangleF clipBounds)
	{
		if (entry.Problems.Count == 0)
			return;
		var docStart = block.DocumentStart;
		for (int i = 0; i < entry.Problems.Count; i++)
		{
			var problem = entry.Problems[i];
			var range = document.GetRange(docStart + problem.Start, docStart + problem.End);
			var pen = problem.Kind == TextProblemKind.Grammar ? Options.GrammarPen : Options.SpellingPen;
			foreach (var bounds in range.Bounds)
			{
				if (!bounds.Intersects(clipBounds))
					continue;
				DrawSquiggle(graphics, bounds, pen);
			}
		}
	}

	void DrawSquiggle(Graphics graphics, RectangleF bounds, Pen pen)
	{
		var options = Options;
		var step = options.Wavelength / 2f;
		if (step <= 0)
			return;
		var amplitude = options.Amplitude;
		var baseY = bounds.Bottom - 1f;
		var points = new List<PointF>();
		var up = false;
		for (var x = bounds.Left; x < bounds.Right; x += step)
		{
			points.Add(new PointF(x, up ? baseY - amplitude : baseY));
			up = !up;
		}
		points.Add(new PointF(bounds.Right, up ? baseY - amplitude : baseY));
		if (points.Count >= 2)
			graphics.DrawLines(pen, points.ToArray());
	}

	// restart: true resets the debounce clock (a content edit). restart: false only starts the timer
	// if it isn't already running (a paint that found pending work) so incidental repaints — e.g. the
	// blinking caret — don't keep pushing the check out.
	void ScheduleCheck(bool restart)
	{
		if (_checker == null)
			return;
		_debounce ??= new UITimer(OnDebounceElapsed);
		_debounce.Interval = Options.DebounceInterval;
		if (restart)
		{
			_debounce.Stop();
			_debounce.Start();
			_timerRunning = true;
		}
		else if (!_timerRunning)
		{
			_debounce.Start();
			_timerRunning = true;
		}
	}

	void OnDebounceElapsed(object? sender, EventArgs e)
	{
		_debounce?.Stop();
		_timerRunning = false;
		var checker = _checker;
		if (checker == null || _pending.Count == 0)
			return;

		// Snapshot text on the UI thread; the background pass only ever sees immutable strings.
		var work = _pending.Select(block => (block, text: block.Text)).ToList();
		_pending.Clear();

		_cts?.Cancel();
		var cts = new CancellationTokenSource();
		_cts = cts;
		var token = cts.Token;
		var epoch = _epoch;

		Task.Run(() =>
		{
			foreach (var (block, text) in work)
			{
				if (token.IsCancellationRequested)
					return;
				IReadOnlyList<TextProblem> problems;
				try
				{
					problems = checker.Check(text, token) ?? Array.Empty<TextProblem>();
				}
				catch
				{
					continue; // a faulty checker shouldn't take down the pass
				}
				if (token.IsCancellationRequested)
					return;
				var resultBlock = block;
				var resultText = text;
				var resultProblems = problems;
				Application.Instance.AsyncInvoke(() => ApplyResult(resultBlock, resultText, resultProblems, epoch));
			}
		}, token);
	}

	void ApplyResult(IBlockElement block, string text, IReadOnlyList<TextProblem> problems, int epoch)
	{
		// A completed result is valid as long as the epoch still matches (no document swap / dictionary
		// change) — the cycle's token may have been cancelled by a newer pass, but the work is already
		// done, so discarding it here would drop squiggles that only reappear on a later repaint.
		if (epoch != _epoch)
			return;
		var entry = new Entry { CheckedText = text, Problems = problems, Epoch = epoch };
#if NETSTANDARD2_0 || NET462
		_cache.Remove(block);
		_cache.Add(block, entry);
#else
		_cache.AddOrUpdate(block, entry);
#endif
		// Only repaint if the cached text still reflects the live paragraph; otherwise a newer
		// edit already queued another check and this would just flash stale squiggles.
		if (block.Text == text)
		{
			_pending.Remove(block); // result is current; drop it so a pending debounce won't re-check it
			_textArea.InvalidateContent();
		}
	}

	/// <summary>
	/// Finds the spelling/grammar problem covering a document index, if any. <paramref name="problem"/>'s
	/// <see cref="TextProblem.Start"/> is returned as a <b>document-absolute</b> offset, and
	/// <paramref name="word"/> is the flagged text — suitable for feeding
	/// <see cref="ITextChecker.GetSuggestions"/> and building a context menu.
	/// </summary>
	public bool TryGetProblem(int documentIndex, out TextProblem problem, out string word)
	{
		problem = default;
		word = string.Empty;
		var document = _textArea.Document;
		foreach (var obj in document)
		{
			if (obj is not IBlockElement block)
				continue;
			var docStart = block.DocumentStart;
			if (documentIndex < docStart || documentIndex > docStart + block.Length)
				continue;
			if (!_cache.TryGetValue(block, out var entry) || entry.Epoch != _epoch)
				return false;
			var text = block.Text;
			var relative = documentIndex - docStart;
			var found = false;
			var bestLength = int.MaxValue;
			for (int i = 0; i < entry.Problems.Count; i++)
			{
				var candidate = entry.Problems[i];
				if (relative < candidate.Start || relative >= candidate.End)
					continue;
				if (candidate.End > text.Length)
					continue;
				// Prefer the most specific (narrowest) problem so a word-level spelling error wins
				// over a sentence-level grammar span that also covers this point.
				if (candidate.Length >= bestLength)
					continue;
				bestLength = candidate.Length;
				word = text.Substring(candidate.Start, candidate.Length);
				problem = new TextProblem(docStart + candidate.Start, candidate.Length, candidate.Kind, candidate.Message, candidate.Suggestions);
				found = true;
			}
			return found;
		}
		return false;
	}

	// Resolves the word at a document index using the document's own word boundaries, so suggestions
	// target the clicked word even when the covering problem is a multi-word grammar span.
	bool TryGetWordAt(int documentIndex, out int start, out string word)
	{
		start = 0;
		word = string.Empty;
		foreach (var (text, wordStart) in _textArea.Document.EnumerateWords(documentIndex, true))
		{
			// EnumerateWords returns the first word at/after the index; only use it when it actually
			// contains the click (otherwise the click was in whitespace/between words).
			if (!string.IsNullOrEmpty(text) && documentIndex >= wordStart && documentIndex <= wordStart + text.Length)
			{
				start = wordStart;
				word = text;
				return true;
			}
			break;
		}
		return false;
	}

	/// <summary>Convenience passthrough to the active checker's suggestions; empty when no checker is set.</summary>
	public IReadOnlyList<string> GetSuggestions(string word, string? context = null) => _checker?.GetSuggestions(word, context) ?? Array.Empty<string>();

	// The text of the paragraph containing a document index. Passed to the checker as language context so
	// it can identify which language the clicked word is written in (a lone word is often ambiguous).
	string GetParagraphText(int documentIndex)
	{
		foreach (var obj in _textArea.Document)
		{
			if (obj is not IBlockElement block)
				continue;
			var docStart = block.DocumentStart;
			if (documentIndex >= docStart && documentIndex <= docStart + block.Length)
				return block.Text;
		}
		return string.Empty;
	}

	/// <summary>
	/// Builds context-menu items for the spelling/grammar problem at <paramref name="documentIndex"/>:
	/// replacement suggestions (each applies the fix when invoked), plus "add to dictionary" for
	/// spelling. Returns an empty list when there is no problem at that index, so the caller can decide
	/// whether to add a separator. Replacing preserves the original formatting.
	/// </summary>
	public IReadOnlyList<MenuItem> CreateSuggestionMenuItems(int documentIndex)
	{
		var items = new List<MenuItem>();
		var checker = _checker;
		if (checker == null)
			return items;

		if (TryGetProblem(documentIndex, out var problem, out var problemWord))
		{
			if (problem.Kind == TextProblemKind.Grammar)
				BuildGrammarItems(items, problem);
			else
				BuildSpellingItems(items, checker, documentIndex, problem, problemWord);
		}
		else
		{
			// Not flagged — but if the word here was user-added to the dictionary, offer to remove it.
			BuildLearnedWordItems(items, checker, documentIndex);
		}

		return items;
	}

	// A word that isn't flagged but was added via AddToDictionary: offer "remove from dictionary".
	void BuildLearnedWordItems(List<MenuItem> items, ITextChecker checker, int documentIndex)
	{
		var label = Options.RemoveFromDictionaryText;
		if (string.IsNullOrEmpty(label))
			return;
		if (!TryGetWordAt(documentIndex, out _, out var word) || !checker.IsWordLearned(word))
			return;
		items.Add(new ButtonMenuItem((sender, e) => checker.RemoveFromDictionary(word)) { Text = label });
	}

	// Grammar problems span a phrase, so replacements come from the checker (TextProblem.Suggestions)
	// and replace the whole span — never per-word spelling alternates.
	void BuildGrammarItems(List<MenuItem> items, TextProblem problem)
	{
		var options = Options;
		var start = problem.Start;
		var length = problem.Length;
		var suggestions = problem.Suggestions;
		if (suggestions != null)
		{
			var shown = 0;
			foreach (var suggestion in suggestions)
			{
				if (shown >= options.MaxSuggestions)
					break;
				var replacement = suggestion;
				var label = string.IsNullOrEmpty(replacement) ? options.DeleteSuggestionText : replacement;
				items.Add(new ButtonMenuItem((sender, e) => ApplySuggestion(start, length, replacement)) { Text = label });
				shown++;
			}
		}

		if (items.Count > 0)
			return;

		// No actionable correction: show the explanation if there is one, else a disabled placeholder.
		if (!string.IsNullOrEmpty(problem.Message))
			items.Add(new ButtonMenuItem { Text = problem.Message, Enabled = false });
		else if (!string.IsNullOrEmpty(options.NoSuggestionsText))
			items.Add(new ButtonMenuItem { Text = options.NoSuggestionsText, Enabled = false });
	}

	// Spelling problems are word-level: target the actual word under the click (the problem span is
	// the word, but narrowing via document word boundaries is robust) and look suggestions up lazily.
	void BuildSpellingItems(List<MenuItem> items, ITextChecker checker, int documentIndex, TextProblem problem, string problemWord)
	{
		int start, length;
		string word;
		if (TryGetWordAt(documentIndex, out var wordStart, out var wordText))
		{
			start = wordStart;
			length = wordText.Length;
			word = wordText;
		}
		else
		{
			start = problem.Start;
			length = problem.Length;
			word = problemWord;
		}

		var options = Options;
		var shown = 0;
		foreach (var suggestion in GetSuggestions(word, GetParagraphText(documentIndex)))
		{
			if (shown >= options.MaxSuggestions)
				break;
			var replacement = suggestion;
			items.Add(new ButtonMenuItem((sender, e) => ApplySuggestion(start, length, replacement)) { Text = replacement });
			shown++;
		}

		if (shown == 0 && !string.IsNullOrEmpty(options.NoSuggestionsText))
			items.Add(new ButtonMenuItem { Text = options.NoSuggestionsText, Enabled = false });

		if (!string.IsNullOrEmpty(options.AddToDictionaryText))
		{
			if (items.Count > 0)
				items.Add(new SeparatorMenuItem());
			items.Add(new ButtonMenuItem((sender, e) => checker.AddToDictionary(word)) { Text = options.AddToDictionaryText });
		}
	}

	void ApplySuggestion(int start, int length, string replacement)
	{
		var document = _textArea.Document;
		if (start < 0 || start + length > document.Length)
			return;

		// Preserve the flagged word's formatting across the replacement.
		var attributes = document.GetAttributes(start, start + length);
		document.BeginEdit();
		document.RemoveAt(start, length);
		document.InsertText(start, replacement, attributes);
		document.EndEdit();
		_textArea.CaretIndex = start + replacement.Length;
		_textArea.Focus();
	}

	public void Dispose()
	{
		_cts?.Cancel();
		_cts = null;
		_debounce?.Stop();
		_debounce?.Dispose();
		_debounce = null;
		if (_checker != null)
			_checker.DictionaryChanged -= Checker_DictionaryChanged;
		Disable();
	}
}
