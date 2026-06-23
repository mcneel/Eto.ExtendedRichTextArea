using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

using Eto.ExtendedRichTextArea.SpellCheck;

namespace Eto.ExtendedRichTextArea.Wpf;

/// <summary>
/// An <see cref="ITextChecker"/> backed by the Windows Spell Checking API (<c>ISpellChecker</c>,
/// available since Windows 8). This is the same system spell-checker used by the OS, and unlike
/// WPF's per-control <c>SpellCheck</c> it is usable as a standalone service. It reports spelling
/// problems plus the API's context-sensitive issues (repeated words, capitalization), offers
/// suggestions, and can add words to the user's dictionary.
/// </summary>
/// <remarks>
/// The API returns UTF-16 code-unit offsets, which match .NET string indices (and the document
/// offsets used by the control) directly. The underlying COM object is free-threaded; this class
/// creates it lazily and serialises access with a lock so it is safe to call <see cref="Check"/>
/// from the controller's background thread. If the API is unavailable (older OS / unsupported
/// language) the methods degrade gracefully to "no problems".
/// </remarks>
public sealed class WindowsSpellChecker : ITextChecker, IDisposable
{
	static readonly Guid CLSID_SpellCheckerFactory = new Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC");
	const int S_OK = 0;

	readonly object _lock = new object();
	// The Windows API can't report whether a word is in the user dictionary, so track the words added
	// this session to decide when to offer "remove from dictionary".
	readonly HashSet<string> _addedWords = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
	readonly string _language;
	ISpellChecker? _checker;
	bool _initialized;
	TextCheckTypes _checkTypes = TextCheckTypes.All;
	bool _checkUppercaseWords;

	public event EventHandler? DictionaryChanged;

	/// <inheritdoc/>
	public TextCheckTypes CheckTypes
	{
		get { lock (_lock) return _checkTypes; }
		set
		{
			lock (_lock)
			{
				if (_checkTypes == value)
					return;
				_checkTypes = value;
			}
			DictionaryChanged?.Invoke(this, EventArgs.Empty);
		}
	}

	/// <inheritdoc/>
	public bool CheckUppercaseWords
	{
		get { lock (_lock) return _checkUppercaseWords; }
		set
		{
			lock (_lock)
			{
				if (_checkUppercaseWords == value)
					return;
				_checkUppercaseWords = value;
			}
			DictionaryChanged?.Invoke(this, EventArgs.Empty);
		}
	}

	/// <param name="language">
	/// BCP-47 language tag (e.g. "en-US"). Defaults to the current UI culture, falling back to
	/// "en-US" when that language isn't installed.
	/// </param>
	public WindowsSpellChecker(string? language = null)
	{
		_language = !string.IsNullOrEmpty(language) ? language! : CultureInfo.CurrentUICulture.Name;
		if (string.IsNullOrEmpty(_language))
			_language = "en-US";
	}

	/// <summary>Gets whether the platform spell checker was successfully created.</summary>
	public bool IsAvailable
	{
		get { lock (_lock) return EnsureChecker() != null; }
	}

	ISpellChecker? EnsureChecker()
	{
		if (_initialized)
			return _checker;
		_initialized = true;
		try
		{
			var type = Type.GetTypeFromCLSID(CLSID_SpellCheckerFactory, throwOnError: false);
			if (type == null)
				return null;
			var factory = (ISpellCheckerFactory?)Activator.CreateInstance(type);
			if (factory == null)
				return null;

			var language = _language;
			if (!factory.IsSupported(language))
			{
				if (!factory.IsSupported("en-US"))
					return null;
				language = "en-US";
			}
			_checker = factory.CreateSpellChecker(language);
			// Seed the session "learned" set from the OS user dictionary so words added in previous
			// sessions can still be offered for removal. The API can't enumerate them, so we read the
			// backing file directly. Its own try/catch keeps a read failure from discarding the checker.
			LoadUserDictionary(language);
		}
		catch
		{
			_checker = null;
		}
		return _checker;
	}

	// Seeds the learned set from the OS user dictionaries under %AppData%\Microsoft\Spelling.
	// Add() writes to the language-neutral list ("neutral"), so that one is essential; the
	// language-specific list holds words added for that language. Both contain only user-added words.
	void LoadUserDictionary(string languageTag)
	{
		var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		if (string.IsNullOrEmpty(appData))
			return;
		var spellingDir = Path.Combine(appData, "Microsoft", "Spelling");
		LoadDictionaryFile(Path.Combine(spellingDir, "neutral", "default.dic"));
		LoadDictionaryFile(Path.Combine(spellingDir, languageTag, "default.dic"));
	}

	// Reads a plain-text, one-word-per-line .dic into the learned set. Best-effort: any failure is swallowed.
	void LoadDictionaryFile(string path)
	{
		try
		{
			if (!File.Exists(path))
				return;
			foreach (var line in File.ReadLines(path))
			{
				var word = line.Trim();
				if (word.Length > 0)
					_addedWords.Add(word);
			}
		}
		catch
		{
			// Unreadable / missing / locked / unexpected format — leave the set as-is.
		}
	}

	public IReadOnlyList<TextProblem> Check(string text, CancellationToken token)
	{
		if (string.IsNullOrEmpty(text))
			return Array.Empty<TextProblem>();

		lock (_lock)
		{
			var checkTypes = _checkTypes;
			if (checkTypes == TextCheckTypes.None)
				return Array.Empty<TextProblem>();

			var checker = EnsureChecker();
			if (checker == null)
				return Array.Empty<TextProblem>();

			var wantSpelling = (checkTypes & TextCheckTypes.Spelling) != 0;
			var wantGrammar = (checkTypes & TextCheckTypes.Grammar) != 0;

			IEnumSpellingError errors;
			try
			{
				errors = checker.Check(text);
			}
			catch
			{
				return Array.Empty<TextProblem>();
			}

			List<TextProblem>? problems = null;
			try
			{
				while (!token.IsCancellationRequested)
				{
					var error = errors.Next();
					if (error == null)
						break;
					try
					{
						var start = (int)error.get_StartIndex();
						var length = (int)error.get_Length();
						if (length <= 0)
							continue;
						// CorrectiveAction.Delete is a repeated word: grammar, fixed by deleting the span
						// (an empty-string suggestion, which the menu renders as "Delete repeated word").
						// Everything else is spelling, with suggestions fetched lazily per word.
						if (error.get_CorrectiveAction() == CorrectiveAction.Delete)
						{
							if (!wantGrammar)
								continue;
							problems ??= new List<TextProblem>();
							problems.Add(new TextProblem(start, length, TextProblemKind.Grammar, null, new[] { string.Empty }));
						}
						else
						{
							if (!wantSpelling)
								continue;
							// When uppercase-checking is off, drop all-caps spans so they are treated as
							// acronyms and skipped (consistent with the documented contract).
							if (!_checkUppercaseWords && SpellCheckText.IsUppercaseWord(text.Substring(start, length)))
								continue;
							problems ??= new List<TextProblem>();
							problems.Add(new TextProblem(start, length, TextProblemKind.Spelling));
						}
					}
					finally
					{
						Marshal.ReleaseComObject(error);
					}
				}
			}
			finally
			{
				Marshal.ReleaseComObject(errors);
			}

			// The API also skips all-caps tokens as presumed acronyms; when the caller opts in, re-check
			// each by spelling its lowercased form and add any that are misspelled.
			if (_checkUppercaseWords && wantSpelling)
				AddUppercaseSpellingProblems(checker, text, ref problems, token);

			return (IReadOnlyList<TextProblem>?)problems ?? Array.Empty<TextProblem>();
		}
	}

	// Caller must hold _lock. Scans all-caps word tokens and adds a spelling problem for any whose
	// lowercased form the API reports as misspelled, skipping tokens already flagged natively.
	void AddUppercaseSpellingProblems(ISpellChecker checker, string text, ref List<TextProblem>? problems, CancellationToken token)
	{
		HashSet<int>? coveredStarts = null;
		foreach (var match in SpellCheckText.EnumerateWords(text))
		{
			if (token.IsCancellationRequested)
				break;
			var word = match.Value;
			if (!SpellCheckText.IsUppercaseWord(word))
				continue;
			coveredStarts ??= BuildCoveredStarts(problems);
			if (coveredStarts.Contains(match.Index))
				continue;
			if (IsMisspelled(checker, word.ToLowerInvariant(), token))
			{
				problems ??= new List<TextProblem>();
				problems.Add(new TextProblem(match.Index, word.Length, TextProblemKind.Spelling));
			}
		}
	}

	static bool IsMisspelled(ISpellChecker checker, string word, CancellationToken token)
	{
		IEnumSpellingError errors;
		try
		{
			errors = checker.Check(word);
		}
		catch
		{
			return false;
		}
		try
		{
			while (!token.IsCancellationRequested)
			{
				var error = errors.Next();
				if (error == null)
					break;
				try
				{
					// Any non-Delete error on a single word means it isn't a recognised spelling.
					if (error.get_CorrectiveAction() != CorrectiveAction.Delete)
						return true;
				}
				finally
				{
					Marshal.ReleaseComObject(error);
				}
			}
		}
		finally
		{
			Marshal.ReleaseComObject(errors);
		}
		return false;
	}

	static HashSet<int> BuildCoveredStarts(List<TextProblem>? problems)
	{
		var starts = new HashSet<int>();
		if (problems != null)
		{
			for (int i = 0; i < problems.Count; i++)
				starts.Add(problems[i].Start);
		}
		return starts;
	}

	public IReadOnlyList<string> GetSuggestions(string word)
	{
		if (string.IsNullOrEmpty(word))
			return Array.Empty<string>();

		lock (_lock)
		{
			var checker = EnsureChecker();
			if (checker == null)
				return Array.Empty<string>();

			var result = FetchSuggestions(checker, word);

			// All-caps words may yield no suggestions; fall back to the lowercased form and re-uppercase,
			// matching how all-caps words are flagged when CheckUppercaseWords is on.
			if (result.Count == 0 && SpellCheckText.IsUppercaseWord(word))
			{
				var lowered = FetchSuggestions(checker, word.ToLowerInvariant());
				if (lowered.Count > 0)
				{
					for (int i = 0; i < lowered.Count; i++)
						lowered[i] = lowered[i].ToUpperInvariant();
					return lowered;
				}
			}
			return result;
		}
	}

	static List<string> FetchSuggestions(ISpellChecker checker, string word)
	{
		IEnumString suggestions;
		try
		{
			suggestions = checker.Suggest(word);
		}
		catch
		{
			return new List<string>();
		}

		var result = new List<string>();
		try
		{
			var buffer = new string[1];
			while (suggestions.Next(1, buffer, out var fetched) == S_OK && fetched == 1)
			{
				if (!string.IsNullOrEmpty(buffer[0]))
					result.Add(buffer[0]);
			}
		}
		finally
		{
			Marshal.ReleaseComObject(suggestions);
		}
		return result;
	}

	public void AddToDictionary(string word)
	{
		if (string.IsNullOrEmpty(word))
			return;
		lock (_lock)
		{
			var checker = EnsureChecker();
			if (checker == null)
				return;
			try
			{
				checker.Add(word);
			}
			catch
			{
				return;
			}
			_addedWords.Add(word);
		}
		DictionaryChanged?.Invoke(this, EventArgs.Empty);
	}

	public void RemoveFromDictionary(string word)
	{
		if (string.IsNullOrEmpty(word))
			return;
		bool removed;
		lock (_lock)
		{
			removed = _addedWords.Remove(word);
			var checker = EnsureChecker();
			// Removal from the persisted user dictionary needs ISpellChecker2 (Windows 8.1+); if it isn't
			// available we can still drop our session record so the word stops offering "remove".
			if (checker is ISpellChecker2 checker2)
			{
				try
				{
					checker2.Remove(word);
				}
				catch
				{
					// best effort
				}
			}
		}
		if (removed)
			DictionaryChanged?.Invoke(this, EventArgs.Empty);
	}

	public bool IsWordLearned(string word)
	{
		if (string.IsNullOrEmpty(word))
			return false;
		lock (_lock)
		{
			EnsureChecker(); // first call also seeds _addedWords from the OS user dictionary
			return _addedWords.Contains(word);
		}
	}

	public void Dispose()
	{
		lock (_lock)
		{
			if (_checker != null)
			{
				Marshal.ReleaseComObject(_checker);
				_checker = null;
			}
		}
	}
}
