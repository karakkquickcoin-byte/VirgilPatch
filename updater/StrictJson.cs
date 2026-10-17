using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace VirgilPatch
{
    // Validate grammar and decoded property uniqueness before JavaScriptSerializer builds the object graph.
    static class StrictJson
    {
        sealed class Validator
        {
            readonly string text;
            readonly JavaScriptSerializer js;
            int pos;
            static readonly Regex Number = new Regex(@"\G-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?", RegexOptions.CultureInvariant);
            internal Validator(string value, JavaScriptSerializer serializer) { text = value; js = serializer; }
            void White() { while (pos < text.Length && (text[pos] == ' ' || text[pos] == '\t' || text[pos] == '\r' || text[pos] == '\n')) pos++; }
            bool Eat(char c) { White(); if (pos < text.Length && text[pos] == c) { pos++; return true; } return false; }
            void Need(char c) { if (!Eat(c)) throw new InvalidDataException("Malformed JSON near character " + pos + "."); }
            string String()
            {
                White(); int start = pos; Need('"');
                while (pos < text.Length)
                {
                    char c = text[pos++];
                    if (c == '"') return js.Deserialize<string>(text.Substring(start, pos - start));
                    if (c < 32) throw new InvalidDataException("Control character in JSON string.");
                    if (c == '\\')
                    {
                        if (pos >= text.Length) break;
                        c = text[pos++];
                        if (c == 'u')
                        {
                            for (int i = 0; i < 4; i++)
                                if (pos >= text.Length || !Uri.IsHexDigit(text[pos++])) throw new InvalidDataException("Invalid JSON Unicode escape.");
                        }
                        else if ("\"\\/bfnrt".IndexOf(c) < 0) throw new InvalidDataException("Invalid JSON escape.");
                    }
                }
                throw new InvalidDataException("Unterminated JSON string.");
            }
            void Value(int depth)
            {
                if (depth > 64) throw new InvalidDataException("JSON is nested too deeply.");
                White(); if (pos >= text.Length) throw new InvalidDataException("Missing JSON value.");
                if (Eat('{'))
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    if (Eat('}')) return;
                    do { var name = String(); if (!names.Add(name)) throw new InvalidDataException("Duplicate JSON property: " + name); Need(':'); Value(depth + 1); } while (Eat(','));
                    Need('}'); return;
                }
                if (Eat('['))
                {
                    if (Eat(']')) return;
                    do { Value(depth + 1); } while (Eat(','));
                    Need(']'); return;
                }
                if (text[pos] == '"') { String(); return; }
                foreach (var literal in new[] { "true", "false", "null" })
                    if (pos + literal.Length <= text.Length && string.CompareOrdinal(text, pos, literal, 0, literal.Length) == 0) { pos += literal.Length; return; }
                var number = Number.Match(text, pos);
                if (!number.Success) throw new InvalidDataException("Invalid JSON value near character " + pos + ".");
                pos += number.Length;
            }
            internal void Check() { Value(0); White(); if (pos != text.Length) throw new InvalidDataException("Trailing data in JSON."); }
        }
        internal static Dictionary<string, object> Read(byte[] bytes, int limit)
        {
            if (bytes == null || bytes.Length > limit) throw new InvalidDataException("JSON exceeds its size limit.");
            try
            {
                var text = new UTF8Encoding(false, true).GetString(bytes);
                if (text.StartsWith("\ufeff", StringComparison.Ordinal)) text = text.Substring(1);
                var js = new JavaScriptSerializer { MaxJsonLength = limit, RecursionLimit = 64 };
                new Validator(text, js).Check();
                var result = js.Deserialize<Dictionary<string, object>>(text);
                if (result == null) throw new InvalidDataException("Expected a JSON object.");
                return result;
            }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("JSON is not valid UTF-8.", ex); }
            catch (ArgumentException ex) { throw new InvalidDataException("Malformed JSON.", ex); }
            catch (InvalidOperationException ex) { throw new InvalidDataException("Malformed JSON.", ex); }
        }
    }
}
