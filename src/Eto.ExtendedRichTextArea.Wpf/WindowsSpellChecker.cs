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
/// <para>
/// The API returns UTF-16 code-unit offsets, which match .NET string indices (and the document
/// offsets used by the control) directly. The underlying COM objects are free-threaded; this class
/// creates them lazily and serialises access with a lock so it is safe to call <see cref="Check"/>
/// from the controller's background thread. If the API is unavailable (older OS / unsupported
/// language) the methods degrade gracefully to "no problems".
/// </para>
/// <para>
/// A single <c>ISpellChecker</c> only checks one language. To support multiple languages
/// this class can hold several: the <b>first</b> (primary) language drives whole-text checking and
/// grammar, and a word the primary flags is treated as a real problem only when <b>no other</b>
/// configured language accepts it (i.e. correct-in-any-language passes, matching how macOS's
/// <c>NSSpellChecker</c> behaves with automatic language identification). Use
/// <see cref="CreateForInstalledLanguages"/> to check against every dictionary installed on the
/// system, or the <see cref="WindowsSpellChecker(IReadOnlyList{string})"/> constructor for an
/// explicit set.
/// </para>
/// </remarks>
public sealed class WindowsSpellChecker : ITextChecker, IDisposable
{
	static readonly Guid CLSID_SpellCheckerFactory = new Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC");
	const int S_OK = 0;

	readonly object _lock = new object();
	// The Windows API can't report whether a word is in the user dictionary, so track the words added
	// this session to decide when to offer "remove from dictionary".
	readonly HashSet<string> _addedWords = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

	// Language configuration, resolved to concrete checkers lazily in EnsureCheckers().
	readonly string _primaryLanguage;            // BCP-47 tag, captured on the constructing (UI) thread
	readonly IReadOnlyList<string>? _explicitLanguages; // when set, the exact set to use (first = primary)
	readonly bool _autoIncludeInstalled;         // when set, add every other installed dictionary

	// Checkers in priority order; [0] is the primary. Empty when the API is unavailable.
	readonly List<ISpellChecker> _checkers = new List<ISpellChecker>();
	// BCP-47 tag for each entry in _checkers, in the same order, so a detected language tag can be mapped
	// back to the checker that handles it.
	readonly List<string> _checkerTags = new List<string>();
	// Per-checker cache of "does this dictionary actually check the script of character X" (see
	// IsScriptCheckedBy). Keyed by checker, then by a lowercased representative character.
	readonly Dictionary<ISpellChecker, Dictionary<char, bool>> _scriptChecked = new Dictionary<ISpellChecker, Dictionary<char, bool>>();
	bool _initialized;
	bool _elsUnavailable; // set once when ELS language detection isn't usable, to stop retrying it
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
	/// "en-US" when that language isn't installed. Checks against this one language only.
	/// </param>
	public WindowsSpellChecker(string? language = null)
		: this(language, autoIncludeInstalled: false)
	{
	}

	/// <param name="languages">
	/// Explicit set of BCP-47 tags to check against, in priority order; the first supported entry is
	/// the primary language. A word is correct if any of these languages accepts it. Unsupported
	/// entries are skipped; if none are supported it falls back to "en-US" when available.
	/// </param>
	public WindowsSpellChecker(IReadOnlyList<string> languages)
	{
		_explicitLanguages = languages ?? throw new ArgumentNullException(nameof(languages));
		_primaryLanguage = "en-US"; // unused when _explicitLanguages is set; kept non-null for safety
	}

	WindowsSpellChecker(string? primaryLanguage, bool autoIncludeInstalled)
	{
		_primaryLanguage = !string.IsNullOrEmpty(primaryLanguage) ? primaryLanguage! : CultureInfo.CurrentUICulture.Name;
		if (string.IsNullOrEmpty(_primaryLanguage))
			_primaryLanguage = "en-US";
		_autoIncludeInstalled = autoIncludeInstalled;
	}

	/// <summary>
	/// Creates a multi-language checker: the primary language (<paramref name="primaryLanguage"/>, or
	/// the current UI culture, falling back to "en-US") plus every other language that has a
	/// spell-check dictionary installed on the system. This brings Windows to parity with macOS's
	/// automatic multi-language checking: a word is flagged only when no installed
	/// dictionary recognises it.
	/// </summary>
	public static WindowsSpellChecker CreateForInstalledLanguages(string? primaryLanguage = null)
		=> new WindowsSpellChecker(primaryLanguage, autoIncludeInstalled: true);

	/// <summary>Gets whether at least one platform spell checker was successfully created.</summary>
	public bool IsAvailable
	{
		get { lock (_lock) return EnsureCheckers().Count > 0; }
	}

	// The primary checker drives whole-text checking, grammar, suggestions priority, and dictionary edits.
	ISpellChecker? PrimaryChecker => _checkers.Count > 0 ? _checkers[0] : null;

	// Resolves the configured languages to live ISpellChecker instances (priority order, primary first).
	// Returns an empty list when the API/dictionaries are unavailable. Caller must hold _lock.
	List<ISpellChecker> EnsureCheckers()
	{
		if (_initialized)
			return _checkers;
		_initialized = true;
		try
		{
			var type = Type.GetTypeFromCLSID(CLSID_SpellCheckerFactory, throwOnError: false);
			if (type == null)
				return _checkers;
			var factory = (ISpellCheckerFactory?)Activator.CreateInstance(type);
			if (factory == null)
				return _checkers;

			var languages = ResolveLanguages(factory);
			foreach (var language in languages)
			{
				try
				{
					var checker = factory.CreateSpellChecker(language);
					if (checker != null)
					{
						_checkers.Add(checker);
						_checkerTags.Add(language);
					}
				}
				catch
				{
					// Skip a language whose checker fails to create; the others still work.
				}
			}

			if (_checkers.Count > 0)
			{
				// Seed the session "learned" set from the OS user dictionary so words added in previous
				// sessions can still be offered for removal. The API can't enumerate them, so we read the
				// backing file directly. Its own try/catch keeps a read failure from discarding the checker.
				LoadUserDictionary(languages[0]);
			}
		}
		catch
		{
			_checkers.Clear();
		}
		return _checkers;
	}

	// Builds the ordered, de-duplicated list of supported language tags to create checkers for.
	List<string> ResolveLanguages(ISpellCheckerFactory factory)
	{
		var result = new List<string>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		if (_explicitLanguages != null)
		{
			foreach (var language in _explicitLanguages)
			{
				if (string.IsNullOrWhiteSpace(language))
					continue;
				var tag = language.Trim();
				if (!seen.Add(tag))
					continue;
				if (factory.IsSupported(tag))
					result.Add(tag);
				else
					seen.Remove(tag); // not added; allow a later (different-cased) supported form
			}
			if (result.Count == 0 && factory.IsSupported("en-US"))
				result.Add("en-US");
			return result;
		}

		// Primary language: requested UI culture, else en-US.
		string? primary = factory.IsSupported(_primaryLanguage) ? _primaryLanguage
			: factory.IsSupported("en-US") ? "en-US"
			: null;
		if (primary != null && seen.Add(primary))
			result.Add(primary);

		if (_autoIncludeInstalled)
		{
			foreach (var tag in GetSupportedLanguages(factory))
			{
				if (string.IsNullOrWhiteSpace(tag))
					continue;
				if (seen.Add(tag))
					result.Add(tag);
			}
		}
		return result;
	}

	// Enumerates the language tags the system spell-check API supports (i.e. has dictionaries for).
	static List<string> GetSupportedLanguages(ISpellCheckerFactory factory)
	{
		var result = new List<string>();
		IEnumString languages;
		try
		{
			languages = factory.get_SupportedLanguages();
		}
		catch
		{
			return result;
		}
		try
		{
			var buffer = new string[1];
			while (languages.Next(1, buffer, out var fetched) == S_OK && fetched == 1)
			{
				if (!string.IsNullOrEmpty(buffer[0]))
					result.Add(buffer[0]);
			}
		}
		finally
		{
			Marshal.ReleaseComObject(languages);
		}
		return result;
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

			var checkers = EnsureCheckers();
			if (checkers.Count == 0)
				return Array.Empty<TextProblem>();

			var primary = checkers[0];
			var multiLanguage = checkers.Count > 1;
			var wantSpelling = (checkTypes & TextCheckTypes.Spelling) != 0;
			var wantGrammar = (checkTypes & TextCheckTypes.Grammar) != 0;

			IEnumSpellingError errors;
			try
			{
				errors = primary.Check(text);
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
							var word = text.Substring(start, length);
							// When uppercase-checking is off, drop all-caps spans so they are treated as
							// acronyms and skipped (consistent with the documented contract).
							if (!_checkUppercaseWords && SpellCheckText.IsUppercaseWord(word))
								continue;
							// Multi-language: a word the primary flags is only a real problem when no other
							// language that actually checks this script accepts it.
							if (multiLanguage && AcceptedByAnyCompetentLanguage(word, 1, token))
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
			// each by spelling its lowercased form and add any that are misspelled in every language.
			if (_checkUppercaseWords && wantSpelling)
				AddUppercaseSpellingProblems(text, ref problems, token);

			return (IReadOnlyList<TextProblem>?)problems ?? Array.Empty<TextProblem>();
		}
	}

	// Caller must hold _lock. Scans all-caps word tokens and adds a spelling problem for any the primary
	// flags (by its lowercased form) that no other competent language accepts, skipping already-flagged
	// tokens. Mirrors the main path: the primary decides candidacy, additional languages can rescue.
	void AddUppercaseSpellingProblems(string text, ref List<TextProblem>? problems, CancellationToken token)
	{
		var primary = PrimaryChecker;
		if (primary == null)
			return;
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
			var lowered = word.ToLowerInvariant();
			if (!IsMisspelled(primary, lowered, token))
				continue;
			if (_checkers.Count > 1 && AcceptedByAnyCompetentLanguage(lowered, 1, token))
				continue;
			problems ??= new List<TextProblem>();
			problems.Add(new TextProblem(match.Index, word.Length, TextProblemKind.Spelling));
		}
	}

	// Caller must hold _lock. True if some configured language at index >= startIndex both recognises
	// `word` as correct AND actually spell-checks its script (see LanguageAcceptsWord).
	bool AcceptedByAnyCompetentLanguage(string word, int startIndex, CancellationToken token)
	{
		for (int i = startIndex; i < _checkers.Count; i++)
		{
			if (token.IsCancellationRequested)
				return true; // bail without changing the result on cancel
			if (LanguageAcceptsWord(_checkers[i], word, token))
				return true;
		}
		return false;
	}

	// True when `checker` reports `word` as correctly spelled AND genuinely checks the word's script.
	// The Windows API returns "no error" both for a valid word and for text in a script the dictionary
	// doesn't handle (e.g. a Japanese checker shrugs at a Latin word), so a bare "no error" would let a
	// real typo through. Confirm competence by checking that the dictionary flags obvious same-script
	// garbage (the first letter repeated), which a script-incompetent checker won't.
	bool LanguageAcceptsWord(ISpellChecker checker, string word, CancellationToken token)
	{
		if (word.Length == 0)
			return false;
		if (IsMisspelled(checker, word, token))
			return false;
		return IsScriptCheckedBy(checker, word[0], token);
	}

	// Caller must hold _lock. Whether `checker` actually spell-checks the script of `sample`, cached per
	// (checker, character). Probes by spelling the character repeated into a string no real word would be.
	bool IsScriptCheckedBy(ISpellChecker checker, char sample, CancellationToken token)
	{
		var key = char.ToLowerInvariant(sample);
		if (!_scriptChecked.TryGetValue(checker, out var perChar))
		{
			perChar = new Dictionary<char, bool>();
			_scriptChecked[checker] = perChar;
		}
		if (perChar.TryGetValue(key, out var known))
			return known;
		var competent = IsMisspelled(checker, new string(key, 8), token);
		if (!token.IsCancellationRequested)
			perChar[key] = competent;
		return competent;
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

	public IReadOnlyList<string> GetSuggestions(string word, string? context = null)
	{
		if (string.IsNullOrEmpty(word))
			return Array.Empty<string>();

		lock (_lock)
		{
			var checkers = EnsureCheckers();
			if (checkers.Count == 0)
				return Array.Empty<string>();

			// Query the language the word is written in first, then the rest. A lone word is ambiguous
			// across the installed languages (many propose an equally-close correction), so without this
			// the primary language's near-matches fill the menu and the right-language suggestion — e.g.
			// "français" for "francais" — is pushed off the end by SpellCheckOptions.MaxSuggestions.
			var order = LanguageOrder(context);

			// Merge suggestions in that order, de-duplicated.
			var result = new List<string>();
			var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
			foreach (var index in order)
			{
				foreach (var suggestion in FetchSuggestions(checkers[index], word))
				{
					if (seen.Add(suggestion))
						result.Add(suggestion);
				}
			}

			// All-caps words may yield no suggestions; fall back to the lowercased form and re-uppercase,
			// matching how all-caps words are flagged when CheckUppercaseWords is on.
			if (result.Count == 0 && SpellCheckText.IsUppercaseWord(word))
			{
				var lowered = word.ToLowerInvariant();
				foreach (var index in order)
				{
					foreach (var suggestion in FetchSuggestions(checkers[index], lowered))
					{
						var upper = suggestion.ToUpperInvariant();
						if (seen.Add(upper))
							result.Add(upper);
					}
				}
			}
			return result;
		}
	}

	// Caller must hold _lock. The order to query languages for suggestions: the language the context
	// (the word's sentence) is written in first, then the remaining languages in priority order.
	List<int> LanguageOrder(string? context)
	{
		var order = new List<int>(_checkers.Count);
		var identified = IdentifyLanguageIndex(context);
		if (identified >= 0)
			order.Add(identified);
		for (int i = 0; i < _checkers.Count; i++)
		{
			if (i != identified)
				order.Add(i);
		}
		return order;
	}

	// Caller must hold _lock. Identifies which configured language the context is written in and returns
	// that language's checker index (or -1 when it can't tell). Tries ELS statistical language detection
	// first (see IdentifyLanguageViaEls), the Windows analog of macOS NSSpellChecker's dominant-language
	// identification; if that's unavailable or inconclusive it falls back to counting how many of the
	// context's words each language accepts.
	int IdentifyLanguageIndex(string? context)
	{
		if (string.IsNullOrEmpty(context) || _checkers.Count < 2)
			return -1;

		// Preferred: statistical detection from the sentence's overall shape. This works even when no
		// single word is uniquely one language in the installed dictionaries — e.g. every Latin dictionary
		// "accepts" the short words "Je"/"le" (the API doesn't flag them), so the word-counting fallback
		// below can't tell French from English for "Je parl le francais", but ELS reports "fr".
		var mapped = MapDetectedLanguageToChecker(IdentifyLanguageViaEls(context!));
		if (mapped >= 0)
			return mapped;

		// Fallback (ELS unavailable, or context too short to identify — e.g. a single word): approximate
		// the language by counting how many of the context's words each language accepts ("Je", "le" →
		// French). Bounded to the first several words so a context-menu open stays cheap.
		var token = CancellationToken.None;
		var scores = new int[_checkers.Count];
		var wordsScored = 0;
		foreach (var match in SpellCheckText.EnumerateWords(context!))
		{
			if (wordsScored >= 12)
				break;
			var contextWord = match.Value;
			// All-caps tokens are presumed acronyms and aren't language-distinctive; skip them.
			if (SpellCheckText.IsUppercaseWord(contextWord))
				continue;
			wordsScored++;
			for (int i = 0; i < _checkers.Count; i++)
			{
				if (LanguageAcceptsWord(_checkers[i], contextWord, token))
					scores[i]++;
			}
		}

		var bestIndex = -1;
		var bestScore = 0;
		for (int i = 0; i < _checkers.Count; i++)
		{
			if (scores[i] > bestScore)
			{
				bestScore = scores[i];
				bestIndex = i;
			}
		}
		return bestIndex;
	}

	// The primary subtag of a BCP-47 tag ("fr" from "fr-CA", "sr" from "sr-Latn-RS"). Used to match a
	// neutral detected language ("fr") to a regional installed checker ("fr-FR", "fr-015", ...).
	static string PrimarySubtag(string tag)
	{
		if (string.IsNullOrEmpty(tag))
			return string.Empty;
		var dash = tag.IndexOf('-');
		return dash < 0 ? tag : tag.Substring(0, dash);
	}

	// Caller must hold _lock. Maps a detected BCP-47 language tag to the index of the configured checker
	// that best matches it: an exact tag match first, else the first checker sharing its primary subtag
	// (so a neutral detected "fr" picks the first installed French dictionary). Returns -1 when nothing
	// matches (so the caller falls back to the word-counting heuristic).
	int MapDetectedLanguageToChecker(string? detectedTag)
	{
		if (string.IsNullOrEmpty(detectedTag))
			return -1;
		for (int i = 0; i < _checkerTags.Count; i++)
		{
			if (string.Equals(_checkerTags[i], detectedTag, StringComparison.OrdinalIgnoreCase))
				return i;
		}
		var primary = PrimarySubtag(detectedTag!);
		for (int i = 0; i < _checkerTags.Count; i++)
		{
			if (string.Equals(PrimarySubtag(_checkerTags[i]), primary, StringComparison.OrdinalIgnoreCase))
				return i;
		}
		return -1;
	}

	// ELS (Extended Linguistic Services) language-detection service GUID (ELS_GUID_LANGUAGE_DETECTION).
	static readonly Guid ELS_GUID_LANGUAGE_DETECTION = new Guid("CF7E00B1-909B-4D95-A8F4-611F7C377702");

	// Caller must hold _lock. Returns the dominant BCP-47 language tag ELS detects for <paramref name=
	// "context"/> (most likely first), or null when ELS is unavailable or can't decide (e.g. a lone word —
	// matching how macOS reports "und" for ambiguous input). Best-effort: any failure degrades to null so
	// IdentifyLanguageIndex falls back to the word-counting heuristic.
	string? IdentifyLanguageViaEls(string context)
	{
		if (_elsUnavailable || string.IsNullOrEmpty(context))
			return null;

		IntPtr pGuid = IntPtr.Zero, pOptions = IntPtr.Zero, services = IntPtr.Zero;
		var bag = new MappingPropertyBag();
		var bagFilled = false;
		try
		{
			var options = new MappingEnumOptions { Size = (IntPtr)Marshal.SizeOf(typeof(MappingEnumOptions)) };
			pGuid = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Guid)));
			Marshal.StructureToPtr(ELS_GUID_LANGUAGE_DETECTION, pGuid, false);
			options.Guid = pGuid;
			pOptions = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(MappingEnumOptions)));
			Marshal.StructureToPtr(options, pOptions, false);

			if (MappingGetServices(pOptions, out var services2, out var count) != S_OK || count == 0 || services2 == IntPtr.Zero)
			{
				_elsUnavailable = true; // the detection service isn't present; don't keep paying for the probe
				return null;
			}
			services = services2;

			bag.Size = (IntPtr)Marshal.SizeOf(typeof(MappingPropertyBag));
			if (MappingRecognizeText(services, context, (uint)context.Length, 0, IntPtr.Zero, ref bag) != S_OK)
				return null;
			bagFilled = true;
			if (bag.RangesCount == 0 || bag.ResultRanges == IntPtr.Zero)
				return null;

			var range = (MappingDataRange)Marshal.PtrToStructure(bag.ResultRanges, typeof(MappingDataRange));
			if (range.Data == IntPtr.Zero)
				return null;
			// Data is the detected languages as consecutive null-terminated UTF-16 tags, most likely first.
			// We only need the dominant one, so read the leading string (empty => ELS couldn't decide).
			var tag = Marshal.PtrToStringUni(range.Data);
			return string.IsNullOrEmpty(tag) ? null : tag;
		}
		catch (DllNotFoundException)
		{
			_elsUnavailable = true; // pre-ELS OS (shouldn't happen on Win8+); stop trying
			return null;
		}
		catch
		{
			return null; // transient/unexpected: fall back this time but keep ELS enabled
		}
		finally
		{
			if (bagFilled)
			{
				try { MappingFreePropertyBag(ref bag); } catch { /* best effort */ }
			}
			if (services != IntPtr.Zero)
			{
				try { MappingFreeServices(services); } catch { /* best effort */ }
			}
			if (pOptions != IntPtr.Zero)
				Marshal.FreeHGlobal(pOptions);
			if (pGuid != IntPtr.Zero)
				Marshal.FreeHGlobal(pGuid);
		}
	}

	[DllImport("elscore.dll")]
	static extern int MappingGetServices(IntPtr options, out IntPtr services, out uint servicesCount);

	[DllImport("elscore.dll", CharSet = CharSet.Unicode)]
	static extern int MappingRecognizeText(IntPtr serviceInfo, [MarshalAs(UnmanagedType.LPWStr)] string text, uint length, uint index, IntPtr options, ref MappingPropertyBag bag);

	[DllImport("elscore.dll")]
	static extern int MappingFreePropertyBag(ref MappingPropertyBag bag);

	[DllImport("elscore.dll")]
	static extern int MappingFreeServices(IntPtr serviceInfo);

	// The subset of the ELS structs (elscore.h) we marshal. Pointer-sized fields are IntPtr; the
	// service-info blob from MappingGetServices is opaque to us (passed straight back to
	// MappingRecognizeText), so it isn't modelled.
	[StructLayout(LayoutKind.Sequential)]
	struct MappingEnumOptions
	{
		public IntPtr Size;
		public IntPtr Category;
		public IntPtr InputLanguage;
		public IntPtr OutputLanguage;
		public IntPtr InputScript;
		public IntPtr OutputScript;
		public IntPtr InputContentType;
		public IntPtr OutputContentType;
		public IntPtr Guid;
		public uint Flags;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct MappingPropertyBag
	{
		public IntPtr Size;
		public IntPtr ResultRanges;
		public uint RangesCount;
		public IntPtr ServiceData;
		public uint ServiceDataSize;
		public IntPtr CallerData;
		public uint CallerDataSize;
		public IntPtr Context;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct MappingDataRange
	{
		public uint StartIndex;
		public uint EndIndex;
		public IntPtr Description;
		public uint DescriptionLength;
		public IntPtr Data;
		public uint DataSize;
		public IntPtr ContentType;
		public IntPtr ActionIds;
		public uint ActionsCount;
		public IntPtr ActionDisplayNames;
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
			EnsureCheckers();
			var checker = PrimaryChecker;
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
			EnsureCheckers();
			// Removal from the persisted user dictionary needs ISpellChecker2 (Windows 8.1+); if it isn't
			// available we can still drop our session record so the word stops offering "remove".
			if (PrimaryChecker is ISpellChecker2 checker2)
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
			EnsureCheckers(); // first call also seeds _addedWords from the OS user dictionary
			return _addedWords.Contains(word);
		}
	}

	public void Dispose()
	{
		lock (_lock)
		{
			for (int i = 0; i < _checkers.Count; i++)
			{
				try
				{
					Marshal.ReleaseComObject(_checkers[i]);
				}
				catch
				{
					// best effort
				}
			}
			_checkers.Clear();
			_checkerTags.Clear();
			_scriptChecked.Clear();
		}
	}
}
