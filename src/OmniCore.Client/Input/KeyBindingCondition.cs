using System.Collections.Immutable;

namespace OmniCore.Client;

/// <summary>Bounded boolean grammar, with no evaluation of code, commands or member access.</summary>
internal sealed class KeyBindingCondition
{
    private readonly Func<KeyBindingContext, bool> _evaluate;
    public ImmutableHashSet<string> Variables { get; }
    private KeyBindingCondition(Func<KeyBindingContext, bool> evaluate, IEnumerable<string> variables)
    { _evaluate = evaluate; Variables = variables.ToImmutableHashSet(StringComparer.Ordinal); }
    public bool Evaluate(KeyBindingContext context) => _evaluate(context);

    public static KeyBindingCondition Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 512) throw new FormatException("condition_length");
        return new Parser(text).Parse();
    }

    private sealed class Parser(string text)
    {
        private int _position;
        private int _depth;
        private readonly HashSet<string> _variables = new(StringComparer.Ordinal);
        public KeyBindingCondition Parse()
        {
            var expression = Or(); White();
            if (_position != text.Length) throw new FormatException("condition_token");
            return new(expression, _variables);
        }
        private void White() { while (_position < text.Length && char.IsWhiteSpace(text[_position])) _position++; }
        private bool Take(string token)
        {
            White();
            if (!text.AsSpan(_position).StartsWith(token, StringComparison.Ordinal)) return false;
            _position += token.Length; return true;
        }
        private Func<KeyBindingContext, bool> Or()
        {
            var left = And();
            while (Take("||")) { var prior = left; var right = And(); left = context => prior(context) || right(context); }
            return left;
        }
        private Func<KeyBindingContext, bool> And()
        {
            var left = Atom();
            while (Take("&&")) { var prior = left; var right = Atom(); left = context => prior(context) && right(context); }
            return left;
        }
        private Func<KeyBindingContext, bool> Atom()
        {
            if (++_depth > 32) throw new FormatException("condition_depth");
            try
            {
                if (Take("!")) { var operand = Atom(); return context => !operand(context); }
                if (Take("(")) { var expression = Or(); if (!Take(")")) throw new FormatException("condition_parenthesis"); return expression; }
                White(); var start = _position;
                while (_position < text.Length && (char.IsAsciiLetterOrDigit(text[_position]) || text[_position] is '_' or '.')) _position++;
                if (start == _position || !char.IsAsciiLetter(text[start])) throw new FormatException("condition_identifier");
                var name = text[start.._position];
                if (name == "true") return _ => true;
                if (name == "false") return _ => false;
                _variables.Add(name); return context => context[name];
            }
            finally { _depth--; }
        }
    }
}
