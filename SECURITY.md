# Security Policy

## Supported versions

TestAtlas is pre-1.0 and released from the `main` branch. Security fixes are
applied to the latest published release on NuGet
([TestAtlas.Cli](https://www.nuget.org/packages/TestAtlas.Cli) /
[TestAtlas.Mcp](https://www.nuget.org/packages/TestAtlas.Mcp)). Please make sure
you are on the newest version before reporting.

## Reporting a vulnerability

**Please do not report security vulnerabilities through public GitHub issues,
discussions, or pull requests.**

Instead, report privately using one of:

- GitHub's [private vulnerability reporting](https://github.com/Karzone/TestAtlas/security/advisories/new)
  (**Security → Report a vulnerability**), or
- Email **karthikawaiting@gmail.com** with the details.

Please include:

- A description of the vulnerability and its impact.
- Steps to reproduce (a minimal solution/input that triggers it is ideal).
- The TestAtlas version and how you invoked it.

You can expect an initial acknowledgement within a few days. Once a fix is
released, credit will be given to the reporter unless you prefer to remain
anonymous.

## Scope notes

TestAtlas performs **static, offline** analysis: it reads a .NET solution and
writes a local SQLite file. It makes no network calls and executes no project
code during indexing. The most relevant security surface is therefore the
handling of untrusted inputs — solution/source files and, for the MCP server,
agent-supplied query strings (which are sanitized before reaching FTS/SQLite).
Reports touching these areas are especially welcome.
