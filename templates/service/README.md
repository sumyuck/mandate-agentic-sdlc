# Service skeleton

The starting tree for a run's workspace: a minimal, buildable .NET project that an
implementation stage extends.

It exists so that compensation can be demonstrated against a real version-controlled tree
rather than against throwaway files — a `git revert` that restores an actual project is
evidence; one that restores a scratch file is a formality.

Greenfield runs grow the URL shortener from here.
