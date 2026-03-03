

using System.ComponentModel;
using System.Runtime.CompilerServices;

using Eto.Drawing;

namespace Eto.ExtendedRichTextArea.Model;

public class Attributes : INotifyPropertyChanged
{
	Font? _font;
	Font? _baseFont;
	FontFamily? _family;
	FontTypeface? _typeface;
	Brush? _foreground;
	Brush? _background;
	float? _size;
	bool? _underline;
	bool? _strikethrough;
	bool? _bold;
	bool? _italic;
	bool? _superscript;
	bool? _subscript;

	[Flags]
	enum VariesAttributes
	{
		None = 0,
		FontFamily = 1 << 0,
		Typeface = 1 << 1,
		Size = 1 << 2,
		Foreground = 1 << 3,
		Background = 1 << 4,
		Underline = 1 << 5,
		Strikethrough = 1 << 6,
		Superscript = 1 << 7,
		Subscript = 1 << 8,
		Bold = 1 << 9,
		Italic = 1 << 10
	}

	VariesAttributes _variesAttributes;


	public bool IsEmpty =>
		_family == null
		&& _typeface == null
		&& _foreground == null
		&& _background == null
		&& _underline == null
		&& _strikethrough == null
		&& _superscript == null
		&& _subscript == null
		&& _bold == null
		&& _italic == null
		&& _size == null;

	public event PropertyChangedEventHandler? PropertyChanged;

	protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}

	public Font? BaseFont => _baseFont ??= CreateFont(true);

	public Font? Font
	{
		get => _font ??= CreateFont(false);
		set
		{
			_font = value;
			_baseFont = null;
			_family = _font?.Family;
			_typeface = _font?.Typeface;
			_size = _font?.Size;
			_underline = _font?.Underline;
			_strikethrough = _font?.Strikethrough;
			_bold = _font?.Bold;
			_italic = _font?.Italic;
			OnPropertyChanged(nameof(Family));
			OnPropertyChanged(nameof(Typeface));
			OnPropertyChanged(nameof(Size));
			OnPropertyChanged(nameof(Underline));
			OnPropertyChanged(nameof(Strikethrough));
			OnPropertyChanged(nameof(Bold));
			OnPropertyChanged(nameof(Italic));
			OnPropertyChanged();
		}
	}

	public bool? Bold
	{
		get => _variesAttributes.HasFlag(VariesAttributes.Bold) ? null : _bold ?? Font?.Bold;
		set
		{
			if (value == _bold)
				return;

			_bold = value;
			_font = null;
			_baseFont = null;
			_typeface = null;
			_variesAttributes &= ~VariesAttributes.Bold;
			if (!_variesAttributes.HasFlag(VariesAttributes.Italic))
				_variesAttributes &= ~VariesAttributes.Typeface;

			OnPropertyChanged();
			OnPropertyChanged(nameof(Typeface));
			OnPropertyChanged(nameof(Font));
		}
	}

	public bool? Italic
	{
		get => _variesAttributes.HasFlag(VariesAttributes.Italic) ? null : _italic ?? Font?.Italic;
		set
		{
			if (value == _italic)
				return;

			_italic = value;
			_font = null;
			_baseFont = null;
			_typeface = null;
			_variesAttributes &= ~VariesAttributes.Italic;
			if (!_variesAttributes.HasFlag(VariesAttributes.Bold))
				_variesAttributes &= ~VariesAttributes.Typeface;
			
			OnPropertyChanged();
			OnPropertyChanged(nameof(Typeface));
			OnPropertyChanged(nameof(Font));
		}
	}

	internal float? Baseline
	{
		get
		{
			var baseline = BaseFont?.Baseline;
			if (baseline == null)
				return null;
			if (Platform.Instance.IsWpf)
				baseline *= 96f / 72f;
			return baseline.Value;
		}
	}

	internal float? LineHeight
	{
		get
		{
			var lineHeight = BaseFont?.LineHeight;
			if (lineHeight == null)
				return null;
			return lineHeight.Value * Scale;
		}
	}

	static float Scale => Platform.Instance.IsWpf ? 96f / 72f : 1f;

	internal float? LineOffset
	{
		get
		{
			var font = Font;
			if (font == null)
				return 0;

			if (Superscript == true)
				return 0;
			if (Subscript == true)
				return LineHeight - font.LineHeight * Scale;
			return 0;
		}
	}

	public float? Size
	{
		get => _variesAttributes.HasFlag(VariesAttributes.Size) ? null : _size ?? Font?.Size;
		set
		{
			_size = value;
			_font = null;
			_baseFont = null;
			_variesAttributes &= ~VariesAttributes.Size;
			OnPropertyChanged();
			OnPropertyChanged(nameof(Font));
		}
	}

	public FontFamily? Family
	{
		get => _variesAttributes.HasFlag(VariesAttributes.FontFamily) ? null : _family ?? _typeface?.Family ?? Font?.Family;
		set
		{
			if (_family == value)
				return;
			_family = value;
			_font = null;
			_baseFont = null;
			_typeface = null;
			_variesAttributes &= ~VariesAttributes.FontFamily;
			OnPropertyChanged();
			OnPropertyChanged(nameof(Typeface));
			OnPropertyChanged(nameof(Bold));
			OnPropertyChanged(nameof(Italic));
			OnPropertyChanged(nameof(Font));
		}
	}

	public FontTypeface? Typeface
	{
		get => _variesAttributes.HasFlag(VariesAttributes.Typeface) ? null : _typeface ?? Font?.Typeface;
		set
		{
			if (_typeface == value)
				return;
			_typeface = value;
			_font = null;
			_baseFont = null;
			_family = null;
			_bold = null;
			_italic = null;
			_variesAttributes &= ~VariesAttributes.Typeface;
			_variesAttributes &= ~VariesAttributes.FontFamily;
			OnPropertyChanged();
			OnPropertyChanged(nameof(Family));
			OnPropertyChanged(nameof(Bold));
			OnPropertyChanged(nameof(Italic));
			OnPropertyChanged(nameof(Font));
		}
	}

	private Font? CreateFont(bool baseFont)
	{
		FontTypeface typeface = CreateTypeface();

		var decoration = FontDecoration.None;
		if (_underline == true)
			decoration |= FontDecoration.Underline;
		if (_strikethrough == true)
			decoration |= FontDecoration.Strikethrough;

		var size = _size ?? 12;
		if (!baseFont)
		{
			if (Superscript == true || Subscript == true)
				size *= 0.65f;
		}
		return new Font(typeface, size, decoration);
	}

	private FontTypeface CreateTypeface()
	{
		var typeface = _typeface ?? _family?.Typefaces.FirstOrDefault() ?? Document.GetDefaultFont().Typeface;

		var family = _typeface?.Family ?? _family;
		if (_bold != null || _italic != null)
		{
			var bold = _bold ?? typeface.Bold;
			var italic = _italic ?? typeface.Italic;
			typeface = family?.Typefaces.FirstOrDefault(r => r.Bold == bold && r.Italic == italic);

			// can't find one with both styles, try just bold
			if (typeface == null)
				typeface = family?.Typefaces.FirstOrDefault(r => r.Bold == bold);
				
			// finally, try just italic
			if (typeface == null)
				typeface = family?.Typefaces.FirstOrDefault(r => r.Italic == italic);
		}
		return typeface ?? _family?.Typefaces.FirstOrDefault() ?? Document.GetDefaultFont().Typeface;
	}

	public Brush? Foreground
	{
		get => _variesAttributes.HasFlag(VariesAttributes.Foreground) ? null : _foreground;
		set
		{
			if (value == _foreground)
				return;
			_foreground = value;
			_variesAttributes &= ~VariesAttributes.Foreground;
			OnPropertyChanged();
		}
	}

	public Brush? Background
	{
		get => _variesAttributes.HasFlag(VariesAttributes.Background) ? null : _background;
		set
		{
			if (value == _background)
				return;
			_background = value;
			_variesAttributes &= ~VariesAttributes.Background;
			OnPropertyChanged();
		}
	}

	public bool? Underline
	{
		get => _variesAttributes.HasFlag(VariesAttributes.Underline) ? null : _underline ?? Font?.Underline;
		set
		{
			if (value == _underline)
				return;
			_underline = value;
			_font = null;
			_baseFont = null;
			_variesAttributes &= ~VariesAttributes.Underline;
			OnPropertyChanged();
			OnPropertyChanged(nameof(Font));
		}
	}
	public bool? Strikethrough
	{
		get => _variesAttributes.HasFlag(VariesAttributes.Strikethrough) ? null : _strikethrough ?? Font?.Strikethrough;
		set
		{
			if (value == _strikethrough)
				return;
			_strikethrough = value;
			_font = null;
			_baseFont = null;
			_variesAttributes &= ~VariesAttributes.Strikethrough;
			OnPropertyChanged();
			OnPropertyChanged(nameof(Font));
		}
	}

	public bool? Superscript
	{
		get => _variesAttributes.HasFlag(VariesAttributes.Superscript) ? null : _superscript ?? false;
		set
		{
			if (_superscript == value)
				return;

			_font = null;
			_baseFont = null;
			_superscript = value;
			_variesAttributes &= ~VariesAttributes.Superscript;
			if (value == true)
			{
				_subscript = null;
				_variesAttributes &= ~VariesAttributes.Subscript;
				OnPropertyChanged(nameof(Subscript));
			}
			OnPropertyChanged();
		}
	}

	public bool? Subscript
	{
		get => _variesAttributes.HasFlag(VariesAttributes.Subscript) ? null : _subscript ?? false;
		set
		{
			if (_subscript == value)
				return;
			_font = null;
			_baseFont = null;
			_subscript = value;
			_variesAttributes &= ~VariesAttributes.Subscript;
			if (value == true)
			{
				_superscript = null;
				_variesAttributes &= ~VariesAttributes.Superscript;
				OnPropertyChanged(nameof(Superscript));
			}
			OnPropertyChanged();
		}
	}

	public Attributes Clone()
	{
		return new Attributes
		{
			_font = _font,
			_baseFont = _baseFont,
			_family = _family,
			_typeface = _typeface,
			_size = _size,
			_foreground = _foreground,
			_background = _background,
			_underline = _underline,
			_strikethrough = _strikethrough,
			_subscript = _subscript,
			_superscript = _superscript,
			_bold = _bold,
			_italic = _italic
		};
	}

	public Attributes Merge(Attributes? attributes, bool copy)
	{
		if (!copy)
		{
			if (attributes == null || attributes.IsEmpty)
				return this;
			if (IsEmpty)
				return attributes;
		}
		var clone = Clone();
		if (attributes == null)
			return clone;

		if (attributes._family != null || attributes._typeface != null
			|| attributes._bold != null || attributes._italic != null)
		{
			clone._font = null;
			clone._baseFont = null;
			if (attributes._family != null)
				clone._family = attributes._family;
			if (attributes._typeface != null)
			{
				clone._typeface = attributes._typeface;
				clone._family = null;
			}
			if (attributes._bold != null)
				clone._bold = attributes._bold;
			if (attributes._italic != null)
				clone._italic = attributes._italic;
		}

		if (attributes._size != null)
			clone.Size = attributes._size;
		if (attributes._foreground != null)
			clone.Foreground = attributes._foreground;
		if (attributes._background != null)
			clone.Background = attributes._background;
		if (attributes._underline != null)
			clone.Underline = attributes._underline;
		if (attributes._strikethrough != null)
			clone.Strikethrough = attributes._strikethrough;
		if (attributes._subscript != null)
			clone.Subscript = attributes._subscript;
		if (attributes._superscript != null)
			clone.Superscript = attributes._superscript;

		return clone;
	}

	internal void ClearUnmatched(Attributes? attributes)
	{
		_font = null;
		_baseFont = null;

		if (Bold != attributes?.Bold)
		{
			_bold = null;
			_typeface = null;
			_variesAttributes |= VariesAttributes.Bold | VariesAttributes.Typeface;
		}
		if (Italic != attributes?.Italic)
		{
			_italic = null;
			_typeface = null;
			_variesAttributes |= VariesAttributes.Italic | VariesAttributes.Typeface;
		}
		if (Family != attributes?.Family)
		{
			_family = null;
			_variesAttributes |= VariesAttributes.FontFamily;
		}
		if (Typeface != attributes?.Typeface)
		{
			if (_typeface != null && _typeface.Family == attributes?.Family)
			{
				_family = _typeface.Family;
			}
			_typeface = null;
			_variesAttributes |= VariesAttributes.Typeface;
		}
		if (_size != attributes?._size)
		{
			_size = null;
			_variesAttributes |= VariesAttributes.Size;
		}
		if (_foreground != attributes?._foreground)
		{
			_foreground = null;
			_variesAttributes |= VariesAttributes.Foreground;
		}
		if (_background != attributes?._background)
		{
			_background = null;
			_variesAttributes |= VariesAttributes.Background;
		}
		if (_underline != attributes?._underline)
		{
			_underline = null;
			_variesAttributes |= VariesAttributes.Underline;
		}
		if (_strikethrough != attributes?._strikethrough)
		{
			_strikethrough = null;
			_variesAttributes |= VariesAttributes.Strikethrough;
		}
		if (_subscript != attributes?._subscript)
		{
			_subscript = null;
			_variesAttributes |= VariesAttributes.Subscript;
		}
		if (_superscript != attributes?._superscript)
		{
			_superscript = null;
			_variesAttributes |= VariesAttributes.Superscript;
		}
	}

	public override bool Equals(object? obj)
	{
		if (obj == null || obj is not Attributes other)
			return false;

		if (ReferenceEquals(this, obj))
			return true;

		return
			_typeface == other._typeface &&
			_family == other._family &&
			_size == other._size &&
			_foreground == other._foreground &&
			_background == other._background &&
			_underline == other._underline &&
			_strikethrough == other._strikethrough &&
			_subscript == other._subscript &&
			_superscript == other._superscript &&
			_bold == other._bold &&
			_italic == other._italic;
	}

	public static bool operator ==(Attributes? attributes1, Attributes? attributes2)
	{
		if (ReferenceEquals(attributes1, null) && ReferenceEquals(attributes2, null))
			return true;
		return attributes1?.Equals(attributes2) == true;
	}

	public static bool operator !=(Attributes? attributes1, Attributes? attributes2)
	{
		if (ReferenceEquals(attributes1, null) && ReferenceEquals(attributes2, null))
			return false;
		return attributes1?.Equals(attributes2) == false;
	}

	public override int GetHashCode()
	{
		return
			_family?.GetHashCode() ?? 0 ^
			_typeface?.GetHashCode() ?? 0 ^
			_size?.GetHashCode() ?? 0 ^
			_foreground?.GetHashCode() ?? 0 ^
			_background?.GetHashCode() ?? 0 ^
			_underline?.GetHashCode() ?? 0 ^
			_strikethrough?.GetHashCode() ?? 0 ^
			_subscript?.GetHashCode() ?? 0 ^
			_superscript?.GetHashCode() ?? 0 ^
			_bold?.GetHashCode() ?? 0 ^
			_italic?.GetHashCode() ?? 0;
	}

	public void Apply(FormattedText formattedText)
	{
		if (Font != null)
			formattedText.Font = Font;
		if (Foreground != null)
			formattedText.ForegroundBrush = Foreground;
	}
}