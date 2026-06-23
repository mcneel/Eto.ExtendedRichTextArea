using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;

using Eto;
using Eto.ExtendedRichTextArea.SpellCheck;
using Eto.ExtendedRichTextArea.Wpf;

[assembly: ExportHandler(typeof(SpellChecker), typeof(WindowsSpellCheckerHandler))]

namespace Eto.ExtendedRichTextArea.Wpf;

/// <summary>
/// Eto handler that backs the cross-platform <see cref="SpellChecker"/> widget with the Windows
/// Spell Checking API via <see cref="WindowsSpellChecker"/>. Registered via the assembly-level
/// <see cref="ExportHandlerAttribute"/> above; Eto loads this companion assembly automatically
/// (by the <c>Eto.ExtendedRichTextArea.Wpf</c> naming convention) when the document control's
/// assembly is loaded on the WPF platform.
/// </summary>
public class WindowsSpellCheckerHandler : WidgetHandler<SpellChecker>, SpellChecker.IHandler
{
	readonly WindowsSpellChecker _checker = new WindowsSpellChecker();

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

	public IReadOnlyList<string> GetSuggestions(string word) => _checker.GetSuggestions(word);

	public void AddToDictionary(string word) => _checker.AddToDictionary(word);

	public void RemoveFromDictionary(string word) => _checker.RemoveFromDictionary(word);

	public bool IsWordLearned(string word) => _checker.IsWordLearned(word);

	public event EventHandler? DictionaryChanged
	{
		add => _checker.DictionaryChanged += value;
		remove => _checker.DictionaryChanged -= value;
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
			_checker.Dispose();
		base.Dispose(disposing);
	}
}
