using System;
using System.Collections.Generic;
using System.Threading;

using Eto;

namespace Eto.ExtendedRichTextArea.SpellCheck;

/// <summary>
/// A platform-backed spell/grammar checker, resolved through Eto's handler mechanism just like any
/// other Eto widget. Construct one and assign it to <see cref="ExtendedRichTextArea.SpellChecker"/>;
/// the actual engine (macOS <c>NSSpellChecker</c>, the Windows Spell Checking API, etc.) is provided
/// by the platform companion assembly (<c>Eto.ExtendedRichTextArea.macOS</c>,
/// <c>Eto.ExtendedRichTextArea.Wpf</c>, …) and discovered automatically — application code never
/// references a platform-specific assembly.
/// </summary>
/// <remarks>
/// Check <see cref="IsSupported"/> before constructing: like all Eto widgets, the constructor throws
/// <see cref="HandlerNotFoundException"/> when no handler is registered for the current platform
/// (e.g. the companion assembly isn't deployed, or the platform has no spell-check facility).
/// </remarks>
[Handler(typeof(SpellChecker.IHandler))]
public class SpellChecker : Widget, ITextChecker
{
	new IHandler Handler => (IHandler)base.Handler;

	/// <summary>
	/// Gets whether a spell-check handler is available for the current platform. When false,
	/// constructing a <see cref="SpellChecker"/> would throw, so callers should disable the feature.
	/// </summary>
	public static bool IsSupported
	{
		get
		{
			var platform = Platform.Instance;
			if (platform == null)
				return false;
			try
			{
				return platform.Find(typeof(IHandler)) != null;
			}
			catch
			{
				return false;
			}
		}
	}

	/// <inheritdoc/>
	public TextCheckTypes CheckTypes
	{
		get => Handler.CheckTypes;
		set => Handler.CheckTypes = value;
	}

	/// <inheritdoc/>
	public bool CheckUppercaseWords
	{
		get => Handler.CheckUppercaseWords;
		set => Handler.CheckUppercaseWords = value;
	}

	/// <inheritdoc/>
	public IReadOnlyList<TextProblem> Check(string text, CancellationToken token) => Handler.Check(text, token);

	/// <inheritdoc/>
	public IReadOnlyList<string> GetSuggestions(string word, string? context = null) => Handler.GetSuggestions(word, context);

	/// <inheritdoc/>
	public void AddToDictionary(string word) => Handler.AddToDictionary(word);

	/// <inheritdoc/>
	public void RemoveFromDictionary(string word) => Handler.RemoveFromDictionary(word);

	/// <inheritdoc/>
	public bool IsWordLearned(string word) => Handler.IsWordLearned(word);

	/// <inheritdoc/>
	public event EventHandler? DictionaryChanged
	{
		add => Handler.DictionaryChanged += value;
		remove => Handler.DictionaryChanged -= value;
	}

	/// <summary>
	/// Platform handler contract. A platform companion assembly implements this (wrapping the native
	/// spell-checker) and registers it via <c>[assembly: ExportHandler(typeof(SpellChecker), typeof(...))]</c>.
	/// </summary>
	public new interface IHandler : Widget.IHandler, ITextChecker
	{
	}
}
