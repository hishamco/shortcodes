﻿using System;
using System.Collections.Generic;
using System.Text;

namespace Shortcodes
{
    public class Scanner
    {
        private readonly string _text;
        private Token _token;
        private Stack<Cursor> _cursors = new Stack<Cursor>();
        private Cursor _cursor;
        private StringBuilder _sb;

        public Scanner(string text)
        {
            _text = text;
            _cursor = new Cursor(_text, 0);
            _cursors.Push(_cursor);
        }

        public Action<Token> OnToken { get; set; }

        public void CreateCursor()
        {
            _cursors.Push(_cursor);
            _cursor = _cursor.Clone();
        }

        public void DiscardCursor()
        {
            _cursor = _cursors.Pop();
        }

        public void PromoteCursor()
        {
            _cursors.Pop();
        }

        /// <summary>
        /// Reads any whitespace without generating a token.
        /// </summary>
        /// <returns>Whether some white space was read.</returns>
        public bool SkipWhiteSpace()
        {
            if (!Character.IsWhiteSpace(_cursor.Char))
            {
                return false;
            }

            while (Character.IsWhiteSpace(_cursor.Char))
            {
                _cursor.Advance();
            }

            return true;
        }

        public void EmitToken(string type, int start, int length)
        {
            _token = new Token(type, _text, start, length);

            OnToken?.Invoke(_token);
        }

        public bool ReadIdentifier()
        {
            var start = _cursor.Offset;

            if (!Character.IsIdentifierStart(_cursor.Char))
            {
                return false;
            }

            _cursor.Advance();

            while (Character.IsIdentifierPart(_cursor.Char))
            {
                if (_cursor.Eof)
                {
                    return false;
                }

                _cursor.Advance();
            }

            EmitToken("identifier", start, _cursor.Offset - start);

            return true;
        }

        public bool ReadValue()
        {
            var start = _cursor.Offset;

            if (_cursor.Char == ']' || _cursor.Char == '\'' || _cursor.Char == '"')
            {
                return false;
            }

            if (_cursor.Char == '/' && _cursor.PeekNext() == ']')
            {
                return false;
            } 

            while (!Character.IsWhiteSpace(_cursor.Char) && _cursor.Char != ']')
            {
                if (_cursor.Eof)
                {
                    return false;
                }

                _cursor.Advance();
            }

            var length = _cursor.Offset - start; 
            
            if (length == 0)
            {
                return false;
            }

            EmitToken("value", start, length);

            return true;
        }

        public List<Node> Scan()
        {
            var nodes = new List<Node>();

            var start = _cursor.Offset;

            CreateCursor();

            while (!_cursor.Eof)
            {
                var startShortcode = _cursor.Offset;

                if (ReadShortcode(out var shortcode))
                {
                    if (startShortcode - start > 0)
                    {
                        nodes.Add(new RawText(_text.Substring(start, startShortcode - start)));
                    }

                    nodes.Add(shortcode);

                    start = _cursor.Offset;
                }
                else
                {
                    _cursor.Advance();
                }
            }

            if (start < _text.Length)
            {
                nodes.Add(new RawText(_text.Substring(start)));
            }

            return nodes;
        }

        public bool ReadShortcode(out Shortcode shortcode)
        {
            // Number of opening braces
            var openBraces = 0;

            // Number of closing braces
            var closeBraces = 0;
            
            shortcode = null;
            var style = ShortcodeStyle.Open;

            if (_cursor.Char != '[')
            {
                return false;
            }

            CreateCursor();

            // Start position of the shortcode
            var index = _cursor.Offset;

            // Read all '[' so we can detect escaped tags
            do 
            {
                openBraces += 1;
                _cursor.Advance();
            } while (_cursor.Char == '[');

            // Is it a closing tag?
            if (_cursor.Char == '/')
            {
                style = ShortcodeStyle.Close;

                _cursor.Advance();
            }

            // Reach Eof before end of shortcode
            if (_cursor.Eof)
            {
                DiscardCursor();

                return false;
            }

            SkipWhiteSpace();

            if (!ReadIdentifier())
            {
                DiscardCursor();

                return false;
            }

            Token identifier = _token;

            SkipWhiteSpace();

            Dictionary<string, string> arguments = null;

            int argumentIndex = 0;

            // Arguments?
            while (true)
            {
                if (ReadString())
                {
                    arguments ??= CreateArgumentsDictionary();

                    arguments[argumentIndex.ToString()] = DecodeString(_token.ToString());

                    argumentIndex += 1;
                }
                else if (ReadIdentifier())
                {
                    var argument = _token;

                    SkipWhiteSpace();

                    // It might just be a value
                    if (ReadEqualSign())
                    {
                        SkipWhiteSpace();

                        if (ReadString())
                        {
                            arguments ??= CreateArgumentsDictionary();

                            arguments[argument.ToString()] = DecodeString(_token.ToString());
                        }
                        else if (ReadValue())
                        {
                            arguments ??= CreateArgumentsDictionary();

                            arguments[argument.ToString()] = _token.ToString();
                        }
                        else
                        {
                            DiscardCursor();

                            return false;
                        }
                    }
                    else
                    {
                        // Positional argument that looks like an identifier

                        _cursor.Seek(argument.Start);

                        if (ReadValue())
                        {
                            arguments ??= CreateArgumentsDictionary();
                            
                            arguments[argumentIndex.ToString()] = _token.ToString();

                            argumentIndex += 1;
                        }
                        else
                        {
                            _cursor.Seek(argument.Start);

                            break;
                        }
                    }
                }
                else if (ReadValue())
                {
                    arguments ??= CreateArgumentsDictionary();

                    arguments[argumentIndex.ToString()] = _token.ToString();

                    argumentIndex += 1;
                }
                else
                {
                    break;
                }

                SkipWhiteSpace();
            }

            // Is it a self-closing tag?
            if (_cursor.Char == '/' && _cursor.PeekNext() == ']')
            {
                style = ShortcodeStyle.SelfClosing;

                _cursor.Advance();
            }

            // Expect closing bracket
            if (_cursor.Char != ']')
            {
                DiscardCursor();

                return false;
            }

            // Read all ']' so we can detect escaped tags
            do 
            {
                closeBraces += 1;
                _cursor.Advance();
            } while (_cursor.Char == ']');

            shortcode = new Shortcode(identifier.ToString(), style, openBraces, closeBraces, index, _cursor.Offset - index - 1);
            shortcode.Arguments = new Arguments(arguments);

            PromoteCursor();

            return true;

            // Local function to use the same logic to create the arguments dictionary
            Dictionary<string, string> CreateArgumentsDictionary()
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        public bool ReadEqualSign()
        {
            if (_cursor.Char != '=')
            {
                return false;
            }

            _cursor.Advance();

            return true;
        }

        public bool ReadString()
        {
            var start = _cursor.Offset;

            var startChar = _cursor.Char;

            if (startChar != '\'' && startChar != '"')
            {
                return false;
            }
            
            CreateCursor();

            _cursor.Advance();

            while (_cursor.Char != startChar)
            {
                if (_cursor.Eof)
                {
                    return false;
                }

                if (_cursor.Char == '\\')
                {
                    _cursor.Advance();

                    switch (_cursor.Char)
                    {
                        case '0':
                        case '\'':
                        case '"':
                        case '\\':
                        case 'b':
                        case 'f':
                        case 'n':
                        case 'r':
                        case 't':
                        case 'v':
                            break;
                        case 'u':
                            var isValidUnicode = false;

                            _cursor.Advance();

                            if (!_cursor.Eof && Character.IsHexDigit(_cursor.Char))
                            {
                                _cursor.Advance();
                                if (!_cursor.Eof && Character.IsHexDigit(_cursor.Char))
                                {
                                    _cursor.Advance();
                                    if (!_cursor.Eof && Character.IsHexDigit(_cursor.Char))
                                    {
                                        _cursor.Advance();
                                        isValidUnicode = true;
                                    }
                                }
                            }

                            if (!isValidUnicode)
                            {
                                DiscardCursor();

                                return false;
                            }

                            break;
                        case 'x':
                            bool isValidHex = false;

                            _cursor.Advance();

                            if (!_cursor.Eof && Character.IsHexDigit(_cursor.Char))
                            {
                                _cursor.Advance();
                                if (!_cursor.Eof && Character.IsHexDigit(_cursor.Char))
                                {
                                    isValidHex = true;
                                }
                            }

                            if (!isValidHex)
                            {
                                DiscardCursor();
                                
                                return false;
                            }

                            break;
                        default:
                            DiscardCursor();

                            return false;
                    }
                }

                _cursor.Advance();
            }

            _cursor.Advance();

            EmitToken("string", start + 1, _cursor.Offset - start - 2);

            return true;
        }

        public string DecodeString(string text)
        {
            // Nothing to do if the string doesn't have any escape char
            if (text.IndexOf('\\') == -1)
            {
                return text;
            }

            var sb = GetStringBuilder();

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];

                if (c == '\\')
                {
                    i = i + 1;
                    c = text[i];

                    switch (c)
                    {
                        case '0': sb.Append("\0"); break;
                        case '\'': sb.Append("\'"); break;
                        case '"': sb.Append("\""); break;
                        case '\\': sb.Append("\\"); break;
                        case 'b': sb.Append("\b"); break;
                        case 'f': sb.Append("\f"); break;
                        case 'n': sb.Append("\n"); break;
                        case 'r': sb.Append("\r"); break;
                        case 't': sb.Append("\t"); break;
                        case 'v': sb.Append("\v"); break;
                        case 'u':
                            sb.Append(Character.ScanHexEscape(text, i));
                            i = i + 4;
                            break;
                        case 'x':
                            sb.Append(Character.ScanHexEscape(text, i));
                            i = i + 2;
                            break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        private StringBuilder GetStringBuilder()
        {
            _sb ??= new StringBuilder();
            _sb.Clear();
            return _sb;
        }
    }
}
