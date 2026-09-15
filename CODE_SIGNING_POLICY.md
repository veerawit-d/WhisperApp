# Code signing policy

Free code signing provided by SignPath.io, certificate by SignPath Foundation.

This policy applies to the public Windows releases of WhisperApp. Releases are
built by the GitHub Actions workflow in `.github/workflows/windows-sign.yml`.
The unsigned build artifact is submitted to SignPath, and every release must
be manually approved by an authorized approver before the signed artifact is
published.

## Roles

- Authors: contributors who submit changes through pull requests.
- Reviewers: maintainers with write access to the GitHub repository who review
  and merge pull requests.
- Approvers: repository owners or maintainers explicitly configured as SignPath
  approvers.

The repository owner must keep the current members of these roles documented in
the repository access controls and SignPath organization settings.

## Build and release rules

- Releases are created from reviewed source on the public GitHub repository.
- The workflow uses GitHub-hosted runners and the checked-in build script.
- The signing request is submitted through the official SignPath GitHub Action.
- A release is published only after manual approval in SignPath.
- Windows binaries must contain the WhisperApp product name and an explicit
  file version in their metadata.

## Privacy

WhisperApp does not collect telemetry. The application sends audio or text only
to the provider selected by the user, when that provider is enabled. API keys
are stored in the user's local application data and are not included in build
artifacts.
