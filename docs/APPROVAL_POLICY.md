# Approval policy

The repository owner and sole maintainer is **RedHolger**.
GitHub login: [`RedHolger`](https://github.com/RedHolger), configured in [`.github/team.json`](../.github/team.json) and [`.github/CODEOWNERS`](../.github/CODEOWNERS).

This policy is enforced by the required **`Review gate`** check (`scripts/check_approvals.py` and `scripts/approval_policy.py`).

## The matrix

| Pull request author | Required approvals | Author command | Extra command for protected CI paths |
| --- | --- | --- | --- |
| **RedHolger** (owner) | None (solo maintainer) | `/signoff <head-sha>` from RedHolger | Covered by author signoff |
| **External contributor** | **RedHolger** | Not required | `/approve-ci <head-sha>` from RedHolger |

Additional rules:

- **Self-review handling**: GitHub's native review system prohibits PR authors from approving their own PRs. When RedHolger opens a PR (e.g. from a feature branch to `main`), posting `/signoff <head-sha>` on the PR satisfies the Review gate without needing an artificial secondary account.
- **Commit invalidation**: Approvals and signoffs must match the **current head commit**. Any new commit/push invalidates previous signoffs and reviews.
- **Changes requested**: A `CHANGES_REQUESTED` review from the maintainer blocks the merge until resolved or dismissed.
- **Drafts and conflicts**: Draft pull requests and merge conflicts block the gate.
- **Protected paths**: Changes to engineering paths (`.github/**`, `scripts/**`, `tests/ci/**`, `Assets/Orchard/Editor/**`, `ProjectSettings/**`, `Packages/**`) require explicit maintainer confirmation.

## Commands are strict

A command must be the **first non-empty line** of the comment and the **only content on that line**. Backticks, trailing words, and prose before the command are ignored.

```text
/signoff 3d3c42e5aac5ba805825da76410c181273ba90b1
```

A short 7-character prefix works, but the full 40-character SHA is recommended.

## Step-by-step review workflows

### When RedHolger opens a PR:
1. Open the PR from your feature branch to `main`.
2. Post a comment on the PR:
   ```text
   /signoff <head-sha>
   ```
3. Once all required CI checks (`Required checks`) pass and the `Review gate` turns green, merge into `main`.

### When an external contributor opens a PR:
1. RedHolger reviews the code and submits a standard GitHub **Approve** review.
2. If protected CI or editor paths changed, RedHolger also posts:
   ```text
   /approve-ci <head-sha>
   ```
3. Once CI and Review gate pass, merge.
