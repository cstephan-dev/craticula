#!/usr/bin/env python3
"""SEC-2 lint: only a placeholder, a quoted identifier, a constant or an enum may be interpolated or concatenated into
query text (TRD SEC-2, P-3; story #1660, AC-4).

Usage: lint-sec2.py [--root <repo>] [--allowlist <file>] [--stats] [--explain] [paths...]   (stdlib, Python 3.10+)

Default scope: the query-text builders csharp/src/Craticula.{Sql,Mongo,Search,Dapper,EntityFrameworkCore}/**/*.cs.
Paths are files or folders, relative to --root. Exit codes: 0 clean; 1 at least one finding or one stale allowlist
entry, each printed as `file:line: SEC-2: <expression> - <why>`; 2 usage or environment error: a scope path missing or
holding no .cs file, an unreadable or malformed allowlist, or a construct the lint cannot parse (it fails closed).

WHAT COUNTS AS QUERY TEXT. Every string construction in a scoped file is presumed query text (a "sink"):
  - each hole of an interpolated string ($"", $@"", @$"", raw $\"\"\"...\"\"\" with any number of $);
  - each operand of a `+` chain with a string-typed operand (a string or char literal, an interpolated string, a
    string-returning call, a name declared `string`), and the right side of `+=` on a string;
  - every argument of string.Join / string.Concat / string.Format (for Join over a collection: each element);
  - every argument of StringBuilder.Append / AppendLine / AppendFormat / Insert, and the text of
    `new StringBuilder(text)`.
Exempt: everything inside the arguments of `new <Name>Exception(...)`, or of a target-typed `new(...)` in a member whose
declared type ends in `Exception`, or of an exception type's `: base(...)`. Exception messages are not query text.

WHEN A SINK OPERAND IS SAFE. Checked by provenance, recursively, under this closed list:
  - a literal (string, char, number, bool, null, default, typeof), `nameof(...)`, `string.Empty`: the author wrote it;
  - a `const` of a scanned type (bare inside the type, or Type.Name): compile-time text;
  - an enum member (Enum.Member) or a name declared with an enum type (enums of the scanned files and of csharp/src, and
    StringComparison): it renders a compiled-in identifier; a bool-valued expression (comparison, !, is) likewise;
  - a PRIMITIVE call, trusted by callee name; its arguments are not query text:
      Bind(...) and the method group X.Bind: ISqlParameterSink, the value goes to the parameter list and the result is
        the dialect's placeholder (SEC-2's own mechanism);
      QuoteIdentifier(...): ISqlDialect, SQL-1, a delimited identifier with its delimiter doubled;
      ParameterPlaceholder(...): ISqlDialect, the placeholder text for an ordinal;
      Escape*(...): ONLY in Mongo, Search and EF Core files, where the result is a pattern VALUE (regex, wildcard, LIKE)
        embedded as a JSON value or bound by EF Core (SEC-4). In a SQL file an escaped value in query text is a
        finding: escaping a pattern does not make it SQL text, binding it does.
    The body of every in-scope primitive is checked with its parameters UNTRUSTED (every splice in it, and its return
    value when it returns a string), so each primitive implementation that touches raw input is a reviewed allowlist
    entry, and a method that only borrows a primitive's name (`string Bind(string x) => x;`) is caught;
  - a composition of safe parts: an interpolated string or a `+` chain (its parts are sinks of their own), string.Join,
    Concat or Format, a StringBuilder's ToString() (its appends are sinks), a cast, arithmetic, ToString/Replace/Trim/
    Substring... on a safe receiver with safe arguments, an index or a range of a safe value, LINQ Select/Where/
    ToList... whose source and selector are safe. The branches of `?:`, `??` and a switch expression (throw arms
    ignored) and the items of a tuple or collection are positions of their own, so a finding names the branch that
    brings the value in;
  - a LOCAL whose every assigned value is safe: initializer, =, +=, ??=, Add/AddRange/Insert, indexer stores, foreach
    over a safe collection, pattern and deconstruction designations. Increments add no source. An out variable, a catch
    variable or the parameter of a lambda handed to an unknown caller has no traceable source;
  - a PARAMETER of a non-primitive in-scope method whose every in-scope call site passes a safe argument (C# binding:
    by position, by name, defaults, `params`). The finding then lands on the CALL SITE, where the value enters. A
    parameter with no in-scope call site is untrusted;
  - a call to an in-scope METHOD whose every return expression is safe, for every declaration the call can reach: an
    unqualified call reaches a local function or a method of an enclosing type, `Type.M(...)` that type's methods,
    `obj.M(...)` every in-scope method named M. Tuple components consumed by deconstruction are followed one by one;
  - COMPILED QUERY TEXT: `<local>.Sql` where every value assigned to the local is a call to SqlQueryBuilder.Build or
    SqlQueryBuilder.BuildSetValues, since that text came out of the linted builder (the Dapper count wrap).
Anything else is a finding: a member of a runtime object (request.Value, key.Field), a call into code the lint cannot
see, a lambda parameter. It is reported where it enters: the sink operand, or the call-site argument, assigned value or
return expression that feeds a sink.

ALLOWLIST (scripts/sec2-allowlist.txt): `path::Type.Member | expression | reason (requirement id)`, '#' comments. The
key survives line shifts: the file, the enclosing member and the exact expression of the finding (whitespace collapsed).
An allowlisted expression is a reviewed trust boundary: accepted as it stands, nothing inside it is traced further. An
entry whose file was scanned but that matched no finding is stale and fails the run.

BLIND SPOTS (syntactic, no Roslyn): calls resolve by NAME within the reach above, so overloads, interface
implementations and virtual dispatch merge (conservative: every reachable body must be safe); a method inherited from a
base type or imported with `using static` is not seen (calling it is a finding). Code outside the scanned files is not
seen either: calling into it is a finding, and a caller of a scanned public method is not checked, so ISqlDialect's
`quotedColumn` contract binds a host that calls a dialect directly. Values that travel through fields, object
properties, delegates or reflection are untrusted, never traced. A primitive is trusted by NAME at its call sites, so a
new method named Bind, QuoteIdentifier, ParameterPlaceholder or Escape* is a review item even when its body passes.
A message built in a local and thrown later is flagged (inline it). JSON keys and paths built WITHOUT string
construction (`new JsonObject { [colId] = ... }`) are not text construction: SEC-1's field allowlist governs them.
Locals are scoped per method by name, not per block (same-named locals merge their sources). Text written through
TextWriter, Span, string.Create or FormattableString is not scanned.
"""
from __future__ import annotations

import argparse
import bisect
import re
import sys
from pathlib import Path

DEFAULT_SCOPE = (
    "csharp/src/Craticula.Sql",
    "csharp/src/Craticula.Mongo",
    "csharp/src/Craticula.Search",
    "csharp/src/Craticula.Dapper",
    "csharp/src/Craticula.EntityFrameworkCore",
)
VALUE_PROFILE_DIRS = ("Craticula.Mongo", "Craticula.Search", "Craticula.EntityFrameworkCore")
SYMBOL_SOURCES = "csharp/src"
DEFAULT_ALLOWLIST = Path(__file__).resolve().parent / "sec2-allowlist.txt"

PRIMITIVES = ("Bind", "QuoteIdentifier", "ParameterPlaceholder")
COMPILED_QUERY_CALLS = {("SqlQueryBuilder", "Build"), ("SqlQueryBuilder", "BuildSetValues")}
STRING_METHODS = {
    "ToString", "Replace", "Trim", "TrimStart", "TrimEnd", "Substring", "PadLeft", "PadRight", "ToUpper", "ToLower",
    "ToUpperInvariant", "ToLowerInvariant", "Normalize",
}
ELEMENT_METHODS = {
    "ToList", "ToArray", "AsEnumerable", "Where", "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending",
    "Distinct", "Skip", "Take", "Reverse", "Concat", "Append", "Prepend",
}
LAMBDA_ELEMENT_METHODS = {
    "Select", "Where", "Any", "All", "First", "FirstOrDefault", "Last", "LastOrDefault", "Single", "SingleOrDefault",
    "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending", "Count", "Sum", "Min", "Max", "ToDictionary",
    "GroupBy", "SelectMany", "TakeWhile", "SkipWhile",
}
BUILDER_METHODS = ("Append", "AppendLine", "AppendFormat", "Insert")
BCL_SAFE_MEMBERS = {("string", "Empty"), ("String", "Empty"), ("CultureInfo", "InvariantCulture"),
                    ("CultureInfo", "CurrentCulture"), ("Environment", "NewLine")}
BCL_ENUMS = {"StringComparison"}
STRING_TYPES = {"string", "String"}
NUMERIC_TYPES = {"int", "long", "short", "byte", "uint", "ulong", "ushort", "sbyte", "nint", "nuint"}
BOOL_OPS = {"==", "!=", "<", ">", "<=", ">=", "&&", "||"}
ASSIGN_OPS = {"=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<=", "??="}
MODIFIERS = {
    "public", "private", "protected", "internal", "static", "readonly", "const", "sealed", "abstract", "virtual",
    "override", "new", "partial", "extern", "unsafe", "volatile", "async", "required", "file", "ref", "scoped",
    "fixed", "implicit", "explicit",
}
PREDEFINED = {
    "bool", "byte", "sbyte", "char", "decimal", "double", "float", "int", "uint", "long", "ulong", "short", "ushort",
    "object", "string", "void", "nint", "nuint", "dynamic",
}
TYPE_KEYWORDS = {"class", "struct", "interface", "enum", "record"}
NOT_A_TYPE = {
    "await", "return", "throw", "new", "yield", "this", "base", "typeof", "nameof", "default", "checked",
    "unchecked", "sizeof", "delegate", "stackalloc", "is", "as", "null", "true", "false", "ref", "out", "in",
}
CONTEXTUAL = {"and", "or", "not", "when", "with", "is", "as", "switch", "in"}


class LintError(Exception):
    """A usage, environment or parse error: exit code 2."""


class ParseError(Exception):
    pass


# ---- lexer -------------------------------------------------------------------------------------------------------

class Tok:
    __slots__ = ("kind", "text", "start", "end", "holes")

    def __init__(self, kind, text, start, end, holes=None):
        self.kind, self.text, self.start, self.end, self.holes = kind, text, start, end, holes


OPS = ("<<=", "??=", "=>", "==", "!=", "<=", ">=", "&&", "||", "++", "--", "+=", "-=", "*=", "/=", "%=", "&=", "|=",
       "^=", "<<", "->", "::", "..", "??", "?.")
_IDENT = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")
_NUMBER = re.compile(r"0[xX][0-9a-fA-F_]+[uUlL]*|0[bB][01_]+[uUlL]*|\d[\d_]*(?:\.\d[\d_]*)?(?:[eE][+-]?\d+)?[a-zA-Z]*")


class Lexer:
    def __init__(self, src: str, path: str):
        self.src, self.path = src, path
        self.newlines = [i for i, c in enumerate(src) if c == "\n"]

    def line(self, offset: int) -> int:
        return bisect.bisect_right(self.newlines, offset - 1) + 1

    def fail(self, offset: int, message: str):
        raise LintError(f"{self.path}:{self.line(offset)}: cannot lex: {message}")

    def lex(self, pos: int = 0, hole: bool = False):
        """Tokens from `pos`; inside an interpolation hole, stops at the depth-0 `}` or at the `:`/`,` that starts its
        format or alignment. Returns (tokens, stop offset)."""
        src, n, toks, depth = self.src, len(self.src), [], 0
        while pos < n:
            c = src[pos]
            if c in " \t\r\n\f\v":
                pos += 1
            elif src.startswith("//", pos):
                end = src.find("\n", pos)
                pos = n if end < 0 else end
            elif src.startswith("/*", pos):
                end = src.find("*/", pos + 2)
                if end < 0:
                    self.fail(pos, "unterminated comment")
                pos = end + 2
            elif c == "#" and src[src.rfind("\n", 0, pos) + 1:pos].strip() == "":
                end = src.find("\n", pos)
                pos = n if end < 0 else end
            elif hole and depth == 0 and (c in "}," or (c == ":" and not src.startswith("::", pos))):
                return toks, pos
            elif c in "\"'$" or (c == "@" and src[pos + 1:pos + 2] in ("\"", "$")):
                tok, pos = self.lex_string(pos)
                toks.append(tok)
            elif c == "@" and _IDENT.match(src, pos + 1):
                m = _IDENT.match(src, pos + 1)
                toks.append(Tok("id", m.group(0), pos, m.end()))
                pos = m.end()
            elif _IDENT.match(src, pos):
                m = _IDENT.match(src, pos)
                toks.append(Tok("id", m.group(0), pos, m.end()))
                pos = m.end()
            elif c.isdigit():
                m = _NUMBER.match(src, pos)
                toks.append(Tok("num", m.group(0), pos, m.end()))
                pos = m.end()
            else:
                op = next((o for o in OPS if src.startswith(o, pos)), c)
                if op in ("(", "[", "{"):
                    depth += 1
                elif op in (")", "]", "}"):
                    depth -= 1
                toks.append(Tok("op", op, pos, pos + len(op)))
                pos += len(op)
        if hole:
            self.fail(pos, "unterminated interpolation hole")
        return toks, pos

    def hole(self, start: int, braces: int, holes: list):
        toks, stop = self.lex(start, hole=True)
        holes.append(Hole(toks, start, stop))
        close = self.src.find("}" * braces, stop)
        if close < 0:
            self.fail(start, "unterminated interpolation hole")
        return close + braces

    def lex_string(self, pos: int):
        src, n, start = self.src, len(self.src), pos
        dollars, verbatim = 0, False
        while pos < n and src[pos] in "$@":
            dollars += src[pos] == "$"
            verbatim |= src[pos] == "@"
            pos += 1
        if pos >= n or src[pos] not in "\"'":
            self.fail(start, "a string prefix with no string")
        if src[pos] == "'":
            pos += 1
            while pos < n and src[pos] != "'":
                pos += 2 if src[pos] == "\\" else 1
            return Tok("chr", src[start:pos + 1], start, pos + 1), pos + 1
        quotes = 0
        while src[pos + quotes:pos + quotes + 1] == '"':
            quotes += 1
        holes: list[Hole] = []
        if not verbatim and quotes >= 3:  # raw string literal
            j = pos + quotes
            while True:
                if j >= n:
                    self.fail(start, "unterminated raw string")
                if src.startswith('"' * quotes, j):
                    j += quotes
                    break
                if dollars and src[j] == "{":
                    run = len(src[j:]) - len(src[j:].lstrip("{"))
                    j = self.hole(j + run, dollars, holes) if run >= dollars else j + run
                    continue
                j += 1
        else:
            j = pos + 1
            while True:
                if j >= n:
                    self.fail(start, "unterminated string")
                ch = src[j]
                if ch == "\\" and not verbatim:
                    j += 2
                elif ch == '"':
                    if verbatim and src[j + 1:j + 2] == '"':
                        j += 2
                        continue
                    j += 1
                    break
                elif dollars and ch == "{":
                    j = j + 2 if src[j + 1:j + 2] == "{" else self.hole(j + 1, 1, holes)
                elif dollars and ch == "}":
                    j += 2 if src[j + 1:j + 2] == "}" else 1
                else:
                    j += 1
        if src[j:j + 2] in ("u8", "U8"):
            j += 2
        return Tok("interp" if dollars else "str", src[start:j], start, j, holes), j


class Hole:
    __slots__ = ("tokens", "start", "end")

    def __init__(self, tokens, start, end):
        self.tokens, self.start, self.end = tokens, start, end


# ---- expressions -------------------------------------------------------------------------------------------------

class Node:
    """One expression; `kind` names the shape, the other attributes depend on it."""

    def __init__(self, kind, start, end, **fields):
        self.kind, self.start, self.end = kind, start, end
        self.__dict__.update(fields)


class Arg:
    __slots__ = ("name", "ref", "expr")

    def __init__(self, name, ref, expr):
        self.name, self.ref, self.expr = name, ref, expr


class Pattern:
    """A pattern: its designations as (name, access path from the subject) and its constant expressions."""

    def __init__(self):
        self.designations: list[tuple[str, list]] = []
        self.exprs: list[Node] = []


def matching(tokens) -> dict[int, int]:
    pairs, stack = {}, []
    for i, t in enumerate(tokens):
        if t.kind != "op":
            continue
        if t.text in ("(", "[", "{"):
            stack.append(i)
        elif t.text in (")", "]", "}"):
            if not stack or tokens[stack[-1]].text + t.text not in ("()", "[]", "{}"):
                raise ParseError(f"unbalanced '{t.text}'")
            j = stack.pop()
            pairs[j], pairs[i] = i, j
    if stack:
        raise ParseError(f"unclosed '{tokens[stack[-1]].text}'")
    return pairs


class ExprParser:
    """A precedence-climbing parser for C# expressions over tokens[lo:hi]."""

    LEVELS = [("??",), ("||",), ("&&",), ("|",), ("^",), ("&",), ("==", "!="), ("<", ">", "<=", ">=", "is", "as"),
              ("<<", ">>"), ("+", "-"), ("*", "/", "%")]

    def __init__(self, tokens, match, lo, hi):
        self.t, self.m, self.i, self.hi = tokens, match, lo, hi

    def sub(self, lo, hi):
        return ExprParser(self.t, self.m, lo, hi)

    def peek(self, k=0):
        j = self.i + k
        return self.t[j] if j < self.hi else None

    def at(self, text, k=0):
        tok = self.peek(k)
        return tok is not None and tok.kind in ("op", "id") and tok.text == text

    def at_id(self, k=0):
        tok = self.peek(k)
        return tok is not None and tok.kind == "id"

    def take(self, text=None):
        tok = self.peek()
        if tok is None or (text is not None and tok.text != text):
            raise ParseError(f"expected '{text}'" if text else "unexpected end of expression")
        self.i += 1
        return tok

    def close(self):
        return self.m[self.i]

    # -- types and type arguments
    def skip_type_args(self, j):
        """`<` at j: the index of its `>` when what lies between can be type arguments, else None."""
        depth, k = 0, j
        while k < self.hi:
            tok = self.t[k]
            if tok.kind == "op" and tok.text == "<":
                depth += 1
            elif tok.kind == "op" and tok.text == ">":
                depth -= 1
                if depth == 0:
                    return k
            elif tok.kind == "op" and tok.text in ("(", "["):
                k = self.m[k]
            elif tok.kind != "id" and not (tok.kind == "op" and tok.text in (",", ".", "?", "::")):
                return None
            k += 1
        return None

    def generic_args_follow(self, j):
        """C# 6.2.5: a `<...>` after a name is a type-argument list when the next token disambiguates it."""
        close = self.skip_type_args(j)
        if close is None:
            return None
        nxt = self.t[close + 1] if close + 1 < self.hi else None
        if nxt is None or nxt.text in ("(", ")", "]", "}", ":", ";", ",", ".", "?", "==", "!=", "|", "^", "&&", "||",
                                       "&", "[", "?.", "=>"):
            return close
        return None

    def try_type(self, j):
        """The end index of a type starting at j, or None. Never moves the cursor."""
        t, hi = self.t, self.hi
        if j >= hi:
            return None
        tok = t[j]
        if tok.kind == "op" and tok.text == "(":
            j = self.m[j] + 1
        elif tok.kind == "id" and tok.text not in NOT_A_TYPE:
            j += 1
            while True:
                if j < hi and t[j].kind == "op" and t[j].text == "<":
                    close = self.skip_type_args(j)
                    if close is None:
                        break
                    j = close + 1
                if j + 1 < hi and t[j].text in (".", "::") and t[j + 1].kind == "id":
                    j += 2
                    continue
                break
        else:
            return None
        while j < hi and t[j].kind == "op":
            if t[j].text == "?" and self.nullable_mark(j):
                j += 1
            elif t[j].text == "[" and all(x.text == "," for x in t[j + 1:self.m[j]]):
                j = self.m[j] + 1
            else:
                break
        return j

    def nullable_mark(self, j):
        """Is the `?` at j a nullable-type mark rather than the conditional operator?"""
        nxt = self.t[j + 1] if j + 1 < self.hi else None
        if nxt is None or nxt.text in (")", ",", ">", "]", ";", "=", "{", "[", "?"):
            return True
        if nxt.kind == "id" and nxt.text not in NOT_A_TYPE:
            after = self.t[j + 2] if j + 2 < self.hi else None
            return after is None or after.text in ("=", ";", ",", ")", "=>", "in")
        return False

    # -- expressions
    def parse_all(self):
        e = self.expr()
        if self.i != self.hi:
            raise ParseError(f"unexpected '{self.t[self.i].text}'")
        return e

    def expr(self):
        lam = self.try_lambda()
        if lam is not None:
            return lam
        left = self.conditional()
        tok = self.peek()
        if tok is not None and tok.kind == "op" and tok.text in ASSIGN_OPS:
            self.i += 1
            value = self.expr()
            return Node("assign", left.start, value.end, op=tok.text, target=left, value=value)
        return left

    def try_lambda(self):
        j = self.i
        while j < self.hi and self.t[j].kind == "id" and self.t[j].text in ("static", "async"):
            j += 1
        if j >= self.hi:
            return None
        tok, params = self.t[j], None
        if tok.kind == "id" and j + 1 < self.hi and self.t[j + 1].text == "=>":
            params, after = [tok.text], j + 2
        elif tok.kind == "op" and tok.text == "(":
            end = self.m[j]
            if end + 1 < self.hi and self.t[end + 1].text == "=>":
                params = []
                for a, b in self.split_commas(j + 1, end):
                    ids = [x.text for x in self.t[a:b] if x.kind == "id" and x.text not in ("ref", "out", "in")]
                    if ids:
                        params.append(ids[-1])
                after = end + 2
        if params is None:
            return None
        start = self.t[self.i].start
        self.i = after
        if self.at("{"):
            lo, hi = self.i, self.close()
            self.i = hi + 1
            return Node("lambda", start, self.t[hi].end, params=params, block=(lo + 1, hi), body=None)
        body = self.expr()
        return Node("lambda", start, body.end, params=params, block=None, body=body)

    def conditional(self):
        cond = self.binary(0)
        if self.at("?"):
            self.i += 1
            then = self.expr()
            self.take(":")
            other = self.expr()
            return Node("cond", cond.start, other.end, test=cond, then=then, other=other)
        return cond

    def binary(self, level):
        if level == len(self.LEVELS):
            return self.switch_level()
        ops = self.LEVELS[level]
        left = self.binary(level + 1)
        while True:
            tok = self.peek()
            if tok is None:
                return left
            text = tok.text
            if (">>" in ops and text == ">" and tok.kind == "op" and self.at(">", 1)
                    and self.peek(1).start == tok.end):
                self.i += 2
                right = self.binary(level + 1)
                left = Node("binary", left.start, right.end, op=">>", left=left, right=right)
                continue
            if text not in ops or (tok.kind == "id" and text not in ("is", "as")) or tok.kind not in ("op", "id"):
                return left
            self.i += 1
            if text == "is":
                pattern = self.pattern()
                left = Node("is", left.start, self.t[self.i - 1].end, operand=left, pattern=pattern)
            elif text == "as":
                end = self.try_type(self.i)
                if end is None:
                    raise ParseError("expected a type after 'as'")
                self.i = end
                left = Node("as", left.start, self.t[end - 1].end, operand=left)
            elif text == "??":
                right = self.binary(level)  # right-associative
                return Node("binary", left.start, right.end, op=text, left=left, right=right)
            else:
                right = self.binary(level + 1)
                left = Node("binary", left.start, right.end, op=text, left=left, right=right)

    def switch_level(self):
        e = self.range_level()
        while self.at("{", 1) and (self.at("switch") or self.at("with")):
            keyword = self.take().text
            lo, hi = self.i, self.close()
            self.i = hi + 1
            if keyword == "switch":
                e = Node("switch", e.start, self.t[hi].end, subject=e, arms=self.switch_arms(lo + 1, hi))
            else:
                e = Node("with", e.start, self.t[hi].end, target=e, init=self.initializer(lo + 1, hi))
        return e

    def switch_arms(self, lo, hi):
        arms = []
        for a, b in self.split_commas(lo, hi):
            p = self.sub(a, b)
            pattern = p.pattern()
            guard = None
            if p.at("when"):
                p.i += 1
                guard = p.binary(0)
            p.take("=>")
            arms.append((pattern, guard, p.parse_all()))
        return arms

    def split_commas(self, lo, hi, types=False):
        """Top-level comma-separated ranges of tokens[lo:hi]; `types` for a declaration list, where every `<...>` that
        reads as type arguments is skipped whatever follows it."""
        parts, start, k = [], lo, lo
        while k < hi:
            tok = self.t[k]
            if tok.kind == "op" and tok.text in ("(", "[", "{"):
                k = self.m[k] + 1
                continue
            if tok.kind == "op" and tok.text == ",":
                parts.append((start, k))
                start = k + 1
            elif tok.kind == "op" and tok.text == "<" and k > lo and self.t[k - 1].kind == "id":
                saved = self.hi
                self.hi = hi
                close = self.skip_type_args(k) if types else self.generic_args_follow(k)
                self.hi = saved
                if close is not None:
                    k = close + 1
                    continue
            k += 1
        if start < hi:
            parts.append((start, hi))
        return parts

    def range_level(self):
        if self.at(".."):
            tok = self.take()
            if self.peek() is None or self.peek().text in (")", "]", ","):
                return Node("range", tok.start, tok.end, left=None, right=None)
            right = self.unary()
            return Node("range", tok.start, right.end, left=None, right=right)
        left = self.unary()
        if self.at(".."):
            tok = self.take()
            if self.peek() is None or self.peek().text in (")", "]", ","):
                return Node("range", left.start, tok.end, left=left, right=None)
            right = self.unary()
            return Node("range", left.start, right.end, left=left, right=right)
        return left

    def unary(self):
        tok = self.peek()
        if tok is None:
            raise ParseError("unexpected end of expression")
        if tok.kind == "op" and tok.text in ("!", "-", "+", "~", "++", "--", "^", "&", "*"):
            self.i += 1
            operand = self.unary()
            return Node("unary", tok.start, operand.end, op=tok.text, operand=operand)
        if tok.kind == "id" and tok.text == "await" and self.peek(1) is not None:
            self.i += 1
            operand = self.unary()
            return Node("unary", tok.start, operand.end, op="await", operand=operand)
        if tok.kind == "op" and tok.text == "(":
            cast = self.try_cast()
            if cast is not None:
                return cast
        return self.postfix(self.primary())

    def try_cast(self):
        lo, hi = self.i, self.m[self.i]
        if self.try_type(lo + 1) != hi or hi + 1 >= self.hi:
            return None
        nxt = self.t[hi + 1]
        operand_start = nxt.kind in ("num", "str", "chr", "interp") or nxt.text in ("(", "~", "!") or (
            nxt.kind == "id" and nxt.text not in ("is", "as", "switch", "with", "and", "or", "when"))
        predefined = self.t[lo + 1].text in PREDEFINED
        if not (operand_start or (predefined and nxt.text in ("-", "+"))):
            return None
        self.i = hi + 1
        operand = self.unary()
        return Node("cast", self.t[lo].start, operand.end, operand=operand)

    def primary(self):
        tok = self.take()
        k, text = tok.kind, tok.text
        if k in ("str", "chr", "num"):
            return Node("lit", tok.start, tok.end, lit=k)
        if k == "interp":
            holes = []
            for hole in tok.holes:
                p = ExprParser(hole.tokens, matching(hole.tokens), 0, len(hole.tokens))
                holes.append(p.parse_all())
            return Node("interp", tok.start, tok.end, holes=holes)
        if k == "op":
            if text == "(":
                return self.paren_or_tuple(tok)
            if text == "[":
                hi = self.m[self.i - 1]
                elements = []
                for a, b in self.split_commas(self.i, hi):
                    p = self.sub(a, b)
                    spread = p.at("..")
                    p.i += spread
                    elements.append((spread, p.parse_all()))
                self.i = hi + 1
                return Node("coll", tok.start, self.t[hi].end, elements=elements)
            raise ParseError(f"unexpected '{text}'")
        if text in ("true", "false"):
            return Node("lit", tok.start, tok.end, lit="bool")
        if text == "null":
            return Node("lit", tok.start, tok.end, lit="null")
        if text in ("default", "typeof", "sizeof"):
            if self.at("("):
                self.i = self.close() + 1
            return Node("lit", tok.start, self.t[self.i - 1].end, lit=text)
        if text in ("checked", "unchecked") and self.at("("):
            hi = self.close()
            inner = self.sub(self.i + 1, hi).parse_all()
            self.i = hi + 1
            return Node("paren", tok.start, self.t[hi].end, inner=inner)
        if text == "throw":
            operand = self.expr()
            return Node("throw", tok.start, operand.end, operand=operand)
        if text == "new":
            return self.new_expr(tok)
        if text == "delegate":
            if self.at("("):
                self.i = self.close() + 1
            if not self.at("{"):
                raise ParseError("expected an anonymous-method body")
            lo, hi = self.i, self.close()
            self.i = hi + 1
            return Node("lambda", tok.start, self.t[hi].end, params=[], block=(lo + 1, hi), body=None)
        if text in ("ref", "out", "in") and self.peek() is not None:
            return self.primary()
        if text == "var" and self.at("("):
            hi = self.close()
            names = [x.text for x in self.t[self.i + 1:hi] if x.kind == "id"]
            self.i = hi + 1
            return Node("decl", tok.start, self.t[hi].end, names=names)
        # a declaration expression `Type name` / `var name` (out arguments, tuple deconstruction)
        end = self.try_type(self.i - 1)
        if (end is not None and end < self.hi and self.t[end].kind == "id" and self.t[end].text not in CONTEXTUAL
                and (end + 1 >= self.hi or self.t[end + 1].text in (",", ")", "="))):
            self.i = end + 1
            return Node("decl", tok.start, self.t[end].end, names=[self.t[end].text])
        if self.at("<"):
            close = self.generic_args_follow(self.i)
            if close is not None:
                self.i = close + 1
        return Node("name", tok.start, self.t[self.i - 1].end, id=text)

    def paren_or_tuple(self, tok):
        lo, hi = self.i, self.m[self.i - 1]
        parts = self.split_commas(lo, hi)
        self.i = hi + 1
        if len(parts) == 1:
            return Node("paren", tok.start, self.t[hi].end, inner=self.sub(*parts[0]).parse_all())
        items = []
        for a, b in parts:
            p = self.sub(a, b)
            if p.at_id() and p.at(":", 1):
                p.i += 2
            items.append(p.parse_all())
        return Node("tuple", tok.start, self.t[hi].end, items=items)

    def new_expr(self, tok):
        type_text, args, init, array = "", [], [], False
        if self.at("["):
            self.i = self.close() + 1
            type_text, array = "[]", True
        elif self.at("{"):
            type_text = "<anonymous>"
        elif not self.at("("):
            end = self.try_type(self.i)
            if end is None:
                raise ParseError("expected a type after 'new'")
            type_text = " ".join(x.text for x in self.t[self.i:end])
            self.i = end
            while self.at("["):
                hi = self.close()
                args = self.args(self.i + 1, hi)
                self.i = hi + 1
                array = True
        if self.at("("):
            hi = self.close()
            args = self.args(self.i + 1, hi)
            self.i = hi + 1
        if self.at("{"):
            hi = self.close()
            init = self.initializer(self.i + 1, hi)
            self.i = hi + 1
        return Node("new", tok.start, self.t[self.i - 1].end, type=type_text, args=args, init=init, array=array)

    def initializer(self, lo, hi):
        """Object, collection, dictionary or anonymous initializer elements: (kind, key, value)."""
        elements = []
        for a, b in self.split_commas(lo, hi):
            p = self.sub(a, b)
            if p.at_id() and p.at("=", 1):
                name = p.take().text
                p.take("=")
                if p.at("{"):
                    close = p.close()
                    value = Node("init", p.t[p.i].start, p.t[close].end, elements=p.initializer(p.i + 1, close))
                    p.i = close + 1
                    elements.append(("member-init", name, value))
                else:
                    elements.append(("member", name, p.expr()))
            elif p.at("["):
                close = p.close()
                key = p.sub(p.i + 1, close).parse_all()
                p.i = close + 1
                p.take("=")
                elements.append(("index", key, p.expr()))
            elif p.at("{"):
                close = p.close()
                items = [p.sub(x, y).parse_all() for x, y in p.split_commas(p.i + 1, close)]
                elements.append(("element", None, Node("tuple", p.t[p.i].start, p.t[close].end, items=items)))
                p.i = close + 1
            else:
                elements.append(("element", None, p.expr()))
            if p.i != b:
                raise ParseError(f"unexpected '{p.t[p.i].text}' in an initializer")
        return elements

    def args(self, lo, hi):
        result = []
        for a, b in self.split_commas(lo, hi):
            p = self.sub(a, b)
            name = ref = None
            if p.at_id() and p.at(":", 1):
                name = p.take().text
                p.i += 1
            if p.at_id() and p.peek().text in ("ref", "out", "in") and p.peek(1) is not None:
                ref = p.take().text
            if ref == "out" and p.at("_") and p.i + 1 == b:
                result.append(Arg(name, ref, Node("lit", p.t[p.i].start, p.t[p.i].end, lit="discard")))
                continue
            result.append(Arg(name, ref, p.parse_all()))
        return result

    def postfix(self, e):
        while True:
            tok = self.peek()
            if tok is None or tok.kind != "op":
                return e
            text = tok.text
            if text in (".", "?.", "->"):
                self.i += 1
                name = self.take()
                if name.kind != "id":
                    raise ParseError(f"expected a member name after '{text}'")
                if self.at("<"):
                    close = self.generic_args_follow(self.i)
                    if close is not None:
                        self.i = close + 1
                e = Node("member", e.start, self.t[self.i - 1].end, target=e, name=name.text)
            elif text == "::":
                self.i += 1
                name = self.take()
                e = Node("name", e.start, name.end, id=name.text)
            elif text == "(":
                hi = self.close()
                args = self.args(self.i + 1, hi)
                self.i = hi + 1
                e = Node("call", e.start, self.t[hi].end, target=e, args=args)
            elif text == "[" or (text == "?" and self.at("[", 1) and self.peek(1).start == tok.end):
                self.i += text == "?"
                hi = self.close()
                args = self.args(self.i + 1, hi)
                self.i = hi + 1
                e = Node("index", e.start, self.t[hi].end, target=e, args=args)
            elif text in ("++", "--"):
                self.i += 1
                e = Node("unary", e.start, tok.end, op="post" + text, operand=e)
            elif text == "!":
                self.i += 1
                e = Node("unary", e.start, tok.end, op="!!", operand=e)
            else:
                return e

    # -- patterns
    def pattern(self):
        p = Pattern()
        self.pat_or(p, [])
        return p

    def pat_or(self, p, path):
        self.pat_and(p, path)
        while self.at("or"):
            self.i += 1
            self.pat_and(p, path)

    def pat_and(self, p, path):
        self.pat_not(p, path)
        while self.at("and"):
            self.i += 1
            self.pat_not(p, path)

    def pat_not(self, p, path):
        if self.at("not"):
            self.i += 1
            self.pat_not(p, path)
        else:
            self.pat_primary(p, path)

    def designation(self, p, path):
        # `and`/`or`/`not` are combinators only when a pattern follows: `AndNode and => ...` declares a variable `and`.
        tok, nxt = self.peek(), self.peek(1)
        if tok is not None and tok.kind == "id" and tok.text not in ("when", "with", "is", "as", "switch", "in") and (
                nxt is None or nxt.text in (")", "]", "}", ",", "=>", ":", "&&", "||", "?", ";", "when", "and", "or")):
            self.i += 1
            if tok.text != "_":
                p.designations.append((tok.text, path))

    def pat_primary(self, p, path):
        tok = self.peek()
        if tok is None:
            raise ParseError("expected a pattern")
        if tok.kind == "op" and tok.text == "(":
            hi = self.close()
            parts = self.split_commas(self.i + 1, hi)
            for index, (a, b) in enumerate(parts):
                sub = self.sub(a, b)
                if sub.at_id() and sub.at(":", 1):
                    sub.i += 2
                sub.pat_or(p, path if len(parts) == 1 else path + [("pos", index)])
                if sub.i != b:
                    raise ParseError("unexpected token in a parenthesized pattern")
            self.i = hi + 1
            if self.at("{"):
                self.property_pattern(p, path)
            self.designation(p, path)
        elif tok.kind == "op" and tok.text == "{":
            self.property_pattern(p, path)
            self.designation(p, path)
        elif tok.kind == "op" and tok.text == "[":
            hi = self.close()
            for a, b in self.split_commas(self.i + 1, hi):
                sub = self.sub(a, b)
                sub.i += sub.at("..")
                if sub.i < b:
                    sub.pat_or(p, path + [("elem",)])
            self.i = hi + 1
            self.designation(p, path)
        elif tok.kind == "op" and tok.text in ("<", ">", "<=", ">="):
            self.i += 1
            p.exprs.append(self.binary(8))
        elif tok.kind == "id" and tok.text == "var":
            self.i += 1
            if self.at("("):
                self.i = self.close() + 1
            else:
                self.designation(p, path)
        else:
            end = self.try_type(self.i) if tok.kind == "id" else None
            if end is not None:
                self.i = end
                if self.at("("):
                    self.pat_primary(p, path)
                    return
                if self.at("{"):
                    self.property_pattern(p, path)
                self.designation(p, path)
            else:
                p.exprs.append(self.binary(8))

    def property_pattern(self, p, path):
        hi = self.close()
        for a, b in self.split_commas(self.i + 1, hi):
            sub = self.sub(a, b)
            names = []
            while sub.at_id() and (sub.at(".", 1) or sub.at(":", 1)):
                names.append(sub.take().text)
                colon = sub.at(":")
                sub.i += 1
                if colon:
                    break
            sub.pat_or(p, path + [("prop", ".".join(names))])
            if sub.i != b:
                raise ParseError("unexpected token in a property pattern")
        self.i = hi + 1


# ---- declarations and statements ---------------------------------------------------------------------------------

class Param:
    __slots__ = ("name", "type", "default", "mods")

    def __init__(self, name, type_text, default, mods):
        self.name, self.type, self.default, self.mods = name, type_text, default, mods


class Method:
    """A member with code: a method, constructor, property getter, local function, or a type's field initializers."""

    def __init__(self, file, type_chain, name, ret_type, params, kind):
        self.file, self.type_chain, self.name, self.ret_type, self.params = file, type_chain, name, ret_type, params
        self.kind = kind
        self.has_body = False
        self.returns: list[Node] = []
        self.roots: list[Node] = []
        self.locals: dict[str, list[tuple]] = {}
        self.local_types: dict[str, str] = {}
        self.param_writes: dict[str, list[Node]] = {}
        self.parent: Method | None = None

    @property
    def key(self):
        return ".".join(self.type_chain + [self.name])

    @property
    def is_primitive(self):
        return self.name in PRIMITIVES or self.name.startswith("Escape")

    def param_index(self, name):
        return next((i for i, p in enumerate(self.params) if p.name == name), None)


class TypeInfo:
    def __init__(self, name, chain):
        self.name, self.chain = name, chain
        self.consts: dict[str, str] = {}
        self.fields: dict[str, str] = {}
        self.primary: Method | None = None


class SourceFile:
    def __init__(self, rel: str, src: str):
        self.rel, self.src = rel, src
        self.profile = "value" if any(part in VALUE_PROFILE_DIRS for part in rel.split("/")) else "sql"
        self.lexer = Lexer(src, rel)
        self.token_starts: list[int] = []
        self.methods: list[Method] = []
        self.types: dict[str, TypeInfo] = {}
        self.enums: dict[str, set[str]] = {}
        self.parser: FileParser | None = None

    def line(self, offset):
        return self.lexer.line(offset)

    def text(self, node):
        """The expression as written, comments dropped and whitespace collapsed: the allowlist's key."""
        tokens = self.parser.t if self.parser is not None else []
        lo = bisect.bisect_left(self.token_starts, node.start)
        parts, previous = [], None
        for tok in tokens[lo:]:
            if tok.end > node.end:
                break
            if previous is not None and self.src[previous.end:tok.start]:
                parts.append(" ")
            parts.append(tok.text)
            previous = tok
        if not parts:  # inside an interpolation hole: no comment can sit there
            return re.sub(r"\s+", " ", self.src[node.start:node.end]).strip()
        return re.sub(r"\s+", " ", "".join(parts)).strip()


class FileParser:
    def __init__(self, file: SourceFile):
        self.file = file
        self.t, _ = file.lexer.lex()
        file.token_starts = [tok.start for tok in self.t]
        try:
            self.m = matching(self.t)
        except ParseError as error:
            raise LintError(f"{file.rel}: cannot parse: {error}") from error

    def fail(self, index, error):
        tok = self.t[min(index, len(self.t) - 1)] if self.t else None
        raise LintError(f"{self.file.rel}:{self.file.line(tok.start) if tok else 1}: cannot parse: {error}")

    def ep(self, lo, hi):
        return ExprParser(self.t, self.m, lo, hi)

    def expr(self, lo, hi):
        try:
            return self.ep(lo, hi).parse_all()
        except ParseError as error:
            self.fail(lo, error)

    def find(self, i, hi, text):
        while i < hi:
            if self.t[i].text == text:
                return i
            if self.t[i].kind == "op" and self.t[i].text in ("(", "[", "{"):
                i = self.m[i]
            i += 1
        self.fail(i, f"expected '{text}'")

    def skip_attributes(self, i, hi):
        while i < hi and self.t[i].kind == "op" and self.t[i].text == "[":
            i = self.m[i] + 1
        return i

    def parse(self):
        self.members(0, len(self.t), [], None)

    def members(self, lo, hi, chain, info: TypeInfo | None):
        t, i = self.t, lo
        while i < hi:
            i = self.skip_attributes(i, hi)
            if i >= hi:
                break
            if t[i].text == ";":
                i += 1
                continue
            if t[i].text == "using" and info is None:
                i = self.find(i, hi, ";") + 1
                continue
            if t[i].text == "namespace":
                j = i + 1
                while t[j].text not in (";", "{"):
                    j += 1
                if t[j].text == "{":
                    self.members(j + 1, self.m[j], chain, None)
                    j = self.m[j]
                i = j + 1
                continue
            j, mods = i, []
            while j < hi and t[j].kind == "id" and t[j].text in MODIFIERS and not (
                    t[j].text == "new" and t[j + 1].text in ("(", "[")):
                mods.append(t[j].text)
                j += 1
            if j < hi and t[j].text in TYPE_KEYWORDS:
                i = self.type_decl(j, hi, chain)
            elif j < hi and t[j].text in ("delegate", "event"):
                i = self.member_end(j, hi)
            elif info is None:
                self.fail(i, f"unexpected '{t[i].text}' outside a type")
            else:
                i = self.member(j, hi, info, mods)

    def member_end(self, i, hi):
        t = self.t
        while i < hi:
            if t[i].kind == "op" and t[i].text in ("=>", "="):
                return self.find(i, hi, ";") + 1
            if t[i].kind == "op" and t[i].text == ";":
                return i + 1
            if t[i].kind == "op" and t[i].text == "{":
                end = self.m[i] + 1
                return self.find(end, hi, ";") + 1 if end < hi and t[end].text == "=" else end
            if t[i].kind == "op" and t[i].text in ("(", "["):
                i = self.m[i]
            i += 1
        return hi

    def type_decl(self, j, hi, chain):
        t = self.t
        kind = t[j].text
        j += 1
        if kind == "record" and t[j].text in ("class", "struct"):
            j += 1
        name = t[j].text
        info = TypeInfo(name, chain + [name])
        self.file.types.setdefault(name, info)
        j += 1
        if j < hi and t[j].text == "<":
            close = self.ep(j, hi).skip_type_args(j)
            j = (close if close is not None else j) + 1
        if j < hi and t[j].text == "(":
            ctor = Method(self.file, info.chain, name, "", self.params(j + 1, self.m[j]), "ctor")
            self.file.methods.append(ctor)
            info.primary = ctor
            j = self.m[j] + 1
        while j < hi and t[j].text not in ("{", ";"):
            if t[j].kind == "op" and t[j].text in ("(", "["):
                j = self.m[j]
            j += 1
        if j >= hi or t[j].text == ";":
            return j + 1
        close = self.m[j]
        if kind == "enum":
            self.file.enums[name] = {t[k].text for k in range(j + 1, close)
                                     if t[k].kind == "id" and t[k - 1].text in ("{", ",")}
        else:
            self.members(j + 1, close, info.chain, info)
        return close + 1

    def params(self, lo, hi):
        result = []
        for a, b in self.ep(lo, hi).split_commas(lo, hi, types=True):
            a = self.skip_attributes(a, b)
            mods = []
            while a < b and self.t[a].text in ("this", "ref", "out", "in", "params", "scoped", "readonly"):
                mods.append(self.t[a].text)
                a += 1
            eq = next((k for k in range(a, b) if self.t[k].kind == "op" and self.t[k].text == "="), None)
            decl_end = b if eq is None else eq
            if decl_end - a < 2:
                self.fail(a, "cannot read a parameter")
            default = None if eq is None else self.expr(eq + 1, b)
            result.append(Param(self.t[decl_end - 1].text, " ".join(x.text for x in self.t[a:decl_end - 1]), default,
                                mods))
        return result

    def member(self, start, hi, info: TypeInfo, mods):
        t, chain = self.t, info.chain
        k = start
        if t[k].kind == "op" and t[k].text == "(":  # a tuple return type
            k = self.m[k] + 1
        while k < hi:
            tok = t[k]
            if tok.kind == "op" and tok.text in ("(", "{", "=>", "=", ";"):
                break
            if tok.kind == "op" and tok.text == "[":
                if t[k - 1].text == "this":
                    break
                k = self.m[k]
            elif tok.kind == "op" and tok.text == "<":
                close = self.ep(k, hi).skip_type_args(k)
                k = close if close is not None else k
            k += 1
        if k >= hi:
            self.fail(start, "cannot read a member")
        stop = t[k].text
        if stop == "[":  # an indexer
            method = Method(self.file, chain, "this[]", self.text(start, k - 1), self.params(k + 1, self.m[k]),
                            "indexer")
            return self.accessors(self.m[k] + 1, hi, method)
        if stop == "(":
            name_index = k - 1
            if t[name_index].text == ">":
                depth = 0
                while name_index > start:
                    depth += {">": 1, "<": -1}.get(t[name_index].text, 0)
                    name_index -= 1
                    if depth == 0:
                        break
            name = t[name_index].text
            ret_type = self.text(start, name_index)
            method = Method(self.file, chain, name, ret_type, self.params(k + 1, self.m[k]),
                            "ctor" if name == info.name and not ret_type else "method")
            self.file.methods.append(method)
            j = self.m[k] + 1
            if method.kind == "ctor" and j < hi and t[j].text == ":" and t[j + 2].text == "(":
                # `: base(...)` / `: this(...)`: scanned like the body, except that an exception type's
                # initializer only forwards its message.
                if not info.name.endswith("Exception"):
                    method.roots.append(self.expr(j + 1, self.m[j + 2] + 1))
                    method.has_body = True
                j = self.m[j + 2] + 1
            while j < hi and t[j].text not in ("{", "=>", ";"):
                if t[j].kind == "op" and t[j].text in ("(", "["):
                    j = self.m[j]
                j += 1
            return self.body(j, hi, method)
        if stop in ("{", "=>"):
            method = Method(self.file, chain, t[k - 1].text, self.text(start, k - 1), [], "property")
            if stop == "{":
                return self.accessors(k, hi, method)
            self.file.methods.append(method)
            return self.body(k, hi, method)
        # fields and constants: Type name [= expr] [, name [= expr]] ;
        end = self.find(k, hi, ";")
        type_end = self.ep(start, end).try_type(start)
        if type_end is None:
            self.fail(start, "cannot read a field")
        type_text = self.text(start, type_end)
        init = Method(self.file, chain, "<field>", type_text, [], "field")
        self.file.methods.append(init)
        for a, b in self.ep(type_end, end).split_commas(type_end, end):
            name = t[a].text
            (info.consts if "const" in mods else info.fields)[name] = type_text
            if b > a + 1:
                init.roots.append(self.expr(a + 2, b))
                init.has_body = True
        return end + 1

    def text(self, lo, hi):
        return " ".join(x.text for x in self.t[lo:hi])

    def accessors(self, k, hi, method: Method):
        t = self.t
        self.file.methods.append(method)
        close = self.m[k]
        j = k + 1
        while j < close:
            j = self.skip_attributes(j, close)
            while j < close and t[j].text in MODIFIERS:
                j += 1
            if j >= close:
                break
            accessor = t[j].text
            j += 1
            if t[j].text == ";":
                j += 1
                continue
            target = method
            if accessor != "get":
                target = Method(self.file, method.type_chain, method.name + "." + accessor, "void",
                                [Param("value", method.ret_type, None, [])], "setter")
                self.file.methods.append(target)
            j = self.body(j, close, target)
        end = close + 1
        if end < hi and t[end].text == "=":
            semi = self.find(end, hi, ";")
            init = Method(self.file, method.type_chain, "<field>", method.ret_type, [], "field")
            self.file.methods.append(init)
            init.roots.append(self.expr(end + 1, semi))
            init.has_body = True
            return semi + 1
        return end

    def body(self, j, hi, method: Method):
        t = self.t
        if j >= hi or t[j].text == ";":
            return j + 1
        method.has_body = True
        if t[j].text == "=>":
            semi = self.find(j, hi, ";")
            expr = self.expr(j + 1, semi)
            method.returns.append(expr)
            method.roots.append(expr)
            return semi + 1
        if t[j].text != "{":
            self.fail(j, "expected a member body")
        StatementParser(self, method).block(j + 1, self.m[j], method.returns)
        return self.m[j] + 1


class StatementParser:
    """Reads a block's statements, recording into its Method every expression (roots), return and local declaration."""

    def __init__(self, fp: FileParser, method: Method):
        self.fp, self.t, self.m, self.method = fp, fp.t, fp.m, method

    def root(self, node):
        self.method.roots.append(node)
        return node

    def expr(self, lo, hi):
        return self.root(self.fp.expr(lo, hi))

    def local(self, name, source):
        if name != "_":
            self.method.locals.setdefault(name, []).append(source)

    def block(self, lo, hi, returns):
        i = lo
        while i < hi:
            i = self.statement(i, hi, returns)

    def statement(self, i, hi, returns):
        t, fp = self.t, self.fp
        tok = t[i]
        text = tok.text
        if tok.kind == "op" and text == "{":
            self.block(i + 1, self.m[i], returns)
            return self.m[i] + 1
        if tok.kind == "op" and text == ";":
            return i + 1
        if tok.kind == "id":
            nxt = t[i + 1].text if i + 1 < hi else ""
            if text == "await" and nxt in ("foreach", "using"):
                return self.statement(i + 1, hi, returns)
            if text == "if":
                close = self.m[i + 1]
                self.expr(i + 2, close)
                j = self.statement(close + 1, hi, returns)
                return self.statement(j + 1, hi, returns) if j < hi and t[j].text == "else" else j
            if text in ("while", "lock", "fixed") and nxt == "(":
                close = self.m[i + 1]
                self.expr(i + 2, close)
                return self.statement(close + 1, hi, returns)
            if text == "do":
                j = self.statement(i + 1, hi, returns)
                close = self.m[j + 1]
                self.expr(j + 2, close)
                return close + 2
            if text == "for":
                return self.for_statement(i, hi, returns)
            if text == "foreach":
                return self.foreach_statement(i, hi, returns)
            if text == "switch" and nxt == "(":
                return self.switch_statement(i, hi, returns)
            if text == "try":
                return self.try_statement(i, hi, returns)
            if text == "using" and nxt == "(":
                close = self.m[i + 1]
                if not self.declaration(i + 2, close):
                    self.expr(i + 2, close)
                return self.statement(close + 1, hi, returns)
            if text in ("using", "const"):
                end = fp.find(i, hi, ";")
                if not self.declaration(i + 1, end):
                    fp.fail(i, f"cannot read a '{text}' declaration")
                return end + 1
            if text in ("checked", "unchecked", "unsafe") and nxt == "{":
                return self.statement(i + 1, hi, returns)
            if text in ("return", "throw"):
                end = fp.find(i, hi, ";")
                if end > i + 1:
                    node = self.expr(i + 1, end)
                    if text == "return":
                        returns.append(node)
                return end + 1
            if text == "yield":
                end = fp.find(i, hi, ";")
                if nxt == "return":
                    node = self.expr(i + 2, end)
                    returns.append(Node("yield", node.start, node.end, operand=node))
                return end + 1
            if text in ("break", "continue", "goto"):
                return fp.find(i, hi, ";") + 1
            if nxt == ":" and t[i + 1].kind == "op":
                return i + 2  # a label
            local_function = self.local_function(i, hi)
            if local_function is not None:
                return local_function
        end = fp.find(i, hi, ";")
        if not self.declaration(i, end):
            self.expr(i, end)
        return end + 1

    def for_statement(self, i, hi, returns):
        t = self.t
        close = self.m[i + 1]
        parts, start, k = [], i + 2, i + 2
        while k < close:
            if t[k].kind == "op" and t[k].text in ("(", "[", "{"):
                k = self.m[k] + 1
                continue
            if t[k].text == ";":
                parts.append((start, k))
                start = k + 1
            k += 1
        parts.append((start, close))
        if len(parts) != 3:
            self.fp.fail(i, "cannot read a for statement")
        (a, b), (c, d), (e, f) = parts
        if b > a and not self.declaration(a, b):
            for x, y in self.fp.ep(a, b).split_commas(a, b):
                self.expr(x, y)
        if d > c:
            self.expr(c, d)
        if f > e:
            for x, y in self.fp.ep(e, f).split_commas(e, f):
                self.expr(x, y)
        return self.statement(close + 1, hi, returns)

    def foreach_statement(self, i, hi, returns):
        t = self.t
        close = self.m[i + 1]
        k = i + 2
        while k < close and not (t[k].kind == "id" and t[k].text == "in"):
            if t[k].kind == "op" and t[k].text in ("(", "[", "{"):
                k = self.m[k]
            k += 1
        collection = self.expr(k + 1, close)
        if t[k - 1].text == ")":
            for x in t[i + 2:k]:
                if x.kind == "id" and x.text != "var":
                    self.local(x.text, ("unknown", "a deconstructing foreach variable"))
        else:
            name = t[k - 1].text
            self.local(name, ("elem", collection))
            self.method.local_types[name] = self.fp.text(i + 2, k - 1)
        return self.statement(close + 1, hi, returns)

    def switch_statement(self, i, hi, returns):
        t = self.t
        close = self.m[i + 1]
        subject = self.expr(i + 2, close)
        body_close = self.m[close + 1]
        j = close + 2
        while j < body_close:
            if t[j].text == "case":
                colon = j + 1
                while colon < body_close and not (t[colon].kind == "op" and t[colon].text == ":"):
                    if t[colon].kind == "op" and t[colon].text in ("(", "[", "{"):
                        colon = self.m[colon]
                    colon += 1
                p = self.fp.ep(j + 1, colon)
                try:
                    pattern = p.pattern()
                    if p.at("when"):
                        p.i += 1
                        self.root(p.binary(0))
                except ParseError as error:
                    self.fp.fail(j, error)
                for name, path in pattern.designations:
                    self.local(name, ("pattern", subject, tuple(path)))
                j = colon + 1
            elif t[j].text == "default" and t[j + 1].text == ":":
                j += 2
            else:
                j = self.statement(j, body_close, returns)
        return body_close + 1

    def try_statement(self, i, hi, returns):
        t = self.t
        j = self.statement(i + 1, hi, returns)
        while j < hi and t[j].text in ("catch", "finally"):
            j += 1
            if t[j - 1].text == "catch":
                if t[j].text == "(":
                    names = [x.text for x in t[j + 1:self.m[j]] if x.kind == "id"]
                    if len(names) >= 2:
                        self.local(names[-1], ("unknown", "a catch variable"))
                    j = self.m[j] + 1
                if t[j].text == "when":
                    self.expr(j + 2, self.m[j + 1])
                    j = self.m[j + 1] + 1
            j = self.statement(j, hi, returns)
        return j

    def local_function(self, i, hi):
        t = self.t
        j = i
        while j < hi and t[j].text in ("static", "async", "unsafe", "extern"):
            j += 1
        p = self.fp.ep(j, hi)
        type_end = p.try_type(j)
        if type_end is None or type_end >= hi or t[type_end].kind != "id":
            return None
        k = type_end + 1
        if k < hi and t[k].text == "<":
            close = p.skip_type_args(k)
            if close is None:
                return None
            k = close + 1
        if k >= hi or t[k].text != "(":
            return None
        after = self.m[k] + 1
        while after < hi and t[after].text not in ("{", "=>", ";"):
            after += 1
        if after >= hi or t[after].text not in ("{", "=>"):
            return None
        if after > self.m[k] + 1 and t[self.m[k] + 1].text != "where":
            return None
        method = Method(self.fp.file, self.method.type_chain, t[type_end].text, self.fp.text(j, type_end),
                        self.fp.params(k + 1, self.m[k]), "local function")
        method.parent = self.method
        self.fp.file.methods.append(method)
        return self.fp.body(after, hi, method)

    def declaration(self, lo, hi):
        """A local declaration over tokens[lo:hi]: records its locals and returns True, else returns False."""
        t = self.t
        j = lo
        while j < hi and t[j].text in ("ref", "readonly", "scoped", "const"):
            j += 1
        if j < hi and t[j].text == "var" and j + 1 < hi and t[j + 1].text == "(":
            close = self.m[j + 1]
            if close + 1 >= hi or t[close + 1].text != "=":
                return False
            value = self.expr(close + 2, hi)
            for index, name in enumerate(x.text for x in t[j + 2:close] if x.kind == "id"):
                self.local(name, ("decon", value, index))
            return True
        p = self.fp.ep(j, hi)
        type_end = p.try_type(j)
        if (type_end is None or type_end >= hi or type_end == j or t[type_end].kind != "id"
                or t[type_end].text in CONTEXTUAL or (type_end + 1 < hi and t[type_end + 1].text not in ("=", ","))):
            return False
        type_text = self.fp.text(j, type_end)
        for a, b in p.split_commas(type_end, hi):
            if t[a].kind != "id":
                return False
            name = t[a].text
            self.method.local_types[name] = type_text
            if b > a + 1:
                if t[a + 1].text != "=":
                    self.fp.fail(a, "cannot read a local declaration")
                value = self.expr(a + 2, b)
                self.local(name, ("expr", value))
                if type_text == "var":
                    self.method.local_types[name] = infer_type(value)
            else:
                self.local(name, ("none",))
        return True


def infer_type(node: Node) -> str:
    if node.kind == "interp" or (node.kind == "lit" and node.lit == "str"):
        return "string"
    if node.kind == "new":
        return node.type
    if node.kind == "call" and callee_name(node) in BUILDER_METHODS and builder_root(node) is not None:
        return "StringBuilder"
    if node.kind == "call" and callee_name(node) in PRIMITIVES + ("ToString",):
        return "string"
    return "var"


def norm_type(type_text) -> str:
    """A declared type's text without spaces or a trailing nullable mark: `string ?` -> `string`."""
    return (type_text or "").replace(" ", "").rstrip("?")


def simple_type(type_text) -> str:
    """The bare type name: `Craticula.Sql.Foo < T > ?` -> `Foo`."""
    return re.sub(r"<.*>", "", norm_type(type_text)).split(".")[-1]


def builder_root(node: Node):
    """The `new StringBuilder(...)` at the root of an Append chain, or None."""
    while node.kind == "call" and node.target.kind == "member" and node.target.name in BUILDER_METHODS:
        node = node.target.target
    return node if node.kind == "new" and simple_type(node.type) == "StringBuilder" else None


def callee_name(call: Node):
    target = call.target
    return target.id if target.kind == "name" else target.name if target.kind == "member" else None


def qualifier(node: Node):
    """The dotted text of a name or member chain (`a.b.c`), else None."""
    if node.kind == "name":
        return node.id
    if node.kind == "member":
        inner = qualifier(node.target)
        return None if inner is None else inner + "." + node.name
    return None


def children(node: Node):
    k = node.kind
    if k == "interp":
        return node.holes
    if k == "member":
        return [node.target]
    if k in ("call", "index"):
        return [node.target] + [a.expr for a in node.args]
    if k == "paren":
        return [node.inner]
    if k in ("unary", "cast", "as", "throw", "yield"):
        return [node.operand]
    if k == "binary":
        return [node.left, node.right]
    if k == "is":
        return [node.operand] + node.pattern.exprs
    if k == "cond":
        return [node.test, node.then, node.other]
    if k == "assign":
        return [node.target, node.value]
    if k == "lambda":
        return [node.body] if node.body is not None else []
    if k == "coll":
        return [e for _, e in node.elements]
    if k == "tuple":
        return node.items
    if k == "switch":
        out = [node.subject]
        for pattern, guard, value in node.arms:
            out += pattern.exprs + ([guard] if guard is not None else []) + [value]
        return out
    if k in ("with", "init", "new"):
        out = [node.target] if k == "with" else ([a.expr for a in node.args] if k == "new" else [])
        for kind, key, value in (node.elements if k == "init" else node.init):
            if kind == "index":
                out.append(key)
            out.append(value)
        return out
    if k == "range":
        return [x for x in (node.left, node.right) if x is not None]
    return []


def plus_operands(node: Node, out: list, chained: set):
    """Flattens a left-associative `a + b + c` chain; records the inner `+` nodes in `chained`."""
    if node.kind == "binary" and node.op == "+":
        chained.add(id(node))
        plus_operands(node.left, out, chained)
        out.append(node.right)
    else:
        out.append(node)


# ---- the analysis ------------------------------------------------------------------------------------------------

SCALAR = ("scalar",)
ELEMENTS = ("elements",)


class Finding:
    def __init__(self, file, line, member, expression, why):
        self.file, self.line, self.member, self.expression, self.why = file, line, member, expression, why

    def __str__(self):
        return f"{self.file}:{self.line}: SEC-2: {self.expression} — {self.why}"


class Analysis:
    def __init__(self, files: list[SourceFile], enums: dict[str, set[str]], allowlist: dict):
        self.files, self.enums, self.allowlist = files, enums, allowlist
        self.methods_by_name: dict[str, list[Method]] = {}
        self.types: dict[str, TypeInfo] = {}
        for f in files:
            for m in f.methods:
                self.methods_by_name.setdefault(m.name, []).append(m)
            for name, info in f.types.items():
                self.types.setdefault(name, info)
        self.calls: dict[str, list[tuple[Node, Method]]] = {}
        self.news: dict[str, list[tuple[Node, Method]]] = {}
        self.sinks: list[tuple[Node, tuple, Method, str]] = []
        self.chained: set[int] = set()
        self.queue: list[tuple] = []
        self.seen: set = set()
        self.text_params: set = set()
        self.text_methods: set = set()
        self.findings: dict[tuple, Finding] = {}
        self.allowlist_hits: dict[tuple, int] = {}
        self.constructions: dict[str, int] = {}
        self.resolutions: dict[str, int] = {}
        self.explained: list[tuple] = []

    def count(self, table, key):
        table[key] = table.get(key, 0) + 1

    # -- phase 1: collect sinks, call sites and every write to a local --------------------------------------------
    def collect(self):
        for f in self.files:
            for method in f.methods:
                index = 0
                while index < len(method.roots):  # lambda block bodies append roots while we walk
                    self.walk(method.roots[index], method, False)
                    index += 1
                # A primitive returning anything but a string (EF Core's Bind builds an Expression) yields no text.
                if (method.is_primitive and method.has_body and method.kind in ("method", "property")
                        and norm_type(method.ret_type) in STRING_TYPES):
                    for ret in method.returns:
                        if ret.kind != "yield":
                            self.sinks.append((ret, SCALAR, method, "return of a primitive"))

    def walk(self, node, method: Method, in_exception: bool):
        if node is None:
            return
        self.visit(node, method, in_exception)
        if node.kind == "new":
            in_exception = in_exception or simple_type(node.type).endswith("Exception") or (
                node.type == "" and simple_type(method.ret_type).endswith("Exception"))
        for child in children(node):
            self.walk(child, method, in_exception)

    def visit(self, node, method: Method, in_exception):
        k = node.kind
        if k == "call":
            name = callee_name(node)
            if name is not None:
                self.calls.setdefault(name, []).append((node, method))
            self.call_writes(node, method)
        elif k == "new":
            self.news.setdefault(simple_type(node.type), []).append((node, method))
        elif k == "assign":
            self.assignment(node, method)
        elif k == "is":
            for name, path in node.pattern.designations:
                method.locals.setdefault(name, []).append(("pattern", node.operand, tuple(path)))
        elif k == "switch":
            for pattern, _, _ in node.arms:
                for name, path in pattern.designations:
                    method.locals.setdefault(name, []).append(("pattern", node.subject, tuple(path)))
        elif k == "lambda":
            receiver = getattr(node, "element_source", None)
            for index, param in enumerate(node.params):
                source = ("elem", receiver) if index == 0 and receiver is not None else (
                    "unknown", "a lambda parameter")
                method.locals.setdefault(param, []).append(source)
            if node.block is not None:
                node.returns = []
                StatementParser(method.file.parser, method).block(node.block[0], node.block[1], node.returns)
        if in_exception:
            if k == "interp" or (k == "binary" and node.op == "+" and id(node) not in self.chained):
                self.count(self.constructions, "exception messages (skipped)")
                if k == "binary":
                    plus_operands(node, [], self.chained)
            return
        self.sink(node, method)

    def call_writes(self, call, method):
        name = callee_name(call)
        target = call.target
        receiver = target.target if target.kind == "member" else None
        for a in call.args:
            if a.ref == "out" and a.expr.kind == "decl":
                for n in a.expr.names:
                    method.locals.setdefault(n, []).append(("unknown", "an out variable"))
            if a.expr.kind == "lambda" and receiver is not None and name in LAMBDA_ELEMENT_METHODS:
                a.expr.element_source = receiver
        if receiver is None or receiver.kind != "name" or not self.is_local(receiver.id, method):
            return
        owner = self.local_owner(receiver.id, method)
        if name in ("Add", "Push", "Enqueue") and call.args:
            owner.locals.setdefault(receiver.id, []).append(("add", call.args[-1].expr))
        elif name == "Insert" and len(call.args) == 2 and not self.is_builder(receiver, method):
            owner.locals.setdefault(receiver.id, []).append(("add", call.args[1].expr))
        elif name == "AddRange" and call.args:
            owner.locals.setdefault(receiver.id, []).append(("expr", call.args[0].expr))

    def assignment(self, node, method):
        target, value = node.target, node.value
        if target.kind == "name":
            if self.is_local(target.id, method):
                self.local_owner(target.id, method).locals.setdefault(target.id, []).append(("expr", value))
            else:
                r = self.resolve(target.id, method)
                if r is not None and r[0] == "param":
                    r[1].param_writes.setdefault(target.id, []).append(value)
        elif target.kind == "index" and target.target.kind == "name" and self.is_local(target.target.id, method):
            self.local_owner(target.target.id, method).locals.setdefault(target.target.id, []).append(("add", value))
        elif target.kind in ("tuple", "decl"):
            items = target.items if target.kind == "tuple" else [Node("decl", 0, 0, names=[n]) for n in target.names]
            for index, item in enumerate(items):
                names = item.names if item.kind == "decl" else [item.id] if item.kind == "name" else []
                for n in names:
                    method.locals.setdefault(n, []).append(("decon", value, index))

    def local_owner(self, name, method):
        m = method
        while m is not None:
            if name in m.locals or name in m.local_types:
                return m
            m = m.parent
        return None

    def is_local(self, name, method):
        return self.local_owner(name, method) is not None

    # -- sinks
    def sink(self, node, method: Method):
        k = node.kind
        if k == "interp":
            self.count(self.constructions, "interpolated strings")
            for hole in node.holes:
                self.sinks.append((hole, SCALAR, method, "interpolation hole"))
        elif k == "binary" and node.op == "+" and id(node) not in self.chained:
            operands: list[Node] = []
            plus_operands(node, operands, self.chained)
            if any(self.stringy(o, method) for o in operands):
                self.count(self.constructions, "+ concatenations")
                for o in operands:
                    self.sinks.append((o, SCALAR, method, "concatenation operand"))
        elif k == "assign" and node.op == "+=" and node.target.kind == "name" and self.stringy(node.target, method):
            self.count(self.constructions, "+ concatenations")
            self.sinks.append((node.value, SCALAR, method, "concatenation operand"))
        elif k == "call":
            name = callee_name(node)
            q = qualifier(node.target.target) if node.target.kind == "member" else None
            if q in STRING_TYPES and name in ("Join", "Concat", "Format"):
                self.count(self.constructions, "string.Join/Concat/Format calls")
                args = [a for a in node.args if a.name is None]
                if name == "Join":
                    if args:
                        self.sinks.append((args[0].expr, SCALAR, method, "string.Join separator"))
                    for a in args[1:]:
                        self.sinks.append((a.expr, ELEMENTS if len(args) == 2 else SCALAR, method, "string.Join value"))
                else:
                    for a in args:
                        mode = ELEMENTS if name == "Concat" and len(args) == 1 else SCALAR
                        self.sinks.append((a.expr, mode, method, f"string.{name} argument"))
            elif name in BUILDER_METHODS and node.target.kind == "member" and self.is_builder(node.target.target,
                                                                                               method):
                self.count(self.constructions, "StringBuilder appends")
                for a in node.args[1:] if name == "Insert" else node.args:
                    self.sinks.append((a.expr, SCALAR, method, f"StringBuilder.{name} argument"))
        elif (k == "new" and simple_type(node.type) == "StringBuilder" and node.args
              and not self.numeric(node.args[0].expr, method)):
            self.count(self.constructions, "StringBuilder appends")
            self.sinks.append((node.args[0].expr, SCALAR, method, "StringBuilder initial text"))

    def numeric(self, node, method):
        """Is this a capacity-like integer expression (literal, .Length/.Count, int-typed name, arithmetic of those)?"""
        k = node.kind
        if k == "lit":
            return node.lit == "num"
        if k == "member":
            return node.name in ("Length", "Count")
        if k == "name":
            return norm_type(self.declared_type(node.id, method)) in NUMERIC_TYPES
        if k == "binary" and node.op in ("+", "-", "*", "/"):
            return self.numeric(node.left, method) and self.numeric(node.right, method)
        if k == "paren":
            return self.numeric(node.inner, method)
        return False

    def is_builder(self, receiver, method):
        while receiver.kind == "call" and receiver.target.kind == "member" and receiver.target.name in BUILDER_METHODS:
            receiver = receiver.target.target
        if receiver.kind == "new":
            return simple_type(receiver.type) == "StringBuilder"
        if receiver.kind == "name":
            return simple_type(self.declared_type(receiver.id, method)) == "StringBuilder"
        return False

    def stringy(self, node, method):
        k = node.kind
        if k == "interp" or (k == "lit" and node.lit in ("str", "chr")):
            return True
        if k == "paren":
            return self.stringy(node.inner, method)
        if k == "cond":
            return self.stringy(node.then, method) or self.stringy(node.other, method)
        if k == "call":
            name = callee_name(node)
            if name in STRING_METHODS or name in PRIMITIVES or (name or "").startswith("Escape"):
                return True
            q = qualifier(node.target.target) if node.target.kind == "member" else None
            if q in STRING_TYPES and name in ("Join", "Concat", "Format"):
                return True
            return any(norm_type(m.ret_type) in STRING_TYPES for m in self.methods_by_name.get(name, []))
        if k == "name":
            declared = self.declared_type(node.id, method)
            if declared is None:
                r = self.resolve(node.id, method)
                declared = self.types[r[1]].consts.get(node.id) if r is not None and r[0] == "const" else None
            return norm_type(declared) in STRING_TYPES
        if k == "member":
            q = qualifier(node.target)
            if (q, node.name) in BCL_SAFE_MEMBERS:
                return True
            info = self.types.get(q.split(".")[-1]) if q else None
            return info is not None and norm_type(info.consts.get(node.name)) in STRING_TYPES
        if k == "index":
            return self.stringy(node.target, method)
        return False

    # -- symbols
    def resolve(self, name, method):
        """('local', owner) | ('param', owner, index) | ('const', type name) | ('field', type name) | None."""
        m = method
        while m is not None:
            if name in m.locals or name in m.local_types:
                return ("local", m)
            index = m.param_index(name)
            if index is not None:
                return ("param", m, index)
            m = m.parent
        chain = list(method.type_chain)
        while chain:
            info = self.types.get(chain[-1])
            if info is not None:
                if name in info.consts:
                    return ("const", info.name)
                if info.primary is not None and info.primary.param_index(name) is not None:
                    return ("param", info.primary, info.primary.param_index(name))
                if name in info.fields:
                    return ("field", info.name)
            chain.pop()
        return None

    def declared_type(self, name, method):
        r = self.resolve(name, method)
        if r is None:
            return None
        if r[0] == "local":
            return r[1].local_types.get(name)
        if r[0] == "param":
            return r[1].params[r[2]].type
        return None

    def is_enum_type(self, type_text):
        last = simple_type(type_text)
        return last in self.enums or last in BCL_ENUMS

    # -- phase 2: check every sink operand, then everything that feeds one -------------------------------------
    def check_all(self):
        for node, mode, method, kind in self.sinks:
            category = self.position(node, mode, method, (method.file.rel, method.file.line(node.start)))
            self.count(self.resolutions, category)
            self.explained.append((method.file.rel, method.file.line(node.start), kind, method.file.text(node),
                                   category))
            while self.queue:
                self.position(*self.queue.pop())

    def position(self, node, mode, method: Method, origin):
        """Judges one text position and returns its top-level category; references it relies on are queued."""
        key = (id(node), mode)
        if key in self.seen:
            return "already checked"
        self.seen.add(key)
        f = method.file
        expression = f.text(node)
        akey = (f.rel, method.key, expression)
        if akey in self.allowlist:
            self.allowlist_hits[akey] = self.allowlist_hits.get(akey, 0) + 1
            return "allowlisted"
        category, why = self.check(node, mode, method, origin)
        if why is None:
            return category
        line = f.line(node.start)
        via = "" if origin == (f.rel, line) else f" (it reaches query text at {origin[0]}:{origin[1]})"
        self.findings.setdefault(akey, Finding(f.rel, line, method.key, expression, why + via))
        return "finding"

    def defer(self, node, mode, method, origin):
        self.queue.append((node, mode, method, origin))

    def check(self, node, mode, method: Method, origin):
        """(category, None) when `node` is safe in a text position, (category, why) when it is not."""
        k = node.kind
        text = method.file.text(node)
        if k == "lit":
            return "literal", None
        if k == "interp":
            return "composition", None  # its holes are sinks of their own
        if k == "paren":
            return self.check(node.inner, mode, method, origin)
        if k in ("cast", "as"):
            return self.check(node.operand, mode, method, origin)
        if k == "unary":
            if node.op == "!":
                return "bool", None
            return self.check(node.operand, mode, method, origin)
        if k == "throw":
            return "conditional", None
        if k in ("cond", "switch") or (k == "binary" and node.op == "??"):
            # each branch is a position of its own, so a finding names the arm that brings the value in
            branches = ([node.then, node.other] if k == "cond" else [node.left, node.right] if k == "binary"
                        else [value for _, _, value in node.arms])
            for branch in branches:
                self.defer(branch, mode, method, origin)
            return "conditional", None
        if k == "binary":
            if node.op in BOOL_OPS:
                return "bool", None
            if node.op == "+":
                operands: list[Node] = []
                plus_operands(node, operands, set())
                if any(self.stringy(o, method) for o in operands):
                    return "composition", None  # a concatenation: its operands are sinks of their own
            for side in (node.left, node.right):
                category, why = self.check(side, mode, method, origin)
                if why:
                    return category, why
            return "arithmetic", None
        if k == "is":
            return "bool", None
        if k == "range":
            for side in (node.left, node.right):
                if side is not None:
                    category, why = self.check(side, SCALAR, method, origin)
                    if why:
                        return category, why
            return "arithmetic", None
        if k == "assign":
            return self.check(node.value, mode, method, origin)
        if k == "tuple":
            if mode[0] == "component":
                if mode[1] < len(node.items):
                    self.defer(node.items[mode[1]], mode[2], method, origin)
                return "tuple component", None
            for item in node.items:
                self.defer(item, mode, method, origin)
            return "composition", None
        if k == "coll":
            for spread, element in node.elements:
                self.defer(element, ELEMENTS if spread else SCALAR, method, origin)
            return "composition", None
        if k == "new":
            return self.check_new(node, mode, method, origin)
        if k == "name":
            return self.check_name(node, mode, method, origin)
        if k == "member":
            return self.check_member(node, mode, method)
        if k == "call":
            return self.check_call(node, mode, method, origin)
        if k == "index":
            ranged = any(a.expr.kind == "range" for a in node.args)
            return self.check(node.target, mode if ranged else ELEMENTS, method, origin)
        if k == "lambda":
            return "unknown", "a lambda is not query text"
        if k == "decl":
            return "unknown", f"`{text}` declares a variable with no traceable value"
        return "unknown", f"`{text}` is an object, not query text"

    def check_new(self, node, mode, method, origin):
        if simple_type(node.type) == "StringBuilder":
            return "composition", None  # its initial text and its appends are sinks of their own
        if mode == ELEMENTS and node.type != "<anonymous>":  # a collection whose elements are query text
            if any(kind != "element" for kind, _, _ in node.init):
                return "unknown", f"`new {node.type}` holds entries the lint cannot trace"
            for _, _, value in node.init:
                self.defer(value, SCALAR, method, origin)
            for a in node.args:
                if not self.numeric(a.expr, method):
                    self.defer(a.expr, ELEMENTS, method, origin)
            return "composition", None
        return "unknown", f"`new {node.type}(...)` is an object, not query text"

    def check_name(self, node, mode, method, origin):
        name = node.id
        r = self.resolve(name, method)
        if r is None:
            return "unknown", f"`{name}` is not a local, a parameter or a constant the lint can see"
        if r[0] == "const":
            return "const", None
        if r[0] == "field":
            return "unknown", f"`{name}` is a field, not a constant"
        if self.is_enum_type(self.declared_type(name, method)):
            return "enum", None
        if r[0] == "local":
            return self.check_local(name, r[1], mode, origin)
        return self.check_param(name, r[1], r[2], mode, origin)

    def check_local(self, name, owner: Method, mode, origin):
        sources = [s for s in owner.locals.get(name, []) if s[0] != "none"]
        if not sources:
            return "unknown", f"local `{name}` is never assigned a value the lint can see"
        for source in sources:
            kind = source[0]
            if kind == "unknown":
                return "unknown", f"`{name}` is {source[1]}, whose value the lint cannot trace"
            if kind == "pattern" and any(step[0] == "prop" for step in source[2]):
                return "unknown", f"`{name}` is a property of a matched value, which the lint cannot trace"
        for source in sources:
            kind = source[0]
            if kind == "expr":
                self.defer(source[1], mode, owner, origin)
            elif kind == "add":
                self.defer(source[1], SCALAR, owner, origin)
            elif kind == "elem":
                self.defer(source[1], ELEMENTS, owner, origin)
            elif kind == "decon":
                self.defer(source[1], ("component", source[2], mode), owner, origin)
            elif kind == "pattern":
                sub = mode
                for step in reversed(source[2]):
                    sub = ("component", step[1], sub) if step[0] == "pos" else ELEMENTS
                self.defer(source[1], sub, owner, origin)
        return "local", None

    def check_param(self, name, owner: Method, index, mode, origin):
        if owner.is_primitive:
            return "unknown", (f"`{name}` is a parameter of the primitive {owner.name}, untrusted inside its own body "
                               f"(a reviewed allowlist entry is the trust boundary)")
        for value in owner.param_writes.get(name, []):
            self.defer(value, mode, owner, origin)
        key = (id(owner), index, mode)
        if key in self.text_params:
            return "parameter", None
        sites = self.call_sites(owner)
        if not sites:
            return "unknown", f"parameter `{name}` of {owner.name} has no call site the lint can check"
        self.text_params.add(key)
        param = owner.params[index]
        for bound, caller in sites:
            for arg in bound.get(index, []):
                self.defer(arg, mode, caller, origin)
            if index not in bound and param.default is not None:
                self.defer(param.default, mode, owner, origin)
        return "parameter", None

    def call_sites(self, owner: Method):
        """Every in-scope call that can reach `owner`, with its arguments bound: [(param index -> [expr]), caller]."""
        if owner.kind == "ctor":
            found = [(node.args, caller) for node, caller in self.news.get(owner.name, [])]
        else:
            found = []
            for node, caller in self.calls.get(owner.name, []):
                if any(m is owner for m in self.targets(node, caller)):
                    args = list(node.args)
                    if owner.params and "this" in owner.params[0].mods and node.target.kind == "member":
                        args.insert(0, Arg(None, None, node.target.target))
                    found.append((args, caller))
        sites = []
        for args, caller in found:
            bound = self.bind(owner, args)
            if bound is not None:
                sites.append((bound, caller))
        return sites

    def bind(self, owner, args):
        """C# argument binding: an argument binds by position unless named (named arguments may sit in their own
        position before positional ones); a trailing `params` parameter takes the rest. None when it cannot bind."""
        params, bound = owner.params, {}
        for i, a in enumerate(args):
            if a.name is not None:
                j = owner.param_index(a.name)
                if j is None:
                    return None
            elif i < len(params):
                j = i
            elif params and "params" in params[-1].mods:
                j = len(params) - 1
            else:
                return None
            bound.setdefault(j, []).append(a.expr)
        for j, p in enumerate(params):
            if j not in bound and p.default is None and "params" not in p.mods:
                return None
        return bound

    def targets(self, call, caller: Method):
        """The in-scope declarations a call can reach. Unqualified: a local function of the calling method, else a
        method of an enclosing type. `Type.M(...)`: that type's methods. `obj.M(...)`: every in-scope method named M."""
        name = callee_name(call)
        candidates = [m for m in self.methods_by_name.get(name, []) if m.kind in ("method", "local function")]
        target = call.target
        receiver = target.target if target.kind == "member" else None
        if receiver is None or (receiver.kind == "name" and receiver.id in ("this", "base")):
            scopes = []
            m = caller
            while m is not None:
                scopes.append(m)
                m = m.parent
            local = [c for c in candidates if c.kind == "local function" and any(c.parent is s for s in scopes)]
            if local:
                return local
            chain = caller.type_chain
            return [c for c in candidates if c.kind == "method" and chain[:len(c.type_chain)] == c.type_chain]
        if self.type_like(receiver, caller):
            type_name = qualifier(receiver).split(".")[-1]
            return [c for c in candidates if c.kind == "method" and c.type_chain and c.type_chain[-1] == type_name]
        return [c for c in candidates if c.kind == "method"]

    def type_like(self, node, method):
        """Is this name or member chain a type (it does not start with a local, a parameter or a field)?"""
        q = qualifier(node)
        return q is not None and self.resolve(q.split(".")[0], method) is None

    def check_member(self, node, mode, method):
        q = qualifier(node.target)
        if q is not None and self.type_like(node.target, method):
            last = q.split(".")[-1]
            if (last, node.name) in BCL_SAFE_MEMBERS or (q, node.name) in BCL_SAFE_MEMBERS:
                return "const", None
            if (last in self.enums and node.name in self.enums[last]) or last in BCL_ENUMS:
                return "enum", None
            info = self.types.get(last)
            if info is not None and node.name in info.consts:
                return "const", None
        if node.name == "Sql" and node.target.kind == "name":
            r = self.resolve(node.target.id, method)
            if r is not None and r[0] == "local":
                sources = [s for s in r[1].locals.get(node.target.id, []) if s[0] != "none"]
                if sources and all(s[0] == "expr" and self.compiled_query(s[1]) for s in sources):
                    return "compiled query", None
        return "unknown", (f"`{method.file.text(node)}` is a member of a runtime object, not a placeholder, a quoted "
                           f"identifier, a constant or an enum")

    def compiled_query(self, node):
        if node.kind != "call" or node.target.kind != "member":
            return False
        q = qualifier(node.target.target)
        return q is not None and (q.split(".")[-1], node.target.name) in COMPILED_QUERY_CALLS

    def check_call(self, node, mode, method, origin):
        f = method.file
        name = callee_name(node)
        target = node.target
        receiver = target.target if target.kind == "member" else None
        if name == "nameof" and target.kind == "name":
            return "nameof", None
        if name in PRIMITIVES:
            return "primitive " + name, None
        if name is not None and name.startswith("Escape"):
            if f.profile == "value" or method.name.startswith("Escape"):
                return "primitive Escape (a pattern value)", None
            return "unknown", ("an escaped pattern is a value, not SQL text: bind it (SEC-2, SEC-4) instead of "
                               "splicing it into the statement")
        if receiver is not None and qualifier(receiver) in STRING_TYPES and name in ("Join", "Concat", "Format"):
            return "composition", None  # a sink of its own
        if receiver is not None and name in BUILDER_METHODS + ("ToString",) and self.is_builder(receiver, method):
            return "composition", None  # a StringBuilder: its text and appends are sinks of their own
        if receiver is not None and name in STRING_METHODS:
            category, why = self.check(receiver, mode, method, origin)
            if why:
                return category, why
            for a in node.args if name != "ToString" else []:
                category, why = self.check(a.expr, SCALAR, method, origin)
                if why:
                    return category, why
            return "string method on a safe value", None
        if receiver is not None and name == "Select" and node.args:
            selector = node.args[0].expr
            if selector.kind == "member" and selector.name in PRIMITIVES:
                return "primitive " + selector.name, None
            if selector.kind == "lambda" and selector.body is not None:
                return self.check(selector.body, SCALAR, method, origin)
            if selector.kind == "lambda":
                for ret in getattr(selector, "returns", []):
                    self.defer(ret, SCALAR, method, origin)
                return "local", None
            return "unknown", f"`{f.text(selector)}` is a selector the lint cannot trace"
        if receiver is not None and name in ELEMENT_METHODS:
            parts = [(receiver, mode)] + [(a.expr, SCALAR if name in ("Append", "Prepend") else mode)
                                          for a in node.args if a.expr.kind != "lambda"]
            for part, part_mode in parts:
                category, why = self.check(part, part_mode, method, origin)
                if why:
                    return category, why
            return "composition", None
        declarations = self.declarations(node, method)
        if declarations is None:
            return "unknown", f"the result of `{f.text(target)}(...)` comes from code the lint cannot see"
        key = (name, id(declarations[0]), mode)
        if key not in self.text_methods:
            self.text_methods.add(key)
            for declaration in declarations:
                for ret in declaration.returns:
                    if ret.kind == "yield":
                        if mode == ELEMENTS:
                            self.defer(ret.operand, SCALAR, declaration, origin)
                    else:
                        self.defer(ret, mode, declaration, origin)
        return "method", None

    def declarations(self, call, method):
        """The in-scope bodies a call can reach (see `targets`), or None when it reaches none."""
        if callee_name(call) is None:
            return None
        bodies = [m for m in self.targets(call, method) if m.has_body]
        return bodies or None


# ---- driver ------------------------------------------------------------------------------------------------------

def read_allowlist(path: Path) -> dict:
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except OSError as error:
        raise LintError(f"cannot read the allowlist {path}: {error.strerror or error}") from error
    entries = {}
    for number, raw in enumerate(lines, 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        parts = line.split(" | ")
        if len(parts) < 3 or "::" not in parts[0]:
            raise LintError(f"{path}:{number}: malformed entry, expected `path::Type.Member | expression | reason`")
        file_part, member = (x.strip() for x in parts[0].split("::", 1))
        expression = re.sub(r"\s+", " ", " | ".join(parts[1:-1])).strip()
        if not (file_part and member and expression and parts[-1].strip()):
            raise LintError(f"{path}:{number}: an entry needs a path, a member, an expression and a reason")
        key = (file_part, member, expression)
        if key in entries:
            raise LintError(f"{path}:{number}: duplicate entry (first at line {entries[key]})")
        entries[key] = number
    return entries


def collect_files(root: Path, paths: list[str]) -> list[Path]:
    files = []
    for p in paths:
        target = Path(p) if Path(p).is_absolute() else root / p
        if target.is_file():
            if target.suffix != ".cs":
                raise LintError(f"not a C# file: {p}")
            files.append(target)
        elif target.is_dir():
            found = sorted(x for x in target.rglob("*.cs")
                           if not {"bin", "obj"} & set(x.relative_to(target).parts))
            if not found:
                raise LintError(f"no .cs file under the scope path {p}")
            files.extend(found)
        else:
            raise LintError(f"scope path not found: {p}")
    return files


_ENUM_DECL = re.compile(r"\benum\s+(\w+)\s*(?::\s*[\w.]+\s*)?\{([^}]*)\}")


def scan_enums(paths) -> dict[str, set[str]]:
    """Enum declarations of csharp/src by a light scan (the lint needs their names, not their code)."""
    enums: dict[str, set[str]] = {}
    for path in paths:
        try:
            text = re.sub(r"//[^\n]*|/\*.*?\*/", "", path.read_text(encoding="utf-8-sig"), flags=re.S)
        except (OSError, UnicodeDecodeError):
            continue
        for match in _ENUM_DECL.finditer(text):
            members = enums.setdefault(match.group(1), set())
            for part in match.group(2).split(","):
                part = re.sub(r"\[[^\]]*\]", "", part).split("=")[0].strip()
                if re.fullmatch(r"[A-Za-z_]\w*", part):
                    members.add(part)
    return enums


def run(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent,
                        help="the repository root that paths and allowlist keys are relative to (default: this repo)")
    parser.add_argument("--allowlist", type=Path, default=DEFAULT_ALLOWLIST,
                        help="the reviewed exceptions (default: scripts/sec2-allowlist.txt)")
    parser.add_argument("--stats", action="store_true", help="also print what was scanned and how it resolved")
    parser.add_argument("--explain", action="store_true", help="also print every sink operand and how it resolved")
    parser.add_argument("paths", nargs="*", help="files or folders to lint (default: the five query-text builders)")
    args = parser.parse_args(argv)

    root = args.root.resolve()
    if not root.is_dir():
        raise LintError(f"--root is not a directory: {args.root}")
    paths = list(dict.fromkeys(p.resolve() for p in collect_files(root, args.paths or list(DEFAULT_SCOPE))))
    allowlist = read_allowlist(args.allowlist)

    files = []
    for path in paths:
        try:
            src = path.read_text(encoding="utf-8-sig")
        except (OSError, UnicodeDecodeError) as error:
            raise LintError(f"cannot read {path}: {error}") from error
        resolved = path.resolve()
        rel = resolved.relative_to(root).as_posix() if resolved.is_relative_to(root) else resolved.as_posix()
        f = SourceFile(rel, src)
        f.parser = FileParser(f)
        try:
            f.parser.parse()
        except ParseError as error:
            raise LintError(f"{rel}: cannot parse: {error}") from error
        files.append(f)

    symbols = root / SYMBOL_SOURCES
    enums = scan_enums(sorted(symbols.rglob("*.cs")) if symbols.is_dir() else [])
    for f in files:
        for name, members in f.enums.items():
            enums.setdefault(name, set()).update(members)

    analysis = Analysis(files, enums, allowlist)
    try:
        analysis.collect()
        analysis.check_all()
    except ParseError as error:
        raise LintError(f"cannot parse a lambda body: {error}") from error

    scanned = {f.rel for f in files}
    stale = [(number, key) for key, number in sorted(allowlist.items(), key=lambda kv: kv[1])
             if key[0] in scanned and key not in analysis.allowlist_hits]
    findings = sorted(analysis.findings.values(), key=lambda x: (x.file, x.line, x.expression))
    for finding in findings:
        print(finding)
    for number, (file_part, member, expression) in stale:
        print(f"{args.allowlist}:{number}: SEC-2: stale allowlist entry {file_part}::{member} | {expression} "
              f"— nothing matches it any more: remove it")

    if args.explain:
        for rel, line, kind, expression, category in sorted(analysis.explained, key=lambda x: (x[0], x[1])):
            print(f"{rel}:{line}: {kind}: {expression} -> {category}")
    built = {k: v for k, v in analysis.constructions.items() if "skipped" not in k}
    total = sum(built.values())
    if args.stats:
        print(f"Scanned {len(files)} files: "
              + ", ".join(f"{v} {k}" for k, v in sorted(analysis.constructions.items())))
        print("Sink operands by resolution: " + ", ".join(
            f"{k} {v}" for k, v in sorted(analysis.resolutions.items(), key=lambda kv: (-kv[1], kv[0]))))
        print(f"Positions checked (sink operands and everything feeding them): {len(analysis.seen)}; "
              f"allowlist: {len(allowlist)} entries, "
              f"{len(analysis.allowlist_hits)} matched")
    if findings or stale:
        print(f"FAIL: SEC-2 lint, {len(findings)} finding(s) and {len(stale)} stale allowlist entr"
              f"{'y' if len(stale) == 1 else 'ies'} in {len(files)} files")
        return 1
    print(f"PASS: SEC-2 lint, {len(files)} files, {total} string constructions, {len(analysis.sinks)} operands, "
          f"{len(analysis.allowlist_hits)} allowlisted")
    return 0


def main() -> int:
    for stream in (sys.stdout, sys.stderr):  # expressions are printed as written; a Windows pipe defaults to cp1252
        stream.reconfigure(encoding="utf-8", errors="replace")
    try:
        return run()
    except LintError as error:
        print(f"lint-sec2.py: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
