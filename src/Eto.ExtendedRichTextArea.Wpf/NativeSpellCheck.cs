using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Eto.ExtendedRichTextArea.Wpf;

// Minimal COM interop for the Windows Spell Checking API (spellcheck.h, Windows 8+).
// Only the members needed for checking, suggestions, and adding to the dictionary are declared;
// the remaining vtable slots of each interface are intentionally omitted because nothing past the
// declared methods is called. Method order MUST match the native vtable exactly.

internal enum CorrectiveAction
{
	None = 0,
	GetSuggestions = 1,
	Replace = 2,
	Delete = 3
}

[ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISpellCheckerFactory
{
	[return: MarshalAs(UnmanagedType.Interface)] IEnumString get_SupportedLanguages();
	[return: MarshalAs(UnmanagedType.Bool)] bool IsSupported([In, MarshalAs(UnmanagedType.LPWStr)] string languageTag);
	[return: MarshalAs(UnmanagedType.Interface)] ISpellChecker CreateSpellChecker([In, MarshalAs(UnmanagedType.LPWStr)] string languageTag);
}

[ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISpellChecker
{
	[return: MarshalAs(UnmanagedType.LPWStr)] string get_LanguageTag();
	[return: MarshalAs(UnmanagedType.Interface)] IEnumSpellingError Check([In, MarshalAs(UnmanagedType.LPWStr)] string text);
	[return: MarshalAs(UnmanagedType.Interface)] IEnumString Suggest([In, MarshalAs(UnmanagedType.LPWStr)] string word);
	void Add([In, MarshalAs(UnmanagedType.LPWStr)] string word);
	// Remaining members (Ignore, AutoCorrect, GetOptionValue, ...) are not declared — not used.
}

// ISpellChecker2 (Windows 8.1+) derives from ISpellChecker and adds Remove. The vtable must list all
// of ISpellChecker's members first, in order, then Remove — so the unused middle members are declared
// purely to preserve slot layout (each declaration is one vtable slot, regardless of its signature).
[ComImport, Guid("E7ED1C71-87F7-4378-A840-C9200DACEE47"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISpellChecker2
{
	[return: MarshalAs(UnmanagedType.LPWStr)] string get_LanguageTag();
	[return: MarshalAs(UnmanagedType.Interface)] IEnumSpellingError Check([In, MarshalAs(UnmanagedType.LPWStr)] string text);
	[return: MarshalAs(UnmanagedType.Interface)] IEnumString Suggest([In, MarshalAs(UnmanagedType.LPWStr)] string word);
	void Add([In, MarshalAs(UnmanagedType.LPWStr)] string word);
	void _Ignore();
	void _AutoCorrect();
	void _GetOptionValue();
	void _get_OptionIds();
	void _get_Id();
	void _get_LocalizedName();
	void _add_SpellCheckerChanged();
	void _remove_SpellCheckerChanged();
	void _GetOptionDescription();
	void _ComprehensiveCheck();
	// --- ISpellChecker2 ---
	void Remove([In, MarshalAs(UnmanagedType.LPWStr)] string word);
}

[ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumSpellingError
{
	// Returns null (S_FALSE) when enumeration is complete.
	[return: MarshalAs(UnmanagedType.Interface)] ISpellingError? Next();
}

[ComImport, Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISpellingError
{
	uint get_StartIndex();
	uint get_Length();
	CorrectiveAction get_CorrectiveAction();
	[return: MarshalAs(UnmanagedType.LPWStr)] string get_Replacement();
}

[ComImport, Guid("00000101-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumString
{
	[PreserveSig]
	int Next(uint celt, [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 0)] string[] rgelt, out uint pceltFetched);
	[PreserveSig] int Skip(uint celt);
	[PreserveSig] int Reset();
	void Clone(out IEnumString ppenum);
}
