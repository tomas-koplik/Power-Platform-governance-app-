# Workshop Playbook

This repository powers a guided hands-on workshop for Microsoft ATUs across EMEA. The audience is technical and customer-facing, but mostly not developers. Assume many participants are unfamiliar with repos, cloning, Markdown, and developer workflows.

The workshop goal is not to teach coding. The goal is to teach participants how to collaborate with AI to turn an idea into a working app.

## Core Learning Pattern

The core pattern is:

**Describe -> Review -> Improve**

Participants describe the outcome they want, review what the AI produces, and improve it through focused iteration.

## Workshop Format

The session has five parts:

| Segment | Duration | Where | Lead | Purpose |
|---|---:|---|---|---|
| 1. Intro | 10 min | Outside VS Code | Facilitator | Set context: collaborate with AI, not learn coding |
| 2. Spec phase | 10 min | VS Code | Spec Coach | Create a lightweight app spec |
| 3. Learning break | 10 min | Outside VS Code | Facilitator | Reveal agents, skills, prompts, context, and MCP at a high level |
| 4. Implementation phase | 15 min | VS Code | Build Coach | Build the app from the spec |
| 5. Wrap-up and Q&A | 10 min | Outside VS Code | Facilitator | Collect learnings, discuss what worked, answer questions |

During the VS Code phases, the facilitator demonstrates slowly on screen so participants can follow along. Participants should be able to continue by watching the facilitator even if their own environment gets stuck.

## What Participants Build

Participants build a small static browser app. The result must feel interactive and app-like. It must not be framed as a static briefing or report.

Supported app ideas:
- **Account Opportunity Navigator** - explore customer signals, priorities, solution plays, opportunity areas, urgency, impact, and next actions.
- **AI Transformation Control Center** - simulate cloud maturity, AI maturity, data readiness, business pressures, and recommended initiatives.
- **Executive Relationship & Strategy Map** - map stakeholders, influence, priorities, concerns, relationships, and opportunity alignment.

Apps should include filters, clickable views, scoring, maps, prioritization, dynamic recommendations, or similar interactions.

## Agent Roles

### Spec Coach

Used only in Segment 2.
- Helps the participant choose or refine one of the three app ideas.
- Keeps scope small and the participant on time.
- Produces a clear final spec summary in the conversation.
- Does not implement code.
- Ensures the spec identifies app idea, target user, supported decision/workflow, interactive features, required sample-data scenario, participant-facing scope boundaries, non-goals, and why it is an app rather than a static report.
- Ends by telling the participant to pause and wait for the workshop leader. Participants switch to Build Coach only after the facilitator-led learning break.

### Build Coach

Used only in Segment 4.
- Reads the final spec summary from the previous conversation context.
- Implements the app from the spec.
- Prefers GitHub MCP to retrieve relevant sample data from `Jfhelin/account-strategy-sample-data`, with public repo/web access as fallback.
- Prefers GitHub MCP and the Zava design skill to retrieve and apply design guidance from `Jfhelin/zava-design-guidelines`, with public repo/web access as fallback.
- Uses Microsoft Learn MCP only when implementation guidance is useful.
- Creates `index.html`, `style.css`, and `app.js`.
- Keeps the app small, interactive, browser-based, and demoable.
- Opens the app for the participant when possible, or gives the exact generated `index.html` path to open directly.
- Uses `bash scripts/preview.sh` or the "Preview App (Fallback)" task only as optional fallbacks.
- Does not introduce backend, auth, database, API keys, or cloud deployment.

## Concept Reveal Timing

Do not expose agents, skills, MCP, prompts, or context too early in participant-facing guidance.

- Segment 1 sets the human goal and the Describe -> Review -> Improve pattern.
- Segment 2 lets participants experience guided specification first.
- Segment 3 reveals agents, skills, prompts, context, and MCP at a high level after the experience.
- Segment 4 reinforces grounding through the built app.
- Segment 5 collects learnings and answers deeper questions.

## Workshop Defaults

Generated apps must:
- run locally in the browser
- open directly from `index.html` without a local server
- use `index.html`, `style.css`, and `app.js`
- avoid backend, auth, databases, API keys, and cloud deployment
- use realistic sample data
- be demoable quickly
- be interactive, not static reports

## What to Avoid

Avoid turning the workshop into:
- a coding lesson
- a GitHub feature tour
- a self-service README exercise
- a lecture on agents, skills, MCP, or prompt engineering
- a static briefing/report generator
- a production architecture discussion

Success is a participant saying: "I described an idea, reviewed what AI made, improved it, and got a working app-like experience."
