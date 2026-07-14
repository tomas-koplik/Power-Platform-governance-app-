---
name: workshop-simplicity
description: "Apply automatically during the workshop to keep apps small, local, interactive, and low-friction."
---

# Workshop Simplicity

Use this skill for all participant app work in the workshop.

## Purpose

Keep the experience simple for non-developers while still producing an app-like result that feels worth reviewing and improving.

## Non-Negotiables

Generated apps must:
- run locally in the browser
- open directly from `index.html` without requiring a local server
- use exactly three app files: `index.html`, `style.css`, `app.js`
- avoid backend services
- avoid authentication
- avoid databases
- avoid API keys
- avoid cloud deployment
- use realistic sample data
- be small enough to demo quickly
- be interactive, not a static report

## Interactivity Requirement

Every app must include visible interaction such as:
- filters or search
- clickable detail views
- scoring or prioritization
- stakeholder maps or relationship views
- dynamic recommendations
- maturity sliders or toggles
- hover and active states

If an output could be replaced by a Word document or PowerPoint, it is not interactive enough.

## Scope Rule

If the request becomes too broad:
- preserve the business value
- remove setup-heavy requirements
- keep one clear workflow
- move future ideas into non-goals

## Data Rule

Use realistic account strategy sample data. For Build Coach work, prefer `Jfhelin/account-strategy-sample-data` through GitHub MCP. If MCP is unavailable, use the public GitHub repository directly through public repo/web access.

Do not use Lorem ipsum, "Item 1", generic placeholder rows, or invented labels when grounded sample data should be used.

## Implementation Rule

Use vanilla HTML, CSS, and JavaScript unless explicitly instructed otherwise.

Prefer creating the app files at the repo root. If tooling creates the app in a generated subfolder, do not make the participant hunt for it. Locate the generated `index.html`, normalize to the repo root when practical, or report the exact file path.

Keep concerns separate:
- `index.html` for structure
- `style.css` for styling
- `app.js` for data and behavior

Do not add build tooling, package managers, external libraries, CDNs, backend code, or deployment files for the workshop app.

Put sample data directly in `app.js`. Do not rely on `fetch()` for local JSON files, ES module imports, or browser features that require a local HTTP server.

## First Version Rule

The first version should be simple, but not flat. It should include:
- a clear header or app shell
- summary metrics or status indicators
- 6-10 realistic data records when list-based
- at least two visible interactions
- a clear empty or filtered state where relevant
- responsive layout for browser preview

## High-Value Enhancement Rule

After a working first version exists, propose a small menu of high-value enhancements rather than waiting for the participant to invent the next step.

In the background, these should create a strong capability moment for the participant. In the user-facing wording, describe them as business-value or decision-support enhancements, not eye candy.

High-value enhancements should:
- make the app feel more interactive, capable, or decision-supportive
- be addable in one iteration
- reuse the same three files
- avoid backend, auth, databases, API keys, cloud deployment, or a rebuild

Good options include animated scoring, recommendation explanations, comparison views, heatmaps, scenario toggles, relationship maps, and next-best-action panels.

## Communication Rule

Tell the participant only what happened and what to do next. Keep explanations short.
