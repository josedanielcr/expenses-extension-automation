# AGENTS.md

## Purpose
This repository has two deployment environments for the Azure Function backend:
- `main` branch deploys to production Function App.
- `staging` branch deploys to staging Function App.

Use this file as the operational guide before changing code, workflows, or manifests.

## Repository layout
- `backend/`: Azure Functions (.NET isolated, solution file `EmailParserService.slnx`).
- `extension/`: Chrome extension source.
- `.github/workflows/main_email-processor-ai.yml`: production deploy workflow (`main` only).
- `.github/workflows/staging_email-processor-ai-staging.yml`: staging deploy workflow (`staging` only).

## Deployment behavior
- Pushing to `main` triggers production deployment to app name `email-processor-ai`.
- Pushing to `staging` triggers staging deployment to app name `email-processor-ai-staging`.
- Both workflows build from `./backend` using:
  - `dotnet build EmailParserService.slnx --configuration Release --output ./output`

## Extension environment behavior
- Extension backend endpoint is resolved at runtime from `manifest.json` host permissions:
  - `extension/background/lib/core.js` reads the first `*.azurewebsites.net` host permission.
  - It appends `/api/OnEmailPush`.
- Environment manifests:
  - `extension/manifest.prod.json`
  - `extension/manifest.staging.json`

## Extension packaging
Use the script instead of manually editing `manifest.json`:

```bash
./extension/build-extension-zip.sh prod
./extension/build-extension-zip.sh staging
```

Outputs:
- `extension.zip` (prod)
- `extension-staging.zip` (staging)

The script copies the selected manifest into a temporary build folder as `manifest.json`, so working files are not modified.

## Branch and release flow
Recommended safe flow:
1. Work and validate changes in `staging`.
2. Push to `staging` and wait for staging GitHub Action to pass.
3. Validate staging endpoint (`/api/HealthCheck`) and extension behavior.
4. Open PR `staging -> main`.
5. Merge PR to promote to production.

## Required Azure/GitHub setup assumptions
- Staging and prod Function Apps both exist and are configured.
- GitHub secrets exist for each workflow (client/tenant/subscription IDs).
- OIDC federated credentials are configured for the matching branch refs.
- Staging app settings mirror required production settings (`OPENAI_*`, `KEY_VAULT_*`, `GOOGLE_*`, etc.).

## Safety rules for future agents
- Never push directly to `main` for unvalidated changes.
- Never change workflow branch triggers unless explicitly requested.
- Avoid changing production secrets, app names, or function app targets without user approval.
- Do not commit zip artifacts.
- Keep `manifest.prod.json` and `manifest.staging.json` as source-of-truth; package via script.
