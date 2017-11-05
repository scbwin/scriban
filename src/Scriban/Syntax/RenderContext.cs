// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license. 
// See license.txt file in the project root for full license information.

using System.Collections;
using System.Collections.Generic;
using System.IO;
using Scriban.Helpers;
using Scriban.Runtime;

namespace Scriban.Syntax
{
    /// <summary>
    /// Render context used to write an AST/<see cref="ScriptNode"/> tree back to a text.
    /// </summary>
    public class RenderContext
    {
        private readonly IScriptOutput _output;
        private bool _isInCode;
        private bool _expectSpace;
        private bool _expectEnd;
        private bool _expectEndOfStatement;
        // Gets a boolean indicating whether the last character written has a whitespace.
        private bool _previousHasSpace;
        private bool _nextLStrip;
        private bool _nextRStrip;
        private bool _hasEndOfStatement;
        private FastStack<bool> _isWhileLoop;
        private ScriptRawStatement _previousRawStatement;

        public RenderContext(IScriptOutput output, RenderOptions options = default(RenderOptions))
        {
            _isWhileLoop = new FastStack<bool>(4);
            Options = options;
            _output = output;
        }

        /// <summary>
        /// Gets the options for rendering
        /// </summary>
        public readonly RenderOptions Options;

        public bool PreviousHasSpace => _previousHasSpace;

        public bool IsInWhileLoop => _isWhileLoop.Count > 0 && _isWhileLoop.Peek();

        public RenderContext Write(ScriptNode node)
        {
            if (node != null)
            {
                bool pushedWhileLoop = false;
                if (node is ScriptLoopStatementBase)
                {
                    _isWhileLoop.Push(node is ScriptWhileStatement);
                    pushedWhileLoop = true;
                }
                try
                {
                    WriteBegin(node);
                    node.Write(this);
                    WriteEnd(node);
                }
                finally
                {
                    if (pushedWhileLoop)
                    {
                        _isWhileLoop.Pop();
                    }

                    if (!IsBlockOrPage(node))
                    {
                        _previousRawStatement = node as ScriptRawStatement;
                    }
                }
            }
            return this;
        }

        public RenderContext Write(string text)
        {
            _previousHasSpace = text.Length > 0 && char.IsWhiteSpace(text[text.Length - 1]);
            _output.Write(text);
            return this;
        }

        public RenderContext ExpectEos()
        {
            if (!_hasEndOfStatement)
            {
                _expectEndOfStatement = true;
            }
            return this;
        }

        public RenderContext ExpectSpace()
        {
            _expectSpace = true;
            return this;
        }

        public RenderContext ExpectEnd()
        {
            _expectEnd = true;
            ExpectEos();
            return this;
        }

        public RenderContext WriteListWithCommas<T>(IList<T> list) where T : ScriptNode
        {
            if (list == null)
            {
                return this;
            }
            for(int i = 0; i < list.Count; i++)
            {
                var value = list[i];
                Write(value);

                // If the value didn't have any Comma Trivia, we can emit it
                if (i + 1 < list.Count && !value.HasTrivia(ScriptTriviaType.Comma, false))
                {
                    Write(",");
                }
            }
            return this;
        }

        public RenderContext WriteEnterCode(int escape = 0)
        {
            Write("{");
            for (int i = 0; i < escape; i++)
            {
                Write("%");
            }
            Write("{");
            if (_nextLStrip)
            {
                Write("~");
                _nextLStrip = false;
            }
            _expectEndOfStatement = false;
            _expectEnd = false;
            _expectSpace = false;
            _hasEndOfStatement = false;
            _isInCode = true;
            return this;
        }

        public RenderContext WriteExitCode(int escape = 0)
        {
            if (_nextRStrip)
            {
                Write("~");
                _nextRStrip = false;
            }
            Write("}");
            for (int i = 0; i < escape; i++)
            {
                Write("%");
            }
            Write("}");

            _expectEndOfStatement = false;
            _expectEnd = false;
            _expectSpace = false;
            _hasEndOfStatement = false;
            _isInCode = false;
            return this;
        }

        private void WriteBegin(ScriptNode node)
        {
            var rawStatement = node as ScriptRawStatement;
            if (!IsBlockOrPage(node))
            {
                if (_isInCode)
                {
                    if (rawStatement != null)
                    {
                        _nextRStrip = rawStatement.HasTrivia(ScriptTriviaType.Whitespace, true);
                        WriteExitCode();
                    }
                }
                else if (rawStatement == null)
                {
                    if (_previousRawStatement != null)
                    {
                        _nextLStrip = _previousRawStatement.HasTrivia(ScriptTriviaType.Whitespace, false);
                    }
                    WriteEnterCode();
                }
            }

            WriteTrivias(node, true);

            HandleEos(node);

            // Add a space if this is required and no trivia are providing it
            if (node.CanHaveLeadingTrivia())
            {
                if (_expectSpace && !_previousHasSpace)
                {
                    Write(" ");
                }
                _expectSpace = false;
            }
        }

        private void WriteEnd(ScriptNode node)
        {
            if (_expectEnd)
            {
                HandleEos(node);

                var triviasHasEnd = node.HasTrivia(ScriptTriviaType.End, false);

                if (_previousRawStatement != null)
                {
                    _nextLStrip = _previousRawStatement.HasTrivia(ScriptTriviaType.Whitespace, false);
                }

                if (!_isInCode)
                {
                    WriteEnterCode();
                }

                if (triviasHasEnd)
                {
                    WriteTrivias(node, false);
                }
                else
                {
                    Write(_isInCode ? "end" : " end ");
                }

                if (!_isInCode)
                {
                    WriteExitCode();
                }
                else
                {
                    _expectEndOfStatement = true;
                }
                _expectEnd = false;
            }
            else
            {
                WriteTrivias(node, false);
            }

            if (node is ScriptPage)
            {
                if (_isInCode)
                {
                    WriteExitCode();
                }
            }
        }

        private void HandleEos(ScriptNode node)
        {
            if (node is ScriptStatement && !IsBlockOrPage(node) && _isInCode && _expectEndOfStatement)
            {
                if (!_hasEndOfStatement)
                {
                    if (!(node is ScriptRawStatement))
                    {
                        Write("; ");
                    }
                }
                _expectEndOfStatement = false;
                _hasEndOfStatement = false;
            }
        }

        private static bool IsBlockOrPage(ScriptNode node)
        {
            return node is ScriptBlockStatement || node is ScriptPage;
        }

        private void WriteTrivias(ScriptNode node, bool before)
        {
            if (node.Trivias != null)
            {
                foreach (var trivia in (before ? node.Trivias.Before : node.Trivias.After))
                {
                    trivia.Write(this);
                    if (trivia.Type == ScriptTriviaType.End)
                    {
                        _hasEndOfStatement = false;
                    }
                    else if (trivia.Type == ScriptTriviaType.NewLine || trivia.Type == ScriptTriviaType.SemiColon)
                    {
                        _hasEndOfStatement = true;
                        // If expect a space and we have a NewLine or SemiColon, we can safely discard the required space
                        if (_expectSpace)
                        {
                            _expectSpace = false;
                        }
                    }
                }
            }
        }
    }
}