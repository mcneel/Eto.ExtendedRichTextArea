using System;
using System.Collections.Generic;
using System.Threading;

using Eto;
using Eto.ExtendedRichTextArea.SpellCheck;
using Eto.ExtendedRichTextArea.Mac;

[assembly: ExportHandler(typeof(SpellChecker), typeof(MacSpellCheckerHandler))]

namespace Eto.ExtendedRichTextArea.Mac;

/// <summary>
/// Eto handler that backs the cross-platform <see cref="SpellChecker"/> widget with macOS's
/// <see cref="MacSpellChecker"/> (NSSpellChecker). Registered via the assembly-level
/// <see cref="ExportHandlerAttribute"/> above; Eto loads this companion assembly automatically
/// (by the <c>Eto.ExtendedRichTextArea.Mac</c> naming convention) when the document control's
/// assembly is loaded on the macOS platform.
/// </summary>
public class MacSpellCheckerHandler : WidgetHandler<SpellChecker>, SpellChecker.IHandler
{
	readonly MacSpellChecker _checker = new MacSpellChecker();

	public TextCheckTypes CheckTypes
	{
		get => _checker.CheckTypes;
		set => _checker.CheckTypes = value;
	}

	public bool CheckUppercaseWords
	{
		get => _checker.CheckUppercaseWords;
		set => _checker.CheckUppercaseWords = value;
	}

	public IReadOnlyList<TextProblem> Check(string text, CancellationToken token) => _checker.Check(text, token);

	public IReadOnlyList<string> GetSuggestions(string word, string? context = null) => _checker.GetSuggestions(word, context);

	public void AddToDictionary(string word) => _checker.AddToDictionary(word);

	public void RemoveFromDictionary(string word) => _checker.RemoveFromDictionary(word);

	public bool IsWordLearned(string word) => _checker.IsWordLearned(word);

	public event EventHandler? DictionaryChanged
	{
		add => _checker.DictionaryChanged += value;
		remove => _checker.DictionaryChanged -= value;
	}
}
