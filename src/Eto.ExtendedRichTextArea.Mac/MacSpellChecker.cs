using System;
using System.Collections.Generic;
using System.Threading;

#if __MACOS__
using AppKit;
using Foundation;
#else
using MonoMac.AppKit;
using MonoMac.Foundation;
#endif

using Eto.Forms;
using Eto.ExtendedRichTextArea.SpellCheck;

namespace Eto.ExtendedRichTextArea.Mac;

/// <summary>
/// An <see cref="ITextChecker"/> backed by macOS's <c>NSSpellChecker</c>, the same engine that
/// powers system-wide spelling and grammar checking. Provides spelling and grammar squiggles,
/// suggestions, and "Learn" (add to dictionary) using the user's configured languages.
/// </summary>
/// <remarks>
/// Offsets from <c>NSSpellChecker</c> are UTF-16 code-unit ranges, which line up exactly with
/// .NET string indices (and therefore the document offsets used by the control), so no conversion
/// is needed. <see cref="Check"/> is invoked on a background thread by the controller, but
/// <c>NSSpellChecker</c> is AppKit and must run on the UI thread, so the native calls are marshalled
/// there via <see cref="Application.Invoke(Action)"/>.
/// </remarks>
public sealed class MacSpellChecker : ITextChecker
{
	readonly string? _language;
	readonly nint _tag;
	TextCheckTypes _checkTypes = TextCheckTypes.All;
	bool _checkUppercaseWords;
	NSSpellChecker _checker;

	public event EventHandler? DictionaryChanged;

	/// <inheritdoc/>
	public TextCheckTypes CheckTypes
	{
		get => _checkTypes;
		set
		{
			if (_checkTypes == value)
				return;
			_checkTypes = value;
			DictionaryChanged?.Invoke(this, EventArgs.Empty);
		}
	}

	/// <inheritdoc/>
	public bool CheckUppercaseWords
	{
		get => _checkUppercaseWords;
		set
		{
			if (_checkUppercaseWords == value)
				return;
			_checkUppercaseWords = value;
			DictionaryChanged?.Invoke(this, EventArgs.Empty);
		}
	}

	/// <param name="language">
	/// BCP-47 language tag to check against (e.g. "en_US"). When null, NSSpellChecker uses the
	/// user's automatic/selected language.
	/// </param>
	public MacSpellChecker(string? language = null)
	{
		_language = language;
		_tag = (nint)NSSpellChecker.UniqueSpellDocumentTag;
		_checker = new NSSpellChecker();
		if (!string.IsNullOrEmpty(_language))
			_checker.Language = _language;
	}

	public IReadOnlyList<TextProblem> Check(string text, CancellationToken token)
	{
		if (string.IsNullOrEmpty(text))
			return Array.Empty<TextProblem>();

		var checkTypes = _checkTypes;
		if (checkTypes == TextCheckTypes.None)
			return Array.Empty<TextProblem>();

		// Only ask NSSpellChecker for the categories the caller selected.
		NSTextCheckingType nativeTypes = 0;
		if ((checkTypes & TextCheckTypes.Spelling) != 0)
			nativeTypes |= NSTextCheckingType.Spelling;
		if ((checkTypes & TextCheckTypes.Grammar) != 0)
			nativeTypes |= NSTextCheckingType.Grammar;
		var types = (NSTextCheckingTypes)nativeTypes;
		var results = _checker.CheckString(text, new NSRange(0, text.Length), types, (NSDictionary?)null, _tag, out _, out _);

		var problems = new List<TextProblem>();
		if (results != null)
		{
			foreach (var result in results)
			{
				if (token.IsCancellationRequested)
					break;
				var range = result.Range;
				if (range.Length == 0)
					continue;
				if (result.ResultType == NSTextCheckingType.Grammar)
				{
					// Grammar corrections apply to the whole flagged span; carry the platform's suggested
					// replacement so the context menu can offer it (spelling guesses don't apply to a phrase).
					var replacement = result.ReplacementString;
					IReadOnlyList<string>? suggestions = !string.IsNullOrEmpty(replacement) ? new[] { replacement! } : null;
					problems.Add(new TextProblem((int)range.Location, (int)range.Length, TextProblemKind.Grammar, null, suggestions));
				}
				else
				{
					// Spelling. When uppercase-checking is off, drop all-caps spans so behaviour matches
					// the "acronyms are skipped" contract (NSSpellChecker still flags a few common all-caps
					// typos such as "TEH").
					if (!_checkUppercaseWords && SpellCheckText.IsUppercaseWord(text.Substring((int)range.Location, (int)range.Length)))
						continue;
					problems.Add(new TextProblem((int)range.Location, (int)range.Length, TextProblemKind.Spelling));
				}
			}
		}

		// NSSpellChecker skips all-caps tokens as presumed acronyms. When the caller opts in, re-check
		// each by spelling its lowercased form (verified: CheckSpelling flags "helllo" but not "nasa").
		if (_checkUppercaseWords && (checkTypes & TextCheckTypes.Spelling) != 0)
			AddUppercaseSpellingProblems(_checker, text, problems, token);

		return problems.Count > 0 ? problems : (IReadOnlyList<TextProblem>)Array.Empty<TextProblem>();
	}

	void AddUppercaseSpellingProblems(NSSpellChecker checker, string text, List<TextProblem> problems, CancellationToken token)
	{
		HashSet<int>? coveredStarts = null;
		foreach (var match in SpellCheckText.EnumerateWords(text))
		{
			if (token.IsCancellationRequested)
				break;
			var word = match.Value;
			if (!SpellCheckText.IsUppercaseWord(word))
				continue;
			// Skip tokens NSSpellChecker already flagged (e.g. "TEH") so we don't double-squiggle.
			coveredStarts ??= BuildCoveredStarts(problems);
			if (coveredStarts.Contains(match.Index))
				continue;
			var range = checker.CheckSpelling(word.ToLowerInvariant(), 0);
			if (range.Length > 0)
				problems.Add(new TextProblem(match.Index, word.Length, TextProblemKind.Spelling));
		}
	}

	static HashSet<int> BuildCoveredStarts(List<TextProblem> problems)
	{
		var starts = new HashSet<int>();
		for (int i = 0; i < problems.Count; i++)
			starts.Add(problems[i].Start);
		return starts;
	}

	public IReadOnlyList<string> GetSuggestions(string word, string? context = null)
	{
		if (string.IsNullOrEmpty(word))
			return Array.Empty<string>();

		var guesses = GuessesAcrossLanguages(word, context);

		// All-caps words yield no guesses from NSSpellChecker (it treats them as acronyms); fall back to
		// guessing the lowercased form and re-uppercasing, so the menu still offers corrections.
		if (guesses.Count == 0 && SpellCheckText.IsUppercaseWord(word))
		{
			var lowerGuesses = GuessesAcrossLanguages(word.ToLowerInvariant(), context);
			if (lowerGuesses.Count > 0)
			{
				var mapped = new string[lowerGuesses.Count];
				for (int i = 0; i < lowerGuesses.Count; i++)
					mapped[i] = lowerGuesses[i].ToUpperInvariant();
				return mapped;
			}
		}
		return guesses;
	}

	// Collects guesses for the word across the languages NSSpellChecker checks against, de-duplicated.
	// Spelling *detection* auto-identifies the language (which is why a French typo gets squiggled), but
	// GuessesForWordRange is language-specific: asking only the checker's single current language returns
	// wrong-language suggestions — English guesses for a French word, the exact symptom of RH-55395.
	//
	// The reliable signal is the word's surrounding sentence: NSSpellChecker can't identify the language
	// of a lone word (it reports "und") but identifies the paragraph ("fr" for "Je parl le francais"), so
	// when context is supplied we lead with that language — its "français"/"parle" then top the menu
	// instead of being buried (and truncated away by SpellCheckOptions.MaxSuggestions) behind a dozen
	// other languages that each propose an equally-close correction. The remaining languages follow,
	// ordered by how closely each one's best guess matches the word, as a fallback when context can't
	// identify the language (the guesses API rejects a null language, so every candidate must be named).
	List<string> GuessesAcrossLanguages(string word, string? context)
	{
		// An explicit language wins outright (the constructor's documented override).
		if (!string.IsNullOrEmpty(_language))
			return Dedup(new List<string[]> { GuessesIn(word, _language!) });

		var languages = _checker.UserPreferredLanguages;
		if (languages == null || languages.Length == 0)
			return Dedup(new List<string[]> { GuessesIn(word, _checker.Language) });

		var identified = IdentifyLanguage(context);

		var lists = new List<string[]>();
		// The language identified from the sentence leads; its guesses are already best-first.
		if (!string.IsNullOrEmpty(identified))
			lists.Add(GuessesIn(word, identified!));

		// Remaining languages, ordered by how closely each one's best guess matches the word. The user's
		// order breaks ties (stable). Skip the identified language; it has already been added.
		var scored = new List<(int distance, int order, string[] guesses)>(languages.Length);
		for (int i = 0; i < languages.Length; i++)
		{
			if (string.IsNullOrEmpty(languages[i]))
				continue;
			if (!string.IsNullOrEmpty(identified) && string.Equals(languages[i], identified, StringComparison.OrdinalIgnoreCase))
				continue;
			var guesses = GuessesIn(word, languages[i]);
			scored.Add((BestDistance(word, guesses), i, guesses));
		}
		scored.Sort((a, b) => a.distance != b.distance ? a.distance.CompareTo(b.distance) : a.order.CompareTo(b.order));
		for (int i = 0; i < scored.Count; i++)
			lists.Add(scored[i].guesses);

		return Dedup(lists);
	}

	// The language NSSpellChecker identifies for the surrounding text (e.g. "fr"), or null when it can't
	// tell or no context was supplied. Identification needs sentence context — a single word reports the
	// undetermined language "und" — so this is given the word's whole paragraph.
	string? IdentifyLanguage(string? context)
	{
		if (string.IsNullOrEmpty(context))
			return null;
		_checker.CheckString(context!, new NSRange(0, context!.Length),
			(NSTextCheckingTypes)NSTextCheckingType.Spelling, (NSDictionary?)null, _tag,
			out var orthography, out _);
		var dominant = orthography?.DominantLanguage;
		if (string.IsNullOrEmpty(dominant) || string.Equals(dominant, "und", StringComparison.OrdinalIgnoreCase))
			return null;
		return dominant;
	}

	string[] GuessesIn(string word, string language)
		=> _checker.GuessesForWordRange(new NSRange(0, word.Length), word, language, _tag) ?? Array.Empty<string>();

	// Flattens guess lists (already in priority order) into one de-duplicated list.
	static List<string> Dedup(List<string[]> guessLists)
	{
		var result = new List<string>();
		var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
		foreach (var guesses in guessLists)
		{
			foreach (var guess in guesses)
			{
				if (!string.IsNullOrEmpty(guess) && seen.Add(guess))
					result.Add(guess);
			}
		}
		return result;
	}

	// Smallest edit distance between the word and any of a language's top guesses (guesses are returned
	// best-first), or int.MaxValue when the language offers none — so a language that can't correct the
	// word sorts last. Only the first few are considered; later guesses are progressively worse fits.
	static int BestDistance(string word, string[] guesses)
	{
		var best = int.MaxValue;
		var limit = Math.Min(guesses.Length, 3);
		for (int i = 0; i < limit; i++)
		{
			var distance = LevenshteinDistance(word, guesses[i]);
			if (distance < best)
				best = distance;
		}
		return best;
	}

	// Standard two-row Levenshtein edit distance, case-insensitive (a capitalised proposal for a
	// lowercase typo is still a close fit).
	static int LevenshteinDistance(string a, string b)
	{
		a = a.ToLowerInvariant();
		b = b.ToLowerInvariant();
		var previous = new int[b.Length + 1];
		var current = new int[b.Length + 1];
		for (int j = 0; j <= b.Length; j++)
			previous[j] = j;
		for (int i = 1; i <= a.Length; i++)
		{
			current[0] = i;
			for (int j = 1; j <= b.Length; j++)
			{
				var cost = a[i - 1] == b[j - 1] ? 0 : 1;
				current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
			}
			(previous, current) = (current, previous);
		}
		return previous[b.Length];
	}

	public void AddToDictionary(string word)
	{
		if (string.IsNullOrEmpty(word))
			return;
		_checker.LearnWord(word);
		DictionaryChanged?.Invoke(this, EventArgs.Empty);
	}

	public void RemoveFromDictionary(string word)
	{
		if (string.IsNullOrEmpty(word))
			return;
		_checker.UnlearnWord(word);
		DictionaryChanged?.Invoke(this, EventArgs.Empty);
	}

	public bool IsWordLearned(string word)
		=> !string.IsNullOrEmpty(word) && _checker.HasLearnedWord(word);
}
