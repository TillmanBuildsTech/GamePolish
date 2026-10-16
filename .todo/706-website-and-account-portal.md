---
id: 706
title: Website and account portal (gamepolish.com)
phase: 7
bucket: post-v1
depends_on: [703, 705]
blocks: []
status: todo
area: service
---

# 706 — Website and account portal (gamepolish.com)

## Objective

`gamepolish.com` exists as a second surface over the same Supabase project: marketing pages, the account routes `/signup`, `/login`, `/verify-email`, `/reset-password` and `/dashboard`, a dashboard that shows the signed-in user's devices with Revoke buttons and a DANGER ZONE, and stable download links pointing at the latest release. The pricing page and the Plan section stay deferred as the notes mark them. The website never handles the desktop app's tokens; the two surfaces share only the Supabase project and the JWT secret.

## Context

Phase 7 is the `post-v1` bucket: the account system exists to gate a *paid storage tier*, and that tier is only worth building once the desktop app has users — **do not start this bucket while the `v1.0` bucket still has open work**. Confirm the earlier buckets are `status: done` before beginning.

This task depends on 703 (accounts that can actually sign in, including the 12-character rule and confirmation) and 705 (`/user/devices`, `/user/me`, `/user/account`, and the Revoke semantics). It is the last piece of the account story that a user sees, and it is the surface the email templates have been pointing at since 702.

External prerequisites: the `gamepolish.com` domain, DNS control, and a host for the site. Check what already exists before assuming anything; if the domain or hosting is missing, **ask the user** — never invent a hostname mapping, deploy target, or credentials.

## Scope

- Hosting decision for a marketing site plus a small authenticated area: choose a real platform (for example Cloudflare Pages, Vercel, or static hosting on GitHub Pages with Supabase called directly from the browser), write the decision and the deploy command into `../game-polish-notes/docs/ci-release.md`, and keep the site deployable by CI.
- Marketing pages: landing (what Game Polish is, the safety promise — never executes mod content, backs up before every mutation), a features or how-it-works page, and a download page. No third-party trackers on any authenticated page.
- Stable download links, exactly as `ci-release.md` specifies, so they keep resolving across releases:
  `https://github.com/game-polish/game-polish/releases/latest/download/GamePolish_x64-setup.exe` (Windows) and
  `https://github.com/game-polish/game-polish/releases/latest/download/GamePolish_aarch64.dmg` (macOS).
- `/signup`: email + password form, "Continue with Google" button, link to `/login`, a client-side 12-character minimum mirroring the Supabase policy (702), and on success a confirmation email plus a redirect to `/verify-email`.
- `/login`: email + password or Google, a "Forgot password?" link to `/reset-password`, and a redirect to `/dashboard` on success.
- `/verify-email`: the holding page that the confirmation link lands on, with a resend action.
- `/reset-password`: the request form and the new-password form that the emailed link opens, writing through Supabase.
- `/dashboard`: the notes' layout — header `Game Polish · brandon@example.com [Sign Out]`, a DEVICES section listing name plus "Last seen: Today" (or "Last seen: Oct 12") with a Revoke action per row, and a DANGER ZONE with Delete Account.
- A privacy policy page. It must state the save-file disclosure the notes require (save files can contain personal data — usernames, playtime, achievements), because 712's setup wizard points at it and the Google OAuth consent screen (704) needs a public policy.
- The Future marker stays verbatim and unimplemented: "> **Future:** A `/pricing` page and Plan section will be added when the managed Game Polish Cloud storage tier launches."

## Out of scope

- The `/pricing` page, the Plan section, checkout and billing — all 713's launch surface, and none of it exists until then.
- Desktop app views (the notes' per-view specs) and the console.
- Adding API endpoints: the site uses the four `/auth` routes (703) and the account routes (705) as they are; a new endpoint means a notes change first.
- Marketing copy beyond a factual first pass, brand design, analytics, SEO tooling, and a blog.
- Payments, invoicing, or any Stripe surface (713).

## Implementation notes

- **Security boundary, stated explicitly.** The website authenticates to Supabase with the browser SDK and the anon key; it never receives, stores or transmits the desktop app's access or refresh tokens. The only things shared between the two surfaces are the Supabase project and the JWT secret (702), which is what makes a website-issued token acceptable at `gamepolish.app`. No page may have a code path that accepts a token as a parameter, query string, or postMessage.
- The dashboard's Revoke calls `DELETE /user/devices/:id` at `gamepolish.app` with the *website's own* Supabase access token — the shared posture from 701/702 in practice. RLS and the service's subject check (invariant 7) mean the site cannot revoke someone else's device even if it tries.
- Redirects after auth must be same-origin paths only; never accept an arbitrary return URL (open-redirect hygiene, the same class of mistake 704 avoids on the OAuth side).
- Email confirmation and reset templates live in the Supabase project (702). This task supplies the page targets and asks 702's owner to point the templates at them; do not edit the project's config silently.
- The desktop app links out to `https://gamepolish.com/reset-password` rather than implementing a reset itself (703) — keep that URL stable and documented in both places.
- Release links: GitHub's `/releases/latest/download/<file>` redirect resolves to the newest non-draft, non-prerelease release. If `v1.0` has not shipped installers yet, the links will 404 — report that fact, do not point them at a placeholder file or a local build.
- Accessibility basics apply: labels tied to inputs, keyboard-operable forms, visible focus, and a responsive layout (the site is public and reviewed by strangers, unlike the desktop app's own views).

## Acceptance criteria

- Every route responds: `/`, `/download`, `/signup`, `/login`, `/verify-email`, `/reset-password`, `/dashboard`, `/privacy` return 200 (dashboard redirects to `/login` when signed out).
- An 11-character password is refused in the signup form client-side and a 12-character one is accepted; the confirmation email arrives and its link opens `/verify-email`.
- Signing in on the website and then revoking a device succeeds, and the revoked device's API calls fail afterwards (the 705 semantics, exercised from the browser).
- The dashboard lists only the signed-in user's devices and renders the notes' strings: device name, "Last seen: Today" or a date, and a [Revoke] control per row.
- The download links return a 302 toward the latest release asset, or the report documents the 404 with the reason (no installers released yet) — never a fabricated placeholder.
- The site contains no code path that accepts or stores a desktop app token, proven by a grep for refresh-token handling and a manual review of the auth pages.
- `/pricing` does not exist, the dashboard has no Plan section, and the Future marker is still present and unbuilt.
- The privacy policy contains the save-files personal-data sentence, and it is reachable from the site footer and from the signup page.

## Verification

```bash
for p in / /download /signup /login /verify-email /reset-password /privacy; do
  curl -s -o /dev/null -w "$p %{http_code}\n" "https://gamepolish.com$p"
done
curl -sI https://github.com/game-polish/game-polish/releases/latest/download/GamePolish_x64-setup.exe | head -3
curl -s -o /dev/null -w '%{http_code}\n' https://gamepolish.com/dashboard      # 302 to /login when signed out
```

Then do the flow in a real browser and paste what you saw at each step (signup → email → `/verify-email` → `/dashboard` → Revoke → device stops working), plus the grep proving no desktop token is accepted anywhere in the site source. Confirm in the Supabase project that the confirmation and reset templates point at these routes.

## References

- `../game-polish-notes/docs/auth-accounts.md` — "Domains", "Sign-Up & Login Options", "Website (gamepolish.com) — Account Pages", "Security Considerations".
- `../game-polish-notes/docs/ci-release.md` — website download links and the release/tag contract.
- `../game-polish-notes/docs/views/cloud-backup-sync.md` — "Security" (the save-file privacy disclosure the policy must carry).
- `.todo/000-CONVENTIONS.md` — invariant 7, secrets in the keychain, definition of done.
- Tasks: 701 (shared JWT secret in practice), 702 (project config and email templates), 703, 705, 712 (saves disclosure), 713 (the deferred pricing page).
