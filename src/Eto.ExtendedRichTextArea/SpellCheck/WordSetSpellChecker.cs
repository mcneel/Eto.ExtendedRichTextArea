using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;

namespace Eto.ExtendedRichTextArea.SpellCheck;

/// <summary>
/// A minimal, dependency-free <see cref="ITextChecker"/> that flags any word not present in a
/// supplied set. Intended as a reference implementation and for testing — a real host would plug
/// in Hunspell or a platform-native checker. Only spelling (not grammar) is implemented.
/// </summary>
/// <remarks>Thread-safe for concurrent <see cref="Check"/> calls; the word set is guarded by a lock.</remarks>
public sealed class WordSetSpellChecker : ITextChecker
{
	// Words: runs of letters, optionally joined by apostrophes (don't, isn't).
	static readonly Regex WordPattern = new Regex(@"\p{L}+(?:'\p{L}+)*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	readonly object _lock = new object();
	readonly HashSet<string> _words;
	// Words added via AddToDictionary, tracked separately from the built-in set so only these are
	// reported as "learned" (and therefore removable).
	readonly HashSet<string> _userWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
	TextCheckTypes _checkTypes = TextCheckTypes.All;
	bool _checkUppercaseWords;

	public event EventHandler? DictionaryChanged;

	/// <inheritdoc/>
	/// <remarks>This checker only implements spelling, so selecting only grammar reports nothing.</remarks>
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

	public WordSetSpellChecker(IEnumerable<string> knownWords)
	{
		if (knownWords == null)
			throw new ArgumentNullException(nameof(knownWords));
		_words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var word in knownWords)
		{
			if (!string.IsNullOrEmpty(word))
				_words.Add(word);
		}
	}

	public IReadOnlyList<TextProblem> Check(string text, CancellationToken token)
	{
		if (string.IsNullOrEmpty(text))
			return Array.Empty<TextProblem>();

		// Only spelling is implemented, so there is nothing to do unless spelling was requested.
		if ((_checkTypes & TextCheckTypes.Spelling) == 0)
			return Array.Empty<TextProblem>();

		List<TextProblem>? problems = null;
		foreach (Match match in WordPattern.Matches(text))
		{
			if (token.IsCancellationRequested)
				break;
			var word = match.Value;
			// All-caps tokens are presumed acronyms and skipped unless the caller opts in.
			if (!_checkUppercaseWords && SpellCheckText.IsUppercaseWord(word))
				continue;
			if (IsKnown(word))
				continue;
			problems ??= new List<TextProblem>();
			problems.Add(new TextProblem(match.Index, match.Length, TextProblemKind.Spelling));
		}
		return (IReadOnlyList<TextProblem>?)problems ?? Array.Empty<TextProblem>();
	}

	bool IsKnown(string word)
	{
		lock (_lock)
			return _words.Contains(word);
	}

	// context is unused: this checker has a single word set, so there is no language to disambiguate.
	public IReadOnlyList<string> GetSuggestions(string word, string? context = null)
	{
		if (string.IsNullOrEmpty(word))
			return Array.Empty<string>();

		// Cheap suggestion: dictionary words within edit distance 1-2, closest first.
		var matches = new List<(string word, int distance)>();
		lock (_lock)
		{
			foreach (var candidate in _words)
			{
				if (Math.Abs(candidate.Length - word.Length) > 2)
					continue;
				var distance = LevenshteinAtMost(word, candidate, 2);
				if (distance >= 0)
					matches.Add((candidate, distance));
			}
		}
		matches.Sort((a, b) => a.distance.CompareTo(b.distance));
		var result = new List<string>(Math.Min(matches.Count, 5));
		for (int i = 0; i < matches.Count && result.Count < 5; i++)
			result.Add(matches[i].word);
		return result;
	}

	public void AddToDictionary(string word)
	{
		if (string.IsNullOrEmpty(word))
			return;
		bool added;
		lock (_lock)
		{
			added = _words.Add(word);
			_userWords.Add(word);
		}
		if (added)
			DictionaryChanged?.Invoke(this, EventArgs.Empty);
	}

	public void RemoveFromDictionary(string word)
	{
		if (string.IsNullOrEmpty(word))
			return;
		bool removed;
		lock (_lock)
		{
			// Only remove words the user added; never evict the built-in set.
			removed = _userWords.Remove(word);
			if (removed)
				_words.Remove(word);
		}
		if (removed)
			DictionaryChanged?.Invoke(this, EventArgs.Empty);
	}

	public bool IsWordLearned(string word)
	{
		if (string.IsNullOrEmpty(word))
			return false;
		lock (_lock)
			return _userWords.Contains(word);
	}

	// Returns the edit distance if it is <= max, otherwise -1. Case-insensitive.
	static int LevenshteinAtMost(string a, string b, int max)
	{
		var previous = new int[b.Length + 1];
		var current = new int[b.Length + 1];
		for (int j = 0; j <= b.Length; j++)
			previous[j] = j;

		for (int i = 1; i <= a.Length; i++)
		{
			current[0] = i;
			var rowMin = current[0];
			for (int j = 1; j <= b.Length; j++)
			{
				var cost = char.ToUpperInvariant(a[i - 1]) == char.ToUpperInvariant(b[j - 1]) ? 0 : 1;
				current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
				if (current[j] < rowMin)
					rowMin = current[j];
			}
			if (rowMin > max)
				return -1; // whole row already exceeds the threshold
			var swap = previous;
			previous = current;
			current = swap;
		}
		var distance = previous[b.Length];
		return distance <= max ? distance : -1;
	}
}
