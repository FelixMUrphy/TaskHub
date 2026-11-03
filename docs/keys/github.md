# GitHub Actions: token for TaskHub

[Українською](github.uk.md) · [All keys](README.md)

## What TaskHub uses it for
- **Watch:** workflow runs of the repositories you list — progress, the failed step, the result.
- **Actions** (only with write access): Cancel and Re-run a run.
- **Pull requests** (optional, from 2.9.4, read-only): the ones waiting for your review and open Dependabot pull requests.
- TaskHub never changes your workflows, code or repository settings.

## Create a fine-grained token
1. GitHub → **Settings** → **Developer settings** → **Personal access tokens** → **Fine-grained tokens** →
   **Generate new token** (direct link: <https://github.com/settings/personal-access-tokens/new>).
2. **Name:** e.g. `TaskHub`.
3. **Expiration:** 30 days by default; pick what suits you (TaskHub warns a week before it expires).
4. **Resource owner:** you, or the organization that owns the repositories.
5. **Repository access:** **Only select repositories** → the repositories you will list in TaskHub.
6. **Permissions** (see below) → **Generate token** → copy it.

If the owner is an organization that requires approval, the token stays pending until an admin approves it.

## Minimum permissions
| You want | Repository permissions |
|---|---|
| (a) Watch only | **Actions:** Read-only · **Contents:** Read-only · **Metadata:** Read-only |
| (b) Cancel / Re-run | **Actions:** Read and write · **Contents:** Read-only · **Metadata:** Read-only |

Pull requests (optional): Dependabot pull requests need **Pull requests: Read-only** as well (GitHub lists the open PR
list only under that permission); the review-request search needs no permission. A fine-grained token sees review
requests only in the repositories it was given and in public ones.

Prefer this token over TaskHub's other option (the token the Git Credential Manager already has), which has
access to all your repositories.

## Watching one more repository later
With **Only select repositories** the token reaches only the repositories you picked. If TaskHub says a repository is
*not found, or the token can't reach it*: check the name, then open the token on GitHub (**Settings** → **Developer settings** →
**Personal access tokens** → **Fine-grained tokens**) and add the repository under **Repository access**. GitHub answers the same way for
a wrong name, a repository the token wasn't given and, on some requests, a missing permission, so TaskHub can't tell you which one it is.
[GitHub: 404 for an existing resource](https://docs.github.com/en/rest/using-the-rest-api/troubleshooting-the-rest-api)

## Paste it into TaskHub
1. TaskHub → **Settings** → **Connectors** → **GitHub Actions**.
2. Turn it on, choose **My own fine-grained token (recommended)**, paste the token, list the repositories
   (`owner/repo`), click **Save**.
3. The line should read **Connected as** *your login*. If GitHub rejects the token, nothing is saved.

## Where the token is kept
- Only in the Windows Credential Manager — never in `config.json`.
- Sent only to `api.github.com`. The optional log tail is fetched through the one-time link GitHub gives,
  without the token.

## Revoke it
- **At GitHub:** Settings → Developer settings → Personal access tokens → Fine-grained tokens → the token →
  **Delete**.
- **In TaskHub:** Settings → Connectors → GitHub Actions → **Remove** (deletes it from the Credential Manager).

## Official docs
- Creating a fine-grained token:
  <https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens#creating-a-fine-grained-personal-access-token>
- Searching pull requests (`review-requested:`):
  <https://docs.github.com/en/search-github/searching-on-github/searching-issues-and-pull-requests>
- Permissions per endpoint:
  <https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens>
