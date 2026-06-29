using System;
using System.Collections.Generic;
using System.Threading;

namespace Eto.ExtendedRichTextArea.SpellCheck;

/// <summary>
/// The category of a <see cref="TextProblem"/>, used to choose how it is rendered
/// (e.g. a red squiggle for spelling, a different colour for grammar).
/// </summary>
public enum TextProblemKind
{
	/// <summary>A misspelled word.</summary>
	Spelling,
	/// <summary>A grammatical issue spanning one or more words.</summary>
	Grammar
}

/// <summary>
/// Selects which categories of problem an <see cref="ITextChecker"/> looks for. A <c>[Flags]</c>
/// enum so callers can request spelling only, grammar only, or both.
/// </summary>
[Flags]
public enum TextCheckTypes
{
	/// <summary>Check nothing. Leaves a checker assigned but reporting no problems.</summary>
	None = 0,
	/// <summary>Flag misspelled words (<see cref="TextProblemKind.Spelling"/>).</summary>
	Spelling = 1,
	/// <summary>Flag grammatical issues (<see cref="TextProblemKind.Grammar"/>).</summary>
	Grammar = 2,
	/// <summary>Flag both spelling and grammar problems.</summary>
	All = Spelling | Grammar
}

/// <summary>
/// A single problem found by an <see cref="ITextChecker"/>, expressed as a span within
/// the text that was checked. Offsets are relative to the start of that text, not the document.
/// </summary>
public readonly struct TextProblem
{
	/// <summary>Offset of the problem, relative to the start of the checked text.</summary>
	public int Start { get; }

	/// <summary>Number of characters covered by the problem.</summary>
	public int Length { get; }

	/// <summary>The category of problem, used to select the decoration.</summary>
	public TextProblemKind Kind { get; }

	/// <summary>Optional human-readable description, e.g. for a tooltip or grammar explanation.</summary>
	public string? Message { get; }

	/// <summary>
	/// Optional replacement suggestions for the <b>entire</b> problem span, supplied by the checker.
	/// This is how grammar corrections are delivered (a grammar span covers a phrase, not a single
	/// word, so per-word spelling lookups don't apply). An empty-string entry means "delete the span"
	/// (e.g. a repeated word). For spelling problems this is normally null — suggestions for those are
	/// fetched lazily per word via <see cref="ITextChecker.GetSuggestions"/> when the menu opens.
	/// </summary>
	public IReadOnlyList<string>? Suggestions { get; }

	/// <summary>Gets the exclusive end offset of the problem (<see cref="Start"/> + <see cref="Length"/>).</summary>
	public int End => Start + Length;

	public TextProblem(int start, int length, TextProblemKind kind = TextProblemKind.Spelling, string? message = null, IReadOnlyList<string>? suggestions = null)
	{
		Start = start;
		Length = length;
		Kind = kind;
		Message = message;
		Suggestions = suggestions;
	}
}

/// <summary>
/// A pluggable spell/grammar checking engine. Implementations are supplied by the host
/// (e.g. wrapping Hunspell, a platform-native checker, or a grammar service) and assigned
/// to <see cref="ExtendedRichTextArea.SpellChecker"/>.
/// </summary>
/// <remarks>
/// <see cref="Check"/> is invoked on a background thread and is given only a plain string,
/// so implementations need no knowledge of the document model and must not touch UI state.
/// Implementations should be safe to call from a thread other than the UI thread.
/// </remarks>
public interface ITextChecker
{
	/// <summary>
	/// Gets or sets which categories of problem <see cref="Check"/> reports. Defaults to
	/// <see cref="TextCheckTypes.All"/>. Setting it to e.g. <see cref="TextCheckTypes.Spelling"/>
	/// suppresses grammar squiggles (and vice versa); <see cref="TextCheckTypes.None"/> reports
	/// nothing without unsetting the checker. Changing the value raises <see cref="DictionaryChanged"/>
	/// so the view discards cached results and re-checks against the new selection.
	/// </summary>
	TextCheckTypes CheckTypes { get; set; }

	/// <summary>
	/// Gets or sets whether words written entirely in capitals (e.g. <c>NASA</c>, <c>HTTP</c>) are
	/// spell-checked. Defaults to <c>false</c>: like the platform spell-checkers, all-caps tokens are
	/// treated as acronyms and skipped. Set to <c>true</c> to check them as well. Changing the value
	/// raises <see cref="DictionaryChanged"/> so the view discards cached results and re-checks.
	/// </summary>
	bool CheckUppercaseWords { get; set; }

	/// <summary>
	/// Finds spelling/grammar problems in <paramref name="text"/>. Called on a background thread,
	/// typically once per paragraph. Returned offsets are relative to <paramref name="text"/>.
	/// Only problems whose kind is selected by <see cref="CheckTypes"/> are returned.
	/// Honour <paramref name="token"/> for cancellation when the text changes again mid-check.
	/// </summary>
	IReadOnlyList<TextProblem> Check(string text, CancellationToken token);

	/// <summary>
	/// Returns replacement suggestions for a flagged word, best match first.
	/// May be called on the UI thread (e.g. when building a context menu), so keep it fast.
	/// </summary>
	/// <param name="word">The flagged word to suggest corrections for.</param>
	/// <param name="context">
	/// Optional surrounding text (typically the word's paragraph) used to identify the language the word
	/// is written in. A lone word is often ambiguous across the user's installed languages — many will
	/// each propose an equally-close correction — so without context a multi-language checker can't tell
	/// which language's suggestions to prefer and defaults to its primary, returning wrong-language
	/// guesses (e.g. English corrections for a French word). Given the sentence, the checker can prefer
	/// the right language. Pass null/empty when no context is available; the checker then falls back to
	/// its best language-agnostic ordering.
	/// </param>
	IReadOnlyList<string> GetSuggestions(string word, string? context = null);

	/// <summary>
	/// Adds a word to the user dictionary so it is no longer flagged.
	/// Implementations should raise <see cref="DictionaryChanged"/> so the view re-checks.
	/// </summary>
	void AddToDictionary(string word);

	/// <summary>
	/// Removes a previously user-added (<see cref="AddToDictionary"/>) word from the user dictionary so
	/// it is flagged again. A no-op for words that were not user-added. Implementations should raise
	/// <see cref="DictionaryChanged"/> so the view re-checks. May be called on the UI thread.
	/// </summary>
	void RemoveFromDictionary(string word);

	/// <summary>
	/// Gets whether <paramref name="word"/> was added to the user dictionary via
	/// <see cref="AddToDictionary"/> (i.e. it can be removed with <see cref="RemoveFromDictionary"/>).
	/// Returns false for words that are correct only because they are in the built-in dictionary.
	/// Used to decide whether to offer an "unlearn" command. May be called on the UI thread.
	/// </summary>
	bool IsWordLearned(string word);

	/// <summary>
	/// Raised when the dictionary (or any checker state affecting results) changes, signalling
	/// that the view should discard cached results and re-check.
	/// </summary>
	event EventHandler? DictionaryChanged;
}
