<!--
One concern per pull request. Explain why, not just what — if you dropped an approach
along the way, say what the evidence was, so nobody re-investigates it later.
-->

## What this changes

<!-- One or two sentences. -->

## Why

<!--
The reasoning, and any evidence behind it. If you are claiming the log does or does not
contain something, say how you counted. See CONTRIBUTING.md — assuming absence from a
search that could not have found it is this project's most repeated mistake.
-->

## Effect on real output

<!--
If rendering changed, run `mtga-pbp build` over an existing archive before and after and
say how many lines move. "Rephrases 153 lines, removes 1" reviews far better than
"improves wording", and it catches changes that were larger than intended.
-->

## Checklist

- [ ] `dotnet test` passes
- [ ] `dotnet format --verify-no-changes` is clean (CI enforces this and it is the one
      people forget)
- [ ] A test fails without this change
- [ ] No real screen name, user id, session id or `C:\Users\...` path is in the diff —
      including inside any fixture
- [ ] If a fixture was added or regenerated, it is scrubbed and something asserts that
- [ ] If the golden markdown file changed, I read the diff rather than just accepting it
